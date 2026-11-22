using System.Runtime.InteropServices;
using CompAI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ChildNode = FFXIVClientStructs.STD.ContainerInterface.IStdList<FFXIVClientStructs.Interop.Pointer<FFXIVClientStructs.FFXIV.Component.GUI.AtkAddonControl.ChildAddonInfo>>.Node;

internal static unsafe class GoldSaucerChildDiagnosticChecks
{
    private static int passed;
    internal static void Run()
    {
        Test("Mahjong direct-child diagnostic binds one live owned child without assuming child tab index", () =>
        {
            using var fixture = new Fixture(1);
            fixture.Info[0].TabIndex = 19;
            Check(fixture.Observe(out var observation) && observation.TextAddon == (nint)fixture.Children &&
                observation.TextAddonName == "SyntheticChild0" && observation.Children[0].RawTabIndex == 19);
            Check(fixture.Observe(out var repeated) && GoldSaucerChildDiagnostic.Same(observation, repeated));
        });
        Test("empty native list is not allocated or mutated by a diagnostic", () =>
        {
            using var fixture = new Fixture(0);
            fixture.Parent->AddonControl.ChildAddons.WithOps.Head = null;
            Check(fixture.Observe(out var result) && result.TextAddon == 0 && result.Status == "no_visible_owned_child");
            Check(fixture.Parent->AddonControl.ChildAddons.WithOps.Head == null);
        });
        Test("multiple visible children and unsettled visible children never select arbitrary text", () =>
        {
            using var fixture = new Fixture(2);
            Check(fixture.Observe(out var result) && result.TextAddon == 0 && result.Status == "multiple_visible_owned_children");
            fixture.Children[1].IsVisible = false;
            Check(fixture.Observe(out result) && result.TextAddon == (nint)fixture.Children);
            fixture.Children[1].IsVisible = true; fixture.Roots[1].NodeFlags = 0;
            Check(fixture.Observe(out result) && result.TextAddon == (nint)fixture.Children);
            fixture.Roots[1].NodeFlags = NodeFlags.Visible;
            fixture.Children[1].IsVisible = true; fixture.Children[1].Flags1A1 = 0;
            Check(fixture.Observe(out result) && result.TextAddon == 0 && result.Status == "visible_child_not_ready");
        });
        Test("wrong parent tab ownership readiness and over-bound count fail before traversal", () =>
        {
            using var fixture = new Fixture(1);
            fixture.Parent->SelectedCategory = 4; Check(!fixture.Observe(out _)); fixture.Parent->SelectedCategory = 5;
            fixture.Parent->AddonControl.ParentAddon = null; Check(!fixture.Observe(out _));
            fixture.Parent->AddonControl.ParentAddon = (AtkUnitBase*)fixture.Parent;
            fixture.Parent->AddonControl.IsChildSetupComplete = false; Check(!fixture.Observe(out _));
            fixture.Parent->AddonControl.IsChildSetupComplete = true;
            fixture.Parent->AddonControl.ChildAddons.WithOps.Size = 17;
            fixture.Parent->AddonControl.ChildAddons.WithOps.Head = (ChildNode*)1;
            Check(!fixture.Observe(out _));
        });
        Test("malformed short cyclic and duplicate linked-child structures fail closed", () =>
        {
            using var fixture = new Fixture(2);
            fixture.Nodes[0].Next = fixture.Head; Check(!fixture.Observe(out _)); fixture.Nodes[0].Next = fixture.Nodes + 1;
            fixture.Nodes[1].Previous = fixture.Head; Check(!fixture.Observe(out _)); fixture.Nodes[1].Previous = fixture.Nodes;
            fixture.Nodes[1].Next = fixture.Nodes; Check(!fixture.Observe(out _)); fixture.Nodes[1].Next = fixture.Head;
            fixture.Nodes[1].Value = fixture.Info; Check(!fixture.Observe(out _)); fixture.Nodes[1].Value = fixture.Info + 1;
            fixture.Info[1].AtkUnitBase = fixture.Children; Check(!fixture.Observe(out _));
        });
        Test("unregistered child invalid names and cyclic ancestry cannot authorize child text", () =>
        {
            using var fixture = new Fixture(1);
            Check(!GoldSaucerChildDiagnostic.TryObserve(fixture.Parent, _ => 0, out _));
            fixture.Children[0].Name.Fill((byte)'X'); Check(!fixture.Observe(out _));
            fixture.Children[0].NameString = "SyntheticChild0";
            fixture.Roots[0].ParentNode = fixture.Roots;
            Check(fixture.Observe(out var result) && result.TextAddon == 0);
            fixture.Roots[0].ParentNode = (AtkResNode*)1;
            Check(fixture.Observe(out result) && result.TextAddon == 0 && result.Status == "visible_child_not_ready");
            fixture.Roots[0].ParentNode = null;
            fixture.Children[0].RootNode = (AtkResNode*)1;
            Check(fixture.Observe(out result) && result.TextAddon == 0 && result.Status == "visible_child_not_ready");
        });
        Test("parent listed as its own child is rejected despite valid registry and visible text roots", () =>
        {
            using var fixture = new Fixture(1);
            fixture.Parent->NameString = "GoldSaucerInfo";
            fixture.Parent->RootNode = fixture.Roots; fixture.Parent->Alpha = 255;
            fixture.Info[0].AtkUnitBase = (AtkUnitBase*)fixture.Parent;
            Check(!GoldSaucerChildDiagnostic.TryObserve(fixture.Parent,
                id => id == fixture.Parent->Id ? (nint)fixture.Parent : fixture.Resolve(id), out _));
        });
        Test("mutation during registry resolution and between observations is rejected", () =>
        {
            using var fixture = new Fixture(1);
            Check(fixture.Observe(out var before));
            fixture.Info[0].PositionX = 42;
            Check(fixture.Observe(out var after) && !GoldSaucerChildDiagnostic.Same(before, after));
            var calls = 0;
            Check(!GoldSaucerChildDiagnostic.TryObserve(fixture.Parent, id =>
            {
                if (++calls == 1) fixture.Nodes[0].Next = fixture.Nodes;
                return fixture.Resolve(id);
            }, out _));
        });
        Console.WriteLine($"Passed {passed} Gold Saucer child diagnostic groups.");
    }

