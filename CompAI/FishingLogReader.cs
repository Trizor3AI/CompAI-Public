using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace CompAI;

/// <summary>Framework-thread only. Copies caught-before flags without opening menus or requesting data.</summary>
internal sealed unsafe class FishingLogReader(IDataManager data)
{
    internal const int FishBitCount = 1522;
    internal const int FishByteCount = 191;
    internal const int SpearfishBitCount = 304;
    internal const int SpearfishByteCount = 38;
    internal const uint SpearfishOffset = 20000;
    private readonly Dictionary<string, FishingLogCatalog> catalogs = new(StringComparer.Ordinal);

    internal ProgressionSnapshot Capture(string category)
    {
        ValidateCategory(category);
        try
        {
            var before = Observe(category);
            if (!before.IsReady(category)) return Unavailable(category);
            var catalog = GetCatalog(category);
            var player = (PlayerState*)before.PlayerAddress;
            var snapshot = Describe(category, DateTimeOffset.UtcNow, catalog.Definitions, id =>
            {
                var caught = category == "fishing_log" ? player->IsFishCaught(id) : player->IsSpearfishCaught(id);
                var again = category == "fishing_log" ? player->IsFishCaught(id) : player->IsSpearfishCaught(id);
                if (caught != again)
                    throw new InvalidOperationException("Caught-before state changed during reading.");
                return caught;
            });
            if (!IsStable(category, before, Observe(category)) || !CatalogMatchesCurrent(category, catalog))
            {
                catalogs.Remove(category);
                return Unavailable(category);
            }
            return snapshot;
        }
        catch (Exception ex)
        {
            catalogs.Remove(category);
            return ProgressionSnapshot.Unavailable(category, $"Fishing-log read unavailable ({ex.GetType().Name}).");
        }
    }

    private static FishingLogObservation Observe(string category)
    {
        var player = PlayerState.Instance();
        if (player == null || !player->IsLoaded || player->ContentId == 0) return default;
        var bits = category == "fishing_log" ? player->CaughtFishBitArray.BitCount : player->CaughtSpearfishBitArray.BitCount;
        var bytes = category == "fishing_log" ? player->CaughtFish : player->CaughtSpearfish;
        if (bits != (category == "fishing_log" ? FishBitCount : SpearfishBitCount) ||
            bytes.Length != (category == "fishing_log" ? FishByteCount : SpearfishByteCount))
            return default;
        // A bounded equality fingerprint detects changes; it is not used to
        // interpret caught flags or infer negative-state readiness.
        return new((nint)player, player->ContentId, player->IsLoaded, bits, bytes.Length, Convert.ToHexString(bytes));
    }

    private FishingLogCatalog GetCatalog(string category)
    {
        var gameData = data.GameData;
        var language = data.Language.ToString();
        var itemSheet = data.GetExcelSheet<Item>();
        object sheet = category == "fishing_log"
            ? data.GetExcelSheet<FishParameter>().RawSheet : data.GetExcelSheet<SpearfishingItem>().RawSheet;
        if (catalogs.TryGetValue(category, out var cached) &&
            cached.Matches(sheet, itemSheet.RawSheet, gameData, language))
            return cached;

        var definitions = new List<FishingLogDefinition>();
        if (category == "fishing_log")
        {
            var rows = data.GetExcelSheet<FishParameter>().Take(ProgressionSnapshot.MaxEntries + 1).ToArray();
            if (rows.Length > ProgressionSnapshot.MaxEntries)
                throw new InvalidOperationException("Fishing catalog exceeds the supported row bound.");
            foreach (var row in rows)
            {
                // The pinned managed API explicitly permits only IsInLog rows.
                // Item is polymorphic; EventItem IDs are a different namespace.
                if (row.RowId == 0 || !row.IsInLog || row.Item.RowId == 0 || !row.Item.Is<Item>()) continue;
                if (!itemSheet.TryGetRow(row.Item.RowId, out var item))
                    throw new InvalidOperationException("A fishing-log Item reference is unavailable.");
                Add(row.RowId, item.RowId, item.Name.ExtractText().Trim());
            }
        }
        else
        {
            var rows = data.GetExcelSheet<SpearfishingItem>().Take(ProgressionSnapshot.MaxEntries + 1).ToArray();
            if (rows.Length > ProgressionSnapshot.MaxEntries)
                throw new InvalidOperationException("Spearfishing catalog exceeds the supported row bound.");
            foreach (var row in rows)
            {
                if (row.RowId == 0 || !row.IsVisible || row.Item.RowId == 0) continue;
                if (!itemSheet.TryGetRow(row.Item.RowId, out var item))
                    throw new InvalidOperationException("A spearfishing-log Item reference is unavailable.");
                Add(row.RowId, item.RowId, item.Name.ExtractText().Trim());
            }
        }
        void Add(uint id, uint itemId, string name)
        {
            if (!ProgressionSnapshot.ValidText(name, ProgressionSnapshot.MaxNameLength))
                throw new InvalidOperationException("A fishing-log name is unsupported.");
            definitions.Add(new(id, itemId, name));
        }
        if (definitions.Count == 0 || definitions.Select(row => row.Id).Distinct().Count() != definitions.Count)
            throw new InvalidOperationException("The fishing-log catalog is empty or ambiguous.");
        var result = new FishingLogCatalog(sheet, itemSheet.RawSheet, gameData, language,
            definitions.OrderBy(row => row.Id).ToArray());
        if (!CatalogMatchesCurrent(category, result))
            throw new InvalidOperationException("Public fishing definitions changed during capture.");
        return catalogs[category] = result;
    }

