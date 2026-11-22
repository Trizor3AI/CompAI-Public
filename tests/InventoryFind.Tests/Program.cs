using CompAI;
using System.Text.Json;
using System.Text.Json.Nodes;

var tests = new (string Name, Action Run)[]
{
    ("same-name and same-ID cards retain separate bag/slot identity", ExactSlotIdentity),
    ("reply-name filter preserves exact slots without substring guesses", MentionedItems),
    ("structured suggestions preserve same-name slots and replace previous selections", SuggestedLocations),
    ("matching pages merge slots without mutating older snapshots", MergeAndSnapshots),
    ("owner, session, capture and change stamp mismatches reject updates", StampMismatch),
    ("invalid rows are ignored without creating or replacing groups", InvalidRows),
    ("four carried bags retain at most 140 exact slots", SlotBound),
    ("candidate enumeration stops at 140 rows, including invalid rows", EnumerationBound),
    ("32-group eviction ignores updates and rejected additions", GroupEviction),
    ("clear removes all handles and resets eviction bookkeeping", Clear),
    ("history retains handles in memory but excludes them from all JSON history forms", HistoryBoundary),
    ("chat switching retains handles while disk reload discards them", StoreBoundary),
    ("manual chat exports exclude handles without changing memory", ExportBoundary),
    ("failed saves retain handles while recovery exports exclude them", RecoveryBoundary),
};
var failures = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { ++failures; Console.WriteLine("FAIL " + name + ": " + ex); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} InventoryFind groups passed.");
return failures == 0 ? 0 : 1;

static InventoryFindStamp Stamp() => new(1234, 7, 8, 9);
static InventoryFindItem Item(string bag = "Inventory1", uint slot = 0) =>
    new("SYNTHETIC PEDIGREE CARD", bag, slot, 101, 101, 1, false);
static void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

static void ExactSlotIdentity()
{
    var catalog = new InventoryFindCatalog();
    var key = Guid.NewGuid();
    InventoryFindItem[] cards = [Item("Inventory2", 0), Item("Inventory1", 1), Item("Inventory1", 0)];
    Check(catalog.Add(key, Stamp(), cards), "same-name cards accepted");
    var group = catalog.Get(key)!;
    Check(group.Items.Count == 3, "same name and item ID must not collapse distinct slots");
    Check(group.Items.Select(item => (item.Bag, item.Slot)).SequenceEqual(new[]
        { ("Inventory1", 0u), ("Inventory1", 1u), ("Inventory2", 0u) }), "stable order uses bag then slot");
    Check(group.Items.All(item => item.Name == cards[0].Name && item.ItemId == cards[0].ItemId),
        "synthetic duplicate names and IDs remain unchanged");
    cards[0] = Item("Inventory4", 34);
    Check(group.Items.All(item => item.Bag != "Inventory4"), "caller array edits cannot change stored slots");
    Check(catalog.Get(null) is null && catalog.Get(Guid.NewGuid()) is null, "absent handles stay absent");
}

static void MergeAndSnapshots()
{
    var catalog = new InventoryFindCatalog();
    var key = Guid.NewGuid();
    Check(catalog.Add(key, Stamp(), [Item(), Item("Inventory2", 1)]), "first page");
    var before = catalog.Get(key)!;
    var replacement = Item() with { Quantity = 7, IsHq = true, ItemId = 1000101 };
    Check(catalog.Add(key, new(1234, 7, 8, 9), [replacement, Item("Inventory3", 2)]), "equal-valued stamp merges");
    var after = catalog.Get(key)!;
    Check(after.Items.Count == 3 && after.Items.Single(item => item.Bag == "Inventory1") == replacement,
        "the same exact bag/slot updates once without merging other locations");
    Check(before.Items.Count == 2 && before.Items[0].Quantity == 1 && !before.Items[0].IsHq,
        "previously returned snapshot remains immutable across updates");
    Check(after.Items.Single(item => item.Bag == "Inventory2").Slot == 1, "unmentioned previous page survives");
}

