using System;
using System.Text.Json;
using System.Threading;

namespace CompAI;

public sealed partial class Plugin
{
    private readonly CraftingLogReader craftingLogReader;

    // Public names are searched first. Only requested recipes are observed and
    // retained for this answer; a character/permission change invalidates it.
    private Func<JsonElement, string>? CreateCraftingLogQueryUnderLock()
    {
        if (!this.Access.CraftingLog || !this.CanReadCurrentCharacter) return null;
        var owner = this.activeContentId;
        var session = this.contextLoadEpoch;
        var territory = ClientState.TerritoryType;
        var areaEpoch = this.currencyAreaEpoch;
        bool Permitted() => this.Access.CraftingLog && this.CanReadCurrentCharacter && !QuestLoading() &&
            owner == this.activeContentId && session == this.contextLoadEpoch &&
            territory == ClientState.TerritoryType && areaEpoch == this.currencyAreaEpoch;
        CraftingLogSession? observations = null;
        var queryGate = new object();
        return arguments =>
        {
            CraftingLogLookup.ValidateArguments(arguments);
            var questionContinues = this.companion.CaptureQuestionContinuation();
            lock (queryGate)
            {
                lock (this.stateGate)
                    if (!Permitted() || !questionContinues()) throw new OperationCanceledException();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var token = deadline.Token;
                try
                {
                    return Framework.RunOnFrameworkThread(() =>
                    {
                        lock (this.stateGate)
                        {
                            token.ThrowIfCancellationRequested();
                            if (!Permitted() || !questionContinues() || ReadLoadedCharacterId() != owner)
                                throw new OperationCanceledException();
                            using var timing = PerformanceDiagnostics.Measure("reader.crafting_log");
                            observations ??= this.craftingLogReader.CreateSession();
                            var result = observations.Query(arguments);
                            token.ThrowIfCancellationRequested();
                            if (!Permitted() || !questionContinues() || ReadLoadedCharacterId() != owner)
                                throw new OperationCanceledException();
                            return JsonSerializer.Serialize(result);
                        }
                    }).WaitAsync(token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                    lock (this.stateGate)
                        if (!Permitted() || !questionContinues()) throw;
                    throw new TimeoutException("Crafting log reader timed out.");
                }
            }
        };
    }
}
