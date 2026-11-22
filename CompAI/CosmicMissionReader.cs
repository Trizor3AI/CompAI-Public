using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.Game.WKS;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

namespace CompAI;

/// <summary>Copies positive medals corroborated by the visible Mission Log. Never populates native mission state.</summary>
internal sealed unsafe class CosmicMissionReader(IDataManager data, IGameGui gui)
{
    internal const string AddonName = "WKSMission";
    internal const int MaximumDisplayedRows = 128;
    internal const int MaximumNativeRows = 512;
    internal const int MaximumNameBytes = 1024;
    private const int CountIndex = 33;
    private const int MissionIdStart = 36;
    private const int MissionIdStride = 6;
    private const int NameStart = 802;
    private const int NameStride = 2;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal CosmicMissionCaptureStatus LastCaptureStatus { get; private set; } = new("not_captured");
    private bool failureRecorded;

    private void Report(CosmicMissionCaptureStatus status)
    {
        if (failureRecorded) return;
        LastCaptureStatus = status;
        failureRecorded = status.Stage is not ("starting" or "captured");
    }

    internal CosmicMissionObservation? Capture(uint territory)
    {
        failureRecorded = false;
        LastCaptureStatus = new("starting");
        if (territory is 0 or > ushort.MaxValue)
        { Report(new("owner", "territory_unavailable")); return null; }
        try
        {
            var before = Observe((ushort)territory);
            if (before is null) return null;
            var game = data.GameData;
            var language = data.Language;
            var missions = data.GetExcelSheet<WKSMissionUnit>();
            var jobs = data.GetExcelSheet<ClassJob>();
            var categories = data.GetExcelSheet<ClassJobCategory>();
            bool CatalogMatches() => ReferenceEquals(game, data.GameData) && language == data.Language &&
                ReferenceEquals(missions.RawSheet, data.GetExcelSheet<WKSMissionUnit>().RawSheet) &&
                ReferenceEquals(jobs.RawSheet, data.GetExcelSheet<ClassJob>().RawSheet) &&
                ReferenceEquals(categories.RawSheet, data.GetExcelSheet<ClassJobCategory>().RawSheet);
            CosmicMissionDefinition? Definition(uint id, uint jobId)
            {
                if (!missions.TryGetRow(id, out var mission) || !jobs.TryGetRow(jobId, out var job) ||
                    mission.ClassJobCategory.Count is < 1 or > 4) return null;
                var matches = false;
                foreach (var reference in mission.ClassJobCategory)
                {
                    if (reference.RowId == 0) continue;
                    if (!categories.TryGetRow(reference.RowId, out var category)) return null;
                    matches |= IncludesJob(category, jobId);
                }
                return matches ? new(jobId, mission.Name.ExtractText().Trim(), job.Name.ExtractText().Trim()) : null;
            }
            return CaptureStable(() => Observe((ushort)territory), owner => Copy(owner, Report), Definition, CatalogMatches,
                (ushort)territory, before, Report);
        }
        catch (Exception) { Report(LastCaptureStatus with { Stage = "exception", Reason = "capture_failed" }); return null; }
    }

    private static bool IncludesJob(ClassJobCategory category, uint job) => job switch
    {
        8 => category.CRP, 9 => category.BSM, 10 => category.ARM, 11 => category.GSM,
        12 => category.LTW, 13 => category.WVR, 14 => category.ALC, 15 => category.CUL,
        16 => category.MIN, 17 => category.BTN, 18 => category.FSH, _ => false,
    };

