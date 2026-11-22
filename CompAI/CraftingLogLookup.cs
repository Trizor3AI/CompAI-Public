using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace CompAI;

internal sealed record CraftingLogLookupSummary(string scope);
internal sealed record CraftingLogDefinition(uint Id, string Name);
internal sealed record CraftingLogLookupRow(uint id, string name, string captured_at_utc,
    string state, bool? completed, string? unknown_reason);
internal sealed record CraftingLogLookupResult(string scope, string queried_at_utc, bool available,
    string? unavailable_reason, string coverage, string source, string readiness, int? catalog_count,
    int matched_count, CraftingLogLookupRow[] entries, int offset, int limit, int? next_offset, bool truncated);
internal sealed record CraftingLogQueryArguments(string Query, HashSet<uint> Ids, int Offset, int Limit);

internal static class CraftingLogLookup
{
    internal const string Scope = "observed_crafting_log";
    internal const string Source = "QuestManager.IsRecipeComplete; RecipeNotebookList/Recipe/Item sheets";
    internal const string Coverage = "Named recipes explicitly listed by the local crafting notebook. IDs are Recipe RowIds. " +
        "Public name/ID filters and pagination run before completion reads; only returned rows are observed. " +
        "Catalog and matched counts describe public definitions, not completed or missing recipes. " +
        "Completed means a positive crafted-before observation. Native false remains unknown because log-specific negative-state readiness is unverified. " +
        "Each recipe's first observation and timestamp stay frozen for this answer; different pages may be observed at different times. " +
        "No craftability, ingredients, current job eligibility, recipe ownership or server freshness is implied.";
    internal const int MaxCatalog = 65536;
    internal const int MaxCachedRows = 600;
    internal const int MaxResults = 50;
    internal const int MaxBytes = 20000;
    internal const int MaxNameLength = 128;
    internal static void ValidateArguments(JsonElement arguments) => _ = Parse(arguments);

    internal static CraftingLogQueryArguments Parse(JsonElement arguments)
    {
        static void Require(bool condition)
        {
            if (!condition) throw new ArgumentException("Invalid crafting-log lookup arguments.", nameof(arguments));
        }
        Require(arguments.ValueKind == JsonValueKind.Object);
        var fields = new HashSet<string>(StringComparer.Ordinal);
        var query = "";
        var ids = new HashSet<uint>();
        var offset = 0;
        var limit = MaxResults;
        foreach (var property in arguments.EnumerateObject())
        {
            Require(fields.Add(property.Name));
            switch (property.Name)
            {
                case "query":
                    Require(property.Value.ValueKind == JsonValueKind.String);
                    query = property.Value.GetString()!;
                    Require(query.Length <= 100 && !query.Any(char.IsControl));
                    query = query.Trim();
                    Require(query.Length is 0 or >= 2);
                    break;
                case "ids":
                    Require(property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() <= 20);
                    foreach (var id in property.Value.EnumerateArray())
                    {
                        Require(id.ValueKind == JsonValueKind.Number && id.TryGetUInt32(out _));
                        Require(id.GetUInt32() > 0 && ids.Add(id.GetUInt32()));
                    }
                    break;
                case "offset":
                    Require(property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out offset) &&
                        offset is >= 0 and < MaxCatalog);
                    break;
                case "limit":
                    Require(property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out limit) &&
                        limit is >= 1 and <= MaxResults);
                    break;
                default:
                    Require(false);
                    break;
            }
        }
        Require(query.Length >= 2 || ids.Count > 0);
        return new(query, ids, offset, limit);
    }

    internal static bool ValidText(string? text, int maximum) =>
        !string.IsNullOrWhiteSpace(text) && text.Length <= maximum && !text.Any(char.IsControl);
}