static void MentionedItems()
{
    var group = new InventoryFindGroup(Stamp(), new[] { Item(), Item("Inventory2", 4),
        Item("Inventory3", 1) with { Name = "Oil" }, Item("Inventory3", 2) with { Name = "Potion" } });
    Check(InventoryFindCatalog.MentionedItems(group, "Use the **synthetic pedigree card**.").SetEquals(new[] { 0, 1 }),
        "both same-name slots remain candidates and preserve original indexes");
    Check(InventoryFindCatalog.MentionedItems(group, "Boil soil; potionless. Take a Potion!").SetEquals(new[] { 3 }),
        "partial words do not create matches");
    Check(InventoryFindCatalog.MentionedItems(group, "No matching names").Count == 0,
        "unknown or translated names leave the all-results fallback available");
    Check(group.Items.Count == 4 && group.Items[1].Bag == "Inventory2" && group.Items[1].Slot == 4,
        "display filtering never rewrites identity or the stored group");
    Check(InventoryFindCatalog.MentionedItems(group, new string('x', 64001)).Count == 0, "bounded answer scan");
}

static void StampMismatch()
{
    var catalog = new InventoryFindCatalog();
    var key = Guid.NewGuid();
    var stamp = Stamp();
    catalog.Add(key, stamp, [Item()]);
    var before = catalog.Get(key);
    foreach (var changed in new[] { stamp with { Owner = 5678 }, stamp with { Session = 8 },
        stamp with { Capture = 9 }, stamp with { Changes = 10 } })
    {
        Check(!catalog.Add(key, changed, [Item("Inventory4", 34)]), "each stale stamp dimension rejects update");
        Check(ReferenceEquals(before, catalog.Get(key)), "stale update leaves the group untouched");
    }
}

static void SuggestedLocations()
{
    var catalog = new InventoryFindCatalog();
    var key = Guid.NewGuid();
    var cards = Enumerable.Range(9, 4).Select(slot => Item("Inventory1", (uint)slot)).ToArray();
    catalog.Add(key, Stamp(), cards);
    var all = catalog.Get(key)!;
    Check(all.SuggestedLocations.Count == 0, "bulk evidence lookup must not imply a preferred card");
    catalog.Add(key, Stamp(), [cards[1]], suggestReturnedLocations: true);
    var recommended = catalog.Get(key)!;
    Check(recommended.Items.Count == 4 && recommended.SuggestedLocations.SequenceEqual(new[] { ("Inventory1", 10u) }),
        "one-based slot 11 suggestion must keep all four independent same-name candidates");
    catalog.Add(key, Stamp(), [cards[2]], suggestReturnedLocations: true);
    var followup = catalog.Get(key)!;
    Check(followup.Items.Count == 4 && followup.SuggestedLocations.SequenceEqual(new[] { ("Inventory1", 11u) }),
        "one-based slot 12 replaces slot 11 instead of accumulating recommendations");
    Check(recommended.SuggestedLocations.Single().Slot == 10 && all.SuggestedLocations.Count == 0,
        "previously returned groups stay immutable");
    Check(!catalog.Add(key, Stamp() with { Capture = 99 }, [cards[0]], suggestReturnedLocations: true) &&
        ReferenceEquals(followup, catalog.Get(key)), "stale structured suggestion must be rejected");
    catalog.Add(key, Stamp(), cards);
    Check(catalog.Get(key)!.SuggestedLocations.Single().Slot == 11,
        "unfiltered evidence query leaves explicit suggestion intact");
    catalog.Add(key, Stamp(), [], suggestReturnedLocations: true);
    Check(catalog.Get(key)!.Items.Count == 4 && catalog.Get(key)!.SuggestedLocations.Count == 0,
        "an exact not-found lookup must clear an earlier suggestion without losing evidence");
    catalog.Add(key, Stamp(), [cards[0] with { Slot = 35 }], suggestReturnedLocations: true);
    Check(catalog.Get(key)!.SuggestedLocations.Count == 0, "invalid rows cannot become suggested locations");
}

