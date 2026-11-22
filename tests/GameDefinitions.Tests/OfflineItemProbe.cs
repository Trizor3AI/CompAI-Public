using System.Diagnostics;
using System.Runtime.Loader;
using System.Text.Json;
using CompAI;
using Lumina;
using Lumina.Data;
using Lumina.Excel.Sheets;

internal static class OfflineItemProbe
{
    internal static void InspectStats(string sqpack)
    {
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XIVLauncher", "addon", "Hooks", "dev", name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        using var game = new GameData(sqpack);
        var watch = Stopwatch.StartNew();
        var items = game.Excel.GetSheet<Item>(Language.English);
        var parameters = game.Excel.GetSheet<BaseParam>(Language.English);
        var materiaSheet = game.Excel.GetSheet<Materia>(Language.English);
        Console.WriteLine($"Materia sheet rows: {materiaSheet.Count}; first row grade slots: {materiaSheet.GetRow(0).Item.Count}.");
        var materia = LocalItemStatsReader.DescribeMateria(materiaSheet);
        Console.WriteLine($"Public local Materia definitions: {materia.Count} item IDs; cold sheets/index {watch.Elapsed.TotalMilliseconds:F1} ms.");
        watch.Restart();
        foreach (var id in new uint[] { 1601, 3578, 3601, 4526, 46010, 46011, 46015, 46023, 46025, 46027 })
        {
            var row = items.GetRow(id);
            var stats = LocalItemStatsReader.DescribeEquipment(row, parameters);
            Console.WriteLine($"{id} {row.Name.ExtractText()}: " + JsonSerializer.Serialize(stats));
        }
        if (!materia.TryGetValue(41772, out var gem) || gem.base_param_id != 27 || gem.grade_index != 11 || gem.value != 54)
            throw new InvalidOperationException("Installed grade XII critical-hit materia differs from the checked definition.");
        var gear = LocalItemStatsReader.DescribeEquipment(items.GetRow(46011), parameters);
        if (gear.physical_damage.hq_value != 147 || gear.attributes.Single(row => row.base_param_id == 44).cap != 398)
            throw new InvalidOperationException("Installed Ceremonial Sainti differs from the checked definition.");
        Console.WriteLine("Verified public installed HQ weapon/materia/cap examples; no character or running-game reads.");
        Console.WriteLine($"Ten equipment definitions plus checks: {watch.Elapsed.TotalMilliseconds:F1} ms (including console output).");
    }

    internal static void DescribeSchema()
    {
        Console.WriteLine(typeof(Item).Assembly.FullName);
        foreach (var type in new[] { typeof(Item), typeof(ItemLevel), typeof(BaseParam), typeof(Materia), typeof(EquipSlotCategory) })
        {
            Console.WriteLine(type.Name);
            foreach (var property in type.GetProperties().OrderBy(property => property.Name))
                Console.WriteLine($"{property.Name}: {property.PropertyType}");
        }
    }

    internal static void Run(string sqpack)
    {
        // Public installed files only. No running process, character state or HTTP.
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XIVLauncher", "addon", "Hooks", "dev", name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        using var game = new GameData(sqpack);
        var clock = Stopwatch.StartNew();
        var sheet = game.Excel.GetSheet<Item>(Language.English);
        if (sheet.Count > GameDefinitionsLookup.MaxCatalogRows) throw new InvalidOperationException("Item sheet exceeded row bound.");
        Console.WriteLine($"Item rows: {sheet.Count}; sheet load {clock.Elapsed.TotalMilliseconds:F1} ms");
        var names = new List<GameDefinitionName>();
        var scanned = 0;
        clock.Restart();
        foreach (var row in sheet)
        {
            if (++scanned > GameDefinitionsLookup.MaxCatalogRows) throw new InvalidOperationException("Item sheet exceeded row bound.");
            if (row.RowId != 0 && GameDefinitionsReader.Name(row.Name) is { } name) names.Add(new(row.RowId, name));
        }
        var index = names.OrderBy(row => row.RowId).ToArray();
        Console.WriteLine($"Named rows: {index.Length}; cold bounded name index {clock.Elapsed.TotalMilliseconds:F1} ms");
        // Three public examples: material, medicine and equipment. Metadata only.
        foreach (var query in new[] { "Bronze Ingot", "Potion", "Weathered Shortsword" })
        {
            clock.Restart();
            var args = new GameDefinitionsArguments("items", null, query, 0, 2);
            var page = GameDefinitionsReader.SelectNamePage(index, args);
            var entries = page.Rows.Select(name => GameDefinitionsReader.DescribeItem(sheet.GetRow(name.RowId), name.Name)).ToArray();
            var result = GameDefinitionsLookup.Build(args, "English", "Offline public Item sheet probe", page.Count, entries);
            Console.WriteLine($"Query {query}: {clock.Elapsed.TotalMilliseconds:F1} ms; {page.Count} matches; {entries.Length} entries");
            Console.WriteLine(JsonSerializer.Serialize(result));
            foreach (var entry in entries)
            {
                var exact = GameDefinitionsReader.DescribeItem(sheet.GetRow(entry.row_id), entry.name);
                if (JsonSerializer.Serialize(exact) != JsonSerializer.Serialize(entry)) throw new InvalidOperationException("Exact lookup differs from name page.");
            }
        }
    }
}
