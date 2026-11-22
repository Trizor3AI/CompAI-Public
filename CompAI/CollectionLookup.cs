using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace CompAI;

internal sealed record CollectionLookupSummary(string scope, string[] categories);
internal sealed record CollectionLookupRow(uint id, string name, string state, bool? unlocked, string? unknown_reason);
internal sealed record CollectionLookupResult(string scope, string category, string captured_at_utc, bool available,
    string? unavailable_reason, string coverage, string source, string readiness, int? catalog_count,
    int? observed_unlocked_count, int? not_observed_unlocked_count, int? unknown_count, CollectionLookupRow[] entries,
    int matched_count, int offset, int limit, int? next_offset, bool truncated);

internal sealed class CollectionLookup(CollectionSnapshot snapshot)
{
    internal const string Scope = "observed_collections";
    internal const int MaxResults = 50;
    internal const int MaxCompactResults = 200;
    internal const int MaxBytes = 20000;
    internal const string CompactCoverage = " Unconfirmed combines native false and unsupported observations. Null reasons on compact native-false rows use this caveat; none proves a missing card.";
    internal CollectionLookupSummary Summary => new(Scope, [snapshot.Category]);
    internal bool IsUsable(DateTimeOffset now) => !snapshot.LastObserved ||
        now >= snapshot.CapturedAtUtc && now - snapshot.CapturedAtUtc < TimeSpan.FromMinutes(15);
    internal static void ValidateArguments(JsonElement arguments) => _ = Parse(arguments);
    internal static string Category(JsonElement arguments) => Parse(arguments).Category;

    internal CollectionLookupResult Query(JsonElement arguments)
    {
        var options = Parse(arguments);
        if (snapshot.Category != options.Category)
            throw new ArgumentException("Collection snapshot does not match the requested category.", nameof(arguments));
        var terms = options.Query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = snapshot.Entries.Where(entry =>
            (options.Ids.Count == 0 || options.Ids.Contains(entry.Id)) &&
            (options.State == "all" || options.State == entry.State || options.State == "unconfirmed" && entry.Unlocked is null) &&
            terms.All(term => entry.Name.Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
        var rows = matches.Skip(options.Offset).Take(options.Limit).Select(entry =>
            new CollectionLookupRow(entry.Id, entry.Name, entry.State, entry.Unlocked,
                options.Compact && entry.State == "not_observed_unlocked" ? null : entry.UnknownReason)).ToList();
        CollectionLookupResult Build()
        {
            int? next = options.Offset + rows.Count < matches.Length ? options.Offset + rows.Count : null;
            return new(Scope, snapshot.Category, snapshot.CapturedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                snapshot.Available, snapshot.UnavailableReason, CollectionSnapshot.CategoryCoverage(snapshot.Category) +
                    (options.Compact ? CompactCoverage : "") +
                    (snapshot.LastObserved ? " Last observed during this login; may be stale, expires after 15 minutes. Reopen the notebook to refresh." : ""), CollectionSnapshot.Source(snapshot.Category),
                snapshot.LastObserved ? "last_observed_positive_observations" : snapshot.Available ? "positive_observations_only" : "unavailable",
                snapshot.Available ? snapshot.Entries.Count : null,
                snapshot.Available ? snapshot.Entries.Count(entry => entry.State == "unlocked") : null,
                snapshot.Available ? snapshot.Entries.Count(entry => entry.State == "not_observed_unlocked") : null,
                snapshot.Available ? snapshot.Entries.Count(entry => entry.State == "unknown") : null,
                rows.ToArray(), matches.Length, options.Offset, options.Limit, next, next is not null);
        }
        var result = Build();
        while (JsonSerializer.SerializeToUtf8Bytes(result).Length > MaxBytes && rows.Count > 0)
        {
            rows.RemoveAt(rows.Count - 1);
            result = Build();
        }
        if (result.truncated && rows.Count == 0)
            throw new InvalidOperationException("Collection result cannot make progress inside its byte bound.");
        return result;
    }

    private static CollectionQueryArguments Parse(JsonElement arguments)
    {
        static void Require(bool condition)
        {
            if (!condition) throw new ArgumentException("Invalid collection lookup arguments.", nameof(arguments));
        }
        Require(arguments.ValueKind == JsonValueKind.Object);
        var fields = new HashSet<string>(StringComparer.Ordinal);
        string? category = null;
        var query = "";
        var ids = new HashSet<uint>();
        var state = "all";
        var offset = 0;
        var limit = MaxResults;
        var limitSpecified = false;
        var compact = false;
        foreach (var property in arguments.EnumerateObject())
        {
            Require(fields.Add(property.Name));
            switch (property.Name)
            {
                case "category":
                    Require(property.Value.ValueKind == JsonValueKind.String);
                    category = property.Value.GetString();
                    Require(CollectionSnapshot.IsCategory(category));
                    break;
                case "query":
                    Require(property.Value.ValueKind == JsonValueKind.String);
                    query = property.Value.GetString()!;
                    Require(query.Length <= 100 && !query.Any(char.IsControl));
                    break;
                case "ids":
                    Require(property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() <= 20);
                    foreach (var idElement in property.Value.EnumerateArray())
                    {
                        Require(idElement.ValueKind == JsonValueKind.Number && idElement.TryGetUInt32(out _));
                        var id = idElement.GetUInt32();
                        Require(id > 0 && ids.Add(id));
                    }
                    break;
                case "state":
                    Require(property.Value.ValueKind == JsonValueKind.String);
                    state = property.Value.GetString()!;
                    Require(state is "all" or "unlocked" or "not_observed_unlocked" or "unknown" or "unconfirmed");
                    break;
                case "compact":
                    Require(property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False);
                    compact = property.Value.GetBoolean();
                    break;
                case "offset":
                    Require(property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out offset) &&
                        offset is >= 0 and <= CollectionSnapshot.MaxEntries);
                    break;
                case "limit":
                    limitSpecified = true;
                    Require(property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out limit) &&
                        limit is >= 1 and <= MaxCompactResults);
                    break;
                default:
                    Require(false);
                    break;
            }
        }
        Require(category is not null);
        Require(!compact || category == "triple_triad" && state == "unconfirmed");
        if (!limitSpecified) limit = compact ? MaxCompactResults : MaxResults;
        Require(compact || limit <= MaxResults);
        return new(category!, query.Trim(), ids, state, offset, limit, compact);
    }
}

internal sealed record CollectionQueryArguments(string Category, string Query, HashSet<uint> Ids,
    string State, int Offset, int Limit, bool Compact);
