using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.Game.WKS;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace CompAI;

/// <summary>Framework-thread copy of loaded research while its own menu is visible. No native research getters or actions.</summary>
internal sealed unsafe class CosmicResearchReader(IDataManager data, IGameGui gui)
{
    internal const string AddonName = "WKSToolCustomize";
    internal const int JobCount = 11;
    internal const int TypeCount = 7;
    internal const int StageCount = 20;

    internal CosmicResearchObservation? Capture(uint territory)
    {
        if (territory is 0 or > ushort.MaxValue) return null;
        try
        {
            var initial = Observe((ushort)territory);
            if (initial is null) return null;
            var game = data.GameData;
            var language = data.Language;
            var jobs = data.GetExcelSheet<ClassJob>();
            var tools = data.GetExcelSheet<WKSCosmoToolClass>();
            var names = data.GetExcelSheet<WKSCosmoToolName>();
            bool CatalogMatches() => ReferenceEquals(game, data.GameData) && language == data.Language &&
                ReferenceEquals(jobs.RawSheet, data.GetExcelSheet<ClassJob>().RawSheet) &&
                ReferenceEquals(tools.RawSheet, data.GetExcelSheet<WKSCosmoToolClass>().RawSheet) &&
                ReferenceEquals(names.RawSheet, data.GetExcelSheet<WKSCosmoToolName>().RawSheet);
            CosmicResearchDefinition? Definition(int toolClass)
            {
                var jobId = (uint)(toolClass + 7);
                if (!jobs.TryGetRow(jobId, out var job) || !tools.TryGetRow((uint)toolClass, out var tool) ||
                    tool.Stages.Count != StageCount || tool.Types.Count != TypeCount) return null;
                var stageNames = new string?[StageCount];
                var typeNames = new string[TypeCount];
                for (var index = 0; index < StageCount; index++)
                {
                    var id = tool.Stages[index].Name.RowId;
                    if (id == 0 && index == 0) continue; // The initial public stage has no research label.
                    if (id == 0 || !names.TryGetRow(id, out var name)) return null;
                    stageNames[index] = name.Name.ExtractText();
                }
                for (var index = 0; index < TypeCount; index++)
                {
                    var id = tool.Types[index].Name.RowId;
                    if (id == 0 || tool.Types[index].Icon == 0 || !names.TryGetRow(id, out var name)) return null;
                    typeNames[index] = name.Name.ExtractText();
                }
                return new(jobId, job.Name.ExtractText(), stageNames, typeNames);
            }
            return CaptureStable(() => Observe((ushort)territory), Copy, Definition, CatalogMatches,
                (ushort)territory, initial);
        }
        catch (Exception) { return null; }
    }

    private CosmicResearchOwner? Observe(ushort territory)
    {
        var player = PlayerState.Instance();
        var wks = WKSManager.Instance();
        var agents = AgentModule.Instance();
        var addons = RaptureAtkUnitManager.Instance();
        if (player == null || !player->IsLoaded || player->ContentId == 0 || wks == null ||
            !wks->IsLoaded || wks->TerritoryId != territory || wks->ResearchModule == null ||
            !wks->ResearchModule->IsLoaded || agents == null || addons == null) return null;
        var agent = agents->GetAgentByInternalId(AgentId.WKSToolCustomize);
        if (agent == null || agent->AddonId is 0 or > ushort.MaxValue || !agent->IsAgentActive() ||
            !agent->IsAddonReady() || !agent->IsAddonShown()) return null;
        var addon = addons->GetAddonById((ushort)agent->AddonId);
        if (addon == null || addon->Id != agent->AddonId || addon->NameString != AddonName) return null;
        var handle = gui.GetAddonByName(AddonName);
        if (handle.IsNull || !MatchesAddon((nint)addon, agent->AddonId, addon->NameString,
                handle.Address, handle.Id, handle.IsReady, handle.IsVisible)) return null;
        return new((nint)player, player->ContentId, (nint)wks, (nint)wks->ResearchModule, territory,
            (nint)agents, (nint)addons, (nint)agent, (nint)addon, agent->AddonId);
    }

    internal static bool MatchesAddon(nint addon, uint addonId, string name, nint handleAddress,
        uint handleId, bool ready, bool visible) => addon != 0 && addonId is > 0 and <= ushort.MaxValue &&
        name == AddonName && addon == handleAddress && addonId == handleId && ready && visible;

    internal static CosmicResearchSample? Copy(CosmicResearchOwner owner)
    {
        if (!ValidOwner(owner, owner.Territory)) return null;
        var wks = (WKSManager*)owner.Wks;
        var research = (WKSResearchModule*)owner.Research;
        bool Matches() => wks->IsLoaded && wks->TerritoryId == owner.Territory &&
            wks->ResearchModule == research && research->IsLoaded;
        if (!Matches()) return null;
        // Documented 11 tool classes x 7 types. The native getter's indexing was
        // corroborated offline; use the installed typed arrays, never raw offsets.
        var sample = new CosmicResearchSample(research->CurrentStages.ToArray(),
            research->UnlockedStages.ToArray(), research->Analysis.ToArray());
        return Matches() ? sample : null;
    }

