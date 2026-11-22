using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CompAI;

internal sealed record MenuLookupSummary(string scope, string[] categories);
internal sealed record MenuLookupCost(string kind, uint? item_id, string? name, uint? amount, string? unknown_reason);
internal sealed record MenuLookupRow(uint id, uint item_id, string name, uint? quantity, string role, string location,
    int? group_id, int? slot, bool? is_hq, MenuLookupCost[] costs, string? unknown_reason);
internal sealed record MenuLookupResult(string scope, string category, string captured_at_utc, bool available,
    string? unavailable_reason, string coverage, string source, string readiness, int? catalog_count,
    MenuLookupRow[] entries, int matched_count, int offset, int limit, int? next_offset, bool truncated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? suggested_names = null);
internal sealed record MenuQueryArguments(string Category, string Query, HashSet<uint> ItemIds, int Offset, int Limit);

internal sealed class MenuLookup(MenuSnapshot snapshot)
{
    internal const string Scope = "observed_menus";
    internal const int MaxResults = 50;
    internal const int MaxBytes = 20000;
    internal MenuLookupSummary Summary => new(Scope, [snapshot.Category]);
    internal static void ValidateArguments(JsonElement arguments, IEnumerable<string>? categories = null) => _ = Parse(arguments, categories);
    internal static string Category(JsonElement arguments) => Parse(arguments).Category;

    internal MenuLookupResult Query(JsonElement arguments)
    {
        var args = Parse(arguments, [snapshot.Category]);
        var terms = args.Query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Fold).ToArray();
        var all = snapshot.Entries;
        var matches = all.Where(row => (args.ItemIds.Count == 0 || args.ItemIds.Contains(row.ItemId)) &&
            terms.All(term => Fold(row.Name).Contains(term, StringComparison.Ordinal))).ToArray();
        string[]? suggestions = null;
        if (matches.Length == 0 && snapshot.Available && snapshot.Category == "glamour_dresser" &&
            args.ItemIds.Count == 0 && args.Offset == 0 && args.Query.Length >= 5)
        {
            var query = Fold(args.Query);
            var maxEdits = Math.Min(5, query.Length / 4);
            var names = all.Select(row => row.Name)
                .Where(name => Math.Abs(name.Length - query.Length) <= maxEdits)
                .Distinct(StringComparer.Ordinal)
                .Select(name => (Name: name, Edits: SpellingDistance(query, Fold(name), maxEdits)))
                .Where(candidate => candidate.Edits <= maxEdits)
                .OrderBy(candidate => candidate.Edits).ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
                .Take(Math.Min(3, args.Limit)).Select(candidate => candidate.Name).ToArray();
            if (names.Length != 0) suggestions = names;
        }
        var rows = matches.Skip(args.Offset).Take(args.Limit).Select(row => new MenuLookupRow(row.Id, row.ItemId,
            row.Name, row.Quantity, row.Role, row.Location, row.GroupId, row.Slot, row.IsHq,
            row.Costs.Select(cost => new MenuLookupCost(cost.Kind, cost.ItemId, cost.Name, cost.Amount, cost.UnknownReason)).ToArray(),
            // This universal caveat is already in every page's coverage. Avoid
            // repeating it up to 240 times, including in previously saved plates.
            snapshot.Category == "glamour_plates" && row.UnknownReason == MenuSnapshot.PlateReferenceNotice ? null : row.UnknownReason)).ToList();
        MenuLookupResult Build()
        {
            int? next = args.Offset + rows.Count < matches.Length ? args.Offset + rows.Count : null;
            return new(Scope, snapshot.Category, snapshot.CapturedAtUtc.ToString("O", CultureInfo.InvariantCulture), snapshot.Available,
                snapshot.UnavailableReason, MenuSnapshot.Coverage(snapshot.Category), snapshot.Source, snapshot.Readiness,
                snapshot.Available ? all.Count : null, rows.ToArray(), matches.Length, args.Offset, args.Limit, next, next.HasValue, suggestions);
        }
        var result = Build();
        while (JsonSerializer.SerializeToUtf8Bytes(result).Length > MaxBytes && rows.Count > 0) { rows.RemoveAt(rows.Count - 1); result = Build(); }
        if (result.truncated && rows.Count == 0 || JsonSerializer.SerializeToUtf8Bytes(result).Length > MaxBytes)
            throw new InvalidOperationException("Menu result cannot fit within the byte bound.");
        return result;
    }

    // Whole-name Levenshtein distance. Once an entire row exceeds the budget,
    // no suffix can bring the final distance back under it.
    private static int SpellingDistance(string query, string name, int maxEdits)
    {
        Span<int> row = stackalloc int[name.Length + 1]; // snapshot names have at most 128 UTF-16 units
        for (var j = 0; j < row.Length; j++) row[j] = j;
        for (var i = 0; i < query.Length; i++)
        {
            var diagonal = row[0];
            row[0] = i + 1;
            var smallest = row[0];
            for (var j = 1; j < row.Length; j++)
            {
                var above = row[j];
                row[j] = Math.Min(diagonal + (query[i] == name[j - 1] ? 0 : 1), Math.Min(above, row[j - 1]) + 1);
                smallest = Math.Min(smallest, row[j]);
                diagonal = above;
            }
            if (smallest > maxEdits) return maxEdits + 1;
        }
        return row[^1];
    }

    internal static MenuQueryArguments Parse(JsonElement arguments, IEnumerable<string>? categories = null)
    {
        static void Require(bool value) { if (!value) throw new ArgumentException("Invalid menu lookup arguments."); }
        Require(arguments.ValueKind == JsonValueKind.Object);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? category = null;
        var query = "";
        var ids = new HashSet<uint>();
        var offset = 0;
        var limit = MaxResults;
        foreach (var property in arguments.EnumerateObject())
        {
            Require(seen.Add(property.Name));
            switch (property.Name)
            {
                case "category":
                    Require(property.Value.ValueKind == JsonValueKind.String);
                    category = property.Value.GetString();
                    Require(MenuSnapshot.IsCategory(category));
                    break;
                case "query":
                    Require(property.Value.ValueKind == JsonValueKind.String);
                    query = property.Value.GetString()!;
                    Require(query.Length <= 100 && !query.Any(char.IsControl));
                    query = query.Trim();
                    Require(query.Length is 0 or >= 2);
                    break;
                case "item_ids":
                    Require(property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() <= 20);
                    foreach (var id in property.Value.EnumerateArray()) Require(id.ValueKind == JsonValueKind.Number &&
                        id.TryGetUInt32(out var parsed) && parsed > 0 && ids.Add(parsed));
                    break;
                case "offset":
                    Require(property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out offset) && offset is >= 0 and < MenuSnapshot.MaxEntries);
                    break;
                case "limit":
                    Require(property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out limit) && limit is >= 1 and <= MaxResults);
                    break;
                default: Require(false); break;
            }
        }
        Require(category is not null && (categories is null || categories.Contains(category, StringComparer.Ordinal)));
        return new(category!, query, ids, offset, limit);
    }
    internal static string Fold(string value) => new(value.Select(char.ToUpperInvariant).ToArray());
}
