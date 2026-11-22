using System;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Bounds = FFXIVClientStructs.FFXIV.Common.Math.Bounds;

namespace CompAI;

/// <summary>Framework-thread read-only ordinary Shop bindings. No callbacks, scrolling or transactions.</summary>
internal sealed unsafe class ShopHighlightReader(IGameGui gui)
{
    internal ShopHighlightNativeIdentity? CaptureIdentity(ulong owner)
    {
        try { return Observe(owner, out var first) && Observe(owner, out var after) && first == after ? first : null; }
        catch (Exception) { return null; }
    }

    internal ShopHighlightNativeCapture? Capture(ulong owner, out string reason)
    {
        reason = "Open the same ordinary gil shop's Buy tab and let it settle.";
        try
        {
            if (!Observe(owner, out var before)) return null;
            var offers = CopyOffers((ShopEventHandler*)before.Handler);
            if (!Observe(owner, out var after) || before != after ||
                !offers.SequenceEqual(CopyOffers((ShopEventHandler*)after.Handler)) ||
                !Observe(owner, out var final) || before != final) return null;
            reason = string.Empty;
            return new(before, Array.AsReadOnly(offers));
        }
        catch (Exception)
        { reason = "The shop is changing or unavailable; no highlight can be bound."; return null; }
    }

    private bool Observe(ulong owner, out ShopHighlightNativeIdentity identity)
    {
        identity = default;
        var player = PlayerState.Instance();
        if (owner == 0 || player == null || !player->IsLoaded || player->ContentId != owner) return false;
        var addon = gui.GetAddonByName<AddonShop>("Shop", 1);
        if (addon == null || addon->Id == 0 || !addon->IsReady || !addon->IsVisible || addon->Alpha == 0 ||
            addon->RootNode == null || !Visible(addon->RootNode) || !ShopReader.GilBuyListVisible(addon)) return false;
        var proxy = ShopEventHandler.AgentProxy.Instance();
        if (proxy == null || proxy->AddonId != addon->Id || !ShopReader.GilReady(proxy->Handler)) return false;
        var handler = proxy->Handler;
        // Installed typed EventHandlerInfo supplies the shop event identity. Pointer equality alone is insufficient.
        if (handler->Info.EventId.Id == 0 || addon->BuyList->OwnerNode == null ||
            addon->BuyList->OwnerNode->Component != (AtkComponentBase*)addon->BuyList ||
            !OwnedBy((AtkResNode*)addon->BuyList->OwnerNode, addon->RootNode)) return false;
        identity = new(owner, (nint)player, (nint)addon, addon->Id, (nint)proxy, (nint)handler,
            (nint)addon->BuyList, handler->Info.EventId.Id, handler->EventParam, (nint)handler->EventGameObject);
        return true;
    }

    internal static ShopHighlightOffer[] CopyOffers(ShopEventHandler* handler)
    {
        var offers = ShopReader.CopyGilRows(handler);
        var result = new ShopHighlightOffer[offers.Length];
        for (var index = 0; index < result.Length; ++index)
        {
            var offer = offers[index];
            result[index] = new(index, handler->VisibleItems[index], offer.ItemId, offer.IsHq == true, offer.Quantity, offer.Cost0Amount);
        }
        return result;
    }

