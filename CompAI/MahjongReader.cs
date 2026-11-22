using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Text.ReadOnly;

namespace CompAI;

/// <summary>Framework-thread copy of six typed fields from the exact owned Mahjong panel.</summary>
internal sealed unsafe class MahjongReader(IGameGui gui)
{
    internal const int MaxNodes = 256;
    internal const int MaxTextBytes = 512;
    private const int MaxAncestors = 32;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    // Typed names come from installed dd57986b. The 0.45.1 typed-field diagnostic
    // and screenshot distinguish Points (node9) from NextRankPoints (node11).
    // IDs are an additional observed-layout gate, not field discovery.
    // https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/FFXIV/Client/UI/AddonGSInfoEmj.cs
    private static readonly uint[] NodeIds = [7, 9, 11, 16, 23, 24];
    private static readonly uint[] ParentIds = [4, 4, 4, 13, 18, 18];

    internal ActivitySnapshot Capture() => Capture(out _);

    // Explicit human diagnostics use the same capture and guards as normal reads.
    // Only the fixed projection below escapes; no display strings or native tokens.
    internal object CaptureReadDiagnostic()
    {
        _ = Capture(out var diagnostic);
        return diagnostic;
    }

    private ActivitySnapshot Capture(out MahjongReadDiagnostic diagnostic) => CaptureCore(Observe,
        address =>
        {
            var accepted = TryCopyPanel((AddonGSInfoEmj*)address, out var panel, out var result);
            return (accepted ? panel : null, result);
        }, out diagnostic);

    internal static ActivitySnapshot CaptureCore(
        Func<(MahjongObservation? Observation, MahjongReadDiagnostic Diagnostic)> observe,
        Func<nint, (MahjongPanelCopy? Panel, MahjongReadDiagnostic Diagnostic)> copy,
        out MahjongReadDiagnostic diagnostic)
    {
        diagnostic = Diagnostic("capture_exception");
        try
        {
            var initial = observe();
            var before = initial.Observation;
            diagnostic = initial.Diagnostic;
            if (before is null) return Unavailable();
            var first = copy(before.Child.TextAddon);
            diagnostic = first.Diagnostic;
            if (first.Panel is null) return Unavailable();
            var middle = observe();
            if (!SameObservation(before, middle.Observation))
            { diagnostic = middle.Observation is null ? middle.Diagnostic : Diagnostic("owner_changed_after_first_copy"); return Unavailable(); }
            var second = copy(before.Child.TextAddon);
            diagnostic = second.Diagnostic;
            if (second.Panel is null) return Unavailable();
            if (!SamePanel(first.Panel, second.Panel))
            { diagnostic = Diagnostic("panel_changed_between_copies", count: first.Panel.Header.NodeCount); return Unavailable(); }
            var after = observe();
            if (!SameObservation(before, after.Observation))
            { diagnostic = after.Observation is null ? after.Diagnostic : Diagnostic("owner_changed_after_second_copy"); return Unavailable(); }
            var result = Describe(DateTimeOffset.UtcNow, first.Panel.Display, second.Panel.Display);
            diagnostic = Diagnostic(result.Available ? "ready" : "projection_rejected", count: first.Panel.Header.NodeCount);
            return result;
        }
        catch (Exception) { diagnostic = Diagnostic("capture_exception"); return Unavailable(); }
    }

