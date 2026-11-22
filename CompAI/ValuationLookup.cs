using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace CompAI;

internal sealed record ValuationContainer(string Category, string SourceKey, string SourceLabel,
    string Container, DateTimeOffset CapturedAtUtc, bool Available, StorageItem[] Items);
internal sealed record ValuationLocation(string category, string source, string container, int slot,
    int quantity, string captured_at_utc);
internal sealed record ValuationDetail(int handle, uint item_id, bool is_hq, string name, long total_quantity,
    int total_locations, bool locations_truncated, ValuationLocation[] locations);

/// <summary>
/// Private bridge transport: complete compact candidates stay on this PC. Only the winning
/// details are selected for the public tool result. No native reads, HTTP or live cache access.
/// </summary>
internal sealed class ValuationLookup
{
    internal const string Scope = "saved_item_valuation_v1";
    internal const int MaxGroups = 4096;
    internal const int MaxRows = StorageSnapshot.MaxRows + 140;
    internal const int MaxCandidateBytes = 512 * 1024;
    internal const int MaxDetailBytes = 128 * 1024;
    internal const int MaxCombinedBytes = MaxCandidateBytes + MaxDetailBytes;
    private readonly ValuationContainer[] containers;
    private readonly string[] categories;
    private readonly Dictionary<(uint Id, bool Hq), int> handles;
    private readonly bool carriedChanged;
    private readonly string? resumeScope;
    private readonly string snapshotId = Guid.NewGuid().ToString("N");
    internal object Summary => new { scope = Scope, snapshot_id = this.snapshotId, categories = this.categories };

    internal ValuationLookup(IEnumerable<ValuationContainer> containers, IEnumerable<string> categories, bool carriedChanged = false,
        string? resumeScope = null)
    {
        this.carriedChanged = carriedChanged;
        this.resumeScope = resumeScope;
        this.categories = categories.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (this.categories.Length == 0 || this.categories.Any(c => c is "key_items" or "armoury" || c != "inventory" && !StorageSnapshot.Categories.Contains(c)))
            throw new ArgumentException("Invalid valuation categories.");
        var copies = containers.Where(c => this.categories.Contains(c.Category)).Take(StorageSnapshot.MaxContainers + 5).ToArray();
        if (copies.Length > StorageSnapshot.MaxContainers + 4 || copies.Sum(c => (long)c.Items.Length) > MaxRows)
            throw new IOException("Saved item valuation exceeds its local snapshot bounds.");
        var identities = new HashSet<(string, string, string)>();
        foreach (var c in copies)
        {
            if (c.CapturedAtUtc == default || c.CapturedAtUtc > DateTimeOffset.UtcNow.AddMinutes(1) ||
                !Text(c.SourceKey, 128) || !Text(c.SourceLabel, 128) || !Text(c.Container, 128) ||
                !identities.Add((c.Category, c.SourceKey, c.Container)) || !c.Available && c.Items.Length != 0 ||
                c.Items.Any(i => i.ItemId is 0 or >= 2000000 || i.BaseItemId is 0 or >= 1000000 || i.Quantity is <= 0 or > 999999 ||
                    i.Slot is < 0 or >= 1000 || !Text(i.Name, 256)) || c.Items.Select(i => i.Slot).Distinct().Count() != c.Items.Length)
                throw new IOException("Saved item valuation snapshot is unavailable.");
        }
        this.containers = copies.Select(c => c with { Items = (StorageItem[])c.Items.Clone() })
            .OrderBy(c => c.Category, StringComparer.Ordinal).ThenBy(c => c.SourceKey, StringComparer.Ordinal)
            .ThenBy(c => c.Container, StringComparer.Ordinal).ToArray();
        this.handles = this.containers.Where(c => !Excluded(c)).SelectMany(c => c.Items)
            .Select(i => (i.BaseItemId, i.IsHq)).Distinct().OrderBy(k => k.BaseItemId).ThenBy(k => k.IsHq)
            .Select((key, index) => (key, handle: index + 1)).ToDictionary(pair => pair.key, pair => pair.handle);
    }

    private static bool Text(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);
    private static bool Market(ValuationContainer c) => c.Category == "retainers" && c.Container == "Current market listings";
    private static bool Placed(ValuationContainer c) => c.Category == "housing" &&
        (c.Container.StartsWith("Indoor placed furnishings ", StringComparison.Ordinal) ||
         c.Container.StartsWith("Outdoor placed furnishings ", StringComparison.Ordinal));
    private static bool Excluded(ValuationContainer c) => Market(c) || Placed(c);

