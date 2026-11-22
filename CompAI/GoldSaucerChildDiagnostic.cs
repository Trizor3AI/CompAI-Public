using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ChildNode = FFXIVClientStructs.STD.ContainerInterface.IStdList<FFXIVClientStructs.Interop.Pointer<FFXIVClientStructs.FFXIV.Component.GUI.AtkAddonControl.ChildAddonInfo>>.Node;

namespace CompAI;

internal sealed record GoldSaucerChildMetadata(int Ordinal, string? Name, ushort Id, ushort ParentId,
    ushort HostId, int RawTabIndex, bool Ready, bool Visible);

// Never serialize this object: the caller explicitly projects Status/Children and the copied text.
internal sealed record GoldSaucerChildObservation(nint TextAddon, string? TextAddonName, string Status,
    IReadOnlyList<GoldSaucerChildMetadata> Children)
{
    internal nint Parent { get; init; }
    internal ushort ParentId { get; init; }
    internal int SelectedCategory { get; init; }
    internal int SelectedSubCategory { get; init; }
    internal nint Head { get; init; }
    internal ulong Count { get; init; }
    internal IReadOnlyList<GoldSaucerChildNative> Native { get; init; } = Array.Empty<GoldSaucerChildNative>();
}

internal readonly record struct GoldSaucerChildNative(nint Node, nint Previous, nint Next, nint Info,
    nint Addon, nint Root, nint NodeList, uint NodeCount, int Tab, short X, short Y, byte Flags1, byte Flags2,
    byte Alpha, bool? RootVisible);

/// <summary>One manual Mahjong diagnostic: bounded direct ownership only, no recursive addon discovery or native mutation.</summary>
internal static unsafe class GoldSaucerChildDiagnostic
{
    internal const int MaxChildren = 16;

    // Pinned dd57986b AtkAddonControl exposes ChildAddons, ParentAddon and the setup flags.
    // ChildAddonInfo.TabIndex is documented using chat tabs as an example; it is NOT a verified Mahjong index.
    // StdList.First/Last may allocate a sentinel. Read only WithOps.Head/Size and explicit bounded links.
    internal static bool TryObserve(AddonGoldSaucerInfo* parent, Func<ushort, nint> resolveAddon,
        out GoldSaucerChildObservation observation)
    {
        observation = Empty("invalid_child_ownership");
        if (parent == null || parent->Id == 0 || parent->SelectedCategory != 5 || !parent->IsReady || !parent->IsVisible)
            return false;
        var control = &parent->AddonControl;
        if (control->ParentAddon != (AtkUnitBase*)parent || !control->IsParentAddonLinked || !control->IsChildSetupComplete)
            return false;
        var parentId = parent->Id;
        var category = parent->SelectedCategory;
        var subCategory = parent->SelectedSubCategory;
        var head = control->ChildAddons.WithOps.Head;
        var count = control->ChildAddons.WithOps.Size;
        observation = Empty("invalid_child_list");
        if (count > MaxChildren || count > 0 && !Address((nuint)head) || head != null && !Address((nuint)head)) return false;
        var nodes = new List<GoldSaucerChildNative>((int)count);
        if (head == null)
        {
            if (count != 0) return false;
        }
        else
        {
            var previous = head;
            var current = head->Next;
            var visited = new HashSet<nint> { (nint)head };
            for (var index = 0; index < (int)count; ++index)
            {
                if (!Address((nuint)current) || !visited.Add((nint)current) || current->Previous != previous ||
                    !Address((nuint)current->Next)) return false;
                nodes.Add(new((nint)current, (nint)current->Previous, (nint)current->Next, (nint)current->Value.Value,
                    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false));
                previous = current;
                current = current->Next;
            }
            // Exact termination rules out short, cyclic, oversized and partially linked lists.
            if (current != head || head->Previous != previous) return false;
        }
        var children = new List<GoldSaucerChildMetadata>((int)count);
        var infos = new HashSet<nint>();
        var addons = new HashSet<nint>();
        var visible = new List<int>();
        var unsettled = false;
        for (var index = 0; index < nodes.Count; ++index)
        {
            var node = nodes[index];
            if (!infos.Add(node.Info) || !ReadChild(node, index, resolveAddon, out var child, out var native)) return false;
            if (native.Addon == (nint)parent || native.Addon != 0 && !addons.Add(native.Addon)) return false;
            children.Add(child);
            nodes[index] = native;
            if (child.Visible)
            {
                if (child.Ready && native.RootVisible == true) visible.Add(index);
                else unsettled = true;
            }
        }
        // Resolve registry membership again and re-read every link/identity before permitting any child text.
        for (var index = 0; index < nodes.Count; ++index)
        {
            if (!ReadChild(nodes[index], index, resolveAddon, out var child, out var native) ||
                child != children[index] || native != nodes[index]) return false;
        }
        if (parent->Id != parentId || parent->SelectedCategory != category || parent->SelectedSubCategory != subCategory ||
            !parent->IsReady || !parent->IsVisible || control->ParentAddon != (AtkUnitBase*)parent ||
            !control->IsParentAddonLinked || !control->IsChildSetupComplete ||
            control->ChildAddons.WithOps.Head != head || control->ChildAddons.WithOps.Size != count ||
            head != null && (head->Next != (nodes.Count == 0 ? head : (ChildNode*)nodes[0].Node) ||
                head->Previous != (nodes.Count == 0 ? head : (ChildNode*)nodes[^1].Node))) return false;
        var chosen = !unsettled && visible.Count == 1 ? visible[0] : -1;
        observation = new(chosen < 0 ? 0 : nodes[chosen].Addon, chosen < 0 ? null : children[chosen].Name,
            unsettled ? "visible_child_not_ready" : visible.Count == 0 ? "no_visible_owned_child" :
            visible.Count > 1 ? "multiple_visible_owned_children" : "one_visible_owned_child",
            new ReadOnlyCollection<GoldSaucerChildMetadata>(children.ToArray()))
        {
            Parent = (nint)parent, ParentId = parentId, SelectedCategory = category, SelectedSubCategory = subCategory,
            Head = (nint)head, Count = count, Native = new ReadOnlyCollection<GoldSaucerChildNative>(nodes.ToArray()),
        };
        return true;
    }

