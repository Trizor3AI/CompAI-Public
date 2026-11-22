using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CompAI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

internal static unsafe class MogpendiumListChecks
{
    internal static void Run()
    {
        using (var fixture = new Fixture())
        {
            var treeNode = fixture.Component(79, ComponentType.TreeList);
            var tree = (AtkComponentTreeList*)treeNode->Component;
            tree->ListLength = 6; tree->SelectedItemIndex = 12; tree->NumVisibleItems = 0;
            tree->AllocatedItemRendererListLength = 9; tree->ItemRendererList = (AtkComponentList.ListItem*)1;
            tree->ItemCount = 13; tree->VisibleItemCount = 13;
            var selected = fixture.Component(40, ComponentType.ListItemRenderer);
            ((AtkComponentListItemRenderer*)selected->Component)->ListItemIndex = 2;
            var recycled = fixture.Component(41, ComponentType.ListItemRenderer);
            ((AtkComponentListItemRenderer*)recycled->Component)->ListItemIndex = 12;
            var unused = fixture.Component(42, ComponentType.ListItemRenderer);
            ((AtkComponentListItemRenderer*)unused->Component)->ListItemIndex = -1;
            var last = fixture.Component(43, ComponentType.ListItemRenderer);
            ((AtkComponentListItemRenderer*)last->Component)->ListItemIndex = 12;
            var header = fixture.Component(44, ComponentType.ListItemRenderer);
            ((AtkComponentListItemRenderer*)header->Component)->ListItemIndex = 11;
            var backing = new nint[13];
            backing[2] = (nint)selected->Component; backing[11] = (nint)header->Component; backing[12] = (nint)last->Component;
            fixture.TreeItems(tree, backing);
            // Many recycled widgets precede the real final rows. They must not consume
            // the exported association cap or match just because their index matches.
            var leftovers = Enumerable.Range(0, 60).Select(index => (nint)fixture.Component((uint)(100 + index), ComponentType.ListItemRenderer)).ToArray();
            fixture.Nodes(&tree->UldManager, [(nint)selected, (nint)recycled, (nint)unused, ..leftovers, (nint)last, (nint)header]);
            fixture.Nodes(fixture.Root, [(nint)treeNode]);
            Check(ActivityMenuDiagnostic.TryCopyVisibleText(fixture.Root, out var capture, out _, true, fixture.TypeOf));
            var lists = capture.Lists!;
            Check(lists.lists.Length == 1 && lists.lists[0].component_type == "tree_list");
            Check(lists.lists[0].list_length == 6 && lists.lists[0].allocated_renderer_count == 9 &&
                lists.lists[0].tree_item_count == 13 && lists.lists[0].tree_vector_count == 13);
            Check(lists.lists[0].selected_index == 12);
            Check(lists.renderers.Select(row => row.logical_index).Order().SequenceEqual(new int?[] { 2, 11, 12 }));
            Check(lists.renderers.All(row => row.component_path[^1] is 40 or 43 or 44));
            Check(!capture.TruncationReasons.Contains("renderer_association_limit"));
            Check(lists.renderers.All(row => row.nearest_list_path!.SequenceEqual([79u])));
            Check(ActivityMenuDiagnostic.TryCopyVisibleText(fixture.Root, out var repeated, out _, true, fixture.TypeOf) && ActivityMenuDiagnostic.SameText(capture, repeated));
            tree->SelectedItemIndex = 3;
            Check(ActivityMenuDiagnostic.TryCopyVisibleText(fixture.Root, out repeated, out _, true, fixture.TypeOf) && !ActivityMenuDiagnostic.SameText(capture, repeated));
            tree->SelectedItemIndex = 12; ((AtkComponentListItemRenderer*)selected->Component)->ListItemIndex = 3;
            Check(ActivityMenuDiagnostic.TryCopyVisibleText(fixture.Root, out repeated, out _, true, fixture.TypeOf) && !ActivityMenuDiagnostic.SameText(capture, repeated));
            using var output = Parse(ActivityMenuDiagnostic.Format(ActivityDiagnosticTarget.Mogpendium, "MoogleCollection", null, capture));
            var structure = output.RootElement.GetProperty("mogpendium_lists");
            Check(structure.GetProperty("schema").GetString() == "mogpendium_list_structure_v2");
            Check(structure.EnumerateObject().Select(value => value.Name).ToHashSet().SetEquals(["schema", "coverage", "lists", "renderers", "output_renderers_omitted"]));
            Check(!output.RootElement.GetRawText().Contains("RendererArray") && !output.RootElement.GetRawText().Contains("ComponentAddress"));
            Console.WriteLine("PASS six-entry/thirteen-row tree accepts late selection; pointer-bound rows survive sixty stale widgets and same-index duplicates without exhausting associations");
        }
        using (var fixture = new Fixture())
        {
            var ordinary = fixture.Component(1, ComponentType.Button); // only base-sized allocation
            var hidden = fixture.Component(2, ComponentType.List); hidden->NodeFlags = 0; hidden->Component = (AtkComponentBase*)1;
            fixture.Nodes(fixture.Root, [(nint)ordinary, (nint)hidden]);
            Check(ActivityMenuDiagnostic.TryCopyVisibleText(fixture.Root, out var capture, out _, true, fixture.TypeOf) && capture.Lists!.lists.Length == 0);
            Check(fixture.TypeCalls == 1); // exact type guard excludes the base-sized Button
            Check(ActivityMenuDiagnostic.TryCopyVisibleText(fixture.Root, out capture, out _, false, _ => throw new Exception("Non-Mog type access")) && capture.Lists is null);
            using var output = Parse(ActivityMenuDiagnostic.Format(ActivityDiagnosticTarget.FieldRecord, "MYCWarResultNotebook", null, capture));
            Check(!output.RootElement.TryGetProperty("mogpendium_lists", out _));
            Check(ActivityMenuDiagnostic.Format(ActivityDiagnosticTarget.FieldRecord, "MYCWarResultNotebook", null,
                capture with { Lists = new([], [], []) }).Text is null);
            Console.WriteLine("PASS exact component-type guards precede derived reads, hidden components are untouched, and non-Mog diagnostics keep their old output");
        }
        using (var fixture = new Fixture())
        {
            var node = fixture.Component(79, ComponentType.TreeList); var tree = (AtkComponentTreeList*)node->Component;
            fixture.Nodes(fixture.Root, [(nint)node]);
            Check(ActivityMenuDiagnostic.TryCopyVisibleText(fixture.Root, out _, out _, true, fixture.TypeOf)); // all-null vector
            tree->ListLength = -1;
            Check(!ActivityMenuDiagnostic.TryCopyVisibleText(fixture.Root, out _, out var failure, true, fixture.TypeOf) && failure == "list count or selection bounds");
            tree->ListLength = 2; tree->SelectedItemIndex = 2;
            Check(ActivityMenuDiagnostic.TryCopyVisibleText(fixture.Root, out _, out _, true, fixture.TypeOf));
            tree->SelectedItemIndex = int.MaxValue;
            Check(!ActivityMenuDiagnostic.TryCopyVisibleText(fixture.Root, out _, out _, true, fixture.TypeOf));
            tree->SelectedItemIndex = -1; tree->Items.Last = (Pointer<AtkComponentTreeListItem>*)8;
            Check(!ActivityMenuDiagnostic.TryCopyVisibleText(fixture.Root, out _, out failure, true, fixture.TypeOf) && failure == "tree list vector or count bounds");
            tree->Items.Last = null; tree->OwnerNode = null;
            Check(!ActivityMenuDiagnostic.TryCopyVisibleText(fixture.Root, out _, out failure, true, fixture.TypeOf) && failure == "list owner node");
            Check(!MogpendiumListDiagnostic.VectorCount(32, 24, 40, out _));
            Check(!MogpendiumListDiagnostic.VectorCount(32, 41, 48, out _));
            Check(!MogpendiumListDiagnostic.VectorCount(0, 0, 8, out _));
            Check(!MogpendiumListDiagnostic.VectorCount(8, 8 + 513 * 8, 8 + 513 * 8, out _));
            Console.WriteLine("PASS malformed/null vector headers, excessive counts, unbounded selection and wrong ownership fail with fixed reasons before array traversal");
        }
        using (var fixture = new Fixture())
        {
            var node = fixture.Component(79, ComponentType.List); var list = (AtkComponentList*)node->Component;
            list->ListLength = 2; list->SelectedItemIndex = 0;
            var collector = new MogpendiumListDiagnostic(fixture.TypeOf);
            Check(collector.Observe(node, [79], out _)); list->SelectedItemIndex = 1;
            Check(!collector.Finish(out _, out var failure) && failure == "changed list structure");
            collector = new(fixture.TypeOf); Check(collector.Observe(node, [79], out _)); list->ItemRendererList = (AtkComponentList.ListItem*)1;
            Check(!collector.Finish(out _, out _));
            collector = new(fixture.TypeOf); Check(collector.Observe(node, [79], out _)); fixture.Types[(nint)list] = ComponentType.Button;
            Check(!collector.Finish(out _, out _));
            var renderer = fixture.Component(40, ComponentType.ListItemRenderer); var nativeRenderer = (AtkComponentListItemRenderer*)renderer->Component;
            nativeRenderer->ListItemIndex = 0; collector = new(fixture.TypeOf); Check(collector.Observe(renderer, [79, 40], out _));
            nativeRenderer->ListItemIndex = 1; Check(!collector.Finish(out _, out _));
            fixture.Types[(nint)list] = ComponentType.List;
            nativeRenderer->ListItemIndex = int.MaxValue; collector = new(fixture.TypeOf);
            Check(collector.Observe(node, [79], out _) && collector.Observe(renderer, [79, 40], out _));
            Check(collector.Finish(out var unbound, out _) && unbound.renderers[0].logical_index is null && unbound.renderers[0].index_state == "outside_bounds");
            Console.WriteLine("PASS within-capture selection, pointer, component-type and renderer changes reject; invalid renderer indices remain explicitly unbound");
        }
        using (var fixture = new Fixture())
        {
            var node = fixture.Component(79, ComponentType.TreeList); var tree = (AtkComponentTreeList*)node->Component;
            var renderer = fixture.Component(7, ComponentType.ListItemRenderer);
            ((AtkComponentListItemRenderer*)renderer->Component)->ListItemIndex = 5;
            fixture.TreeItems(tree, [(nint)renderer->Component, (nint)1]); // unseen Renderer must never be dereferenced
            tree->ItemCount = 2; tree->VisibleItemCount = 2; tree->ListLength = 1;
            fixture.Nodes(&tree->UldManager, [(nint)renderer]); fixture.Nodes(fixture.Root, [(nint)node]);
            Check(ActivityMenuDiagnostic.TryCopyVisibleText(fixture.Root, out var capture, out _, true, fixture.TypeOf));
            Check(capture.Lists!.renderers.Length == 1 && capture.Lists.renderers[0].logical_index == 5);
            // Vector slot is a separate structural observation; index5 must not be
            // rewritten to slot0 or assumed to identify a public objective.
            Check(capture.Lists.renderers[0].tree_vector_slot == 0);
            var collector = new MogpendiumListDiagnostic(fixture.TypeOf);
            Check(collector.Observe(node, [79], out _) && collector.Observe(renderer, [79, 7], out _));
            var item = (AtkComponentTreeListItem*)tree->Items.First[0];
            item->Renderer = (AtkComponentListItemRenderer*)1;
            Check(!collector.Finish(out _, out _));
            item->Renderer = (AtkComponentListItemRenderer*)renderer->Component;
            var other = (AtkComponentTreeListItem*)tree->Items.First[1];
            other->Renderer = item->Renderer;
            Check(ActivityMenuDiagnostic.TryCopyVisibleText(fixture.Root, out var duplicate, out _, true, fixture.TypeOf));
            Check(duplicate.Lists!.renderers.Length == 0 && duplicate.Lists.lists[0].tree_items_ambiguous > 0);
            var ordinary = fixture.Component(80, ComponentType.List);
            ((AtkComponentList*)ordinary->Component)->ListLength = 2; ((AtkComponentList*)ordinary->Component)->SelectedItemIndex = 2;
            fixture.Nodes(fixture.Root, [(nint)ordinary]);
            Check(!ActivityMenuDiagnostic.TryCopyVisibleText(fixture.Root, out _, out _, true, fixture.TypeOf));
            Console.WriteLine("PASS active tree association uses identity rather than logical index, never dereferences unseen renderers, detects changed/duplicate backing rows and preserves ordinary-list bounds");
        }
        using (var fixture = new Fixture())
        {
            var nodes = new List<nint>();
            for (uint index = 0; index < MogpendiumListDiagnostic.MaxLists + 1; ++index)
            {
                var node = fixture.Component(index + 1, ComponentType.List);
                if (index == MogpendiumListDiagnostic.MaxLists) ((AtkComponentList*)node->Component)->ListLength = int.MaxValue;
                nodes.Add((nint)node);
            }
            var children = new List<nint>();
            for (uint index = 0; index < MogpendiumListDiagnostic.MaxRenderers + 1; ++index)
                children.Add((nint)fixture.Component(index + 100, ComponentType.ListItemRenderer));
            fixture.Nodes(&((AtkComponentNode*)nodes[0])->Component->UldManager, children.ToArray());
            fixture.Nodes(fixture.Root, nodes.ToArray());
            Check(ActivityMenuDiagnostic.TryCopyVisibleText(fixture.Root, out var capture, out _, true, fixture.TypeOf));
            Check(capture.Lists!.lists.Length == 8 && capture.Lists.renderers.Length == 32 &&
                capture.TruncationReasons.Contains("list_component_limit") && capture.TruncationReasons.Contains("renderer_association_limit"));
            var longPath = Enumerable.Repeat(uint.MaxValue, 8).ToArray();
            var lists = capture.Lists with {
                lists = capture.Lists.lists.Select(row => row with { component_path = longPath }).ToArray(),
                renderers = capture.Lists.renderers.Select(row => row with { component_path = longPath, nearest_list_path = longPath }).ToArray(),
            };
            var texts = Enumerable.Range(0, 200).Select(index => new ActivityDiagnosticText((uint)index, null, longPath, new string('界', 13))).ToArray();
            var maximal = capture with { Lists = lists, Texts = texts };
            using var output = Parse(ActivityMenuDiagnostic.Format(ActivityDiagnosticTarget.Mogpendium, "MoogleCollection", null, maximal));
            Check(output.RootElement.GetProperty("truncated").GetBoolean());
            Check(output.RootElement.GetProperty("mogpendium_lists").GetProperty("lists").GetArrayLength() == 8);
            var kept = output.RootElement.GetProperty("mogpendium_lists").GetProperty("renderers").GetArrayLength();
            Check(kept == 32 || kept > 0 && output.RootElement.GetProperty("mogpendium_lists").GetProperty("output_renderers_omitted").GetInt32() == 32 - kept);
            Check(output.RootElement.GetProperty("truncation_reasons").EnumerateArray().Any(value => value.GetString() == "output_byte_limit"));
            var denseLists = lists.lists.Select((row, index) => row with {
                component_path = [..longPath.Take(6), (uint)(uint.MaxValue - index)], component_type = "tree_list",
                tree_item_count = 8, tree_visible_item_count = 8, tree_vector_count = 8, layout_refresh_pending = false,
                tree_items_examined = 8, tree_items_matched = 8, tree_items_unmatched = 0, tree_items_null = 0, tree_items_ambiguous = 0,
            }).ToArray();
            var dense = lists with { lists = denseLists, renderers = lists.renderers.Select((row, index) => row with {
                nearest_list_path = denseLists[index / 4].component_path, component_path = [..denseLists[index / 4].component_path, uint.MaxValue],
                index_in_list = null, tree_vector_slot = index % 4, tree_item_type = 255, tree_item_depth = 255, tree_item_hidden = false,
            }).ToArray() };
            using var metadataOnly = Parse(ActivityMenuDiagnostic.Format(ActivityDiagnosticTarget.Mogpendium, "MoogleCollection", null,
                maximal with { Lists = dense, Texts = [] }));
            var densePayload = metadataOnly.RootElement.GetProperty("mogpendium_lists");
            Check(densePayload.GetProperty("lists").GetArrayLength() == 8);
            Check(densePayload.GetProperty("renderers").GetArrayLength() + densePayload.GetProperty("output_renderers_omitted").GetInt32() == 32);
            Check(ActivityMenuDiagnostic.Format(ActivityDiagnosticTarget.Mogpendium, "MoogleCollection", null,
                maximal with { Lists = lists with { lists = [..lists.lists, lists.lists[0]] } }).Text is null);
            Console.WriteLine("PASS list/renderer caps are explicit and max metadata plus dense text fits16KB while retaining structural records");
        }
        using (var fixture = new Fixture())
        {
            var node = fixture.Component(79, ComponentType.TreeList); var tree = (AtkComponentTreeList*)node->Component;
            fixture.TreeItems(tree, new nint[65]); tree->ItemCount = 65; tree->VisibleItemCount = 65;
            tree->Items.First[64] = (AtkComponentTreeListItem*)1; // beyond the bounded traversal; must never be followed
            fixture.Nodes(fixture.Root, [(nint)node]);
            Check(ActivityMenuDiagnostic.TryCopyVisibleText(fixture.Root, out var capture, out _, true, fixture.TypeOf));
            Check(capture.Lists!.lists[0].tree_items_examined == 64 && capture.TruncationReasons.Contains("tree_item_limit"));
            using var output = Parse(ActivityMenuDiagnostic.Format(ActivityDiagnosticTarget.Mogpendium, "MoogleCollection", null, capture));
            Check(output.RootElement.GetProperty("truncated").GetBoolean());
            Console.WriteLine("PASS backing traversal is capped before the untrusted65th pointer and reports the omitted tree range");
        }
    }

