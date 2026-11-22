using System.Text.Json;
using System.Collections.Immutable;
using CompAI;

var captured = DateTimeOffset.UtcNow.AddMinutes(-5);
StorageContainerCapture Capture(string category, string key, string source, string container, StorageItem[] items) =>
    new(category, key, source, container, 35, items, captured);
var saddle = Capture("saddlebag", "PRIVATE-SOURCE-KEY", "Saddlebag", "Page 1", [new(0, 1, 1, "Test Ore", 12, false)]);
var retainer = Capture("retainers", "PRIVATE-RETAINER-ID", "Synthetic Retainer", "Current market listings",
    [new(11, 1, 1, "Test Ore", 2, false, 700)]);
var housing = Capture("housing", "PRIVATE-HOUSE-ID", "Synthetic estate", "Placed indoors", [new(2, 2, 2, "Test Chair", 1, false)]);
var empty = Capture("fc_chest", "PRIVATE-FC-ID", "Synthetic FC", "Chest 1", []);
using var argsDoc = JsonDocument.Parse("{}");
var fixtures = new List<object>(); var passed = 0;
void Check(bool condition) { if (!condition) throw new Exception("Storage assertion failed."); }
JsonElement Args(string view="items",string category="all",string query="",string source="",int offset=0,int limit=30) =>
    JsonSerializer.SerializeToElement(new { view, category, query, source, offset, limit });