    private CosmicMissionOwner? Observe(ushort territory)
    {
        var player = PlayerState.Instance();
        var wks = WKSManager.Instance();
        var agents = AgentModule.Instance();
        var addons = RaptureAtkUnitManager.Instance();
        if (player == null || !player->IsLoaded || player->ContentId == 0 || wks == null || !wks->IsLoaded ||
            wks->TerritoryId != territory || agents == null || addons == null)
        { Report(new("owner", "loaded_cosmic_context_unavailable")); return null; }
        var agent = (AgentWKSMission*)agents->GetAgentByInternalId(AgentId.WKSMission);
        if (agent == null || agent->Data == null || agent->AddonId is 0 or > ushort.MaxValue ||
            !agent->IsAgentActive() || !agent->IsAddonReady() || !agent->IsAddonShown())
        { Report(new("readiness", "mission_agent_unavailable")); return null; }
        var addon = addons->GetAddonById((ushort)agent->AddonId);
        if (addon == null || addon->Id != agent->AddonId || addon->NameString != AddonName)
        { Report(new("owner", "addon_identity_mismatch")); return null; }
        var handle = gui.GetAddonByName(AddonName);
        if (handle.IsNull || !MatchesAddon((nint)addon, agent->AddonId, addon->NameString,
                handle.Address, handle.Id, handle.IsReady, handle.IsVisible))
        { Report(new("readiness", "addon_not_ready_or_visible")); return null; }
        var mission = agent->Data;
        var status = new CosmicMissionCaptureStatus("readiness", SelectedJob: mission->SelectedJobIndex,
            UpdateFlags: mission->UpdateFlags, ValueCount: addon->AtkValuesCount);
        if (!mission->IsMissionLog) { Report(status with { Reason = "not_mission_log" }); return null; }
        if (mission->UpdateFlags != 0) { Report(status with { Reason = "update_pending" }); return null; }
        if (mission->SelectedJobIndex > 10) { Report(status with { Reason = "unsupported_job_index" }); return null; }
        if (addon->AtkValues == null || addon->AtkValuesCount is <= NameStart or > 4096)
        { Report(status with { Stage = "bounds", Reason = "value_buffer" }); return null; }
        if (addon->AtkValues[CountIndex].Type != AtkValueType.UInt)
        { Report(status with { Stage = "copy", Reason = "display_count_type" }); return null; }
        var owner = new CosmicMissionOwner((nint)player, player->ContentId, (nint)wks, territory,
            wks->State.CurrentPlanetIndex, (nint)agents, (nint)addons, (nint)agent, (nint)mission,
            (nint)addon, agent->AddonId, (nint)addon->AtkValues, addon->AtkValuesCount,
            addon->AtkValues[CountIndex].UInt, (nint)mission->MissionList.First, (nint)mission->MissionList.Last,
            (nint)mission->MissionList.End, mission->SelectedJobIndex, mission->SelectedTabIndex,
            mission->SelectedFilterIndex, agent->SelectedTab);
        if (ValidOwner(owner, territory)) return owner;
        Report(Status(owner, "bounds", "display_or_native_bounds"));
        return null;
    }

    internal static bool MatchesAddon(nint addon, uint addonId, string name, nint handle,
        uint handleId, bool ready, bool visible) => addon != 0 && addonId is > 0 and <= ushort.MaxValue &&
        name == AddonName && addon == handle && addonId == handleId && ready && visible;

    internal static bool ValidOwner(CosmicMissionOwner owner, ushort territory) => territory != 0 &&
        owner.Territory == territory && owner.Player != 0 && owner.ContentId != 0 && owner.Wks != 0 &&
        owner.Agents != 0 && owner.Addons != 0 && owner.Agent != 0 && owner.Data != 0 && owner.Addon != 0 &&
        owner.AddonId is > 0 and <= ushort.MaxValue && owner.Values != 0 &&
        owner.ValueCount is > NameStart and <= 4096 && owner.DisplayedCount is >= 1 and <= MaximumDisplayedRows &&
        NameStart + (owner.DisplayedCount - 1) * NameStride < owner.ValueCount && owner.SelectedJob <= 10 &&
        VectorCount(owner.First, owner.Last, owner.End) is >= 1 and <= MaximumNativeRows;

    internal static int VectorCount(nint first, nint last, nint end)
    {
        if (first <= 0 || last < first || end < last || first % IntPtr.Size != 0) return -1;
        var begin = (nuint)first; var finish = (nuint)last; var capacity = (nuint)end;
        var size = (nuint)sizeof(AgentWKSMission.MissionEntry);
        if (begin == 0 || finish < begin || capacity < finish || (finish - begin) % size != 0 ||
            (capacity - begin) % size != 0 || (capacity - begin) / size > MaximumNativeRows) return -1;
        return (int)((finish - begin) / size);
    }

