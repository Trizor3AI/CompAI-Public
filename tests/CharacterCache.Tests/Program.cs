using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using CompAI;

var tests = new (string, Func<Task>)[]
{
    ("menu schema 4 preserves observations per character and migrates older caches", MenuCacheChecks.RoundTrip),
    ("menu retention expires shops without dropping glamour and rejects malformed records", MenuCacheChecks.Retention),
    ("restart preserves inventory, quest journal text, Unicode and original timestamps", RoundTrip),
    ("progression schema 3 saves observed pages with character isolation and category clear", ProgressionRoundTrip),
    ("progression legacy schemas load empty and malformed pages cannot replace good data", ProgressionMigrationAndInvalid),
    ("legacy and automatically decoded pedigree provenance survives current cache restart", PedigreeProvenance),
    ("storage schema 2 round trips per character and clear preserves unrelated data", StorageRoundTrip),
    ("personal crystals and key item caches retain namespaces across restart", PersonalExtraRoundTrip),
    ("armoury slots and materia survive per-character reload without mixing identities", ArmouryRoundTrip),
    ("storage legacy schema 1 loads without grants and upgrades only on save", StorageLegacy),
    ("storage malformed rows and duplicate identities reject without rewriting", StorageInvalid),
    ("different ContentIds stay isolated and offline selection survives restart", IdentityAndSelection),
    ("clear wins over queued writes and stale revisions cannot resurrect data", ClearOrdering),
    ("corrupt, oversized, mismatched and unsupported files are rejected and preserved", InvalidFiles),
    ("failed saves preserve the last good file and report an error", FailedSave),
    ("flush and unload retry failed writes once without a new revision", RetryFailedWrites),
    ("queued immutable pedigree values cannot change after offer", ImmutablePedigree),
    ("parallel producers and readers preserve revision ordering", ConcurrentOffers),
    ("load states distinguish missing, corrupt, inaccessible and unsupported files", LoadStates),
    ("a temporarily locked login cache is restored before ordinary updates can rotate its backup", LockedStartupRestore),
    ("replacement keeps bounded recovery copies and never restores cleared values", BackupAndClear),
    ("unsupported schemas block writes even without a prior load", UnsupportedReplacement),
    ("read errors clear independently and index write failures retry", IndependentRecovery),
    ("backup failures leave the primary and previous recovery copy intact", BlockedBackup),
};
var failed = 0;
foreach (var (name, test) in tests.Where(test => args.Length == 0 ||
    args.Any(filter => test.Item1.Contains(filter, StringComparison.OrdinalIgnoreCase))))
{
    try { await test().WaitAsync(TimeSpan.FromSeconds(30)); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
return failed == 0 ? 0 : 1;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static async Task<CharacterCacheData?> LoadData(CharacterCacheStore store, ulong id) => (await store.LoadAsync(id)).Data;
static async Task<CharacterCacheData?> LoadLastData(CharacterCacheStore store) => (await store.LoadLastAsync()).Data;

static CharacterCacheData Sample(ulong id = 123, long revision = 1)
{
    var captured = new DateTimeOffset(2026, 10, 3, 10, 20, 30, TimeSpan.Zero);
    var pedigree = new ObservedPedigree("ItemDetail", "user_verified_tooltip", captured.AddMinutes(-2),
        "covering", 4, "F", [4, 3, 2, 1, 4], [1, 2, 3, 4, 1]);
    var inventory = new CachedInventorySnapshot(captured,
    [
        new("Inventory1", 35, null,
        [
            new(0, 1, 1, "Potion 東京 șț é 🐦", 3, true),
            new(1, 10, 10, "Covering permission", 1, false, CachedPedigree.From(pedigree)),
            new(2, 55, 55, null, 4, false),
        ]),
        new("Inventory2", 35, null, []),
        new("Inventory3", null, "Container not loaded", []),
        new("Inventory4", 35, null, []),
    ]);
    var observation = new QuestObservation(0x10001, 1, "A journey — 新しい冒険", true, true, false,
        3, 5, 19, [0, 1, 127, 255], "Partial coverage") { JournalText = "Speak to Élodie.\n東京へ向かう。 🐦" };
    var quests = new QuestSnapshot(captured, 2.25, "test build", "readiness unknown", "partial coverage",
        "Catalog note", 2, 1, 30, [observation,
            new(null, 9000, null, null, false, null, null, null, null, [], "Unavailable")]);
    return new(id, "Saved character", revision, captured.AddSeconds(1), inventory, CachedQuestSnapshot.From(quests));
}

static ProgressionMenuCapture SavedPage(string category, int page = 0) => new(category, page,
    DateTimeOffset.Parse("2026-10-20T12:00:00Z"), category == "shared_fates"
        ? [new(813, "Synthetic Zone", "observed", Rank: 3, Progress: 0, Required: 0)]
        : [new(1, "Synthetic Member", "observed", Level: 71, Experience: 3, ExperienceRequired: 100)]);

static async Task ProgressionRoundTrip()
{
    using var folder = new TempFolder();
    var original = Sample() with { Progression = [SavedPage("shared_fates"), SavedPage("trust")] };
    using (var store = new CharacterCacheStore(folder.Path))
    {
        Check(store.Offer(original), "progression save accepted");
        Check(store.Offer(Sample(456) with { Progression = [SavedPage("trust", 1)] }), "other character separate");
        await store.FlushAsync();
        Check(store.Error is null, "progression persisted: " + store.Error);
    }
    using (var restarted = new CharacterCacheStore(folder.Path))
    {
        var restored = (await LoadData(restarted, 123))!;
        Check(JsonSerializer.Serialize(restored) == JsonSerializer.Serialize(original), "all page values and timestamps survive restart");
        Check((await LoadData(restarted, 456))!.Progression.Single().PageIndex == 1, "different character never gets first pages");
        Check((await LoadData(restarted, 789)) is null, "missing character remains unknown");
        Check(restarted.Offer(restored with { Revision = 2, Progression = [restored.Progression[1]] }), "clear only FATEs accepted");
        Check(!restarted.Offer(original), "stale revision cannot undo clear");
    }
    using var cleared = new CharacterCacheStore(folder.Path);
    var saved = (await LoadData(cleared, 123))!;
    Check(saved.Progression.Single().Category == "trust" && saved.Inventory is not null && saved.Quests is not null,
        "category clear preserves unrelated pages and existing context after restart");
}

static async Task ProgressionMigrationAndInvalid()
{
    foreach (var schema in new[] { 1, 2 })
    {
        using var folder = new TempFolder();
        var legacy = JsonSerializer.SerializeToNode(Sample() with { SchemaVersion = schema })!.AsObject();
        legacy.Remove("Progression");
        var bytes = legacy.ToJsonString();
        var path = System.IO.Path.Combine(folder.Path, "000000000000007B.json");
        await File.WriteAllTextAsync(path, bytes);
        using var store = new CharacterCacheStore(folder.Path);
        var restored = (await LoadData(store, 123))!;
        Check(restored.SchemaVersion == schema && restored.Progression.IsEmpty, "legacy adds no invented progression");
        Check(await File.ReadAllTextAsync(path) == bytes, "read-only migration keeps original file");
        Check(store.Offer(restored with { SchemaVersion = 3, Revision = 2, Progression = [SavedPage("shared_fates")] }), "new observation upgrades envelope");
        await store.FlushAsync();
        Check((await LoadData(store, 123))!.Progression.Length == 1 && store.Error is null, "upgraded observation readable");
        var goodBytes = await File.ReadAllTextAsync(path);
        Check(store.Offer(restored with { SchemaVersion = 3, Revision = 3,
            Progression = [SavedPage("shared_fates") with { PageIndex = 3 }] }), "invalid write validation stays off caller thread");
        await store.FlushAsync();
        Check(store.Error is not null && await File.ReadAllTextAsync(path) == goodBytes, "invalid page cannot replace known-good saved context");
    }
}

static async Task RoundTrip()
{
    using var folder = new TempFolder();
    var original = Sample();
    using (var store = new CharacterCacheStore(folder.Path))
    {
        Check(store.Offer(original), "first offer accepted");
        // Dispose must drain even when the caller does not explicitly flush.
    }
    using var restarted = new CharacterCacheStore(folder.Path);
    var loaded = await LoadLastData(restarted);
    Check(loaded is not null, "offline cache restored");
    Check(JsonSerializer.Serialize(loaded) == JsonSerializer.Serialize(original), "every persisted DTO property round trips");
    var quest = loaded!.Quests!.ToSnapshot().Quests[0];
    Check(quest.JournalText == original.Quests!.Quests[0].JournalText && quest.Name!.Contains("冒険"), "journal and Unicode preserved");
    Check(loaded.Inventory!.CapturedAtUtc == original.Inventory!.CapturedAtUtc &&
        loaded.Quests.CapturedAtUtc == original.Quests.CapturedAtUtc, "restore does not refresh capture time");
    Check(loaded.Inventory.Bags[1].SlotCount == 35 && loaded.Inventory.Bags[1].Items.Length == 0 &&
        loaded.Inventory.Bags[2].SlotCount is null && loaded.Inventory.Bags[2].UnavailableReason == "Container not loaded",
        "empty and unavailable remain distinct");
    Check(restarted.Error is null && !folder.Files("*.tmp").Any(), "successful writes clean temporary files");
}

static StorageContainerCapture Stored(string category = "retainers", string sourceKey = "retainer-a") =>
    new(category, sourceKey, "Synthetic storage ș", "Synthetic container", 20,
        [new(3, 5121, 5121, "Synthetic ore", 7, false, category == "retainers" ? 582u : null)],
        new DateTimeOffset(2026, 10, 3, 10, 20, 30, TimeSpan.Zero));

static async Task ArmouryRoundTrip()
{
    using var folder = new TempFolder();
    var materia = Enumerable.Range(0, 5).Select(slot => new StorageMateria(slot, 0, 0, null, null, true)).ToArray();
    materia[2] = new(2, 27, 9, 33931, "Synthetic materia", false);
    var armoury = Stored("armoury", "personal") with { Container = "ArmoryMainHand", Capacity = 50,
        Items = [new(3, 1005121, 5121, "Synthetic weapon", 1, true, Materia: materia)] };
    var sample = Sample() with { Storage = [armoury] };
    using (var store = new CharacterCacheStore(folder.Path))
    {
        Check(store.Offer(sample) && store.Offer(Sample(id: 456)), "independent character writes accepted");
    }
    using var restarted = new CharacterCacheStore(folder.Path);
    var loaded = await LoadData(restarted, 123);
    Check(loaded is not null && loaded.Storage.Length == 1 &&
        StorageSnapshot.SameItems(loaded.Storage[0].Items, armoury.Items), "HQ and materia values survived restart");
    Check((await LoadData(restarted, 456))!.Storage.IsEmpty, "another character never inherits Armoury slots");
    Check(restarted.Offer(sample with { Revision = 2, Storage = [] }), "explicit clear queued");
    await restarted.FlushAsync();
    Check((await LoadData(restarted, 123))!.Storage.IsEmpty, "clear removes saved Armoury");
    var invalid = sample with { Revision = 3, Storage = [armoury with { Items = [armoury.Items[0] with { Materia = [] }] }] };
    restarted.Offer(invalid);
    await restarted.FlushAsync();
    Check((await LoadData(restarted, 123))!.Storage.IsEmpty && restarted.Error is not null,
        "invalid materia snapshot cannot overwrite verified cache");
}

static async Task PersonalExtraRoundTrip()
{
    using var folder = new TempFolder();
    var sample = Sample() with { Storage = [Stored("crystals", "personal") with {
        Container = "Crystals", Capacity = 18, Items = [new(0, 1, 1, "Fire Shard", 9999, false)] },
        Stored("key_items", "personal") with { Container = "KeyItems", Capacity = 105,
            Items = [new(3, 2000001, 2000001, "Quest letter", 1, false)] }] };
    using (var store = new CharacterCacheStore(folder.Path)) Check(store.Offer(sample), "Personal extras accepted.");
    using var restarted = new CharacterCacheStore(folder.Path);
    var loaded = await LoadData(restarted, sample.ContentId);
    Check(loaded is not null && JsonSerializer.Serialize(loaded.Storage) == JsonSerializer.Serialize(sample.Storage),
        "Personal extra namespaces, quantities, original timestamps and IDs survive restart.");
}

static async Task StorageRoundTrip()
{
    using var folder = new TempFolder();
    var first = Sample(123) with { Storage = [Stored(), Stored("saddlebag", "chocobo"),
        Stored("housing", "house-a"), Stored("fc_chest", "company-a")] };
    var second = Sample(456) with { Storage = [Stored("retainers", "retainer-b") with { Items = [] }] };
    using (var store = new CharacterCacheStore(folder.Path))
    {
        Check(store.Offer(first) && store.Offer(second), "both character envelopes queued");
        await store.FlushAsync();
        Check(store.Error is null, "storage write must finish: " + store.Error);
    }
    using (var store = new CharacterCacheStore(folder.Path))
    {
        var loaded = await LoadData(store, 123);
        Check(loaded?.SchemaVersion == 4 && JsonSerializer.Serialize(loaded) == JsonSerializer.Serialize(first),
            "all storage categories, prices, identity and observation timestamps round trip: " + store.Error + "; loaded schema=" + loaded?.SchemaVersion);
        var other = await LoadData(store, 456);
        Check(other?.Storage.Length == 1 && other.Storage[0].SourceKey == "retainer-b" && other.Storage[0].Items.Length == 0,
            "empty observed storage is retained for its owner without mixing character rows");
        Check(await LoadData(store, 789) is null, "missing character never inherits someone else's storage");
        Check(store.Offer(first with { Revision = 2, Storage = [] }), "clear storage only");
        await store.FlushAsync();
        Check(!store.Offer(first), "stale storage capture cannot resurrect cleared rows");
    }
    using var restarted = new CharacterCacheStore(folder.Path);
    var cleared = await LoadData(restarted, 123);
    Check(cleared?.Storage.Length == 0 && cleared.Inventory is not null && cleared.Quests is not null,
        "storage clear survives restart while preserving inventory and journal");
    Check((await LoadData(restarted, 456))?.Storage.Length == 1, "clear does not affect another character");
}

static async Task StorageLegacy()
{
    using var folder = new TempFolder();
    var original = Sample() with { SchemaVersion = 1 };
    var node = JsonSerializer.SerializeToNode(original)!.AsObject();
    node.Remove("Storage");
    var legacy = node.ToJsonString();
    var path = System.IO.Path.Combine(folder.Path, "000000000000007B.json");
    await File.WriteAllTextAsync(path, legacy);
    using var store = new CharacterCacheStore(folder.Path);
    var restored = await LoadData(store, 123);
    Check(restored?.SchemaVersion == 1 && restored.Storage.Length == 0 && restored.Inventory is not null && restored.Quests is not null,
        "legacy file without Storage restores existing data and no invented observations");
    Check(await File.ReadAllTextAsync(path) == legacy, "read-only legacy migration does not rewrite disk");
    Check(store.Offer(restored! with { SchemaVersion = 2, Revision = 2, Storage = [Stored()] }), "explicit new capture upgrades schema");
    await store.FlushAsync();
    Check((await LoadData(store, 123))?.Storage.Length == 1 && store.Error is null, "upgraded storage is readable: " + store.Error);
}

static async Task StorageInvalid()
{
    using var folder = new TempFolder();
    var path = System.IO.Path.Combine(folder.Path, "000000000000007B.json");
    var row = Stored();
    var item = row.Items[0];
    var malformed = new[] {
        Sample() with { Storage = [row, row] },
        Sample() with { Storage = [row with { Items = [item, item] }] },
        Sample() with { Storage = [row with { Items = [item with { Slot = 20 }] }] },
        Sample() with { Storage = [row with { Items = [item with { Quantity = 0 }] }] },
        Sample() with { Storage = [row with { Items = [item with { UnitPriceGil = 0 }] }] },
        Sample() with { Storage = [row with { Category = "saddlebag" }] },
        Sample() with { Storage = [row with { Category = "unknown" }] },
        Sample() with { Storage = [row with { SourceKey = "" }] },
        Sample() with { Storage = [row with { Items = null! }] },
        Sample() with { Storage = [row with { CapturedAtUtc = DateTimeOffset.UtcNow.AddDays(1) }] },
        Sample() with { SchemaVersion = 1, Storage = [row] },
    };
    foreach (var envelope in malformed)
    {
        var json = JsonSerializer.Serialize(envelope);
        await File.WriteAllTextAsync(path, json);
        using var store = new CharacterCacheStore(folder.Path);
        Check((await store.LoadAsync(123)).Status == CharacterCacheLoadStatus.Unreadable && store.Error is not null,
            "malformed storage must not become queryable cached data");
        Check(await File.ReadAllTextAsync(path) == json, "bad data remains available for recovery");
    }
}

static async Task IdentityAndSelection()
{
    using var folder = new TempFolder();
    using (var store = new CharacterCacheStore(folder.Path))
    {
        Check(store.Offer(Sample(1)), "save first");
        Check(store.Offer(Sample(ulong.MaxValue) with { Inventory = null }), "save second");
        await store.FlushAsync();
        var last = await LoadLastData(store);
        Check(last?.ContentId == ulong.MaxValue && last.Inventory is null, "last offered character selected");
        var untouched = await LoadData(store, 1);
        Check(untouched?.Inventory is not null, "second character did not overwrite first");
        Check((await LoadLastData(store))?.ContentId == ulong.MaxValue, "load alone does not change selection");
        store.Select(1);
        store.Select(0);
    }
    using var restarted = new CharacterCacheStore(folder.Path);
    Check((await LoadLastData(restarted))?.ContentId == 1, "explicit selection persists without a new capture");
    Check(await LoadData(restarted, 2) is null && await LoadData(restarted, 0) is null, "unknown/zero identity never falls back to another character");
    Check(folder.Files("*.json").Select(System.IO.Path.GetFileName).Order().SequenceEqual(
        new[] { "0000000000000001.json", "FFFFFFFFFFFFFFFF.json", "last-character.json" }.Order()), "fixed identity-only names");
}

static async Task PedigreeProvenance()
{
    using var folder = new TempFolder();
    var original = Sample();
    var inventory = original.Inventory!;
    var bag = inventory.Bags[0];
    var legacy = bag.Items[1];
    var automatic = legacy with { Slot = 3, Pedigree = legacy.Pedigree! with { Status = "decoded_tooltip" } };
    original = original with { Inventory = inventory with {
        Bags = inventory.Bags.SetItem(0, bag with { Items = bag.Items.Add(automatic) }) } };
    using (var store = new CharacterCacheStore(folder.Path))
    {
        Check(store.Offer(original), "mixed provenance cache write accepted");
        await store.FlushAsync();
        Check(store.Error is null, "pedigree write must finish: " + store.Error);
    }
    using var restarted = new CharacterCacheStore(folder.Path);
    var loaded = await LoadLastData(restarted);
    Check(loaded?.SchemaVersion == 4 && JsonSerializer.Serialize(loaded) == JsonSerializer.Serialize(original),
        "current cache must preserve complete old and new records without relabeling: " + restarted.Error + "; loaded schema=" + loaded?.SchemaVersion);
    var loadedItems = loaded!.Inventory!.Bags[0].Items;
    Check(loadedItems[1].Pedigree!.ToPedigree().Status == "user_verified_tooltip" &&
        loadedItems[^1].Pedigree!.ToPedigree().Status == "decoded_tooltip",
        "restored runtime rows must retain exact provenance");
}

static async Task ClearOrdering()
{
    using var folder = new TempFolder();
    var original = Sample();
    using (var store = new CharacterCacheStore(folder.Path))
    {
        for (var revision = 1; revision <= 200; revision++)
            Check(store.Offer(original with { Revision = revision }), "ordered queued update accepted");
        var clearedInventory = original with { Revision = 201, Inventory = null };
        Check(store.Offer(clearedInventory), "inventory clear accepted");
        Check(!store.Offer(original with { Revision = 200 }), "stale snapshot rejected");
        await store.FlushAsync();
        var loaded = await LoadData(store, original.ContentId);
        Check(loaded?.Inventory is null && loaded?.Quests is not null && loaded.Revision == 201, "inventory clear preserves quests");
        Check(store.Offer(clearedInventory with { Revision = 202, Quests = null }), "quest clear accepted");
    }
    using var restarted = new CharacterCacheStore(folder.Path);
    var cleared = await LoadLastData(restarted);
    Check(cleared is { Inventory: null, Quests: null, Revision: 202 }, "newer clears survive unload/restart");
    Check(!restarted.Offer(original with { Revision = 202 }), "loaded revision seeds old-write rejection");
}

static async Task InvalidFiles()
{
    using var folder = new TempFolder();
    var path = System.IO.Path.Combine(folder.Path, "000000000000007B.json");
    var invalid = new byte[] { 0x7B, 0xFF, 0x7D };
    await File.WriteAllBytesAsync(path, invalid);
    using (var store = new CharacterCacheStore(folder.Path))
    {
        Check(await LoadData(store, 123) is null && store.Error is not null, "invalid UTF-8/JSON rejected");
        Check(File.ReadAllBytes(path).SequenceEqual(invalid), "corrupt data preserved");
    }
    using (var large = new FileStream(path, FileMode.Create)) large.SetLength(CharacterCacheStore.MaxFileBytes + 1L);
    using (var store = new CharacterCacheStore(folder.Path))
        Check(await LoadData(store, 123) is null && store.Error is not null, "oversized file rejected before deserialization");
    var original = Sample();
    var bags = original.Inventory!.Bags;
    foreach (var value in new[] { Sample(456), Sample() with { SchemaVersion = 99 },
        original with { Inventory = original.Inventory with { Bags = bags.SetItem(0, bags[0] with { Bag = "RetainerPage1" }) } },
        original with { Inventory = original.Inventory with { Bags = bags.SetItem(0, bags[0] with { SlotCount = 100 }) } },
        original with { Inventory = original.Inventory with { Bags = bags.SetItem(0, bags[0] with { Items = [bags[0].Items[0], bags[0].Items[0]] }) } },
        original with { Inventory = original.Inventory with { Bags = bags.SetItem(0, bags[0] with { Items = [bags[0].Items[0] with { Slot = 35 }] }) } },
        original with { Inventory = original.Inventory with { Bags = bags.SetItem(0, bags[1]) } },
    })
    {
        var json = JsonSerializer.Serialize(value);
        await File.WriteAllTextAsync(path, json, new UTF8Encoding(false));
        using var store = new CharacterCacheStore(folder.Path);
        Check(await LoadData(store, 123) is null && store.Error is not null, "invalid identity/schema/bag/slot rejected");
        Check(await File.ReadAllTextAsync(path) == json, "rejected file not rewritten or deleted");
    }
    await File.WriteAllTextAsync(path, "{\"ContentId\":123,\"CharacterLabel\":\"Saved character\",\"Revision\":1,\"SavedAtUtc\":\"2026-10-03T10:20:30Z\",\"Inventory\":{\"CapturedAtUtc\":\"2026-10-03T10:20:30Z\"}}");
    using (var store = new CharacterCacheStore(folder.Path))
        Check(await LoadData(store, 123) is null && store.Error is not null, "missing collection rejected");
    await File.WriteAllTextAsync(System.IO.Path.Combine(folder.Path, "last-character.json"), "{\"SchemaVersion\":1,\"ContentId\":0}");
    using var indexStore = new CharacterCacheStore(folder.Path);
    Check(await LoadLastData(indexStore) is null && indexStore.Error is not null, "invalid selection never guesses a character");
}

static async Task FailedSave()
{
    using var folder = new TempFolder();
    using var store = new CharacterCacheStore(folder.Path);
    Check(store.Offer(Sample()), "initial save");
    await store.FlushAsync();
    var first = Sample();
    var invalid = first with { Revision = 2, CharacterLabel = new string('x', 300) };
    Check(store.Offer(invalid), "write validation runs off caller thread");
    await store.FlushAsync();
    Check(store.Error is not null && (await LoadData(store, 123))?.Revision == 1,
        "failed newer write retains old file and load cannot spin forever");
    var largeQuests = first.Quests! with { Quests = Enumerable.Repeat(first.Quests.Quests[0] with
        { JournalText = new string('x', 32768) }, 600).ToImmutableArray() };
    Check(store.Offer(first with { Revision = 3, Quests = largeQuests }), "offer oversized serialized snapshot");
    await store.FlushAsync();
    Check((await LoadData(store, 123))?.Revision == 3, "accepted oversized snapshot remains in memory with save warning");
    using (var restarted = new CharacterCacheStore(folder.Path))
        Check((await LoadData(restarted, 123))?.Revision == 1, "oversized output preserves last good disk file");
    var savedPath = System.IO.Path.Combine(folder.Path, "000000000000007B.json");
    using (var held = new FileStream(savedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        Check(store.Offer(first with { Revision = 4, Inventory = null, Quests = null }), "offer clear while destination cannot be replaced");
        await store.FlushAsync();
        Check(await LoadData(store, 123) is { Revision: 4, Inventory: null, Quests: null },
            "failed clear stays latest in memory and cannot resurrect older disk data");
    }
    Check(store.Offer(first with { Revision = 5, Inventory = null, Quests = null }), "newer revision retries persistence after failure");
    await store.FlushAsync();
    Check(store.Error is null, "successful newer save clears recovered validation and storage errors");
    using (var restarted = new CharacterCacheStore(folder.Path))
        Check(await LoadData(restarted, 123) is { Revision: 5, Inventory: null, Quests: null }, "recovered storage persists latest clear");
    using var blocked = new CharacterCacheStore(System.IO.Path.Combine(folder.Path, "as-file"));
    await File.WriteAllTextAsync(System.IO.Path.Combine(folder.Path, "as-file"), "keep");
    Check(blocked.Offer(Sample()), "unavailable storage does not block game caller");
    await blocked.FlushAsync();
    Check(blocked.Error is not null, "write failure is visible");
}

static async Task RetryFailedWrites()
{
    using var folder = new TempFolder();
    using var store = new CharacterCacheStore(folder.Path);
    var original = Sample();
    Check(store.Offer(original), "create original disk snapshot");
    await store.FlushAsync();
    var savedPath = System.IO.Path.Combine(folder.Path, "000000000000007B.json");
    using (var held = new FileStream(savedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        Check(store.Offer(original with { Revision = 2, Inventory = null, Quests = null }), "queue blocked clear");
        store.Select(456);
        await store.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(store.Error is not null, "persistent failure returns and remains visible");
        using var index = JsonDocument.Parse(await File.ReadAllTextAsync(System.IO.Path.Combine(folder.Path, "last-character.json")));
        Check(index.RootElement.GetProperty("ContentId").GetUInt64() == 456 && store.Error is not null,
            "successful index write cannot hide failed character write");
        Check(await LoadData(store, 123) is { Revision: 2, Inventory: null, Quests: null }, "failed clear stays in memory");
        using var restarted = new CharacterCacheStore(folder.Path);
        Check((await LoadData(restarted, 123))?.Revision == 1, "blocked write leaves previous durable snapshot");
    }
    // There is no Offer or revision increment after releasing the lock.
    await store.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
    Check(store.Error is null, "successful retained-write retry clears its error");
    using (var restarted = new CharacterCacheStore(folder.Path))
        Check(await LoadData(restarted, 123) is { Revision: 2, Inventory: null, Quests: null }, "explicit flush persists same failed revision");
    using (var held = new FileStream(savedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        Check(store.Offer(original with { Revision = 3 }), "queue another blocked update");
        await store.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }
    await store.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    using (var restarted = new CharacterCacheStore(folder.Path))
        Check((await LoadData(restarted, 123))?.Revision == 3, "unload retries retained failure without another offer");
    Check(!store.Offer(original with { Revision = 4 }), "unloaded store rejects new updates");
    var blockedDirectory = System.IO.Path.Combine(folder.Path, "unavailable-directory");
    await File.WriteAllTextAsync(blockedDirectory, "keep");
    using var blocked = new CharacterCacheStore(blockedDirectory);
    Check(blocked.Offer(original), "queue write to unavailable destination");
    await blocked.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    Check(blocked.Error is not null, "persistent unload failure terminates and reports error");
}

static async Task ImmutablePedigree()
{
    using var folder = new TempFolder();
    var stars = new[] { 1, 2, 3, 4, 1 };
    var observed = DateTimeOffset.UtcNow;
    var pedigree = new ObservedPedigree("ItemDetail", "user_verified_tooltip", observed, "covering", 4, "F", stars, stars);
    var cached = CachedPedigree.From(pedigree);
    stars[0] = 4;
    var converted = cached.ToPedigree();
    converted.FatherStars[0] = 3;
    Check(cached.FatherStars[0] == 1 && cached.MotherStars[0] == 1, "mutable native-facing arrays are copied at both boundaries");
    var original = Sample();
    var bag = original.Inventory!.Bags[0];
    var data = original with { Inventory = original.Inventory with { Bags = original.Inventory.Bags.SetItem(0, bag with
        { Items = [bag.Items[1] with { Pedigree = cached }] }) } };
    using (var store = new CharacterCacheStore(folder.Path)) Check(store.Offer(data), "offer immutable pedigree");
    using var restarted = new CharacterCacheStore(folder.Path);
    Check((await LoadLastData(restarted))!.Inventory!.Bags[0].Items[0].Pedigree!.FatherStars[0] == 1,
        "saved pedigree contains original observation");
}

static async Task ConcurrentOffers()
{
    using var folder = new TempFolder();
    using (var store = new CharacterCacheStore(folder.Path))
    {
        var original = Sample();
        var tasks = Enumerable.Range(1, 100).Select(revision => Task.Run(async () =>
        {
            store.Offer(original with { Revision = revision });
            var saved = await LoadData(store, 123);
            Check(saved is null || saved.ContentId == 123, "concurrent read identity is stable");
        }));
        await Task.WhenAll(tasks);
        Check(store.Offer(original with { Revision = 101, Inventory = null, Quests = null }), "final clear accepted");
    }
    using var restarted = new CharacterCacheStore(folder.Path);
    Check(await LoadData(restarted, 123) is { Revision: 101, Inventory: null, Quests: null }, "highest final revision wins across concurrent work");
}

static async Task LoadStates()
{
    using var folder = new TempFolder();
    using var store = new CharacterCacheStore(folder.Path);
    var path = System.IO.Path.Combine(folder.Path, "000000000000007B.json");
    Check((await store.LoadAsync(123)).Status == CharacterCacheLoadStatus.Missing && store.Error is null,
        "absent file is not a failure");
    await File.WriteAllTextAsync(path, "{broken");
    Check((await store.LoadAsync(123)).Status == CharacterCacheLoadStatus.Unreadable && store.Error is not null,
        "corrupt file is distinct from absent file");
    // A future payload need not even deserialize into the current DTO.
    var future = "{\"SchemaVersion\":99,\"ContentId\":{\"new\":\"shape\"}}";
    await File.WriteAllTextAsync(path, future);
    Check((await store.LoadAsync(123)).Status == CharacterCacheLoadStatus.UnsupportedSchema &&
        store.Error!.Contains("unsupported schema 99"), "schema is checked before field deserialization");
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(Sample()));
    using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        Check((await store.LoadAsync(123)).Status == CharacterCacheLoadStatus.TemporarilyUnavailable,
            "inaccessible file is not mistaken for missing");
    Check((await store.LoadAsync(123)).Status == CharacterCacheLoadStatus.Loaded && store.Error is null,
        "successful reread clears that file's recovered read error");
    foreach (var malformedSchema in new[] { "{}", "{\"SchemaVersion\":\"1\"}", "{\"SchemaVersion\":null}" })
    {
        await File.WriteAllTextAsync(path, malformedSchema);
        Check((await store.LoadAsync(123)).Status == CharacterCacheLoadStatus.Unreadable,
            "missing or incorrectly typed schemas produce a bounded read failure");
    }
    File.Delete(path);
    Check((await store.LoadAsync(123)).Status == CharacterCacheLoadStatus.Missing && store.Error is null,
        "removing a damaged file resolves only its read error");
    var asFile = System.IO.Path.Combine(folder.Path, "not-a-directory");
    await File.WriteAllTextAsync(asFile, "keep");
    using var invalidDirectory = new CharacterCacheStore(asFile);
    Check((await invalidDirectory.LoadAsync(123)).Status == CharacterCacheLoadStatus.Unreadable,
        "invalid cache directory is not mistaken for absent data");
}

static async Task LockedStartupRestore()
{
    using var folder = new TempFolder();
    var path = Path.Combine(folder.Path, "000000000000007B.json");
    var baseline = Sample(revision: 90) with { Progression = [SavedPage("shared_fates")] };
    await using (var seed = new CharacterCacheStore(folder.Path)) Check(seed.Offer(baseline), "baseline saved");
    await using var store = new CharacterCacheStore(folder.Path);
    Task<CharacterCacheLoadResult?> recovery;
    using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
    {
        recovery = store.RestoreForSessionAsync(123, () => true);
        while (store.Error is null) await Task.Delay(10);
        Check((await store.LoadAsync(123)).Status == CharacterCacheLoadStatus.TemporarilyUnavailable,
            "startup sharing failure is retryable");
    }
    Check(!store.Offer(Sample(revision: 1)), "incomplete update refused until initial restore");
    var restored = (await recovery)!.Data!;
    Check(restored.Revision == 90 && restored.Progression.Length == 1, "original state and revision recovered");
    foreach (var revision in new long[] { 91, 92 })
    {
        Check(store.Offer(restored with { Revision = revision, Storage = [Stored()] }), "ordinary update accepted");
        await store.FlushAsync();
    }
    foreach (var file in new[] { path, path + ".bak" })
    {
        var contents = JsonSerializer.Deserialize<CharacterCacheData>(await File.ReadAllBytesAsync(file))!;
        Check(contents.Progression.Length == 1 && contents.Revision == (file == path ? 92 : 91),
            "unvisited page survives both writes and backup rotation");
    }
}

static async Task BackupAndClear()
{
    using var folder = new TempFolder();
    var path = System.IO.Path.Combine(folder.Path, "000000000000007B.json");
    using (var store = new CharacterCacheStore(folder.Path))
    {
        Check(store.Offer(Sample()), "initial backup fixture accepted");
        await store.FlushAsync();
        var original = await File.ReadAllBytesAsync(path);
        Check(store.Offer(Sample() with { Revision = 2, Inventory = null, Quests = null }), "clear accepted");
        await store.FlushAsync();
        Check((await File.ReadAllBytesAsync(path + ".bak")).SequenceEqual(original),
            "atomic replacement preserves exact previous bytes");
        using var restarted = new CharacterCacheStore(folder.Path);
        Check(await LoadData(restarted, 123) is { Revision: 2, Inventory: null, Quests: null },
            "backup containing old data does not override an explicit clear");
    }
    File.Delete(path);
    using (var restarted = new CharacterCacheStore(folder.Path))
        Check((await restarted.LoadAsync(123)).Status == CharacterCacheLoadStatus.Missing,
            "backup is never automatically restored after primary removal");
    var corrupt = Encoding.UTF8.GetBytes("{damaged original}");
    await File.WriteAllBytesAsync(path, corrupt);
    using (var store = new CharacterCacheStore(folder.Path))
    {
        Check((await store.LoadAsync(123)).Status == CharacterCacheLoadStatus.Unreadable, "damaged fixture detected");
        Check(store.Offer(Sample() with { Revision = 3 }), "replacement after corruption accepted");
        await store.FlushAsync();
        Check(store.Error is null && (await File.ReadAllBytesAsync(path + ".quarantine")).SequenceEqual(corrupt),
            "corrupt bytes preserved before successful replacement clears recovered error");
        Check(JsonSerializer.Deserialize<CharacterCacheData>(await File.ReadAllBytesAsync(path + ".bak"))!.Revision == 1,
            "quarantining corrupt data preserves the existing last-good backup");
        Check(store.Offer(Sample() with { Revision = 4 }), "later save accepted");
        await store.FlushAsync();
        Check(JsonSerializer.Deserialize<CharacterCacheData>(await File.ReadAllBytesAsync(path + ".bak"))!.Revision == 3,
            "backup rolls forward to the immediately previous good file");
    }
    Check(folder.Files("000000000000007B.json*").Length == 3 && !folder.Files("*.tmp").Any(),
        "one last-good and one quarantine copy remain and temporary files are removed");
}

static async Task UnsupportedReplacement()
{
    using var folder = new TempFolder();
    var path = System.IO.Path.Combine(folder.Path, "000000000000007B.json");
    var future = "{\"SchemaVersion\":99,\"ContentId\":{\"new\":\"shape\"}}";
    await File.WriteAllTextAsync(path, future);
    using (var store = new CharacterCacheStore(folder.Path))
    {
        Check(store.Offer(Sample() with { Inventory = null, Quests = null }), "new capture may stay in memory");
        await store.FlushAsync();
        Check(await File.ReadAllTextAsync(path) == future && !File.Exists(path + ".bak") && store.Error is not null,
            "write without prior load cannot replace or rename a future schema");
        Check(await LoadData(store, 123) is { Inventory: null, Quests: null }, "latest unsaved clear remains in memory");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(Sample()));
        await store.FlushAsync();
        Check(store.Error is null, "explicitly repaired storage allows retained write to recover");
    }
    await File.WriteAllTextAsync(path, "{\"SchemaVersion\":999999999999}");
    using (var store = new CharacterCacheStore(folder.Path))
    {
        Check((await store.LoadAsync(123)).Status == CharacterCacheLoadStatus.UnsupportedSchema,
            "out-of-range numeric schemas stay protected");
        Check(store.Offer(Sample()), "future fixture accepts in-memory capture");
        await store.FlushAsync();
        Check(await File.ReadAllTextAsync(path) == "{\"SchemaVersion\":999999999999}",
            "unrepresentable future schema cannot be mistaken for replaceable corruption");
    }
    using (var large = new FileStream(path, FileMode.Create)) large.SetLength(CharacterCacheStore.MaxFileBytes + 1L);
    using (var store = new CharacterCacheStore(folder.Path))
    {
        Check(store.Offer(Sample()), "oversized fixture accepts in-memory capture");
        await store.FlushAsync();
        Check(new FileInfo(path).Length == CharacterCacheStore.MaxFileBytes + 1L && store.Error is not null,
            "replacement is blocked when the full envelope cannot be inspected within its size bound");
    }
    var indexPath = System.IO.Path.Combine(folder.Path, "last-character.json");
    await File.WriteAllTextAsync(indexPath, "{\"SchemaVersion\":2,\"ContentId\":123}");
    using (var store = new CharacterCacheStore(folder.Path))
    {
        Check((await store.LoadLastAsync()).Status == CharacterCacheLoadStatus.UnsupportedSchema,
            "unsupported offline index has an explicit status");
        store.Select(456);
        await store.FlushAsync();
        Check(await File.ReadAllTextAsync(indexPath) == "{\"SchemaVersion\":2,\"ContentId\":123}" && store.Error is not null,
            "unsupported selection index is protected too");
    }
}

static async Task IndependentRecovery()
{
    using var folder = new TempFolder();
    using var store = new CharacterCacheStore(folder.Path);
    var first = System.IO.Path.Combine(folder.Path, "000000000000007B.json");
    var second = System.IO.Path.Combine(folder.Path, "00000000000001C8.json");
    await File.WriteAllTextAsync(first, "{bad");
    await File.WriteAllTextAsync(second, "{bad");
    await store.LoadAsync(123);
    await store.LoadAsync(456);
    await File.WriteAllTextAsync(first, JsonSerializer.Serialize(Sample()));
    await store.LoadAsync(123);
    Check(store.Error is not null, "one recovered read cannot hide another damaged file");
    await File.WriteAllTextAsync(second, JsonSerializer.Serialize(Sample(456)));
    await store.LoadAsync(456);
    Check(store.Error is null, "all recovered reads remove their own stale errors");
    store.Select(123);
    await store.FlushAsync();
    var indexPath = System.IO.Path.Combine(folder.Path, "last-character.json");
    using (var held = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        store.Select(456);
        await store.FlushAsync();
        Check(store.Error is not null, "failed selection update is visible");
        Check(store.Offer(Sample() with { Revision = 2 }), "unrelated character write accepted");
        await store.FlushAsync();
        Check(store.Error is not null, "successful character write does not hide unresolved index failure");
    }
    await store.FlushAsync();
    Check(store.Error is null, "selection write retries after access recovers without a new selection");
    using var index = JsonDocument.Parse(await File.ReadAllTextAsync(indexPath));
    Check(index.RootElement.GetProperty("ContentId").GetUInt64() == 123,
        "retry persists the latest selected character");
}

static async Task BlockedBackup()
{
    using var folder = new TempFolder();
    using var store = new CharacterCacheStore(folder.Path);
    var path = System.IO.Path.Combine(folder.Path, "000000000000007B.json");
    Check(store.Offer(Sample()), "initial save accepted");
    await store.FlushAsync();
    var original = await File.ReadAllBytesAsync(path);
    var recovery = Encoding.UTF8.GetBytes("previous recovery bytes");
    await File.WriteAllBytesAsync(path + ".bak", recovery);
    using (var held = new FileStream(path + ".bak", FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        Check(store.Offer(Sample() with { Revision = 2 }), "blocked backup update accepted in memory");
        await store.FlushAsync();
        Check(store.Error is not null && (await File.ReadAllBytesAsync(path)).SequenceEqual(original) &&
            (await File.ReadAllBytesAsync(path + ".bak")).SequenceEqual(recovery),
            "failed backup replacement preserves both existing copies");
    }
    await store.FlushAsync();
    Check(store.Error is null && (await File.ReadAllBytesAsync(path + ".bak")).SequenceEqual(original),
        "retry safely replaces the primary after backup access recovers");
}

sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ffxiv-character-cache-" + Guid.NewGuid().ToString("N"));
    public TempFolder() => Directory.CreateDirectory(this.Path);
    public string[] Files(string pattern) => Directory.GetFiles(this.Path, pattern);
    public void Dispose()
    {
        var resolved = System.IO.Path.GetFullPath(this.Path);
        var temporaryRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
        if (!resolved.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
            !System.IO.Path.GetFileName(resolved).StartsWith("ffxiv-character-cache-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected test directory");
        Directory.Delete(resolved, recursive: true);
    }
}
