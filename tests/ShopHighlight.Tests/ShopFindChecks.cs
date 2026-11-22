using CompAI;

internal static class ShopFindChecks
{
    internal static void Run()
    {
        var now = DateTimeOffset.UtcNow;
        var stamp = new ShopFindStamp(9, 2, 3, 4, 5);
        var identity = new ShopHighlightNativeIdentity(9, 1, 2, 3, 4, 5, 6, 7, 8, 9);
        var offers = new[] { new ShopHighlightOffer(0, 10, 100, false, 1, 20), new ShopHighlightOffer(1, 11, 100, true, 1, 40) };
        var binding = new ShopHighlightBinding(9, 2, 3, now.AddMinutes(5), identity, offers);
        var expires = now.AddMinutes(4);
        MenuLookupRow Row(int index) => new((uint)index + 1, 100, "Synthetic offer", 1, "offer", "Last observed Shop offers",
            null, index, index == 1, [new("gil", null, "gil", index == 1 ? 40u : 20u, null)], null);
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        var catalog = new ShopFindCatalog();
        var key = Guid.NewGuid();
        Check(catalog.Add(key, stamp, binding, expires, [Row(0)], now), "first returned row rejected");
        Check(catalog.Get(key, now)!.Items.Count == 1 && catalog.Get(key, now)!.Items[0].Id == 1,
            "unreturned rows became actions");
        Check(catalog.Add(key, stamp, binding, expires, [Row(1)], now), "same bound second page rejected");
        Check(catalog.Get(key, now)!.Items.Count == 2 && catalog.Get(key, now)!.Items[1].IsHq,
            "same-ID distinct offers lost their exact rows");
        Check(!catalog.Add(key, stamp, binding with { }, expires, [Row(0)], now), "later page replaced original binding");
        Check(!catalog.Add(key, stamp with { Area = 4 }, binding, expires, [Row(0)], now), "later page crossed zone epoch");
        Check(!catalog.Add(key, stamp, binding, expires.AddSeconds(1), [Row(0)], now), "later page extended action lifetime");

        foreach (var invalid in new[] { Row(0) with { id = 2 }, Row(0) with { slot = null }, Row(0) with { quantity = 2 },
            Row(0) with { is_hq = true }, Row(0) with { item_id = 101 }, Row(0) with { role = "stored" },
            Row(0) with { costs = [new("gil", null, "gil", 40, null)] },
            Row(0) with { costs = [new("item", 1, "gil", 20, null)] },
            Row(0) with { costs = [new("gil", null, "gil", null, "unknown")] } })
            Check(!catalog.Add(Guid.NewGuid(), stamp, binding, expires, [invalid], now), "unsupported or reassociated offer accepted");
        Check(!catalog.Add(Guid.NewGuid(), stamp, binding, expires, [Row(0), Row(0)], now), "duplicate row accepted");
        Check(catalog.Get(key, expires) is null, "expired shop action remained usable");
        Check(!catalog.Add(Guid.NewGuid(), stamp, binding, binding.ExpiresAt.AddSeconds(1), [Row(0)], now),
            "action outlived native binding");
        Check(!catalog.Add(Guid.NewGuid(), stamp with { Owner = 10 }, binding, expires, [Row(0)], now), "wrong owner accepted");
        for (var index = 0; index < ShopFindCatalog.MaxGroups; index++)
            Check(catalog.Add(Guid.NewGuid(), stamp, binding, expires, [Row(0)], now), "bounded synthetic group rejected");
        Check(catalog.Get(key, now) is null, "oldest action was not evicted at group cap");
        var final = Guid.NewGuid();
        Check(catalog.Add(final, stamp, binding, expires, [Row(0)], now), "final group rejected");
        catalog.Clear(); Check(catalog.Get(final, now) is null, "revocation retained actions");
        Console.WriteLine("PASS shop finder returned-row identity, pagination, ownership, expiry and bounds");
    }
}
