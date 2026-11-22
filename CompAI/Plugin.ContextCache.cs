using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Game.Inventory;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace CompAI;

public sealed partial class Plugin
{
    private readonly CharacterCacheStore characterCache;
    private ulong activeContentId;
    private ulong cacheOwnerContentId;
    private long cacheRevision;
    private int contextLoadEpoch;
    private bool contextCacheLoading;
    private bool contextDisposed;
    private DateTimeOffset nextIdentityCheck;
    private SnapshotData? persistedInventorySource;
    private QuestSnapshot? persistedQuestSource;
    private CachedInventorySnapshot? persistedInventory;
    private CachedQuestSnapshot? persistedQuests;

    // Caller holds stateGate. Restored data is never attached as another character's context.
    private bool CanReadCurrentCharacter => !this.contextDisposed && !this.contextCacheLoading &&
        ClientState.IsLoggedIn && this.activeContentId != 0 && this.activeContentId == this.cacheOwnerContentId;
    internal bool ContextCanCapture { get { lock (this.stateGate) return this.CanReadCurrentCharacter; } }
    internal bool ContextCacheLoading { get { lock (this.stateGate) return this.contextCacheLoading; } }
    internal string ContextCacheStatus
    {
        get
        {
            lock (this.stateGate)
            {
                if (this.characterCache.Error is { } error) return error;
                if (this.contextCacheLoading) return "Loading this character's saved context...";
                return this.activeContentId == 0
                    ? "Offline: showing the last character's saved context. Values are last known, not live."
                    : "Saved separately for this character. Updates stay local until you choose to send context.";
            }
        }
    }

    private void InitializeContextCache()
    {
        lock (this.stateGate)
            if (!ClientState.IsLoggedIn) this.BeginContextLoadUnderLock(0);
    }

    private static unsafe ulong ReadLoadedCharacterId()
    {
        if (!ClientState.IsLoggedIn) return 0;
        var player = PlayerState.Instance();
        return player != null && player->IsLoaded ? player->ContentId : 0;
    }

    private void PollContextCharacter()
    {
        lock (this.stateGate)
        {
            if (this.contextDisposed || !ClientState.IsLoggedIn || DateTimeOffset.UtcNow < this.nextIdentityCheck) return;
            this.nextIdentityCheck = DateTimeOffset.UtcNow.AddSeconds(1);
        }
        var id = ReadLoadedCharacterId();
        if (id == 0) return;
        lock (this.stateGate)
        {
            if (this.contextDisposed || !ClientState.IsLoggedIn) return;
            if (id == this.activeContentId) return;
            this.companion.InvalidateQuestion();
            this.HideContextUnderLock();
            this.activeContentId = id;
            this.BeginContextLoadUnderLock(id);
        }
    }

    private void BeginContextLoadUnderLock(ulong id)
    {
        this.contextCacheLoading = true;
        var epoch = ++this.contextLoadEpoch;
        _ = this.LoadContextAsync(id, epoch);
    }

