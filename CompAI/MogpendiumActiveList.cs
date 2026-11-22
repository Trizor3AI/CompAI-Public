using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace CompAI;

/// <summary>Complete, bounded active Weekly/Minimog tree labels; no progress or claim fields.</summary>
internal static unsafe class MogpendiumActiveList
{
    internal const int MaxWeeks = 16;
    internal const int MaxTreeItems = 64;
    private const int MaxNodes = 512;
    private const int MaxTextNodes = 64;

    // FFXIVClientStructs dd57986b: AtkComponentTreeList.Items and its item.Renderer
    // provide an explicit association. A renderer index or recycled visible text alone
    // does not identify an active item. No UIntValues/StringValues/AtkValues are read.
    internal static bool TryCopy(AtkUnitBase* addon, int choicesPerWeek, out MogpendiumObservedList? result,
        Func<nint, ComponentType>? componentType = null)
    {
        result = null;
        if (addon == null || choicesPerWeek is not (1 or 2) ||
            !Copy(addon, choicesPerWeek, componentType, out var first) ||
            !Copy(addon, choicesPerWeek, componentType, out var second) || !first!.Same(second!)) return false;
        result = first.Result;
        return true;
    }

    internal static bool Same(MogpendiumObservedList? left, MogpendiumObservedList? right) =>
        left is null ? right is null : right is not null && left.Choices.SequenceEqual(right.Choices);

    private static ComponentType TypeOf(AtkComponentBase* component, Func<nint, ComponentType>? resolver) =>
        resolver is null ? component->GetComponentType() : resolver((nint)component);

    private static bool Copy(AtkUnitBase* addon, int choicesPerWeek, Func<nint, ComponentType>? resolver, out CopyState? copy)
    {
        copy = null;
        if (!ReadNodes(&addon->UldManager, MaxNodes, out var rootNodes)) return false;
        AtkComponentNode* treeNode = null;
        foreach (var address in rootNodes.Nodes)
        {
            var node = (AtkResNode*)address;
            if (node == null || node->NodeId != 79) continue;
            if (treeNode != null || (ushort)node->Type < 1000 || !Visible(node)) return false;
            treeNode = (AtkComponentNode*)node;
        }
        if (treeNode == null || !ReadHeader(treeNode, choicesPerWeek, resolver, out var header)) return false;
        var tree = (AtkComponentTreeList*)treeNode->Component;
        var slots = tree->Items.First;
        if ((nuint)slots != header.First || !ReadNodes(&tree->UldManager, MaxNodes, out var treeNodes)) return false;
        var items = new ItemState[header.Count];
        var rendererPointers = new HashSet<nint>();
        var itemPointers = new HashSet<nint>();
        for (var slot = 0; slot < header.Count; ++slot)
        {
            var item = slots[slot].Value;
            if (!ReadItem(item, slot, choicesPerWeek, out items[slot]) ||
                !itemPointers.Add((nint)item) || !rendererPointers.Add(items[slot].Renderer) || slots[slot].Value != item) return false;
        }

        var candidates = new Dictionary<nint, nint>();
        foreach (var address in treeNodes.Nodes)
        {
            var node = (AtkResNode*)address;
            if (node == null || (ushort)node->Type < 1000 || !Visible(node)) continue;
            var candidate = (AtkComponentNode*)node;
            var component = candidate->Component;
            // Even an arbitrary item.Renderer is only compared with reached components.
            // Unrelated stale renderers are not followed or charged against active rows.
            if (component == null || !rendererPointers.Contains((nint)component)) continue;
            if (!Address((nuint)component) || component->OwnerNode != candidate || TypeOf(component, resolver) != ComponentType.ListItemRenderer) return false;
            if (candidates.TryGetValue((nint)component, out var existing))
            { if (existing != (nint)candidate) return false; }
            else candidates.Add((nint)component, (nint)candidate);
        }
        if (candidates.Count != items.Length) return false;

        var renderers = new RendererState[items.Length];
        var texts = new List<TextState>();
        var choices = new List<MogpendiumObservedChoice>();
        var labels = new HashSet<string>(StringComparer.Ordinal);
        string? week = null;
        for (var slot = 0; slot < items.Length; ++slot)
        {
            var candidate = (AtkComponentNode*)candidates[items[slot].Renderer];
            if (!ReadRenderer(candidate, items[slot].Renderer, resolver, out renderers[slot])) return false;
            if (slot == 0) continue; // Status section is deliberately not interpreted or exported.
            var offset = (slot - 1) % (choicesPerWeek + 1);
            if (!ReadLabel(candidate, offset == 0 ? 7u : 4u, out var text)) return false;
            texts.Add(text!);
            if (offset == 0)
            {
                week = text!.Value;
                if (!labels.Add(week)) return false;
            }
            else
            {
                if (week == null) return false;
                choices.Add(new(week, text!.Value, offset));
            }
        }
        if (choices.Count != header.Length) return false;

        // Recheck identities before following an observed item again. Changed targets
        // are rejected, not treated as permission to inspect a replacement pointer.
        if (treeNode->Component != (AtkComponentBase*)header.Component ||
            !ReadHeader(treeNode, choicesPerWeek, resolver, out var after) || header != after ||
            !StillNodes(&addon->UldManager, rootNodes) || !StillNodes(&tree->UldManager, treeNodes)) return false;
        for (var slot = 0; slot < items.Length; ++slot)
        {
            var item = slots[slot].Value;
            if ((nint)item != items[slot].Item || !ReadItem(item, slot, choicesPerWeek, out var again) || again != items[slot] ||
                slots[slot].Value != item ||
                !ReadRenderer((AtkComponentNode*)renderers[slot].Node, items[slot].Renderer, resolver, out var renderer) || renderer != renderers[slot]) return false;
        }
        foreach (var text in texts)
        {
            var candidate = (AtkComponentNode*)text.RendererNode;
            if (candidate->Component != (AtkComponentBase*)text.RendererComponent || !ReadLabel(candidate, text.TextId, out var repeated) || !text.Same(repeated!)) return false;
        }
        if (treeNode->Component != (AtkComponentBase*)header.Component ||
            !ReadHeader(treeNode, choicesPerWeek, resolver, out after) || after != header ||
            !StillNodes(&addon->UldManager, rootNodes) || !StillNodes(&tree->UldManager, treeNodes)) return false;
        copy = new(header, rootNodes, treeNodes, items, renderers, texts.ToArray(), new(choices.ToArray()));
        return true;
    }

