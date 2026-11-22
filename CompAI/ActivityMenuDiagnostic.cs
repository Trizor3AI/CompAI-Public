using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace CompAI;

/// <summary>Human-triggered clipboard diagnostic. It is never an AI reader or automatic observation.</summary>
internal static unsafe class ActivityMenuDiagnostic
{
    internal const int MaxOutputBytes = 16384;
    internal const int MaxNodes = 512;
    internal const int MaxTextNodes = 200;
    internal const int MaxTextBytes = 1024;
    internal const int MaxTotalTextBytes = 8192;
    private const int MaxDepth = 8;
    private const int MaxAncestors = 32;
    private static readonly string[] AllowedTruncations =
    ["node_list_limit", "node_visit_limit", "node_examination_limit", "component_depth_limit", "parent_depth_limit",
        "text_node_limit", "text_byte_limit", "total_text_byte_limit", "output_byte_limit", "list_component_limit", "renderer_association_limit",
        "renderer_candidate_limit", "tree_item_limit"];
    internal static readonly string[] Labels =
    ["Mogpendium", "Yo-kai Medallium", "Field Record", "Collection menu", "Gold Saucer", "Archive-item view (not confirmed Portable Archive)", "Occult Record", "Free Company (no Members or Activity tab)", "Portable Archive (selected entry)", "Cosmic Mission Log"];

    internal static bool SupportsReaderStatus(ActivityDiagnosticTarget target) => target is
        ActivityDiagnosticTarget.FreeCompany or ActivityDiagnosticTarget.GoldSaucer or ActivityDiagnosticTarget.Description or ActivityDiagnosticTarget.CosmicMissions;

    internal static AgentId? AgentFor(ActivityDiagnosticTarget target) => target switch
    {
        ActivityDiagnosticTarget.Mogpendium => AgentId.MoogleCollection,
        ActivityDiagnosticTarget.Yokai => AgentId.YkwNote,
        ActivityDiagnosticTarget.FieldRecord => AgentId.MycWarResultNotebook,
        ActivityDiagnosticTarget.Collection => AgentId.McGuffin,
        ActivityDiagnosticTarget.GoldSaucer => AgentId.GoldSaucer,
        ActivityDiagnosticTarget.ArchiveItem => AgentId.ArchiveItem,
        ActivityDiagnosticTarget.OccultRecord => AgentId.MKDLoreBook,
        ActivityDiagnosticTarget.FreeCompany => AgentId.FreeCompany,
        ActivityDiagnosticTarget.Description => AgentId.Description,
        ActivityDiagnosticTarget.CosmicMissions => AgentId.WKSMission,
        _ => null,
    };

    internal static ChallengeDiagnosticResult Capture(IGameGui gui, ActivityDiagnosticTarget target, object? readerStatus = null)
    {
        if (readerStatus is not null && !SupportsReaderStatus(target))
            return Failed("Reader status is not supported for this diagnostic; nothing copied.");
        try
        {
            var before = Observe(gui, target, out var unavailable);
            if (before is null)
                return Failed(unavailable);
            var addon = (AtkUnitBase*)(before.Value.GoldSaucerChild is { TextAddon: not 0 } child
                ? child.TextAddon : before.Value.Addon);
            var includeLists = target == ActivityDiagnosticTarget.Mogpendium;
            if (!TryCopyVisibleText(&addon->UldManager, out var first, out var failure, includeLists))
                return Failed($"The selected menu has an invalid {failure}; nothing copied.");
            var middle = Observe(gui, target, out _);
            if (!SameObservation(middle, before) || !TryCopyVisibleText(&addon->UldManager, out var second, out _, includeLists) ||
                !SameText(first, second) || !SameObservation(Observe(gui, target, out _), before))
                return Failed("The selected menu or character changed during capture; nothing copied.");
            return Format(target, before.Value.AddonName, before.Value.SelectedTab, first, readerStatus,
                before.Value.FieldRecord, before.Value.GoldSaucerChild);
        }
        catch (Exception) { return Failed("The selected menu diagnostic is unavailable; nothing copied."); }
    }