    internal ShopHighlightFrame CaptureFrame(ShopHighlightNativeCapture baseline, int row)
    {
        ShopRendererBindingResult? rendererResult = null;
        ShopHighlightFrame Empty(ShopHighlightBlockReason reason) => new(DateTimeOffset.UtcNow, null, null,
            ShopHighlightMessages.Blocked(reason, rendererResult?.Failure ?? ShopRendererBindingFailure.None), reason, rendererResult);
        if (row < 0 || row >= baseline.Offers.Count) return Empty(ShopHighlightBlockReason.RowUnavailable);
        // Controller just copied the complete offers on this framework tick. Recheck native
        // identity here, then compare another full offer copy after resolving the row bounds.
        if (CaptureIdentity(baseline.Identity.OwnerId) != baseline.Identity) return Empty(ShopHighlightBlockReason.ShopChanged);
        var addon = (AddonShop*)baseline.Identity.Addon;
        var list = addon->BuyList;
        var header = Header(list);
        if (!ValidHeader(header, baseline.Offers.Count) || header.UpdatePending)
            return Empty(ShopHighlightBlockReason.ListUnsettled);
        if (header.ScrollPending)
            return CaptureDimOnlyFrame(baseline, header, ShopHighlightBlockReason.ListUnsettled);
        if (!VisibleRow(header, row)) return CaptureDimOnlyFrame(baseline, header, ShopHighlightBlockReason.Offscreen);
        // The typed accessor returns the logical row's current renderer. Corroborate the result
        // with backing membership, a unique visible logical row, exact index and owned visible node.
        var renderer = list->GetItemRenderer(row);
        rendererResult = CheckRendererBinding(list, renderer, row);
        if (!rendererResult.Value.Success) return Empty(ShopHighlightBlockReason.Renderer);
        var rendererNode = renderer->OwnerNode;
        var rowBounds = ReadBounds((AtkResNode*)rendererNode);
        var listBounds = ReadBounds((AtkResNode*)list->OwnerNode);
        var windowBounds = ReadBounds(addon->RootNode);
        if (!rowBounds.Valid)
            return Empty(ShopHighlightBlockReason.Bounds);
        // A clipped edge row has no safe cutout yet; keep a freshly verified full-list mask.
        if (!Contains(listBounds, rowBounds) || !Contains(windowBounds, rowBounds))
            return CaptureDimOnlyFrame(baseline, header, ShopHighlightBlockReason.Offscreen);
        var corner = new Vector2(windowBounds.Max.X, windowBounds.Min.Y);
        var dimArea = ShopHighlightDimming.Valid(listBounds, rowBounds) && Contains(windowBounds, listBounds)
            ? (ShopHighlightRectangle?)listBounds : null;
        if (!OverlayClear(addon, rowBounds, corner, dimArea, out var dimClear)) return Empty(ShopHighlightBlockReason.FocusOrOverlap);
        var after = Capture(baseline.Identity.OwnerId, out _);
        if (!ShopHighlightState.Same(baseline, after) || Header(list) != header || list->GetItemRenderer(row) != renderer ||
            renderer->OwnerNode != rendererNode || CheckRendererBinding(list, renderer, row) != rendererResult.Value ||
            ReadBounds((AtkResNode*)rendererNode) != rowBounds || ReadBounds((AtkResNode*)list->OwnerNode) != listBounds ||
            ReadBounds(addon->RootNode) != windowBounds || !OverlayClear(addon, rowBounds, corner, dimArea, out var dimStillClear))
            return Empty(ShopHighlightBlockReason.LayoutChanged);
        return new(DateTimeOffset.UtcNow, rowBounds, corner, "Selected shop row outlined.", RendererResult: rendererResult,
            DimArea: dimClear && dimStillClear ? dimArea : null);
    }

