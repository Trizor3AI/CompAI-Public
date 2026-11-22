using System;
using System.Numerics;

namespace CompAI;

internal sealed record InventoryHighlightSelection(string Bag, uint Slot, uint ItemId, uint BaseItemId, int Quantity, bool IsHq)
{
    internal int BagIndex => Bag switch { "Inventory1" => 0, "Inventory2" => 1, "Inventory3" => 2, "Inventory4" => 3, _ => -1 };
    internal bool Valid => BagIndex >= 0 && Slot < 35 && ItemId != 0 && BaseItemId is > 0 and < 1_000_000 && Quantity > 0;
    internal bool Matches(InventorySlotState slot) => !slot.IsEmpty && slot.ItemId == ItemId && slot.BaseItemId == BaseItemId &&
        slot.Quantity == Quantity && slot.IsHq == IsHq;
}

internal readonly record struct InventoryHighlightRectangle(Vector2 Min, Vector2 Max, bool Selected)
{
    internal bool Valid => float.IsFinite(Min.X) && float.IsFinite(Min.Y) && float.IsFinite(Max.X) && float.IsFinite(Max.Y) &&
        Max.X - Min.X is >= 8 and <= 256 && Max.Y - Min.Y is >= 8 and <= 256;
}

internal sealed record InventoryHighlightFrame(DateTimeOffset CapturedAt, InventoryHighlightRectangle[] Slots,
    Vector2? WindowTopRight, string Status, bool SelectedTooltipVisible = false);

/// <summary>Managed lifetime and identity gate; no item-ID-only reassociation.</summary>
internal sealed class InventoryHighlightState
{
    internal InventoryHighlightSelection? Selection { get; private set; }
    internal bool Active => Selection is not null;
    internal ulong OwnerId { get; private set; }
    internal int SessionGeneration { get; private set; }
    internal DateTimeOffset ExpiresAt { get; private set; }
    internal InventoryStateStamp? Baseline { get; private set; }
    internal InventoryHighlightFrame? Frame { get; private set; }
    private DateTimeOffset? drawnAt;
    private string status = "Choose Find in bags for a current inventory result.";
    internal string Status => IsVisible(DateTimeOffset.UtcNow)
        ? "Selected slot outlined; other supported visible item slots dimmed." : status;

    internal bool Start(InventoryHighlightSelection selection, ulong ownerId, int generation, int seconds,
        InventoryStateStamp baseline, InventorySlotState selectedSlot, DateTimeOffset now)
    {
        Clear("The item location is no longer current; ask for a fresh inventory lookup.");
        if (!selection.Valid || ownerId == 0 || baseline.ContentId != ownerId || seconds is < 5 or > 60 || !selection.Matches(selectedSlot)) return false;
        Selection = selection; OwnerId = ownerId; SessionGeneration = generation;
        ExpiresAt = now.AddSeconds(seconds); Baseline = baseline;
        status = "Highlight armed; opening inventory and locating the exact selected slot.";
        return true;
    }

    internal bool Check(bool enabled, bool inventoryAllowed, ulong ownerId, int generation, bool loading, DateTimeOffset now)
    {
        if (!Active) return false;
        if (!enabled || !inventoryAllowed) Clear("Inventory highlighting is off.");
        else if (loading || ownerId == 0 || ownerId != OwnerId || generation != SessionGeneration) Clear("Character or session changed; highlight cleared.");
        else if (now >= ExpiresAt) Clear("Inventory highlight expired.");
        return Active;
    }

    internal bool Publish(InventoryStateStamp before, InventoryStateStamp after, InventoryHighlightFrame frame)
    {
        if (!Active) return false;
        if (before != Baseline || after != Baseline)
        { Clear("Inventory changed; select a fresh result before highlighting again."); return false; }
        if (frame.SelectedTooltipVisible)
        { Clear("Selected item's tooltip opened; highlight cleared."); return false; }
        if (frame.Slots.Length > 140 || Array.Exists(frame.Slots, slot => !slot.Valid) ||
            frame.Slots.Length > 0 && Array.FindAll(frame.Slots, slot => slot.Selected).Length != 1)
        { Clear("Inventory layout is unsupported; no slots were highlighted."); return false; }
        Frame = frame; status = frame.Status;
        return true;
    }

    internal InventoryHighlightFrame? VisibleFrame(DateTimeOffset now) => Active && now < ExpiresAt && Frame is { } frame &&
        now >= frame.CapturedAt && now - frame.CapturedAt <= TimeSpan.FromMilliseconds(250) ? frame : null;

    internal bool IsVisible(DateTimeOffset now) => VisibleFrame(now) is { } frame &&
        Array.Exists(frame.Slots, slot => slot.Selected) && drawnAt is { } drawn && now >= drawn &&
        now - drawn <= TimeSpan.FromMilliseconds(250);

    internal void MarkDrawn(InventoryHighlightFrame frame, DateTimeOffset now)
    {
        if (ReferenceEquals(frame, VisibleFrame(now)) && Array.Exists(frame.Slots, slot => slot.Selected)) drawnAt = now;
    }

    internal void Clear(string reason = "Inventory highlight cleared.")
    { Selection = null; Baseline = null; Frame = null; drawnAt = null; OwnerId = 0; SessionGeneration = 0; status = reason; }
}