    private static ActivityDiagnosticObservation? Observe(IGameGui gui, ActivityDiagnosticTarget target, out string unavailable)
    {
        unavailable = "The selected diagnostic target is unsupported; nothing copied.";
        if (AgentFor(target) is not { } id) return null;
        unavailable = "Log into a loaded character first; nothing copied.";
        var player = PlayerState.Instance();
        var module = AgentModule.Instance();
        var manager = RaptureAtkUnitManager.Instance();
        if (player == null || !player->IsLoaded || player->ContentId == 0 || module == null || manager == null)
            return null;
        unavailable = target == ActivityDiagnosticTarget.ArchiveItem
            ? "ArchiveItem has no visible, ready owned window. Portable Archive is not a verified target for this agent; nothing copied."
            : $"No visible, ready window owned by {id}. Open {Labels[(int)target]}, let it load, and copy again.";
        var agent = module->GetAgentByInternalId(id);
        if (agent == null || agent->AddonId is 0 or > ushort.MaxValue ||
            !agent->IsAgentActive() || !agent->IsAddonReady() || !agent->IsAddonShown()) return null;
        // Exact AgentIds are documented in pinned AgentModule.cs dd57986b.
        // The owning agent resolves the addon; no guessed names, offsets or AtkValues.
        var addon = manager->GetAddonById((ushort)agent->AddonId);
        if (addon == null || !ValidAddonName(addon->NameString)) return null;
        var name = addon->NameString;
        var handle = gui.GetAddonByName(name);
        if (handle.IsNull || handle.Address != (nint)addon || handle.Id != agent->AddonId ||
            !handle.IsReady || !handle.IsVisible) return null;
        int? tab = null;
        nint infoAddress = 0, profileAddress = 0, membersAddress = 0;
        ulong companyId = 0;
        FieldRecordDiagnosticSelection? fieldRecord = null;
        GoldSaucerChildObservation? goldSaucerChild = null;
        if (target == ActivityDiagnosticTarget.FieldRecord)
        {
            if (name != "MYCWarResultNotebook") return null;
            // Pinned typed display fields only. These raw values are not registration
            // flags, content IDs, or a verified displayed-number mapping.
            var notebook = (AddonMYCWarResultNotebook*)addon;
            fieldRecord = new(notebook->MaxNoteIndex, notebook->CurrentNoteIndex, notebook->CurrentPageIndex);
        }
        if (target == ActivityDiagnosticTarget.GoldSaucer)
        {
            var saucer = AgentGoldSaucer.Instance();
            if ((nint)saucer != (nint)agent || name != "GoldSaucerInfo") return null;
            var info = (AddonGoldSaucerInfo*)addon;
            if (info->CategoryRadioButtons.Length != 6 || info->SelectedCategory is < 0 or > 5 ||
                saucer->GoldSaucerSelectedTab != info->SelectedCategory) return null;
            tab = info->SelectedCategory;
            if (tab == 5)
            {
                unavailable = "The Mahjong panel is still loading or its ownership could not be verified; nothing copied.";
                if (!GoldSaucerChildDiagnostic.TryObserve(info, id => (nint)manager->GetAddonById(id), out goldSaucerChild))
                    return null;
            }
        }
        else if (target == ActivityDiagnosticTarget.FreeCompany)
        {
            unavailable = "Open the main Free Company Topics, Rank, Actions or Info tab. Members, Activity and inspection windows are excluded; nothing copied.";
            var company = AgentFreeCompany.Instance();
            var info = InfoModule.Instance();
            var inspect = AgentInspect.Instance();
            var inspectedCompany = AgentFreeCompanyProfile.Instance();
            if ((nint)company != (nint)agent || info == null || info->LocalContentId != player->ContentId ||
                inspect != null && inspect->IsAgentActive() || inspectedCompany != null && inspectedCompany->IsAgentActive())
                return null;
            var profile = company->InfoProxyFreeCompany;
            var members = company->InfoProxyFreeCompanyMember;
            // Match the main-menu ownership guards used by FreeCompanyReader. Never read
            // member CharData, Activity event records, or inspection profile text.
            if (profile == null || members == null || profile->Id == 0 || profile->Id != members->FreeCompanyId ||
                (nint)profile != (nint)info->GetInfoProxyById(InfoProxyId.FreeCompany) ||
                (nint)members != (nint)info->GetInfoProxyById(InfoProxyId.FreeCompanyMember) ||
                !ValidSelectedTab(target, profile->ActiveListItemNum)) return null;
            tab = profile->ActiveListItemNum;
            infoAddress = (nint)info; profileAddress = (nint)profile; membersAddress = (nint)members; companyId = profile->Id;
        }
        return new((nint)player, player->ContentId, (nint)module, (nint)manager, (nint)agent,
            (nint)addon, agent->AddonId, name, tab, infoAddress, profileAddress, membersAddress, companyId, fieldRecord, goldSaucerChild);
    }

