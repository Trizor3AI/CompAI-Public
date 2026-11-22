using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CompAI;

public sealed partial class Plugin
{
    private readonly DutyReader dutyReader;

    // Frozen on first tool use in this answer; no polling or saved personal state.
    private Func<JsonElement, string>? CreateDutyQueryUnderLock()
    {
        if (!this.Access.Duties || !this.CanReadCurrentCharacter) return null;
        var owner = this.activeContentId;
        var session = this.contextLoadEpoch;
        var territory = ClientState.TerritoryType;
        var areaEpoch = this.currencyAreaEpoch;
        bool Permitted() => this.Access.Duties && this.CanReadCurrentCharacter && !QuestLoading() &&
            owner == this.activeContentId && session == this.contextLoadEpoch &&
            territory == ClientState.TerritoryType && areaEpoch == this.currencyAreaEpoch;
        DutyLookup? captured = null;
        var queryGate = new object();
        return arguments =>
        {
            DutyLookup.ValidateArguments(arguments);
            var questionContinues = this.companion.CaptureQuestionContinuation();
            lock (queryGate)
            {
                lock (this.stateGate)
                    if (!Permitted() || !questionContinues()) throw new OperationCanceledException();
                if (captured is null)
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var token = deadline.Token;
                    try
                    {
                        captured = Framework.RunOnFrameworkThread(() =>
                        {
                            lock (this.stateGate)
                            {
                                token.ThrowIfCancellationRequested();
                                if (!Permitted() || !questionContinues() || ReadLoadedCharacterId() != owner)
                                    throw new OperationCanceledException();
                                using var timing = PerformanceDiagnostics.Measure("reader.duty");
                                var reading = this.dutyReader.Capture();
                                token.ThrowIfCancellationRequested();
                                if (!Permitted() || !questionContinues() || ReadLoadedCharacterId() != owner)
                                    throw new OperationCanceledException();
                                return new DutyLookup(reading);
                            }
                        }).WaitAsync(token).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                    {
                        lock (this.stateGate)
                            if (!Permitted() || !questionContinues()) throw;
                        throw new TimeoutException("Duty reader timed out.");
                    }
                }
                lock (this.stateGate)
                {
                    if (!Permitted() || !questionContinues()) throw new OperationCanceledException();
                    return JsonSerializer.Serialize(captured.Query(arguments));
                }
            }
        };
    }
}