    private ShopHighlightFrame CaptureDimOnlyFrame(ShopHighlightNativeCapture baseline, ListHeader header,
        ShopHighlightBlockReason reason)
    {
        ShopHighlightFrame Empty(ShopHighlightBlockReason blocked) =>
            new(DateTimeOffset.UtcNow, null, null, ShopHighlightMessages.Blocked(blocked), blocked);
        // No offscreen renderer is queried and no old row cutout is reused. The verified
        // shop/list viewport is sufficient for a dim-only frame, even while rows recycle.
        if (CaptureIdentity(baseline.Identity.OwnerId) != baseline.Identity)
            return Empty(ShopHighlightBlockReason.ShopChanged);
        var addon = (AddonShop*)baseline.Identity.Addon;
        var list = addon->BuyList;
        var listNode = list->OwnerNode;
        var windowNode = addon->RootNode;
        if (Header(list) != header || !ValidHeader(header, baseline.Offers.Count) || header.UpdatePending ||
            listNode == null || windowNode == null || !Visible((AtkResNode*)listNode))
            return Empty(ShopHighlightBlockReason.ListUnsettled);
        var area = ReadBounds((AtkResNode*)listNode);
        var window = ReadBounds(windowNode);
        if (!ShopHighlightDimming.ValidArea(area) || !Contains(window, area)) return Empty(ShopHighlightBlockReason.Bounds);
        var corner = new Vector2(window.Max.X, window.Min.Y);
        if (!OverlayClear(addon, area, corner, area, out var clear)) return Empty(ShopHighlightBlockReason.FocusOrOverlap);
        var after = Capture(baseline.Identity.OwnerId, out _);
        if (!ShopHighlightState.Same(baseline, after) || Header(list) != header ||
            list->OwnerNode != listNode || addon->RootNode != windowNode ||
            listNode->Component != (AtkComponentBase*)list || !Visible((AtkResNode*)listNode) ||
            ReadBounds((AtkResNode*)listNode) != area || ReadBounds(windowNode) != window ||
            !OverlayClear(addon, area, corner, area, out var stillClear)) return Empty(ShopHighlightBlockReason.LayoutChanged);
        return new(DateTimeOffset.UtcNow, null, corner, ShopHighlightMessages.Blocked(reason), reason,
            DimArea: clear && stillClear ? area : null);
    }

    internal readonly record struct ListHeader(int Length, int First, int Visible, int Allocated, nint Array,
        bool UpdatePending, bool ScrollPending);
    private static ListHeader Header(AtkComponentList* list) => new(list->ListLength, list->FirstVisibleItemIndex,
        list->NumVisibleItems, list->AllocatedItemRendererListLength, (nint)list->ItemRendererList,
        list->IsUpdatePending, list->IsScrollRefreshPending);
    internal static bool ValidHeader(ListHeader header, int offers) => offers is > 0 and <= 60 && header.Length == offers &&
        header.First >= 0 && header.First < offers && header.Visible is > 0 and <= 60 &&
        header.Allocated is > 0 and <= 128 && header.Array != 0;
    internal static bool VisibleRow(ListHeader header, int row) => row >= 0 && row < header.Length &&
        row >= header.First && row < header.First + header.Visible;

    internal static bool RendererBound(AtkComponentList* list, AtkComponentListItemRenderer* renderer, int row,
        Func<nint, ComponentType>? componentType = null, Func<int, nint>? getRenderer = null) =>
        CheckRendererBinding(list, renderer, row, componentType, getRenderer).Success;

