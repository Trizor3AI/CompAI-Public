using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;
using ActionSheet = Lumina.Excel.Sheets.Action;

namespace CompAI;

/// <summary>
/// Framework-thread only. Copies public local sheets; never reads character/native state.
/// Capture returns a detached page. A requesting answer may retain its serialized result.
/// Exact IDs bypass indexing. Name indexes are lazy, bounded, and invalidated with language/sheet identity.
/// </summary>
internal sealed class GameDefinitionsReader(IDataManager data)
{
    private readonly MapLocationsReader maps = new(data);
    private readonly Dictionary<string, (object Sheet, object GameData, string Language, GameDefinitionName[] Names)> indexes = new();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly LocalItemStatsReader itemStats = new(data);
    private static string Source => $"Local Lumina.Excel {typeof(Recipe).Assembly.GetName().Version}; Item/Recipe/Action/ActionTransient/Quest/Map/TerritoryType";

    internal GameDefinitionsLookupResult Capture(JsonElement arguments)
    {
        var args = GameDefinitionsLookup.Parse(arguments);
        var language = data.Language.ToString();
        try
        {
            var result = args.Kind switch
            {
                "recipes" => Read(args, data.GetExcelSheet<Recipe>(), row => ItemName(row.ItemResult.RowId), RecipeEntry, language),
                "actions" => Read(args, data.GetExcelSheet<ActionSheet>(), row => Name(row.Name), ActionEntry, language),
                "quests" => Read(args, data.GetExcelSheet<Quest>(), row => Name(row.Name), QuestEntry, language),
                "items" => Read(args, data.GetExcelSheet<Item>(), row => Name(row.Name),
                    (row, title) => ItemEntry(row, title, args.Stats), language),
                "maps" => ReadMaps(args, language),
                _ => throw new ArgumentException("Unsupported definition kind."),
            };
            if (language != data.Language.ToString()) throw new InvalidOperationException("Client language changed during capture.");
            return result;
        }
        catch (Exception ex)
        {
            // Retrying a later explicit request can recover a temporarily unavailable sheet.
            indexes.Remove(args.Kind);
            return GameDefinitionsLookup.Build(args, language, Source, 0, [], unavailableReason:
                $"Local game definition read unavailable ({ex.GetType().Name}).");
        }
    }

    private GameDefinitionsLookupResult ReadMaps(GameDefinitionsArguments args, string language)
    {
        var page = this.maps.Capture(new(args.RowId, null, args.Query, args.Offset, args.Limit));
        return GameDefinitionsLookup.Build(args, language, Source, page.Count, page.Rows.Select(row =>
            new GameDefinitionEntry(row.map_id, row.name, map: new(row.territory_id, row.territory_name,
                row.region_name, row.floor_name, row.size_factor))));
    }

    private GameDefinitionsLookupResult Read<T>(GameDefinitionsArguments args, ExcelSheet<T> sheet,
        Func<T, string?> name, Func<T, string, GameDefinitionEntry> detail, string language) where T : struct, IExcelRow<T>
    {
        if (sheet.Count > GameDefinitionsLookup.MaxCatalogRows) throw new InvalidOperationException("Sheet row bound exceeded.");
        if (args.RowIds is { } ids)
        {
            var found = ids.Select(id => sheet.TryGetRow(id, out var row) && name(row) is { } title ? new GameDefinitionName(id, title) : null)
                .Where(row => row is not null).Select(row => row!).ToArray();
            var selected = found.Skip(args.Offset).Take(args.Limit).Select(row => detail(sheet.GetRow(row.RowId), row.Name));
            return GameDefinitionsLookup.Build(args, language, Source, found.Length, selected);
        }
        if (args.RowId is { } id)
        {
            if (!sheet.TryGetRow(id, out var row) || name(row) is not { } title)
                return GameDefinitionsLookup.Build(args, language, Source, 0, []);
            return GameDefinitionsLookup.Build(args, language, Source, 1, [detail(row, title)]);
        }
        if (!indexes.TryGetValue(args.Kind, out var cached) || !ReferenceEquals(cached.Sheet, sheet.RawSheet) ||
            !ReferenceEquals(cached.GameData, data.GameData) || cached.Language != language)
        {
            var names = new List<GameDefinitionName>();
            var scanned = 0;
            foreach (var row in sheet)
            {
                if (++scanned > GameDefinitionsLookup.MaxCatalogRows) throw new InvalidOperationException("Sheet row bound exceeded.");
                if (row.RowId != 0 && name(row) is { } title) names.Add(new(row.RowId, title));
            }
            cached = (sheet.RawSheet, data.GameData, language, names.OrderBy(row => row.RowId).ToArray());
            indexes[args.Kind] = cached;
        }
        var page = SelectNamePage(cached.Names, args);
        var entries = page.Rows.Select(row =>
        {
            if (!sheet.TryGetRow(row.RowId, out var value)) throw new InvalidOperationException("Definition sheet changed during capture.");
            return detail(value, row.Name);
        }).ToArray();
        return GameDefinitionsLookup.Build(args, language, Source, page.Count, entries);
    }