static void InvalidRows()
{
    var catalog = new InventoryFindCatalog();
    var key = Guid.NewGuid();
    var valid = Item();
    InventoryFindItem[] invalid = [valid with { Bag = "Inventory5" }, valid with { Bag = "Retainer" },
        valid with { Bag = "inventory1" }, valid with { Slot = 35 }, valid with { Slot = uint.MaxValue },
        valid with { ItemId = 0 }, valid with { BaseItemId = 0 }, valid with { Quantity = 0 },
        valid with { Quantity = -1 }, valid with { Name = "" }, valid with { Name = " \t" },
        valid with { Name = null! }, valid with { Name = new string('x', 257) },
        valid with { Name = "SYNTHETIC\nCARD" }, valid with { Name = "SYNTHETIC\0CARD" },
        valid with { Name = "SYNTHETIC\u007fCARD" }];
    Check(!catalog.Add(key, Stamp(), invalid) && catalog.Get(key) is null, "invalid-only input creates no handle");
    Check(!catalog.Add(key, Stamp(), []), "empty input creates no handle");
    Check(catalog.Add(key, Stamp(), invalid.Append(valid)), "valid row after invalid candidates survives");
    Check(catalog.Get(key)!.Items.Count == 1 && catalog.Get(key)!.Items[0] == valid, "invalid rows are omitted");
    Check(catalog.Add(key, Stamp(), [valid with { Quantity = 0 }]) && catalog.Get(key)!.Items[0] == valid,
        "invalid replacement cannot destroy an already valid slot");
    Check(catalog.Add(key, Stamp(), [Item("Inventory4", 34) with { Name = new string('x', 256) }]),
        "maximum valid name and final valid slot accepted");
}

static void SlotBound()
{
    Check(InventoryFindCatalog.MaxItems == 140, "expected four-bag slot budget");
    var catalog = new InventoryFindCatalog();
    var key = Guid.NewGuid();
    var slots = Enumerable.Range(1, 4).SelectMany(bag => Enumerable.Range(0, 35)
        .Select(slot => Item("Inventory" + bag, (uint)slot))).ToArray();
    Check(catalog.Add(key, Stamp(), slots), "all carried slots accepted");
    var full = catalog.Get(key)!;
    Check(full.Items.Count == 140 && full.Items.Select(item => (item.Bag, item.Slot)).Distinct().Count() == 140,
        "every carried location retained exactly once");
    Check(catalog.Add(key, Stamp(), slots.Select(item => item with { Quantity = 2 })), "next page can refresh existing slots");
    Check(catalog.Get(key)!.Items.Count == 140 && catalog.Get(key)!.Items.All(item => item.Quantity == 2),
        "incremental additions cannot exceed the physical slot cap");
    Check(full.Items.All(item => item.Quantity == 1), "full-size previous snapshot remains unchanged");
}

static void EnumerationBound()
{
    var visited = 0;
    IEnumerable<InventoryFindItem> BoundedInput(bool valid)
    {
        for (var i = 0; i < 140; ++i)
        {
            ++visited;
            yield return valid ? Item() : Item() with { Quantity = 0 };
        }
        throw new InvalidOperationException("Read beyond the candidate budget");
    }
    var catalog = new InventoryFindCatalog();
    Check(catalog.Add(Guid.NewGuid(), Stamp(), BoundedInput(true)) && visited == 140,
        "even duplicate rows cannot cause an unbounded input scan");
    visited = 0;
    var key = Guid.NewGuid();
    Check(!catalog.Add(key, Stamp(), BoundedInput(false)) && visited == 140 && catalog.Get(key) is null,
        "invalid rows still consume the bounded input budget");
}

static void GroupEviction()
{
    Check(InventoryFindCatalog.MaxGroups == 32, "expected session group budget");
    var catalog = new InventoryFindCatalog();
    var keys = Enumerable.Range(0, 34).Select(_ => Guid.NewGuid()).ToArray();
    foreach (var key in keys.Take(32)) Check(catalog.Add(key, Stamp(), [Item()]), "fill group budget");
    Check(!catalog.Add(Guid.NewGuid(), Stamp(), [Item() with { Quantity = 0 }]), "invalid new group rejected");
    Check(keys.Take(32).All(key => catalog.Get(key) is not null), "invalid group does not evict retained actions");
    Check(catalog.Add(keys[0], Stamp(), [Item("Inventory2", 1)]), "updating a retained group does not add queue entries");
    catalog.Add(keys[32], Stamp(), [Item()]);
    Check(catalog.Get(keys[0]) is null && keys.Skip(1).Take(32).All(key => catalog.Get(key) is not null),
        "33rd group evicts only the oldest created group");
    catalog.Add(keys[33], Stamp(), [Item()]);
    Check(catalog.Get(keys[1]) is null && keys.Count(key => catalog.Get(key) is not null) == 32,
        "continued insertion keeps exactly 32 groups");
}

