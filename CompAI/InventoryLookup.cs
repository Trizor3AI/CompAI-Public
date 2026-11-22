using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace CompAI;

internal sealed record InventoryLookupBag(string name, int? slot_count, string? unavailable_reason, int item_count);
internal sealed record InventoryLookupRow(string bag, uint slot, uint item_id, uint base_item_id,
    string? name, int quantity, bool is_hq, ObservedPedigree? pedigree);
internal sealed record InventoryLookupSummary(string captured_at_utc, bool changed, string scope,
    InventoryLookupBag[] containers, int item_slot_count, int verified_pedigree_count);
// The count field's legacy wire name is retained. It counts all available pedigree
// records; each record's status distinguishes automatic decode from old user checks.
internal sealed record InventoryLookupResult(string captured_at_utc, bool changed, string scope,
    InventoryLookupBag[] containers, int item_slot_count, int verified_pedigree_count,
    int total_matches, long total_quantity, int offset, int? next_offset, InventoryLookupRow[] rows);

/// <summary>Managed copies frozen at Send. No game reads, I/O or item-ID-only pedigree association.</summary>
internal sealed class InventoryLookup
{
    internal const int MaxBytes = 20000;
    internal const string Scope = "carried_inventory_1_4_lookup";
    private static readonly HashSet<string> ArgumentFields = new(StringComparer.Ordinal)
        { "view", "query", "ids", "bags", "offset", "limit", "locations" };
    private readonly InventoryLookupRow[] rows;
    internal InventoryLookupSummary Summary { get; }

    internal InventoryLookup(DateTimeOffset capturedAtUtc, bool changed,
        IEnumerable<InventoryLookupBag> containers, IEnumerable<InventoryLookupRow> rows)
    {
        // Copy nested star arrays as well: clearing/updating a live capture must not rewrite
        // a result midway through an answer. Bag and slot remain part of every card identity.
        this.rows = rows.Select(CloneRow).OrderBy(row => row.bag, StringComparer.Ordinal)
            .ThenBy(row => row.slot).ToArray();
        this.Summary = new(capturedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            changed, Scope, containers.ToArray(), this.rows.Length, this.rows.Count(row => row.pedigree is not null));
    }