    internal static CosmicResearchObservation? CaptureStable(Func<CosmicResearchOwner?> observe,
        Func<CosmicResearchOwner, CosmicResearchSample?> copy,
        Func<int, CosmicResearchDefinition?> definition, Func<bool> catalogMatches, ushort territory,
        CosmicResearchOwner? initialOwner = null)
    {
        var before = initialOwner ?? observe();
        if (before is not { } owner || !ValidOwner(owner, territory) || !catalogMatches() || observe() != before) return null;
        var first = copy(owner);
        if (!ValidSample(first) || observe() != before) return null;
        var definitions = new CosmicResearchDefinition[JobCount];
        for (var index = 0; index < JobCount; index++)
        {
            var item = definition(index + 1);
            if (!ValidDefinition(item, index + 1)) return null;
            definitions[index] = item!;
        }
        if (!catalogMatches() || observe() != before || !SameSample(first, copy(owner)) || observe() != before) return null;
        for (var index = 0; index < JobCount; index++)
            if (!SameDefinition(definitions[index], definition(index + 1))) return null;
        if (!catalogMatches() || observe() != before) return null;
        var result = new CosmicResearchJob[JobCount];
        for (var job = 0; job < JobCount; job++)
        {
            var item = definitions[job];
            var values = new CosmicResearchData[TypeCount];
            for (var type = 0; type < TypeCount; type++)
                values[type] = new((uint)type + 1, item.DataNames[type].Trim(), first!.Analysis[job * TypeCount + type]);
            var current = first!.CurrentStages[job];
            var unlocked = first.UnlockedStages[job];
            result[job] = new((uint)job + 1, item.ClassJobId, item.Name.Trim(), current, unlocked,
                current == 0 ? null : item.StageNames[current - 1]?.Trim(),
                unlocked == 0 ? null : item.StageNames[unlocked - 1]?.Trim(), Array.AsReadOnly(values));
        }
        return new(DateTimeOffset.UtcNow, Array.AsReadOnly(result));
    }

    private static bool ValidOwner(CosmicResearchOwner owner, ushort territory) => territory != 0 &&
        owner.Territory == territory && owner.Player != 0 && owner.ContentId != 0 && owner.Wks != 0 &&
        owner.Research != 0 && owner.Agents != 0 && owner.Addons != 0 && owner.Agent != 0 && owner.Addon != 0 &&
        owner.AddonId is > 0 and <= ushort.MaxValue;

    private static bool ValidSample(CosmicResearchSample? sample) => sample is not null &&
        sample.CurrentStages is { Length: JobCount } && sample.UnlockedStages is { Length: JobCount } &&
        sample.Analysis is { Length: JobCount * TypeCount } &&
        sample.CurrentStages.All(stage => stage <= StageCount) && sample.UnlockedStages.All(stage => stage <= StageCount);

    private static bool ValidName(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Any(char.IsControl);
    private static bool ValidDefinition(CosmicResearchDefinition? value, int toolClass) => value is not null &&
        value.ClassJobId == toolClass + 7 && ValidName(value.Name) && value.StageNames is { Length: StageCount } &&
        value.DataNames is { Length: TypeCount } && value.DataNames.All(ValidName) &&
        value.StageNames.Select((name, index) => index == 0 && name is null || ValidName(name)).All(valid => valid);

    private static bool SameSample(CosmicResearchSample? first, CosmicResearchSample? second) => ValidSample(first) && ValidSample(second) &&
        first!.CurrentStages.SequenceEqual(second!.CurrentStages) && first.UnlockedStages.SequenceEqual(second.UnlockedStages) &&
        first.Analysis.SequenceEqual(second.Analysis);
    private static bool SameDefinition(CosmicResearchDefinition first, CosmicResearchDefinition? second) =>
        second is not null && first.ClassJobId == second.ClassJobId && first.Name == second.Name &&
        second.StageNames is not null && second.DataNames is not null &&
        first.StageNames.SequenceEqual(second.StageNames) && first.DataNames.SequenceEqual(second.DataNames);
}

internal sealed record CosmicResearchData(uint Type, string Name, int Quantity);
internal sealed record CosmicResearchJob(uint ToolClassId, uint ClassJobId, string Name, int CurrentStage, int UnlockedStage,
    string? CurrentStageName, string? UnlockedStageName, ReadOnlyCollection<CosmicResearchData> Data);
internal sealed record CosmicResearchObservation(DateTimeOffset CapturedAtUtc, ReadOnlyCollection<CosmicResearchJob> Jobs);
internal readonly record struct CosmicResearchOwner(nint Player, ulong ContentId, nint Wks, nint Research, ushort Territory,
    nint Agents, nint Addons, nint Agent, nint Addon, uint AddonId);
internal sealed record CosmicResearchSample(byte[] CurrentStages, byte[] UnlockedStages, ushort[] Analysis);
internal sealed record CosmicResearchDefinition(uint ClassJobId, string Name, string?[] StageNames, string[] DataNames);