    internal static bool SameObservation(ActivityDiagnosticObservation? first, ActivityDiagnosticObservation? second) =>
        first is { } a && second is { } b &&
        (a with { GoldSaucerChild = null }) == (b with { GoldSaucerChild = null }) &&
        (a.GoldSaucerChild is null && b.GoldSaucerChild is null ||
            GoldSaucerChildDiagnostic.Same(a.GoldSaucerChild, b.GoldSaucerChild));

    internal static bool ValidSelectedTab(ActivityDiagnosticTarget target, int? tab) => target switch
    {
        ActivityDiagnosticTarget.GoldSaucer => tab is >= 0 and <= 5,
        ActivityDiagnosticTarget.FreeCompany => tab is 0 or 2 or 3 or 5,
        _ => tab is null,
    };

    private static string? TabLabel(ActivityDiagnosticTarget target, int? tab) => target == ActivityDiagnosticTarget.FreeCompany
        ? tab switch { 0 => "Topics", 2 => "Rank", 3 => "Actions", 5 => "Info", _ => null }
        : target == ActivityDiagnosticTarget.GoldSaucer && tab == 5 ? "Mahjong" : null;

    internal static bool SameText(ActivityDiagnosticTextCapture first, ActivityDiagnosticTextCapture second) =>
        MogpendiumListDiagnostic.Same(first.Lists, second.Lists) &&
        first.NodesExamined == second.NodesExamined && first.TruncationReasons.SequenceEqual(second.TruncationReasons) &&
        first.Texts.Length == second.Texts.Length && first.Texts.Zip(second.Texts).All(pair => pair.First.NodeId == pair.Second.NodeId &&
            pair.First.ParentNodeId == pair.Second.ParentNodeId && pair.First.Text == pair.Second.Text &&
            pair.First.Omission == pair.Second.Omission &&
            pair.First.ComponentPath.SequenceEqual(pair.Second.ComponentPath));

    // Deliberately separate from the strict Challenge walker: these partial observations are
    // human diagnostics only and must never corroborate an AI reader's progress or completion.
    internal static bool TryCopyVisibleText(AtkUldManager* manager, out ActivityDiagnosticTextCapture capture, out string? failure,
        bool includeMogpendiumLists = false, Func<nint, ComponentType>? componentType = null)
    {
        var state = new WalkState { Lists = includeMogpendiumLists ? new(componentType) : null };
        capture = new([], [], 0);
        state.Pending.Enqueue(new((nint)manager, []));
        // Breadth-first traversal preserves page-level labels before list-row descendants.
        // The entry budget counts every examined pointer, including hidden/decorative nodes.
        while (state.Pending.TryDequeue(out var work))
        {
            if (!CopyManager((AtkUldManager*)work.Manager, work.Path, state))
            { failure = state.Failure; return false; }
            if (state.Examined >= MaxNodes * 4 && state.Pending.Count > 0)
            { state.Truncations.Add("node_examination_limit"); break; }
        }
        // Retain the recursive walker's parent-list stability check after descendants, too.
        foreach (var observed in state.Managers)
        {
            var current = (AtkUldManager*)observed.Manager;
            if ((nint)current->NodeList != observed.List || current->NodeListCount != observed.Count)
            { failure = "changed node list"; return false; }
        }
        MogpendiumListCapture? lists = null;
        if (state.Lists is not null)
        {
            if (!state.Lists.Finish(out lists, out failure)) return false;
            state.Truncations.UnionWith(lists.limits);
        }
        failure = null;
        capture = new(state.Texts.ToArray(), state.Truncations.Order(StringComparer.Ordinal).ToArray(), state.Examined, lists);
        return true;
    }