    private static bool NativeMatches(CosmicMissionOwner owner)
    {
        var player = (PlayerState*)owner.Player;
        var wks = (WKSManager*)owner.Wks;
        var agent = (AgentWKSMission*)owner.Agent;
        var mission = (AgentWKSMission.MissionData*)owner.Data;
        var addon = (AtkUnitBase*)owner.Addon;
        return player->IsLoaded && player->ContentId == owner.ContentId && wks->IsLoaded &&
            wks->TerritoryId == owner.Territory && wks->State.CurrentPlanetIndex == owner.Planet &&
            agent->Data == mission && agent->AddonId == owner.AddonId && agent->SelectedTab == owner.AgentTab &&
            mission->IsMissionLog && mission->UpdateFlags == 0 && mission->SelectedJobIndex == owner.SelectedJob &&
            mission->SelectedTabIndex == owner.SelectedTab && mission->SelectedFilterIndex == owner.SelectedFilter &&
            (nint)mission->MissionList.First == owner.First && (nint)mission->MissionList.Last == owner.Last &&
            (nint)mission->MissionList.End == owner.End && addon->Id == owner.AddonId &&
            (nint)addon->AtkValues == owner.Values && addon->AtkValuesCount == owner.ValueCount &&
            addon->AtkValues[CountIndex].Type == AtkValueType.UInt && addon->AtkValues[CountIndex].UInt == owner.DisplayedCount;
    }

    internal static CosmicMissionSample? Copy(CosmicMissionOwner owner) => Copy(owner, null);

    internal static CosmicMissionSample? Copy(CosmicMissionOwner owner, Action<CosmicMissionCaptureStatus>? report)
    {
        if (!ValidOwner(owner, owner.Territory))
        { report?.Invoke(Status(owner, "bounds", "copy_owner_bounds")); return null; }
        if (!NativeMatches(owner))
        { report?.Invoke(Status(owner, "copy", "owner_changed_before_copy")); return null; }
        var count = VectorCount(owner.First, owner.Last, owner.End);
        var source = (AgentWKSMission.MissionEntry*)owner.First;
        var native = new CosmicMissionNativeEntry[count];
        for (var index = 0; index < count; index++)
        {
            var row = source[index];
            native[index] = new(row.MissionUnitId, row.IconId, (int)row.Flags, row.MissionGroup, row.ClassJobIndex);
        }
        var values = (AtkValue*)owner.Values;
        var displayed = new CosmicMissionDisplayedEntry[owner.DisplayedCount];
        for (var index = 0; index < displayed.Length; index++)
        {
            var id = values + MissionIdStart + index * MissionIdStride;
            if (id->Type != AtkValueType.UInt)
            { report?.Invoke(Status(owner, "copy", "mission_id_type")); return null; }
            if (id->UInt == 0) { displayed[index] = new(0, null); continue; } // Documented category header.
            var name = ReadName(values + NameStart + index * NameStride);
            if (name is null)
            { report?.Invoke(Status(owner, "copy", "mission_name_unreadable") with { RowId = id->UInt }); return null; }
            displayed[index] = new(id->UInt, name);
        }
        if (NativeMatches(owner)) return new(native, displayed);
        report?.Invoke(Status(owner, "postcopy", "owner_changed_after_copy"));
        return null;
    }

    private static string? ReadName(AtkValue* value)
    {
        var type = value->Type;
        if (type is not (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString)) return null;
        var pointer = value->String.Value;
        if (pointer == null) return null;
        var length = 0;
        while (length <= MaximumNameBytes && pointer[length] != 0) length++;
        if (length is 0 or > MaximumNameBytes) return null;
        var bytes = new ReadOnlySpan<byte>(pointer, length + 1).ToArray();
        var content = bytes.AsSpan(0, length);
        try
        {
            foreach (var payload in new ReadOnlySeStringSpan(content))
            {
                if (payload.Type == ReadOnlySePayloadType.Invalid) return null;
                if (payload.Type == ReadOnlySePayloadType.Text) _ = StrictUtf8.GetCharCount(payload.Body);
                else if (!payload.Validate()) return null;
            }
            var text = SeString.Parse(content.ToArray()).TextValue.Trim();
            return ValidName(text) && value->Type == type && value->String.Value == pointer &&
                new ReadOnlySpan<byte>(pointer, bytes.Length).SequenceEqual(bytes) ? text : null;
        }
        catch (Exception) { return null; }
    }