    // GetItemRenderer and ListItemIndex describe logical rows. The separately allocated
    // backing array has no documented uniqueness guarantee: use it only for membership.
    // https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/FFXIV/Component/GUI/AtkComponentList.cs
    internal static ShopRendererBindingResult CheckRendererBinding(AtkComponentList* list,
        AtkComponentListItemRenderer* renderer, int row, Func<nint, ComponentType>? componentType = null,
        Func<int, nint>? getRenderer = null)
    {
        ShopRendererBindingResult Fail(ShopRendererBindingFailure failure, int? matches = null, int? index = null,
            int? type = null, int? visibleMatches = null) => new(failure, row, matches, index, type, visibleMatches);
        if (list == null) return Fail(ShopRendererBindingFailure.ListMissing);
        if (list->OwnerNode == null) return Fail(ShopRendererBindingFailure.ListOwnerMissing);
        var listNode = list->OwnerNode;
        if (listNode->Component != (AtkComponentBase*)list) return Fail(ShopRendererBindingFailure.ListOwnerMismatch);
        var header = Header(list);
        if (!ValidHeader(header, list->ListLength) || header.UpdatePending || header.ScrollPending)
            return Fail(ShopRendererBindingFailure.InvalidListHeader);
        if (row < 0 || row >= list->ListLength) return Fail(ShopRendererBindingFailure.RowOutOfRange);
        if (!VisibleRow(header, row)) return Fail(ShopRendererBindingFailure.RowNotVisible);
        if (renderer == null) return Fail(ShopRendererBindingFailure.GetterMissing);
        var matches = BackingMatches(list, renderer);
        if (matches == 0) return Fail(ShopRendererBindingFailure.MissingBacking, matches);
        var index = renderer->ListItemIndex;
        if (index != row) return Fail(ShopRendererBindingFailure.RowIndexMismatch, matches, index);
        if (renderer->OwnerNode == null) return Fail(ShopRendererBindingFailure.RendererOwnerMissing, matches, index);
        var rendererNode = renderer->OwnerNode;
        if (rendererNode->Component != (AtkComponentBase*)renderer)
            return Fail(ShopRendererBindingFailure.RendererOwnerMismatch, matches, index);
        if (!OwnedBy((AtkResNode*)renderer->OwnerNode, (AtkResNode*)list->OwnerNode))
            return Fail(ShopRendererBindingFailure.OutsideListAncestry, matches, index);
        if (!Visible((AtkResNode*)renderer->OwnerNode)) return Fail(ShopRendererBindingFailure.HiddenRenderer, matches, index);
        var type = componentType is null ? renderer->GetComponentType() : componentType((nint)renderer);
        if (type != ComponentType.ListItemRenderer)
            return Fail(ShopRendererBindingFailure.WrongComponentType, matches, index, (int)type);
        var visibleMatches = 0;
        var selectedMatches = false;
        var end = Math.Min(header.Length, header.First + header.Visible);
        for (var logicalRow = header.First; logicalRow < end; ++logicalRow)
        {
            // Compare other renderer addresses only; never dereference them. At most60
            // logical rows are queried, regardless of the allocated backing length.
            var candidate = getRenderer is null ? (nint)list->GetItemRenderer(logicalRow) : getRenderer(logicalRow);
            if (candidate != (nint)renderer) continue;
            ++visibleMatches;
            if (logicalRow == row) selectedMatches = true;
        }
        if (Header(list) != header || renderer->ListItemIndex != index ||
            list->OwnerNode != listNode || renderer->OwnerNode != rendererNode)
            return Fail(ShopRendererBindingFailure.RendererChanged, matches, index, (int)type, visibleMatches);
        var currentRenderer = getRenderer is null ? (nint)list->GetItemRenderer(row) : getRenderer(row);
        if (currentRenderer != (nint)renderer || Header(list) != header || renderer->ListItemIndex != index ||
            list->OwnerNode != listNode || renderer->OwnerNode != rendererNode ||
            listNode->Component != (AtkComponentBase*)list || rendererNode->Component != (AtkComponentBase*)renderer)
            return Fail(ShopRendererBindingFailure.RendererChanged, matches, index, (int)type, visibleMatches);
        if (visibleMatches > 1)
            return Fail(ShopRendererBindingFailure.DuplicateVisibleRenderer, matches, index, (int)type, visibleMatches);
        return Fail(selectedMatches && visibleMatches == 1 ? ShopRendererBindingFailure.None : ShopRendererBindingFailure.RendererChanged,
            matches, index, (int)type, visibleMatches);
    }

    private static int BackingMatches(AtkComponentList* list, AtkComponentListItemRenderer* renderer)
    {
        // Called only after the existing bounded list-header guard succeeds.
        var found = 0;
        for (var index = 0; index < list->AllocatedItemRendererListLength; ++index)
            if (list->ItemRendererList[index].AtkComponentListItemRenderer == renderer) ++found;
        return found;
    }