    internal object Query(JsonElement arguments)
    {
        static void Require(bool value) { if (!value) throw new ArgumentException("Invalid valuation lookup arguments."); }
        Require(arguments.ValueKind == JsonValueKind.Object);
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in arguments.EnumerateObject()) Require(fields.Add(p.Name));
        Require(fields.SetEquals(["view", "categories", "handles"]));
        var viewValue = arguments.GetProperty("view");
        Require(viewValue.ValueKind == JsonValueKind.String);
        var view = viewValue.GetString()!;
        Require(view is "candidates" or "details");
        var categoriesValue = arguments.GetProperty("categories");
        Require(categoriesValue.ValueKind == JsonValueKind.Array && categoriesValue.GetArrayLength() is >= 1 and <= 6);
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in categoriesValue.EnumerateArray())
            Require(c.ValueKind == JsonValueKind.String && this.categories.Contains(c.GetString()!) && selected.Add(c.GetString()!));
        var requested = new HashSet<int>();
        var handlesValue = arguments.GetProperty("handles");
        Require(handlesValue.ValueKind == JsonValueKind.Array && handlesValue.GetArrayLength() <= 10);
        foreach (var h in handlesValue.EnumerateArray()) Require(h.TryGetInt32(out var id) && id is >= 1 and <= MaxGroups && requested.Add(id));
        Require(view == "candidates" ? requested.Count == 0 : requested.Count is >= 1 and <= 10);
        // There is no truncated fallback. A hard bound is explicit unavailable data.
        if (this.handles.Count > MaxGroups) throw new IOException("Saved item valuation has too many unique candidates.");
        var selectedContainers = this.containers.Where(c => selected.Contains(c.Category)).ToArray();
        var grouped = selectedContainers.Where(c => !Excluded(c)).SelectMany(c => c.Items.Select(i => (c, i)))
            .GroupBy(pair => (pair.i.BaseItemId, pair.i.IsHq)).OrderBy(g => this.handles[g.Key]).ToArray();
        var selectedCategories = selected.Order(StringComparer.Ordinal).ToArray();
        var observed = selectedContainers.Where(c => c.Available).ToArray();
        var observedSlots = observed.Where(c => !Excluded(c)).Sum(c => c.Items.Length);
        var from = observed.Length == 0 ? null : observed.Min(c => c.CapturedAtUtc).ToUniversalTime().ToString("O");
        var to = observed.Length == 0 ? null : observed.Max(c => c.CapturedAtUtc).ToUniversalTime().ToString("O");
        var excluded = new { market_listings = observed.Where(Market).Sum(c => c.Items.Length),
            placed_furnishings = observed.Where(Placed).Sum(c => c.Items.Length) };
        if (view == "candidates")
        {
            // Tuples intentionally omit item names, retainer labels and all local source keys.
            var rows = grouped.Select(g => new long[] { this.handles[g.Key], g.Key.BaseItemId, g.Key.IsHq ? 1 : 0,
                g.Sum(pair => (long)pair.i.Quantity) }).ToArray();
            // Only this digest crosses the private pipe. It binds the owner/session,
            // permission epoch, selected sources and exact observed contents. A
            // timestamp-only refresh of unchanged storage does not invalidate it.
            var resumeIdentity = this.resumeScope is null ? null : Convert.ToHexStringLower(SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(new { owner_session = this.resumeScope, permitted = this.categories,
                    selected = selectedCategories, carried_changed = selected.Contains("inventory") && this.carriedChanged,
                    containers = selectedContainers.Select(c => new { c.Category, c.SourceKey, c.SourceLabel,
                        c.Container, c.Available, items = c.Items.OrderBy(i => i.Slot).ToArray() }).ToArray() })));
            var result = new { scope = Scope, snapshot_id = this.snapshotId, view, categories = selectedCategories,
                resume_identity = resumeIdentity,
                observed_containers = observed.Length, observed_slots = observedSlots,
                unavailable_carried_containers = selectedContainers.Count(c => c.Category == "inventory" && !c.Available),
                carried_changed = selected.Contains("inventory") && this.carriedChanged,
                captured_from_utc = from, captured_to_utc = to, excluded_rows = excluded, rows, complete = true };
            if (JsonSerializer.SerializeToUtf8Bytes(result).Length > MaxCandidateBytes)
                throw new IOException("Saved item valuation candidate response exceeds its local bound.");
            return result;
        }
        Require(requested.IsSubsetOf(grouped.Select(g => this.handles[g.Key]).ToHashSet()));
        var details = grouped.Where(g => requested.Contains(this.handles[g.Key])).Select(g => new ValuationDetail(
            this.handles[g.Key], g.Key.BaseItemId, g.Key.IsHq, g.First().i.Name, g.Sum(pair => (long)pair.i.Quantity),
            g.Count(), g.Count() > 20, g.Take(20).Select(pair => new ValuationLocation(pair.c.Category,
                pair.c.SourceLabel, pair.c.Container, pair.i.Slot, pair.i.Quantity,
                pair.c.CapturedAtUtc.ToUniversalTime().ToString("O"))).ToArray())).ToArray();
        object Build() => new { scope = Scope, snapshot_id = this.snapshotId, view, categories = selectedCategories, rows = details };
        var response = Build();
        while (JsonSerializer.SerializeToUtf8Bytes(response).Length > MaxDetailBytes)
        {
            var index = Array.FindIndex(details, r => r.locations.Length == details.Max(d => d.locations.Length));
            if (index < 0 || details[index].locations.Length <= 1)
                throw new IOException("Saved item valuation details exceed their local bound.");
            var row = details[index];
            details[index] = row with { locations = row.locations[..^1], locations_truncated = true };
            response = Build();
        }
        return response;
    }
}
