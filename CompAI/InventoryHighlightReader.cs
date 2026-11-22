using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.Inventory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Bounds = FFXIVClientStructs.FFXIV.Common.Math.Bounds;

namespace CompAI;

/// <summary>Framework thread only. Copies known open grids; never assumes visual order equals inventory order.</summary>
internal sealed unsafe class InventoryHighlightReader(IGameGui gui, IGameInventory inventory)
{
    private readonly InventoryStateReader stateReader = new(inventory);
    // Names are the installed AddonInventoryGrid AddonAttribute's complete carried-grid allowlist.
    private static readonly string[] GridNames = ["InventoryGrid", "InventoryGrid0", "InventoryGrid1",
        "InventoryGrid0E", "InventoryGrid1E", "InventoryGrid2E", "InventoryGrid3E"];
    private static readonly string[] WindowNames = ["Inventory", "InventoryLarge", "InventoryExpansion"];

    internal InventoryStateStamp ReadState(ulong owner)
    {
        RequireOwner(owner);
        var state = stateReader.Read(owner);
        RequireOwner(owner);
        return state;
    }

    internal InventorySlotState ReadSelected(InventoryHighlightSelection selection)
    {
        if (!selection.Valid) throw new InvalidOperationException("Invalid carried slot.");
        var type = (GameInventoryType)selection.BagIndex;
        var slots = inventory.GetInventoryItems(type);
        if (slots.Length != 35) throw new InvalidOperationException("Inventory is not loaded.");
        var item = slots[(int)selection.Slot];
        if (item.IsEmpty || item.ContainerType != type || item.InventorySlot != selection.Slot)
            return InventorySlotState.Empty;
        return new(item.ItemId, item.BaseItemId, item.Quantity, item.IsHq);
    }

    // Called only while fulfilling one explicit Find click. A later user page change is
    // respected: the controller consumes the request after the first ready page selection.
    internal bool TrySelectPage(InventoryHighlightSelection selection, ulong owner, out bool switched)
    {
        switched = false;
        RequireOwner(owner);
        AtkUnitBase* parent = null;
        var layout = -1;
        for (var index = 0; index < WindowNames.Length; index++)
        {
            var candidate = gui.GetAddonByName<AtkUnitBase>(WindowNames[index], 1);
            if (candidate == null || !candidate->IsReady || !candidate->IsVisible || candidate->Alpha == 0 ||
                candidate->RootNode == null || !Visible(candidate->RootNode)) continue;
            if (parent != null) return false;
            parent = candidate; layout = index;
        }
        if (parent == null) return false;
        var control = AddonControl(parent, layout);
        if (control == null || control->ParentAddon != parent || !control->IsChildSetupComplete) return false;
        Span<int> order = stackalloc int[140];
        if (!ReadSortOrder(owner, order)) return false;
        var target = TargetTab(layout, selection, order);
        if (target < 0) return false;
        if (!selection.Matches(ReadSelected(selection))) throw new InvalidOperationException("Selected inventory item changed.");
        Span<int> afterOrder = stackalloc int[140];
        RequireOwner(owner);
        if (!ReadSortOrder(owner, afterOrder) || !order.SequenceEqual(afterOrder) ||
            gui.GetAddonByName<AtkUnitBase>(WindowNames[layout], 1) != parent || !parent->IsReady || !parent->IsVisible)
            return false;
        if (CurrentTab(parent, layout) == target) return true;
        // Installed native SetTab methods update the actual grid and tab controls together.
        // Never write TabIndex or guess callback numbers. Only carried-item tabs are allowed.
        switch (layout)
        {
            case 0: ((AddonInventory*)parent)->SetTab(target); break;
            case 1: ((AddonInventoryLarge*)parent)->SetTab(target); break;
            case 2: ((AddonInventoryExpansion*)parent)->SetTab(target, false); break;
            default: return false;
        }
        switched = true;
        return true;
    }

