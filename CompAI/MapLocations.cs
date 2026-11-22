using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

namespace CompAI;

internal sealed record MapLookupArguments(uint? MapId, uint? TerritoryId, string Query, int Offset, int Limit);
internal sealed record MapLocationDefinition(uint map_id, uint territory_id, string name, string territory_name,
    string? region_name, string? floor_name, int size_factor)
{
    internal string SearchName => MapLocationsReader.Fold(name);
}
internal sealed record MapLookupPage(int Count, MapLocationDefinition[] Rows);

/// <summary>Public installed map definitions only. Capture and map clicks run on the framework thread.</summary>
internal sealed class MapLocationsReader(IDataManager data)
{
    internal const int MaxCatalogRows = 65536;
    internal const int MaxResults = 20;
    private object? cachedMapSheet;
    private object? cachedTerritorySheet;
    private object? cachedGameData;
    private string? cachedLanguage;
    private MapLocationDefinition[]? cachedRows;

    internal MapLookupPage Capture(MapLookupArguments args)
    {
        ValidateArguments(args);
        var language = data.Language.ToString();
        var maps = data.GetExcelSheet<Map>();
        var territories = data.GetExcelSheet<TerritoryType>();
        if (maps.Count > MaxCatalogRows || territories.Count > MaxCatalogRows)
            throw new InvalidOperationException("Map definition sheet row bound exceeded.");
        MapLookupPage result;
        if (args.MapId is { } mapId)
        {
            var row = maps.TryGetRow(mapId, out var map) ? Describe(map, territories) : null;
            result = row is not null && (!args.TerritoryId.HasValue || row.territory_id == args.TerritoryId)
                ? new(1, [row]) : new(0, []);
        }
        else
        {
            if (cachedRows is null || !ReferenceEquals(cachedMapSheet, maps.RawSheet) ||
                !ReferenceEquals(cachedTerritorySheet, territories.RawSheet) ||
                !ReferenceEquals(cachedGameData, data.GameData) || cachedLanguage != language)
            {
                var rows = new List<MapLocationDefinition>();
                var scanned = 0;
                foreach (var map in maps)
                {
                    if (++scanned > MaxCatalogRows) throw new InvalidOperationException("Map definition scan bound exceeded.");
                    if (Describe(map, territories) is { } row) rows.Add(row);
                }
                cachedRows = rows.OrderBy(row => row.map_id).ToArray();
                cachedMapSheet = maps.RawSheet;
                cachedTerritorySheet = territories.RawSheet;
                cachedGameData = data.GameData;
                cachedLanguage = language;
            }
            result = SelectPage(cachedRows, args);
        }
        if (language != data.Language.ToString()) throw new InvalidOperationException("Client language changed during map lookup.");
        return result;
    }

    // Map.TerritoryType is the pairing: TerritoryType.Map is only its default map and excludes other floors.
    internal static MapLocationDefinition? Describe(Map map, ExcelSheet<TerritoryType> territories)
    {
        if (map.RowId is 0 or > ushort.MaxValue || map.SizeFactor == 0 ||
            map.TerritoryType.RowId is 0 or > ushort.MaxValue ||
            !territories.TryGetRow(map.TerritoryType.RowId, out var territory)) return null;
        var territoryName = Place(territory.PlaceName);
        var mapName = Place(map.PlaceName);
        if (territoryName is null || mapName is null) return null;
        var region = Place(map.PlaceNameRegion) ?? Place(territory.PlaceNameRegion);
        var floor = Place(map.PlaceNameSub);
        var name = string.Join(" / ", new[] { territoryName, mapName, floor, region }
            .Where(value => value is not null).Distinct(StringComparer.Ordinal));
        if (name.Length > 128) return null;
        return new(map.RowId, territory.RowId, name, territoryName, region, floor, map.SizeFactor);
    }