    private static bool ReadHeader(AtkComponentNode* node, int choicesPerWeek, Func<nint, ComponentType>? resolver, out Header value)
    {
        value = default;
        if (node == null || node->NodeId != 79 || (ushort)node->Type < 1000 || !Visible((AtkResNode*)node) ||
            !Address((nuint)node->Component) || node->Component->OwnerNode != node || TypeOf(node->Component, resolver) != ComponentType.TreeList) return false;
        var tree = (AtkComponentTreeList*)node->Component;
        var first = (nuint)tree->Items.First; var last = (nuint)tree->Items.Last; var end = (nuint)tree->Items.End;
        var size = (nuint)IntPtr.Size;
        if (first < 65536 || first > last || last > end || first % size != 0 || (last - first) % size != 0 || (end - first) % size != 0 ||
            (last - first) / size > MaxTreeItems) return false;
        var count = (int)((last - first) / size);
        if (count < choicesPerWeek + 2 || (count - 1) % (choicesPerWeek + 1) != 0) return false;
        var weeks = (count - 1) / (choicesPerWeek + 1);
        if (weeks > MaxWeeks || tree->ListLength != weeks * choicesPerWeek || tree->ItemCount != count || tree->VisibleItemCount != count ||
            tree->IsUpdatePending || tree->IsScrollRefreshPending || tree->LayoutRefreshPending ||
            tree->SelectedItemIndex is < -1 or > MaxNodes || tree->FirstVisibleItemIndex is < -1 or > MaxNodes ||
            tree->NumVisibleItems is < 0 or > MaxNodes || tree->AllocatedItemRendererListLength is < 0 or > MaxNodes ||
            tree->AllocatedItemRendererListLength > 0 && tree->ItemRendererList == null) return false;
        value = new((nint)node, (nint)node->Component, first, last, end, count, tree->ListLength,
            tree->SelectedItemIndex, tree->FirstVisibleItemIndex, tree->NumVisibleItems, tree->AllocatedItemRendererListLength, (nint)tree->ItemRendererList);
        return true;
    }

    private static bool ReadItem(AtkComponentTreeListItem* item, int slot, int choicesPerWeek, out ItemState value)
    {
        value = default;
        if (!Address((nuint)item) || item->Renderer == null || item->IsHidden || item->Depth != 0) return false;
        var header = slot == 0 || (slot - 1) % (choicesPerWeek + 1) == 0;
        if ((byte)item->Type != (header ? 4 : 0)) return false;
        value = new((nint)item, (nint)item->Renderer, (byte)item->Type, item->Depth, (byte)item->State, item->IsHidden);
        return true;
    }

    private static bool ReadRenderer(AtkComponentNode* node, nint expectedComponent, Func<nint, ComponentType>? resolver, out RendererState value)
    {
        value = default;
        if (node == null || (ushort)node->Type < 1000 || !Visible((AtkResNode*)node) || !Address((nuint)node->Component) ||
            (nint)node->Component != expectedComponent || node->Component->OwnerNode != node ||
            TypeOf(node->Component, resolver) != ComponentType.ListItemRenderer) return false;
        var index = ((AtkComponentListItemRenderer*)node->Component)->ListItemIndex;
        if (index is < -1 or >= MaxNodes) return false;
        value = new((nint)node, node->NodeId, expectedComponent, index);
        return true;
    }

