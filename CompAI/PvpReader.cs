using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace CompAI;

/// <summary>Framework-thread only. Copies a loaded personal profile; never opens a menu or requests data.</summary>
internal sealed unsafe class PvpReader(IDataManager data, IGameGui gui)
{
    internal const string Category = "pvp";
    internal const uint RankRowId = 1;
    internal const uint SeriesRowId = 2;
    private ushort[]? seriesLevels;

    internal ProgressionSnapshot Capture()
    {
        try
        {
            var before = Observe();
            if (!before.IsReady) return Unavailable();
            var grandCompanyName = ReadGrandCompanyName(before.GrandCompany);
            // Public definitions are loaded lazily and copied once; no player data
            // is sent anywhere while resolving the next Series EXP target.
            var levels = ReadSeriesLevels();
            return Describe(DateTimeOffset.UtcNow, before, Observe(), grandCompanyName, levels);
        }
        catch (Exception ex)
        {
            return ProgressionSnapshot.Unavailable(Category, $"PvP profile read unavailable ({ex.GetType().Name}).");
        }
    }

    private string? ReadGrandCompanyName(byte id)
    {
        try
        {
            return data.GetExcelSheet<Lumina.Excel.Sheets.GrandCompany>().TryGetRow(id, out var row)
                ? row.Name.ExtractText().Trim() : null;
        }
        catch
        {
            // A missing public label does not erase valid personal scalar data.
            return null;
        }
    }

    private ushort[]? ReadSeriesLevels()
    {
        if (this.seriesLevels is not null) return this.seriesLevels;
        try
        {
            this.seriesLevels = BuildSeriesLevels(data.GetExcelSheet<Lumina.Excel.Sheets.PvPSeriesLevel>()
                .Select(row => new PvpSeriesLevelDefinition(row.RowId, row.ExpToNext)));
            return this.seriesLevels;
        }
        catch { return null; }
    }

    internal static ushort[] BuildSeriesLevels(IEnumerable<PvpSeriesLevelDefinition> definitions)
    {
        var rows = definitions.Take(32).OrderBy(row => row.Id).ToArray();
        if (rows.Length != 31 || rows.Where((row, index) => row.Id != index ||
                (index == 0 ? row.ExperienceRequired != 0 : row.ExperienceRequired == 0)).Any())
            throw new ArgumentException("Unrecognized public Series level catalog.");
        return rows.Select(row => row.ExperienceRequired).ToArray();
    }

    internal static uint? SeriesExperienceRequired(byte rank, ushort experience, IReadOnlyList<ushort>? levels)
    {
        if (levels is null || levels.Count != 31 || levels[0] != 0 || levels.Skip(1).Any(value => value == 0)) return null;
        // Installed 2026.10.16 AgentPvpProfile: the sheet 0x414 (PvPSeriesLevel)
        // is copied in row order by 0x141085130. At 0x1410846AD the displayed
        // ratio indexes min(GetSeriesCurrentRank(), rowCount - 1), with no +1.
        var required = levels[Math.Min((int)rank, levels.Count - 1)];
        return required > 0 && experience <= required ? required : null;
    }

