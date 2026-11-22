using System.Runtime.Loader;
using System.Text.Json;
using CompAI;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

var libraryPath = Environment.GetEnvironmentVariable("DALAMUD_LIB_PATH") ??
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "addon", "Hooks", "dev");
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var path = Path.Combine(libraryPath, name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
Tests.Run();
if (args is ["--produce-fixtures", var path]) Tests.WriteProducerFixtures(path);

internal static class Tests
{
    private static int passed;
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-10-20T09:00:00Z");

    internal static void Run()
    {
        Test("pinned generated bounds and synthetic native minion flags", NativeMinionFlags);
        Test("mount row IDs stay separate from bounded unlock order", () =>
        {
            var calls = new List<uint>();
            var snapshot = CollectionReader.Describe("mounts", Time,
                [new(400, "High row valid order", 200), new(401, "Last supported order", 358),
                 new(402, "Unsupported high order", 359), new(403, "Negative order", -1),
                 new(uint.MaxValue, "Invalid row ID", 0)], id => { calls.Add(id); return id == 400; });
            Check(calls.SequenceEqual([400u, 401u]));
            Check(snapshot.Entries.Select(e => e.State).SequenceEqual(
                ["unlocked", "not_observed_unlocked", "unknown", "unknown", "unknown"]));
        });
        Test("minion row IDs use direct bounded index and ignore mount mapping", () =>
        {
            var calls = new List<uint>();
            var snapshot = CollectionReader.Describe("minions", Time,
                [new(1, "First", null), new(599, "Last supported", null), new(600, "Out of bounds", null),
                 new(2, "Invalid mapping", 4)], id => { calls.Add(id); return false; });
            Check(calls.SequenceEqual([1u, 599u]));
            Check(snapshot.Entries.Single(e => e.Id == 599).State == "not_observed_unlocked");
            Check(snapshot.Entries.Single(e => e.Id == 600).State == "unknown");
        });
        Test("fashion and orchestrion callbacks receive sheet row IDs inside pinned bounds", () =>
        {
            foreach (var (category, count) in new[] { ("fashion_accessories", CollectionReader.OrnamentUnlockBitCount),
                         ("orchestrion", CollectionReader.OrchestrionUnlockBitCount) })
            {
                var calls = new List<uint>();
                var snapshot = CollectionReader.Describe(category, Time,
                    [new(1, "First", null), new((uint)count - 1, "Last", null),
                     new((uint)count, "Unsupported", null), new(2, "Wrong mapping", 0)],
                    id => { calls.Add(id); return id == 1; });
                Check(calls.SequenceEqual([1u, (uint)count - 1]));
                Check(snapshot.Entries.Single(e => e.Id == 1).Unlocked == true);
                Check(snapshot.Entries.Single(e => e.Id == count - 1).State == "not_observed_unlocked");
                Check(snapshot.Entries.Single(e => e.Id == count).State == "unknown");
            }
        });
        Test("facewear groups variants but passes real Glasses IDs including style zero", () =>
        {
            var definitions = CollectionReader.GroupFacewear(
                [new(13, "Style one", null, 1), new(3, "Style zero", null, 0), new(1, "Style zero", null, 0),
                 new(14, "Style one", null, 1), new(733, "Last supported style", null, 61),
                 new(745, "Unsupported style", null, 62), new(746, "Missing mapping", null),
                 new(747, "Missing mapping again", null)]);
            Check(definitions.Select(r => r.Id).SequenceEqual([1u, 13u, 733u, 745u, 746u, 747u]));
            var calls = new List<uint>();
            var snapshot = CollectionReader.Describe("facewear", Time, definitions,
                id => { calls.Add(id); return id == 1; });
            Check(calls.SequenceEqual([1u, 13u, 733u]));
            Check(snapshot.Entries.Single(e => e.Id == 1).Unlocked == true);
            Check(snapshot.Entries.Single(e => e.Id == 733).State == "not_observed_unlocked");
            Check(snapshot.Entries.Count(e => e.State == "unknown") == 3);
            var bounds = CollectionReader.Describe("facewear", Time,
                [new(65536, "Too wide", null, 0), new(4097, "Outside catalog bound", null, 0),
                 new(2, "Wrong mapping", 0, 0)], _ => throw new Exception("Unexpected native call"));
            Check(bounds.Entries.All(e => e.State == "unknown"));
            Throws<InvalidOperationException>(() => CollectionReader.GroupFacewear(
                Enumerable.Range(1, 4097).Select(id => new CollectionDefinition((uint)id, "Too many", null, 0))));
        });
        Test("emotes use bounded sheet IDs rather than unlock-link indexes", () =>
        {
            var calls = new List<uint>();
            var snapshot = CollectionReader.Describe("emotes", Time,
                [new(333, "Emote RowId", null), new(4096, "Supported sheet bound", null),
                 new(4097, "Beyond sheet bound", null), new(65536, "Beyond ushort", null),
                 new(1, "Incorrect mount mapping", 0)],
                id => { calls.Add(id); return id == 333; });
            Check(calls.SequenceEqual([333u, 4096u]));
            Check(snapshot.Entries.Single(e => e.Id == 333).Unlocked == true);
            Check(snapshot.Entries.Count(e => e.State == "unknown") == 3);
        });
        Test("native negatives remain null and never verified locked", () =>
        {
            var rows = Small().Entries;
            Check(rows[0].Unlocked == true && rows[0].UnknownReason is null);
            Check(rows[1].Unlocked is null && rows[1].State == "not_observed_unlocked" && rows[1].UnknownReason is not null);
            Check(rows[2].Unlocked is null && rows[2].State == "unknown" && rows[2].UnknownReason is not null);
            Throws<ArgumentException>(() => new CollectionSnapshot("mounts", Time, true,
                [new(1, "Invalid false", "not_observed_unlocked", false, "Unverified")]));
        });
        Test("unloaded and malformed catalogs cannot call native methods", () =>
        {
            foreach (var rows in new CollectionDefinition[][] { [], [new(0, "Zero", 0)],
                         [new(1, "Same", 0), new(1, "Same again", 1)], [new(1, "", 0)],
                         [new(1, "Control\nname", 0)], [new(1, new string('a', 129), 0)] })
                Check(!CollectionReader.Describe("mounts", Time, rows, _ => throw new Exception("Unexpected native call")).Available);
            Check(!CollectionReader.Describe("mounts", Time, [new(1, "Valid", 0)],
                _ => throw new Exception("Unexpected native call"), loaded: false).Available);
            Check(!CollectionReader.Describe("mounts", Time,
                Enumerable.Range(1, 4097).Select(id => new CollectionDefinition((uint)id, "Too many", 0)),
                _ => throw new Exception("Unexpected native call")).Available);
        });
        Test("whole named catalog counts remain explicit through filtering", () =>
        {
            var lookup = new CollectionLookup(Small());
            var result = lookup.Query(Args("""{"category":"mounts","query":"SYNTHETIC alpha","ids":[1,2]}"""));
            Check(result.entries.Length == 1 && result.entries[0].id == 1 && result.matched_count == 1);
            Check(result.catalog_count == 3 && result.observed_unlocked_count == 1 &&
                result.not_observed_unlocked_count == 1 && result.unknown_count == 1);
            Check(result.readiness == "positive_observations_only");
            Check(lookup.Query(Args("""{"category":"mounts","ids":[999]}""")).matched_count == 0);
            Check(lookup.Query(Args("""{"category":"mounts","state":"not_observed_unlocked"}""")).entries.Single().id == 2);
            Check(lookup.Query(Args("""{"category":"mounts","state":"unknown"}""")).entries.Single().id == 3);
        });
        Test("stable ordered pages advance by actual returned row count", () =>
        {
            var lookup = new CollectionLookup(Large());
            var collected = new List<uint>();
            var offset = 0;
            while (true)
            {
                var result = lookup.Query(JsonSerializer.SerializeToElement(new { category = "minions", offset, limit = 17 }));
                Check(result.offset == offset && result.limit == 17 && result.matched_count == 4096);
                Check(result.entries.Length is > 0 and <= 17);
                collected.AddRange(result.entries.Select(row => row.id));
                Check(result.truncated == (result.next_offset is not null));
                if (result.next_offset is not { } next) break;
                Check(next == offset + result.entries.Length);
                offset = next;
            }
            Check(collected.SequenceEqual(Enumerable.Range(1, 4096).Select(id => (uint)id)));
            var exhausted = lookup.Query(Args("""{"category":"minions","offset":4096}"""));
            Check(exhausted.entries.Length == 0 && exhausted.next_offset is null && !exhausted.truncated);
        });
        Test("unavailable returns unknown counts and no retained state", () =>
        {
            Throws<ArgumentException>(() => new CollectionSnapshot("mounts", Time, false, Small().Entries, "Unloaded"));
            var result = new CollectionLookup(new("minions", Time, false, [], "Unloaded"))
                .Query(Args("""{"category":"minions","offset":10,"limit":2}"""));
            Check(!result.available && result.catalog_count is null && result.observed_unlocked_count is null &&
                result.not_observed_unlocked_count is null && result.unknown_count is null &&
                result.entries.Length == 0 && result.matched_count == 0 && result.next_offset is null &&
                !result.truncated && result.readiness == "unavailable" && result.offset == 10 && result.limit == 2);
        });
        Test("invalid query types duplicate fields and unsupported locked filter rejected", () =>
        {
            foreach (var json in new[] { "null", "[]", "{}", """{"category":"achievements"}""",
                         """{"category":"mounts","state":"locked"}""", """{"category":"mounts","offset":-1}""",
                         """{"category":"mounts","offset":4097}""", """{"category":"mounts","limit":0}""",
                         """{"category":"mounts","limit":51}""", """{"category":"mounts","limit":1.2}""",
                         """{"category":"mounts","query":null}""", """{"category":"mounts","query":"\n"}""",
                         """{"category":"mounts","extra":1}""", """{"category":"mounts","ids":[0]}""",
                         """{"category":"mounts","ids":[1,1]}""", """{"category":"mounts","ids":[true]}""",
                         """{"category":"mounts","ids":[4294967296]}""", """{"category":"mounts","category":"minions"}""" })
                Throws<ArgumentException>(() => CollectionLookup.ValidateArguments(Args(json)));
            Throws<ArgumentException>(() => CollectionLookup.ValidateArguments(JsonSerializer.SerializeToElement(
                new { category = "mounts", ids = Enumerable.Range(1, 21) })));
            Throws<ArgumentException>(() => CollectionLookup.ValidateArguments(JsonSerializer.SerializeToElement(
                new { category = "mounts", query = new string('x', 101) })));
            Throws<ArgumentException>(() => new CollectionLookup(Small()).Query(Args("""{"category":"minions"}""")));
        });
        Test("captured arrays and returned arrays cannot mutate later queries", () =>
        {
            var rows = new[] { new CollectionEntry(1, "First", "unlocked", true, null) };
            var lookup = new CollectionLookup(new("mounts", Time, true, rows));
            rows[0] = new(2, "Changed", "unlocked", true, null);
            var first = lookup.Query(Args("""{"category":"mounts"}"""));
            first.entries[0] = new(3, "Changed again", "unlocked", true, null);
            Check(lookup.Query(Args("""{"category":"mounts"}""")).entries[0].id == 1);
        });
        Test("Unicode byte truncation preserves counts and progress", () =>
        {
            var result = new CollectionLookup(Large()).Query(Args("""{"category":"minions"}"""));
            Check(result.entries.Length is > 0 and < 50 && result.truncated && result.matched_count == 4096);
            Check(result.next_offset == result.entries.Length && JsonSerializer.SerializeToUtf8Bytes(result).Length <= CollectionLookup.MaxBytes);
        });
        Test("unconfirmed combines negative and unknown while compact pages keep counts and byte limits", () =>
        {
            var lookup = new CollectionLookup(Cards());
            var normal = lookup.Query(Args("""{"category":"triple_triad","state":"unconfirmed"}"""));
            Check(normal.entries.Length == 50 && normal.limit == 50 && normal.matched_count == 426);
            Check(normal.entries.All(row => row.unlocked is null && row.unknown_reason is not null));
            var seen = new HashSet<uint>();
            var offset = 0;
            var pages = 0;
            do
            {
                var page = lookup.Query(JsonSerializer.SerializeToElement(new { category = "triple_triad", state = "unconfirmed", compact = true, offset }));
                pages++;
                Check(page.limit == 200 && page.entries.Length > 0 && page.entries.Length <= 200 && page.matched_count == 426);
                Check(page.catalog_count == 476 && page.observed_unlocked_count == 50 && page.not_observed_unlocked_count == 425 && page.unknown_count == 1);
                Check(page.coverage.Contains(CollectionLookup.CompactCoverage) && page.coverage.Length <= 1000);
                Check(JsonSerializer.SerializeToUtf8Bytes(page).Length <= 20000);
                Check(page.entries.All(row => seen.Add(row.id) && row.unlocked is null &&
                    (row.state == "not_observed_unlocked" ? row.unknown_reason is null : row.unknown_reason == "Synthetic unsupported mapping.")));
                if (offset == 0) Check(page.entries.Length > 50);
                if (page.next_offset is not { } next) break;
                Check(next == offset + page.entries.Length);
                offset = next;
            } while (true);
            Check(seen.Count == 426 && pages == 3 && !seen.Contains(1) && seen.Contains(500));
        });
        Test("compact mode is restricted and explicit limits are order independent", () =>
        {
            foreach (var json in new[] {
                """{"category":"mounts","state":"unconfirmed","compact":true}""",
                """{"category":"triple_triad","compact":true}""",
                """{"category":"triple_triad","state":"unknown","compact":true}""",
                """{"category":"triple_triad","state":"unconfirmed","compact":1}""",
                """{"category":"triple_triad","state":"unconfirmed","compact":false,"limit":51}""",
                """{"category":"triple_triad","state":"unconfirmed","compact":true,"limit":201}""" })
                Throws<ArgumentException>(() => CollectionLookup.ValidateArguments(Args(json)));
            var result = new CollectionLookup(Cards()).Query(Args("""{"limit":100,"compact":true,"state":"unconfirmed","category":"triple_triad"}"""));
            Check(result.limit == 100 && result.entries.Length == 100);
            var other = new CollectionLookup(Small()).Query(Args("""{"category":"mounts","state":"unconfirmed"}"""));
            Check(other.entries.Length == 2 && other.entries.All(row => row.unlocked is null && row.unknown_reason is not null));
        });
        Test("compact Unicode pages preserve exceptional unsupported reasons", () =>
        {
            var result = new CollectionLookup(Cards(unicode: true)).Query(Args("""{"category":"triple_triad","state":"unconfirmed","compact":true}"""));
            Check(result.entries.Length is > 0 and < 200 && result.next_offset == result.entries.Length);
            Check(JsonSerializer.SerializeToUtf8Bytes(result).Length <= 20000);
        });
        Test("all category provenance and explicit permitted-summary shapes remain bounded", () =>
        {
            foreach (var category in new[] { "mounts", "minions", "fashion_accessories", "facewear", "emotes", "orchestrion" })
            {
                var lookup = new CollectionLookup(Expanded(category));
                var result = lookup.Query(JsonSerializer.SerializeToElement(new { category }));
                Check(result.category == category && result.source == CollectionSnapshot.Source(category));
                Check(result.entries.Length == 3 && result.entries[0].unlocked == true && result.entries[1].unlocked is null);
                Check(result.readiness == "positive_observations_only" && JsonSerializer.SerializeToUtf8Bytes(result).Length <= 20000);
                Check(lookup.Summary.scope == "observed_collections" && lookup.Summary.categories.SequenceEqual([category]));
                var summary = JsonSerializer.SerializeToElement(lookup.Summary);
                Check(summary.EnumerateObject().Select(p => p.Name).SequenceEqual(["scope", "categories"]));
                var unavailable = new CollectionLookup(new(category, Time, false, [], "Not loaded"))
                    .Query(JsonSerializer.SerializeToElement(new { category }));
                Check(unavailable.catalog_count is null && unavailable.entries.Length == 0);
            }
            Check(CollectionSnapshot.CategoryCoverage("facewear").Contains("colour variants are not separate unlocks"));
            Check(CollectionSnapshot.CategoryCoverage("orchestrion").Contains("not ownership of roll items"));
            Check(CollectionSnapshot.CategoryCoverage("emotes").Contains("do not establish current usability"));
        });
        Console.WriteLine($"{passed} collection fixture groups passed; actual native observations and game hydration still require in-game verification.");
    }

    private static unsafe void NativeMinionFlags()
    {
        PlayerState player = default;
        UIState ui = default;
        Check(player.UnlockedMountsBitArray.BitCount == CollectionReader.MountUnlockBitCount);
        Check(ui.UnlockedCompanionsBitArray.BitCount == CollectionReader.MinionUnlockBitCount);
        Check(player.UnlockedOrnamentsBitArray.BitCount == CollectionReader.OrnamentUnlockBitCount);
        Check(player.UnlockedGlassesStylesBitArray.BitCount == CollectionReader.GlassesStyleUnlockBitCount);
        Check(player.UnlockedOrchestrionRollsBitArray.BitCount == CollectionReader.OrchestrionUnlockBitCount);
        ui.UnlockedCompanions[0] = 0b00000010;
        ui.UnlockedCompanions[^1] = 0b10000000;
        Check(ui.IsCompanionUnlocked(1) && ui.IsCompanionUnlocked(599) && !ui.IsCompanionUnlocked(2));
    }

    private static CollectionSnapshot Small() => CollectionReader.Describe("mounts", Time,
        [new(1, "Synthetic Alpha", 0), new(2, "Synthetic Beta", 1), new(3, "Synthetic Unsupported", -1)], id => id == 1);
    private static CollectionSnapshot Large() => new("minions", Time, true, Enumerable.Range(1, CollectionSnapshot.MaxEntries)
        .Select(id => new CollectionEntry((uint)id, new('界', 128), "unknown", null, "Synthetic unsupported catalog row")));
    private static CollectionSnapshot Expanded(string category) => CollectionReader.Describe(category, Time,
        [new(1, "Synthetic Alpha", category == "mounts" ? 0 : null, category == "facewear" ? 0u : null),
         new(2, "Synthetic Beta", category == "mounts" ? 1 : null, category == "facewear" ? 1u : null),
         new(4097, "Synthetic Unsupported", null)], id => id == 1);
    private static CollectionSnapshot Cards(bool unicode = false) => new("triple_triad", Time, true,
        Enumerable.Range(1, 475).Select(id => new CollectionEntry((uint)id, unicode ? new('界', 128) : $"Card {id}",
            id <= 50 ? "unlocked" : "not_observed_unlocked", id <= 50 ? true : null,
            id <= 50 ? null : "Synthetic negative observation; collection readiness is unverified and this does not prove a missing card."))
        .Append(new(500, "Synthetic unsupported card", "unknown", null, "Synthetic unsupported mapping.")));

    internal static void WriteProducerFixtures(string output)
    {
        static object Fixture(string name, CollectionSnapshot snapshot, string json)
        {
            var arguments = Args(json);
            return new { name, arguments, result = new CollectionLookup(snapshot).Query(arguments) };
        }
        var fixtures = new[]
        {
            Fixture("positive_negative_unknown", Small(), """{"category":"mounts"}"""),
            Fixture("name_and_ids", Small(), """{"category":"mounts","query":"synthetic alpha","ids":[1,2]}"""),
            Fixture("negative_state", Small(), """{"category":"mounts","state":"not_observed_unlocked"}"""),
            Fixture("unknown_state", Small(), """{"category":"mounts","state":"unknown"}"""),
            Fixture("first_page", Small(), """{"category":"mounts","limit":1}"""),
            Fixture("last_page", Small(), """{"category":"mounts","offset":2,"limit":2}"""),
            Fixture("empty_id", Small(), """{"category":"mounts","ids":[999]}"""),
            Fixture("unavailable", new("minions", Time, false, [], "Unloaded"), """{"category":"minions","offset":10,"limit":2}"""),
            Fixture("minion_positive_negative", CollectionReader.Describe("minions", Time,
                [new(1, "Synthetic Minion", null), new(2, "Other Synthetic Minion", null)], id => id == 1), """{"category":"minions"}"""),
            Fixture("unicode_truncation", Large(), """{"category":"minions"}"""),
            Fixture("fashion_accessories_positive_negative_unknown", Expanded("fashion_accessories"), """{"category":"fashion_accessories"}"""),
            Fixture("facewear_positive_negative_unknown", Expanded("facewear"), """{"category":"facewear"}"""),
            Fixture("emotes_positive_negative_unknown", Expanded("emotes"), """{"category":"emotes"}"""),
            Fixture("orchestrion_positive_negative_unknown", Expanded("orchestrion"), """{"category":"orchestrion"}"""),
            Fixture("triple_triad_unconfirmed_default", Cards(), """{"category":"triple_triad","state":"unconfirmed"}"""),
            Fixture("triple_triad_unconfirmed_compact", Cards(), """{"category":"triple_triad","state":"unconfirmed","compact":true}"""),
            Fixture("triple_triad_unconfirmed_exception", Cards(), """{"category":"triple_triad","state":"unconfirmed","compact":true,"ids":[500]}"""),
            Fixture("triple_triad_unconfirmed_unicode", Cards(true), """{"category":"triple_triad","state":"unconfirmed","compact":true}"""),
        };
        var full = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, JsonSerializer.Serialize(fixtures, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static JsonElement Args(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
    private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
}

