using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace CompAI;

/// <summary>Manual clipboard structure only; never an activity reader or assignment source.</summary>
internal sealed unsafe class MogpendiumListDiagnostic(Func<nint, ComponentType>? componentType = null)
{
    internal const int MaxLists = 8;
    internal const int MaxRenderers = 32;
    internal const int MaxItems = 512;
    internal const int MaxTreeItems = 64;
    private readonly List<Observation> observations = [];
    private readonly HashSet<string> limits = [];
    private int listCount;
    private int rendererCount;
    private int treeItemsRead;

    // Pinned FFXIVClientStructs dd57986b8287e7c6ed9c27198f8c76cb04a81dfe:
    // Component/GUI/{AtkComponentBase,AtkComponentList,AtkComponentTreeList,
    // AtkComponentListItemRenderer,AtkUldManager}.cs. GetComponentType is a native
    // read accessor. NodeType >= 1000 does not authorize a derived component cast.
    private ComponentType TypeOf(AtkComponentBase* value) => componentType is null
        ? value->GetComponentType() : componentType((nint)value);

    internal bool Observe(AtkComponentNode* node, uint[] path, out string? failure)
    {
        failure = null;
        if (node == null || node->Component == null || path.Length is < 1 or > 8) { failure = "list component"; return false; }
        var type = TypeOf(node->Component);
        if (type is not (ComponentType.List or ComponentType.TreeList or ComponentType.ListItemRenderer)) return true;
        // Collect within the walker's node budget. Recycled renderers cannot consume
        // the smaller output budget before typed backing membership is established.
        if (type == ComponentType.ListItemRenderer && rendererCount >= MaxItems)
        { limits.Add("renderer_candidate_limit"); return true; }
        if (type != ComponentType.ListItemRenderer && listCount >= MaxLists)
        { limits.Add("list_component_limit"); return true; }
        if (!Read(node, type, out var value, out failure)) return false;
        TreeItem[] items = [];
        if (type == ComponentType.TreeList)
        {
            var count = Math.Min(value.VectorCount!.Value, MaxTreeItems - treeItemsRead);
            if (count < value.VectorCount) limits.Add("tree_item_limit");
            items = new TreeItem[count];
            var tree = (AtkComponentTreeList*)node->Component;
            for (var slot = 0; slot < count; ++slot)
            {
                var item = tree->Items.First[slot].Value;
                if (!ReadItem(item, slot, out items[slot], out failure)) return false;
                if (tree->Items.First[slot].Value != item) { failure = "changed tree item"; return false; }
            }
            treeItemsRead += count;
            if (!Read(node, type, out var repeated, out failure) || repeated != value)
            { failure = "changed tree list header"; return false; }
        }
        observations.Add(new(path, value, items));
        if (type == ComponentType.ListItemRenderer) ++rendererCount; else ++listCount;
        return true;
    }