    internal static int TargetTab(int layout, InventoryHighlightSelection selection, ReadOnlySpan<int> order)
    {
        if (!selection.Valid || order.Length != 140 || layout is < 0 or > 2) return -1;
        var physical = selection.BagIndex * 35 + (int)selection.Slot;
        var visual = -1;
        for (var index = 0; index < order.Length; index++)
        {
            if (order[index] != physical) continue;
            if (visual >= 0) return -1;
            visual = index;
        }
        return visual < 0 ? -1 : layout switch { 0 => visual / 35, 1 => visual / 70, _ => 0 };
    }

    internal InventoryHighlightFrame Capture(InventoryHighlightSelection selection, ulong owner)
    {
        RequireOwner(owner);
        if (SelectedTooltipVisible(selection))
            return new(DateTimeOffset.UtcNow, [], null, "Selected item's tooltip opened.", SelectedTooltipVisible: true);
        AtkUnitBase* parent = null;
        var layout = -1;
        for (var layoutIndex = 0; layoutIndex < WindowNames.Length; layoutIndex++)
        {
            var name = WindowNames[layoutIndex];
            var candidate = gui.GetAddonByName<AtkUnitBase>(name, 1);
            if (candidate == null || !candidate->IsReady || !candidate->IsVisible) continue;
            if (parent != null) return Empty("Multiple inventory layouts are visible; no highlight drawn.");
            parent = candidate;
            layout = layoutIndex;
        }
        if (parent == null || parent->RootNode == null)
            return Empty("Open your carried inventory to show the selected item.");
        var parentBounds = new Bounds();
        parent->RootNode->GetBounds(&parentBounds);
        if (!FiniteBounds(new(parentBounds.Pos1.X, parentBounds.Pos1.Y), new(parentBounds.Pos2.X, parentBounds.Pos2.Y)))
            return Empty("Inventory bounds are unavailable; no highlight drawn.");
        var topRight = new Vector2(parentBounds.Pos2.X, parentBounds.Pos1.Y);
        var stage = AtkStage.Instance();
        if (stage == null || stage->RaptureAtkUnitManager == null) return Empty("Inventory UI is not ready.");
        var manager = &stage->RaptureAtkUnitManager->AtkUnitManager;
        var tab = CurrentTab(parent, layout);
        var control = AddonControl(parent, layout);
        var inventoryFamily = new HashSet<nint> { (nint)parent };
        foreach (var name in GridNames)
        {
            var grid = gui.GetAddonByName<AddonInventoryGrid>(name, 1);
            if (grid != null && grid->IsReady && grid->IsVisible && VisualPage(layout, tab, name) >= 0 &&
                IsInventoryChild(grid, parent, control)) inventoryFamily.Add((nint)grid);
        }
        if (manager->FocusedAddon == null || !inventoryFamily.Contains((nint)manager->FocusedAddon))
            return Empty("Focus the inventory window to show the highlight.");
        Span<int> order = stackalloc int[140];
        if (!ReadSortOrder(owner, order)) return Empty("Inventory sort order is not ready; no highlight drawn.");
        var rectangles = new List<InventoryHighlightRectangle>(140);
        var locations = new HashSet<(InventoryType, int)>();
        var selectedCount = 0;
        foreach (var name in GridNames)
        {
            var grid = gui.GetAddonByName<AddonInventoryGrid>(name, 1);
            var page = VisualPage(layout, tab, name);
            if (grid == null || !grid->IsReady || !grid->IsVisible || grid->RootNode == null ||
                page < 0 || !inventoryFamily.Contains((nint)grid)) continue;
            var slots = grid->Slots;
            if (slots.Length != 35) continue;
            for (var index = 0; index < 35; index++)
            {
                var component = slots[index].Value;
                if (component == null || component->OwnerNode == null) continue;
                var node = &component->OwnerNode->AtkResNode;
                if (!Visible(node)) continue;
                var visualSlot = VisualSlot(node->NodeId);
                if (visualSlot < 0) return Empty("Inventory slot layout is unsupported; no highlight drawn.");
                // Slots are visual positions. The native sorter resolves each position to
                // the original bag/slot, including cards sharing the same item ID.
                var location = order[page * 35 + visualSlot];
                var type = (InventoryType)(location / 35);
                var slot = location % 35;
                // A registered item tooltip is optional, but conflicting evidence fails closed.
                var tooltipBinding = FindSlotBinding(&stage->TooltipManager, component, grid->Id, parent->Id,
                    out var tooltipType, out var tooltipSlot);
                if (tooltipBinding == BindingStatus.Conflict ||
                    tooltipBinding == BindingStatus.Found && (tooltipType != type || tooltipSlot != slot))
                    return Empty("Inventory bindings disagree with the sort order; no highlight drawn.");
                if (!locations.Add((type, slot)))
                    return Empty("Inventory slot bindings are ambiguous; no highlight drawn.");
                var selected = (int)type == selection.BagIndex && slot == selection.Slot;
                if (selected && !selection.Matches(ReadSelected(selection)))
                    throw new InvalidOperationException("Selected inventory item changed.");
                var bounds = new Bounds();
                node->GetBounds(&bounds);
                var rectangle = new InventoryHighlightRectangle(new(bounds.Pos1.X, bounds.Pos1.Y), new(bounds.Pos2.X, bounds.Pos2.Y), selected);
                if (!rectangle.Valid || bounds.Pos1.X < parentBounds.Pos1.X || bounds.Pos1.Y < parentBounds.Pos1.Y ||
                    bounds.Pos2.X > parentBounds.Pos2.X || bounds.Pos2.Y > parentBounds.Pos2.Y) continue;
                if (selected) selectedCount++;
                rectangles.Add(rectangle);
                if (rectangles.Count > 140) return Empty("Inventory layout exceeds the supported slot bound.");
            }
            if (gui.GetAddonByName<AddonInventoryGrid>(name, 1) != grid || !grid->IsReady || !grid->IsVisible)
                return Empty("Inventory layout changed; no highlight drawn.");
        }
        RequireOwner(owner);
        if (!parent->IsReady || !parent->IsVisible) return Empty("Inventory closed; no highlight drawn.");
        Span<int> afterOrder = stackalloc int[140];
        if (CurrentTab(parent, layout) != tab || !ReadSortOrder(owner, afterOrder) || !order.SequenceEqual(afterOrder))
            return Empty("Inventory page or sort order changed; waiting for it to settle.");
        if (!NativeOverlayClear(manager, inventoryFamily, rectangles, topRight) ||
            manager->FocusedAddon == null || !inventoryFamily.Contains((nint)manager->FocusedAddon))
            return Empty("Highlight paused: another game UI overlaps inventory or its visibility cannot be verified.");
        // Never dim the rest of a page when the selected slot is not visible on that page.
        return new(DateTimeOffset.UtcNow, selectedCount == 1 ? rectangles.ToArray() : [], topRight,
            selectedCount == 1 ? "Selected slot located; waiting for the overlay to draw." :
            rectangles.Count > 0 ? "Selected slot is not on the visible page. Change inventory page manually." :
            "This inventory layout has no supported visible item bindings; no highlight is drawn.");
    }

