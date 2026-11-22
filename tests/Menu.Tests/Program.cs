using System.Text.Json;
using CompAI;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

var passed = 0;
void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Test(string name, Action action) { action(); ++passed; Console.WriteLine("PASS " + name); }
void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Expected rejection"); }
JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;
MenuSnapshot Snapshot(string category, MenuEntry[] entries) => new(category, DateTimeOffset.Parse("2026-01-01T00:00:00Z"), true,
    "Synthetic menu fixture", "synthetic_ready", entries);
MenuEntry Entry(uint id = 1, uint item = 100, string role = "offer", uint? quantity = 1) => new(id, item, "Synthetic Item", quantity, role,
    "Synthetic menu", null, (int)id - 1, null, role == "offer" ? [new("item", 1, "Gil", 50)] : []);
MenuLookupResult Query(MenuSnapshot snapshot, string json) => new MenuLookup(snapshot).Query(Json(json));
MenuSnapshot SpellingFixture() => Snapshot("glamour_dresser",
    [Entry(role: "stored", quantity: null) with { Name = "Figments of the Shallows" },
     Entry(2, 101, "stored", null) with { Name = "Augmented Deepshadow Greaves of Casting" }]);

Test("strict selectors and permission scopes reject malformed or ungranted categories", () =>
{
    foreach (var json in new[] { "null", "[]", "{}", "{\"category\":\"inventory\"}", "{\"category\":\"shops\",\"unknown\":1}",
        "{\"category\":\"shops\",\"category\":\"armoire\"}", "{\"category\":\"shops\",\"query\":\"x\"}",
        "{\"category\":\"shops\",\"query\":\"ab\\n\"}", "{\"category\":\"shops\",\"offset\":10000}",
        "{\"category\":\"shops\",\"limit\":51}", "{\"category\":\"shops\",\"item_ids\":[1,1]}",
        "{\"category\":\"shops\",\"item_ids\":[true]}" }) Reject(() => MenuLookup.ValidateArguments(Json(json)));
    Reject(() => MenuLookup.ValidateArguments(Json("{\"category\":\"shops\"}"), ["armoire"]));
    Check(MenuLookup.Parse(Json("{\"category\":\"shops\",\"item_ids\":[1],\"query\":\"\"}")).ItemIds.Contains(1));
    Check(MenuLookup.Parse(Json("{\"category\":\"shops\",\"query\":\"   \"}")).Query == "");
});
Test("storage roles distinguish appearances from physical quantities and plate references", () =>
{
    var stored = Snapshot("armoire", [Entry(role: "stored", quantity: null)]);
    Check(Query(stored, "{\"category\":\"armoire\"}").entries[0].quantity is null);
    var references = Snapshot("glamour_plates", [Entry(role: "appearance_reference", quantity: null),
        Entry(2, role: "appearance_reference", quantity: null) with { GroupId = 1 }]);
    var result = Query(references, "{\"category\":\"glamour_plates\"}");
    Check(result.entries.Length == 2 && result.entries.All(row => row.quantity is null));
    Reject(() => Snapshot("glamour_plates", [Entry(role: "appearance_reference", quantity: 1)]));
    Reject(() => Snapshot("glamour_dresser", [Entry()]));
    Reject(() => Snapshot("armoire", [Entry(role: "stored") with { Costs = [new("item", 1, "Gil", 1)] }]));
});
Test("menu snapshots deep-copy costs and returned pages", () =>
{
    var entry = Entry();
    var snapshot = Snapshot("shops", [entry]);
    entry.Costs[0] = new("item", 999, "Injected", 999);
    snapshot.Entries[0].Costs[0] = new("item", 999, "Injected", 999);
    var page = Query(snapshot, "{\"category\":\"shops\"}");
    page.entries[0].costs[0] = new("unknown", null, null, null, "Injected");
    Check(Query(snapshot, "{\"category\":\"shops\"}").entries[0].costs[0].item_id == 1);
});
Test("dresser name misses suggest close observed spellings apart from matching rows", () =>
{
    var dresser = SpellingFixture();
    MenuLookupResult Search(MenuSnapshot snapshot, string name, int limit = 50) => new MenuLookup(snapshot).Query(
        JsonSerializer.SerializeToElement(new { category = snapshot.Category, query = name, limit }));
    foreach (var typo in new[] { "Frigment of the Shadows", "Fragment of the Shadows" })
    {
        var result = Search(dresser, typo);
        Check(result.suggested_names!.SequenceEqual(["Figments of the Shallows"]));
        Check(result.matched_count == 0 && result.entries.Length == 0 && !result.truncated);
    }
    var exact = Search(dresser, "Figments of the Shallows");
    Check(exact.suggested_names is null && exact.entries.Single().name == "Figments of the Shallows");
    Check(Search(dresser, "shadow").entries.Single().item_id == 101);
    Check(Search(dresser, "Completely Different Item").suggested_names is null);
    Check(Search(Snapshot("glamour_dresser", [dresser.Entries[1]]), "Fragment of the Shadows").suggested_names is null);
    Check(Query(dresser, """{"category":"glamour_dresser","query":"Fragment of the Shadows","item_ids":[101]}""").suggested_names is null);
    Check(Query(dresser, """{"category":"glamour_dresser","query":"Fragment of the Shadows","offset":1}""").suggested_names is null);
    Check(Search(Snapshot("armoire", dresser.Entries.ToArray()), "Fragment of the Shadows").suggested_names is null);
    var names = new[] { "Royal Circlet B", "Royal Circlet A", "Royal Circlet B", "Royal Circlet C", "Royal Circlet D" };
    var similar = Snapshot("glamour_dresser", names.Select((name, i) => Entry((uint)i + 1, role: "stored") with { Name = name }).ToArray());
    Check(Search(similar, "Royal Circlet X").suggested_names!.SequenceEqual(["Royal Circlet A", "Royal Circlet B", "Royal Circlet C"]));
    Check(Search(similar, "Royal Circlet X", 1).suggested_names!.SequenceEqual(["Royal Circlet A"]));
});
Test("bounded pagination keeps separate duplicate offers and filters by item identity", () =>
{
    var snapshot = Snapshot("shops", Enumerable.Range(1, 100).Select(id => Entry((uint)id, id % 2 == 0 ? 100u : 101u)).ToArray());
    var page = Query(snapshot, "{\"category\":\"shops\",\"item_ids\":[100],\"query\":\"synthetic\",\"offset\":20,\"limit\":2}");
    Check(page.matched_count == 50 && page.entries.Select(row => row.id).SequenceEqual(new uint[] { 42, 44 }) && page.next_offset == 22);
    var none = Query(snapshot, "{\"category\":\"shops\",\"item_ids\":[999]}");
    Check(none.available && none.entries.Length == 0 && none.catalog_count == 100);
    var missing = Query(MenuSnapshot.Unavailable("shops", "Synthetic", "Open shop"), "{\"category\":\"shops\"}");
    Check(!missing.available && missing.catalog_count is null && missing.matched_count == 0);
});
Test("large Unicode cost pages shrink under the wire budget with progress", () =>
{
    var rows = Enumerable.Range(1, 50).Select(id => Entry((uint)id) with { Name = new string('界', 128),
        Costs = Enumerable.Range(1, 8).Select(n => new MenuCost("item", (uint)n, new string('界', 128), 1)).ToArray() }).ToArray();
    var result = Query(Snapshot("shops", rows), "{\"category\":\"shops\"}");
    Check(result.entries.Length is > 0 and < 50 && result.next_offset == result.entries.Length);
    Check(JsonSerializer.SerializeToUtf8Bytes(result).Length <= MenuLookup.MaxBytes);
});
Test("bounded outfit references above four thousand remain pageable", () =>
{
    var rows = Enumerable.Range(1, 8800).Select(id => Entry((uint)id, role: "stored", quantity: null)).ToArray();
    var page = Query(Snapshot("glamour_dresser", rows), "{\"category\":\"glamour_dresser\",\"offset\":8799,\"limit\":1}");
    Check(page.catalog_count == 8800 && page.entries.Single().id == 8800 && page.next_offset is null);
    Reject(() => Snapshot("glamour_dresser", [Entry(10001, role: "stored", quantity: null)]));
});
Test("shop cost mapping preserves bundles, multiple currencies, and unknown values", () =>
{
    string? Name(uint id) => id switch { 1 => "Gil", 100 => "Synthetic Hat", 200 => "Synthetic Token", 201 => "Synthetic Seal", _ => null };
    var rows = ShopReader.Describe([new(100, 1, 1, 50, 0, 0, 0, 0), new(100, 3, 200, 5, 201, 10, 0, 0),
        new(100, 0, 999, 7, 0, 0, 0, 0), new(100, 1, 0, 0, 0, 0, 0, 0), new(100, 1, 200, 0, 0, 0, 0, 0)], Name, "Synthetic shop");
    Check(rows[0].Costs.Single().ItemId == 1 && rows[0].Costs.Single().Amount == 50);
    Check(rows[1].Quantity == 3 && rows[1].Costs.Select(cost => cost.ItemId).SequenceEqual(new uint?[] { 200, 201 }));
    Check(rows[2].Quantity is null && rows[2].Costs.Single().Kind == "unknown" && rows[2].Costs.Single().Amount == 7);
    Check(rows[3].Costs.Single().UnknownReason is not null && rows[4].Costs.Single().Amount is null);
    Snapshot("shops", rows);
});
Test("native shop copying is limited to verified counts and three cost slots", NativeCopy);
Test("gil offers use the handler's visible ordering, unit price, and actual HQ flag", NativeGil);
Test("hidden-parent Buyback nodes do not suppress the visible Buy tab", NativeGilVisibility);
Test("manual shop diagnostic projects only bounded public offer fields and readiness", NativeGilDiagnostic);
Test("scrip exchange reads typed visible bundles and resolved cost IDs", NativeInclusion);
Test("currency exchange costs require independently matching item rows and bounded mapping", NativeCurrency);
Test("company-credit item offers preserve the named non-item price", NativeCompanyCredits);