    private static bool ReadLabel(AtkComponentNode* renderer, uint textId, out TextState? result)
    {
        result = null;
        if (renderer->Component == null || renderer->Component->OwnerNode != renderer ||
            !ReadNodes(&renderer->Component->UldManager, MaxTextNodes, out var nodes)) return false;
        AtkTextNode* text = null;
        foreach (var address in nodes.Nodes)
        {
            var node = (AtkResNode*)address;
            if (node == null || node->NodeId != textId) continue;
            if (text != null || node->Type != NodeType.Text || !Visible(node)) return false;
            text = (AtkTextNode*)node;
        }
        if (text == null) return false;
        var pointer = (nint)(byte*)text->NodeText.StringPtr; var used = text->NodeText.BufUsed; var capacity = text->NodeText.BufSize;
        var parent = (nint)text->ParentNode;
        if (!MogpendiumReader.ReadText((nint)text, renderer->NodeId, 128, out var label) || label == null ||
            (nint)(byte*)text->NodeText.StringPtr != pointer || text->NodeText.BufUsed != used || text->NodeText.BufSize != capacity ||
            (nint)text->ParentNode != parent || !StillNodes(&renderer->Component->UldManager, nodes)) return false;
        result = new((nint)renderer, (nint)renderer->Component, textId, (nint)text, parent, pointer, used, capacity, label, nodes);
        return true;
    }

    private static bool ReadNodes(AtkUldManager* manager, int maximum, out NodesState result)
    {
        result = new(0, []);
        if (manager == null || manager->NodeListCount > maximum || manager->NodeListCount > 0 && !Address((nuint)manager->NodeList)) return false;
        var count = manager->NodeListCount; var list = manager->NodeList;
        var nodes = new nint[count];
        for (var i = 0; i < count; ++i)
        {
            if (list[i] != null && !Address((nuint)list[i])) return false;
            nodes[i] = (nint)list[i];
        }
        result = new((nint)list, nodes);
        return StillNodes(manager, result);
    }

    private static bool StillNodes(AtkUldManager* manager, NodesState value)
    {
        if ((nint)manager->NodeList != value.List || manager->NodeListCount != value.Nodes.Length) return false;
        for (var i = 0; i < value.Nodes.Length; ++i) if ((nint)manager->NodeList[i] != value.Nodes[i]) return false;
        return true;
    }

    private static bool Visible(AtkResNode* node)
    {
        for (var depth = 0; node != null && depth < 32; ++depth, node = node->ParentNode)
            if (!Address((nuint)node) || (node->NodeFlags & NodeFlags.Visible) == 0 || node->Color.A == 0) return false;
        return node == null;
    }

    private static bool Address(nuint value) => value >= 65536 && value % (nuint)IntPtr.Size == 0;

    // Private native identity records are only used for stable-copy checks; never exported.
    private readonly record struct Header(nint Node, nint Component, nuint First, nuint Last, nuint End, int Count, int Length,
        int Selected, int FirstVisible, int Visible, int Allocated, nint RendererArray);
    private readonly record struct ItemState(nint Item, nint Renderer, byte Type, byte Depth, byte State, bool Hidden);
    private readonly record struct RendererState(nint Node, uint Id, nint Component, int Index);
    private sealed record NodesState(nint List, nint[] Nodes)
    { internal bool Same(NodesState other) => List == other.List && Nodes.SequenceEqual(other.Nodes); }
    private sealed record TextState(nint RendererNode, nint RendererComponent, uint TextId, nint Node, nint Parent,
        nint Buffer, long Used, long Capacity, string Value, NodesState Nodes)
    {
        internal bool Same(TextState other) => RendererNode == other.RendererNode && RendererComponent == other.RendererComponent &&
            TextId == other.TextId && Node == other.Node && Parent == other.Parent && Buffer == other.Buffer && Used == other.Used &&
            Capacity == other.Capacity && Value == other.Value && Nodes.Same(other.Nodes);
    }
    private sealed record CopyState(Header Header, NodesState Root, NodesState Tree, ItemState[] Items,
        RendererState[] Renderers, TextState[] Texts, MogpendiumObservedList Result)
    {
        internal bool Same(CopyState other) => Header == other.Header && Root.Same(other.Root) && Tree.Same(other.Tree) &&
            Items.SequenceEqual(other.Items) && Renderers.SequenceEqual(other.Renderers) && Texts.Length == other.Texts.Length &&
            Texts.Zip(other.Texts).All(pair => pair.First.Same(pair.Second)) && MogpendiumActiveList.Same(Result, other.Result);
    }
}

internal sealed record MogpendiumObservedList(MogpendiumObservedChoice[] Choices);
internal sealed record MogpendiumObservedChoice(string WeekLabel, string Label, int Slot);
