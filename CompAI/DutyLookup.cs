using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace CompAI;

internal sealed record DutyLookupSummary(string scope);
internal sealed record DutyLookupRow(uint duty_id, uint? instance_content_id, string name,
    bool? unlocked, bool? completed, string? unknown_reason);
internal sealed record DutyLookupResult(string scope, string captured_at_utc, bool available,
    string? unavailable_reason, string coverage, DutyLookupRow[] entries, int matched_count, bool truncated);

internal sealed class DutyLookup(DutySnapshot snapshot)
{
    internal const string Scope = "observed_instance_duties";
    internal const int MaxResults = 50;
    internal const int MaxBytes = 20000;
    internal DutyLookupSummary Summary => new(Scope);
    internal static void ValidateArguments(JsonElement arguments) => _ = Parse(arguments);

    internal DutyLookupResult Query(JsonElement arguments)
    {
        var (query, ids) = Parse(arguments);
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = snapshot.Entries.Where(entry => (ids.Count == 0 || ids.Contains(entry.DutyId)) &&
            terms.All(term => entry.Name.Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
        var rows = matches.Take(MaxResults).Select(entry => new DutyLookupRow(entry.DutyId, entry.InstanceContentId,
            entry.Name, entry.Unlocked, entry.Completed, entry.UnknownReason)).ToList();
        DutyLookupResult Build() => new(Scope, snapshot.CapturedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            snapshot.Available, snapshot.UnavailableReason, DutySnapshot.Coverage, rows.ToArray(),
            matches.Length, rows.Count < matches.Length);
        var result = Build();
        while (JsonSerializer.SerializeToUtf8Bytes(result).Length > MaxBytes && rows.Count > 0)
        {
            rows.RemoveAt(rows.Count - 1);
            result = Build();
        }
        return result;
    }

    private static (string Query, HashSet<uint> Ids) Parse(JsonElement arguments)
    {
        static void Require(bool condition)
        {
            if (!condition) throw new ArgumentException("Invalid duty lookup arguments.", nameof(arguments));
        }
        Require(arguments.ValueKind == JsonValueKind.Object);
        var fields = new HashSet<string>(StringComparer.Ordinal);
        var query = "";
        var ids = new HashSet<uint>();
        foreach (var property in arguments.EnumerateObject())
        {
            Require(fields.Add(property.Name));
            switch (property.Name)
            {
                case "query":
                    Require(property.Value.ValueKind == JsonValueKind.String);
                    query = property.Value.GetString()!;
                    Require(query.Length <= 100 && !query.Any(char.IsControl));
                    break;
                case "duty_ids":
                    Require(property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() <= 20);
                    foreach (var idElement in property.Value.EnumerateArray())
                    {
                        Require(idElement.ValueKind == JsonValueKind.Number && idElement.TryGetUInt32(out _));
                        var id = idElement.GetUInt32();
                        Require(id > 0 && ids.Add(id));
                    }
                    break;
                default:
                    Require(false);
                    break;
            }
        }
        return (query.Trim(), ids);
    }
}
