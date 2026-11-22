using System.Runtime.InteropServices;
using System.Text;
using CompAI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

internal static unsafe class ActiveListChecks
{
    internal static void Run()
    {
        Test("active Weekly tree copies all six matched pairs despite stale same-index widgets", () =>
        {
            using var fixture = new Fixture(1);
            Check(fixture.Copy(out var copied));
            Check(copied!.Choices.Length == 6 && copied.Choices[0] == new MogpendiumObservedChoice("Week 6", "Duty 6", 1) &&
                copied.Choices[^1] == new MogpendiumObservedChoice("Week 1", "Duty 1", 1));
            Check(((AtkComponentNode*)fixture.Renderers[3])->NodeId == 51001 && ((AtkTextNode*)fixture.Texts[3])->ParentNode->NodeId == 51001);
            fixture.Tree->SelectedItemIndex = 512; // only bounded scalar; no selected-index-domain inference
            foreach (var node in fixture.Renderers) ((AtkComponentListItemRenderer*)((AtkComponentNode*)node)->Component)->ListItemIndex += 20;
            Check(fixture.Copy(out var alternate) && MogpendiumActiveList.Same(copied, alternate));
        });
        Test("active Minimog tree preserves both displayed choices per week including generic repeated labels", () =>
        {
            using var fixture = new Fixture(2);
            Check(fixture.Copy(out var copied));
            Check(copied!.Choices.Length == 12 && copied.Choices.Where(row => row.Slot == 1).All(row => row.Label == "Save the Queen") &&
                copied.Choices[0].WeekLabel == "Week 6" && copied.Choices[^1] == new MogpendiumObservedChoice("Week 1", "Choice 1", 2));
            fixture.SetText(fixture.Texts[4], "Week 6");
            Check(!fixture.Copy(out _)); // duplicate displayed week labels are ambiguous
        });
        Test("active tree rejects incomplete counts, excessive weeks and malformed headers before item traversal", () =>
        {
            Reject(f => f.Tree->VisibleItemCount--);
            Reject(f => f.Tree->ItemCount--);
            Reject(f => f.Tree->ListLength--);
            Reject(f => f.Tree->IsUpdatePending = true);
            Reject(f => f.Tree->IsScrollRefreshPending = true);
            Reject(f => f.Tree->LayoutRefreshPending = true);
            Reject(f => f.Tree->SelectedItemIndex = 513);
            Reject(f => f.Tree->Items.Last = (Pointer<AtkComponentTreeListItem>*)8);
            Reject(f => f.Tree->Items.First = null);
            Reject(f => f.Addon->UldManager.NodeListCount = 513);
            Reject(f => f.Addon->UldManager.NodeList = (AtkResNode**)1);
            Reject(f => f.Nodes(&f.Addon->UldManager, [(nint)1]));
            using var tooMany = new Fixture(2, 17);
            Check(!tooMany.Copy(out _));
            using var bounded = new Fixture(2, 16);
            Check(bounded.Copy(out var copied) && copied!.Choices.Length == 32);
        });
        Test("active tree rejects null, duplicate, hidden, wrong-type and unbound backing items without following arbitrary Renderer", () =>
        {
            Reject(f => f.Tree->Items.First[2] = (AtkComponentTreeListItem*)1);
            Reject(f => f.Tree->Items.First[2] = (AtkComponentTreeListItem*)null);
            Reject(f => f.Tree->Items.First[2] = f.Tree->Items.First[1]);
            Reject(f => f.Item(2)->Renderer = f.Item(1)->Renderer);
            Reject(f => f.Item(2)->Renderer = (AtkComponentListItemRenderer*)1);
            Reject(f => f.Item(2)->IsHidden = true);
            Reject(f => f.Item(2)->Depth = 1);
            Reject(f => f.Item(2)->Type = TreeListItemType.SectionHeader);
            Reject(f => f.Types[(nint)f.Item(2)->Renderer] = ComponentType.Button);
            Reject(f => ((AtkComponentBase*)f.Item(2)->Renderer)->OwnerNode = null);
            Reject(f => ((AtkComponentNode*)f.Renderers[2])->NodeFlags = 0);
            Reject(f => f.Types[(nint)f.Tree] = ComponentType.Button);
            Reject(f => f.RootNode->Component = (AtkComponentBase*)1);
        });
        Test("active labels require exact text nodes, visible ancestry, valid bounded text and unique root identity", () =>
        {
            Reject(f => ((AtkTextNode*)f.Texts[2])->NodeId = 99);
            Reject(f => ((AtkTextNode*)f.Texts[2])->NodeFlags = 0);
            Reject(f => f.SetText(f.Texts[2], new string('x', 129)));
            Reject(f => f.SetText(f.Texts[2], [255]));
            Reject(f => ((AtkTextNode*)f.Texts[2])->NodeText.BufUsed = 1026);
            Reject(f => ((AtkTextNode*)f.Texts[2])->ParentNode = (AtkResNode*)1);
            Reject(f => f.Nodes(&f.Addon->UldManager, [(nint)f.RootNode, (nint)f.RootNode]));
            Reject(f =>
            {
                var renderer = (AtkComponentNode*)f.Renderers[2];
                f.Nodes(&renderer->Component->UldManager, [f.Texts[2], f.Texts[2]]);
            });
        });
        Test("active snapshot detects item, renderer, selection and text changes during and between native copies", () =>
        {
            Reject(f => f.OnType = (component, call) => { if (component != (nint)f.Tree && call == 2) f.Item(2)->Renderer = (AtkComponentListItemRenderer*)1; });
            Reject(f => f.OnTreeType = count => { if (count == 4) f.Tree->SelectedItemIndex = 2; });
            Reject(f => f.OnTreeType = count => { if (count == 4) f.Item(2)->State = TreeListItemState.Disabled; });
            Reject(f => f.OnTreeType = count => { if (count == 4) f.SetText(f.Texts[2], "Changed duty"); });
            Reject(f => f.OnTreeType = count => { if (count == 4) ((AtkComponentListItemRenderer*)f.Item(2)->Renderer)->ListItemIndex = 51; });
        });
    }

