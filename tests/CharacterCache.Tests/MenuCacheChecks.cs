using System.Collections.Immutable;
using System.Text.Json;
using CompAI;

internal static class MenuCacheChecks
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    private static CachedMenuObservation Sample(string category, DateTimeOffset at) =>
        MenuObservationCache.From(new(category, at, true, "Synthetic source", "visible", [
            new(1, 42, "Example item", category == "shops" ? 1u : null,
                category == "shops" ? "offer" : "stored", "Synthetic location", null, null, false,
                category == "shops" ? [new("gil", null, "Gil", 10)] : [])]));

    internal static async Task RoundTrip()
    {
        var folder = Path.Combine(Path.GetTempPath(), "compai-menu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var at = DateTimeOffset.UtcNow.AddMinutes(-1);
            var first = new CharacterCacheData(1, "Synthetic", 1, at, null, null)
                { Menus = [Sample("armoire", at), Sample("shops", at)] };
            await using (var writer = new CharacterCacheStore(folder))
            {
                Check(writer.Offer(first), "offer first character");
                Check(writer.Offer(new(2, "Other", 1, at, null, null)), "offer second character");
            }
            await using (var reader = new CharacterCacheStore(folder))
            {
                var restored = (await reader.LoadAsync(1)).Data!;
                Check(restored.SchemaVersion == 4 && JsonSerializer.Serialize(restored.Menus) == JsonSerializer.Serialize(first.Menus),
                    "all saved fields and original timestamp survive restart");
                Check((await reader.LoadAsync(2)).Data!.Menus.IsEmpty, "no cross-character menu data");
            }
            foreach (var schema in new[] { 1, 2, 3 })
            {
                var old = new CharacterCacheData((ulong)(10 + schema), "Old", 1, at, null, null) { SchemaVersion = schema };
                // Old producers had no Menus property.
                var json = JsonSerializer.SerializeToNode(old)!;
                json.AsObject().Remove("Menus");
                await File.WriteAllTextAsync(Path.Combine(folder, $"{old.ContentId:X16}.json"), json.ToJsonString());
                await using var reader = new CharacterCacheStore(folder);
                var restored = (await reader.LoadAsync(old.ContentId)).Data;
                Check(restored is not null && restored.Menus.IsEmpty, "old schema remains readable without fabricated observations");
            }
        }
        finally { Directory.Delete(folder, true); }
    }

    internal static Task Retention()
    {
        var at = DateTimeOffset.UtcNow;
        var shop = Sample("shops", at);
        var armoire = Sample("armoire", at);
        ImmutableArray<CachedMenuObservation> observations = [shop, armoire];
        Check(MenuObservationCache.Freeze(observations, "shops", at.AddMinutes(4)).Available, "closed shop remains recent");
        Check(!MenuObservationCache.Freeze(observations, "shops", at.AddMinutes(5)).Available, "shop expires at boundary");
        var failed = MenuObservationCache.Freeze(observations, "shops", at.AddMinutes(5), "Current shop layout is unsupported.");
        Check(failed.UnavailableReason == "Current shop layout is unsupported.", "specific capture failure replaces ambiguous expiry advice");
        Check(MenuObservationCache.Freeze(observations, "shops", at, "Closed menu.").Available,
            "a closed menu must not hide a retained valid observation");
        Check(MenuObservationCache.Freeze([], "shops", at).UnavailableReason!.Contains("never have been captured"),
            "absence must not assert an expired observation existed");
        Check(MenuObservationCache.Freeze(observations, "armoire", at.AddYears(1)).Available, "glamour retains last-known membership");
        Check(!MenuObservationCache.Freeze(observations, "shops", at.AddSeconds(-1)).Available, "future records not exposed");
        Check(MenuObservationCache.Merge(observations, Sample("shops", at.AddSeconds(30))) == observations,
            "unchanged observations do not write each tick");
        var row = shop.Entries[0];
        Check(MenuObservationCache.SameContent(shop, shop with { Entries = [row with { Costs = [row.Costs[0] with { }] }] }),
            "separate cost arrays with the same offer compare equal");
        foreach (var changed in new[] { row with { Id = 2 }, row with { ItemId = 43 }, row with { Name = "Other" },
                     row with { Quantity = 2 }, row with { Role = "stored" }, row with { Location = "Other" },
                     row with { GroupId = 1 }, row with { Slot = 1 }, row with { IsHq = true },
                     row with { UnknownReason = "Unknown" }, row with { Costs = [row.Costs[0] with { Amount = 11 }] } })
            Check(!MenuObservationCache.SameContent(shop, shop with { Entries = [changed] }),
                "changed offer fields or costs must trigger a new observation");
        var refreshed = MenuObservationCache.Merge(observations, Sample("shops", at.AddMinutes(1)));
        Check(MenuObservationCache.Freeze(refreshed, "shops", at.AddMinutes(5)).Available, "reopening refreshes lifetime");
        var empty = MenuObservationCache.From(new("armoire", at.AddMinutes(1), true, "Synthetic source", "visible", []));
        Check(MenuObservationCache.Freeze(MenuObservationCache.Merge(observations, empty), "armoire", at.AddMinutes(2)).Entries.Count == 0,
            "valid empty observation removes previously saved contents");
        try { MenuObservationCache.Validate([shop, shop]); throw new Exception("duplicate category accepted"); }
        catch (InvalidDataException) { }
        try { MenuObservationCache.Validate([shop with { Entries = [shop.Entries[0] with { Costs = [new("gil", null, "Gil", 0)] }] }]);
            throw new Exception("invalid cost accepted"); }
        catch (ArgumentException) { }
        return Task.CompletedTask;
    }
}