if (args is ["--produce-fixtures", var path])
{
    object Fixture(string name, MenuSnapshot snapshot, string query) => new { name, arguments = Json(query).Clone(), result = Query(snapshot, query) };
    var dresser = SpellingFixture();
    var fixtures = new[] {
        Fixture("shop", Snapshot("shops", [Entry()]), "{\"category\":\"shops\"}"),
        Fixture("exchange", Snapshot("shops", [Entry() with { Quantity = 3, Costs = [new("item",200,"Synthetic Token",5),new("item",201,"Synthetic Seal",10)] }]), "{\"category\":\"shops\"}"),
        Fixture("unknown_cost", Snapshot("shops", [Entry() with { Quantity = null, Costs = [new("unknown",null,null,null,"Unmapped currency")] }]), "{\"category\":\"shops\"}"),
        Fixture("named_currency", Snapshot("shops", [Entry() with { Costs = [new("unknown",null,"Company credits",100,"Named non-item currency; inventory balance comparison is unsupported.")] }]), "{\"category\":\"shops\"}"),
        Fixture("armoire", Snapshot("armoire", [Entry(role:"stored",quantity:null)]), "{\"category\":\"armoire\"}"),
        Fixture("dresser", Snapshot("glamour_dresser", [Entry(role:"stored",quantity:null)]), "{\"category\":\"glamour_dresser\"}"),
        Fixture("dresser_spelling", dresser, """{"category":"glamour_dresser","query":"Frigment of the Shadows"}"""),
        Fixture("dresser_corrected", dresser, """{"category":"glamour_dresser","query":"Figments of the Shallows"}"""),
        Fixture("outfit_bounds", Snapshot("glamour_dresser", Enumerable.Range(1,8800).Select(id=>Entry((uint)id,role:"stored",quantity:null)).ToArray()), "{\"category\":\"glamour_dresser\",\"offset\":8799,\"limit\":1}"),
        Fixture("plates", Snapshot("glamour_plates", [Entry(role:"appearance_reference",quantity:null),Entry(2,role:"appearance_reference",quantity:null)]), "{\"category\":\"glamour_plates\"}"),
        Fixture("pagination", Snapshot("shops", Enumerable.Range(1,60).Select(id=>Entry((uint)id)).ToArray()), "{\"category\":\"shops\",\"offset\":20,\"limit\":2}"),
        Fixture("missing", Snapshot("shops", [Entry()]), "{\"category\":\"shops\",\"item_ids\":[999]}"),
        Fixture("unavailable", MenuSnapshot.Unavailable("shops","Synthetic source","Open the shop"), "{\"category\":\"shops\"}") };
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    File.WriteAllText(path, JsonSerializer.Serialize(fixtures));
}
Console.WriteLine($"{passed} menu fixture groups passed; no game/account/network calls.");