    internal bool Finish(out MogpendiumListCapture capture, out string? failure)
    {
        capture = new([], [], []); failure = null;
        foreach (var observed in observations)
        {
            var node = (AtkComponentNode*)observed.State.Node;
            if (node->Component != (AtkComponentBase*)observed.State.Component || TypeOf(node->Component) != observed.State.Type ||
                !Read(node, observed.State.Type, out var repeated, out failure) || repeated != observed.State)
            { failure = "changed list structure"; return false; }
            if (observed.State.Type != ComponentType.TreeList) continue;
            var tree = (AtkComponentTreeList*)node->Component;
            foreach (var item in observed.Items)
            {
                // Compare slot identity before dereferencing its target. Renderer is
                // copied for equality only, never followed into unknown native memory.
                var current = tree->Items.First[item.Slot].Value;
                if ((nint)current != item.Item || !ReadItem(current, item.Slot, out var repeatedItem, out failure) || repeatedItem != item ||
                    tree->Items.First[item.Slot].Value != current)
                { failure = "changed tree item"; return false; }
            }
            if (!Read(node, observed.State.Type, out repeated, out failure) || repeated != observed.State)
            { failure = "changed tree list header"; return false; }
        }
        var listObservations = observations.Where(row => row.State.Type != ComponentType.ListItemRenderer).ToArray();
        var candidates = observations.Where(row => row.State.Type == ComponentType.ListItemRenderer).ToArray();
        var references = listObservations.SelectMany(row => row.Items).Where(item => item.Renderer != 0)
            .GroupBy(item => item.Renderer).ToDictionary(group => group.Key, group => group.Count());
        var lists = new List<MogpendiumDiagnosticList>();
        var renderers = new List<MogpendiumDiagnosticRenderer>();
        foreach (var observed in listObservations)
        {
            var value = observed.State;
            var matched = 0; var unmatched = 0; var empty = 0; var ambiguous = 0;
            foreach (var item in observed.Items)
            {
                if (item.Item == 0) { ++empty; continue; }
                var matches = candidates.Where(candidate => candidate.State.Component == item.Renderer &&
                    NearestList(candidate, listObservations) == observed).ToArray();
                if (item.Renderer != 0 && references[item.Renderer] != 1 || matches.Length > 1) { ++ambiguous; continue; }
                if (matches.Length == 0) { ++unmatched; continue; }
                ++matched;
                renderers.Add(Renderer(matches[0], observed, item));
            }
            var isTree = value.Type == ComponentType.TreeList;
            lists.Add(new(observed.Path, isTree ? "tree_list" : "list", value.Length, value.Selected, value.FirstVisible,
                value.Visible, value.Allocated, value.UpdatePending, value.ScrollPending, value.TreeItems, value.TreeVisible,
                value.VectorCount, value.LayoutPending, isTree ? observed.Items.Length : null, isTree ? matched : null,
                isTree ? unmatched : null, isTree ? empty : null, isTree ? ambiguous : null));
        }
        // Ordinary lists retain their previous index diagnostic. Unreferenced tree
        // renderer candidates are deliberately omitted, including same-index stale rows.
        foreach (var candidate in candidates)
        {
            var ancestor = NearestList(candidate, listObservations);
            if (ancestor?.State.Type == ComponentType.List) renderers.Add(Renderer(candidate, ancestor, null));
        }
        if (renderers.Count > MaxRenderers) limits.Add("renderer_association_limit");
        capture = new(lists.ToArray(), renderers.Take(MaxRenderers).ToArray(), limits.Order(StringComparer.Ordinal).ToArray());
        return true;
    }

    private static Observation? NearestList(Observation renderer, Observation[] lists) => lists
        .Where(list => list.Path.Length < renderer.Path.Length && renderer.Path.Take(list.Path.Length).SequenceEqual(list.Path))
        .OrderByDescending(list => list.Path.Length).FirstOrDefault();

    private static MogpendiumDiagnosticRenderer Renderer(Observation row, Observation ancestor, TreeItem? item)
    {
        int? bounded = row.State.RendererIndex is >= -1 and < MaxItems ? row.State.RendererIndex : null;
        return new(row.Path, bounded, bounded is null ? "outside_bounds" : bounded == -1 ? "unused" : "observed", ancestor.Path,
            bounded is null || ancestor.State.Type == ComponentType.TreeList ? null : bounded >= 0 && bounded < ancestor.State.Length,
            item?.Slot, item?.Type, item?.Depth, item?.Hidden);
    }

    private static bool ReadItem(AtkComponentTreeListItem* item, int slot, out TreeItem value, out string? failure)
    {
        value = new() { Slot = slot, Item = (nint)item }; failure = null;
        if (item == null) return true;
        if ((nuint)item < 65536 || (nuint)item % (nuint)IntPtr.Size != 0)
        { failure = "tree item pointer bounds"; return false; }
        // Source-backed scalar fields only. UIntValues, StringValues and Unk38 remain
        // unread. Renderer is compared against already-reached typed components only.
        value = value with { Renderer = (nint)item->Renderer, Type = (byte)item->Type, Depth = item->Depth,
            Hidden = item->IsHidden, State = (byte)item->State };
        return true;
    }

