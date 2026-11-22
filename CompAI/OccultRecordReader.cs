using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace CompAI;

/// <summary>Framework-thread only. Copies permanent unlock-link observations; never opens or requests a notebook.</summary>
internal sealed unsafe class OccultRecordReader(IDataManager data)
{
    internal const string Category = "occult_records";
    internal const int MaxRecords = 255;
    internal const int UnlockLinkBitCount = 736;
    private OccultRecordCatalog? catalog;

    internal CollectionSnapshot Capture()
    {
        try
        {
            var player = PlayerState.Instance();
            var ui = UIState.Instance();
            if (player == null || !player->IsLoaded || player->ContentId == 0 || ui == null)
                return Unavailable("Loaded character Occult Record state is unavailable.");
            if (ui->UnlockLinksBitArray.BitCount != UnlockLinkBitCount)
                return Unavailable("Native unlock-link bounds do not match the supported structure.");
            var owner = player->ContentId;
            var current = GetCatalog();
            var initialFlags = ui->UnlockLinks.ToArray();
            // AgentMKDLoreBook.LoreEntry embeds MKDLore.UnlockLink. Dalamud's
            // UnlockState.IsMKDLoreUnlocked passes this field to its unlock-link helper.
            // https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/FFXIV/Client/UI/Agent/AgentMKDLoreBook.cs
            // https://github.com/goatcorp/Dalamud/blob/30509c3b4fd00edd9a3d65b7bbf16433d84be03b/Dalamud/Game/UnlockState/UnlockState.cs
            var snapshot = Describe(DateTimeOffset.UtcNow, current.Definitions, id => ui->IsUnlockLinkUnlocked(id));
            if (PlayerState.Instance() != player || UIState.Instance() != ui || !player->IsLoaded ||
                player->ContentId != owner || !initialFlags.AsSpan().SequenceEqual(ui->UnlockLinks) || !CatalogMatches(current))
                return Unavailable("Character or public Occult Record definitions changed during capture.");
            return snapshot;
        }
        catch (Exception ex)
        {
            return Unavailable($"Occult Record read unavailable ({ex.GetType().Name}).");
        }
    }

    private OccultRecordCatalog GetCatalog()
    {
        if (catalog is { } cached && CatalogMatches(cached)) return cached;
        catalog = null;
        var sheet = data.GetExcelSheet<MKDLore>();
        var rows = sheet.Take(MaxRecords + 2).ToArray();
        if (rows.Length > MaxRecords + 1)
            throw new InvalidOperationException("Occult Record catalog exceeds supported bounds.");
        var definitions = BuildCatalog(rows.Where(row => row.RowId != 0)
            .Select(row => new OccultRecordDefinition(row.RowId, row.Name.ExtractText().Trim(), row.UnlockLink)));
        return catalog = new(sheet.RawSheet, data.GameData, data.Language.ToString(), definitions);
    }

    private bool CatalogMatches(OccultRecordCatalog current) =>
        ReferenceEquals(current.Sheet, data.GetExcelSheet<MKDLore>().RawSheet) &&
        ReferenceEquals(current.GameData, data.GameData) && current.Language == data.Language.ToString();

    internal static OccultRecordDefinition[] BuildCatalog(IEnumerable<OccultRecordDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var rows = definitions.Take(MaxRecords + 1).ToArray();
        if (rows.Length is 0 or > MaxRecords ||
            rows.Any(row => row is null || row.Id is 0 or > MaxRecords ||
                row.UnlockLink is 0 or >= UnlockLinkBitCount ||
                !CollectionSnapshot.ValidText(row.Name, CollectionSnapshot.MaxNameLength)) ||
            rows.Select(row => row.Id).Distinct().Count() != rows.Length ||
            rows.Select(row => row.UnlockLink).Distinct().Count() != rows.Length)
            throw new InvalidOperationException("Occult Record definitions are invalid or unsupported.");
        return rows.OrderBy(row => row.Id).ToArray();
    }

    internal static CollectionSnapshot Describe(DateTimeOffset capturedAt,
        IEnumerable<OccultRecordDefinition> definitions, Func<uint, bool> observe, bool loaded = true)
    {
        ArgumentNullException.ThrowIfNull(observe);
        if (!loaded) return Unavailable("Loaded character Occult Record state is unavailable.");
        OccultRecordDefinition[] rows;
        try { rows = BuildCatalog(definitions); }
        catch (InvalidOperationException) { return Unavailable("Occult Record definitions are invalid or unsupported."); }
        var entries = new List<CollectionEntry>();
        foreach (var row in rows)
        {
            var unlocked = observe(row.UnlockLink);
            if (observe(row.UnlockLink) != unlocked)
                return Unavailable("Occult Record unlock state changed during capture.");
            entries.Add(new(row.Id, row.Name, unlocked ? "unlocked" : "not_observed_unlocked",
                unlocked ? true : null, unlocked ? null :
                    "No positive unlock was observed; record-specific negative readiness is unverified, so this does not prove a missing record."));
        }
        return new(Category, capturedAt, true, entries);
    }

    private static CollectionSnapshot Unavailable(string reason) => CollectionSnapshot.Unavailable(Category, reason);
}

internal sealed record OccultRecordDefinition(uint Id, string Name, uint UnlockLink);
internal sealed record OccultRecordCatalog(object Sheet, object GameData, string Language, OccultRecordDefinition[] Definitions);
