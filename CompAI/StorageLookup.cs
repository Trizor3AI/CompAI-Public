using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace CompAI;

internal sealed class StorageLookup
{
    internal const string Scope = "observed_storage_v1";
    private readonly StorageContainerCapture[] captures;
    private readonly string[] categories;
    internal object Summary => new { scope = Scope, categories = this.categories };
    internal StorageLookup(IEnumerable<StorageContainerCapture> captures, IEnumerable<string> categories)
    {
        this.categories = categories.Where(StorageSnapshot.Categories.Contains).Distinct().ToArray();
        this.captures = captures.Where(value => this.categories.Contains(value.Category) && StorageSnapshot.Valid(value))
            .Select(value => value with { Items = StorageSnapshot.CloneItems(value.Items) })
            .OrderBy(value => value.Category, StringComparer.Ordinal).ThenBy(value => value.SourceKey, StringComparer.Ordinal)
            .ThenBy(value => value.Container, StringComparer.Ordinal).ToArray();
    }

    internal object Query(JsonElement arguments)
    {
        static void Require(bool condition) { if (!condition) throw new ArgumentException("Invalid storage lookup arguments."); }
        Require(arguments.ValueKind == JsonValueKind.Object);
        var fields = new HashSet<string>();
        foreach (var p in arguments.EnumerateObject()) Require(fields.Add(p.Name));
        Require(fields.SetEquals(["view", "category", "query", "source", "offset", "limit"]));
        string Text(string field, int max)
        {
            var value = arguments.GetProperty(field);
            Require(value.ValueKind == JsonValueKind.String);
            var text = value.GetString()!;
            Require(text.Length <= max && !text.Any(char.IsControl));
            return text;
        }
        var view = Text("view", 20); var category = Text("category", 20);
        var query = Text("query", 200); var source = Text("source", 128).Trim();
        Require(view is "items" or "containers");
        Require(category == "all" || this.categories.Contains(category));
        Require(arguments.GetProperty("offset").TryGetInt32(out var offset) && offset >= 0);
        Require(arguments.GetProperty("limit").TryGetInt32(out var limit) && limit is >= 1 and <= 30);
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        bool Match(string text) => terms.All(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
        var sources = this.captures.Where(c => (category == "all" || c.Category == category) &&
            c.SourceLabel.Contains(source, StringComparison.OrdinalIgnoreCase)).ToArray();
        object[] all;
        long? quantity = null;
        if (view == "containers") all = sources.Where(c => Match(c.Container + " " + c.SourceLabel)).Select(c => (object)new {
            category = c.Category, source = c.SourceLabel, container = c.Container,
            captured_at_utc = c.CapturedAtUtc.ToUniversalTime().ToString("O"), capacity = c.Capacity, occupied_slots = c.Items.Length }).ToArray();
        else
        {
            var items = sources.SelectMany(c => c.Items.Where(i => Match(i.Name)).Select(i => (c, i))).ToArray();
            quantity = items.Sum(pair => (long)pair.i.Quantity);
            all = items.Select(pair => (object)new {
                category = pair.c.Category, source = pair.c.SourceLabel, container = pair.c.Container,
                captured_at_utc = pair.c.CapturedAtUtc.ToUniversalTime().ToString("O"), slot = pair.i.Slot,
                item_id = pair.i.ItemId, base_item_id = pair.i.BaseItemId, name = pair.i.Name,
                item_namespace = StorageSnapshot.ItemNamespace(pair.c.Category),
                quantity = pair.i.Quantity, is_hq = pair.i.IsHq, unit_price_gil = pair.i.UnitPriceGil,
                materia = pair.i.Materia?.Select(m => new { slot = m.Slot, materia_id = m.MateriaId,
                    grade = m.Grade, item_id = m.ItemId, name = m.Name, is_empty = m.IsEmpty }).ToArray(),
                materia_unavailable = pair.i.MateriaUnavailable }).ToArray();
        }
        var rows = all.Skip(offset).Take(limit).ToList();
        object Result() => new { scope = Scope, view, total_matches = all.Length, total_quantity = quantity, offset,
            next_offset = offset < all.Length && rows.Count < all.Length - offset ? (int?)(offset + rows.Count) : null, rows = rows.ToArray() };
        var result = Result();
        while (JsonSerializer.SerializeToUtf8Bytes(result).Length > 20000 && rows.Count > 0)
        { rows.RemoveAt(rows.Count - 1); result = Result(); }
        if (offset < all.Length && rows.Count == 0) throw new InvalidOperationException("Storage row exceeds response budget.");
        return result;
    }
}