    internal InventoryLookupResult Query(JsonElement arguments)
    {
        var request = Parse(arguments);
        var ids = request.Ids.ToHashSet();
        var bags = request.Bags.ToHashSet(StringComparer.Ordinal);
        var terms = request.Query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = this.rows
            // Include unscanned chocobo cards too; absence of a decoded pedigree is relevant.
            .Where(row => request.View != "pedigrees" || PedigreeDecoder.TryClassify(row.base_item_id, out _, out _, out _))
            .Where(row => ids.Count == 0 || ids.Contains(row.item_id) || ids.Contains(row.base_item_id))
            .Where(row => bags.Count == 0 || bags.Contains(row.bag))
            .Where(row => request.Locations.Count == 0 || request.Locations.Contains((row.bag, row.slot)))
            .Where(row => terms.Length == 0 || row.name is { } name &&
                terms.All(term => name.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var selected = matches.Skip(request.Offset).Take(request.Limit).Select(CloneRow).ToList();
        var totalQuantity = matches.Sum(row => (long)row.quantity);
        InventoryLookupResult Build() => new(this.Summary.captured_at_utc, this.Summary.changed, Scope,
            (InventoryLookupBag[])this.Summary.containers.Clone(), this.Summary.item_slot_count,
            this.Summary.verified_pedigree_count, matches.Length, totalQuantity, request.Offset,
            request.Offset < matches.Length && selected.Count < matches.Length - request.Offset
                ? request.Offset + selected.Count : null, selected.ToArray());
        var result = Build();
        while (JsonSerializer.SerializeToUtf8Bytes(result).Length > MaxBytes && selected.Count > 0)
        {
            selected.RemoveAt(selected.Count - 1);
            result = Build();
        }
        if (request.Offset < matches.Length && selected.Count == 0)
            throw new InvalidOperationException("An inventory lookup row exceeded its response budget.");
        return result;
    }

    private static InventoryLookupRow CloneRow(InventoryLookupRow row) => row.pedigree is not { } pedigree
        ? row : row with { pedigree = pedigree with {
            FatherStars = (int[])pedigree.FatherStars.Clone(), MotherStars = (int[])pedigree.MotherStars.Clone() } };

    // Locations are an additive filter: old callers may omit them. Like row.slot, these are
    // zero-based storage locations, never positions in the user's sorted inventory UI.
    internal static bool HasExactLocations(JsonElement arguments) =>
        arguments.TryGetProperty("locations", out var locations) && locations.GetArrayLength() > 0;

    private sealed record LookupRequest(string View, string Query, uint[] Ids, string[] Bags,
        HashSet<(string Bag, uint Slot)> Locations, int Offset, int Limit);
    private static LookupRequest Parse(JsonElement arguments)
    {
        static void Require(bool condition)
        {
            if (!condition) throw new ArgumentException("Invalid saved inventory lookup arguments.", nameof(arguments));
        }
        Require(arguments.ValueKind == JsonValueKind.Object);
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
            Require(ArgumentFields.Contains(property.Name) && fields.Add(property.Name));
        Require(ArgumentFields.All(field => field == "locations" || fields.Contains(field)));
        var viewElement = arguments.GetProperty("view");
        Require(viewElement.ValueKind == JsonValueKind.String);
        var view = viewElement.GetString()!;
        Require(view is "all" or "items" or "pedigrees");
        var queryElement = arguments.GetProperty("query");
        Require(queryElement.ValueKind == JsonValueKind.String);
        var query = queryElement.GetString()!;
        Require(query.Length <= 200 && !query.Contains('\0'));
        var offsetElement = arguments.GetProperty("offset");
        Require(offsetElement.ValueKind == JsonValueKind.Number && offsetElement.TryGetInt32(out _));
        var offset = offsetElement.GetInt32();
        Require(offset >= 0);
        var limitElement = arguments.GetProperty("limit");
        Require(limitElement.ValueKind == JsonValueKind.Number && limitElement.TryGetInt32(out _));
        var limit = limitElement.GetInt32();
        Require(limit is >= 1 and <= 50);
        var idsElement = arguments.GetProperty("ids");
        Require(idsElement.ValueKind == JsonValueKind.Array && idsElement.GetArrayLength() <= 50);
        var ids = new HashSet<uint>();
        foreach (var value in idsElement.EnumerateArray())
        {
            Require(value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out _));
            var id = value.GetUInt32();
            Require(id > 0 && ids.Add(id));
        }
        var bagsElement = arguments.GetProperty("bags");
        Require(bagsElement.ValueKind == JsonValueKind.Array && bagsElement.GetArrayLength() <= 4);
        var bags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in bagsElement.EnumerateArray())
        {
            Require(value.ValueKind == JsonValueKind.String);
            var bag = value.GetString()!;
            Require(bag is "Inventory1" or "Inventory2" or "Inventory3" or "Inventory4");
            Require(bags.Add(bag));
        }
        var locations = new HashSet<(string Bag, uint Slot)>();
        if (arguments.TryGetProperty("locations", out var locationsElement))
        {
            Require(locationsElement.ValueKind == JsonValueKind.Array && locationsElement.GetArrayLength() <= 50);
            foreach (var value in locationsElement.EnumerateArray())
            {
                Require(value.ValueKind == JsonValueKind.Object);
                var locationFields = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                    Require(property.Name is "bag" or "slot" && locationFields.Add(property.Name));
                Require(locationFields.SetEquals(["bag", "slot"]));
                var bagElement = value.GetProperty("bag");
                var slotElement = value.GetProperty("slot");
                Require(bagElement.ValueKind == JsonValueKind.String && slotElement.ValueKind == JsonValueKind.Number &&
                    slotElement.TryGetUInt32(out _));
                var bag = bagElement.GetString()!;
                var slot = slotElement.GetUInt32();
                Require(bag is "Inventory1" or "Inventory2" or "Inventory3" or "Inventory4");
                Require(slot < 35 && locations.Add((bag, slot)));
            }
        }
        return new(view, query, ids.ToArray(), bags.ToArray(), locations, offset, limit);
    }
}