    internal PvpObservation Observe()
    {
        var player = PlayerState.Instance();
        var profile = PvPProfile.Instance();
        var agent = AgentPvpProfile.Instance();
        var manager = RaptureAtkUnitManager.Instance();
        if (player == null || profile == null || agent == null || manager == null ||
            !player->IsLoaded || player->ContentId == 0 || !profile->IsLoaded ||
            player->GrandCompany is < 1 or > 3 || agent->AddonId is 0 or > ushort.MaxValue ||
            !agent->IsAgentActive() || !agent->IsAddonReady() || !agent->IsAddonShown())
            return default;

        // Resolve this agent's actual addon, then require the same visible, ready
        // Dalamud handle. Do not guess an addon name or interpret SelectedTab.
        var addon = manager->GetAddonById((ushort)agent->AddonId);
        if (addon == null) return default;
        var handle = gui.GetAddonByName(addon->NameString);
        if (handle.IsNull || handle.Address != (nint)addon || handle.Id != agent->AddonId ||
            !handle.IsVisible || !handle.IsReady) return default;

        var owner = player->ContentId;
        var grandCompany = player->GrandCompany;
        var selected = SelectGrandCompany(grandCompany,
            profile->RankMaelstrom, profile->ExperienceMaelstrom,
            profile->RankTwinAdder, profile->ExperienceTwinAdder,
            profile->RankImmortalFlames, profile->ExperienceImmortalFlames);
        if (selected is not { } rank) return default;

        // Pinned ClientStructs dd57986b8287e7c6ed9c27198f8c76cb04a81dfe,
        // verified against the installed 2026.10.16 executable without running it:
        // GetPvPRank/GetPvPTotalExperience select GrandCompany - 1 directly (no rank offset).
        // Experience{GC} is cumulative: GetPvPCurrentRankExperience subtracts the
        // PvPRank.ExpRequired threshold. Do not label it progress toward the next rank.
        // GetSeriesCurrentRank/GetSeriesExperience directly return offsets 0x25/0x28.
        // UIState.Initialize clears IsLoaded; ReadPacket fills these fields and sets
        // IsLoaded last. The profile carries no owner ID or reset-acknowledgement token.
        var observation = new PvpObservation((nint)player, (nint)profile, (nint)agent,
            handle.Address, owner, true, true, true, agent->AddonId, grandCompany,
            rank.Rank, rank.Experience, profile->Series, profile->SeriesCurrentRank, profile->SeriesExperience);
        return player->IsLoaded && player->ContentId == owner && player->GrandCompany == grandCompany &&
            profile->IsLoaded ? observation : default;
    }

    internal static PvpRankObservation? SelectGrandCompany(byte grandCompany,
        byte maelstromRank, uint maelstromExperience, byte adderRank, uint adderExperience,
        byte flamesRank, uint flamesExperience) => grandCompany switch
    {
        1 => new(maelstromRank, maelstromExperience),
        2 => new(adderRank, adderExperience),
        3 => new(flamesRank, flamesExperience),
        _ => null,
    };

    internal static ProgressionSnapshot Describe(DateTimeOffset capturedAt, PvpObservation before, PvpObservation after,
        string? grandCompanyName = null, IReadOnlyList<ushort>? seriesLevels = null)
    {
        if (!before.IsReady || !after.IsReady || before != after) return Unavailable();
        var name = "PvP rank and cumulative EXP — " + grandCompanyName;
        if (!ProgressionSnapshot.ValidText(grandCompanyName, ProgressionSnapshot.MaxNameLength) ||
            !ProgressionSnapshot.ValidText(name, ProgressionSnapshot.MaxNameLength))
            name = $"PvP rank and cumulative EXP — observed Grand Company {before.GrandCompany}";
        // Only native scalar bounds are asserted. No maximum-rank, Series-period,
        // competitive tier, extra-level, unlock, reward or claim inference is made.
        return new(Category, capturedAt, true, "visible_menu_observation",
            [new(RankRowId, name, "observed",
                Rank: before.Rank, Experience: before.Experience),
             new(SeriesRowId, "Current PvP Series rank and EXP", "observed",
                Rank: before.SeriesRank, Experience: before.SeriesExperience,
                ExperienceRequired: SeriesExperienceRequired(before.SeriesRank, before.SeriesExperience, seriesLevels))], menuPage: 0);
    }

    private static ProgressionSnapshot Unavailable() => ProgressionSnapshot.Unavailable(Category,
        "No stable loaded personal PvP profile is available. Open PvP Profile yourself, wait for it to populate, then ask again. " +
        "Missing data is unknown, never zero.");
}

internal readonly record struct PvpRankObservation(byte Rank, uint Experience);
internal readonly record struct PvpSeriesLevelDefinition(uint Id, ushort ExperienceRequired);

// Owner and addresses are local stability guards only; neither reaches the snapshot or wire result.
internal readonly record struct PvpObservation(nint PlayerAddress, nint ProfileAddress, nint AgentAddress,
    nint AddonAddress, ulong ContentId, bool PlayerLoaded, bool ProfileLoaded, bool MenuReady,
    uint AddonId, byte GrandCompany, byte Rank, uint Experience, byte Series, byte SeriesRank, ushort SeriesExperience)
{
    internal bool IsReady => PlayerAddress != 0 && ProfileAddress != 0 && AgentAddress != 0 && AddonAddress != 0 &&
        ContentId != 0 && PlayerLoaded && ProfileLoaded && MenuReady && AddonId is > 0 and <= ushort.MaxValue &&
        GrandCompany is >= 1 and <= 3 && Series != 0;
}