static void Clear()
{
    var catalog = new InventoryFindCatalog();
    var oldKeys = Enumerable.Range(0, 32).Select(_ => Guid.NewGuid()).ToArray();
    foreach (var key in oldKeys) catalog.Add(key, Stamp(), [Item()]);
    catalog.Clear();
    catalog.Clear();
    Check(oldKeys.All(key => catalog.Get(key) is null), "clear removes every old action handle and is idempotent");
    var freshKeys = Enumerable.Range(0, 32).Select(_ => Guid.NewGuid()).ToArray();
    foreach (var key in freshKeys) catalog.Add(key, Stamp(), [Item()]);
    Check(freshKeys.All(key => catalog.Get(key) is not null), "cleared eviction queue does not remove fresh groups early");
    catalog.Add(Guid.NewGuid(), Stamp(), [Item()]);
    Check(catalog.Get(freshKeys[0]) is null && freshKeys.Skip(1).All(key => catalog.Get(key) is not null),
        "eviction resumes with the first fresh group");
}

static void HistoryBoundary()
{
    var key = Guid.NewGuid();
    var shopKey = Guid.NewGuid();
    var history = new ChatHistory();
    history.Add("Locate a synthetic card", "Synthetic answer", [], "Synthetic saved context", true,
        inventoryFindKey: key, shopFindKey: shopKey);
    Check(history.View[0].InventoryFindKey == key && history.Export()[0].InventoryFindKey == key,
        "live view and in-memory clones retain their session handle");
    var restored = new ChatHistory();
    restored.Restore(history.Export());
    Check(restored.View[0].InventoryFindKey == key, "in-memory clone/restore retains the session handle");
    Check(restored.View[0].ShopFindKey == shopKey && history.View[0].ShopFindKey == shopKey,
        "shop handle follows the originating live turn and managed clones");
    AssertNoHandle(JsonSerializer.Serialize(history.View), key);
    AssertNoHandle(JsonSerializer.Serialize(history.Export()), key);
    AssertNoHandle(JsonSerializer.Serialize(history.Context()), key);
    AssertNoHandle(JsonSerializer.Serialize(history.View), shopKey);
    AssertNoHandle(JsonSerializer.Serialize(history.Export()), shopKey);
    AssertNoHandle(JsonSerializer.Serialize(history.Context()), shopKey);
    var lookup = ChatHistoryLookup.Query(history.Export(), JsonSerializer.SerializeToElement(new { query = "", offset = 0, limit = 5 }));
    Check(lookup.rows.Length == 1 && lookup.rows[0].answer == "Synthetic answer", "history query still returns ordinary content");
    AssertNoHandle(JsonSerializer.Serialize(lookup), key);
    var read = ChatHistoryLookup.Query(history.Export(), JsonSerializer.SerializeToElement(new
        { query = "", offset = 0, limit = 1, index = 1, field = "answer" }));
    AssertNoHandle(JsonSerializer.Serialize(read), key);
    var serialized = JsonSerializer.SerializeToNode(history.Export())!.AsArray();
    serialized[0]![nameof(ChatTurn.InventoryFindKey)] = key.ToString();
    serialized[0]![nameof(ChatTurn.ShopFindKey)] = shopKey.ToString();
    var deserialized = serialized.Deserialize<ChatTurn[]>()!;
    Check(deserialized[0].InventoryFindKey is null, "injected serialized handles cannot be revived");
    Check(deserialized[0].ShopFindKey is null, "injected serialized shop handles cannot be revived");
    Check(history.View[0].InventoryFindKey == key, "serializing and querying never clear the live handle");
}