    private static void Reject(Action<Fixture> change)
    {
        using var fixture = new Fixture(1); change(fixture); Check(!fixture.Copy(out _));
    }
    private static void Test(string name, Action action) { action(); Console.WriteLine("PASS " + name); }
    private static void Check(bool value) { if (!value) throw new Exception("Mogpendium active-list assertion failed"); }

    private sealed class Fixture : IDisposable
    {
        private readonly List<nint> allocations = [];
        private readonly int choicesPerWeek;
        internal readonly Dictionary<nint, ComponentType> Types = [];
        internal readonly AtkUnitBase* Addon;
        internal readonly AtkComponentNode* RootNode;
        internal AtkComponentTreeList* Tree => (AtkComponentTreeList*)RootNode->Component;
        internal readonly nint[] Renderers;
        internal readonly nint[] Texts;
        internal Action<nint, int>? OnType;
        internal Action<int>? OnTreeType;
        private int typeCalls;
        private int treeTypeCalls;

        internal Fixture(int choicesPerWeek, int weeks = 6)
        {
            this.choicesPerWeek = choicesPerWeek;
            Addon = Allocate<AtkUnitBase>();
            RootNode = Component(79, true);
            var count = 1 + weeks * (choicesPerWeek + 1);
            Renderers = new nint[count]; Texts = new nint[count];
            var slots = (Pointer<AtkComponentTreeListItem>*)NativeMemory.AllocZeroed((nuint)(count * IntPtr.Size));
            allocations.Add((nint)slots);
            Tree->Items.First = slots; Tree->Items.Last = Tree->Items.End = slots + count;
            Tree->ItemCount = Tree->VisibleItemCount = (uint)count;
            Tree->ListLength = weeks * choicesPerWeek; Tree->SelectedItemIndex = count - 1;
            var children = new List<nint>();
            for (var stale = 0; stale < 60; ++stale)
            {
                var node = Component((uint)(80000 + stale), false);
                Types.Remove((nint)node->Component); // querying a stale type would fail the fixture
                ((AtkComponentListItemRenderer*)node->Component)->ListItemIndex = count - 1;
                children.Add((nint)node);
            }
            for (var slot = 0; slot < count; ++slot)
            {
                var header = slot == 0 || (slot - 1) % (choicesPerWeek + 1) == 0;
                var weekIndex = slot == 0 ? 0 : (slot - 1) / (choicesPerWeek + 1);
                var choiceIndex = slot == 0 ? 0 : weekIndex * choicesPerWeek + (slot - 1) % (choicesPerWeek + 1) - 1;
                var nodeId = slot == 0 ? 4u : header ? weekIndex == 0 ? 5u : (uint)(51000 + weekIndex)
                    : choiceIndex == 0 ? 7u : (uint)(71000 + choiceIndex);
                var node = Component(nodeId, false); node->ParentNode = (AtkResNode*)RootNode;
                ((AtkComponentListItemRenderer*)node->Component)->ListItemIndex = slot;
                Renderers[slot] = (nint)node; children.Add((nint)node);
                var item = Allocate<AtkComponentTreeListItem>(); slots[slot] = item;
                item->Renderer = (AtkComponentListItemRenderer*)node->Component;
                item->Type = header ? TreeListItemType.SectionHeader : TreeListItemType.None;
                if (slot == 0) continue;
                var offset = (slot - 1) % (choicesPerWeek + 1);
                var week = weeks - (slot - 1) / (choicesPerWeek + 1);
                var label = header ? "Week " + week : choicesPerWeek == 1 ? "Duty " + week : offset == 1 ? "Save the Queen" : "Choice " + week;
                var text = Allocate<AtkTextNode>(); text->NodeId = header ? 7u : 4u; text->Type = NodeType.Text;
                text->NodeFlags = NodeFlags.Visible; text->Color.A = 255; text->ParentNode = (AtkResNode*)node;
                Texts[slot] = (nint)text; SetText((nint)text, label);
                Nodes(&node->Component->UldManager, [(nint)text]);
            }
            Nodes(&Tree->UldManager, children.ToArray());
            Nodes(&Addon->UldManager, [(nint)RootNode]);
        }

