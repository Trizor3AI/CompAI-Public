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
Checks.Run();
if (args is ["--produce-fixtures", var output]) Checks.Produce(output);

internal static class Checks
{
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-10-23T10:00:00Z");
    private static int passed;
    internal static void Run()
    {
        Test("installed card buffer shape matches the supported public domain", NativeBounds);
        Test("public names join resident keys rather than enumeration position", () =>
        {
            var catalog = TripleTriadReader.BuildCatalog(
                [new(475, "Synthetic Final Card"), new(0, "0"), new(1, "Synthetic First Card"),
                 new(8, ""), new(12, "Synthetic Unmapped Card")], [0, 8, 1, 475]);
            Check(catalog.Select(row => row.Id).SequenceEqual([1u, 12u, 475u]));
            Check(catalog[0].HasResident && !catalog[1].HasResident && catalog[2].HasResident);
            var calls = new List<uint>();
            var snapshot = TripleTriadReader.Describe(Time, catalog, id => { calls.Add(id); return id == 475; });
            Check(calls.SequenceEqual([1u, 1u, 475u, 475u]));
            Check(snapshot.Entries[0].Unlocked is null && snapshot.Entries[1].State == "unknown" && snapshot.Entries[2].Unlocked == true);
        });
        Test("unsupported or missing native keys never reach the native helper", () =>
        {
            var calls = new List<uint>();
            var result = TripleTriadReader.Describe(Time,
                [new(1, "First", true), new(475, "Last", true), new(476, "Outside bit count", true),
                 new(65536, "Outside ushort", true), new(uint.MaxValue, "Huge ID", true), new(2, "Missing Resident", false)],
                id => { calls.Add(id); return true; });
            Check(calls.SequenceEqual([1u, 1u, 475u, 475u]));
            Check(result.Entries.Count(row => row.State == "unknown") == 4);
        });
        Test("a native false never claims a card is unowned or unregistered", () =>
        {
            var result = TripleTriadReader.Describe(Time, [new(1, "Synthetic Card", true)], _ => false);
            Check(result.Entries[0].State == "not_observed_unlocked" && result.Entries[0].Unlocked is null &&
                result.Entries[0].UnknownReason!.Contains("does not mean unowned or unregistered"));
        });
        Test("unloaded and changing observations discard all partial rows", () =>
        {
            var unloaded = TripleTriadReader.Describe(Time, [new(1, "Synthetic Card", true)],
                _ => throw new Exception("Unexpected native read"), loaded: false);
            Check(!unloaded.Available && unloaded.Entries.Count == 0);
            var calls = 0;
            var changed = TripleTriadReader.Describe(Time, [new(1, "Synthetic Card", true), new(2, "Changing Card", true)],
                _ => ++calls != 4);
            Check(!changed.Available && changed.Entries.Count == 0 && calls == 4);
        });
        Test("changed buffers and malformed buffer lengths reject stability", () =>
        {
            var first = new byte[60]; var second = new byte[60];
            Check(TripleTriadReader.StableBitmap(first, second));
            second[59] = 8;
            Check(!TripleTriadReader.StableBitmap(first, second));
            Check(!TripleTriadReader.StableBitmap(new byte[59], new byte[59]));
            Check(!TripleTriadReader.StableBitmap(new byte[61], new byte[61]));
            Check(!TripleTriadReader.StableBitmap([], []));
        });
        Test("each public sheet identity game-data identity and language bind the cache", () =>
        {
            var names = new object(); var residents = new object(); var game = new object();
            var cached = new TripleTriadCatalog(names, residents, game, "English", []);
            Check(cached.Matches(names, residents, game, "English"));
            Check(!cached.Matches(new object(), residents, game, "English"));
            Check(!cached.Matches(names, new object(), game, "English"));
            Check(!cached.Matches(names, residents, new object(), "English"));
            Check(!cached.Matches(names, residents, game, "German"));
        });
        Test("ambiguous invalid and oversized catalogs reject before native reads", () =>
        {
            foreach (var rows in new TripleTriadDefinition[][]
            {
                [], [new(0, "Zero", true)], [new(1, "Duplicate A", true), new(1, "Duplicate B", true)],
                [new(1, "Invalid\nName", true)], [new(1, new string('x', 129), true)],
                Enumerable.Range(1, 4097).Select(id => new TripleTriadDefinition((uint)id, "Too many", true)).ToArray(),
            })
                Check(!TripleTriadReader.Describe(Time, rows, _ => throw new Exception("Unexpected native read")).Available);
            Throws(() => TripleTriadReader.BuildCatalog([new(1, "First"), new(1, "Duplicate")], [1]));
            Throws(() => TripleTriadReader.BuildCatalog([new(1, "First")], [1, 1]));
            Throws(() => TripleTriadReader.BuildCatalog([new(0, "0"), new(1, "")], [0, 1]));
            Throws(() => TripleTriadReader.BuildCatalog([new(1, "Invalid\nName")], [1]));
            Throws(() => TripleTriadReader.BuildCatalog([new(1, "First")], Enumerable.Range(0, 4097).Select(id => (uint)id)));
            Throws(() => TripleTriadReader.BuildCatalog(Enumerable.Range(0, 4097).Select(id => new TripleTriadCardName((uint)id, "Too many")), [1]));
        });
        Test("registered card states preserve shared collection pagination and unknown counts", () =>
        {
            var lookup = new CollectionLookup(Small());
            var first = lookup.Query(Args("""{"category":"triple_triad","limit":1}"""));
            var last = lookup.Query(Args("""{"category":"triple_triad","offset":2,"limit":2}"""));
            Check(first.catalog_count == 3 && first.observed_unlocked_count == 1 && first.not_observed_unlocked_count == 1 &&
                first.unknown_count == 1 && first.next_offset == 1);
            Check(last.entries.Length == 1 && last.entries[0].state == "unknown" && last.next_offset is null);
            Check(lookup.Query(Args("""{"category":"triple_triad","ids":[475]}""")).entries.Single().name == "Synthetic Final Card");
        });
        Test("unavailable counts remain unknown", () =>
        {
            var result = new CollectionLookup(CollectionSnapshot.Unavailable("triple_triad", "Unloaded"))
                .Query(Args("""{"category":"triple_triad"}"""));
            Check(result.entries.Length == 0 && result.catalog_count is null && result.observed_unlocked_count is null &&
                result.not_observed_unlocked_count is null && result.unknown_count is null);
        });
        Console.WriteLine($"PASS {passed} Triple Triad checks");
    }

