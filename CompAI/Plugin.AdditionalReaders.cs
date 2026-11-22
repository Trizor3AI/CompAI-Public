using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CompAI;

public sealed partial class Plugin
{
    private readonly GameDefinitionsReader gameDefinitionsReader;
    private readonly GcSupplyReader gcSupplyReader;
    private readonly NpcTargetReader npcTargetReader;

    private Func<JsonElement, string> CreateGameDefinitionsQueryUnderLock()
    {
        // Public local definitions need no character or internet permission. Only
        // the requested, bounded pages are materialized and shared with the model.
        var pages = new Dictionary<string, string>(StringComparer.Ordinal);
        var queryGate = new object();
        return arguments =>
        {
            GameDefinitionsLookup.ValidateArguments(arguments);
            var continues = this.companion.CaptureQuestionContinuation();
            lock (queryGate)
            {
                var key = arguments.GetRawText();
                lock (this.stateGate)
                    if (this.contextDisposed || !continues()) throw new OperationCanceledException();
                if (pages.TryGetValue(key, out var page)) return page;
                page = this.ReadAdditionalOnFramework(() => !this.contextDisposed, continues, () =>
                {
                    using var timing = PerformanceDiagnostics.Measure("reader.game_definitions");
                    return JsonSerializer.Serialize(this.gameDefinitionsReader.Capture(arguments));
                });
                pages.Add(key, page);
                return page;
            }
        };
    }

    private Func<JsonElement, string>? CreateGcSupplyQueryUnderLock() => this.CreateObservedQueryUnderLock(
        () => this.Access.GcSupply, GcSupplyLookup.ValidateArguments, this.RecentGcSupplyUnderLock,
        reading => !reading.Available || reading.IsFresh(DateTimeOffset.UtcNow),
        (reading, arguments) => JsonSerializer.Serialize(new GcSupplyLookup(reading).Query(arguments)), "reader.gc_supply");

    private Func<JsonElement, string>? CreateNpcTargetQueryUnderLock()
    {
        // A question keeps the permissions it started with; enabling another
        // category later cannot expand a pending question's target access.
        var allowNpcs = this.Access.NpcDialogue;
        var allowOthers = this.Access.TargetInfo;
        return this.CreateObservedQueryUnderLock(
            () => (allowNpcs || allowOthers) && (!allowNpcs || this.Access.NpcDialogue) &&
                (!allowOthers || this.Access.TargetInfo),
            NpcTargetLookup.ValidateArguments, () => this.npcTargetReader.Capture(allowNpcs, allowOthers),
            reading => !reading.Available || this.npcTargetReader.IsCurrent(reading),
            (reading, arguments) => JsonSerializer.Serialize(new NpcTargetLookup(reading).Query(arguments)), "reader.npc_target");
    }

    private Func<JsonElement, string>? CreateObservedQueryUnderLock<T>(Func<bool> allowed,
        Action<JsonElement> validate, Func<T> capture, Func<T, bool> current,
        Func<T, JsonElement, string> query, string timingStage) where T : class
    {
        if (!allowed() || !this.CanReadCurrentCharacter) return null;
        var owner = this.activeContentId;
        var session = this.contextLoadEpoch;
        var areaEpoch = this.currencyAreaEpoch;
        var territory = ClientState.TerritoryType;
        bool Permitted() => !this.contextDisposed && allowed() && this.CanReadCurrentCharacter && !QuestLoading() &&
            owner == this.activeContentId && session == this.contextLoadEpoch &&
            areaEpoch == this.currencyAreaEpoch && territory == ClientState.TerritoryType;
        T? reading = null;
        var queryGate = new object();
        return arguments =>
        {
            validate(arguments);
            var continues = this.companion.CaptureQuestionContinuation();
            lock (queryGate)
                return this.ReadAdditionalOnFramework(Permitted, continues, () =>
                {
                    if (ReadLoadedCharacterId() != owner) throw new OperationCanceledException();
                    using var timing = PerformanceDiagnostics.Measure(timingStage);
                    if (reading is null) reading = capture();
                    else if (!current(reading)) throw new IOException("Observation is no longer current.");
                    var result = query(reading, arguments);
                    if (ReadLoadedCharacterId() != owner) throw new OperationCanceledException();
                    return result;
                });
        };
    }

    private string ReadAdditionalOnFramework(Func<bool> permitted, Func<bool> continues, Func<string> read)
    {
        // The worker waits without the plugin lock. Permission and continuation
        // are checked on both sides of the native read, including late callbacks.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var token = deadline.Token;
        try
        {
            return Framework.RunOnFrameworkThread(() =>
            {
                lock (this.stateGate)
                {
                    token.ThrowIfCancellationRequested();
                    if (!permitted() || !continues()) throw new OperationCanceledException();
                    var result = read();
                    token.ThrowIfCancellationRequested();
                    if (!permitted() || !continues()) throw new OperationCanceledException();
                    return result;
                }
            }).WaitAsync(token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            lock (this.stateGate)
                if (!permitted() || !continues()) throw;
            throw new TimeoutException("Game reader timed out.");
        }
    }
}
