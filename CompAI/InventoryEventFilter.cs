using System;
using System.Collections.Generic;
using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;

namespace CompAI;

internal static class InventoryEventFilter
{
    // API 15 InventoryChangedRaw reports Added/Removed/Changed before interpreting
    // pairs as moves, splits or merges. Consume this temporary batch synchronously:
    // https://github.com/goatcorp/Dalamud/blob/master/Dalamud/Game/Inventory/GameInventory.cs
    // A cross-boundary move therefore includes a carried-bag raw event. Unexpected
    // complex events remain conservative rather than guessing which slots changed.
    internal static bool MayAffectCarriedBags(IReadOnlyCollection<InventoryEventArgs>? events)
    {
        if (events is null) return true;
        try
        {
            var expectedCount = events.Count;
            if (expectedCount <= 0) return true;
            var seen = 0;
            foreach (var change in events)
            {
                if (++seen > expectedCount || !IsKnownOutsideChange(change)) return true;
            }
            return seen != expectedCount;
        }
        catch (Exception)
        {
            // Failure to enumerate or classify is not evidence that carried bags
            // were untouched. Keep the existing invalidation in that case.
            return true;
        }
    }

    private static bool IsKnownOutsideChange(InventoryEventArgs? change) => change switch
    {
        InventoryItemAddedArgs added when added.Type == GameInventoryEvent.Added =>
            IsKnownOutsideItem(in added.Item),
        InventoryItemRemovedArgs removed when removed.Type == GameInventoryEvent.Removed =>
            IsKnownOutsideItem(in removed.Item),
        InventoryItemChangedArgs changed when changed.Type == GameInventoryEvent.Changed =>
            IsKnownOutsideItem(in changed.OldItemState) && IsKnownOutsideItem(in changed.Item) &&
            changed.OldItemState.ContainerType == changed.Item.ContainerType &&
            changed.OldItemState.InventorySlot == changed.Item.InventorySlot,
        _ => false,
    };

    private static bool IsKnownOutsideItem(in GameInventoryItem item)
    {
        // A default value has no valid location (or callable item accessors).
        if (item.Equals(default(GameInventoryItem))) return false;
        var type = item.ContainerType;
        return Enum.IsDefined(type) && type is not (
            GameInventoryType.Inventory1 or GameInventoryType.Inventory2 or
            GameInventoryType.Inventory3 or GameInventoryType.Inventory4 or
            GameInventoryType.Invalid or GameInventoryType.Unknown2006 or GameInventoryType.Unknown2008);
    }
}