static unsafe void NativeCopy()
{
    var receive = stackalloc AgentShop.ShopItem[2];
    var costs = stackalloc AgentShop.ShopItem[6];
    receive[0].ItemId = 100; receive[0].ItemCount = 1;
    receive[1].ItemId = 101; receive[1].ItemCount = 3;
    costs[0].ItemId = 1; costs[0].ItemCount = 50;
    costs[3].ItemId = 200; costs[3].ItemCount = 5;
    costs[4].ItemId = 201; costs[4].ItemCount = 10;
    var agent = new AgentShop { ItemReceive = receive, ItemReceiveCount = 2, ItemCost = costs, ItemCostCount = 6 };
    var rows = ShopReader.CopyRows(&agent);
    if (rows.Length != 2 || rows[1].Cost0Id != 200 || rows[1].Cost1Amount != 10) throw new Exception("Cost grouping failed");
    foreach (var (count, costCount, received, costPresent) in new[] { (-1,0,true,true), (1025,0,true,true), (1,2,true,true), (1,3,false,true), (1,3,true,false) })
        if (ShopReader.SupportedLayout(count,costCount,received,costPresent)) throw new Exception("Unsupported native layout accepted");
    agent.ItemCostCount = 0;
    if (ShopReader.CopyRows(&agent)[0].Cost0Id != 0) throw new Exception("Absent costs were read");
}

