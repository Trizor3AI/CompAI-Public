using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace CompAI;

/// <summary>Framework-thread only. Copies battle-companion progression without opening menus or requesting data.</summary>
internal sealed unsafe class BattleChocoboReader(IDataManager data)
{
    internal const int MaxRank = 20;
    internal const int MaxSkillRank = 10;
    internal const int SkillTreeCount = 3;
    internal const int MaxSkillPoints = byte.MaxValue;
    private BattleChocoboCatalog? catalog;

    internal ProgressionSnapshot Capture()
    {
        try
        {
            var player = PlayerState.Instance();
            var ui = UIState.Instance();
            if (player == null || !player->IsLoaded || player->ContentId == 0 || ui == null)
                return Unavailable("Loaded character battle-chocobo state is unavailable.");
            var owner = player->ContentId;
            var currentCatalog = GetCatalog();
            var first = CopyObservation(&ui->Buddy.CompanionInfo);
            var second = CopyObservation(&ui->Buddy.CompanionInfo);
            if (PlayerState.Instance() != player || UIState.Instance() != ui || !player->IsLoaded ||
                player->ContentId != owner || !CatalogMatchesCurrent(currentCatalog))
            {
                catalog = null;
                return Unavailable("Character or public battle-chocobo definitions changed during reading; try again after loading.");
            }
            return Describe(DateTimeOffset.UtcNow, currentCatalog.Ranks, first, second);
        }
        catch (Exception ex)
        {
            catalog = null;
            return Unavailable($"Battle-chocobo read unavailable ({ex.GetType().Name}).");
        }
    }

    internal static BattleChocoboObservation CopyObservation(CompanionInfo* companion)
    {
        // Installed ClientStructs informational commit dd57986b8287e7c6ed9c27198f8c76cb04a81dfe:
        // https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/FFXIV/Client/Game/UI/Buddy.cs
        // CompanionInfo is the battle chocobo, not Character.Companion (minions) or racing data.
        // Levels explicitly maps Defender, Attacker, Healer. Do not follow Companion: it may be null
        // while the chocobo is dismissed, and is unnecessary for these persistent progression fields.
        if (companion == null || companion->Levels.Length != SkillTreeCount)
            throw new InvalidOperationException("Battle-chocobo structure bounds are unsupported.");
        return new(companion->Rank, companion->CurrentXP, companion->Levels[0], companion->Levels[1],
            companion->Levels[2], companion->SkillPoints);
    }

    private BattleChocoboCatalog GetCatalog()
    {
        var rankSheet = data.GetExcelSheet<BuddyRank>();
        var skillSheet = data.GetExcelSheet<BuddySkill>();
        var gameData = data.GameData;
        var language = data.Language.ToString();
        if (catalog is { } cached && cached.Matches(rankSheet.RawSheet, skillSheet.RawSheet, gameData, language))
            return cached;
        catalog = null;
        var ranks = rankSheet.Take(MaxRank + 2).ToArray();
        var skills = skillSheet.Take(MaxSkillRank + 2).ToArray();
        if (ranks.Length != MaxRank + 1 || ranks.Count(row => row.RowId == 0) != 1 ||
            skills.Length != MaxSkillRank + 1)
            throw new InvalidOperationException("Battle-chocobo catalog bounds are unsupported.");
        // Local Lumina sheets: BuddyRank.RowId is the rank; terminal rank 20 has no next-rank EXP.
        // BuddySkill.RowId/BuddyLevel confirms the supported skill-tree rank domain. No action text,
        // descriptions, character/chocobo names, stable occupants or training records are copied.
        var definitions = BuildCatalog(ranks.Where(row => row.RowId != 0)
            .Select(row => new BattleChocoboRankDefinition(row.RowId, row.ExpRequired)),
            skills.Select(row => new BattleChocoboSkillDefinition(row.RowId, row.BuddyLevel)));
        var result = new BattleChocoboCatalog(rankSheet.RawSheet, skillSheet.RawSheet, gameData, language, definitions);
        if (!CatalogMatchesCurrent(result))
            throw new InvalidOperationException("Public battle-chocobo definitions changed during capture.");
        return catalog = result;
    }

    private bool CatalogMatchesCurrent(BattleChocoboCatalog current) => current.Matches(
        data.GetExcelSheet<BuddyRank>().RawSheet, data.GetExcelSheet<BuddySkill>().RawSheet,
        data.GameData, data.Language.ToString());

