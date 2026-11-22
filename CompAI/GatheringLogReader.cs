using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace CompAI;

/// <summary>Framework-thread only. Reads gathered-before flags for the public gathering notebook; no menu or server requests.</summary>
internal sealed unsafe class GatheringLogReader(IDataManager data)
{
    // These are pinned structure/catalog shapes, not raw GatheringItem ID limits.
    // The documented native method takes a GatheringItem RowId and owns its mapping.
    internal const int GatheredBitCount = 816;
    internal const int MaxNotebookRows = 512;
    internal const int NotebookEntriesPerRow = 100;
    private GatheringLogDefinition[]? catalog;

    internal ProgressionSnapshot Capture()
    {
        try
        {
            var player = PlayerState.Instance();
            var quests = QuestManager.Instance();
            if (player == null || !player->IsLoaded || player->ContentId == 0 || quests == null)
                return ProgressionSnapshot.Unavailable("gathering_log", "Loaded character gathering-log state is unavailable.");
            if (quests->GatheredGatheringItemsBitArray.BitCount != GatheredBitCount)
                return ProgressionSnapshot.Unavailable("gathering_log",
                    "Native gathering-log structure does not match the supported bounds.");
            var owner = player->ContentId;
            var snapshot = Describe(DateTimeOffset.UtcNow, GetCatalog(), id =>
            {
                var gathered = QuestManager.IsGatheringItemGathered(checked((ushort)id));
                if (gathered != QuestManager.IsGatheringItemGathered(checked((ushort)id)))
                    throw new InvalidOperationException("Gathering-log state changed during reading.");
                return gathered;
            });
            if (PlayerState.Instance() != player || QuestManager.Instance() != quests ||
                !player->IsLoaded || player->ContentId != owner ||
                quests->GatheredGatheringItemsBitArray.BitCount != GatheredBitCount)
                return ProgressionSnapshot.Unavailable("gathering_log",
                    "Character or gathering-log state changed during reading; try again after loading.");
            return snapshot;
        }
        catch (Exception ex)
        {
            return ProgressionSnapshot.Unavailable("gathering_log", $"Gathering-log read unavailable ({ex.GetType().Name}).");
        }
    }

    private GatheringLogDefinition[] GetCatalog()
    {
        if (catalog is not null) return catalog;
        var notebook = data.GetExcelSheet<GatheringNotebookList>().Take(MaxNotebookRows + 1).ToArray();
        if (notebook.Length > MaxNotebookRows)
            throw new InvalidOperationException("Gathering notebook exceeds the supported row bound.");
        var ids = new HashSet<uint>();
        foreach (var row in notebook)
        {
            if (row.GatheringItem.Count != NotebookEntriesPerRow)
                throw new InvalidOperationException("Gathering notebook row shape is unsupported.");
            foreach (var reference in row.GatheringItem)
            {
                // Installed sheets use both zero and signed -1 for empty entries.
                if (reference.RowId is 0 or uint.MaxValue) continue;
                if (!reference.IsValid)
                    throw new InvalidOperationException("Gathering notebook contains an unresolved entry.");
                ids.Add(reference.RowId);
                if (ids.Count > ProgressionSnapshot.MaxEntries)
                    throw new InvalidOperationException("Gathering notebook exceeds the supported catalog bound.");
            }
        }
        var gathering = data.GetExcelSheet<GatheringItem>();
        var definitions = new List<GatheringLogDefinition>();
        foreach (var id in ids.Order())
        {
            if (!gathering.TryGetRow(id, out var row))
                throw new InvalidOperationException("Gathering notebook entry is unavailable.");
            // GatheringItem.Item is polymorphic. EventItem and other namespaces
            // must never be reinterpreted as ordinary inventory Item IDs.
            if (row.Item.RowId == 0 || !row.Item.Is<Item>() ||
                row.Item.GetValueOrDefault<Item>() is not { } item) continue;
            var name = item.Name.ExtractText().Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!ProgressionSnapshot.ValidText(name, ProgressionSnapshot.MaxNameLength))
                name = "[gathering item name unavailable]";
            definitions.Add(new(id, item.RowId, name));
        }
        if (definitions.Count == 0)
            throw new InvalidOperationException("Gathering notebook names are unavailable.");
        return catalog = definitions.ToArray();
    }

    internal static ProgressionSnapshot Describe(DateTimeOffset capturedAt,
        IEnumerable<GatheringLogDefinition> definitions, Func<uint, bool> observe, bool loaded = true)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(observe);
        if (!loaded)
            return ProgressionSnapshot.Unavailable("gathering_log", "Loaded character gathering-log state is unavailable.");
        var rows = definitions.Take(ProgressionSnapshot.MaxEntries + 1).ToArray();
        if (rows.Length == 0 || rows.Length > ProgressionSnapshot.MaxEntries ||
            rows.Any(row => row is null || row.Id == 0 || row.ItemId == 0 ||
                !ProgressionSnapshot.ValidText(row.Name, ProgressionSnapshot.MaxNameLength)) ||
            rows.Select(row => row.Id).Distinct().Count() != rows.Length)
            return ProgressionSnapshot.Unavailable("gathering_log", "Gathering notebook catalog is invalid or unavailable.");
        var entries = new List<ProgressionEntry>();
        foreach (var row in rows)
        {
            if (row.Id > ushort.MaxValue)
            {
                entries.Add(new(row.Id, row.Name, "unknown", UnknownReason:
                    "This named gathering-notebook entry is outside the native method's supported ID width; completion is unknown."));
                continue;
            }
            var gathered = observe(row.Id);
            entries.Add(new(row.Id, row.Name, gathered ? "completed" : "not_observed_complete",
                Completed: gathered ? true : null, UnknownReason: gathered ? null :
                    "A native false was observed, but gathering-log negative-state readiness is unverified; this does not mean never gathered."));
        }
        return new("gathering_log", capturedAt, true, "positive_observations_only", entries);
    }
}

internal sealed record GatheringLogDefinition(uint Id, uint ItemId, string Name);
