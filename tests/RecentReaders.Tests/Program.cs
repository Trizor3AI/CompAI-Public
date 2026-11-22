using System.Text.Json;
using CompAI;

var passed = 0;
void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Test(string name, Action action) { action(); ++passed; Console.WriteLine("PASS " + name); }
var stamp = DateTimeOffset.UtcNow.AddMinutes(-2);
GcSupplySnapshot Gc(DateTimeOffset at, uint item = 100, bool? eligible = true) => new(at, true,
    [new(0, 8, "CRP", "supply", item, "Synthetic item", 1, 100, 10, eligible, null)],
    ownerContentId: 123, menuAddress: 456, agentAddress: 789);
CollectionSnapshot Bestiary(DateTimeOffset at, bool unlocked = true) => new("beastmaster_bestiary", at, true,
    [new(1, "Synthetic familiar", unlocked ? "unlocked" : "not_observed_unlocked", unlocked ? true : null,
        unlocked ? null : "No positive observation")]);

Test("GC stable observations retain managed requests but discard native identity and eligibility", () =>
{
    var cache = new RecentReaderCache();
    cache.Observe(Gc(stamp));
    Check(!cache.HasGc(stamp));
    cache.Observe(Gc(stamp.AddSeconds(1), eligible: false));
    var stored = cache.GcSupply(stamp.AddSeconds(2));
    Check(stored.Available && stored.LastObserved && stored.Entries[0].ItemId == 100);
    Check(stored.OwnerContentId == 0 && stored.MenuAddress == 0 && stored.AgentAddress == 0);
    Check(stored.Entries[0].TurnInAvailable is null && stored.Entries[0].BonusReward is null);
    cache.Observe(GcSupplySnapshot.Unavailable("Closed"));
    Check(cache.GcSupply(stamp.AddMinutes(5)).CapturedAtUtc == stamp.AddSeconds(1));
    Check(!cache.HasGc(stamp.AddSeconds(1).AddMinutes(15)));
    Check(!cache.HasGc(stamp));
});
Test("changed GC requests require two matching samples before replacing retained rows", () =>
{
    var cache = new RecentReaderCache();
    cache.Observe(Gc(stamp)); cache.Observe(Gc(stamp.AddSeconds(1)));
    cache.Observe(Gc(stamp.AddSeconds(2), item: 200));
    Check(cache.GcSupply(stamp.AddSeconds(2)).Entries[0].ItemId == 100);
    cache.Observe(Gc(stamp.AddSeconds(3), item: 200));
    Check(cache.GcSupply(stamp.AddSeconds(3)).Entries[0].ItemId == 200);
    cache.ClearGc(); Check(!cache.HasGc(stamp.AddSeconds(3)));
});
Test("GC unavailable reason survives lookup without discarding a still valid observation", () =>
{
    var cache = new RecentReaderCache();
    const string reason = "The daily request data is unavailable; reopen the delivery menu.";
    cache.Observe(GcSupplySnapshot.Unavailable(reason));
    Check(cache.GcSupply(stamp).UnavailableReason == reason);
    cache.ClearGc();
    Check(cache.GcSupply(stamp).UnavailableReason != reason);
    cache.Observe(GcSupplySnapshot.Unavailable(reason));
    cache.Reset();
    Check(cache.GcSupply(stamp).UnavailableReason != reason);
    cache.Observe(Gc(stamp)); cache.Observe(Gc(stamp.AddSeconds(1)));
    cache.Observe(GcSupplySnapshot.Unavailable(reason));
    Check(cache.GcSupply(stamp.AddSeconds(2)).Available);
    Check(cache.GcSupply(stamp.AddSeconds(1).AddMinutes(15)).UnavailableReason == reason);
    cache.Observe(Gc(stamp.AddMinutes(16)));
    Check(!cache.GcSupply(stamp.AddMinutes(16)).Available);
    Check(cache.GcSupply(stamp.AddMinutes(16)).UnavailableReason != reason);
});
Test("Bestiary retains positive and unknown observations after closing and expires exactly at fifteen minutes", () =>
{
    var cache = new RecentReaderCache();
    cache.Observe(Bestiary(stamp)); cache.Observe(Bestiary(stamp.AddSeconds(1)));
    cache.Observe(CollectionSnapshot.Unavailable("beastmaster_bestiary", "Closed"));
    var stored = cache.Bestiary(stamp.AddMinutes(10));
    Check(stored.Available && stored.LastObserved && stored.Entries[0].Unlocked == true);
    Check(stored.CapturedAtUtc == stamp.AddSeconds(1));
    cache.Observe(Bestiary(stamp.AddSeconds(2), unlocked: false));
    Check(cache.Bestiary(stamp.AddSeconds(2)).Entries[0].Unlocked == true);
    cache.Observe(Bestiary(stamp.AddSeconds(3), unlocked: false));
    Check(cache.Bestiary(stamp.AddSeconds(3)).Entries[0].Unlocked is null);
    var lookup = new CollectionLookup(cache.Bestiary(stamp.AddSeconds(3)));
    Check(!lookup.IsUsable(stamp.AddSeconds(3).AddMinutes(15)));
    Check(!cache.HasBestiary(stamp.AddSeconds(3).AddMinutes(15)));
});
Test("reset candidates preserves recent copies while owner reset clears all data", () =>
{
    var cache = new RecentReaderCache();
    cache.Observe(Gc(stamp)); cache.Observe(Gc(stamp.AddSeconds(1)));
    cache.Observe(Bestiary(stamp)); cache.Observe(Bestiary(stamp.AddSeconds(1)));
    cache.ResetCandidates();
    Check(cache.HasGc(stamp.AddMinutes(1)) && cache.HasBestiary(stamp.AddMinutes(1)));
    cache.Reset();
    Check(!cache.HasGc(stamp.AddMinutes(1)) && !cache.HasBestiary(stamp.AddMinutes(1)));
    cache.Observe(Gc(stamp.AddSeconds(2)));
    Check(!cache.HasGc(stamp.AddSeconds(2)));
});

if (args is ["--produce-fixtures", var path])
{
    var cache = new RecentReaderCache();
    cache.Observe(Gc(stamp)); cache.Observe(Gc(stamp.AddSeconds(1)));
    cache.Observe(Bestiary(stamp)); cache.Observe(Bestiary(stamp.AddSeconds(1)));
    var gcArgs = JsonDocument.Parse("{}").RootElement.Clone();
    var collectionArgs = JsonDocument.Parse("{\"category\":\"beastmaster_bestiary\"}").RootElement.Clone();
    object[] fixtures = [
        new { name = "gc_recent", arguments = gcArgs, result = new GcSupplyLookup(cache.GcSupply(stamp.AddSeconds(2))).Query(gcArgs) },
        new { name = "bestiary_recent", arguments = collectionArgs, result = new CollectionLookup(cache.Bestiary(stamp.AddSeconds(2))).Query(collectionArgs) }];
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    File.WriteAllText(path, JsonSerializer.Serialize(fixtures));
}
Console.WriteLine($"{passed} recent-reader fixture groups passed; no game/account/network calls.");