    private bool CatalogMatchesCurrent(string category, FishingLogCatalog catalog) =>
        catalog.Matches(category == "fishing_log" ? data.GetExcelSheet<FishParameter>().RawSheet :
            data.GetExcelSheet<SpearfishingItem>().RawSheet, data.GetExcelSheet<Item>().RawSheet,
            data.GameData, data.Language.ToString());

    internal static bool IsStable(string category, FishingLogObservation before, FishingLogObservation after) =>
        before.IsReady(category) && after.IsReady(category) && before == after;

    internal static bool IsSupportedId(string category, uint id)
    {
        ValidateCategory(category);
        if (id == 0 || id > int.MaxValue) return false;
        // Exact pinned source: IsFishCaught -> Get((int)FishParameter.RowId);
        // IsSpearfishCaught -> Get((int)SpearfishingItem.RowId - 20000).
        // Installed BitArray.Get rejects index > BitCount (not >=). Also enforce
        // physical byte capacity: e.g. spear index304 would exceed38 bytes.
        // https://github.com/aers/FFXIVClientStructs/blob/d6397a76b3a63c2b247025dc296653182b718ca6/FFXIVClientStructs/FFXIV/Client/Game/UI/PlayerState.cs
        var index = category == "fishing_log" ? (long)id : (long)id - SpearfishOffset;
        return index >= 0 && index <= (category == "fishing_log" ? FishBitCount : SpearfishBitCount) &&
            index < (category == "fishing_log" ? FishByteCount : SpearfishByteCount) * 8;
    }

    internal static ProgressionSnapshot Describe(string category, DateTimeOffset capturedAt,
        IEnumerable<FishingLogDefinition> definitions, Func<uint, bool> observe, bool loaded = true)
    {
        ValidateCategory(category);
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(observe);
        if (!loaded) return Unavailable(category);
        var bounded = definitions.Take(ProgressionSnapshot.MaxEntries + 1).ToArray();
        if (bounded.Length > ProgressionSnapshot.MaxEntries || bounded.Any(row => row is null))
            return ProgressionSnapshot.Unavailable(category, "Fishing-log catalog is invalid or exceeds the supported bound.");
        var rows = bounded.Where(row => row.IsLogEntry).ToArray();
        if (rows.Length == 0 || rows.Any(row => row.Id == 0 || row.ItemId == 0 ||
                !ProgressionSnapshot.ValidText(row.Name, ProgressionSnapshot.MaxNameLength)) ||
            rows.Select(row => row.Id).Distinct().Count() != rows.Length)
            return ProgressionSnapshot.Unavailable(category, "Fishing-log definitions are invalid or unavailable.");
        var entries = new List<ProgressionEntry>();
        foreach (var row in rows)
        {
            if (!IsSupportedId(category, row.Id))
            {
                entries.Add(new(row.Id, row.Name, "unknown", UnknownReason:
                    "This named log entry is outside the pinned caught-flag mapping and physical bounds; caught-before state is unknown."));
                continue;
            }
            var caught = observe(row.Id);
            entries.Add(new(row.Id, row.Name, caught ? "completed" : "not_observed_complete",
                Completed: caught ? true : null, UnknownReason: caught ? null :
                    "No positive caught-before flag was observed; negative-state readiness is unverified, so this does not mean never caught."));
        }
        return new(category, capturedAt, true, "positive_observations_only", entries);
    }

    private static void ValidateCategory(string category)
    {
        if (category is not ("fishing_log" or "spearfishing_log"))
            throw new ArgumentException("Unsupported fishing-log category.", nameof(category));
    }

    private static ProgressionSnapshot Unavailable(string category) => ProgressionSnapshot.Unavailable(category,
        "Stable loaded character fishing-log state is unavailable; try again after character loading.");
}

internal sealed record FishingLogDefinition(uint Id, uint ItemId, string Name, bool IsLogEntry = true);

internal sealed record FishingLogCatalog(object Sheet, object ItemSheet, object GameData, string Language,
    FishingLogDefinition[] Definitions)
{
    internal bool Matches(object sheet, object itemSheet, object gameData, string language) =>
        ReferenceEquals(Sheet, sheet) && ReferenceEquals(ItemSheet, itemSheet) &&
        ReferenceEquals(GameData, gameData) && Language == language;
}

internal readonly record struct FishingLogObservation(nint PlayerAddress, ulong ContentId, bool Loaded,
    int BitCount, int ByteCount, string? Fingerprint)
{
    internal bool IsReady(string category) => category is "fishing_log" or "spearfishing_log" &&
        PlayerAddress != 0 && ContentId != 0 && Loaded &&
        BitCount == (category == "fishing_log" ? FishingLogReader.FishBitCount : FishingLogReader.SpearfishBitCount) &&
        ByteCount == (category == "fishing_log" ? FishingLogReader.FishByteCount : FishingLogReader.SpearfishByteCount) &&
        Fingerprint is not null && Fingerprint.Length == ByteCount * 2;
}