static void InvalidNative(Action action)
{
    try { action(); } catch (InvalidOperationException) { return; }
    throw new Exception("Unsupported native state accepted.");
}

static unsafe void NativeGil()
{
    var handler = new ShopEventHandler { CurrentMode = 1, ItemsCount = 2, VisibleItemsCount = 2 };
    handler.Items[0] = new() { ItemId = 100, PriceBuy = 50, StackSize = 999 };
    handler.Items[1] = new() { ItemId = 101, PriceBuy = 100, StackSize = 99, IsHQ = true };
    handler.VisibleItems[0] = 1; handler.VisibleItems[1] = 0;
    var pointer = &handler;
    var rows = ShopReader.CopyGilRows(pointer);
    if (rows[0].ItemId != 101 || rows[0].Quantity != 1 || rows[0].IsHq != true || rows[0].Cost0Amount != 100 || !rows[0].GilCost)
        throw new Exception("Gil row mapping or per-unit price failed.");
    var described = ShopReader.Describe(rows, id => id == 1 ? "Gil" : "Fixture", "Gil shop");
    if (described[0].Costs[0].Kind != "gil" || described[0].Costs[0].ItemId != null) throw new Exception("Gil classified as an item.");
    handler.CurrentMode = 0;
    if (ShopReader.CopyGilRows(pointer).Length != 2) throw new Exception("Idle transaction mode suppressed visible Buy offers.");
    handler.CurrentMode = 2; InvalidNative(() => ShopReader.CopyGilRows(pointer));
    handler.CurrentMode = 1;
    handler.VisibleItems[1] = 2; InvalidNative(() => ShopReader.CopyGilRows(pointer));
    handler.VisibleItems[1] = 1; InvalidNative(() => ShopReader.CopyGilRows(pointer));
    handler.VisibleItems[1] = 0;
    handler.BuybackTabActive = true; InvalidNative(() => ShopReader.CopyGilRows(pointer));
    handler.BuybackTabActive = false; handler.NeedSorting = true;
    if (!ShopReader.GilReady(pointer) || !rows.SequenceEqual(ShopReader.CopyGilRows(pointer)))
        throw new Exception("Sort flag suppressed readable visible Buy offers.");
    handler.StartingBuy = true; InvalidNative(() => ShopReader.CopyGilRows(pointer));
    handler.StartingBuy = false; handler.StartingSell = true; InvalidNative(() => ShopReader.CopyGilRows(pointer));
    handler.StartingSell = false; handler.WaitingForTransactionToFinish = true; InvalidNative(() => ShopReader.CopyGilRows(pointer));
    handler.WaitingForTransactionToFinish = false;
    handler.VisibleItems[1] = 1; InvalidNative(() => ShopReader.CopyGilRows(pointer));
    handler.VisibleItems[1] = 2; InvalidNative(() => ShopReader.CopyGilRows(pointer));
    handler.VisibleItems[1] = 0;
    handler.Items[0].PriceBuy = -1; InvalidNative(() => ShopReader.CopyGilRows(pointer));
    handler.Items[0].PriceBuy = 50; handler.Items[0].ItemId = 0; InvalidNative(() => ShopReader.CopyGilRows(pointer));
}