    private static bool CopyManager(AtkUldManager* manager, uint[] path, WalkState state)
    {
        if (manager == null) return state.Fail("component manager");
        var count = manager->NodeListCount;
        var list = manager->NodeList;
        if (count > 0 && list == null) return state.Fail("node list");
        state.Managers.Add(new((nint)manager, (nint)list, count));
        var boundedCount = Math.Min((int)count, MaxNodes);
        if (count > boundedCount) state.Truncations.Add("node_list_limit");
        for (var i = 0; i < boundedCount; ++i)
        {
            if (state.Examined >= MaxNodes * 4) { state.Truncations.Add("node_examination_limit"); break; }
            ++state.Examined;
            var node = list[i];
            if (node == null || state.Visited.Contains((nint)node)) continue;
            // Only visible text/components need a content visit. Images, layout nodes and
            // hidden rows still consume the separate 2048-entry examination budget, but
            // cannot starve later visible labels of the 512 content visits.
            if (node->Type != NodeType.Text && (ushort)node->Type < 1000) continue;
            if (!VisibleThroughParents(node, state)) continue;
            if (state.Visited.Count >= MaxNodes) { state.Truncations.Add("node_visit_limit"); break; }
            state.Visited.Add((nint)node);
            if (node->Type == NodeType.Text)
            {
                if (state.Texts.Count >= MaxTextNodes) { state.Truncations.Add("text_node_limit"); continue; }
                var text = (AtkTextNode*)node;
                var length = text->NodeText.StringLength;
                var used = text->NodeText.BufUsed;
                if (length < 0 || used < 0 || used < length || text->NodeText.BufSize < used)
                    return state.Fail("text buffer metadata");
                if (used <= 1) continue;
                string? value = null;
                string? omission = null;
                // Never dereference a large text buffer or parse a partial SeString payload.
                if (length > MaxTextBytes || used > MaxTextBytes + 1) omission = "text_byte_limit";
                else
                {
                    if ((byte*)text->NodeText.StringPtr == null) return state.Fail("text buffer pointer");
                    var span = text->NodeText.AsSpan();
                    if (span.Length > MaxTextBytes) omission = "text_byte_limit";
                    else if (state.Bytes + span.Length > MaxTotalTextBytes) omission = "total_text_byte_limit";
                    else
                    {
                        state.Bytes += span.Length;
                        try { value = SeString.Parse(span.ToArray()).TextValue; }
                        catch (Exception) { return state.Fail("text payload"); }
                        if (string.IsNullOrWhiteSpace(value)) continue;
                        if (Encoding.UTF8.GetByteCount(value) > MaxTextBytes)
                        { value = null; omission = "text_byte_limit"; }
                    }
                }
                if (omission is not null) state.Truncations.Add(omission);
                state.Texts.Add(new(node->NodeId, node->ParentNode == null ? null : node->ParentNode->NodeId,
                    path, value, omission));
            }
            else if ((ushort)node->Type >= 1000)
            {
                if (path.Length >= MaxDepth) { state.Truncations.Add("component_depth_limit"); continue; }
                var component = ((AtkComponentNode*)node)->Component;
                if (component == null) return state.Fail("component pointer");
                if (state.Lists is not null && !state.Lists.Observe((AtkComponentNode*)node, [..path, node->NodeId], out var failure))
                    return state.Fail(failure ?? "list structure");
                state.Pending.Enqueue(new((nint)(&component->UldManager), [..path, node->NodeId]));
            }
        }
        return manager->NodeList == list && manager->NodeListCount == count || state.Fail("changed node list");
    }

    private static bool VisibleThroughParents(AtkResNode* node, WalkState state)
    {
        for (var depth = 0; node != null && depth < MaxAncestors; ++depth, node = node->ParentNode)
            if ((node->NodeFlags & NodeFlags.Visible) == 0 || node->Color.A == 0) return false;
        if (node == null) return true;
        state.Truncations.Add("parent_depth_limit");
        return false;
    }

    private sealed class WalkState
    {
        internal readonly List<ActivityDiagnosticText> Texts = [];
        internal readonly HashSet<nint> Visited = [];
        internal readonly HashSet<string> Truncations = [];
        internal readonly Queue<ManagerWork> Pending = [];
        internal readonly List<ManagerObservation> Managers = [];
        internal int Bytes;
        internal int Examined;
        internal string? Failure;
        internal MogpendiumListDiagnostic? Lists;
        internal bool Fail(string reason) { this.Failure = reason; return false; }
    }

    private readonly record struct ManagerWork(nint Manager, uint[] Path);
    private readonly record struct ManagerObservation(nint Manager, nint List, ushort Count);

