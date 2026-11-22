using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace CompAI;

public sealed partial class Plugin
{
    private readonly string valuationSession = Guid.NewGuid().ToString("N");
    private int valuationAccessEpoch;

    private ValuationLookup? FreezeValuationUnderLock(bool includeInventory, bool online)
    {
        if (!online || !this.Access.WebSearch || !this.CanReadCurrentCharacter) return null;
        // EventItem IDs belong to another sheet and must never become market Item IDs.
        // Armoury ownership does not establish market sale eligibility (binding, spiritbond, etc.).
        var categories = StorageCategories(this.Access).Where(c => c is not ("key_items" or "armoury")).ToList();
        var captures = this.observedStorage.Where(c => categories.Contains(c.Category)).Select(c => new ValuationContainer(
            c.Category, c.SourceKey, c.SourceLabel, c.Container, c.CapturedAtUtc, true, c.Items)).ToList();
        if (includeInventory && this.Access.Inventory && this.snapshot is { } inventory)
        {
            categories.Add("inventory");
            captures.AddRange(inventory.Containers.Select(c => new ValuationContainer("inventory", "carried", "Carried bags",
                c.Type.ToString(), inventory.CapturedAtUtc, c.UnavailableReason is null && c.SlotCount.HasValue,
                c.Items.Select(i => new StorageItem((int)i.Slot, i.ItemId, i.BaseItemId,
                    i.Name ?? $"Item #{i.BaseItemId}", i.Quantity, i.IsHq)).ToArray())));
        }
        return categories.Count == 0 ? null : new ValuationLookup(captures, categories,
            includeInventory && (this.changedSinceCapture || this.capturePending),
            $"{this.valuationSession}:{this.activeContentId}:{this.contextLoadEpoch}:{this.valuationAccessEpoch}");
    }

    private Func<JsonElement, string> CreateValuationQueryUnderLock(ValuationLookup lookup, bool includeInventory)
    {
        var owner = this.activeContentId;
        var epoch = this.contextLoadEpoch;
        var allowed = StorageCategories(this.Access).Where(c => c is not ("key_items" or "armoury")).ToArray();
        return arguments =>
        {
            var continues = this.companion.CaptureQuestionContinuation();
            lock (this.stateGate)
            {
                bool Permitted() => continues() && this.CanReadCurrentCharacter && this.Access.WebSearch &&
                    (!includeInventory || this.Access.Inventory) && owner == this.activeContentId && epoch == this.contextLoadEpoch &&
                    !allowed.Except(StorageCategories(this.Access)).Any();
                if (!Permitted()) throw new OperationCanceledException();
                var result = JsonSerializer.Serialize(lookup.Query(arguments));
                if (!Permitted()) throw new OperationCanceledException();
                return result;
            }
        };
    }
}