static unsafe void NativeGilVisibility()
{
    var buyParent = new AtkResNode { NodeFlags = NodeFlags.Visible };
    var buybackParent = new AtkResNode();
    var buy = new AtkComponentNode { NodeFlags = NodeFlags.Visible, ParentNode = &buyParent };
    var buyback = new AtkComponentNode { NodeFlags = NodeFlags.Visible, ParentNode = &buybackParent };
    var buyList = new AtkComponentList { OwnerNode = &buy };
    var buybackList = new AtkComponentList { OwnerNode = &buyback };
    var addon = new AddonShop { BuyList = &buyList, BuybackList = &buybackList };
    if (!ShopReader.GilBuyListVisible(&addon)) throw new Exception("Hidden Buyback parent rejected the Buy tab.");
    buybackParent.NodeFlags = NodeFlags.Visible;
    if (ShopReader.GilBuyListVisible(&addon)) throw new Exception("Actually visible Buyback was accepted.");
    buybackParent.NodeFlags = 0; buyParent.NodeFlags = 0;
    if (ShopReader.GilBuyListVisible(&addon)) throw new Exception("Hidden Buy tab was accepted.");
    buyParent.NodeFlags = NodeFlags.Visible; buyParent.ParentNode = &buyParent;
    if (ShopReader.GilBuyListVisible(&addon)) throw new Exception("Cyclic visibility ancestry was accepted.");
    buyParent.ParentNode = null; buybackParent.NodeFlags = NodeFlags.Visible;
    buybackParent.ParentNode = &buybackParent;
    if (ShopReader.GilBuyListVisible(&addon) || ShopReader.VisibleThroughParents((AtkResNode*)&buyback) != null)
        throw new Exception("Cyclic Buyback ancestry was treated as hidden.");
    var ancestors = stackalloc AtkResNode[32];
    for (var i = 0; i < 32; i++)
        ancestors[i] = new AtkResNode { NodeFlags = NodeFlags.Visible, ParentNode = i < 31 ? &ancestors[i + 1] : null };
    buyback.ParentNode = ancestors;
    if (ShopReader.GilBuyListVisible(&addon) || ShopReader.VisibleThroughParents((AtkResNode*)&buyback) != null)
        throw new Exception("Over-depth Buyback ancestry was treated as hidden.");
    buyback.ParentNode = &ancestors[1];
    if (ShopReader.VisibleThroughParents((AtkResNode*)&buyback) != true)
        throw new Exception("Valid 32-node ancestry was not recognized.");
    buyback.ParentNode = &buybackParent; buybackParent.NodeFlags = 0; buy.ParentNode = ancestors;
    if (ShopReader.GilBuyListVisible(&addon) || ShopReader.VisibleThroughParents((AtkResNode*)&buy) != null)
        throw new Exception("Over-depth Buy ancestry was accepted.");
    buy.ParentNode = &buyParent; buybackList.OwnerNode = null;
    if (ShopReader.GilBuyListVisible(&addon)) throw new Exception("Missing Buyback owner was treated as hidden.");
}

