using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace CompAI;

/// <summary>Framework-thread only. Collection-tool access, not registration of records inside each tool.</summary>
internal sealed unsafe class CollectionToolReader(IDataManager data)
{
    internal const string Category = "collection_tools";
    internal const int MaxRecords = 255;
    private CollectionToolCatalog? catalog;

    internal CollectionSnapshot Capture()
    {
        try
        {
            var player = PlayerState.Instance();
            if (player == null || !player->IsLoaded || player->ContentId == 0)
                return Unavailable("Loaded character collection-tool state is unavailable.");
            var owner = player->ContentId;
            var current = GetCatalog();
            // The pinned PlayerState explicitly documents McGuffin sheet RowIds.
            // UnlockedMcGuffins is a byte-keyed set; never pass a UIData RowId.
            // https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/FFXIV/Client/Game/UI/PlayerState.cs
            var snapshot = Describe(DateTimeOffset.UtcNow, current.Definitions, id => player->IsMcGuffinUnlocked(id));
            if (PlayerState.Instance() != player || !player->IsLoaded || player->ContentId != owner || !CatalogMatches(current))
                return Unavailable("Character or public collection-tool definitions changed during capture.");
            return snapshot;
        }
        catch (Exception ex) { return Unavailable($"Collection-tool read unavailable ({ex.GetType().Name})."); }
    }

    private CollectionToolCatalog GetCatalog()
    {
        if (catalog is { } cached && CatalogMatches(cached)) return cached;
        catalog = null;
        var sheet = data.GetExcelSheet<McGuffin>();
        var rows = sheet.Take(MaxRecords + 2).ToArray();
        if (rows.Length > MaxRecords + 1) throw new InvalidOperationException("Collection-tool catalog exceeds supported bounds.");
        // Blank placeholder rows have no named tool. Do not infer one from their index.
        var definitions = BuildCatalog(rows.Where(row => row.RowId != 0 && row.UIData.RowId != 0)
            .Select(row => new CollectionToolDefinition(row.RowId,
                row.UIData.IsValid ? row.UIData.Value.Name.ExtractText().Trim() : "", row.UIData.RowId)));
        return catalog = new(sheet.RawSheet, data.GetExcelSheet<McGuffinUIData>().RawSheet,
            data.GameData, data.Language.ToString(), definitions);
    }

    private bool CatalogMatches(CollectionToolCatalog current) =>
        ReferenceEquals(current.Sheet, data.GetExcelSheet<McGuffin>().RawSheet) &&
        ReferenceEquals(current.NamesSheet, data.GetExcelSheet<McGuffinUIData>().RawSheet) &&
        ReferenceEquals(current.GameData, data.GameData) && current.Language == data.Language.ToString();

    internal static CollectionToolDefinition[] BuildCatalog(IEnumerable<CollectionToolDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var rows = definitions.Take(MaxRecords + 1).ToArray();
        if (rows.Length is 0 or > MaxRecords || rows.Any(row => row is null || row.Id is 0 or > MaxRecords ||
                row.UIDataId == 0 || !CollectionSnapshot.ValidText(row.Name, CollectionSnapshot.MaxNameLength)) ||
            rows.Select(row => row.Id).Distinct().Count() != rows.Length ||
            rows.Select(row => row.UIDataId).Distinct().Count() != rows.Length)
            throw new InvalidOperationException("Collection-tool definitions are invalid or unsupported.");
        return rows.OrderBy(row => row.Id).ToArray();
    }

    internal static CollectionSnapshot Describe(DateTimeOffset capturedAt, IEnumerable<CollectionToolDefinition> definitions,
        Func<uint, bool> observe, bool loaded = true)
    {
        ArgumentNullException.ThrowIfNull(observe);
        if (!loaded) return Unavailable("Loaded character collection-tool state is unavailable.");
        CollectionToolDefinition[] rows;
        try { rows = BuildCatalog(definitions); }
        catch (InvalidOperationException) { return Unavailable("Collection-tool definitions are invalid or unsupported."); }
        var entries = new List<CollectionEntry>();
        foreach (var row in rows)
        {
            var unlocked = observe(row.Id);
            if (observe(row.Id) != unlocked) return Unavailable("Collection-tool access changed during capture.");
            entries.Add(new(row.Id, row.Name, unlocked ? "unlocked" : "not_observed_unlocked", unlocked ? true : null,
                unlocked ? null : "No positive access flag was observed; negative readiness is unverified. This says nothing about records inside the tool."));
        }
        return new(Category, capturedAt, true, entries);
    }

    private static CollectionSnapshot Unavailable(string reason) => CollectionSnapshot.Unavailable(Category, reason);
}

internal sealed record CollectionToolDefinition(uint Id, string Name, uint UIDataId);
internal sealed record CollectionToolCatalog(object Sheet, object NamesSheet, object GameData, string Language,
    CollectionToolDefinition[] Definitions);
