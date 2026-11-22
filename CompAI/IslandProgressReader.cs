using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace CompAI;

/// <summary>Framework-thread observation of rank/EXP in the player's normally open Islekeep's Index.</summary>
internal sealed unsafe class IslandProgressReader(IDataManager data, IGameGui gui)
{
    internal const string AddonName = "MJIHud";
    private const int ValueLimit = 256;

    internal IslandProgressObservation? Capture()
    {
        try
        {
            var owner = Observe();
            if (owner is null) return null;
            var game = data.GameData;
            var language = data.Language;
            var sheet = data.GetExcelSheet<MJIRank>();
            var maximumRank = ValidateCatalog(sheet.Take(257).Select(row => (row.RowId, row.ExpToNext)));
            if (maximumRank is null) return null;
            IslandRankDefinition? Definition(int rank) => sheet.TryGetRow((uint)rank, out var row) && row.ExpToNext <= int.MaxValue
                ? new(rank, (int)row.ExpToNext) : null;
            bool CatalogMatches() => ReferenceEquals(game, data.GameData) && language == data.Language &&
                ReferenceEquals(sheet.RawSheet, data.GetExcelSheet<MJIRank>().RawSheet);
            return CaptureStable(Observe, Copy, Definition, CatalogMatches, maximumRank.Value, owner);
        }
        catch (Exception) { return null; }
    }

    // Rank zero is a placeholder. Require one complete, bounded progression and
    // exactly one terminal zero requirement, rather than assuming today's cap.
    internal static int? ValidateCatalog(IEnumerable<(uint Rank, uint Required)> rows)
    {
        var entries = rows.Take(257).ToArray();
        if (entries.Length is < 2 or > 256 || entries.Any(row => row.Rank > byte.MaxValue || row.Required > int.MaxValue) ||
            entries.Select(row => row.Rank).Distinct().Count() != entries.Length) return null;
        if (entries.Any(row => row.Rank == 0 && row.Required != 0)) return null;
        var ranks = entries.Where(row => row.Rank > 0).OrderBy(row => row.Rank).ToArray();
        if (ranks.Length < 2 || ranks[^1].Required != 0) return null;
        for (var index = 0; index < ranks.Length; index++)
            if (ranks[index].Rank != index + 1 || (index < ranks.Length - 1 && ranks[index].Required == 0)) return null;
        return ranks.Length;
    }

    private IslandProgressOwner? Observe()
    {
        var player = PlayerState.Instance();
        var module = AgentModule.Instance();
        var manager = RaptureAtkUnitManager.Instance();
        if (player == null || !player->IsLoaded || player->ContentId == 0 || module == null || manager == null) return null;
        var agent = module->GetAgentByInternalId(AgentId.MJIHud);
        if (agent == null || agent->AddonId is 0 or > ushort.MaxValue || !agent->IsAgentActive() ||
            !agent->IsAddonReady() || !agent->IsAddonShown()) return null;
        var addon = manager->GetAddonById((ushort)agent->AddonId);
        if (addon == null || addon->NameString != AddonName) return null;
        var handle = gui.GetAddonByName(AddonName);
        if (handle.IsNull || !MatchesAddon((nint)addon, agent->AddonId, addon->NameString,
                handle.Address, handle.Id, handle.IsReady, handle.IsVisible) ||
            addon->AtkValues == null || addon->AtkValuesCount is < 14 or > ValueLimit) return null;
        return new((nint)player, player->ContentId, (nint)module, (nint)manager,
            (nint)agent, (nint)addon, agent->AddonId, (nint)addon->AtkValues, addon->AtkValuesCount);
    }

    internal static bool MatchesAddon(nint addon, uint addonId, string name,
        nint handleAddress, uint handleId, bool ready, bool visible) =>
        addon != 0 && addonId is > 0 and <= ushort.MaxValue && name == AddonName &&
        addon == handleAddress && addonId == handleId && ready && visible;

