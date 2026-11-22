using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace CompAI;

/// <summary>Framework-thread only. Captures the requested category once, without opening menus or storing personal state.</summary>
internal sealed unsafe class CollectionReader(IDataManager data)
{
    private readonly BlueMageReader blueMage = new(data);
    private readonly MasterBestiaryReader bestiary = new(data);
    private readonly TripleTriadReader tripleTriad = new(data);
    private readonly OccultRecordReader occultRecords = new(data);
    private readonly CollectionToolReader tools = new(data);
    // Pinned generated structures expose 359 mount bits and 600 companion bits.
    // Mount IDs passed to the documented API are sheet RowIds. Mount.Order is a
    // conservative support screen; the opaque native routine's internal offset is not inferred here.
    // Companion IDs directly address its generated bit array; Companion.Order is a UI sort order.
    internal const int MountUnlockBitCount = 359;
    internal const int MinionUnlockBitCount = 600;
    internal const int OrnamentUnlockBitCount = 59;
    internal const int GlassesStyleUnlockBitCount = 62;
    internal const int OrchestrionUnlockBitCount = 892;
    private readonly Dictionary<string, CollectionDefinition[]> catalogs = new(StringComparer.Ordinal);

    internal CollectionSnapshot Capture(string category)
    {
        CollectionSnapshot.ValidateCategory(category);
        if (category == "blue_mage_spells") return this.blueMage.Capture();
        if (category == "beastmaster_bestiary") return this.bestiary.Capture();
        if (category == "triple_triad") return this.tripleTriad.Capture();
        if (category == "occult_records") return this.occultRecords.Capture();
        if (category == "collection_tools") return this.tools.Capture();
        try
        {
            var player = PlayerState.Instance();
            var ui = UIState.Instance();
            if (player == null || !player->IsLoaded || player->ContentId == 0 || ui == null)
                return CollectionSnapshot.Unavailable(category, "Loaded character collection state is unavailable.");
            var supportedBounds = category switch
            {
                "mounts" or "minions" => player->UnlockedMountsBitArray.BitCount == MountUnlockBitCount &&
                    ui->UnlockedCompanionsBitArray.BitCount == MinionUnlockBitCount,
                "fashion_accessories" => player->UnlockedOrnamentsBitArray.BitCount == OrnamentUnlockBitCount,
                "facewear" => player->UnlockedGlassesStylesBitArray.BitCount == GlassesStyleUnlockBitCount,
                "orchestrion" => player->UnlockedOrchestrionRollsBitArray.BitCount == OrchestrionUnlockBitCount,
                // IsEmoteUnlocked resolves both unlock links and quest conditions internally.
                // Emote RowId is not an index into UIState.UnlockLinks.
                "emotes" => true,
                _ => false,
            };
            if (!supportedBounds)
                return CollectionSnapshot.Unavailable(category, "Native collection bounds do not match the supported structure.");
            var owner = player->ContentId;
            var definitions = GetCatalog(category);
            var snapshot = Describe(category, DateTimeOffset.UtcNow, definitions, id =>
            {
                bool Observe() => category switch
                {
                    "mounts" => player->IsMountUnlocked(id),
                    "minions" => ui->IsCompanionUnlocked(id),
                    "fashion_accessories" => player->IsOrnamentUnlocked(id),
                    // Pass the Glasses RowId, never the shared GlassesStyle RowId.
                    "facewear" => player->IsGlassesUnlocked(checked((ushort)id)),
                    "emotes" => ui->IsEmoteUnlocked(checked((ushort)id)),
                    "orchestrion" => player->IsOrchestrionRollUnlocked(id),
                    _ => throw new InvalidOperationException("Unsupported collection category."),
                };
                var observed = Observe();
                var repeated = Observe();
                if (observed != repeated)
                    throw new InvalidOperationException("Collection state changed during reading.");
                return observed;
            });
            if (PlayerState.Instance() != player || UIState.Instance() != ui || !player->IsLoaded || player->ContentId != owner)
                return CollectionSnapshot.Unavailable(category, "Character collection state changed during reading; try again after loading.");
            return snapshot;
        }
        catch (Exception ex)
        {
            return CollectionSnapshot.Unavailable(category, $"Collection read unavailable ({ex.GetType().Name}).");
        }
    }

