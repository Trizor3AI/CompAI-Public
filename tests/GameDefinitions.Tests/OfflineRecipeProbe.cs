using System.Diagnostics;
using System.Runtime.Loader;
using System.Text.Json;
using CompAI;
using Lumina;
using Lumina.Data;
using Lumina.Excel.Sheets;

internal static class OfflineRecipeProbe
{
    internal static void Run(string sqpack)
    {
        // Explicit opt-in to public installed sqpack files only; no running-game access.
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XIVLauncher", "addon", "Hooks", "dev", name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        using var game = new GameData(sqpack);
        var clock = Stopwatch.StartNew();
        var recipes = game.Excel.GetSheet<Recipe>();
        var items = game.Excel.GetSheet<Item>(Language.English);
        Console.WriteLine($"Recipe rows: {recipes.Count}; Item rows: {items.Count}; sheet load {clock.Elapsed.TotalMilliseconds:F1} ms");
        var matches = 0;
        clock.Restart();
        foreach (var row in recipes)
        {
            if (!items.TryGetRow(row.ItemResult.RowId, out var item) || item.Name.ExtractText() != "Bronze Ingot") continue;
            matches++;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                recipe_id = row.RowId, output = row.ItemResult.RowId, output_quantity = row.AmountResult,
                ingredient_slots = row.Ingredient.Count, quantity_slots = row.AmountIngredient.Count,
                ingredients = row.Ingredient.Select((ingredient, index) => new { id = ingredient.RowId, quantity = row.AmountIngredient[index] }).ToArray(),
                craft_type = row.CraftType.RowId,
                craft_type_name = row.CraftType.Value.Name.ExtractText(),
                recipe_level = row.RecipeLevelTable.Value.ClassJobLevel,
            }));
            var entry = GameDefinitionsReader.DescribeRecipe(row, item.Name.ExtractText(),
                id => items.TryGetRow(id, out var ingredient) ? ingredient.Name.ExtractText() : null);
            var result = GameDefinitionsLookup.Build(new("recipes", row.RowId, "", 0, 20), "English",
                "Offline installed Recipe sheet probe", 1, [entry]);
            Console.WriteLine(JsonSerializer.Serialize(result));
        }
        Console.WriteLine($"Bronze Ingot matches: {matches}; offline name scan {clock.Elapsed.TotalMilliseconds:F1} ms");
    }
}