    internal static CosmicMissionObservation? CaptureStable(Func<CosmicMissionOwner?> observe,
        Func<CosmicMissionOwner, CosmicMissionSample?> copy, Func<uint, uint, CosmicMissionDefinition?> definition,
        Func<bool> catalogMatches, ushort territory, CosmicMissionOwner? initialOwner = null,
        Action<CosmicMissionCaptureStatus>? report = null)
    {
        var before = initialOwner ?? observe();
        if (before is not { } owner)
        { report?.Invoke(new("owner", "observation_unavailable")); return null; }
        CosmicMissionObservation? Fail(string stage, string reason, CosmicMissionNativeEntry? row = null,
            uint? id = null, bool? nameMatch = null, bool? placeholder = null)
        {
            report?.Invoke(Status(owner, stage, reason) with { RowId = id ?? row?.MissionId,
                NativeJob = row?.ClassJobId, NativeFlags = row?.Flags, NameMatch = nameMatch, Placeholder = placeholder });
            return null;
        }
        if (!ValidOwner(owner, territory)) return Fail("bounds", "owner_or_buffer_bounds");
        if (!catalogMatches()) return Fail("public_mapping", "catalog_changed_before_copy");
        if (observe() != before) return Fail("owner", "changed_before_copy");
        var first = copy(owner);
        if (first is null || first.Native is null || first.Displayed is null || first.Native.Length != VectorCount(owner.First, owner.Last, owner.End) ||
            first.Displayed.Length != owner.DisplayedCount) return Fail("copy", "sample_unavailable_or_count_mismatch");
        if (observe() != before) return Fail("postcopy", "owner_changed_after_first_copy");
        var native = new Dictionary<uint, CosmicMissionNativeEntry>();
        foreach (var row in first.Native)
        {
            // In the mission-log builder this named field contains the mapped ClassJob ID, not the selected tab index.
            if (row.MissionId is 0 or > ushort.MaxValue || row.ClassJobId is < 8 or > 18)
                return Fail("native_mapping", "unsupported_id_or_job", row);
            if (!native.TryAdd(row.MissionId, row)) return Fail("native_mapping", "duplicate_id", row);
            if ((row.Flags & (int)(AgentWKSMission.MissionFlags.Silver | AgentWKSMission.MissionFlags.Gold)) ==
                (int)(AgentWKSMission.MissionFlags.Silver | AgentWKSMission.MissionFlags.Gold))
                return Fail("native_mapping", "conflicting_medals", row);
        }
        var displayedIds = new HashSet<uint>();
        var definitions = new List<(uint Id, uint Job, CosmicMissionDefinition Definition)>();
        var result = new List<CosmicMissionEntry>();
        foreach (var row in first.Displayed)
        {
            if (row.MissionId == 0) continue;
            if (!displayedIds.Add(row.MissionId)) return Fail("display_mapping", "duplicate_id", id: row.MissionId);
            if (!native.TryGetValue(row.MissionId, out var item)) return Fail("display_mapping", "native_id_missing", id: row.MissionId);
            var publicRow = definition(row.MissionId, item.ClassJobId);
            if (!ValidDefinition(publicRow, item.ClassJobId)) return Fail("public_mapping", "definition_unavailable_or_invalid", item);
            var medal = item.Flags & (int)(AgentWKSMission.MissionFlags.Silver | AgentWKSMission.MissionFlags.Gold);
            definitions.Add((row.MissionId, item.ClassJobId, publicRow!));
            // The log hides some real mission IDs behind "???". They provide no
            // medal evidence; omit them without revealing their public names.
            // Identity, public job membership and repeated-sample checks still apply.
            if (medal == 0 && row.Name == "???") continue;
            if (row.Name != publicRow!.Name)
                return Fail("name_match", "display_public_name_mismatch", item, nameMatch: false, placeholder: row.Name == "???");
            if (medal == 0) continue; // Absence of a positive medal is deliberately not a negative status.
            result.Add(new(row.MissionId, publicRow.Name, publicRow.ClassJobId, publicRow.JobName,
                medal == (int)AgentWKSMission.MissionFlags.Gold ? "Gold" : "Silver"));
        }
        if (displayedIds.Count == 0) return Fail("display_mapping", "header_only");
        if (!catalogMatches()) return Fail("postcopy", "catalog_changed");
        if (observe() != before) return Fail("postcopy", "owner_changed_before_second_copy");
        if (!SameSample(first, copy(owner))) return Fail("postcopy", "sample_changed");
        if (observe() != before) return Fail("postcopy", "owner_changed_after_second_copy");
        foreach (var item in definitions)
            if (definition(item.Id, item.Job) != item.Definition) return Fail("postcopy", "public_definition_changed", id: item.Id);
        if (!catalogMatches()) return Fail("postcopy", "catalog_changed_at_finish");
        if (observe() != before) return Fail("postcopy", "owner_changed_at_finish");
        report?.Invoke(Status(owner, "captured", null) with { ReturnedCount = result.Count });
        return new(DateTimeOffset.UtcNow, Array.AsReadOnly(result.ToArray()));
    }

