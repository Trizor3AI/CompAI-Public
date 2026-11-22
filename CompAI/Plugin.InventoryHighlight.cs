using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CompAI;

public sealed partial class Plugin
{
    private CompanionBetaSettingsStore betaSettings = null!;
    private InventoryHighlightController inventoryHighlight = null!;
    private readonly InventoryFindCatalog inventoryFind = new();
    private long inventoryHighlightChanges;
    private bool inventoryHighlightWasAllowed;
    private (Guid Key, int Index)? pendingInventoryHighlight;
    private string? inventoryFindNotice;

    private void InitializeInventoryHighlight()
    {
        this.betaSettings = new(Path.Combine(this.storage.Configuration, "companion-beta.json"));
        this.inventoryHighlight = new(GameGui, Inventory);
    }

    internal bool InventoryHighlightEnabled { get { lock (this.stateGate) return this.betaSettings.Settings.InventoryHighlight; } }
    internal int InventoryHighlightSeconds { get { lock (this.stateGate) return this.betaSettings.Settings.InventoryHighlightSeconds; } }
    internal string InventoryHighlightStatus { get { lock (this.stateGate) return this.pendingInventoryHighlight is not null
        ? "Checking the selected inventory slot…" : this.inventoryHighlight.Status; } }
    internal bool InventoryHighlightActive { get { lock (this.stateGate) return this.pendingInventoryHighlight is not null || this.inventoryHighlight.Active; } }
    internal string? InventoryHighlightSaveError { get { lock (this.stateGate) return this.betaSettings.Error; } }
    internal string? InventoryFindNotice { get { lock (this.stateGate) return this.inventoryFindNotice ??
        (this.InventoryHighlightActive ? this.InventoryHighlightStatus : null); } }

    internal void SetInventoryHighlight(bool enabled)
    {
        lock (this.stateGate)
        {
            this.betaSettings.Save(this.betaSettings.Settings with { InventoryHighlight = enabled });
            if (!this.InventoryHighlightEnabled) this.RevokeInventoryFindUnderLock("Inventory highlighting is off.");
            if (!this.ShopHighlightEnabled) this.RevokeShopFindUnderLock("Shop highlighting is off.");
        }
    }

    internal void SetInventoryHighlightSeconds(int seconds)
    {
        lock (this.stateGate)
        {
            this.betaSettings.Save(this.betaSettings.Settings with { InventoryHighlightSeconds = Math.Clamp(seconds, 5, 60) });
            if (!this.InventoryHighlightEnabled) this.RevokeInventoryFindUnderLock("Inventory highlighting is off.");
            if (!this.ShopHighlightEnabled) this.RevokeShopFindUnderLock("Shop highlighting is off.");
        }
    }

    internal void ClearInventoryHighlight()
    {
        lock (this.stateGate)
        {
            this.pendingInventoryHighlight = null;
            this.inventoryFindNotice = null;
            this.inventoryHighlight.Clear();
        }
    }

    private InventoryFindStamp InventoryFindStampUnderLock() =>
        new(this.activeContentId, this.contextLoadEpoch, this.captureEpoch, this.inventoryHighlightChanges);

    private bool InventoryFindCurrentUnderLock(InventoryFindStamp stamp) =>
        this.InventoryHighlightEnabled && this.Access.Inventory && this.CanReadCurrentCharacter &&
        !this.capturePending && !this.changedSinceCapture && this.snapshot is not null &&
        stamp == this.InventoryFindStampUnderLock();

    // Only rows actually returned by the permitted tool can produce a local action.
    // Its session-only key and local identity checks never enter the model payload.
    private Func<JsonElement, string> CreateInventoryQueryUnderLock(InventoryLookup lookup)
    {
        var key = Guid.NewGuid();
        var stamp = this.InventoryFindStampUnderLock();
        return arguments =>
        {
            var result = lookup.Query(arguments);
            lock (this.stateGate)
            {
                if (!result.changed && this.InventoryFindCurrentUnderLock(stamp) &&
                    this.inventoryFind.Add(key, stamp, result.rows.Select(row => new InventoryFindItem(
                        row.name ?? string.Empty, row.bag, row.slot, row.item_id, row.base_item_id, row.quantity, row.is_hq)),
                        suggestReturnedLocations: InventoryLookup.HasExactLocations(arguments)))
                    this.companion.AttachInventoryFindKey(key);
            }
            return JsonSerializer.Serialize(result);
        };
    }

    internal InventoryFindGroup? InventoryFindItems(Guid key)
    {
        lock (this.stateGate) return this.inventoryFind.Get(key);
    }

    internal string? FindInventoryItem(Guid key, int index)
    {
        lock (this.stateGate)
        {
            if (this.inventoryFind.Get(key) is not { } group || !this.InventoryFindCurrentUnderLock(group.Stamp))
                return "This item location is no longer current. Ask for a fresh inventory lookup.";
            if (index < 0 || index >= group.Items.Count) return "This item is unavailable.";
            // Consume on the framework thread, with the same identity checks repeated there.
            this.inventoryFindNotice = null;
            this.pendingInventoryHighlight = (key, index);
            return null;
        }
    }

    private void RevokeInventoryFindUnderLock(string reason)
    {
        ++this.inventoryHighlightChanges;
        this.pendingInventoryHighlight = null;
        this.inventoryFindNotice = null;
        this.inventoryFind.Clear();
        this.inventoryHighlight.Clear(reason);
    }

    private void UpdateInventoryHighlight()
    {
        lock (this.stateGate)
        {
            var allowed = this.InventoryHighlightEnabled && this.Access.Inventory && this.CanReadCurrentCharacter;
            if (!allowed && this.inventoryHighlightWasAllowed)
                this.RevokeInventoryFindUnderLock("Inventory highlighting is unavailable for this session.");
            this.inventoryHighlightWasAllowed = allowed;
            if (!allowed || (!this.inventoryHighlight.Active && this.pendingInventoryHighlight is null)) return;
            var loading = QuestLoading();
            if (this.pendingInventoryHighlight is { } pending)
            {
                this.pendingInventoryHighlight = null;
                var group = this.inventoryFind.Get(pending.Key);
                if (loading || group is null || !this.InventoryFindCurrentUnderLock(group.Stamp) ||
                    pending.Index < 0 || pending.Index >= group.Items.Count)
                {
                    this.inventoryFindNotice = "Inventory changed or is loading. Ask for a fresh inventory lookup.";
                    this.inventoryHighlight.Clear(this.inventoryFindNotice);
                }
                else
                {
                    var item = group.Items[pending.Index];
                    if (!this.inventoryHighlight.Start(new(item.Bag, item.Slot, item.ItemId, item.BaseItemId, item.Quantity, item.IsHq),
                        this.activeContentId, this.contextLoadEpoch, this.InventoryHighlightSeconds))
                        this.inventoryFindNotice = this.inventoryHighlight.Status;
                }
            }
            this.inventoryHighlight.Update(allowed, this.Access.Inventory, this.activeContentId, this.contextLoadEpoch, loading);
        }
    }

    private void DrawInventoryHighlight()
    {
        lock (this.stateGate)
        {
            if (!this.contextDisposed && this.InventoryHighlightEnabled && this.Access.Inventory && this.CanReadCurrentCharacter)
                this.inventoryHighlight.Draw();
        }
    }
}