    private async Task LoadContextAsync(ulong requestedId, int epoch)
    {
        try
        {
            var loaded = await this.characterCache.RestoreForSessionAsync(requestedId, () =>
            {
                lock (this.stateGate) return !this.contextDisposed && epoch == this.contextLoadEpoch;
            }).ConfigureAwait(false);
            if (loaded is null) return;
            var saved = loaded.Data;
            await Framework.RunOnFrameworkThread(() =>
            {
                lock (this.stateGate)
                {
                    if (this.contextDisposed || epoch != this.contextLoadEpoch) return;
                    if (requestedId == 0 && ClientState.IsLoggedIn || requestedId != 0 &&
                        (!ClientState.IsLoggedIn || requestedId != this.activeContentId)) return;
                    var loadedId = ReadLoadedCharacterId();
                    if (requestedId != 0 && loadedId != 0 && loadedId != requestedId)
                    {
                        this.OnDetectedCharacterMismatch();
                        return;
                    }
                    this.cacheOwnerContentId = requestedId == 0 ? saved?.ContentId ?? 0 : requestedId;
                    this.cacheRevision = saved?.Revision ?? 0;
                    this.snapshot = saved?.Inventory is { } inventory ? RestoreInventory(inventory) : null;
                    this.capturePending = false;
                    this.changedSinceCapture = this.snapshot is not null;
                    this.unavailableReason = this.snapshot is not null ? "Restored last-known inventory; waiting for live refresh." : null;
                    this.contextCacheLoading = false;
                    this.RestoreQuestCache(saved?.Quests?.ToSnapshot(), requestedId != 0 && this.Access.Quests);
                    this.inventoryWatching = requestedId != 0 && this.Access.Inventory;
                    this.inventoryApplied = this.inventoryCandidate = null;
                    this.inventoryLastChecked = null;
                    this.inventoryNeedsRefresh = this.inventoryWatching;
                    this.nextInventoryCheck = DateTimeOffset.MinValue;
                    this.persistedInventorySource = this.snapshot;
                    this.persistedQuestSource = this.quests.View.Snapshot;
                    this.persistedInventory = saved?.Inventory;
                    this.persistedQuests = saved?.Quests;
                    this.observedStorage = saved?.Storage ?? ImmutableArray<StorageContainerCapture>.Empty;
                    this.persistedStorage = this.observedStorage;
                    this.observedProgression = saved?.Progression ?? ImmutableArray<ProgressionMenuCapture>.Empty;
                    this.persistedProgression = this.observedProgression;
                    this.ResetProgressionCaptureUnderLock();
                    this.observedMenus = saved?.Menus ?? ImmutableArray<CachedMenuObservation>.Empty;
                    this.persistedMenus = this.observedMenus;
                    this.ResetMenuCaptureUnderLock();
                    this.ResetRecentReadersUnderLock();
                    this.ResetRecentAchievementProgressUnderLock();
                    this.nextStorageCheck = default;
                    if (this.cacheOwnerContentId != 0) this.characterCache.Select(this.cacheOwnerContentId);
                    if (requestedId != 0 && this.Access.Quests && this.quests.View.Snapshot is null)
                        this.CaptureQuests();
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (this.stateGate)
            {
                if (this.contextDisposed || epoch != this.contextLoadEpoch) return;
                this.contextCacheLoading = false;
                this.unavailableReason = $"Saved context could not be restored ({ex.GetType().Name}).";
                this.activeContentId = 0; // Retry matching the character on a later framework tick.
            }
        }
    }

    private static SnapshotData RestoreInventory(CachedInventorySnapshot saved) => new(saved.CapturedAtUtc,
        saved.Bags.Select(bag =>
        {
            if (!Enum.TryParse<GameInventoryType>(bag.Bag, out var type) ||
                type is not (GameInventoryType.Inventory1 or GameInventoryType.Inventory2 or GameInventoryType.Inventory3 or GameInventoryType.Inventory4))
                throw new InvalidDataException("Unsupported cached inventory bag.");
            var copy = new ContainerSnapshot(type) { SlotCount = bag.SlotCount, UnavailableReason = bag.UnavailableReason };
            copy.Items.AddRange(bag.Items.Select(item => new ItemSnapshot(item.Slot, item.ItemId, item.BaseItemId,
                item.Name, item.Quantity, item.IsHq, item.Pedigree?.ToPedigree())));
            return copy;
        }).ToArray());

    private void PersistContextUnderLock()
    {
        if (this.contextDisposed || this.contextCacheLoading || this.cacheOwnerContentId == 0) return;
        var questSnapshot = this.quests.View.Snapshot;
        if (ReferenceEquals(this.persistedInventorySource, this.snapshot) && ReferenceEquals(this.persistedQuestSource, questSnapshot) &&
            this.persistedStorage == this.observedStorage && this.persistedProgression == this.observedProgression &&
            this.persistedMenus == this.observedMenus) return;
        if (!ReferenceEquals(this.persistedInventorySource, this.snapshot))
            this.persistedInventory = this.snapshot is not { } inventory ? null : new(inventory.CapturedAtUtc,
                inventory.Containers.Select(bag => new CachedInventoryBag(bag.Type.ToString(), bag.SlotCount, bag.UnavailableReason,
                    bag.Items.Select(item => new CachedInventoryItem(item.Slot, item.ItemId, item.BaseItemId, item.Name,
                        item.Quantity, item.IsHq, item.Pedigree is { } pedigree ? CachedPedigree.From(pedigree) : null)).ToImmutableArray())).ToImmutableArray());
        if (!ReferenceEquals(this.persistedQuestSource, questSnapshot))
            this.persistedQuests = questSnapshot is null ? null : CachedQuestSnapshot.From(questSnapshot);
        if (this.characterCache.Offer(new(this.cacheOwnerContentId, "Saved character", ++this.cacheRevision,
                DateTimeOffset.UtcNow, this.persistedInventory, this.persistedQuests)
                { Storage = this.observedStorage, Progression = this.observedProgression, Menus = this.observedMenus }))
        {
            this.persistedInventorySource = this.snapshot;
            this.persistedQuestSource = questSnapshot;
            this.persistedStorage = this.observedStorage;
            this.persistedProgression = this.observedProgression;
            this.persistedMenus = this.observedMenus;
        }
    }

    private void HideContextUnderLock()
    {
        this.observedMenus = ImmutableArray<CachedMenuObservation>.Empty;
        this.ResetMenuCaptureUnderLock();
        this.ResetRecentReadersUnderLock();
        this.ResetRecentAchievementProgressUnderLock();
        this.observedProgression = ImmutableArray<ProgressionMenuCapture>.Empty;
        this.ResetProgressionCaptureUnderLock();
        this.observedStorage = ImmutableArray<StorageContainerCapture>.Empty;
        this.storageReadError = null;
        this.nextStorageCheck = default;
        this.ClearGameContextUnderLock();
        ++this.contextLoadEpoch;
        this.activeContentId = this.cacheOwnerContentId = 0;
        this.inventoryWatching = false;
        this.inventoryApplied = this.inventoryCandidate = null;
        this.inventoryLastChecked = null;
        this.InvalidateUnderLock("Waiting for this character's saved context.");
        this.RestoreQuestCache(null, false);
    }

    private void OnDetectedCharacterMismatch(bool force = false)
    {
        lock (this.stateGate)
        {
            if (this.contextDisposed) return;
            // Every detection caller already reads PlayerState on this same framework callback.
            // Its temporary zero during zone loading is not evidence of another character.
            if (!force && ClientState.IsLoggedIn && ReadLoadedCharacterId() == 0)
            {
                this.ClearCharacterContextUnderLock();
                this.ClearLocationContextUnderLock();
                this.recentNpcDialogue.Observe(null, DateTimeOffset.UtcNow);
                this.PauseInventoryUnderLock("Changing areas; showing saved inventory until loading finishes.");
                this.SuspendQuestTracking("Area data is loading; cached quests will refresh after loading.");
                return;
            }
            this.HideContextUnderLock();
            this.contextCacheLoading = true;
            this.nextIdentityCheck = DateTimeOffset.MinValue;
        }
        this.companion.InvalidateQuestion();
    }

    private void OnContextLogin() => this.OnDetectedCharacterMismatch(force: true);

    private void OnContextLogout()
    {
        lock (this.stateGate)
        {
            if (this.contextDisposed) return;
            this.ClearGameContextUnderLock();
            this.ResetMenuCaptureUnderLock();
            this.ResetRecentReadersUnderLock();
            this.ResetRecentAchievementProgressUnderLock();
            this.ResetProgressionCaptureUnderLock();
            ++this.contextLoadEpoch;
            this.contextCacheLoading = false;
            this.activeContentId = 0;
            this.PauseQuestsForOffline("Offline: saved quests retained; updates resume for this character after login.");
            this.PauseInventoryUnderLock("Offline: saved inventory retained; counts are last known.");
            this.inventoryWatching = false;
            this.ClearPedigreeScanUnderLock("Offline; scan cards again after login.");
            if (this.cacheOwnerContentId == 0) this.BeginContextLoadUnderLock(0);
        }
        this.companion.InvalidateQuestion();
    }

    private void OnContextTerritoryChanged(uint _)
    {
        lock (this.stateGate)
        {
            ++this.currencyAreaEpoch;
            this.ResetMenuCaptureUnderLock();
            this.recentReaders.ResetCandidates();
            this.nextRecentReaderPoll = default;
            this.ResetProgressionCaptureUnderLock();
            this.OnGameContextAreaChangedUnderLock();
            this.SuspendQuestTracking("Area changed; cached quests will refresh after loading.");
            this.PauseInventoryUnderLock("Changing areas; showing saved inventory until loading finishes.");
        }
    }

    private void DisposeContextCache()
    {
        lock (this.stateGate)
        {
            this.PersistContextUnderLock();
            this.contextDisposed = true;
            this.ClearGameContextUnderLock();
            ++this.contextLoadEpoch;
            this.PauseInventoryUnderLock("Plugin unloaded; last known inventory is saved.");
            this.inventoryWatching = false;
        }
        this.characterCache.Dispose(); // Drain queued writes before the plugin disappears.
    }
}