static unsafe void NativeGilDiagnostic()
{
    var handler = new ShopEventHandler { ItemsCount = 60, VisibleItemsCount = 60 };
    for (var i = 0; i < 60; i++)
    {
        handler.VisibleItems[i] = i;
        handler.Items[i] = new() { ItemId = 100u + (uint)i, PriceBuy = 50, NumOwned = 87654321 };
    }
    var proxy = new ShopEventHandler.AgentProxy { AddonId = 123, Handler = &handler };
    var addon = new AddonShop { Id = 123 };
    var json = ShopReader.FormatGilDiagnostic(&addon, &proxy);
    var output = JsonDocument.Parse(json).RootElement;
    if (System.Text.Encoding.UTF8.GetByteCount(json) > 16384 || output.GetProperty("visible_offers").GetArrayLength() != 60 ||
        json.Contains("87654321") || !output.GetProperty("proxy_matches_addon").GetBoolean())
        throw new Exception("Shop diagnostic exceeded bounds or exposed unrequested fields.");
    proxy.AddonId = 124;
    // A stale mismatching proxy must not be followed, even if its handler pointer is unusable.
    proxy.Handler = (ShopEventHandler*)1;
    output = JsonDocument.Parse(ShopReader.FormatGilDiagnostic(&addon, &proxy)).RootElement;
    if (output.GetProperty("handler_present").GetBoolean() || output.GetProperty("visible_offers").GetArrayLength() != 0)
        throw new Exception("Mismatched shop proxy was followed.");
    proxy.AddonId = 123; proxy.Handler = &handler; handler.VisibleItems[59] = 60;
    output = JsonDocument.Parse(ShopReader.FormatGilDiagnostic(&addon, &proxy)).RootElement;
    if (output.GetProperty("visible_mapping_valid").GetBoolean() || output.GetProperty("visible_offers").GetArrayLength() != 0)
        throw new Exception("Invalid native row mapping produced partial diagnostic offers.");
    var buyback = new AtkComponentNode { NodeFlags = NodeFlags.Visible };
    buyback.ParentNode = (AtkResNode*)&buyback;
    var buybackList = new AtkComponentList { OwnerNode = &buyback };
    addon.BuybackList = &buybackList;
    output = JsonDocument.Parse(ShopReader.FormatGilDiagnostic(&addon, &proxy)).RootElement;
    if (output.GetProperty("buyback_visible_through_parents").ValueKind != JsonValueKind.Null ||
        output.GetProperty("buyback_ancestors").GetArrayLength() != 32)
        throw new Exception("Invalid Buyback ancestry was reported as known visibility.");
}

