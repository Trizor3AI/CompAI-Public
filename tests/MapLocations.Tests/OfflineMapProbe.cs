using System.Diagnostics;
using System.Runtime.Loader;
using System.Text.Json;
using CompAI;
using Lumina;
using Lumina.Data;
using Lumina.Excel.Sheets;

internal static class OfflineMapProbe
{
    internal static void Run(string sqpack)
    {
        // Explicit read-only probe of public installed sheets; no live game or character observations.
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XIVLauncher", "addon", "Hooks", "dev", name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        using var game = new GameData(sqpack);
        var clock = Stopwatch.StartNew();
        var maps = game.Excel.GetSheet<Map>(Language.English);
        var territories = game.Excel.GetSheet<TerritoryType>(Language.English);
        var rows = maps.Select(row => MapLocationsReader.Describe(row, territories)).Where(row => row is not null)
            .Select(row => row!).OrderBy(row => row.map_id).ToArray();
        Console.WriteLine($"Map rows: {maps.Count}; resolved named map/territory pairs: {rows.Length}; elapsed {clock.Elapsed.TotalMilliseconds:F1} ms");
        foreach (var query in new[] { "Limsa", "Gridania", "Ul'dah", "Mor Dhona", "Labyrinthos", "Tuliyollal" })
            Console.WriteLine(JsonSerializer.Serialize(new { query, result = MapLocationsReader.SelectPage(rows, new(null, null, query, 0, 20)) }));
        var mismatches = rows.Count(row => !MapLocationsReader.IsVerifiedPair(new(row.territory_id, row.map_id, 12.3m, 15.4m),
            row.map_id, maps.GetRow(row.map_id).TerritoryType.RowId, row.size_factor, territories.GetRow(row.territory_id).RowId));
        if (mismatches != 0) throw new Exception("Map/territory pairing failed.");
        Console.WriteLine("Every resolved map pair passed installed-sheet validation. No map was opened.");
    }
}
