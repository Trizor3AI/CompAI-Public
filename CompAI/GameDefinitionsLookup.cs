using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CompAI;

internal sealed record GameDefinitionsLookupSummary(string scope);
internal sealed record GameDefinitionName(uint RowId, string Name)
{
    internal string SearchName { get; } = GameDefinitionsLookup.Fold(Name);
}
internal sealed record GameDefinitionIngredient(uint item_id, string? name, int quantity);
internal sealed record GameDefinitionCategory(uint row_id, string? name);
internal sealed record GameDefinitionRecipe(uint output_item_id, int output_quantity,
    GameDefinitionIngredient[] ingredients, uint craft_type_id, string? craft_type_name, int? crafting_level,
    int required_craftsmanship, int required_control, uint? required_quest_id, uint? required_recipe_book_id,
    bool specialist_required, string character_availability = "unknown");
internal sealed record GameDefinitionAction(string? description, string? description_unknown_reason,
    uint? class_job_id, string? class_job_name, uint? class_job_category_id, string? class_job_category_name,
    int? level, int base_cast_100ms, int base_recast_100ms, bool is_pvp, bool is_player_action,
    string character_availability = "unknown");
internal sealed record GameDefinitionQuest(int[] level_values, uint? required_class_job_id, string? required_class_job_name,
    GameDefinitionCategory[] class_job_categories, uint[] previous_quest_ids, int previous_quest_join,
    bool repeatable, string prerequisite_logic = "uninterpreted", string character_availability = "unknown");
internal sealed record GameDefinitionItem(string? description, string? description_unknown_reason,
    uint? ui_category_id, string? ui_category_name, uint stack_size, bool can_be_hq, bool is_unique, bool is_untradable,
    uint? equip_slot_category_id, int? equip_level, uint? item_level, uint? class_job_category_id, string? class_job_category_name,
    string character_availability = "unknown",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GameDefinitionItemStats? stats = null);
internal sealed record GameDefinitionStat(uint base_param_id, string? name, int base_value, int? hq_value,
    int? cap, string? cap_unknown_reason);
internal sealed record GameDefinitionQualityValue(int base_value, int? hq_value);
internal sealed record GameDefinitionEquipmentStats(GameDefinitionStat[] attributes,
    GameDefinitionQualityValue physical_damage, GameDefinitionQualityValue magical_damage,
    GameDefinitionQualityValue physical_defense, GameDefinitionQualityValue magical_defense,
    int delay_ms, int materia_slots, bool advanced_melding_permitted);
internal sealed record GameDefinitionMateriaStats(uint materia_row_id, int grade_index, uint base_param_id,
    string? stat_name, int value, uint? minimum_item_level);
internal sealed record GameDefinitionItemStats(GameDefinitionEquipmentStats? equipment, string? equipment_unknown_reason,
    GameDefinitionMateriaStats? materia, string? materia_unknown_reason);
internal sealed record GameDefinitionMap(uint territory_id, string territory_name, string? region_name,
    string? floor_name, int size_factor);
internal sealed record GameDefinitionEntry(uint row_id, string name, GameDefinitionRecipe? recipe = null,
    GameDefinitionAction? action = null, GameDefinitionQuest? quest = null, GameDefinitionItem? item = null,
    GameDefinitionMap? map = null);
internal sealed record GameDefinitionsLookupResult(string scope, string kind, string captured_at_utc,
    string language, string source, bool available, string? unavailable_reason, string coverage,
    GameDefinitionEntry[] entries, int matched_count, int offset, int? next_offset);
internal sealed record GameDefinitionsArguments(string Kind, uint? RowId, string Query, int Offset, int Limit,
    uint[]? RowIds = null, bool Stats = false);

