using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;

namespace CompAI;

public sealed partial class Plugin
{
    private readonly RetainerSaddlebagReader retainerStorageReader;
    private readonly HousingFcStorageReader housingStorageReader;
    private readonly ArmouryReader armouryReader;
    private ImmutableArray<StorageContainerCapture> observedStorage = ImmutableArray<StorageContainerCapture>.Empty;
    private ImmutableArray<StorageContainerCapture> persistedStorage = ImmutableArray<StorageContainerCapture>.Empty;
    private DateTimeOffset nextStorageCheck;
    private string? storageReadError;

    private static string[] StorageCategories(CompanionAccessSettings access) => StorageSnapshot.Categories.Where(category => category switch {
        "crystals" or "key_items" => access.Inventory,
        "armoury" => access.Armoury,
        "retainers" => access.Retainers, "saddlebag" => access.Saddlebag,
        "housing" => access.Housing, "fc_chest" => access.FcChest, _ => false }).ToArray();

    internal StorageContainerCapture[] ObservedStorageView
    { get { lock (this.stateGate) { var permitted = StorageCategories(this.Access); return this.observedStorage.Where(c => permitted.Contains(c.Category)).ToArray(); } } }
    internal string? StorageReadError { get { lock (this.stateGate) return this.storageReadError; } }

    private void PollObservedStorage()
    {
        lock (this.stateGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (now < this.nextStorageCheck || !this.CanReadCurrentCharacter || QuestLoading()) return;
            this.nextStorageCheck = now.AddSeconds(2);
            var access = this.Access;
            if (StorageCategories(access).Length == 0) return;
            if (ReadLoadedCharacterId() != this.activeContentId) return;
            try
            {
                var captures = this.retainerStorageReader.Capture(this.activeContentId, access.Retainers, access.Saddlebag, access.Inventory)
                    .Concat(this.housingStorageReader.Capture(this.activeContentId, access.Housing, access.FcChest))
                    .Concat(this.armouryReader.Capture(this.activeContentId, access.Armoury)).ToArray();
                if (ReadLoadedCharacterId() != this.activeContentId) return;
                var merged = this.observedStorage.ToDictionary(c => (c.Category, c.SourceKey, c.Container));
                var changed = false;
                foreach (var capture in captures)
                {
                    if (!StorageSnapshot.Valid(capture)) continue;
                    var key = (capture.Category, capture.SourceKey, capture.Container);
                    if (merged.TryGetValue(key, out var old) && old.SourceLabel == capture.SourceLabel &&
                        old.Capacity == capture.Capacity && StorageSnapshot.SameItems(old.Items, capture.Items) &&
                        now - old.CapturedAtUtc < TimeSpan.FromMinutes(1)) continue;
                    merged[key] = capture with { Items = StorageSnapshot.CloneItems(capture.Items) };
                    changed = true;
                }
                if (changed)
                {
                    // Preserve the newest bounded observations. A missing container is unknown,
                    // never replaced by an empty capture simply because its menu closed.
                    var retained = new List<StorageContainerCapture>(); var rows = 0;
                    foreach (var capture in merged.Values.OrderByDescending(c => c.CapturedAtUtc))
                        if (retained.Count < StorageSnapshot.MaxContainers && rows + capture.Items.Length <= StorageSnapshot.MaxRows)
                        { retained.Add(capture); rows += capture.Items.Length; }
                    this.observedStorage = retained.ToImmutableArray();
                    this.PersistContextUnderLock();
                }
                this.storageReadError = null;
            }
            catch (Exception ex) { this.storageReadError = $"Storage read unavailable ({ex.GetType().Name}); previous observations retained."; }
        }
    }

    private StorageLookup? FreezeStorageUnderLock()
    {
        var categories = StorageCategories(this.Access);
        return this.CanReadCurrentCharacter && categories.Length > 0 ? new(this.observedStorage, categories) : null;
    }

    private Func<JsonElement, string> CreateStorageQueryUnderLock(StorageLookup lookup)
    {
        var owner = this.activeContentId; var epoch = this.contextLoadEpoch;
        var allowed = StorageCategories(this.Access);
        return arguments =>
        {
            var continues = this.companion.CaptureQuestionContinuation();
            lock (this.stateGate)
            {
                if (!continues() || !this.CanReadCurrentCharacter || owner != this.activeContentId || epoch != this.contextLoadEpoch ||
                    allowed.Except(StorageCategories(this.Access)).Any()) throw new OperationCanceledException();
                return JsonSerializer.Serialize(lookup.Query(arguments));
            }
        };
    }

    internal StorageClearRequest? PrepareStorageClear(string? category = null, string? sourceKey = null)
    {
        lock (this.stateGate)
        {
            if (this.contextDisposed || this.contextCacheLoading) return null;
            var request = new StorageClearRequest(this.cacheOwnerContentId, this.contextLoadEpoch, category, sourceKey);
            return request.Valid ? request : null;
        }
    }

    internal bool ClearObservedStorage(StorageClearRequest request)
    {
        lock (this.stateGate)
        {
            if (this.contextDisposed || this.contextCacheLoading ||
                !request.MatchesCharacter(this.cacheOwnerContentId, this.contextLoadEpoch)) return false;
            var remaining = request.Apply(this.observedStorage);
            if (remaining.Length == this.observedStorage.Length) return true;
            this.observedStorage = remaining;
            this.preparedQuestion = null;
            this.companion.CancelAllQuestions();
            this.PersistContextUnderLock();
            return true;
        }
    }
}