    private static InventoryHighlightFrame Empty(string reason) => new(DateTimeOffset.UtcNow, [], null, reason);

    // Native typed TabIndex plus the carried-grid layout used by the game. Corroborated
    // by CriticalCommonLib's AtkInventoryLarge/Expansion and InventoryTools' overlays.
    // Do not use physical bag numbers directly as visual page numbers after sorting.
    internal static int VisualPage(int layout, int tab, string grid) => (layout, tab, grid) switch
    {
        (0, >= 0 and < 4, "InventoryGrid") => tab,
        (1, >= 0 and < 2, "InventoryGrid0") => tab * 2,
        (1, >= 0 and < 2, "InventoryGrid1") => tab * 2 + 1,
        (2, 0, "InventoryGrid0E") => 0,
        (2, 0, "InventoryGrid1E") => 1,
        (2, 0, "InventoryGrid2E") => 2,
        (2, 0, "InventoryGrid3E") => 3,
        _ => -1,
    };

    // Carried-grid slot node IDs are 3..37, independent of the order of node pointers.
    // See CriticalCommonLib/Services/Ui/AtkInventoryLarge.cs at 34d364ea938e585b.
    internal static int VisualSlot(uint nodeId) => nodeId is >= 3 and <= 37 ? (int)nodeId - 3 : -1;

