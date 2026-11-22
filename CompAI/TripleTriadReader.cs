using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace CompAI;

/// <summary>Framework-thread only. Registered-card observations; no decks, NPC records or item quantities.</summary>
internal sealed unsafe class TripleTriadReader(IDataManager data)
{
    internal const int UnlockBitCount = 476;
    internal const int UnlockByteCount = 60;
    private TripleTriadCatalog? catalog;

    internal CollectionSnapshot Capture()
    {
        try
        {
            var player = PlayerState.Instance();
            var ui = UIState.Instance();
            if (player == null || !player->IsLoaded || player->ContentId == 0 || ui == null)
                return Unavailable("Loaded character Triple Triad card state is unavailable.");
            var owner = player->ContentId;
            var currentCatalog = GetCatalog();
            if (PlayerState.Instance() != player || UIState.Instance() != ui || !player->IsLoaded || player->ContentId != owner)
                return Unavailable("Character Triple Triad state changed before reading; try again after loading.");

            var first = ReadBitmap(ui);
            var snapshot = Describe(DateTimeOffset.UtcNow, currentCatalog.Definitions,
                id => ui->IsTripleTriadCardUnlocked(checked((ushort)id)));
            var second = ReadBitmap(ui);
            if (PlayerState.Instance() != player || UIState.Instance() != ui || !player->IsLoaded ||
                player->ContentId != owner || !StableBitmap(first, second) || !CatalogMatchesCurrent(currentCatalog))
            {
                catalog = null;
                return Unavailable("Character, card observations or public Triple Triad definitions changed during reading.");
            }
            return snapshot;
        }
        catch (Exception ex)
        {
            catalog = null;
            return Unavailable($"Triple Triad card read unavailable ({ex.GetType().Name}).");
        }
    }

    private static byte[] ReadBitmap(UIState* ui)
    {
        // Exact installed ClientStructs dd57986b: 476 card bits in 60 bytes.
        // The helper explicitly takes TripleTriadCardResident RowId. The opaque native
        // bit mapping is not reproduced here; bytes are copied only to reject changes.
        // https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/FFXIV/Client/Game/UI/UIState.cs
        if (ui->UnlockedTripleTriadCardsBitArray.BitCount != UnlockBitCount ||
            ui->UnlockedTripleTriadCards.Length != UnlockByteCount)
            throw new InvalidOperationException("Triple Triad card structure bounds are unsupported.");
        return ui->UnlockedTripleTriadCards.ToArray();
    }

    internal static bool StableBitmap(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second) =>
        first.Length == UnlockByteCount && second.Length == UnlockByteCount && first.SequenceEqual(second);

    private TripleTriadCatalog GetCatalog()
    {
        var gameData = data.GameData;
        var language = data.Language.ToString();
        var names = data.GetExcelSheet<TripleTriadCard>();
        var residents = data.GetExcelSheet<TripleTriadCardResident>();
        if (catalog is { } cached && cached.Matches(names.RawSheet, residents.RawSheet, gameData, language))
            return cached;
        catalog = null;
        // Installed Lumina.Excel SourceLink pins these schemas. Only the card title and
        // resident key are used; Order, acquisition, description and NPC fields are excluded.
        // https://github.com/xivdev/EXDSchema/blob/75f674655bb89d6172effa3a5a2d93bcfc7deb51/TripleTriadCard.yml
        // https://github.com/xivdev/EXDSchema/blob/75f674655bb89d6172effa3a5a2d93bcfc7deb51/TripleTriadCardResident.yml
        var definitions = BuildCatalog(names.Select(row => new TripleTriadCardName(row.RowId, row.Name.ExtractText().Trim())),
            residents.Select(row => row.RowId));
        var result = new TripleTriadCatalog(names.RawSheet, residents.RawSheet, gameData, language, definitions);
        if (!CatalogMatchesCurrent(result))
            throw new InvalidOperationException("Public Triple Triad definitions changed during capture.");
        return catalog = result;
    }