JsonElement Query(string name, StorageLookup lookup, JsonElement args)
{
    var result = lookup.Query(args);
    fixtures.Add(new { name, summary=lookup.Summary, arguments=args, result });
    return JsonSerializer.SerializeToElement(result);
}
void Test(string name, Action run) { run(); passed++; Console.WriteLine("PASS " + name); }
var lookup = new StorageLookup([saddle,retainer,housing,empty], StorageSnapshot.Categories);
Test("exact scope excludes IDs and keeps housing separate from owned bags", () =>
{
    var r = Query("all items",lookup,Args());
    Check(r.GetProperty("total_matches").GetInt32()==3 && r.GetProperty("total_quantity").GetInt64()==15);
    Check(!r.GetRawText().Contains("PRIVATE") && r.GetProperty("rows").EnumerateArray().Any(x=>x.GetProperty("category").GetString()=="housing"));
});
Test("search and pagination retain totals and offer prices", () =>
{
    var r=Query("ore page",lookup,Args(query:"Ore",limit:1));
    Check(r.GetProperty("total_quantity").GetInt64()==14 && r.GetProperty("next_offset").GetInt32()==1);
    r=Query("retainer price",lookup,Args(category:"retainers",source:" Synthetic "));
    Check(r.GetProperty("rows")[0].GetProperty("unit_price_gil").GetInt32()==700);
    r=Query("empty page",lookup,Args(offset:int.MaxValue));
    Check(r.GetProperty("rows").GetArrayLength()==0 && r.GetProperty("next_offset").ValueKind==JsonValueKind.Null);
});
Test("observed empty container stays distinct from never-observed storage", () =>
{
    var r=Query("known empty",lookup,Args(view:"containers",category:"fc_chest"));
    Check(r.GetProperty("rows")[0].GetProperty("occupied_slots").GetInt32()==0 && r.GetProperty("total_quantity").ValueKind==JsonValueKind.Null);
    r=Query("unknown source",lookup,Args(view:"containers",source:"Never Visited"));
    Check(r.GetProperty("total_matches").GetInt32()==0);
});
Test("permissions filter sources and reject direct disabled category queries", () =>
{
    var permitted=new StorageLookup([saddle,retainer,housing,empty],["saddlebag"]);
    Check(Query("permission",permitted,Args()).GetProperty("total_matches").GetInt32()==1);
    try { permitted.Query(Args(category:"retainers")); throw new Exception("Permission accepted"); } catch(ArgumentException) { }
});
Test("frozen rows cannot change when live capture arrays mutate", () =>
{
    var before=Query("frozen",lookup,Args(category:"saddlebag")).GetRawText();
    saddle.Items[0]=saddle.Items[0] with { Quantity=999 };
    Check(before==Query("frozen again",lookup,Args(category:"saddlebag")).GetRawText());
});
Test("invalid bounds duplicates and misleading prices are rejected", () =>
{
    Check(!StorageSnapshot.Valid(retainer with { Items=[retainer.Items[0],retainer.Items[0]] }));
    Check(!StorageSnapshot.Valid(housing with { Items=[housing.Items[0] with { UnitPriceGil=1 }] }));
    Check(!StorageSnapshot.Valid(empty with { CapturedAtUtc=DateTimeOffset.UtcNow.AddDays(1) }));
    Check(!StorageSnapshot.Valid(empty with { Capacity=1001 }));
    foreach (var arg in new[] { Args(limit:0), Args(offset:-1), Args(category:"retainer"), Args(query:new string('x',201)) })
    { try { lookup.Query(arg); throw new Exception("Invalid query accepted"); } catch(ArgumentException) { } }
});
Test("source clears use identity, preserve other categories, and reject stale character confirmations", () =>
{
    var sameName = retainer with { SourceKey = "ANOTHER-RETAINER-ID" };
    var renamedPage = retainer with { SourceLabel = "Renamed retainer", Container = "RetainerPage1" };
    var sameKeyOtherCategory = empty with { SourceKey = retainer.SourceKey };
    var captures = ImmutableArray.Create(retainer, sameName, renamedPage, saddle, housing, sameKeyOtherCategory);
    var source = new StorageClearRequest(123, 5, "retainers", retainer.SourceKey);
    Check(source.MatchesCharacter(123, 5) && !source.MatchesCharacter(456, 5) && !source.MatchesCharacter(123, 6));
    var remaining = source.Apply(captures);
    Check(remaining.Length == 4 && remaining.Contains(sameName) && remaining.Contains(sameKeyOtherCategory));
    var category = new StorageClearRequest(123, 5, "retainers", null).Apply(captures);
    Check(category.Length == 3 && category.All(c => c.Category != "retainers"));
    Check(new StorageClearRequest(123, 5, null, null).Apply(captures).IsEmpty && captures.Length == 6);
    foreach (var invalid in new[] { new StorageClearRequest(0, 5, null, null),
        new StorageClearRequest(123, 5, null, "retainer"), new StorageClearRequest(123, 5, "unknown", null),
        new StorageClearRequest(123, 5, "retainers", " ") })
    {
        Check(!invalid.MatchesCharacter(invalid.OwnerId, invalid.Epoch));
        try { invalid.Apply(captures); throw new Exception("Invalid clear selection accepted"); } catch (ArgumentException) { }
    }
});
Test("browser names and search preserve original container and item identities", () =>
{
    var row = retainer with { Container = "RetainerPage2" };
    Check(StorageBrowser.ContainerLabel(row.Container) == "Page 2" && row.Container == "RetainerPage2");
    Check(StorageBrowser.ContainerLabel("PremiumSaddleBag1") == "Premium page 1");
    Check(StorageBrowser.ContainerLabel("Indoor storeroom 10") == "Indoor storeroom 10");
    Check(StorageBrowser.Matches(row, "synthetic") && StorageBrowser.Matches(row, "ORE") &&
        StorageBrowser.Matches(row, "1") && StorageBrowser.Matches(row, "Page 2") &&
        !StorageBrowser.Matches(row, "PRIVATE-RETAINER-ID") && !StorageBrowser.Matches(row, "unrelated"));
    Check(StorageBrowser.Matches(empty, "FC") && empty.Items.Length == 0);
});
Console.WriteLine($"{passed} storage lookup groups passed.");
Test("personal extras preserve distinct namespaces and enforce their permission categories", () =>
{
    var crystals = Capture("crystals", "personal", "Carried inventory", "Crystals", [new(0, 1, 1, "Fire Shard", 9999, false)]);
    var keys = Capture("key_items", "personal", "Carried inventory", "KeyItems", [new(3, 2000001, 2000001, "Quest letter", 1, false)]);
    Check(StorageSnapshot.Valid(crystals) && StorageSnapshot.Valid(keys));
    Check(!StorageSnapshot.Valid(keys with { Items = [keys.Items[0] with { BaseItemId = 1 }] }));
    Check(!StorageSnapshot.Valid(keys with { Category = "retainers" }));
    Check(!StorageSnapshot.Valid(keys with { Category = "retainers", Items = [keys.Items[0] with { BaseItemId = 1 }] }));
    var personal = new StorageLookup([crystals, keys], ["crystals", "key_items"]);
    var result = Query("key items", personal, Args(query: "letter"));
    Check(result.GetProperty("rows")[0].GetProperty("item_namespace").GetString() == "event_item" &&
        result.GetProperty("rows")[0].GetProperty("base_item_id").GetUInt32() == 2000001);
    Check(JsonSerializer.SerializeToElement(new StorageLookup([crystals, keys], []).Query(Args())).GetProperty("total_matches").GetInt32() == 0);
    Query("personal crystals", personal, Args(category: "crystals"));
});
if (args.Length==2 && args[0]=="--write-fixture")
    File.WriteAllText(args[1],JsonSerializer.Serialize(fixtures));
ValuationChecks.Run(args);
