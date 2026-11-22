using System;
using System.Collections.Generic;
using System.Linq;

namespace CompAI;

internal sealed record ShopFindStamp(ulong Owner, int Session, long Area, long Access, long Changes);
internal sealed record ShopFindItem(uint Id, string Name, uint ItemId, bool IsHq, uint Quantity, uint GilCost);
internal sealed record ShopFindGroup(ShopFindStamp Stamp, ShopHighlightBinding Binding,
    DateTimeOffset ExpiresAt, IReadOnlyList<ShopFindItem> Items);

/// <summary>Bounded session-only actions for exact rows actually returned to this answer.</summary>
internal sealed class ShopFindCatalog
{
    internal const int MaxGroups = 16;
    internal const int MaxItems = 60;
    private readonly Dictionary<Guid, ShopFindGroup> groups = new();
    private readonly Queue<Guid> order = new();

    internal ShopFindGroup? Get(Guid key, DateTimeOffset now) =>
        this.groups.TryGetValue(key, out var value) && now < value.ExpiresAt ? value : null;

    internal bool Add(Guid key, ShopFindStamp stamp, ShopHighlightBinding binding, DateTimeOffset expiresAt,
        IEnumerable<MenuLookupRow> returned, DateTimeOffset now)
    {
        if (stamp.Owner == 0 || binding.OwnerId != stamp.Owner || binding.SessionGeneration != stamp.Session ||
            now >= expiresAt || expiresAt > binding.ExpiresAt) return false;
        var old = this.Get(key, now);
        if (old is not null && (old.Stamp != stamp || !ReferenceEquals(old.Binding, binding) || old.ExpiresAt != expiresAt))
            return false;
        var rows = returned.Take(MaxItems + 1).ToArray();
        if (rows.Length is 0 or > MaxItems || rows.Select(row => row.id).Distinct().Count() != rows.Length)
            return false;
        var items = (old?.Items ?? Array.Empty<ShopFindItem>()).ToDictionary(item => item.Id);
        foreach (var row in rows)
        {
            if (row.slot is not (>= 0 and < MaxItems) || row.id != (uint)row.slot.Value + 1 || row.role != "offer" ||
                row.group_id is not null || row.item_id == 0 || row.is_hq is null || row.quantity is not > 0 ||
                !MenuSnapshot.ValidText(row.name, 128) || row.costs is not { Length: 1 } ||
                row.costs[0] is not { kind: "gil", item_id: null, amount: > 0, unknown_reason: null }) return false;
            var offer = binding.Offers.Where(value => value.Row == row.slot.Value).Take(2).ToArray();
            if (offer.Length != 1 || offer[0].ItemId != row.item_id || offer[0].IsHq != row.is_hq ||
                offer[0].Quantity != row.quantity || offer[0].GilCost != row.costs[0].amount) return false;
            var item = new ShopFindItem(row.id, row.name, row.item_id, row.is_hq.Value, row.quantity.Value,
                row.costs[0].amount!.Value);
            if (items.TryGetValue(item.Id, out var prior) && prior != item) return false;
            items[item.Id] = item;
        }
        if (items.Count > MaxItems) return false;
        if (!this.groups.ContainsKey(key))
        {
            while (this.order.Count >= MaxGroups) this.groups.Remove(this.order.Dequeue());
            this.order.Enqueue(key);
        }
        this.groups[key] = new(stamp, binding, expiresAt, Array.AsReadOnly(items.Values.OrderBy(item => item.Id).ToArray()));
        return true;
    }

    internal void Remove(Guid key) => this.groups.Remove(key);
    internal void Clear() { this.groups.Clear(); this.order.Clear(); }
}