    internal static BattleChocoboRankDefinition[] BuildCatalog(IEnumerable<BattleChocoboRankDefinition> ranks,
        IEnumerable<BattleChocoboSkillDefinition> skills)
    {
        ArgumentNullException.ThrowIfNull(ranks);
        ArgumentNullException.ThrowIfNull(skills);
        var rankRows = ranks.Take(MaxRank + 1).ToArray();
        var skillRows = skills.Take(MaxSkillRank + 2).ToArray();
        if (!ValidRanks(rankRows) || skillRows.Length != MaxSkillRank + 1 ||
            skillRows.Any(row => row is null || row.Id > MaxSkillRank || row.Level != row.Id) ||
            skillRows.Select(row => row.Id).Distinct().Count() != skillRows.Length)
            throw new InvalidOperationException("Battle-chocobo public rank or skill definitions are unsupported.");
        return rankRows.OrderBy(row => row.Rank).ToArray();
    }

    private static bool ValidRanks(IReadOnlyList<BattleChocoboRankDefinition> ranks) => ranks.Count == MaxRank &&
        ranks.All(row => row is not null && row.Rank is >= 1 and <= MaxRank &&
            (row.Rank == MaxRank ? row.ExperienceRequired == 0 : row.ExperienceRequired is > 0 and <= int.MaxValue)) &&
        ranks.Select(row => row.Rank).Distinct().Count() == MaxRank;

    internal static ProgressionSnapshot Describe(DateTimeOffset capturedAt,
        IEnumerable<BattleChocoboRankDefinition> definitions, BattleChocoboObservation? first,
        BattleChocoboObservation? second, bool characterLoaded = true, bool ownerStable = true)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        if (!characterLoaded || !ownerStable)
            return Unavailable("Loaded character battle-chocobo state is unavailable or changed during reading.");
        if (first is null || first != second)
            return Unavailable("Battle-chocobo progression changed during reading; try again after loading.");
        var ranks = definitions.Take(MaxRank + 1).ToArray();
        if (!ValidRanks(ranks))
            return Unavailable("Battle-chocobo public rank definitions are unsupported.");
        if (first.Rank is < 1 or > MaxRank || first.DefenderRank is < 0 or > MaxSkillRank ||
            first.AttackerRank is < 0 or > MaxSkillRank || first.HealerRank is < 0 or > MaxSkillRank ||
            first.UnspentSkillPoints is < 0 or > MaxSkillPoints)
            return Unavailable("Battle-chocobo progression is not populated or has unsupported values; ownership or unlock state is unknown.");
        var target = ranks.Single(row => row.Rank == first.Rank).ExperienceRequired;
        if (first.Experience > (first.Rank == MaxRank ? ranks.Max(row => row.ExperienceRequired) : target))
            return Unavailable("Battle-chocobo experience is outside the supported public rank bounds.");
        // There is no companion-specific loaded marker. A nonzero, bounded rank plus stable copies
        // is a client observation, not proof of server freshness, ownership unlocks, or training status.
        // Public IDs below are this reader's fixed metric IDs, not item, minion, racing, or action IDs.
        return new("battle_chocobo", capturedAt, true, "loaded_companion_observation",
        [
            new(1, "Battle chocobo", "observed", Rank: first.Rank, Experience: first.Experience,
                ExperienceRequired: first.Rank == MaxRank ? null : target),
            new(2, "Defender skill tree", "observed", Rank: first.DefenderRank),
            new(3, "Attacker skill tree", "observed", Rank: first.AttackerRank),
            new(4, "Healer skill tree", "observed", Rank: first.HealerRank),
            new(5, "Unspent skill points", "observed", Progress: first.UnspentSkillPoints),
        ]);
    }

    private static ProgressionSnapshot Unavailable(string reason) => ProgressionSnapshot.Unavailable("battle_chocobo", reason);
}

internal sealed record BattleChocoboObservation(int Rank, uint Experience, int DefenderRank, int AttackerRank,
    int HealerRank, int UnspentSkillPoints);
internal sealed record BattleChocoboRankDefinition(uint Rank, uint ExperienceRequired);
internal sealed record BattleChocoboSkillDefinition(uint Id, int Level);
internal sealed record BattleChocoboCatalog(object RankSheet, object SkillSheet, object GameData, string Language,
    BattleChocoboRankDefinition[] Ranks)
{
    internal bool Matches(object rankSheet, object skillSheet, object gameData, string language) =>
        ReferenceEquals(RankSheet, rankSheet) && ReferenceEquals(SkillSheet, skillSheet) &&
        ReferenceEquals(GameData, gameData) && Language == language;
}