    private static int CurrentTab(AtkUnitBase* parent, int layout) => layout switch
    {
        0 => ((AddonInventory*)parent)->TabIndex,
        1 => ((AddonInventoryLarge*)parent)->TabIndex,
        2 => ((AddonInventoryExpansion*)parent)->TabIndex,
        _ => -1,
    };

    private static AtkAddonControl* AddonControl(AtkUnitBase* parent, int layout) => layout switch
    {
        0 => &((AddonInventory*)parent)->AddonControl,
        1 => &((AddonInventoryLarge*)parent)->AddonControl,
        2 => &((AddonInventoryExpansion*)parent)->AddonControl,
        _ => null,
    };

    private static bool IsInventoryChild(AddonInventoryGrid* grid, AtkUnitBase* parent, AtkAddonControl* control)
    {
        if (grid->ParentId == parent->Id || grid->HostId == parent->Id) return true;
        // Some layouts link grids through AddonControl instead of ParentId/HostId.
        // Bound traversal and require the live child's exact address, not just its name.
        if (control == null || control->ParentAddon != parent || !control->IsChildSetupComplete ||
            control->ChildAddons.LongCount is <= 0 or > 16) return false;
        var remaining = (int)control->ChildAddons.LongCount;
        foreach (var entry in control->ChildAddons)
        {
            if (remaining-- <= 0) break;
            if (entry.Value != null && entry.Value->AtkUnitBase == (AtkUnitBase*)grid) return true;
        }
        return false;
    }

    private static bool ReadSortOrder(ulong owner, Span<int> order)
    {
        var module = ItemOrderModule.Instance();
        if (module == null || module->CharacterContentId != owner) return false;
        return CopySortOrder(module->InventorySorter, order);
    }

    internal static bool CopySortOrder(ItemOrderModuleSorter* sorter, Span<int> order)
    {
        // ItemOrderModule's Items is the display order; each entry contains the
        // original container Page and physical Slot. Never fall back to item IDs.
        if (sorter == null || order.Length != 140 || sorter->InventoryType != InventoryType.Inventory1 ||
            sorter->ItemsPerPage != 35 || sorter->Items.WithOps.First == null ||
            sorter->Items.WithOps.End < sorter->Items.WithOps.Last || sorter->Items.LongCount != 140 ||
            sorter->SortFunctionIndex != -1) return false;
        // SortFunctionIndex=-1 is documented as done. PercentComplete is not a
        // readiness flag: a loaded saved order need not have run a new sort.
        Span<bool> seen = stackalloc bool[140];
        seen.Clear();
        for (var index = 0; index < 140; index++)
        {
            var item = sorter->Items[index].Value;
            if (item == null || item->Page >= 4 || item->Slot >= 35) return false;
            var location = item->Page * 35 + item->Slot;
            if (seen[location]) return false;
            seen[location] = true;
            order[index] = location;
        }
        return true;
    }

