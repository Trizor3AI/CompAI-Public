using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using AchievementSheet = Lumina.Excel.Sheets.Achievement;
using NativeAchievement = FFXIVClientStructs.FFXIV.Client.Game.UI.Achievement;

namespace CompAI;

/// <summary>Framework-thread only. Reads an already loaded completion bitmap without requesting or opening anything.</summary>
internal sealed unsafe class AchievementReader(IDataManager data)
{
    // FFXIVClientStructs 7.57.0.9386 documents and generates a 4078-bit
    // main completion bitmap. IsComplete takes the Achievement sheet RowId.
    internal const int CompletionBitCount = 4078;
    private AchievementDefinition[]? catalog;

    internal ProgressionSnapshot Capture()
    {
        try
        {
            var player = PlayerState.Instance();
            var achievement = NativeAchievement.Instance();
            if (player == null || !player->IsLoaded || player->ContentId == 0 || achievement == null)
                return ProgressionSnapshot.Unavailable("achievements", "Loaded character achievement state is unavailable.");
            if (!achievement->IsLoaded())
                return ProgressionSnapshot.Unavailable("achievements",
                    "Achievement completion data has not loaded. Open the Achievements menu yourself, wait for it to load, then ask again.");
            if (achievement->CompletedAchievementsBitArray.BitCount != CompletionBitCount)
                return ProgressionSnapshot.Unavailable("achievements",
                    "Native achievement completion bounds do not match the supported structure.");

            var owner = player->ContentId;
            var snapshot = Describe(DateTimeOffset.UtcNow, GetCatalog(), id =>
            {
                if (!achievement->IsLoaded())
                    throw new InvalidOperationException("Achievement completion data is no longer loaded.");
                var completed = achievement->IsComplete(checked((int)id));
                if (completed != achievement->IsComplete(checked((int)id)))
                    throw new InvalidOperationException("Achievement completion changed during reading.");
                return completed;
            });
            if (PlayerState.Instance() != player || NativeAchievement.Instance() != achievement ||
                !player->IsLoaded || player->ContentId != owner || !achievement->IsLoaded() ||
                achievement->CompletedAchievementsBitArray.BitCount != CompletionBitCount)
                return ProgressionSnapshot.Unavailable("achievements",
                    "Character or achievement readiness changed during reading; try again after loading.");
            return snapshot;
        }
        catch (Exception ex)
        {
            return ProgressionSnapshot.Unavailable("achievements", $"Achievement read unavailable ({ex.GetType().Name}).");
        }
    }

    private AchievementDefinition[] GetCatalog()
    {
        if (catalog is not null) return catalog;
        // Bound traversal including unnamed rows; cache public definitions only.
        var rows = data.GetExcelSheet<AchievementSheet>()
            .Take(ProgressionSnapshot.MaxEntries + 1).ToArray();
        if (rows.Length > ProgressionSnapshot.MaxEntries)
            throw new InvalidOperationException("Achievement catalog exceeds the supported bound.");
        var definitions = rows.Where(row => row.RowId != 0).Select(row =>
            new AchievementDefinition(row.RowId, row.Name.ExtractText().Trim()))
            .Where(row => !string.IsNullOrWhiteSpace(row.Name))
            .Select(row => ProgressionSnapshot.ValidText(row.Name, ProgressionSnapshot.MaxNameLength)
                ? row : row with { Name = "[achievement name unavailable]" })
            .OrderBy(row => row.Id).ToArray();
        if (definitions.Length == 0)
            throw new InvalidOperationException("Achievement catalog is unavailable.");
        return catalog = definitions;
    }

    internal static ProgressionSnapshot Describe(DateTimeOffset capturedAt,
        IEnumerable<AchievementDefinition> definitions, Func<uint, bool> observe, bool loaded = true)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(observe);
        if (!loaded)
            return ProgressionSnapshot.Unavailable("achievements",
                "Achievement completion data has not loaded. Open the Achievements menu yourself, wait for it to load, then ask again.");
        var bounded = definitions.Take(ProgressionSnapshot.MaxEntries + 1).ToArray();
        if (bounded.Length == 0 || bounded.Length > ProgressionSnapshot.MaxEntries ||
            bounded.Any(row => row is null || row.Id == 0 ||
                !ProgressionSnapshot.ValidText(row.Name, ProgressionSnapshot.MaxNameLength)) ||
            bounded.Select(row => row.Id).Distinct().Count() != bounded.Length)
            return ProgressionSnapshot.Unavailable("achievements", "Achievement catalog is invalid or unavailable.");

        var entries = new List<ProgressionEntry>();
        foreach (var row in bounded)
        {
            if (row.Id >= CompletionBitCount)
            {
                entries.Add(new(row.Id, row.Name, "unknown", UnknownReason:
                    "This named Achievement sheet row is outside the supported completion bitmap; its completion is unknown."));
                continue;
            }
            // IsLoaded is explicitly documented as the prerequisite to completion
            // checks. False here describes the already loaded main bitmap, not
            // an unloaded zero buffer or a claim about current server freshness.
            var completed = observe(row.Id);
            entries.Add(new(row.Id, row.Name, completed ? "completed" : "not_completed", Completed: completed));
        }
        return new("achievements", capturedAt, true, "loaded_completion_bitmap", entries);
    }
}

internal sealed record AchievementDefinition(uint Id, string Name);