    // Keep only the requested page while counting matches; no second full match array.
    internal static (int Count, GameDefinitionName[] Rows) SelectNamePage(IEnumerable<GameDefinitionName> names,
        GameDefinitionsArguments args)
    {
        var terms = GameDefinitionsLookup.Terms(args.Query);
        var matches = 0;
        var scanned = 0;
        var page = new List<GameDefinitionName>(args.Limit);
        foreach (var row in names)
        {
            if (++scanned > GameDefinitionsLookup.MaxCatalogRows) throw new InvalidOperationException("Name index row bound exceeded.");
            if (!GameDefinitionsLookup.MatchesTerms(row.SearchName, terms)) continue;
            if (matches >= args.Offset && page.Count < args.Limit) page.Add(row);
            ++matches;
        }
        return (matches, page.ToArray());
    }

    internal static GameDefinitionEntry DescribeItem(Item row, string title)
    {
        var description = StaticDescription(row.Description);
        // Non-equipment rows often carry placeholder levels. Only attach equipment
        // metadata when a nonzero valid equip-slot category is actually present.
        var equipment = row.EquipSlotCategory.RowId > 0 && row.EquipSlotCategory.IsValid;
        return new(row.RowId, title, item: new(description,
            description is null ? "No bounded static description; missing text or dynamic/unsupported text payloads are not evaluated." : null,
            Nonzero(row.ItemUICategory.RowId), row.ItemUICategory.RowId > 0 && row.ItemUICategory.IsValid ? Name(row.ItemUICategory.Value.Name) : null,
            row.StackSize, row.CanBeHq, row.IsUnique, row.IsUntradable,
            equipment ? row.EquipSlotCategory.RowId : null,
            equipment && row.LevelEquip > 0 ? row.LevelEquip : null,
            equipment && row.LevelItem.RowId > 0 && row.LevelItem.IsValid ? row.LevelItem.RowId : null,
            equipment ? Nonzero(row.ClassJobCategory.RowId) : null,
            equipment && row.ClassJobCategory.RowId > 0 && row.ClassJobCategory.IsValid ? Name(row.ClassJobCategory.Value.Name) : null));
    }

    private GameDefinitionEntry ItemEntry(Item row, string title, bool stats)
    {
        var entry = DescribeItem(row, title);
        return stats ? entry with { item = entry.item! with { stats = itemStats.Capture(row) } } : entry;
    }

    private GameDefinitionEntry RecipeEntry(Recipe row, string title) => DescribeRecipe(row, title, ItemName);

    internal static GameDefinitionEntry DescribeRecipe(Recipe row, string title, Func<uint, string?> itemName)
    {
        if (row.ItemResult.RowId == 0 || row.AmountResult == 0 || row.Ingredient.Count != row.AmountIngredient.Count || row.Ingredient.Count > 16)
            throw new InvalidOperationException("Unsupported recipe layout.");
        var ingredients = DescribeRecipeIngredients(Enumerable.Range(0, row.Ingredient.Count)
            .Select(index => (row.Ingredient[index].RowId, (int)row.AmountIngredient[index])), itemName);
        return new(row.RowId, title, recipe: new(row.ItemResult.RowId, row.AmountResult, ingredients,
            row.CraftType.RowId, row.CraftType.IsValid ? Name(row.CraftType.Value.Name) : null,
            row.RecipeLevelTable.IsValid && row.RecipeLevelTable.Value.ClassJobLevel > 0 ? row.RecipeLevelTable.Value.ClassJobLevel : null,
            row.RequiredCraftsmanship, row.RequiredControl, Nonzero(row.Quest.RowId), Nonzero(row.SecretRecipeBook.RowId), row.IsSpecializationRequired));
    }