    private bool SelectedTooltipVisible(InventoryHighlightSelection selection)
    {
        var addon = gui.GetAddonByName<AtkUnitBase>("ItemDetail", 1);
        if (addon == null || !TooltipShown(addon->IsReady, addon->IsVisible, addon->Alpha,
                addon->RootNode != null && Visible(addon->RootNode))) return false;
        var agent = AgentItemDetail.Instance();
        if (agent == null) return false;
        var kind = agent->DetailKind;
        var type = agent->TypeOrId;
        var slot = agent->Index;
        var itemId = agent->ItemId;
        if (!SelectedTooltipIdentity(selection, kind, type, slot, itemId, gui.HoveredItem, agent->IsDirty) ||
            !selection.Matches(ReadSelected(selection))) return false;
        // Reject a tooltip being regenerated or an identity changed during observation.
        return agent == AgentItemDetail.Instance() && agent->DetailKind == kind && agent->TypeOrId == type &&
            agent->Index == slot && agent->ItemId == itemId && !agent->IsDirty && gui.HoveredItem == itemId &&
            gui.GetAddonByName<AtkUnitBase>("ItemDetail", 1) == addon &&
            TooltipShown(addon->IsReady, addon->IsVisible, addon->Alpha, addon->RootNode != null && Visible(addon->RootNode));
    }

    internal static bool SelectedTooltipIdentity(InventoryHighlightSelection selection, DetailKind kind,
        uint type, uint slot, uint itemId, ulong hoveredItem, bool dirty) =>
        selection.Valid && !dirty && kind == DetailKind.InventoryItem && type is >= 48 and <= 51 &&
        type - 48 == selection.BagIndex && slot == selection.Slot && itemId == selection.ItemId && hoveredItem == itemId;

    internal static bool TooltipShown(bool ready, bool visible, byte alpha, bool rootVisible) => ready && visible && alpha > 0 && rootVisible;

    private enum BindingStatus { Missing, Found, Conflict }

    private static BindingStatus FindSlotBinding(AtkTooltipManager* manager, AtkComponentDragDrop* component,
        ushort gridId, ushort parentId, out InventoryType type, out int slot)
    {
        type = default; slot = -1;
        // Check only nodes explicitly owned by this slot. A text tooltip on one of them
        // must not prevent finding the item tooltip on another. Conflicting bindings fail closed.
        Span<nint> keys = stackalloc nint[4];
        keys.Clear();
        keys[0] = (nint)component->OwnerNode;
        keys[1] = (nint)component->AtkResNode;
        if (component->AtkComponentIcon != null)
        {
            keys[2] = (nint)component->AtkComponentIcon->OwnerNode;
            keys[3] = (nint)component->AtkComponentIcon->AtkResNode;
        }
        var found = false;
        foreach (var key in keys)
        {
            var info = FindTooltip(manager, (AtkResNode*)key);
            if (info == null || !CarriedBinding(*info, gridId, parentId, out var nextType, out var nextSlot)) continue;
            if (found && (nextType != type || nextSlot != slot)) return BindingStatus.Conflict;
            found = true; type = nextType; slot = nextSlot;
        }
        return found ? BindingStatus.Found : BindingStatus.Missing;
    }

    internal static bool CarriedBinding(AtkTooltipManager.AtkTooltipInfo info, ushort gridId, ushort parentId,
        out InventoryType type, out int slot)
    {
        var item = info.AtkTooltipArgs.ItemArgs;
        type = item.InventoryType; slot = item.Slot;
        // Installed XML explicitly documents InventoryType and Slot for Kind=InventoryItem.
        // This optional cross-check uses the explicit physical slot, not an icon match.
        return gridId != 0 && parentId != 0 && info.Type == AtkTooltipType.Item && (info.ParentId == gridId || info.ParentId == parentId) &&
            item.Kind == DetailKind.InventoryItem && (uint)type <= 3 && slot is >= 0 and < 35;
    }