static void StoreBoundary()
{
    using var folder = new TempFolder();
    var store = new ChatStore(folder.Path);
    var first = store.ActiveId;
    Check(File.Exists(System.IO.Path.Combine(folder.Path, first + ".json")), "initial synthetic chat saved: " + store.Error);
    var second = store.NewChat();
    var key = Guid.NewGuid();
    var shopKey = Guid.NewGuid();
    Check(store.AppendAnswer(first, "Synthetic owner question", "Synthetic owner answer", [], null, true, null, key, shopKey),
        "background answer accepted into originating chat");
    Check(store.ActiveId == second && store.ActiveHistory.View.Count == 0, "background handle does not leak to current chat");
    Check(store.Select(first) && store.ActiveHistory.View.Single().InventoryFindKey == key, "originating chat retains handle after save");
    Check(store.Select(second) && store.Select(first) && store.ActiveHistory.View.Single().InventoryFindKey == key,
        "switching chats preserves the session handle");
    var path = System.IO.Path.Combine(folder.Path, first + ".json");
    AssertNoHandle(File.ReadAllText(path), key);
    AssertNoHandle(File.ReadAllText(path), shopKey);
    var reloaded = new ChatStore(folder.Path);
    Check(reloaded.Select(first) && reloaded.ActiveHistory.View.Single().InventoryFindKey is null,
        "disk reload does not revive session-only actions");
    Check(reloaded.ActiveHistory.View.Single().ShopFindKey is null && store.ActiveHistory.View.Single().ShopFindKey == shopKey,
        "shop actions survive only in the current live chat");
    Check(reloaded.ActiveHistory.View.Single().Answer == "Synthetic owner answer", "saved conversation remains intact");
    Check(store.ActiveHistory.View.Single().InventoryFindKey == key, "independent reload cannot mutate current live session");
}

static void ExportBoundary()
{
    using var folder = new TempFolder();
    var store = new ChatStore(folder.Path);
    var key = Guid.NewGuid();
    Check(File.Exists(System.IO.Path.Combine(folder.Path, store.ActiveId + ".json")), "initial synthetic chat saved: " + store.Error);
    store.ActiveHistory.Add("Synthetic export question", "Synthetic export answer", [], null, true, inventoryFindKey: key);
    var exported = store.ExportActiveChat();
    Check(exported is not null && File.Exists(exported), "manual export written");
    var json = File.ReadAllText(exported!);
    AssertNoHandle(json, key);
    using var document = JsonDocument.Parse(json);
    Check(document.RootElement.GetProperty("Turns")[0].GetProperty("Answer").GetString() == "Synthetic export answer",
        "export retains answer content");
    Check(store.ActiveHistory.View.Single().InventoryFindKey == key, "manual export does not disable live actions");
}

static void RecoveryBoundary()
{
    using var folder = new TempFolder();
    var store = new ChatStore(folder.Path);
    var key = Guid.NewGuid();
    var id = store.ActiveId;
    var path = System.IO.Path.Combine(folder.Path, id + ".json");
    using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        Check(store.AppendAnswer(id, "Synthetic recovery question", "Synthetic recovery answer", [], null, true, null, key),
            "failed persistence still accepts completed answer into memory");
        Check(store.StorageInfo.UnsavedChats == 1 && store.ActiveHistory.View.Single().InventoryFindKey == key,
            "save failure retains the session handle and warns about unsaved work");
        var recovery = System.IO.Path.Combine(folder.Path, "exports", id + "-recovery.json");
        Check(File.Exists(recovery), "recovery export written");
        AssertNoHandle(File.ReadAllText(recovery), key);
    }
    Check(store.SaveActive() && store.ActiveHistory.View.Single().InventoryFindKey == key,
        "successful persistence retry does not clear the session handle");
    AssertNoHandle(File.ReadAllText(path), key);
}

static void AssertNoHandle(string json, Guid key)
{
    Check(!json.Contains(nameof(ChatTurn.InventoryFindKey), StringComparison.OrdinalIgnoreCase) &&
        !json.Contains(nameof(ChatTurn.ShopFindKey), StringComparison.OrdinalIgnoreCase) &&
        !json.Contains(key.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
        !json.Contains(key.ToString("N"), StringComparison.OrdinalIgnoreCase), "session finder handle leaked into JSON");
}

sealed class TempFolder : IDisposable
{
    private readonly string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory,
        "../../../../../.test-tmp")).TrimEnd(
        System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
    internal string Path { get; }
    internal TempFolder()
    {
        this.Path = System.IO.Path.Combine(this.root, "CompAI-InventoryFind-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.Path);
    }
    public void Dispose()
    {
        var target = System.IO.Path.GetFullPath(this.Path);
        if (!target.StartsWith(this.root, StringComparison.OrdinalIgnoreCase) ||
            !System.IO.Path.GetFileName(target).StartsWith("CompAI-InventoryFind-", StringComparison.Ordinal))
            throw new InvalidOperationException("Temporary fixture cleanup escaped its root.");
        Directory.Delete(target, recursive: true);
    }
}

namespace CompAI
{
    internal readonly record struct QuestionSource(string Title, string Url, bool IsBundled = false);
}
