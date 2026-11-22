using System;
using System.Collections.Generic;
using System.Linq;

namespace CompAI;

internal sealed record InventoryFindStamp(ulong Owner, int Session, int Capture, long Changes);
internal sealed record InventoryFindItem(string Name, string Bag, uint Slot, uint ItemId,
    uint BaseItemId, int Quantity, bool IsHq);
internal sealed record InventoryFindGroup(InventoryFindStamp Stamp, IReadOnlyList<InventoryFindItem> Items)
{
    // Suggested locations are model-selected, not a new authority or an automatic UI action.
    internal IReadOnlyList<(string Bag, uint Slot)> SuggestedLocations { get; init; } = Array.Empty<(string, uint)>();
}

/// <summary>Session-only local actions, bounded independently of chat retention.</summary>
internal sealed class InventoryFindCatalog
{
    internal const int MaxGroups = 32;
    internal const int MaxItems = 140;
    private readonly Dictionary<Guid, InventoryFindGroup> groups = new();
    private readonly Queue<Guid> order = new();
    internal InventoryFindGroup? Get(Guid? key) => key is { } value ? this.groups.GetValueOrDefault(value) : null;

    // A display filter only: matching a name never chooses a slot or grants a new action.
    // Keep every same-name card and leave all lookup results available as a fallback.
    internal static HashSet<int> MentionedItems(InventoryFindGroup group, string answer)
    {
        var result = new HashSet<int>();
        if (answer.Length > 64000) return result;
        for (var i = 0; i < group.Items.Count; i++)
        {
            var name = group.Items[i].Name;
            if (name.Length == 0) continue;
            var start = 0;
            while (start < answer.Length)
            {
                var index = answer.IndexOf(name, start, StringComparison.OrdinalIgnoreCase);
                if (index < 0) break;
                var end = index + name.Length;
                if ((index == 0 || !char.IsLetterOrDigit(answer[index - 1])) &&
                    (end == answer.Length || !char.IsLetterOrDigit(answer[end])))
                { result.Add(i); break; }
                start = index + 1;
            }
        }
        return result;
    }

    internal bool Add(Guid key, InventoryFindStamp stamp, IEnumerable<InventoryFindItem> items, bool suggestReturnedLocations = false)
    {
        var old = this.Get(key);
        if (old is not null && old.Stamp != stamp) return false;
        var unique = (old?.Items ?? Array.Empty<InventoryFindItem>()).ToDictionary(item => (item.Bag, item.Slot));
        var suggested = new List<(string Bag, uint Slot)>();
        foreach (var item in items.Take(MaxItems))
        {
            if (item.Bag is not ("Inventory1" or "Inventory2" or "Inventory3" or "Inventory4") ||
                item.Slot >= 35 || item.ItemId == 0 || item.BaseItemId == 0 || item.Quantity < 1 ||
                string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 256 || item.Name.Any(char.IsControl)) continue;
            unique[(item.Bag, item.Slot)] = item;
            if (suggestReturnedLocations && !suggested.Contains((item.Bag, item.Slot))) suggested.Add((item.Bag, item.Slot));
        }
        if (unique.Count == 0) return false;
        if (old is null)
        {
            while (this.order.Count >= MaxGroups) this.groups.Remove(this.order.Dequeue());
            this.order.Enqueue(key);
        }
        this.groups[key] = new(stamp, Array.AsReadOnly(unique.Values.OrderBy(item => item.Bag, StringComparer.Ordinal)
            .ThenBy(item => item.Slot).Take(MaxItems).ToArray())) {
            SuggestedLocations = suggestReturnedLocations ? Array.AsReadOnly(suggested.ToArray()) :
                old?.SuggestedLocations ?? Array.Empty<(string, uint)>() };
        return true;
    }

    internal void Clear() { this.groups.Clear(); this.order.Clear(); }
}
