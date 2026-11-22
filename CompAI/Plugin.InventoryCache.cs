using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Dalamud.Game.Inventory.InventoryEventArgTypes;

namespace CompAI;

public sealed partial class Plugin
{
    private readonly InventoryStateReader inventoryStateReader;
    private bool inventoryWatching;
    private bool inventoryNeedsRefresh;
    private InventoryStateStamp? inventoryApplied;
    private InventoryStateStamp? inventoryCandidate;
    private DateTimeOffset nextInventoryCheck;
    private DateTimeOffset? inventoryLastChecked;

    internal bool InventoryAutoUpdating { get { lock (this.stateGate) return this.inventoryWatching && this.CanReadCurrentCharacter; } }
    internal DateTimeOffset? InventoryLastChecked { get { lock (this.stateGate) return this.inventoryLastChecked; } }

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        lock (this.stateGate)
        {
            if (!this.Access.Inventory || !this.inventoryWatching || this.contextDisposed) return;
            if (!InventoryEventFilter.MayAffectCarriedBags(events)) return;
            this.RevokeInventoryFindUnderLock("Inventory changed; ask for a fresh lookup before highlighting again.");
            this.changedSinceCapture = true;
            this.inventoryNeedsRefresh = true;
            this.inventoryCandidate = null;
            this.nextInventoryCheck = DateTimeOffset.UtcNow.AddMilliseconds(250);
            this.preparedQuestion = null;
            this.ClearPedigreeScanUnderLock("Inventory changed; scan cards again for new readings.");
        }
    }

    private void PauseInventoryUnderLock(string reason)
    {
        this.RevokeInventoryFindUnderLock("Inventory paused or changed; ask for a fresh lookup before highlighting again.");
        ++this.captureEpoch;
        this.capturePending = false;
        this.changedSinceCapture = this.snapshot is not null;
        this.unavailableReason = reason;
        this.preparedQuestion = null;
        this.inventoryCandidate = null;
        this.inventoryNeedsRefresh = true;
        this.inventoryLastChecked = null;
        this.nextInventoryCheck = DateTimeOffset.UtcNow.AddSeconds(1);
    }

    private void PollInventoryChanges()
    {
        int epoch;
        ulong id;
        lock (this.stateGate)
        {
            if (!this.Access.Inventory || !this.CanReadCurrentCharacter || !this.inventoryWatching || this.capturePending || DateTimeOffset.UtcNow < this.nextInventoryCheck) return;
            this.nextInventoryCheck = DateTimeOffset.UtcNow.AddSeconds(15);
            if (QuestLoading()) { this.PauseInventoryUnderLock("Loading; showing last-known inventory."); return; }
            epoch = this.captureEpoch;
            id = this.activeContentId;
        }
        try
        {
            var loadedId = ReadLoadedCharacterId();
            if (loadedId == 0) throw new InvalidOperationException("Character is loading; showing last-known inventory.");
            if (loadedId != id) { this.OnDetectedCharacterMismatch(); return; }
            var state = this.inventoryStateReader.Read(id);
            if (ReadLoadedCharacterId() != id) { this.OnDetectedCharacterMismatch(); return; }
            lock (this.stateGate)
            {
                if (!this.Access.Inventory || !this.CanReadCurrentCharacter || !this.inventoryWatching || epoch != this.captureEpoch || id != this.activeContentId) return;
                // Raw change events can be missed. A changed applied state must revoke
                // slot-bound parentage before a later capture can reuse that slot/ID.
                // No prior applied state (initial load) is not evidence of a change.
                if (this.inventoryApplied is not null && !state.Equals(this.inventoryApplied) && this.pedigrees.HasReadings)
                {
                    this.ClearPedigreeScanUnderLock("Inventory changed; scan cards again for new readings.");
                    this.preparedQuestion = null;
                }
                if (!state.Equals(this.inventoryCandidate))
                {
                    this.inventoryCandidate = state;
                    this.changedSinceCapture = this.snapshot is not null;
                    this.unavailableReason = "Inventory update settling; showing the previous capture.";
                    this.nextInventoryCheck = DateTimeOffset.UtcNow.AddMilliseconds(250);
                    return;
                }
                if (this.inventoryNeedsRefresh || !state.Equals(this.inventoryApplied))
                    this.CaptureInventory(automatic: true);
                else
                {
                    this.changedSinceCapture = false;
                    this.unavailableReason = null;
                    this.inventoryLastChecked = DateTimeOffset.UtcNow;
                }
            }
        }
        catch (Exception ex)
        {
            lock (this.stateGate)
            {
                if (epoch != this.captureEpoch || !this.inventoryWatching) return;
                this.PauseInventoryUnderLock(ex is InvalidOperationException ? ex.Message : $"Inventory temporarily unavailable ({ex.GetType().Name}).");
                this.nextInventoryCheck = DateTimeOffset.UtcNow.AddSeconds(3);
            }
        }
    }

    private void ClearInventoryCache()
    {
        lock (this.stateGate)
        {
            if (this.contextCacheLoading) return;
            this.SetAccess(false, this.Access.Quests, this.Access.WebSearch);
            this.inventoryWatching = false;
            this.inventoryApplied = this.inventoryCandidate = null;
            this.inventoryNeedsRefresh = false;
            this.inventoryLastChecked = null;
            this.InvalidateUnderLock("Inventory cache cleared; automatic updates stopped.");
            this.observedStorage = this.observedStorage.Where(c => c.Category is not ("crystals" or "key_items")).ToImmutableArray();
            this.PersistContextUnderLock();
        }
        this.companion.InvalidateQuestion();
    }

    private void ClearQuestCache()
    {
        lock (this.stateGate)
        {
            if (this.contextCacheLoading) return;
            this.SetAccess(this.Access.Inventory, false, this.Access.WebSearch);
            this.ResetQuestTracking("Quest cache cleared; automatic updates stopped.");
            this.PersistContextUnderLock();
        }
    }
}