        internal bool Copy(out MogpendiumObservedList? result) => MogpendiumActiveList.TryCopy(Addon, choicesPerWeek, out result, TypeOf);
        internal AtkComponentTreeListItem* Item(int slot) => Tree->Items.First[slot].Value;
        private ComponentType TypeOf(nint component)
        {
            ++typeCalls; OnType?.Invoke(component, typeCalls);
            if (component == (nint)RootNode->Component) { ++treeTypeCalls; OnTreeType?.Invoke(treeTypeCalls); }
            return Types[component];
        }
        private T* Allocate<T>() where T : unmanaged
        { var pointer = (T*)NativeMemory.AllocZeroed((nuint)sizeof(T)); allocations.Add((nint)pointer); return pointer; }
        private AtkComponentNode* Component(uint id, bool tree)
        {
            var node = Allocate<AtkComponentNode>(); node->NodeId = id; node->Type = (NodeType)1000;
            node->NodeFlags = NodeFlags.Visible; node->Color.A = 255;
            node->Component = tree ? (AtkComponentBase*)Allocate<AtkComponentTreeList>() : (AtkComponentBase*)Allocate<AtkComponentListItemRenderer>();
            node->Component->OwnerNode = node; Types.Add((nint)node->Component, tree ? ComponentType.TreeList : ComponentType.ListItemRenderer);
            return node;
        }
        internal void SetText(nint node, string text) => SetText(node, Encoding.UTF8.GetBytes(text));
        internal void SetText(nint node, byte[] bytes)
        {
            var text = (AtkTextNode*)node;
            var buffer = (byte*)NativeMemory.AllocZeroed((nuint)(bytes.Length + 1)); allocations.Add((nint)buffer);
            bytes.CopyTo(new Span<byte>(buffer, bytes.Length)); text->NodeText.StringPtr = buffer;
            text->NodeText.StringLength = bytes.Length; text->NodeText.BufUsed = text->NodeText.BufSize = bytes.Length + 1;
        }
        internal void Nodes(AtkUldManager* manager, nint[] nodes)
        {
            var list = (AtkResNode**)NativeMemory.AllocZeroed((nuint)(nodes.Length * IntPtr.Size)); allocations.Add((nint)list);
            for (var index = 0; index < nodes.Length; ++index) list[index] = (AtkResNode*)nodes[index];
            manager->NodeList = list; manager->NodeListCount = (ushort)nodes.Length;
        }
        public void Dispose() { foreach (var pointer in allocations) NativeMemory.Free((void*)pointer); }
    }
}