    private static bool OwnedBy(AtkResNode* node, AtkResNode* owner)
    {
        for (var depth = 0; node != null && depth < 32; ++depth, node = node->ParentNode)
            if (node == owner) return true;
        return false;
    }
    private static bool Visible(AtkResNode* node)
    {
        if (node == null) return false;
        for (var depth = 0; node != null && depth < 32; ++depth, node = node->ParentNode)
            if ((node->NodeFlags & NodeFlags.Visible) == 0 || node->IsDrawDisabled) return false;
        return node == null;
    }
    private static ShopHighlightRectangle ReadBounds(AtkResNode* node)
    {
        if (node == null) return default;
        var bounds = new Bounds();
        node->GetBounds(&bounds);
        return new(new(bounds.Pos1.X, bounds.Pos1.Y), new(bounds.Pos2.X, bounds.Pos2.Y));
    }
    private static bool Finite(ShopHighlightRectangle bounds) => float.IsFinite(bounds.Min.X) && float.IsFinite(bounds.Min.Y) &&
        float.IsFinite(bounds.Max.X) && float.IsFinite(bounds.Max.Y) && bounds.Max.X > bounds.Min.X && bounds.Max.Y > bounds.Min.Y;
    private static bool Contains(ShopHighlightRectangle outer, ShopHighlightRectangle inner) => Finite(outer) &&
        inner.Min.X >= outer.Min.X && inner.Min.Y >= outer.Min.Y && inner.Max.X <= outer.Max.X && inner.Max.Y <= outer.Max.Y;
    private static bool Overlaps(ShopHighlightRectangle a, ShopHighlightRectangle b) =>
        a.Min.X < b.Max.X && a.Max.X > b.Min.X && a.Min.Y < b.Max.Y && a.Max.Y > b.Min.Y;

    private bool OverlayClear(AddonShop* shop, ShopHighlightRectangle row, Vector2 corner,
        ShopHighlightRectangle? dimArea, out bool dimClear)
    {
        dimClear = dimArea is not null;
        var stage = AtkStage.Instance();
        if (stage == null || stage->RaptureAtkUnitManager == null) return false;
        var manager = &stage->RaptureAtkUnitManager->AtkUnitManager;
        if (manager->FocusedAddon != (AtkUnitBase*)shop) return false;
        var list = &manager->AllLoadedUnitsList;
        var count = list->Count;
        if (count is 0 or > 256 || list->Entries.Length != 256) return false;
        var tooltip = gui.GetAddonByName<AtkUnitBase>("Tooltip", 1);
        var item = gui.GetAddonByName<AtkUnitBase>("ItemDetail", 1);
        // Tooltip addons may not have WindowNode. Protect their RootNode footprint
        // without pausing the existing outline when a tooltip opens.
        if (dimClear && (!ClearForDimming(tooltip, dimArea!.Value) || !ClearForDimming(item, dimArea.Value))) dimClear = false;
        var dismiss = new ShopHighlightRectangle(corner + new Vector2(-240, 0), corner + new Vector2(0, 80));
        for (var index = 0; index < count; ++index)
        {
            var other = list->Entries[index].Value;
            if (other == null || other == (AtkUnitBase*)shop || other == tooltip || other == item ||
                !other->IsVisible || other->Alpha == 0) continue;
            if (other->WindowNode == null || !Visible((AtkResNode*)other->WindowNode)) continue;
            if (!other->IsReady) return false;
            var bounds = ReadBounds((AtkResNode*)other->WindowNode);
            if (!Finite(bounds) || Overlaps(bounds, row) || Overlaps(bounds, dismiss)) return false;
            if (dimClear && Overlaps(bounds, dimArea!.Value)) dimClear = false;
        }
        return list->Count == count && manager->FocusedAddon == (AtkUnitBase*)shop;
    }

    private static bool ClearForDimming(AtkUnitBase* other, ShopHighlightRectangle area)
    {
        if (other == null || !other->IsVisible || other->Alpha == 0) return true;
        if (!other->IsReady || other->RootNode == null) return false;
        var bounds = ReadBounds(other->RootNode);
        return Finite(bounds) && !Overlaps(bounds, area);
    }
}