    private (MahjongObservation? Observation, MahjongReadDiagnostic Diagnostic) Observe()
    {
        var player = PlayerState.Instance();
        var module = AgentModule.Instance();
        var manager = RaptureAtkUnitManager.Instance();
        var agent = AgentGoldSaucer.Instance();
        if (player == null || !player->IsLoaded || player->ContentId == 0 || module == null || manager == null ||
            agent == null || (nint)module->GetAgentByInternalId(AgentId.GoldSaucer) != (nint)agent ||
            agent->AddonId is 0 or > ushort.MaxValue || !agent->IsAgentActive() || !agent->IsAddonReady() ||
            !agent->IsAddonShown()) return (null, Diagnostic("owner_unavailable"));
        var parent = manager->GetAddonById((ushort)agent->AddonId);
        var handle = gui.GetAddonByName("GoldSaucerInfo");
        if (parent == null || parent->NameString != "GoldSaucerInfo" || handle.IsNull ||
            handle.Address != (nint)parent || handle.Id != agent->AddonId || !handle.IsReady || !handle.IsVisible ||
            parent->Alpha == 0) return (null, Diagnostic("parent_addon_unavailable"));
        var info = (AddonGoldSaucerInfo*)parent;
        if (info->CategoryRadioButtons.Length != 6) return (null, Diagnostic("parent_layout_unsupported"));
        // Do not inspect any other Gold Saucer tab for this manual diagnostic.
        if (info->SelectedCategory != 5 || agent->GoldSaucerSelectedTab != 5) return (null, Diagnostic("mahjong_tab_not_selected"));
        if (!GoldSaucerChildDiagnostic.TryObserve(info, id => (nint)manager->GetAddonById(id), out var child))
            return (null, Diagnostic("owned_child_rejected"));
        if (child.Status != "one_visible_owned_child" || child.TextAddon == 0 || child.TextAddonName != "GSInfoEmj")
            return (null, Diagnostic("owned_mahjong_panel_unavailable"));
        var childHandle = gui.GetAddonByName("GSInfoEmj");
        if (childHandle.IsNull || childHandle.Address != child.TextAddon || !childHandle.IsReady || !childHandle.IsVisible ||
            child.Children.Count(value => value.Name == "GSInfoEmj" && value.Id == childHandle.Id && value.Ready && value.Visible) != 1)
            return (null, Diagnostic("child_name_handle_mismatch"));
        return (new((nint)player, player->ContentId, (nint)module, (nint)manager, (nint)agent,
            (nint)parent, parent->Id, info->SelectedCategory, info->SelectedSubCategory, childHandle.Id, child), Diagnostic("owner_ready"));
    }

    internal static bool SameObservation(MahjongObservation? first, MahjongObservation? second) =>
        first is not null && second is not null && (first with { Child = null! }) == (second with { Child = null! }) &&
        GoldSaucerChildDiagnostic.Same(first.Child, second.Child);

    internal static ActivitySnapshot Describe(DateTimeOffset time, MahjongDisplay? first, MahjongDisplay? second)
    {
        if (first is null || first != second) return Unavailable();
        var values = new[] { first.Rank, first.Points, first.NextRank, first.MatchesPlayed, first.CurrentRating, first.HighestRating };
        if (values.Any(value => !ActivitySnapshot.ValidText(value, MahjongContract.MaxTextLength))) return Unavailable();
        return new(MahjongContract.Category, time, true, values.Select((value, index) =>
            new ActivityEntry((uint)index + 1, "statistics", MahjongContract.Name((uint)index + 1)!, Text: value)));
    }

    // Only top-level owned nodes are followed. Every typed pointer and ancestor must
    // already belong to this child's bounded node list; external pointers are rejected.
    internal static bool TryCopyPanel(AddonGSInfoEmj* addon, out MahjongPanelCopy? copy) => TryCopyPanel(addon, out copy, out _);

