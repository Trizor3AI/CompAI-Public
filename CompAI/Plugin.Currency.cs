using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CompAI;

public sealed partial class Plugin
{
    private readonly CurrencyReader currencyReader;
    private int currencyAreaEpoch;

    // No polling or disk cache. Freeze one capture only when this answer uses the tool.
    private Func<JsonElement, string>? CreateCurrencyQueryUnderLock()
    {
        if (!this.Access.Currencies || !this.CanReadCurrentCharacter) return null;
        var owner = this.activeContentId;
        var session = this.contextLoadEpoch;
        var territory = ClientState.TerritoryType;
        var areaEpoch = this.currencyAreaEpoch;
        bool Permitted() => this.Access.Currencies && this.CanReadCurrentCharacter && !QuestLoading() &&
            owner == this.activeContentId && session == this.contextLoadEpoch &&
            territory == ClientState.TerritoryType && areaEpoch == this.currencyAreaEpoch;
        CurrencyLookup? captured = null;
        var queryGate = new object();
        return arguments =>
        {
            CurrencyLookup.ValidateArguments(arguments);
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
                        // The waiting worker never holds the game-state lock.
                        captured = Framework.RunOnFrameworkThread(() =>
                        {
                            lock (this.stateGate)
                            {
                                token.ThrowIfCancellationRequested();
                                if (!Permitted() || !questionContinues() || ReadLoadedCharacterId() != owner)
                                    throw new OperationCanceledException();
                                using var timing = PerformanceDiagnostics.Measure("reader.currency");
                                var reading = this.currencyReader.Capture(territory);
                                token.ThrowIfCancellationRequested();
                                if (!Permitted() || !questionContinues() || ReadLoadedCharacterId() != owner)
                                    throw new OperationCanceledException();
                                return new CurrencyLookup(reading);
                            }
                        }).WaitAsync(token).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                    {
                        lock (this.stateGate)
                            if (!Permitted() || !questionContinues()) throw;
                        throw new TimeoutException("Currency reader timed out.");
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