    internal static MapLookupPage SelectPage(IEnumerable<MapLocationDefinition> rows, MapLookupArguments args)
    {
        ValidateArguments(args);
        var terms = args.Query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Fold).ToArray();
        var page = new List<MapLocationDefinition>(args.Limit);
        var matches = 0;
        var scanned = 0;
        foreach (var row in rows)
        {
            if (++scanned > MaxCatalogRows) throw new InvalidOperationException("Map name index row bound exceeded.");
            var searchName = row.SearchName;
            if ((args.MapId.HasValue && row.map_id != args.MapId.Value) ||
                (args.TerritoryId.HasValue && row.territory_id != args.TerritoryId.Value) ||
                !terms.All(term => searchName.Contains(term, StringComparison.Ordinal))) continue;
            if (matches >= args.Offset && page.Count < args.Limit) page.Add(row);
            ++matches;
        }
        return new(matches, page.ToArray());
    }

    internal bool TryCreatePayload(MapLocationLink link, out MapLinkPayload? payload, out string? reason) =>
        TryCreatePayload(link, out payload, out _, out reason);

    internal bool TryCreatePayload(MapLocationLink link, out MapLinkPayload? payload, out string label, out string? reason)
    {
        payload = null;
        label = "";
        reason = "This map link is not valid.";
        if (!link.IsValid) return false;
        try
        {
            var maps = data.GetExcelSheet<Map>();
            var territories = data.GetExcelSheet<TerritoryType>();
            if (!maps.TryGetRow(link.MapId, out var map) ||
                !IsVerifiedPair(link, map.RowId, map.TerritoryType.RowId, map.SizeFactor,
                    territories.TryGetRow(link.TerritoryId, out var territory) ? territory.RowId : 0))
            {
                reason = "The map and territory do not match the installed game data.";
                return false;
            }
            label = FormattableString.Invariant($"Map {link.MapId}");
            // Labels come from bounded public sheet names, including the map/floor; never from model bytes.
            // An unavailable place-name reference need not prevent an otherwise verified map from opening.
            try { label = Describe(map, territories)?.name ?? label; }
            catch (Exception) { /* Keep the bounded numeric map label. */ }
            // The float overload takes displayed X/Y; the int overload takes raw world coordinates.
            // Dalamud performs SizeFactor/OffsetX/OffsetY conversion and its documented rounding adjustment.
            payload = new MapLinkPayload(link.TerritoryId, link.MapId, (float)link.X, (float)link.Y);
            reason = null;
            return true;
        }
        catch (Exception)
        {
            reason = "Local map data is unavailable. Try again after the game has loaded.";
            return false;
        }
    }

    internal bool TryOpen(IGameGui gui, MapLocationLink link, out string? reason)
    {
        if (!TryCreatePayload(link, out var payload, out reason)) return false;
        try
        {
            if (gui.OpenMapWithMapLink(payload!)) return true;
            reason = "The game could not open this map right now.";
        }
        catch (Exception) { reason = "The game could not open this map right now."; }
        return false;
    }

    internal static bool IsVerifiedPair(MapLocationLink link, uint mapId, uint mapTerritoryId, int sizeFactor, uint territoryId) =>
        link.IsValid && mapId == link.MapId && mapTerritoryId == link.TerritoryId && territoryId == link.TerritoryId && sizeFactor > 0;

    // Match the cross-language definition contract: one UTF-16 character at a time, with no expansions.
    internal static string Fold(string value) => new(value.Select(char.ToUpperInvariant).ToArray());

    private static void ValidateArguments(MapLookupArguments args)
    {
        if (args.Offset is < 0 or >= MaxCatalogRows || args.Limit is < 1 or > MaxResults ||
            args.MapId is 0 or > ushort.MaxValue || args.TerritoryId is 0 or > ushort.MaxValue ||
            args.Query is null || args.Query.Length > 100 || args.Query.Any(char.IsControl) ||
            (args.MapId.HasValue && (args.Query.Length != 0 || args.Offset != 0)) ||
            (!args.MapId.HasValue && !args.TerritoryId.HasValue && args.Query.Trim().Length < 2))
            throw new ArgumentException("Invalid map lookup arguments.");
    }

    private static string? Place(RowRef<PlaceName> place) => place.RowId != 0 && place.IsValid ? Name(place.Value.Name) : null;
    private static string? Name(ReadOnlySeString text)
    {
        var name = text.ExtractText().Trim();
        return name.Length is > 0 and <= 128 && !name.Any(char.IsControl) ? name : null;
    }
}