/// <summary>Pure managed paging/serialization contract; the reader supplies only the requested page.</summary>
internal static class GameDefinitionsLookup
{
    internal const string Scope = "local_game_definitions_v3";
    internal const int MaxCatalogRows = 65536;
    internal const int MaxResults = 20;
    internal const int MaxBytes = 20000;
    internal const int MaxNameLength = 128;
    internal const int MaxDescriptionLength = 1600;
    internal const string Coverage = "Public definitions from installed local game sheets, in the selected client language. " +
        "No character state or HTTP requests. Missing rows or fields are unknown, not proof of removal or ineligibility. " +
        "Items use base Item row IDs. HQ, uniqueness and untradability are static flags, not instance condition, ownership, marketability or equip eligibility. " +
        "Recipes use CraftType discipline labels; listed requirements are not exhaustive and unlocks, materials owned and craftability are unknown. " +
        "Action times are unmodified sheet values in 100 ms units; traits, speed, sync and cooldown state are not evaluated. " +
        "Quest level/category values and previous-quest links are raw definitions; join logic and other eligibility conditions are not interpreted. " +
        "Quest titles may reveal names; journal/story text is omitted. Maps identify a zone/floor, not an NPC or destination's coordinates. " +
        "Local data does not prove current server availability.";

    internal static GameDefinitionsLookupSummary Summary => new(Scope);
    internal static void ValidateArguments(JsonElement arguments) => _ = Parse(arguments);

