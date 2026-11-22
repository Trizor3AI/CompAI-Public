using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace CompAI;

/// <summary>Framework-thread only. YKW catalog minion registrations, not event progress or weapon ownership.</summary>
internal sealed unsafe class YokaiCollectionReader(IDataManager data)
{
    internal const string Category = "yokai_minions";
    internal const int MaxRecords = 128;
    internal const int CompanionBitCount = 600;
    private YokaiCollectionCatalog? catalog;

    internal CollectionSnapshot Capture()
    {
        try
        {
            var player = PlayerState.Instance();
            var ui = UIState.Instance();
            if (player == null || !player->IsLoaded || player->ContentId == 0 || ui == null)
                return Unavailable("Loaded character Yo-kai minion state is unavailable.");
            if (ui->UnlockedCompanionsBitArray.BitCount != CompanionBitCount)
                return Unavailable("Native companion bounds do not match the supported structure.");
            var owner = player->ContentId;
            var current = GetCatalog();
            var flags = ui->UnlockedCompanions.ToArray();
            // YKW.Companion is a typed public-sheet reference. Pass its Companion RowId,
            // not the YKW notebook number, to the documented companion unlock helper.
            var snapshot = Describe(DateTimeOffset.UtcNow, current.Definitions, id => ui->IsCompanionUnlocked(id));
            if (PlayerState.Instance() != player || UIState.Instance() != ui || !player->IsLoaded ||
                player->ContentId != owner || !flags.AsSpan().SequenceEqual(ui->UnlockedCompanions) || !CatalogMatches(current))
                return Unavailable("Character or public Yo-kai definitions changed during capture.");
            return snapshot;
        }
        catch (Exception ex) { return Unavailable($"Yo-kai minion read unavailable ({ex.GetType().Name})."); }
    }

    private YokaiCollectionCatalog GetCatalog()
    {
        if (catalog is { } cached && CatalogMatches(cached)) return cached;
        catalog = null;
        var sheet = data.GetExcelSheet<YKW>();
        var rows = sheet.Take(MaxRecords + 2).ToArray();
        if (rows.Length > MaxRecords + 1) throw new InvalidOperationException("Yo-kai catalog exceeds supported bounds.");
        var definitions = BuildCatalog(rows.Where(row => row.RowId != 0).Select(row => new YokaiMinionDefinition(
            row.RowId, row.Companion.IsValid ? row.Companion.Value.Singular.ExtractText().Trim() : "",
            row.Companion.RowId)));
        return catalog = new(sheet.RawSheet, data.GetExcelSheet<Companion>().RawSheet,
            data.GameData, data.Language.ToString(), definitions);
    }

    private bool CatalogMatches(YokaiCollectionCatalog current) =>
        ReferenceEquals(current.Sheet, data.GetExcelSheet<YKW>().RawSheet) &&
        ReferenceEquals(current.CompanionSheet, data.GetExcelSheet<Companion>().RawSheet) &&
        ReferenceEquals(current.GameData, data.GameData) && current.Language == data.Language.ToString();

    internal static YokaiMinionDefinition[] BuildCatalog(IEnumerable<YokaiMinionDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var rows = definitions.Take(MaxRecords + 1).ToArray();
        if (rows.Length is 0 or > MaxRecords || rows.Any(row => row is null || row.Id is 0 or > MaxRecords ||
                row.CompanionId is 0 or >= CompanionBitCount ||
                !CollectionSnapshot.ValidText(row.Name, CollectionSnapshot.MaxNameLength)) ||
            rows.Select(row => row.Id).Distinct().Count() != rows.Length ||
            rows.Select(row => row.CompanionId).Distinct().Count() != rows.Length)
            throw new InvalidOperationException("Yo-kai definitions are invalid or unsupported.");
        return rows.OrderBy(row => row.Id).ToArray();
    }

    internal static CollectionSnapshot Describe(DateTimeOffset capturedAt, IEnumerable<YokaiMinionDefinition> definitions,
        Func<uint, bool> observe, bool loaded = true)
    {
        ArgumentNullException.ThrowIfNull(observe);
        if (!loaded) return Unavailable("Loaded character Yo-kai minion state is unavailable.");
        YokaiMinionDefinition[] rows;
        try { rows = BuildCatalog(definitions); }
        catch (InvalidOperationException) { return Unavailable("Yo-kai definitions are invalid or unsupported."); }
        var entries = new List<CollectionEntry>();
        foreach (var row in rows)
        {
            var unlocked = observe(row.CompanionId);
            if (observe(row.CompanionId) != unlocked) return Unavailable("Yo-kai registration changed during capture.");
            entries.Add(new(row.Id, row.Name, unlocked ? "unlocked" : "not_observed_unlocked", unlocked ? true : null,
                unlocked ? null : "No positive companion registration was observed; negative readiness is unverified, so this does not prove a missing minion."));
        }
        return new(Category, capturedAt, true, entries);
    }

    private static CollectionSnapshot Unavailable(string reason) => CollectionSnapshot.Unavailable(Category, reason);
}

internal sealed record YokaiMinionDefinition(uint Id, string Name, uint CompanionId);
internal sealed record YokaiCollectionCatalog(object Sheet, object CompanionSheet, object GameData, string Language,
    YokaiMinionDefinition[] Definitions);