    internal static ChallengeDiagnosticResult Format(ActivityDiagnosticTarget target, string addonName, int? selectedTab,
        ActivityDiagnosticTextCapture capture, object? readerStatus = null, FieldRecordDiagnosticSelection? fieldRecord = null,
        GoldSaucerChildObservation? goldSaucerChild = null)
    {
        if (AgentFor(target) is not { } agent || !ValidAddonName(addonName) ||
            !ValidSelectedTab(target, selectedTab) || readerStatus is not null && !SupportsReaderStatus(target) ||
            fieldRecord is not null && (target != ActivityDiagnosticTarget.FieldRecord || addonName != "MYCWarResultNotebook") ||
            goldSaucerChild is not null && (target != ActivityDiagnosticTarget.GoldSaucer || selectedTab != 5 ||
                addonName != "GoldSaucerInfo" || !ValidChildReport(goldSaucerChild)) ||
            capture is null || capture.NodesExamined is < 0 or > MaxNodes * 4 || capture.Texts is null ||
            capture.TruncationReasons is null || capture.TruncationReasons.Length > AllowedTruncations.Length ||
            capture.TruncationReasons.Any(reason => !AllowedTruncations.Contains(reason)) ||
            capture.Lists is not null && (target != ActivityDiagnosticTarget.Mogpendium || !MogpendiumListDiagnostic.Valid(capture.Lists) ||
                capture.Lists.limits.Any(reason => !capture.TruncationReasons.Contains(reason))) ||
            capture.Texts.Length > MaxTextNodes ||
            capture.Texts.Any(row => row is null || row.ComponentPath is null || row.ComponentPath.Length > MaxDepth ||
                (row.Omission is null ? string.IsNullOrWhiteSpace(row.Text) || Encoding.UTF8.GetByteCount(row.Text) > MaxTextBytes
                    : row.Text is not null || row.Omission is not ("text_byte_limit" or "total_text_byte_limit") ||
                        !capture.TruncationReasons.Contains(row.Omission))) ||
            capture.Texts.Sum(row => row.Text is null ? 0 : Encoding.UTF8.GetByteCount(row.Text)) > MaxTotalTextBytes)
            return Failed("The selected menu diagnostic contains invalid metadata; nothing copied.");
        // Caller supplies a fixed human-only status DTO, never native observations or player
        // identities. Freeze it once so the bounded output trimming cannot re-read state.
        JsonElement? statusCopy = null;
        if (readerStatus is not null)
        {
            try
            {
                var status = JsonSerializer.SerializeToElement(readerStatus);
                if (status.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(status.GetRawText()) > MaxOutputBytes)
                    return Failed("The reader status is invalid or exceeds 16 KB; nothing copied.");
                statusCopy = status;
            }
            catch (Exception) { return Failed("The reader status could not be copied; nothing copied."); }
        }
        var texts = capture.Texts.ToList();
        var outputLists = capture.Lists;
        var reasons = new HashSet<string>(capture.TruncationReasons);
        var rowsOmitted = 0;
        var renderersOmitted = 0;
        // Explicit projection contains only public menu identifiers and displayed text.
        // Observation pointers and character identity are never projected or logged.
        string Serialize()
        {
            var payload = new Dictionary<string, object?>
            {
                ["diagnostic"] = "compai_activity_menu_v2", ["selected_menu"] = Labels[(int)target], ["agent"] = agent.ToString(),
                ["addon"] = addonName, ["selected_tab"] = selectedTab, ["selected_tab_label"] = TabLabel(target, selectedTab),
                ["truncated"] = reasons.Count > 0, ["truncation_reasons"] = reasons.Order(StringComparer.Ordinal),
                ["nodes_examined"] = capture.NodesExamined, ["output_rows_omitted"] = rowsOmitted,
                ["text_nodes"] = texts.Select(row => new
                {
                    node_id = row.NodeId, parent_node_id = row.ParentNodeId,
                    component_path = row.ComponentPath, text = row.Text, omission = row.Omission,
                }),
            };
            if (statusCopy is { } status) payload["reader_status"] = status;
            if (fieldRecord is { } selection) payload["field_record_selection"] = new
            {
                coverage = "Raw typed display indices only; registration and displayed record-number mapping are unverified.",
                max_note_index = selection.MaxNoteIndex, current_note_index = selection.CurrentNoteIndex,
                current_page_index = selection.CurrentPageIndex,
            };
            if (target == ActivityDiagnosticTarget.Description)
                payload["scope"] = "Visible window owned by the Description agent. The separate reader requires a stable full selected Portable Archive title with a unique local definition; this diagnostic alone does not establish registration or unvisited entries.";
            if (goldSaucerChild is { } panel) payload["gold_saucer_panel"] = new
            {
                coverage = "Direct owned panels only. Text comes from the named source; child tab indices have no verified Mahjong meaning.",
                status = panel.Status, text_source_addon = panel.TextAddonName ?? addonName,
                children = panel.Children.Select(row => new
                {
                    ordinal = row.Ordinal, addon = row.Name, raw_tab_index = row.RawTabIndex,
                    ready = row.Ready, visible = row.Visible,
                }),
            };
            if (outputLists is { } lists) payload["mogpendium_lists"] = MogpendiumListDiagnostic.Payload(lists, renderersOmitted);
            return JsonSerializer.Serialize(payload);
        }
        var output = Serialize();
        // JSON escaping also counts toward the cap. Remove longest text first so short labels,
        // counters and all available node metadata survive before dropping rows as a last resort.
        while (Encoding.UTF8.GetByteCount(output) > MaxOutputBytes)
        {
            reasons.Add("output_byte_limit");
            var longest = texts.Select((row, index) => (row, index)).Where(value => value.row.Text is not null)
                .OrderByDescending(value => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(value.row.Text))).FirstOrDefault();
            if (longest.row is not null) texts[longest.index] = longest.row with { Text = null, Omission = "output_byte_limit" };
            else if (texts.Count > 0) { texts.RemoveAt(texts.Count - 1); ++rowsOmitted; }
            else if (outputLists is { renderers.Length: > 0 })
            {
                outputLists = outputLists with { renderers = outputLists.renderers[..^1] };
                ++renderersOmitted;
            }
            else return Failed("The menu metadata exceeds 16 KB; nothing copied.");
            output = Serialize();
        }
        return new(output, reasons.Count > 0
            ? $"Partial menu diagnostic copied ({string.Join(", ", reasons.Order(StringComparer.Ordinal))}). Review the omission markers and visible text before sharing."
            : "Menu diagnostic copied. Review the visible text before sharing it.");
    }

    private static bool ValidAddonName(string name) => name.Length is > 0 and <= 64 &&
        name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    private static bool ValidChildReport(GoldSaucerChildObservation panel) =>
        panel.Children is not null && panel.Children.Count <= GoldSaucerChildDiagnostic.MaxChildren &&
        panel.Children.Select((row, index) => row is not null && row.Ordinal == index &&
            (row.Name is null || ValidAddonName(row.Name))).All(valid => valid) &&
        (panel.Status == "one_visible_owned_child"
            ? panel.TextAddon != 0 && panel.TextAddonName is { } name && ValidAddonName(name) &&
                panel.Children.Count(row => row.Name == name && row.Visible && row.Ready) == 1
            : (panel.Status is "no_visible_owned_child" or "multiple_visible_owned_children" or "visible_child_not_ready") &&
                panel.TextAddon == 0 && panel.TextAddonName is null);
    private static ChallengeDiagnosticResult Failed(string status) => new(null, status);
}

internal enum ActivityDiagnosticTarget { Mogpendium, Yokai, FieldRecord, Collection, GoldSaucer, ArchiveItem, OccultRecord, FreeCompany, Description, CosmicMissions }
internal sealed record FieldRecordDiagnosticSelection(int MaxNoteIndex, int CurrentNoteIndex, int CurrentPageIndex);
internal sealed record ActivityDiagnosticText(uint NodeId, uint? ParentNodeId, uint[] ComponentPath, string? Text, string? Omission = null);
internal sealed record ActivityDiagnosticTextCapture(ActivityDiagnosticText[] Texts, string[] TruncationReasons, int NodesExamined, MogpendiumListCapture? Lists = null);
internal readonly record struct ActivityDiagnosticObservation(nint Player, ulong Owner, nint Module, nint Manager,
    nint Agent, nint Addon, uint AddonId, string AddonName, int? SelectedTab,
    nint Info = 0, nint Profile = 0, nint Members = 0, ulong CompanyId = 0, FieldRecordDiagnosticSelection? FieldRecord = null,
    GoldSaucerChildObservation? GoldSaucerChild = null);
