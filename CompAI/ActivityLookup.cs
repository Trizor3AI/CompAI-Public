using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace CompAI;

internal sealed record ActivityLookupSummary(string scope, string[] categories);
internal sealed record ActivityLookupRow(uint id, string group, string name, string state, string? text,
    long? value, long? required, string? unit, uint? reference_id, string? reference_kind, int? slot);
internal sealed record ActivityLookupResult(string scope, string category, string captured_at_utc, bool available,
    string? unavailable_reason, string coverage, string source, string readiness, int? entry_count,
    ActivityLookupRow[] entries, int matched_count, int offset, int limit, int? next_offset, bool truncated);

internal sealed class ActivityLookup(ActivitySnapshot snapshot)
{
    internal const string Scope = "observed_activities";
    internal const int MaxResults = 50;
    internal const int MaxBytes = 20000;
    internal ActivityLookupSummary Summary => new(Scope, [snapshot.Category]);
    internal bool IsUsable(DateTimeOffset now) => !snapshot.LastObserved ||
        now >= snapshot.CapturedAtUtc && now - snapshot.CapturedAtUtc < TimeSpan.FromMinutes(15);
    internal static void ValidateArguments(JsonElement arguments) => _ = ParseArguments(arguments);
    internal static string Category(JsonElement arguments) => ParseArguments(arguments).Category;
    internal static ActivityArguments ParseArguments(JsonElement arguments) => ActivityArguments.Parse(arguments);

    internal ActivityLookupResult Query(JsonElement arguments) => Query(ParseArguments(arguments));

    internal ActivityLookupResult Query(ActivityArguments options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (snapshot.Category != options.Category) throw new ArgumentException("Activity category mismatch.");
        var terms = options.Query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = snapshot.Entries.Where(entry => options.IncludesId(entry.Id) &&
            (options.Group.Length == 0 || options.Group == entry.Group) && terms.All(term =>
                entry.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                entry.Text?.Contains(term, StringComparison.OrdinalIgnoreCase) == true)).ToArray();
        var rows = matches.Skip(options.Offset).Take(options.Limit).Select(entry => new ActivityLookupRow(
            entry.Id, entry.Group, entry.Name, entry.State, entry.Text, entry.Value, entry.Required, entry.Unit,
            entry.ReferenceId, entry.ReferenceKind, entry.Slot)).ToList();
        ActivityLookupResult Build()
        {
            int? next = options.Offset + rows.Count < matches.Length ? options.Offset + rows.Count : null;
            return new(Scope, snapshot.Category, snapshot.CapturedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                snapshot.Available, snapshot.UnavailableReason, ActivitySnapshot.CategoryCoverage(snapshot.Category) +
                    (snapshot.LastObserved ? " Last observed during this login; expires after 15 minutes. Reopen the relevant menu tab to refresh." : ""),
                ActivitySnapshot.Source(snapshot.Category), snapshot.Readiness,
                snapshot.Available ? snapshot.Entries.Count : null, rows.ToArray(), matches.Length,
                options.Offset, options.Limit, next, next is not null);
        }
        var result = Build();
        while (JsonSerializer.SerializeToUtf8Bytes(result).Length > MaxBytes && rows.Count != 0)
        { rows.RemoveAt(rows.Count - 1); result = Build(); }
        if (result.truncated && rows.Count == 0) throw new InvalidOperationException("Activity result cannot make progress.");
        return result;
    }

}

// Only Parse can construct these detached options. The ID set never escapes, and
// callers cannot replace validated fields or retain a mutable input collection.
internal sealed class ActivityArguments
{
    private readonly HashSet<uint> ids;
    internal string Category { get; }
    internal string Query { get; }
    internal string Group { get; }
    internal int Offset { get; }
    internal int Limit { get; }

    private ActivityArguments(string category, string query, string group, HashSet<uint> ids, int offset, int limit)
    {
        Category = category;
        Query = query;
        Group = group;
        this.ids = ids;
        Offset = offset;
        Limit = limit;
    }

    internal bool IncludesId(uint id) => this.ids.Count == 0 || this.ids.Contains(id);

    internal static ActivityArguments Parse(JsonElement arguments)
    {
        static void Require(bool condition) { if (!condition) throw new ArgumentException("Invalid activity lookup arguments."); }
        Require(arguments.ValueKind == JsonValueKind.Object);
        var fields = new HashSet<string>(StringComparer.Ordinal);
        string? category = null;
        var query = "";
        var group = "";
        var ids = new HashSet<uint>();
        var offset = 0;
        var limit = ActivityLookup.MaxResults;
        foreach (var property in arguments.EnumerateObject())
        {
            Require(fields.Add(property.Name));
            switch (property.Name)
            {
                case "category":
                    Require(property.Value.ValueKind == JsonValueKind.String);
                    category = property.Value.GetString();
                    Require(ActivitySnapshot.IsCategory(category));
                    break;
                case "query":
                    Require(property.Value.ValueKind == JsonValueKind.String);
                    query = property.Value.GetString()!;
                    Require(query.Length <= 100 && !query.Any(char.IsControl));
                    break;
                case "group":
                    Require(property.Value.ValueKind == JsonValueKind.String);
                    group = property.Value.GetString()!;
                    Require(group.Length <= 64 && !group.Any(char.IsControl));
                    break;
                case "ids":
                    Require(property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() <= 20);
                    foreach (var element in property.Value.EnumerateArray())
                    { Require(element.ValueKind == JsonValueKind.Number && element.TryGetUInt32(out var id) && id is > 0 and <= ActivitySnapshot.MaxEntries && ids.Add(id)); }
                    break;
                case "offset":
                    Require(property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out offset) && offset is >= 0 and <= ActivitySnapshot.MaxEntries);
                    break;
                case "limit":
                    Require(property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out limit) && limit is >= 1 and <= ActivityLookup.MaxResults);
                    break;
                default: Require(false); break;
            }
        }
        Require(category is not null);
        return new(category!, query.Trim(), group, ids, offset, limit);
    }
}
