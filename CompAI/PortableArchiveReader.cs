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
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

namespace CompAI;

/// <summary>Copies one corroborated archive selection on the framework thread. Caller owns permission/session gates.</summary>
internal sealed unsafe class PortableArchiveReader(IDataManager data, IGameGui gui)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private PortableArchiveCatalog? catalog;

    internal ActivitySnapshot? Capture() => CaptureCore(out _, out _);
    internal ActivitySnapshot? Capture(out bool menuObserved) => CaptureCore(out menuObserved, out _);

    // Human-triggered diagnostics execute the identical capture path. Only fixed
    // stage/field/reason labels escape; copied text and native identities do not.
    internal PortableArchiveReadDiagnostic CaptureReadDiagnostic()
    {
        _ = CaptureCore(out _, out var diagnostic);
        return diagnostic;
    }

    private ActivitySnapshot? CaptureCore(out bool menuObserved, out PortableArchiveReadDiagnostic diagnostic)
    {
        menuObserved = false;
        var trace = new ReadTrace();
        ActivitySnapshot? result = null;
        try
        {
            trace.Mark("owner", "initial");
            var before = Observe(trace);
            if (before is null) trace.Fallback("owner_unavailable");
            else
            {
                // A visible owned viewer must invalidate an earlier selected entry
                // even when its language, catalog or current title cannot be read.
                menuObserved = true;
                trace.Mark("language");
                if (data.Language.ToString() != "English") trace.Fail("unsupported_language");
                else
                {
                    trace.Mark("catalog");
                    var current = GetCatalog(trace);
                    var rows = CaptureRowsCore(() => Observe(trace), address => CopyViewCore((AtkUnitBase*)address, trace),
                        current.Definitions, () => CatalogMatches(current), trace, before);
                    if (rows is not null)
                    {
                        trace.Mark("snapshot");
                        result = new(PortableArchiveContract.Category, DateTimeOffset.UtcNow, true, rows);
                        trace.Mark("complete", reason: "passed");
                    }
                }
            }
        }
        catch (Exception) { trace.Fallback("read_error"); }
        diagnostic = trace.Report(result is not null);
        return result;
    }

    private PortableArchiveOwner? Observe(ReadTrace trace)
    {
        var player = PlayerState.Instance();
        var module = AgentModule.Instance();
        var manager = RaptureAtkUnitManager.Instance();
        if (player == null || !player->IsLoaded || player->ContentId == 0 || module == null || manager == null)
        { trace.Fail("character_or_modules_unavailable"); return null; }
        var agent = module->GetAgentByInternalId(AgentId.Description);
        if (agent == null || agent->AddonId is 0 or > ushort.MaxValue || !agent->IsAgentActive() ||
            !agent->IsAddonReady() || !agent->IsAddonShown())
        { trace.Fail("agent_not_ready"); return null; }
        var addon = manager->GetAddonById((ushort)agent->AddonId);
        // Description is shared by other viewers; its agent alone is insufficient.
        // Only DescriptionYTC was observed with the Archive's exact selected content.
        if (addon == null || addon->NameString != "DescriptionYTC")
        { trace.Fail("owned_addon_mismatch"); return null; }
        var handle = gui.GetAddonByName("DescriptionYTC");
        if (handle.IsNull || handle.Address != (nint)addon || handle.Id != agent->AddonId || !handle.IsReady || !handle.IsVisible)
        { trace.Fail("visible_handle_mismatch"); return null; }
        return new((nint)player, player->ContentId, (nint)module, (nint)manager, (nint)agent, (nint)addon, agent->AddonId);
    }

    internal static ActivityEntry[]? CaptureRows(Func<PortableArchiveOwner?> observe, Func<nint, PortableArchiveView?> copy,
        PortableArchiveDefinition[] definitions, Func<bool> catalogMatches) => CaptureRows(observe, copy, definitions, catalogMatches, out _);

    internal static ActivityEntry[]? CaptureRows(Func<PortableArchiveOwner?> observe, Func<nint, PortableArchiveView?> copy,
        PortableArchiveDefinition[] definitions, Func<bool> catalogMatches, out PortableArchiveReadDiagnostic diagnostic,
        PortableArchiveOwner? initialOwner = null)
    {
        var trace = new ReadTrace();
        var rows = CaptureRowsCore(observe, copy, definitions, catalogMatches, trace, initialOwner);
        diagnostic = trace.Report(rows is not null);
        return rows;
    }

    private static ActivityEntry[]? CaptureRowsCore(Func<PortableArchiveOwner?> observe, Func<nint, PortableArchiveView?> copy,
        PortableArchiveDefinition[] definitions, Func<bool> catalogMatches, ReadTrace trace, PortableArchiveOwner? initialOwner = null)
    {
        trace.Mark("owner", "initial");
        var before = initialOwner ?? observe();
        if (before is null) { trace.Fallback("owner_unavailable"); return null; }
        if (initialOwner is not null)
        {
            // Catalog loading can outlive the initially observed viewer. Verify
            // its ownership again before dereferencing the retained addon handle.
            trace.Mark("stability", "owner_before_first_copy");
            if (before != observe()) { trace.Fallback("owner_changed"); return null; }
        }
        trace.Mark("copy", "first");
        var first = copy(before.Value.Addon);
        if (first is null) { trace.Fallback("copy_unavailable"); return null; }
        trace.Mark("stability", "owner_after_first_copy");
        if (before != observe()) { trace.Fallback("owner_changed"); return null; }
        trace.Mark("match");
        var rows = DescribeRows(first, definitions);
        if (rows is null) { trace.Fail("selection_not_corroborated"); return null; }
        trace.Mark("copy", "second");
        var second = copy(before.Value.Addon);
        if (second is null) { trace.Fallback("copy_unavailable"); return null; }
        if (first != second) { trace.Mark("stability", "selection", "selection_changed"); return null; }
        trace.Mark("stability", "owner_after_second_copy");
        if (before != observe()) { trace.Fallback("owner_changed"); return null; }
        trace.Mark("stability", "catalog");
        if (!catalogMatches()) { trace.Fail("catalog_changed"); return null; }
        trace.Mark("complete", reason: "passed");
        return rows;
    }

    // Supplied captures corroborate the full selected title at [30,2]/3.
    // Neither component 26 (recycled list) nor the body component is traversed.
    internal static PortableArchiveView? CopyView(AtkUnitBase* addon) => CopyView(addon, out _);

    internal static PortableArchiveView? CopyView(AtkUnitBase* addon, out PortableArchiveReadDiagnostic diagnostic)
    {
        var trace = new ReadTrace();
        var view = CopyViewCore(addon, trace);
        diagnostic = trace.Report(view is not null);
        return view;
    }

    private static PortableArchiveView? CopyViewCore(AtkUnitBase* addon, ReadTrace trace)
    {
        trace.Mark("copy", "root_manager");
        if (addon == null) { trace.Fail("addon_missing"); return null; }
        var state = new CopyState(trace);
        if (!Find(&addon->UldManager, [14, 30, 32], state, out var root)) return null;
        trace.Mark("copy", "detail_manager");
        if (!FindComponent(root.GetValueOrDefault(30u), [2], state, out var detail)) return null;
        trace.Mark("copy", "title_manager");
        if (!FindComponent(detail.GetValueOrDefault(2u), [3], state, out var title)) return null;
        trace.Mark("copy", "heading");
        if (!ReadText(root.GetValueOrDefault(14u), 6, 64, state, out var heading)) return null;
        if (heading != "Data") { trace.Fail("label_mismatch"); return null; }
        trace.Mark("copy", "hint");
        if (!ReadText(root.GetValueOrDefault(32u), 31, 64, state, out var hint)) return null;
        if (hint != "View recorded data.") { trace.Fail("label_mismatch"); return null; }
        trace.Mark("copy", "title");
        if (!ReadText(title.GetValueOrDefault(3u), 2, 256, state, out var titleText)) return null;
        trace.Mark("visibility", "components");
        if (!state.Components.All(address => Visible((AtkResNode*)address, state))) return null;
        trace.Mark("stability", "node_lists");
        if (!state.Stable()) { trace.Fail("node_list_changed"); return null; }
        trace.Mark("stability", "text_buffers");
        if (!state.Texts.All(TextBufferMatches)) { trace.Fail("text_buffer_changed"); return null; }
        trace.Mark("copy", reason: "passed");
        return new(titleText!);
    }

    private static bool FindComponent(nint address, uint[] ids, CopyState state, out Dictionary<uint, nint> nodes)
    {
        nodes = [];
        var node = (AtkComponentNode*)address;
        if (node == null) return state.Fail("component_missing");
        if ((ushort)node->Type < 1000) return state.Fail("component_type");
        if (node->Component == null) return state.Fail("component_pointer");
        state.Components.Add(address);
        return Find(&node->Component->UldManager, ids, state, out nodes);
    }

    private static bool Find(AtkUldManager* manager, uint[] ids, CopyState state, out Dictionary<uint, nint> nodes)
    {
        nodes = [];
        if (manager == null) return state.Fail("manager_missing");
        if (manager->NodeListCount is 0 or > 512) return state.Fail("node_list_bounds");
        if (manager->NodeList == null) return state.Fail("node_list_pointer");
        var count = manager->NodeListCount; var list = manager->NodeList;
        if (state.Managers.Any(value => value.Manager == (nint)manager)) return state.Fail("manager_repeated");
        state.Managers.Add(new((nint)manager, (nint)list, count));
        for (var index = 0; index < count; ++index)
        {
            var node = list[index];
            if (node == null) continue;
            state.Owned.Add((nint)node);
            if (ids.Contains(node->NodeId) && !nodes.TryAdd(node->NodeId, (nint)node)) return state.Fail("node_duplicate");
        }
        if (!ids.All(nodes.ContainsKey)) return state.Fail("node_missing");
        return manager->NodeList == list && manager->NodeListCount == count || state.Fail("node_list_changed");
    }

    private static bool Visible(AtkResNode* node, CopyState state)
    {
        for (var depth = 0; node != null && depth < 32; ++depth, node = node->ParentNode)
        {
            if (!state.Owned.Contains((nint)node)) return state.Fail("parent_chain_unowned");
            if ((node->NodeFlags & NodeFlags.Visible) == 0 || node->Color.A == 0) return state.Fail("node_hidden");
        }
        return node == null || state.Fail("parent_depth_limit");
    }

    private static bool ReadText(nint address, uint parentId, int maximum, CopyState state, out string? value)
    {
        value = null;
        var node = (AtkResNode*)address;
        if (node == null) return state.Fail("text_node_missing");
        if (node->Type != NodeType.Text) return state.Fail("text_node_type");
        if (node->ParentNode == null) return state.Fail("parent_missing");
        if (!state.Owned.Contains((nint)node->ParentNode)) return state.Fail("parent_unowned");
        if (node->ParentNode->NodeId != parentId) return state.Fail("parent_id_mismatch");
        if (!Visible(node, state)) return false;
        var text = (AtkTextNode*)node;
        var buffer = (byte*)text->NodeText.StringPtr;
        var used = text->NodeText.BufUsed;
        var size = text->NodeText.BufSize;
        if (used < 2 || used > maximum + 1) return state.Fail("text_buffer_bounds");
        if (size < used) return state.Fail("text_capacity_metadata");
        if (buffer == null) return state.Fail("text_buffer_pointer");
        // Installed Utf8String.Length is Max(0, BufUsed - 1), and AsSpan uses
        // that length. StringLength is not used by either getter and rejected
        // the live, otherwise valid Data heading. Bound the actual byte buffer.
        var bytes = new ReadOnlySpan<byte>(buffer, (int)used).ToArray();
        var copy = new PortableArchiveTextBuffer(address, (nint)buffer, used, size, bytes);
        var content = bytes.AsSpan(0, bytes.Length - 1);
        if (bytes[^1] != 0 || content.Contains((byte)0)) return state.Fail("text_terminator");
        try
        {
            foreach (var payload in new ReadOnlySeStringSpan(content))
            {
                if (payload.Type == ReadOnlySePayloadType.Invalid) return state.Fail("text_payload_invalid");
                if (payload.Type == ReadOnlySePayloadType.Text) _ = StrictUtf8.GetCharCount(payload.Body);
                else if (!payload.Validate()) return state.Fail("text_payload_invalid");
            }
            value = Normalize(SeString.Parse(content.ToArray()).TextValue);
            if (!ActivitySnapshot.ValidText(value, maximum)) return state.Fail("normalized_text_bounds");
            if (!TextBufferMatches(copy)) return state.Fail("text_buffer_changed");
            state.Texts.Add(copy);
            return true;
        }
        catch (Exception) { return state.Fail("text_decode_error"); }
    }

    internal static bool TextBufferMatches(PortableArchiveTextBuffer value)
    {
        if (value.Node == 0 || value.Buffer == 0 || value.Used is < 2 or > 1025 || value.Size < value.Used ||
            value.Bytes.Length != value.Used) return false;
        var node = (AtkTextNode*)value.Node;
        return (nint)(byte*)node->NodeText.StringPtr == value.Buffer && node->NodeText.BufUsed == value.Used &&
            node->NodeText.BufSize == value.Size && new ReadOnlySpan<byte>((byte*)value.Buffer, (int)value.Used).SequenceEqual(value.Bytes);
    }

    private PortableArchiveCatalog GetCatalog(ReadTrace trace)
    {
        if (catalog is { } cached && CatalogMatches(cached)) return cached;
        catalog = null;
        var pages = data.GetSubrowExcelSheet<DescriptionPage>();
        var sections = data.GetSubrowExcelSheet<DescriptionSection>();
        var strings = data.GetExcelSheet<DescriptionString>();
        // Installed public definitions establish Section 5 -> pages 12,22,37.
        // No Quest/Unknown field is interpreted as registration or permission.
        var section = sections.GetRow(5).Take(4).ToArray();
        uint[] expectedPages = [12, 22, 37];
        if (section.Length != 3 || !section.Select(row => (int)row.SubrowId).SequenceEqual([0, 1, 2]) ||
            !section.Select(row => row.Page.RowId).SequenceEqual(expectedPages))
        { trace.Fail("section_shape"); throw new InvalidOperationException("Unsupported archive sections."); }
        var definitions = new List<PortableArchiveDefinition>();
        foreach (var id in expectedPages)
        {
            foreach (var page in pages.GetRow(id).Take(12))
            {
                if (page.Text.Count != 11 || !strings.TryGetRow(page.Text[0].RowId, out var title))
                { trace.Fail("text_links"); throw new InvalidOperationException("Unsupported archive text links."); }
                definitions.Add(new(id, page.SubrowId, title.RowId, Normalize(title.Text.ExtractText())));
            }
        }
        trace.Mark("catalog", "definitions");
        return catalog = new(pages.RawSheet, sections.RawSheet, strings.RawSheet, data.GameData, data.Language.ToString(), BuildCatalog(definitions));
    }

    private bool CatalogMatches(PortableArchiveCatalog value) => value.Language == "English" && value.Language == data.Language.ToString() &&
        ReferenceEquals(value.GameData, data.GameData) && ReferenceEquals(value.Pages, data.GetSubrowExcelSheet<DescriptionPage>().RawSheet) &&
        ReferenceEquals(value.Sections, data.GetSubrowExcelSheet<DescriptionSection>().RawSheet) &&
        ReferenceEquals(value.Strings, data.GetExcelSheet<DescriptionString>().RawSheet);

    internal static PortableArchiveDefinition[] BuildCatalog(IEnumerable<PortableArchiveDefinition> definitions)
    {
        var rows = definitions.Take(29).ToArray();
        if (rows.Length != 28 || rows.Any(row => row is null || !PortableArchiveContract.ValidPage(row.PageId, row.SubrowId) ||
            row.TitleId is 0 or > ushort.MaxValue || !ActivitySnapshot.ValidText(row.Title, 128)) ||
            rows.Select(row => (row.PageId, row.SubrowId)).Distinct().Count() != 28 ||
            rows.Select(row => row.Title).Distinct(StringComparer.Ordinal).Count() != 28)
            throw new InvalidOperationException("Unsupported archive definitions.");
        return rows;
    }

    internal static ActivityEntry[]? DescribeRows(PortableArchiveView view, IEnumerable<PortableArchiveDefinition> definitions)
    {
        if (!ActivitySnapshot.ValidText(view.Title, 128)) return null;
        PortableArchiveDefinition[] rows;
        try { rows = BuildCatalog(definitions); }
        catch (InvalidOperationException) { return null; }
        var matches = rows.Where(row => row.Title == view.Title).ToArray();
        if (matches.Length != 1) return null;
        var selected = matches[0];
        ActivityEntry[] result =
        [
            new(1, "selection", "Selected archive entry", Text: selected.Title, ReferenceId: selected.TitleId, ReferenceKind: "description_string"),
            new(2, "selection", "Selected archive page", Value: selected.SubrowId, Unit: "subrow", ReferenceId: selected.PageId, ReferenceKind: "description_page"),
        ];
        return PortableArchiveContract.ValidShape(result) ? result : null;
    }

    internal static string Normalize(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed class ReadTrace
    {
        private string stage = "start";
        private string? field;
        private string reason = "pending";
        internal void Mark(string stage, string? field = null, string reason = "pending")
        { this.stage = stage; this.field = field; this.reason = reason; }
        internal bool Fail(string reason) { this.reason = reason; return false; }
        internal void Fallback(string reason) { if (this.reason == "pending") this.reason = reason; }
        internal PortableArchiveReadDiagnostic Report(bool available) => new("compai_portable_archive_reader_v1",
            "portable_archive", available ? "available" : "unavailable", stage, field, reason);
    }

    private sealed class CopyState(ReadTrace trace)
    {
        internal readonly HashSet<nint> Owned = [];
        internal readonly List<nint> Components = [];
        internal readonly List<PortableArchiveManager> Managers = [];
        internal readonly List<PortableArchiveTextBuffer> Texts = [];
        internal bool Fail(string reason) => trace.Fail(reason);
        internal bool Stable() => Managers.All(value => ((AtkUldManager*)value.Manager)->NodeList == (AtkResNode**)value.List &&
            ((AtkUldManager*)value.Manager)->NodeListCount == value.Count);
    }
}

internal sealed record PortableArchiveView(string Title);
internal sealed record PortableArchiveDefinition(uint PageId, ushort SubrowId, uint TitleId, string Title);
internal sealed record PortableArchiveCatalog(object Pages, object Sections, object Strings, object GameData, string Language, PortableArchiveDefinition[] Definitions);
internal readonly record struct PortableArchiveOwner(nint Player, ulong ContentId, nint Module, nint Manager, nint Agent, nint Addon, uint AddonId);
internal readonly record struct PortableArchiveManager(nint Manager, nint List, ushort Count);
internal sealed record PortableArchiveTextBuffer(nint Node, nint Buffer, long Used, long Size, byte[] Bytes);
internal sealed record PortableArchiveReadDiagnostic(string schema, string category, string status, string stage, string? field, string reason);