    internal static GameDefinitionIngredient[] DescribeRecipeIngredients(IEnumerable<(uint Id, int Quantity)> slots,
        Func<uint, string?> itemName)
    {
        var bounded = slots.Take(17).ToArray();
        if (bounded.Length > 16) throw new InvalidOperationException("Unsupported recipe ingredient count.");
        var ingredients = new List<GameDefinitionIngredient>();
        foreach (var (id, quantity) in bounded)
        {
            // Recipe.Ingredient reads signed Int32 fields into RowRef<uint>. Installed
            // recipes use both 0 and -1 (uint.MaxValue) for unused zero-quantity slots.
            if (quantity == 0 && id is 0 or uint.MaxValue) continue;
            if (id is 0 or uint.MaxValue || quantity is <= 0 or > byte.MaxValue)
                throw new InvalidOperationException("Inconsistent recipe ingredient.");
            ingredients.Add(new(id, itemName(id), quantity));
        }
        return ingredients.ToArray();
    }

    private GameDefinitionEntry ActionEntry(ActionSheet row, string title)
    {
        string? description = null;
        try
        {
            if (data.GetExcelSheet<ActionTransient>().TryGetRow(row.RowId, out var transient))
                description = StaticDescription(transient.Description);
        }
        catch (Exception)
        {
            // A missing description sheet or malformed text does not erase valid Action metadata.
        }
        return new(row.RowId, title, action: new(description,
            description is null ? "No bounded static description; missing text or dynamic/unsupported text payloads are not evaluated." : null,
            Nonzero(row.ClassJob.RowId), row.ClassJob.RowId > 0 && row.ClassJob.IsValid ? Name(row.ClassJob.Value.Name) : null,
            Nonzero(row.ClassJobCategory.RowId), row.ClassJobCategory.RowId > 0 && row.ClassJobCategory.IsValid ? Name(row.ClassJobCategory.Value.Name) : null,
            row.ClassJobLevel > 0 ? row.ClassJobLevel : null, row.Cast100ms, row.Recast100ms, row.IsPvP, row.IsPlayerAction));
    }

    private GameDefinitionEntry QuestEntry(Quest row, string title)
    {
        if (row.PreviousQuest.Count > 3 || row.ClassJobLevel.Count > 2) throw new InvalidOperationException("Unsupported quest layout.");
        var categories = new List<GameDefinitionCategory>();
        foreach (var reference in new[] { row.ClassJobCategory0, row.ClassJobCategory1 })
            if (reference.RowId > 0 && categories.All(item => item.row_id != reference.RowId))
                categories.Add(new(reference.RowId, reference.IsValid ? Name(reference.Value.Name) : null));
        return new(row.RowId, title, quest: new(row.ClassJobLevel.Where(level => level > 0).Select(level => (int)level).Distinct().ToArray(),
            Nonzero(row.ClassJobRequired.RowId), row.ClassJobRequired.RowId > 0 && row.ClassJobRequired.IsValid ? Name(row.ClassJobRequired.Value.Name) : null,
            categories.ToArray(), row.PreviousQuest.Select(previous => previous.RowId).Where(id => id > 0).Distinct().ToArray(),
            row.PreviousQuestJoin, row.IsRepeatable));
    }

    private string? ItemName(uint id) => id > 0 && data.GetExcelSheet<Item>().TryGetRow(id, out var row) ? Name(row.Name) : null;
    private static uint? Nonzero(uint id) => id == 0 ? null : id;
    internal static string? Name(ReadOnlySeString value)
    {
        if (value.ByteLength > 4096) return null;
        var name = value.ExtractText().Trim();
        return name.Length is > 0 and <= GameDefinitionsLookup.MaxNameLength && !name.Any(char.IsControl) ? name : null;
    }

    internal static string? StaticDescription(ReadOnlySeString value)
    {
        if (value.IsEmpty || value.ByteLength > 8192) return null;
        var output = new StringBuilder();
        foreach (var payload in value)
        {
            if (payload.Type == ReadOnlySePayloadType.Text) output.Append(StrictUtf8.GetString(payload.Body.Span));
            else if (payload.Type != ReadOnlySePayloadType.Macro) return null;
            else switch (payload.MacroCode)
            {
                case MacroCode.NewLine:
                case MacroCode.NonBreakingSpace: output.Append(' '); break;
                case MacroCode.Hyphen: output.Append('-'); break;
                case MacroCode.Color: case MacroCode.EdgeColor: case MacroCode.ShadowColor:
                case MacroCode.ColorType: case MacroCode.EdgeColorType:
                case MacroCode.Bold: case MacroCode.Italic: case MacroCode.Edge: case MacroCode.Shadow:
                case MacroCode.SoftHyphen: break;
                default: return null; // No conditional, sheet, player or expression evaluation.
            }
            if (output.Length > GameDefinitionsLookup.MaxDescriptionLength) return null;
        }
        var text = output.ToString().Trim();
        return text.Length > 0 && !text.Any(char.IsControl) ? text : null;
    }
}