    internal static IslandProgressSample? Copy(IslandProgressOwner owner, int maximumRank)
    {
        if (!ValidOwner(owner) || maximumRank is < 2 or > byte.MaxValue) return null;
        var agent = (AgentInterface*)owner.Agent;
        var addon = (AtkUnitBase*)owner.Addon;
        if (agent->AddonId != owner.AddonId || addon->Id != owner.AddonId ||
            (nint)addon->AtkValues != owner.Values || addon->AtkValuesCount != owner.ValueCount) return null;
        var values = addon->AtkValues;
        var rankValue = values[11];
        if (!TryNumber(rankValue, out var rank) || rank < 1 || rank > maximumRank) return null;
        int? experience = null, required = null;
        var experienceType = 0;
        var requiredType = 0;
        // At cap the Index displays -/-. The public terminal MJIRank row determines
        // this state; masked EXP slots are not read or exported as progress.
        if (rank != maximumRank)
        {
            var experienceValue = values[12];
            var requiredValue = values[13];
            if (!TryNumber(experienceValue, out var current) || !TryNumber(requiredValue, out var next)) return null;
            experience = current;
            required = next;
            experienceType = (int)experienceValue.Type;
            requiredType = (int)requiredValue.Type;
        }
        if (agent->AddonId != owner.AddonId || addon->Id != owner.AddonId ||
            addon->AtkValues != values || addon->AtkValuesCount != owner.ValueCount) return null;
        return new(rank, experience, required, (int)rankValue.Type, experienceType, requiredType);
    }

    private static bool TryNumber(AtkValue value, out int number)
    {
        number = 0;
        if (value.Type == AtkValueType.Int && value.Int >= 0) { number = value.Int; return true; }
        if (value.Type == AtkValueType.UInt && value.UInt <= int.MaxValue) { number = (int)value.UInt; return true; }
        return false;
    }

    internal static IslandProgressObservation? CaptureStable(Func<IslandProgressOwner?> observe,
        Func<IslandProgressOwner, int, IslandProgressSample?> copy,
        Func<int, IslandRankDefinition?> definition, Func<bool> catalogMatches,
        int maximumRank, IslandProgressOwner? initialOwner = null)
    {
        var before = initialOwner ?? observe();
        if (before is not { } owner || !ValidOwner(owner) || maximumRank is < 2 or > byte.MaxValue) return null;
        var cap = definition(maximumRank);
        if (cap is not { RequiredExperience: 0 } || cap.Value.Rank != maximumRank) return null;
        // Sheet loading and each later managed lookup may outlive the menu.
        // Re-resolve ownership before dereferencing any retained native address.
        if (!catalogMatches() || observe() != before) return null;
        var first = copy(owner, maximumRank);
        if (first is not { } sample || observe() != before) return null;
        var rankDefinition = definition(sample.Rank);
        if (!ValidSample(sample, rankDefinition, maximumRank)) return null;
        if (!catalogMatches() || observe() != before) return null;
        if (copy(owner, maximumRank) != first || observe() != before) return null;
        if (definition(sample.Rank) != rankDefinition || definition(maximumRank) != cap ||
            !catalogMatches() || observe() != before) return null;
        return new(DateTimeOffset.UtcNow, sample.Rank, sample.Experience, sample.RequiredExperience, sample.Rank == maximumRank);
    }

    private static bool ValidOwner(IslandProgressOwner owner) => owner.Player != 0 && owner.ContentId != 0 &&
        owner.Module != 0 && owner.Manager != 0 && owner.Agent != 0 && owner.Addon != 0 && owner.Values != 0 &&
        owner.AddonId is > 0 and <= ushort.MaxValue && owner.ValueCount is >= 14 and <= ValueLimit;

    private static bool Integral(int type) => type is (int)AtkValueType.Int or (int)AtkValueType.UInt;

    private static bool ValidSample(IslandProgressSample sample, IslandRankDefinition? definition, int maximumRank) =>
        sample.Rank >= 1 && sample.Rank <= maximumRank && Integral(sample.RankType) &&
        definition is { } rank && rank.Rank == sample.Rank &&
        (sample.Rank == maximumRank
            ? rank.RequiredExperience == 0 && sample.Experience is null && sample.RequiredExperience is null &&
                sample.ExperienceType == 0 && sample.RequiredType == 0
            : rank.RequiredExperience > 0 && sample.RequiredExperience == rank.RequiredExperience &&
                sample.Experience >= 0 && sample.Experience < sample.RequiredExperience &&
                Integral(sample.ExperienceType) && Integral(sample.RequiredType));
}

internal sealed record IslandProgressObservation(DateTimeOffset CapturedAtUtc, int Rank,
    int? Experience, int? RequiredExperience, bool IsMaximumRank);
internal readonly record struct IslandRankDefinition(int Rank, int RequiredExperience);
internal readonly record struct IslandProgressOwner(nint Player, ulong ContentId, nint Module, nint Manager,
    nint Agent, nint Addon, uint AddonId, nint Values, int ValueCount);
internal readonly record struct IslandProgressSample(int Rank, int? Experience, int? RequiredExperience,
    int RankType, int ExperienceType = 0, int RequiredType = 0);