    private static unsafe void NativeBounds()
    {
        UIState ui = default;
        Check(ui.UnlockedTripleTriadCardsBitArray.BitCount == TripleTriadReader.UnlockBitCount);
        Check(ui.UnlockedTripleTriadCards.Length == TripleTriadReader.UnlockByteCount);
    }

    private static CollectionSnapshot Small() => TripleTriadReader.Describe(Time,
        [new(1, "Synthetic First Card", true), new(475, "Synthetic Final Card", true), new(476, "Synthetic Unsupported Card", true)],
        id => id == 475);

    internal static void Produce(string output)
    {
        object Fixture(string name, CollectionSnapshot snapshot, string json)
        {
            var arguments = Args(json);
            return new { name, arguments, result = new CollectionLookup(snapshot).Query(arguments) };
        }
        var fixtures = new[]
        {
            Fixture("triple_triad_states", Small(), """{"category":"triple_triad"}"""),
            Fixture("triple_triad_resident_id", Small(), """{"category":"triple_triad","ids":[475]}"""),
            Fixture("triple_triad_name", Small(), """{"category":"triple_triad","query":"first"}"""),
            Fixture("triple_triad_negative", Small(), """{"category":"triple_triad","state":"not_observed_unlocked"}"""),
            Fixture("triple_triad_unknown", Small(), """{"category":"triple_triad","state":"unknown"}"""),
            Fixture("triple_triad_first_page", Small(), """{"category":"triple_triad","limit":1}"""),
            Fixture("triple_triad_last_page", Small(), """{"category":"triple_triad","offset":2,"limit":2}"""),
            Fixture("triple_triad_unavailable", CollectionSnapshot.Unavailable("triple_triad", "Unloaded"),
                """{"category":"triple_triad"}"""),
        };
        var path = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(fixtures, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static JsonElement Args(string text) => JsonDocument.Parse(text).RootElement.Clone();
    private static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
    private static void Throws(Action action) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected invalid catalog rejection"); }
    private static void Test(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
}