    private static CosmicMissionCaptureStatus Status(CosmicMissionOwner owner, string stage, string? reason) =>
        new(stage, reason, owner.DisplayedCount, DiagnosticVectorSize(owner.First, owner.Last),
            DiagnosticVectorSize(owner.First, owner.End), owner.ValueCount, owner.SelectedJob, 0);

    // Arithmetic only: report rejected capacities without dereferencing them or weakening the capture bounds.
    private static int? DiagnosticVectorSize(nint first, nint end)
    {
        if (first <= 0 || end < first || first % IntPtr.Size != 0) return null;
        var bytes = (nuint)end - (nuint)first;
        var size = (nuint)sizeof(AgentWKSMission.MissionEntry);
        return bytes % size == 0 && bytes / size <= int.MaxValue ? (int)(bytes / size) : null;
    }

    private static bool ValidName(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= 128 && !text.Any(char.IsControl);
    private static bool ValidDefinition(CosmicMissionDefinition? row, uint job) => row is not null &&
        row.ClassJobId == job && job is >= 8 and <= 18 && ValidName(row.Name) && ValidName(row.JobName) &&
        row.JobName.Length + 2 + row.Name.Length <= 128;
    private static bool SameSample(CosmicMissionSample first, CosmicMissionSample? second) => second?.Native is not null &&
        second.Displayed is not null && first.Native.SequenceEqual(second.Native) && first.Displayed.SequenceEqual(second.Displayed);
}

internal sealed record CosmicMissionEntry(uint MissionId, string Name, uint ClassJobId, string JobName, string Medal);
internal sealed record CosmicMissionObservation(DateTimeOffset CapturedAtUtc, ReadOnlyCollection<CosmicMissionEntry> Entries);
internal sealed record CosmicMissionCaptureStatus(string Stage, string? Reason = null, uint? DisplayedCount = null,
    int? NativeCount = null, int? NativeCapacity = null, int? ValueCount = null, int? SelectedJob = null,
    int? UpdateFlags = null, uint? RowId = null, int? NativeJob = null, int? NativeFlags = null,
    bool? NameMatch = null, bool? Placeholder = null, int? ReturnedCount = null);
internal readonly record struct CosmicMissionOwner(nint Player, ulong ContentId, nint Wks, ushort Territory, byte Planet,
    nint Agents, nint Addons, nint Agent, nint Data, nint Addon, uint AddonId, nint Values, ushort ValueCount,
    uint DisplayedCount, nint First, nint Last, nint End, byte SelectedJob, byte SelectedTab, byte SelectedFilter, byte AgentTab);
internal readonly record struct CosmicMissionNativeEntry(uint MissionId, uint IconId, int Flags, byte Group, byte ClassJobId);
internal readonly record struct CosmicMissionDisplayedEntry(uint MissionId, string? Name);
internal sealed record CosmicMissionSample(CosmicMissionNativeEntry[] Native, CosmicMissionDisplayedEntry[] Displayed);
internal sealed record CosmicMissionDefinition(uint ClassJobId, string Name, string JobName);