    private static JsonDocument Parse(ChallengeDiagnosticResult result)
    { Check(result.Text is not null && Encoding.UTF8.GetByteCount(result.Text) <= ActivityMenuDiagnostic.MaxOutputBytes); return JsonDocument.Parse(result.Text!); }
    private static void Check(bool value) { if (!value) throw new Exception("Mogpendium structural diagnostic assertion failed"); }

    private sealed class Fixture : IDisposable
    {
        private readonly List<nint> allocations = [];
        internal readonly Dictionary<nint, ComponentType> Types = [];
        internal readonly AtkUldManager* Root;
        internal int TypeCalls;
        internal Fixture() => Root = Allocate<AtkUldManager>();
        internal ComponentType TypeOf(nint component) { ++TypeCalls; return Types[component]; }
        private T* Allocate<T>() where T : unmanaged
        { var pointer = (T*)NativeMemory.AllocZeroed((nuint)sizeof(T)); allocations.Add((nint)pointer); return pointer; }
        internal AtkComponentNode* Component(uint id, ComponentType type)
        {
            var node = Allocate<AtkComponentNode>(); node->NodeId = id; node->Type = (NodeType)1000;
            node->NodeFlags = NodeFlags.Visible; node->Color.A = 255;
            node->Component = type switch {
                ComponentType.List => (AtkComponentBase*)Allocate<AtkComponentList>(),
                ComponentType.TreeList => (AtkComponentBase*)Allocate<AtkComponentTreeList>(),
                ComponentType.ListItemRenderer => (AtkComponentBase*)Allocate<AtkComponentListItemRenderer>(),
                _ => Allocate<AtkComponentBase>(),
            };
            node->Component->OwnerNode = node; Types.Add((nint)node->Component, type);
            if (type is ComponentType.List or ComponentType.TreeList) ((AtkComponentList*)node->Component)->SelectedItemIndex = -1;
            return node;
        }
        internal void Nodes(AtkUldManager* manager, nint[] nodes)
        {
            var list = (AtkResNode**)NativeMemory.AllocZeroed((nuint)(nodes.Length * IntPtr.Size)); allocations.Add((nint)list);
            for (var index = 0; index < nodes.Length; ++index) list[index] = (AtkResNode*)nodes[index];
            manager->NodeList = list; manager->NodeListCount = (ushort)nodes.Length;
        }
        internal void TreeItems(AtkComponentTreeList* tree, nint[] renderers)
        {
            var items = (Pointer<AtkComponentTreeListItem>*)NativeMemory.AllocZeroed((nuint)(renderers.Length * IntPtr.Size));
            allocations.Add((nint)items);
            for (var index = 0; index < renderers.Length; ++index)
            {
                var item = Allocate<AtkComponentTreeListItem>();
                item->Renderer = (AtkComponentListItemRenderer*)renderers[index];
                items[index] = item;
            }
            tree->Items.First = items; tree->Items.Last = tree->Items.End = items + renderers.Length;
        }
        public void Dispose() { foreach (var pointer in allocations) NativeMemory.Free((void*)pointer); }
    }
}