    private bool CatalogMatchesCurrent(TripleTriadCatalog current) =>
        current.Matches(data.GetExcelSheet<TripleTriadCard>().RawSheet, data.GetExcelSheet<TripleTriadCardResident>().RawSheet,
            data.GameData, data.Language.ToString());

    internal static TripleTriadDefinition[] BuildCatalog(IEnumerable<TripleTriadCardName> names, IEnumerable<uint> residentIds)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(residentIds);
        var rows = names.Take(CollectionSnapshot.MaxEntries + 1).ToArray();
        var residents = residentIds.Take(CollectionSnapshot.MaxEntries + 1).ToArray();
        if (rows.Length == 0 || rows.Length > CollectionSnapshot.MaxEntries ||
            residents.Length == 0 || residents.Length > CollectionSnapshot.MaxEntries ||
            rows.Any(row => row is null) || rows.Select(row => row.Id).Distinct().Count() != rows.Length ||
            residents.Distinct().Count() != residents.Length)
            throw new InvalidOperationException("Triple Triad public catalog is invalid or exceeds its bound.");
        var residentSet = residents.ToHashSet();
        // Both installed sheets have the same 0..475 key domain. Join on RowId, never
        // enumeration position, Resident.Order (display sorting), an item ID or an NPC ID.
        var result = rows.Where(row => row.Id != 0 && !string.IsNullOrWhiteSpace(row.Name))
            .Select(row => new TripleTriadDefinition(row.Id, row.Name, residentSet.Contains(row.Id)))
            .OrderBy(row => row.Id).ToArray();
        if (result.Length == 0 || result.Any(row => !CollectionSnapshot.ValidText(row.Name, CollectionSnapshot.MaxNameLength)))
            throw new InvalidOperationException("Named Triple Triad card definitions are invalid or unavailable.");
        return result;
    }

    internal static CollectionSnapshot Describe(DateTimeOffset capturedAt, IEnumerable<TripleTriadDefinition> definitions,
        Func<uint, bool> observe, bool loaded = true)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(observe);
        if (!loaded) return Unavailable("Loaded character Triple Triad card state is unavailable.");
        var rows = definitions.Take(CollectionSnapshot.MaxEntries + 1).ToArray();
        if (rows.Length == 0 || rows.Length > CollectionSnapshot.MaxEntries ||
            rows.Any(row => row is null || row.Id == 0 || !CollectionSnapshot.ValidText(row.Name, CollectionSnapshot.MaxNameLength)) ||
            rows.Select(row => row.Id).Distinct().Count() != rows.Length)
            return Unavailable("Triple Triad card catalog is invalid or unavailable.");
        var entries = new List<CollectionEntry>();
        foreach (var row in rows)
        {
            if (!row.HasResident || row.Id >= UnlockBitCount)
            {
                entries.Add(new(row.Id, row.Name, "unknown", null,
                    "This named card has no supported resident mapping within native bounds; registered-card state is unknown."));
                continue;
            }
            var unlocked = observe(row.Id);
            if (unlocked != observe(row.Id))
                return Unavailable("Triple Triad card observations changed during reading.");
            entries.Add(new(row.Id, row.Name, unlocked ? "unlocked" : "not_observed_unlocked", unlocked ? true : null,
                unlocked ? null : "A native false was observed, but card-specific negative readiness is unverified; this does not mean unowned or unregistered."));
        }
        return new("triple_triad", capturedAt, true, entries);
    }

    private static CollectionSnapshot Unavailable(string reason) => CollectionSnapshot.Unavailable("triple_triad", reason);
}

internal sealed record TripleTriadCardName(uint Id, string Name);
internal sealed record TripleTriadDefinition(uint Id, string Name, bool HasResident);
internal sealed record TripleTriadCatalog(object NameSheet, object ResidentSheet, object GameData, string Language,
    TripleTriadDefinition[] Definitions)
{
    internal bool Matches(object nameSheet, object residentSheet, object gameData, string language) =>
        ReferenceEquals(NameSheet, nameSheet) && ReferenceEquals(ResidentSheet, residentSheet) &&
        ReferenceEquals(GameData, gameData) && Language == language;
}