    internal static bool TryCopyPanel(AddonGSInfoEmj* addon, out MahjongPanelCopy? copy, out MahjongReadDiagnostic diagnostic)
    {
        copy = null;
        diagnostic = Diagnostic("panel_header_unavailable");
        if (addon == null || addon->NameString != "GSInfoEmj" || addon->Id == 0 || !addon->IsReady || !addon->IsVisible ||
            addon->Alpha == 0 || !Address((nuint)addon->RootNode)) return false;
        var header = Header(addon);
        if (header.NodeCount is < 1 or > MaxNodes || !Address((nuint)header.NodeList))
            return Fail(out diagnostic, "node_list_bounds", count: header.NodeCount);
        var list = (AtkResNode**)header.NodeList;
        var addresses = new nint[header.NodeCount];
        var owned = new HashSet<nint>();
        for (var index = 0; index < addresses.Length; ++index)
        {
            var address = (nint)list[index];
            addresses[index] = address;
            if (address != 0 && !Address((nuint)address)) return Fail(out diagnostic, "invalid_node_address", count: header.NodeCount);
            if (address != 0 && !owned.Add(address)) return Fail(out diagnostic, "duplicate_node_pointer", count: header.NodeCount);
        }
        if (!owned.Contains(header.Root)) return Fail(out diagnostic, "root_not_owned", count: header.NodeCount);
        var fields = Fields(addon);
        for (var index = 0; index < fields.Length; ++index)
            if (!owned.Contains(fields[index]) || fields[index] == header.Root)
                return Fail(out diagnostic, "typed_field_not_owned", index, header.NodeCount);
        if (fields.Distinct().Count() != 6) return Fail(out diagnostic, "typed_fields_aliased", count: header.NodeCount);
        var observedNodes = new Dictionary<nint, MahjongNodeCopy>();
        var texts = new string[6];
        var textCopies = new MahjongTextCopy[6];
        for (var index = 0; index < fields.Length; ++index)
        {
            var node = (AtkTextNode*)fields[index];
            if (!VisibleOwned((AtkResNode*)node, header.Root, owned, observedNodes, out var status))
                return Fail(out diagnostic, status, index, header.NodeCount);
            if (node->Type != NodeType.Text) return Fail(out diagnostic, "field_type_mismatch", index, header.NodeCount);
            if (node->NodeId != NodeIds[index]) return Fail(out diagnostic, "field_node_id_mismatch", index, header.NodeCount);
            if (node->ParentNode == null || node->ParentNode->NodeId != ParentIds[index])
                return Fail(out diagnostic, "field_parent_mismatch", index, header.NodeCount);
            if (owned.Count(address => ((AtkResNode*)address)->NodeId == NodeIds[index]) != 1)
                return Fail(out diagnostic, "duplicate_field_node_id", index, header.NodeCount);
            if (!ReadText(node, out texts[index], out textCopies[index], out status))
                return Fail(out diagnostic, status, index, header.NodeCount);
        }
        if (header != Header(addon)) return Fail(out diagnostic, "panel_header_changed", count: header.NodeCount);
        if (!fields.SequenceEqual(Fields(addon))) return Fail(out diagnostic, "typed_fields_changed", count: header.NodeCount);
        for (var index = 0; index < addresses.Length; ++index)
            if ((nint)list[index] != addresses[index]) return Fail(out diagnostic, "node_list_changed", count: header.NodeCount);
        foreach (var pair in observedNodes)
            if (Node((AtkResNode*)pair.Key) != pair.Value) return Fail(out diagnostic, "ancestor_changed", count: header.NodeCount);
        for (var index = 0; index < fields.Length; ++index)
        {
            if (!ReadText((AtkTextNode*)fields[index], out var text, out var repeated, out var status))
                return Fail(out diagnostic, status, index, header.NodeCount);
            if (text != texts[index] || repeated != textCopies[index]) return Fail(out diagnostic, "field_text_changed", index, header.NodeCount);
        }
        if (header != Header(addon)) return Fail(out diagnostic, "panel_header_changed", count: header.NodeCount);
        if (!fields.SequenceEqual(Fields(addon))) return Fail(out diagnostic, "typed_fields_changed", count: header.NodeCount);
        for (var index = 0; index < addresses.Length; ++index)
            if ((nint)list[index] != addresses[index]) return Fail(out diagnostic, "node_list_changed", count: header.NodeCount);
        foreach (var pair in observedNodes)
            if (Node((AtkResNode*)pair.Key) != pair.Value) return Fail(out diagnostic, "ancestor_changed", count: header.NodeCount);
        copy = new(header, addresses, fields, observedNodes.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray(),
            textCopies, new(texts[0], texts[1], texts[2], texts[3], texts[4], texts[5]));
        diagnostic = Diagnostic("panel_ready", count: header.NodeCount);
        return true;
    }

    private static MahjongPanelHeader Header(AddonGSInfoEmj* addon) => new((nint)addon, addon->Id,
        (nint)addon->RootNode, (nint)addon->UldManager.NodeList, addon->UldManager.NodeListCount,
        addon->IsReady, addon->IsVisible, addon->Alpha);
    private static nint[] Fields(AddonGSInfoEmj* addon) => [(nint)addon->Rank, (nint)addon->Points,
        (nint)addon->NextRankPoints, (nint)addon->MatchesPlayed, (nint)addon->CurrentRating, (nint)addon->HighestRating];
    private static MahjongNodeCopy Node(AtkResNode* node) => new((nint)node, node->NodeId, (nint)node->ParentNode,
        node->Type, node->NodeFlags, node->Color.A, node->IsDrawDisabled);

    private static bool VisibleOwned(AtkResNode* node, nint root, HashSet<nint> owned,
        Dictionary<nint, MahjongNodeCopy> observed, out string status)
    {
        status = "root_not_reached";
        var visited = new HashSet<nint>();
        for (var depth = 0; depth < MaxAncestors && node != null; ++depth)
        {
            var address = (nint)node;
            if (!owned.Contains(address)) { status = "ancestor_not_owned"; return false; }
            if (!visited.Add(address)) { status = "ancestor_cycle"; return false; }
            var current = Node(node);
            if ((current.Flags & NodeFlags.Visible) == 0) { status = "field_or_ancestor_hidden"; return false; }
            if (current.Alpha == 0) { status = "field_or_ancestor_transparent"; return false; }
            if (current.DrawDisabled) { status = "field_or_ancestor_draw_disabled"; return false; }
            if (observed.TryGetValue(address, out var previous) && previous != current) { status = "ancestor_changed"; return false; }
            observed[address] = current;
            if (address == root) return true;
            node = node->ParentNode;
        }
        return false;
    }