    private static bool Read(AtkComponentNode* node, ComponentType type, out Native value, out string? failure)
    {
        value = default; failure = null;
        var component = node->Component;
        if (component == null || component->OwnerNode != node) { failure = "list owner node"; return false; }
        value = new() { Node = (nint)node, NodeId = node->NodeId, Component = (nint)component, Type = type };
        if (type == ComponentType.ListItemRenderer)
        { value = value with { RendererIndex = ((AtkComponentListItemRenderer*)component)->ListItemIndex }; return true; }
        if (type is not (ComponentType.List or ComponentType.TreeList)) { failure = "list component type"; return false; }
        var list = (AtkComponentList*)component;
        if (list->ListLength is < 0 or > MaxItems || list->AllocatedItemRendererListLength is < 0 or > MaxItems ||
            list->NumVisibleItems is < 0 or > MaxItems || list->FirstVisibleItemIndex is < -1 or > MaxItems ||
            list->SelectedItemIndex is < -1 or > MaxItems ||
            type == ComponentType.List && list->SelectedItemIndex >= list->ListLength && list->SelectedItemIndex != -1 ||
            list->AllocatedItemRendererListLength > 0 && list->ItemRendererList == null)
        { failure = "list count or selection bounds"; return false; }
        value = value with { Length = list->ListLength, Selected = list->SelectedItemIndex, FirstVisible = list->FirstVisibleItemIndex,
            Visible = list->NumVisibleItems, Allocated = list->AllocatedItemRendererListLength,
            RendererArray = (nint)list->ItemRendererList, UpdatePending = list->IsUpdatePending, ScrollPending = list->IsScrollRefreshPending };
        if (type == ComponentType.List) return true;
        var tree = (AtkComponentTreeList*)component;
        // Vector count is independent of base ListLength. Validate before the bounded
        // item traversal; ItemRendererList and nested value arrays are never followed.
        var first = (nuint)tree->Items.First; var last = (nuint)tree->Items.Last; var end = (nuint)tree->Items.End;
        if (!VectorCount(first, last, end, out var count) || tree->ItemCount > MaxItems || tree->VisibleItemCount > MaxItems)
        { failure = "tree list vector or count bounds"; return false; }
        value = value with { First = first, Last = last, End = end, VectorCount = count,
            TreeItems = tree->ItemCount, TreeVisible = tree->VisibleItemCount, LayoutPending = tree->LayoutRefreshPending };
        return true;
    }

    internal static bool VectorCount(nuint first, nuint last, nuint end, out int count)
    {
        count = 0;
        if (first == 0) return last == 0 && end == 0;
        var size = (nuint)IntPtr.Size;
        if (first < 65536 || first > last || last > end || first % size != 0 || (last - first) % size != 0 || (end - first) % size != 0 ||
            (last - first) / size > MaxItems) return false;
        count = (int)((last - first) / size);
        return true;
    }

    internal static bool Valid(MogpendiumListCapture value) => value is not null && value.lists is not null && value.renderers is not null &&
        value.limits is not null && value.lists.Length <= MaxLists && value.renderers.Length <= MaxRenderers &&
        value.limits.Length <= 4 && value.limits.Distinct().Count() == value.limits.Length &&
        value.limits.All(reason => reason is "list_component_limit" or "renderer_association_limit" or "renderer_candidate_limit" or "tree_item_limit") &&
        value.lists.All(row => row is not null && Path(row.component_path) && row.component_type is "list" or "tree_list" &&
            row.list_length is >= 0 and <= MaxItems && row.selected_index is >= -1 and <= MaxItems &&
            (row.component_type == "tree_list" || row.selected_index == -1 || row.selected_index < row.list_length) &&
            row.first_visible_index is >= -1 and <= MaxItems && row.visible_item_count is >= 0 and <= MaxItems && row.allocated_renderer_count is >= 0 and <= MaxItems &&
            (row.component_type == "list" ? row.tree_item_count is null && row.tree_visible_item_count is null && row.tree_vector_count is null && row.layout_refresh_pending is null &&
                row.tree_items_examined is null && row.tree_items_matched is null && row.tree_items_unmatched is null && row.tree_items_null is null && row.tree_items_ambiguous is null
                : row.tree_item_count is <= MaxItems && row.tree_visible_item_count is <= MaxItems && row.tree_vector_count is >= 0 and <= MaxItems && row.layout_refresh_pending is not null &&
                row.tree_items_examined is >= 0 and <= MaxTreeItems && row.tree_items_examined <= row.tree_vector_count &&
                row.tree_items_matched is >= 0 and <= MaxTreeItems && row.tree_items_unmatched is >= 0 and <= MaxTreeItems &&
                row.tree_items_null is >= 0 and <= MaxTreeItems && row.tree_items_ambiguous is >= 0 and <= MaxTreeItems &&
                row.tree_items_examined == row.tree_items_matched + row.tree_items_unmatched + row.tree_items_null + row.tree_items_ambiguous)) &&
        value.lists.Sum(row => row.tree_items_examined ?? 0) <= MaxTreeItems &&
        value.renderers.All(row => row is not null && Path(row.component_path) && (row.nearest_list_path is null || Path(row.nearest_list_path)) &&
            (row.index_state switch { "outside_bounds" => row.logical_index is null && row.index_in_list is null,
                "unused" => row.logical_index == -1, "observed" => row.logical_index is >= 0 and < MaxItems, _ => false }) &&
            (row.tree_vector_slot is null ? row.tree_item_type is null && row.tree_item_depth is null && row.tree_item_hidden is null
                : row.tree_vector_slot is >= 0 and < MaxTreeItems && row.tree_item_type is not null && row.tree_item_depth is not null &&
                row.tree_item_hidden is not null && row.index_in_list is null && row.nearest_list_path is not null && value.lists.Any(list =>
                    list.component_type == "tree_list" && list.component_path.SequenceEqual(row.nearest_list_path) && row.tree_vector_slot < list.tree_vector_count)));
    private static bool Path(uint[]? path) => path is { Length: >= 1 and <= 8 };
    internal static bool Same(MogpendiumListCapture? left, MogpendiumListCapture? right) => left is null ? right is null :
        right is not null && JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
    internal static object Payload(MogpendiumListCapture value, int outputRenderersOmitted = 0) => new
    {
        schema = "mogpendium_list_structure_v2",
        coverage = "Visible widget structure only. Tree renderers are matched by backing-item pointer identity; unrelated recycled widgets are omitted. Selection range flags do not establish an index domain. No assignments, weeks, content IDs, completion or claims are inferred.",
        output_renderers_omitted = outputRenderersOmitted,
        value.lists, value.renderers,
    };