    private static void Test(string name, Action body) { body(); ++passed; Console.WriteLine("PASS " + name); }
    private static void Check(bool value) { if (!value) throw new Exception("Gold Saucer child diagnostic assertion failed"); }

    private sealed class Fixture : IDisposable
    {
        internal readonly AddonGoldSaucerInfo* Parent;
        internal readonly ChildNode* Head;
        internal readonly ChildNode* Nodes;
        internal readonly AtkAddonControl.ChildAddonInfo* Info;
        internal readonly AtkUnitBase* Children;
        internal readonly AtkResNode* Roots;
        private readonly int count;
        internal Fixture(int count)
        {
            this.count = count;
            Parent = (AddonGoldSaucerInfo*)NativeMemory.AllocZeroed((nuint)sizeof(AddonGoldSaucerInfo));
            Head = (ChildNode*)NativeMemory.AllocZeroed((nuint)sizeof(ChildNode));
            var allocationCount = Math.Max(1, count);
            Nodes = (ChildNode*)NativeMemory.AllocZeroed((nuint)allocationCount, (nuint)sizeof(ChildNode));
            Info = (AtkAddonControl.ChildAddonInfo*)NativeMemory.AllocZeroed((nuint)allocationCount, (nuint)sizeof(AtkAddonControl.ChildAddonInfo));
            Children = (AtkUnitBase*)NativeMemory.AllocZeroed((nuint)allocationCount, (nuint)sizeof(AtkUnitBase));
            Roots = (AtkResNode*)NativeMemory.AllocZeroed((nuint)allocationCount, (nuint)sizeof(AtkResNode));
            Parent->Id = 77; Parent->Flags1A1 = 1; Parent->IsVisible = true; Parent->SelectedCategory = 5;
            Parent->AddonControl.ParentAddon = (AtkUnitBase*)Parent;
            Parent->AddonControl.IsParentAddonLinked = true; Parent->AddonControl.IsChildSetupComplete = true;
            Parent->AddonControl.ChildAddons.WithOps.Head = Head; Parent->AddonControl.ChildAddons.WithOps.Size = (ulong)count;
            Head->Next = count == 0 ? Head : Nodes; Head->Previous = count == 0 ? Head : Nodes + count - 1;
            for (var index = 0; index < count; ++index)
            {
                Nodes[index].Previous = index == 0 ? Head : Nodes + index - 1;
                Nodes[index].Next = index == count - 1 ? Head : Nodes + index + 1;
                Nodes[index].Value = Info + index;
                Info[index].AtkUnitBase = Children + index;
                Info[index].TabIndex = index;
                Children[index].Id = (ushort)(100 + index);
                Children[index].NameString = $"SyntheticChild{index}";
                Children[index].Flags1A1 = 1; Children[index].IsVisible = true; Children[index].Alpha = 255;
                Children[index].RootNode = Roots + index; Roots[index].NodeFlags = NodeFlags.Visible;
            }
        }
        internal nint Resolve(ushort id) => id >= 100 && id < 100 + count ? (nint)(Children + id - 100) : 0;
        internal bool Observe(out GoldSaucerChildObservation result) => GoldSaucerChildDiagnostic.TryObserve(Parent, Resolve, out result);
        public void Dispose()
        { NativeMemory.Free(Parent); NativeMemory.Free(Head); NativeMemory.Free(Nodes); NativeMemory.Free(Info); NativeMemory.Free(Children); NativeMemory.Free(Roots); }
    }
}
