using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;

namespace CompAI;

/// <summary>Public reward definitions associated with the stable observed Series; never claim state.</summary>
internal sealed class PvpSeriesRewardsReader(IDataManager data, IGameGui gui)
{
    internal const string Category = "pvp_series_rewards";
    private readonly PvpReader profileReader = new(data, gui);
    private byte catalogSeries;
    private PvpSeriesRewardDefinition[]? catalog;

    internal ActivitySnapshot Capture()
    {
        try
        {
            var before = this.profileReader.Observe();
            if (!before.IsReady) return Unavailable();
            var definitions = this.ReadCatalog(before.Series);
            return Describe(DateTimeOffset.UtcNow, before, this.profileReader.Observe(), before.Series, definitions);
        }
        catch (Exception ex) { return ActivitySnapshot.Unavailable(Category, $"Public Series reward preview unavailable ({ex.GetType().Name})."); }
    }

    private PvpSeriesRewardDefinition[] ReadCatalog(byte series)
    {
        if (this.catalogSeries == series && this.catalog is not null) return this.catalog;
        var sheet = data.GetExcelSheet<Lumina.Excel.Sheets.PvPSeries>();
        if (!sheet.TryGetRow(series, out var row) || row.LevelRewards.Count != 32)
            throw new InvalidOperationException("Unknown public Series definition.");
        var definitions = new List<PvpSeriesRewardDefinition>(58);
        // AgentPvpProfile's 2026.10.16 reward renderer starts at catalog slot 1
        // and advances by one 16-byte definition for each displayed level.
        // Level 0/1 have no public reward in the installed catalog; repeat slot31
        // is intentionally omitted because repeat-cycle eligibility is unobserved.
        for (var level = 2; level <= 30; level++)
        {
            var reward = row.LevelRewards[level];
            if (reward.LevelRewardItem.Count != 2 || reward.LevelRewardCount.Count != 2)
                throw new InvalidOperationException("Unknown public reward shape.");
            for (var slot = 0; slot < 2; slot++)
            {
                var item = reward.LevelRewardItem[slot];
                if (item.RowId == 0) continue;
                if (!item.IsValid) throw new InvalidOperationException("Missing public reward item.");
                definitions.Add(new(level, slot, item.RowId, item.Value.Name.ExtractText().Trim(), reward.LevelRewardCount[slot]));
            }
        }
        var result = ValidateDefinitions(definitions);
        this.catalog = result;
        this.catalogSeries = series;
        return result;
    }

    internal static PvpSeriesRewardDefinition[] ValidateDefinitions(IEnumerable<PvpSeriesRewardDefinition> values)
    {
        var rows = values.Take(59).ToArray();
        if (rows.Length is < 1 or > 58 || rows.Any(row => row.Level is < 2 or > 30 || row.ItemSlot is < 0 or > 1 ||
                row.ItemId == 0 || row.Count == 0 || !ActivitySnapshot.ValidText(row.Name, 128)) ||
            rows.Select(row => (row.Level, row.ItemSlot)).Distinct().Count() != rows.Length)
            throw new ArgumentException("Invalid public Series reward definitions.");
        return rows.OrderBy(row => row.Level).ThenBy(row => row.ItemSlot).ToArray();
    }

    internal static ActivitySnapshot Describe(DateTimeOffset capturedAt, PvpObservation before, PvpObservation after,
        byte catalogSeries, IEnumerable<PvpSeriesRewardDefinition> definitions)
    {
        if (!before.IsReady || !after.IsReady || before != after || before.Series != catalogSeries) return Unavailable();
        var rows = ValidateDefinitions(definitions);
        return new(Category, capturedAt, true, rows.Select(row => new ActivityEntry(
            (uint)(row.Level * 2 + row.ItemSlot), "level_reward", row.Name,
            Text: $"Series {catalogSeries}: public reward preview for level {row.Level}.",
            Value: row.Count, Unit: "items", ReferenceId: row.ItemId, ReferenceKind: "item", Slot: row.Level)));
    }

    private static ActivitySnapshot Unavailable() => ActivitySnapshot.Unavailable(Category,
        "No stable current Series is observed. Open PvP Profile yourself and wait for it to populate. Reward previews do not establish earned or claimable rewards.");
}

internal readonly record struct PvpSeriesRewardDefinition(int Level, int ItemSlot, uint ItemId, string Name, ushort Count);