    private sealed record Observation(uint[] Path, Native State, TreeItem[] Items);
    private readonly record struct TreeItem
    {
        internal int Slot { get; init; } internal nint Item { get; init; } internal nint Renderer { get; init; }
        internal byte Type { get; init; } internal byte Depth { get; init; } internal byte State { get; init; } internal bool Hidden { get; init; }
    }
    // Native-only stability record. Never included in the detached capture or serializer.
    private readonly record struct Native
    {
        internal nint Node { get; init; } internal uint NodeId { get; init; } internal nint Component { get; init; }
        internal ComponentType Type { get; init; } internal int Length { get; init; } internal int Selected { get; init; }
        internal int FirstVisible { get; init; } internal int Visible { get; init; } internal int Allocated { get; init; }
        internal nint RendererArray { get; init; } internal bool UpdatePending { get; init; } internal bool ScrollPending { get; init; }
        internal uint? TreeItems { get; init; } internal uint? TreeVisible { get; init; } internal int? VectorCount { get; init; }
        internal bool? LayoutPending { get; init; } internal nuint First { get; init; } internal nuint Last { get; init; } internal nuint End { get; init; }
        internal int RendererIndex { get; init; }
    }
}

internal sealed record MogpendiumListCapture(MogpendiumDiagnosticList[] lists, MogpendiumDiagnosticRenderer[] renderers, string[] limits);
internal sealed record MogpendiumDiagnosticList(uint[] component_path, string component_type, int list_length, int selected_index,
    int first_visible_index, int visible_item_count, int allocated_renderer_count, bool update_pending, bool scroll_refresh_pending,
    uint? tree_item_count, uint? tree_visible_item_count, int? tree_vector_count, bool? layout_refresh_pending,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? tree_items_examined = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? tree_items_matched = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? tree_items_unmatched = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? tree_items_null = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? tree_items_ambiguous = null)
{
    // Descriptive range comparisons only; neither proves the native selected-index domain.
    public bool selected_in_base_list => selected_index >= 0 && selected_index < list_length;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? selected_in_tree_vector => component_type == "tree_list" ? selected_index >= 0 && selected_index < tree_vector_count : null;
}
internal sealed record MogpendiumDiagnosticRenderer(uint[] component_path, int? logical_index, string index_state,
    uint[]? nearest_list_path, bool? index_in_list,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? tree_vector_slot = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] byte? tree_item_type = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] byte? tree_item_depth = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? tree_item_hidden = null);
