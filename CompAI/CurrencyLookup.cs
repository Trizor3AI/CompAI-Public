using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace CompAI;

internal sealed record CurrencyLookupSummary(string scope);
internal sealed record CurrencyLookupRow(uint item_id, string name, int quantity);
internal sealed record CurrencyLookupResult(string scope, string captured_at_utc, bool available,
    string? unavailable_reason, string coverage, CurrencyLookupRow[] entries, int matched_count, bool truncated);

/// <summary>Queries one immutable capture frozen on first use in an answer. No I/O.</summary>
internal sealed class CurrencyLookup(CurrencySnapshot snapshot)
{
    internal const string Scope = "observed_currency_inventory";
    internal const int MaxResults = 50;
    internal const int MaxBytes = 20000;
    internal CurrencyLookupSummary Summary => new(Scope);

    internal static void ValidateArguments(JsonElement arguments) => _ = Parse(arguments);

    internal CurrencyLookupResult Query(JsonElement arguments)
    {
        var (query, ids) = Parse(arguments);
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = snapshot.Entries.Where(entry => (ids.Count == 0 || ids.Contains(entry.ItemId)) &&
            terms.All(term => entry.Name.Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
        var rows = matches.Take(MaxResults).Select(entry =>
            new CurrencyLookupRow(entry.ItemId, entry.Name, entry.Quantity)).ToList();
        CurrencyLookupResult Build() => new(Scope,
            snapshot.CapturedAtUtc.ToString("O", CultureInfo.InvariantCulture), snapshot.Available,
            snapshot.UnavailableReason, CurrencySnapshot.Coverage, rows.ToArray(), matches.Length,
            rows.Count < matches.Length);
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
            if (!condition) throw new ArgumentException("Invalid currency lookup arguments.", nameof(arguments));
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
                case "item_ids":
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