    private static bool ReadText(AtkTextNode* node, out string value, out MahjongTextCopy copy, out string status)
    {
        value = string.Empty; copy = default;
        status = "text_buffer_bounds";
        var buffer = (byte*)node->NodeText.StringPtr;
        var used = node->NodeText.BufUsed;
        var size = node->NodeText.BufSize;
        if ((nuint)buffer < 65536 || used is < 2 or > MaxTextBytes + 1 || size < used) return false;
        var bytes = new ReadOnlySpan<byte>(buffer, (int)used).ToArray();
        if (bytes[^1] != 0) { status = "text_terminator_invalid"; return false; }
        if (bytes.AsSpan(0, bytes.Length - 1).Contains((byte)0)) { status = "text_embedded_null"; return false; }
        status = "text_payload_invalid";
        try
        {
            var content = bytes.AsSpan(0, bytes.Length - 1);
            foreach (var payload in new ReadOnlySeStringSpan(content))
            {
                if (payload.Type == ReadOnlySePayloadType.Invalid) return false;
                if (payload.Type == ReadOnlySePayloadType.Text) _ = StrictUtf8.GetCharCount(payload.Body);
                else if (!payload.Validate()) return false;
            }
            value = SeString.Parse(content.ToArray()).TextValue.Trim();
        }
        catch (Exception) { status = "text_decode_failed"; return false; }
        if (!ActivitySnapshot.ValidText(value, MahjongContract.MaxTextLength)) { status = "text_value_invalid"; return false; }
        if ((byte*)node->NodeText.StringPtr != buffer || node->NodeText.BufUsed != used || node->NodeText.BufSize != size)
        { status = "text_buffer_changed"; return false; }
        copy = new((nint)buffer, used, size, Convert.ToHexString(bytes));
        return true;
    }

    internal static bool SamePanel(MahjongPanelCopy? first, MahjongPanelCopy? second) => first is not null && second is not null &&
        first.Header == second.Header && first.Display == second.Display && first.NodeList.SequenceEqual(second.NodeList) &&
        first.Fields.SequenceEqual(second.Fields) && first.Nodes.SequenceEqual(second.Nodes) && first.Texts.SequenceEqual(second.Texts);
    private static bool Fail(out MahjongReadDiagnostic diagnostic, string status, int? field = null, uint? count = null)
    { diagnostic = Diagnostic(status, field, count); return false; }
    private static MahjongReadDiagnostic Diagnostic(string status, int? field = null, uint? count = null) =>
        new("compai_mahjong_reader_v1", MahjongContract.Category, status, field switch
        { 0 => "rank", 1 => "points", 2 => "next_rank", 3 => "matches_played", 4 => "current_rating", 5 => "highest_rating", _ => null },
            count is <= MaxNodes ? (int?)count : null);
    private static bool Address(nuint value) => value >= 65536 && value % (nuint)IntPtr.Size == 0;
    private static ActivitySnapshot Unavailable() => ActivitySnapshot.Unavailable(MahjongContract.Category,
        "Open Gold Saucer's Mahjong tab yourself and let it load. A stable owned statistics panel is unavailable; missing values are unknown.");
}

internal sealed record MahjongReadDiagnostic(string schema, string category, string status, string? field, int? node_count);
internal sealed record MahjongDisplay(string Rank, string Points, string NextRank, string MatchesPlayed, string CurrentRating, string HighestRating);
// Private capture tokens only; none of these native identities reach ActivitySnapshot.
internal sealed record MahjongObservation(nint Player, ulong Owner, nint Module, nint Manager, nint Agent,
    nint Parent, ushort ParentId, int Category, int SubCategory, ushort ChildId, GoldSaucerChildObservation Child);
internal readonly record struct MahjongPanelHeader(nint Addon, ushort Id, nint Root, nint NodeList, uint NodeCount, bool Ready, bool Visible, byte Alpha);
internal readonly record struct MahjongNodeCopy(nint Address, uint Id, nint Parent, NodeType Type, NodeFlags Flags, byte Alpha, bool DrawDisabled);
internal readonly record struct MahjongTextCopy(nint Buffer, long Used, long Size, string Raw);
internal sealed record MahjongPanelCopy(MahjongPanelHeader Header, nint[] NodeList, nint[] Fields,
    MahjongNodeCopy[] Nodes, MahjongTextCopy[] Texts, MahjongDisplay Display);