static unsafe void NativeInclusion()
{
    var values = new AddonInclusionShop.InclusionShopAtkValues();
    values.ItemCount = UInt(1);
    values.Items[0].ItemId = UInt(100); values.Items[0].Stacksize = UInt(3);
    values.Items[0].GiveCount = UInt(2);
    values.Items[0].GiveItemId[0] = UInt(200); values.Items[0].GiveAmount[0] = UInt(500);
    values.Items[0].GiveItemId[1] = UInt(201); values.Items[0].GiveAmount[1] = UInt(10);
    var addon = new AddonInclusionShop { AtkValues = (AtkValue*)&values, AtkValuesCount = 2939 };
    var pointer = &addon;
    var row = ShopReader.CopyInclusionRows(pointer).Single();
    if (row.Quantity != 3 || row.Cost0Id != 200 || row.Cost0Amount != 500 || row.Cost1Id != 201 || row.Cost1Amount != 10)
        throw new Exception("Inclusion shop cost or bundle fields failed.");
    values.Items[0].GiveCount = UInt(4); InvalidNative(() => ShopReader.CopyInclusionRows(pointer));
    values.Items[0].GiveCount = UInt(2); values.Items[0].GiveAmount[0] = new AtkValue { Type = AtkValueType.Float };
    InvalidNative(() => ShopReader.CopyInclusionRows(pointer));
    values.Items[0].GiveAmount[0] = UInt(500); addon.AtkValuesCount = 316;
    InvalidNative(() => ShopReader.CopyInclusionRows(pointer));
    addon.AtkValuesCount = 2939; values.ItemCount = UInt(61);
    InvalidNative(() => ShopReader.CopyInclusionRows(pointer));
}

static unsafe void NativeCurrency()
{
    var values = stackalloc AtkValue[1432];
    var received = stackalloc AgentShop.ShopItem[2];
    received[0].ItemId = 100; received[0].ItemCount = 1;
    received[1].ItemId = 101; received[1].ItemCount = 3;
    values[4] = UInt(2); values[87] = UInt(999);
    values[1066] = UInt(100); values[1067] = UInt(101);
    values[456] = UInt(2); values[457] = UInt(5);
    values[1310] = UInt(0); values[1311] = UInt(1);
    var addon = new AtkUnitBase { AtkValues = values, AtkValuesCount = 1432 };
    var agent = new AgentShop { ItemReceive = received, ItemReceiveCount = 2, ItemCostCount = 0 };
    var agentPointer = &agent; var addonPointer = &addon;
    var rows = ShopReader.CopyCurrencyRows(agentPointer, addonPointer);
    if (rows[0].Cost0Amount != 2 || rows[1].Cost0Amount != 5 || rows[1].Quantity != 3)
        throw new Exception("Currency costs were not read separately from the empty common cost array.");
    values[1066] = UInt(999); InvalidNative(() => ShopReader.CopyCurrencyRows(agentPointer, addonPointer));
    values[1066] = UInt(100); values[1311] = UInt(0); InvalidNative(() => ShopReader.CopyCurrencyRows(agentPointer, addonPointer));
    values[1311] = UInt(1); addon.AtkValuesCount = 1311; InvalidNative(() => ShopReader.CopyCurrencyRows(agentPointer, addonPointer));
    addon.AtkValuesCount = 1432; agent.ItemReceiveCount = 1; InvalidNative(() => ShopReader.CopyCurrencyRows(agentPointer, addonPointer));
}

static unsafe void NativeCompanyCredits()
{
    var values = stackalloc AtkValue[150];
    values[9] = UInt(1); values[30] = UInt(100); values[130] = UInt(1000);
    var addon = new AtkUnitBase { AtkValues = values, AtkValuesCount = 150 };
    var pointer = &addon;
    var described = ShopReader.Describe(ShopReader.CopyCompanyCreditRows(pointer), _ => "Fixture", "FC item shop").Single();
    if (described.Quantity != 1 || described.Costs[0].Kind != "unknown" || described.Costs[0].Name != "Company credits" ||
        described.Costs[0].Amount != 1000 || described.Costs[0].ItemId != null)
        throw new Exception("Company credit amount or non-item identity was lost.");
    values[9] = UInt(21); InvalidNative(() => ShopReader.CopyCompanyCreditRows(pointer));
    values[9] = UInt(1); values[30] = UInt(0); InvalidNative(() => ShopReader.CopyCompanyCreditRows(pointer));
}

static AtkValue UInt(uint value) => new() { Type = AtkValueType.UInt, UInt = value };
