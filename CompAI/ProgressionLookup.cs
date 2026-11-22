using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CompAI;

internal sealed record ProgressionLookupSummary(string scope, string[] categories);
internal sealed record ProgressionLookupRow(uint id, string name, string state, bool? completed,
    int? rank, long? progress, long? required, int? level, uint? experience, uint? experience_required,
    string? unknown_reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? observed_at_utc = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? observed_page = null);
internal sealed record ProgressionLookupResult(string scope, string category, string captured_at_utc, bool available,
    string? unavailable_reason, string coverage, string source, string readiness, int? catalog_count,
    int? completed_count, int? not_completed_count, int? not_observed_complete_count, int? observed_count,
    int? unknown_count, ProgressionLookupRow[] entries, int matched_count, int offset, int limit,
    int? next_offset, bool truncated);

internal sealed class ProgressionLookup(ProgressionSnapshot snapshot)
{
    internal const string Scope = "observed_progression";
    internal const int MaxResults = 50;
    internal const int MaxBytes = 20000;
    internal ProgressionLookupSummary Summary => new(Scope, [snapshot.Category]);
    internal static void ValidateArguments(JsonElement arguments) => _ = Parse(arguments);
    internal static string Category(JsonElement arguments) => Parse(arguments).Category;

    internal ProgressionLookupResult Query(JsonElement arguments)
    {
        var options = Parse(arguments);
        if (snapshot.Category != options.Category)
            throw new ArgumentException("Progression snapshot does not match the requested category.", nameof(arguments));
        var terms = options.Query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = snapshot.Entries.Where(entry =>
            (options.Ids.Count == 0 || options.Ids.Contains(entry.Id)) &&
            (options.State == "all" || options.State == entry.State) &&
            terms.All(term => entry.Name.Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
        var rows = matches.Skip(options.Offset).Take(options.Limit).Select(entry =>
            new ProgressionLookupRow(entry.Id, entry.Name, entry.State, entry.Completed, entry.Rank,
                entry.Progress, entry.Required, entry.Level, entry.Experience, entry.ExperienceRequired,
                entry.UnknownReason, entry.ObservedAtUtc?.ToString("O", CultureInfo.InvariantCulture), entry.ObservedPage)).ToList();
        int? Count(string state) => snapshot.Available ? snapshot.Entries.Count(entry => entry.State == state) : null;
        ProgressionLookupResult Build()
        {
            int? next = options.Offset + rows.Count < matches.Length ? options.Offset + rows.Count : null;
            return new(Scope, snapshot.Category, snapshot.CapturedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                snapshot.Available, snapshot.UnavailableReason, snapshot.Coverage,
                (snapshot.SavedMenu ? "Saved local " : "") + ProgressionSnapshot.Source(snapshot.Category), snapshot.Readiness, snapshot.Available ? snapshot.Entries.Count : null,
                Count("completed"), Count("not_completed"), Count("not_observed_complete"), Count("observed"), Count("unknown"),
                rows.ToArray(), matches.Length, options.Offset, options.Limit, next, next is not null);
        }
        var result = Build();
        while (JsonSerializer.SerializeToUtf8Bytes(result).Length > MaxBytes && rows.Count > 0)
        {
            rows.RemoveAt(rows.Count - 1);
            result = Build();
        }
        if (result.truncated && rows.Count == 0)
            throw new InvalidOperationException("Progression result cannot make progress inside its byte bound.");
        return result;
    }

    private static ProgressionQueryArguments Parse(JsonElement arguments)
    {
        static void Require(bool condition)
        {
            if (!condition) throw new ArgumentException("Invalid progression lookup arguments.", nameof(arguments));
        }
        Require(arguments.ValueKind == JsonValueKind.Object);
        var fields = new HashSet<string>(StringComparer.Ordinal);
        string? category = null;
        var query = "";
        var ids = new HashSet<uint>();
        var state = "all";
        var offset = 0;
        var limit = MaxResults;
        foreach (var property in arguments.EnumerateObject())
        {
            Require(fields.Add(property.Name));
            switch (property.Name)
            {
                case "category":
                    Require(property.Value.ValueKind == JsonValueKind.String);
                    category = property.Value.GetString();
                    Require(ProgressionSnapshot.IsCategory(category));
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
                    break;
                case "offset":
                    Require(property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out offset) &&
                        offset is >= 0 and <= ProgressionSnapshot.MaxEntries);
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
        Require(category is not null && (state == "all" || ProgressionSnapshot.IsState(category!, state)));
        return new(category!, query.Trim(), ids, state, offset, limit);
    }
}

internal sealed record ProgressionQueryArguments(string Category, string Query, HashSet<uint> Ids,
    string State, int Offset, int Limit);