    private static bool ReadChild(GoldSaucerChildNative expected, int ordinal, Func<ushort, nint> resolveAddon,
        out GoldSaucerChildMetadata child, out GoldSaucerChildNative native)
    {
        child = new(ordinal, null, 0, 0, 0, 0, false, false);
        native = expected;
        var node = (ChildNode*)expected.Node;
        if ((nint)node->Previous != expected.Previous || (nint)node->Next != expected.Next ||
            (nint)node->Value.Value != expected.Info || !Address((nuint)expected.Info)) return false;
        var info = (AtkAddonControl.ChildAddonInfo*)expected.Info;
        var addon = info->AtkUnitBase;
        native = expected with { Addon = (nint)addon, Tab = info->TabIndex, X = info->PositionX, Y = info->PositionY,
            Flags1 = info->Flags1, Flags2 = info->Flags2 };
        if (addon == null) { child = child with { RawTabIndex = info->TabIndex }; return true; }
        if (!Address((nuint)addon) || addon->Id == 0 || resolveAddon(addon->Id) != (nint)addon ||
            !TryName(addon, out var name)) return false;
        var root = addon->RootNode;
        var rootVisible = root != null && Address((nuint)root) ? VisibleRoot(root) : null;
        native = native with { Root = (nint)root, NodeList = (nint)addon->UldManager.NodeList,
            NodeCount = addon->UldManager.NodeListCount, Alpha = addon->Alpha, RootVisible = rootVisible };
        child = new(ordinal, name, addon->Id, addon->ParentId, addon->HostId, info->TabIndex, addon->IsReady,
            addon->IsVisible && addon->Alpha > 0 && rootVisible != false);
        return true;
    }

    private static bool TryName(AtkUnitBase* addon, out string name)
    {
        name = string.Empty;
        var bytes = addon->Name;
        var end = bytes.IndexOf((byte)0);
        if (bytes.Length != 32 || end is <= 0 or > 31) return false;
        for (var index = 0; index < end; ++index)
            if (bytes[index] is not (>= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or
                >= (byte)'0' and <= (byte)'9' or (byte)'_')) return false;
        name = Encoding.ASCII.GetString(bytes[..end]);
        return true;
    }

    private static bool? VisibleRoot(AtkResNode* root)
    {
        var node = root;
        for (var depth = 0; node != null && depth < 32; ++depth)
        {
            if (!Address((nuint)node)) return null;
            if ((node->NodeFlags & NodeFlags.Visible) == 0 || node->IsDrawDisabled) return false;
            node = node->ParentNode;
        }
        return node == null ? true : null;
    }
    private static bool Address(nuint value) => value >= 65536 && value % (nuint)IntPtr.Size == 0;
    private static GoldSaucerChildObservation Empty(string status) => new(0, null, status, Array.Empty<GoldSaucerChildMetadata>());
    internal static bool Same(GoldSaucerChildObservation? first, GoldSaucerChildObservation? second) =>
        first is not null && second is not null && first.TextAddon == second.TextAddon && first.TextAddonName == second.TextAddonName &&
        first.Status == second.Status && first.Parent == second.Parent && first.ParentId == second.ParentId &&
        first.SelectedCategory == second.SelectedCategory && first.SelectedSubCategory == second.SelectedSubCategory &&
        first.Head == second.Head && first.Count == second.Count && first.Children.SequenceEqual(second.Children) &&
        first.Native.SequenceEqual(second.Native);
}
