using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CompAI;

public sealed partial class Plugin
{
    private readonly CollectionReader collectionReader;

    // Each requested category is captured once per answer, without polling or saving unlock state.
    private Func<JsonElement, string>? CreateCollectionQueryUnderLock()
    {
        var categories = new HashSet<string>(CollectionAccess.Categories(this.Access), StringComparer.Ordinal);
        if (categories.Count == 0 || !this.CanReadCurrentCharacter) return null;
        var owner = this.activeContentId;
        var session = this.contextLoadEpoch;
        var territory = ClientState.TerritoryType;
        var areaEpoch = this.currencyAreaEpoch;
        bool Permitted(string category) => categories.Contains(category) && CollectionAccess.Allows(this.Access, category) &&
            this.CanReadCurrentCharacter && !QuestLoading() &&
            owner == this.activeContentId && session == this.contextLoadEpoch &&
            territory == ClientState.TerritoryType && areaEpoch == this.currencyAreaEpoch;
        var captured = new Dictionary<string, CollectionLookup>(StringComparer.Ordinal);
        var queryGate = new object();
        return arguments =>
        {
            CollectionLookup.ValidateArguments(arguments);
            var category = CollectionLookup.Category(arguments);
            var questionContinues = this.companion.CaptureQuestionContinuation();
            lock (queryGate)
            {
                lock (this.stateGate)
                    if (!Permitted(category) || !questionContinues()) throw new OperationCanceledException();
                if (!captured.TryGetValue(category, out var lookup))
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var token = deadline.Token;
                    try
                    {
                        lookup = Framework.RunOnFrameworkThread(() =>
                        {
                            lock (this.stateGate)
                            {
                                token.ThrowIfCancellationRequested();
                                if (!Permitted(category) || !questionContinues() || ReadLoadedCharacterId() != owner)
                                    throw new OperationCanceledException();
                                using var timing = PerformanceDiagnostics.Measure("reader.collections");
                                var reading = category == RecentReaderCache.MasterBestiaryCategory
                                    ? this.RecentBestiaryUnderLock() : this.collectionReader.Capture(category);
                                token.ThrowIfCancellationRequested();
                                if (!Permitted(category) || !questionContinues() || ReadLoadedCharacterId() != owner)
                                    throw new OperationCanceledException();
                                return new CollectionLookup(reading);
                            }
                        }).WaitAsync(token).GetAwaiter().GetResult();
                        captured.Add(category, lookup);
                    }
                    catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                    {
                        lock (this.stateGate)
                            if (!Permitted(category) || !questionContinues()) throw;
                        throw new TimeoutException("Collectible reader timed out.");
                    }
                }
                lock (this.stateGate)
                {
                    if (!Permitted(category) || !questionContinues()) throw new OperationCanceledException();
                    if (!lookup.IsUsable(DateTimeOffset.UtcNow))
                        throw new IOException("Recent collection observation expired; reopen the notebook to refresh.");
                    return JsonSerializer.Serialize(lookup.Query(arguments));
                }
            }
        };
    }
}