    private bool NativeOverlayClear(AtkUnitManager* manager, HashSet<nint> inventoryFamily,
        List<InventoryHighlightRectangle> rectangles, Vector2 topRight)
    {
        // The installed list is a fixed256-entry container. Do not guess relative draw order:
        // a foreign visible window intersecting any overlay area pauses it, even if behind us.
        // Item/text tooltips do not pause highlighting, even when they overlap slots.
        var itemTooltip = gui.GetAddonByName<AtkUnitBase>("ItemDetail", 1);
        var textTooltip = gui.GetAddonByName<AtkUnitBase>("Tooltip", 1);
        var list = &manager->AllLoadedUnitsList;
        var count = list->Count;
        var entries = list->Entries;
        if (count is 0 or > 256 || entries.Length != 256) return false;
        var clearMin = topRight + new Vector2(-240, 0);
        var clearMax = topRight + new Vector2(0, 80);
        for (var index = 0; index < count; index++)
        {
            var addon = entries[index].Value;
            if (addon == null || addon == itemTooltip || addon == textTooltip ||
                inventoryFamily.Contains((nint)addon) || !addon->IsVisible || addon->Alpha == 0) continue;
            // WindowNode is the installed typed window component. HUD/root-only addons
            // often span the viewport and do not establish an overlapping window.
            // Non-window decorative UI occlusion remains outside this bounded beta guard.
            if (addon->WindowNode == null) continue;
            if (!addon->IsReady) return false;
            var node = &addon->WindowNode->AtkResNode;
            if (!Visible(node)) continue;
            var bounds = new Bounds();
            node->GetBounds(&bounds);
            var min = new Vector2(bounds.Pos1.X, bounds.Pos1.Y);
            var max = new Vector2(bounds.Pos2.X, bounds.Pos2.Y);
            // An empty/unknown visible window does not prove it is outside the overlay.
            if (!FiniteBounds(min, max)) return false;
            if (Overlaps(min, max, clearMin, clearMax)) return false;
            foreach (var rectangle in rectangles)
                if (Overlaps(min, max, rectangle.Min, rectangle.Max)) return false;
        }
        return list->Count == count;
    }

    private static bool Overlaps(Vector2 min, Vector2 max, Vector2 otherMin, Vector2 otherMax) =>
        min.X < otherMax.X && max.X > otherMin.X && min.Y < otherMax.Y && max.Y > otherMin.Y;
    private static bool FiniteBounds(Vector2 min, Vector2 max) =>
        float.IsFinite(min.X) && float.IsFinite(min.Y) && float.IsFinite(max.X) && float.IsFinite(max.Y) &&
        max.X > min.X && max.Y > min.Y;
    private static void RequireOwner(ulong owner)
    {
        var player = PlayerState.Instance();
        if (owner == 0 || player == null || !player->IsLoaded || player->ContentId != owner)
            throw new InvalidOperationException("Character is not loaded.");
    }
    private static bool Visible(AtkResNode* node)
    {
        for (var depth = 0; depth < 32; depth++)
        {
            if (node == null) return true;
            if ((node->NodeFlags & NodeFlags.Visible) == 0 || node->IsDrawDisabled) return false;
            node = node->ParentNode;
        }
        return false;
    }

    private static AtkTooltipManager.AtkTooltipInfo* FindTooltip(AtkTooltipManager* manager, AtkResNode* key)
    {
        // Bounded read-only tree search: library enumeration/find could traverse without an explicit cap.
        var tree = manager->TooltipMap.WithOps.Tree;
        if (key == null || tree.Head == null || tree.LongCount is <= 0 or > 8192) return null;
        var node = tree.Head->_Parent;
        for (var depth = 0; depth < 32 && node != null && !node->_Isnil; depth++)
        {
            var address = (nuint)node->_Myval.Item1.Value;
            if (address == (nuint)key) return node->_Myval.Item2.Value;
            node = (nuint)key < address ? node->_Left : node->_Right;
        }
        return null;
    }
}