    internal static GameDefinitionsArguments Parse(JsonElement arguments)
    {
        static void Require(bool condition)
        {
            if (!condition) throw new ArgumentException("Invalid game definition lookup arguments.", nameof(arguments));
        }
        Require(arguments.ValueKind == JsonValueKind.Object);
        var fields = new HashSet<string>(StringComparer.Ordinal);
        string? kind = null;
        string? query = null;
        uint? rowId = null;
        uint[]? rowIds = null;
        var stats = false;
        var offset = 0;
        var limit = MaxResults;
        foreach (var property in arguments.EnumerateObject())
        {
            Require(fields.Add(property.Name));
            switch (property.Name)
            {
                case "kind":
                    Require(property.Value.ValueKind == JsonValueKind.String);
                    kind = property.Value.GetString();
                    Require(kind is "recipes" or "actions" or "quests" or "items" or "maps");
                    break;
                case "row_id":
                    Require(property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetUInt32(out var _) && property.Value.GetUInt32() > 0);
                    rowId = property.Value.GetUInt32();
                    break;
                case "query":
                    Require(property.Value.ValueKind == JsonValueKind.String);
                    query = property.Value.GetString()!;
                    Require(query.Length <= 100 && !query.Any(char.IsControl));
                    query = query.Trim();
                    Require(query.Length >= 2);
                    break;
                case "row_ids":
                    Require(property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() is >= 1 and <= MaxResults);
                    var ids = new List<uint>();
                    foreach (var entry in property.Value.EnumerateArray())
                    {
                        Require(entry.ValueKind == JsonValueKind.Number && entry.TryGetUInt32(out var _) && entry.GetUInt32() > 0);
                        ids.Add(entry.GetUInt32());
                    }
                    Require(ids.Distinct().Count() == ids.Count);
                    rowIds = ids.OrderBy(id => id).ToArray();
                    break;
                case "detail":
                    Require(property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() == "stats");
                    stats = true;
                    break;
                case "offset":
                    Require(property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out offset) && offset >= 0 && offset < MaxCatalogRows);
                    break;
                case "limit":
                    Require(property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out limit) && limit is >= 1 and <= MaxResults);
                    break;
                default:
                    Require(false);
                    break;
            }
        }
        Require(kind is not null && (Convert.ToInt32(rowId.HasValue) + Convert.ToInt32(query is not null) + Convert.ToInt32(rowIds is not null) == 1) && (!rowId.HasValue || offset == 0));
        Require(rowIds is null || kind == "items");
        Require(!stats || kind == "items" && query is null);
        Require(rowIds is null || offset < rowIds.Length);
        Require(kind != "maps" || !rowId.HasValue || rowId <= ushort.MaxValue);
        return new(kind!, rowId, query ?? "", offset, limit, rowIds, stats);
    }

    // One UTF-16 character at a time, with no linguistic multi-character expansions.
    internal static string Fold(string value) => new(value.Select(char.ToUpperInvariant).ToArray());
    internal static string[] Terms(string query) => query.Split((char[]?)null,
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Fold).ToArray();
    internal static bool Matches(string name, string query) => MatchesTerms(Fold(name), Terms(query));
    internal static bool MatchesTerms(string foldedName, string[] terms) =>
        terms.All(term => foldedName.Contains(term, StringComparison.Ordinal));

    internal static GameDefinitionsLookupResult Build(GameDefinitionsArguments args, string language, string source,
        int count, IEnumerable<GameDefinitionEntry> page, DateTimeOffset? capturedAt = null, string? unavailableReason = null)
    {
        var rows = page.Take(args.Limit + 1).ToList();
        if (count is < 0 or > MaxCatalogRows || (args.RowIds is not null && count > args.RowIds.Length) ||
            rows.Count > args.Limit || rows.Count > Math.Max(0, count - args.Offset) ||
            rows.Any(row => row.row_id == 0 || string.IsNullOrWhiteSpace(row.name) || row.name.Length > MaxNameLength ||
                row.name.Any(char.IsControl) || (args.RowId is { } id && row.row_id != id) ||
                (args.RowIds is not null && !args.RowIds.Contains(row.row_id)) || !Matches(row.name, args.Query) ||
                (row.item is not null && args.Stats != (row.item.stats is not null)) ||
                (args.Kind switch
                {
                    "recipes" => row.recipe is null || row.action is not null || row.quest is not null || row.item is not null || row.map is not null,
                    "actions" => row.action is null || row.recipe is not null || row.quest is not null || row.item is not null || row.map is not null,
                    "quests" => row.quest is null || row.recipe is not null || row.action is not null || row.item is not null || row.map is not null,
                    "items" => row.item is null || row.recipe is not null || row.action is not null || row.quest is not null || row.map is not null,
                    "maps" => row.map is null || row.recipe is not null || row.action is not null || row.quest is not null || row.item is not null ||
                        row.map.territory_id == 0 || row.map.territory_id > ushort.MaxValue || row.row_id > ushort.MaxValue ||
                        row.map.size_factor is <= 0 or > ushort.MaxValue || string.IsNullOrWhiteSpace(row.map.territory_name) ||
                        new[] { row.map.territory_name, row.map.region_name, row.map.floor_name }.Any(text => text is not null &&
                            (text.Length > MaxNameLength || text.Any(char.IsControl))),
                    _ => true,
                })) ||
            rows.Select(row => row.row_id).Distinct().Count() != rows.Count ||
            (unavailableReason is not null && (rows.Count != 0 || count != 0)))
            throw new ArgumentException("Invalid definition result page.");
        var stamp = (capturedAt ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        GameDefinitionsLookupResult Result() => new(Scope, args.Kind, stamp, language, source,
            unavailableReason is null, unavailableReason, args.Stats ? StatsCoverage : Coverage, rows.ToArray(), count, args.Offset,
            args.Offset + rows.Count < count ? args.Offset + rows.Count : null);
        var result = Result();
        while (JsonSerializer.SerializeToUtf8Bytes(result).Length > MaxBytes && rows.Count > 0)
        {
            rows.RemoveAt(rows.Count - 1);
            result = Result();
        }
        if ((args.Offset < count && rows.Count == 0) || JsonSerializer.SerializeToUtf8Bytes(result).Length > MaxBytes)
            throw new InvalidOperationException("A game definition exceeds the supported result bound.");
        return result;
    }

    internal const string StatsCoverage = "Public installed Item/BaseParam/ItemLevel/Materia sheets; no character state or HTTP. " +
        "Base/HQ values are unmelded static definitions, not owned quality or effective attributes. Caps use installed level/slot/role factors and supported rounding; null means unknown. " +
        "Offhand/compound-slot caps are not calculated. Relic custom allocations, sync, buffs, food, traits, job GCD and DPS are not evaluated. " +
        "Slot count and advanced-melding flag do not establish grade compatibility or success chance. Materia grade_index is zero-based. " +
        "Batch results contain only matched named rows, sorted by ID; absent IDs are unknown. Follow next_offset with the same IDs/detail until null before treating a batch as complete.";
}