    private CollectionDefinition[] GetCatalog(string category)
    {
        if (catalogs.TryGetValue(category, out var catalog)) return catalog;
        IEnumerable<CollectionDefinition> rows = category switch
        {
            "mounts" => data.GetExcelSheet<Mount>().Select(row =>
                new CollectionDefinition(row.RowId, row.Singular.ExtractText().Trim(), row.Order)),
            "minions" => data.GetExcelSheet<Companion>().Select(row =>
                new CollectionDefinition(row.RowId, row.Singular.ExtractText().Trim(), null)),
            "fashion_accessories" => data.GetExcelSheet<Ornament>().Select(row =>
                new CollectionDefinition(row.RowId, row.Singular.ExtractText().Trim(), null)),
            "facewear" => data.GetExcelSheet<Glasses>().Select(FacewearDefinition),
            "emotes" => data.GetExcelSheet<Emote>().Select(row =>
                new CollectionDefinition(row.RowId, row.Name.ExtractText().Trim(), null)),
            "orchestrion" => data.GetExcelSheet<Orchestrion>().Select(row =>
                new CollectionDefinition(row.RowId, row.Name.ExtractText().Trim(), null)),
            _ => throw new ArgumentException("Invalid collection category.", nameof(category)),
        };
        // Bound sheet traversal too, including unnamed rows that do not enter the public named catalog.
        var bounded = rows.Take(CollectionSnapshot.MaxEntries + 1).ToArray();
        if (bounded.Length > CollectionSnapshot.MaxEntries)
            throw new InvalidOperationException("Collection catalog exceeds the supported bound.");
        catalog = bounded.Where(row => row.Id != 0 && !string.IsNullOrWhiteSpace(row.Name))
            .Select(row => CollectionSnapshot.ValidText(row.Name, CollectionSnapshot.MaxNameLength)
                ? row : row with { Name = "[collection name unavailable]" })
            .OrderBy(row => row.Id).ToArray();
        if (category == "facewear") catalog = GroupFacewear(catalog);
        if (catalog.Length == 0)
            throw new InvalidOperationException("Collection catalog is unavailable.");
        catalogs.Add(category, catalog);
        return catalog;
    }

    private static CollectionDefinition FacewearDefinition(Glasses row)
    {
        var validStyle = row.Style.IsValid;
        return new(row.RowId, (validStyle ? row.Style.Value.Name : row.Name).ExtractText().Trim(), null,
            validStyle ? row.Style.RowId : null);
    }

    internal static CollectionDefinition[] GroupFacewear(IEnumerable<CollectionDefinition> rows)
    {
        // Keep real Glasses RowIds for the documented native API. A style (including style 0)
        // unlocks all its colour variants, so only its lowest nonzero Glasses RowId is returned.
        // Invalid mappings remain individually unknown instead of being combined as one style.
        var bounded = rows.Take(CollectionSnapshot.MaxEntries + 1).ToArray();
        if (bounded.Length > CollectionSnapshot.MaxEntries)
            throw new InvalidOperationException("Facewear catalog exceeds the supported bound.");
        return bounded.Where(row => row.FacewearStyle is null)
            .Concat(bounded.Where(row => row.Id != 0 && row.FacewearStyle is not null)
                .GroupBy(row => row.FacewearStyle).Select(group => group.MinBy(row => row.Id)!))
            .OrderBy(row => row.Id).ToArray();
    }

    internal static CollectionSnapshot Describe(string category, DateTimeOffset capturedAt,
        IEnumerable<CollectionDefinition> definitions, Func<uint, bool> observe, bool loaded = true)
    {
        CollectionSnapshot.ValidateCategory(category);
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(observe);
        if (!loaded)
            return CollectionSnapshot.Unavailable(category, "Loaded character collection state is unavailable.");
        var bounded = definitions.Take(CollectionSnapshot.MaxEntries + 1).ToArray();
        if (bounded.Length == 0 || bounded.Length > CollectionSnapshot.MaxEntries ||
            bounded.Any(row => row is null || row.Id == 0 || !CollectionSnapshot.ValidText(row.Name, CollectionSnapshot.MaxNameLength)) ||
            bounded.Select(row => row.Id).Distinct().Count() != bounded.Length)
            return CollectionSnapshot.Unavailable(category, "Collection catalog is invalid or unavailable.");
        var entries = new List<CollectionEntry>();
        foreach (var row in bounded)
        {
            var supported = category switch
            {
                "mounts" => row.Id <= CollectionSnapshot.MaxEntries && row.MountOrder is >= 0 and < MountUnlockBitCount &&
                    row.FacewearStyle is null,
                "minions" => row.Id < MinionUnlockBitCount && row.MountOrder is null && row.FacewearStyle is null,
                "fashion_accessories" => row.Id < OrnamentUnlockBitCount && row.MountOrder is null && row.FacewearStyle is null,
                "facewear" => row.Id <= ushort.MaxValue && row.Id <= CollectionSnapshot.MaxEntries &&
                    row.MountOrder is null && row.FacewearStyle is < GlassesStyleUnlockBitCount,
                "emotes" => row.Id <= ushort.MaxValue && row.Id <= CollectionSnapshot.MaxEntries &&
                    row.MountOrder is null && row.FacewearStyle is null,
                "orchestrion" => row.Id < OrchestrionUnlockBitCount && row.MountOrder is null && row.FacewearStyle is null,
                _ => false,
            };
            if (!supported)
            {
                entries.Add(new(row.Id, row.Name, "unknown", null,
                    "This named sheet row is outside the supported collection mapping or native bounds; its unlock state is unknown."));
                continue;
            }
            var unlocked = observe(row.Id);
            entries.Add(new(row.Id, row.Name, unlocked ? "unlocked" : "not_observed_unlocked", unlocked ? true : null,
                unlocked ? null : "A native false was observed, but collection-specific negative-state readiness is unverified; this does not mean locked."));
        }
        return new(category, capturedAt, true, entries);
    }
}

internal sealed record CollectionDefinition(uint Id, string Name, int? MountOrder, uint? FacewearStyle = null);
