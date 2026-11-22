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
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-10-20T17:00:00Z");
    private static int passed;
    internal static void Run()
    {
        Test("installed managed helpers use the verified sheet-ID mappings", NativeMapping);
        Test("Tsoly Gar uses its visible spear row and preserves unknown negative state", TsolyGarMapping);
        Test("both logs preserve positive true and native false as nullable unknown", () =>
        {
            foreach (var category in new[] { "fishing_log", "spearfishing_log" })
            {
                var result = Small(category);
                Check(result.Available && result.Readiness == "positive_observations_only" &&
                    result.Entries[0].State == "completed" && result.Entries[0].Completed == true &&
                    result.Entries[1].State == "not_observed_complete" && result.Entries[1].Completed is null &&
                    result.Entries[2].State == "unknown" && result.Entries[2].Completed is null);
                Check(result.Entries.All(row => row.Rank is null && row.Progress is null && row.Required is null &&
                    row.Level is null && row.Experience is null && row.ExperienceRequired is null));
            }
        });
        Test("only actual eligible log IDs reach the caught helper", () =>
        {
            var fishCalls = new List<uint>();
            var result = FishingLogReader.Describe("fishing_log", Time,
                [new(1, 4776, "First"), new(1522, 52011, "Last"), new(1523, 52012, "Future"),
                 new(uint.MaxValue, 10, "Overflow"), new(2, 1, "Not in log", false)],
                id => { fishCalls.Add(id); return true; });
            Check(fishCalls.SequenceEqual([1u, 1522u]) && result.Entries.Count == 4);
            var spearCalls = new List<uint>();
            result = FishingLogReader.Describe("spearfishing_log", Time,
                [new(19999, 1, "Below offset"), new(20000, 20144, "First"), new(20303, 2, "Last physical"),
                 new(20304, 3, "Past physical"), new(30000, 4, "Special hidden", false)],
                id => { spearCalls.Add(id); return true; });
            Check(spearCalls.SequenceEqual([20000u, 20303u]) && result.Entries.Count == 4 &&
                !FishingLogReader.IsSupportedId("spearfishing_log", uint.MaxValue));
        });
        Test("unloaded state neither enumerates catalog nor reads caught flags", () =>
        {
            IEnumerable<FishingLogDefinition> Throwing() => throw new Exception("Unexpected enumeration");
            // Iterator defers its exception until enumeration.
            IEnumerable<FishingLogDefinition> Rows() { foreach (var row in Throwing()) yield return row; }
            Check(!FishingLogReader.Describe("fishing_log", Time, Rows(),
                _ => throw new Exception("Unexpected read"), loaded: false).Available);
        });
        Test("malformed named entries and duplicates fail before native reads", () =>
        {
            foreach (var rows in new FishingLogDefinition[][]
            {
                [], [new(0, 1, "Zero")], [new(1, 0, "Missing Item")], [new(1, 1, "Bad\nname")],
                [new(1, 1, new string('a', 129))], [new(1, 1, "First"), new(1, 2, "Duplicate")],
            })
                Check(!FishingLogReader.Describe("fishing_log", Time, rows, _ => throw new Exception()).Available);
        });
        Test("catalog enumeration is bounded even for ineligible rows", () =>
        {
            var seen = 0;
            IEnumerable<FishingLogDefinition> Rows()
            {
                for (uint id = 1; ; id++) { seen++; yield return new(id, 1, "Hidden", false); }
            }
            Check(!FishingLogReader.Describe("fishing_log", Time, Rows(), _ => throw new Exception()).Available);
            Check(seen == ProgressionSnapshot.MaxEntries + 1);
        });
        Test("owner pointer readiness shape and fingerprint changes invalidate capture", () =>
        {
            var ready = new FishingLogObservation(1, 42, true, 1522, 191, new string('0', 382));
            Check(FishingLogReader.IsStable("fishing_log", ready, ready));
            foreach (var changed in new[]
            {
                ready with { PlayerAddress = 2 }, ready with { ContentId = 43 },
                ready with { Loaded = false }, ready with { BitCount = 1523 },
                ready with { ByteCount = 192 }, ready with { Fingerprint = new string('1', 382) },
                ready with { Fingerprint = null },
            })
                Check(!FishingLogReader.IsStable("fishing_log", ready, changed));
            Check(!ready.IsReady("spearfishing_log") && !ready.IsReady("other"));
        });
        Test("public name caches invalidate for either sheet game data or language", () =>
        {
            var sheet = new object();
            var items = new object();
            var game = new object();
            var catalog = new FishingLogCatalog(sheet, items, game, "English", []);
            Check(catalog.Matches(sheet, items, game, "English"));
            Check(!catalog.Matches(new object(), items, game, "English"));
            Check(!catalog.Matches(sheet, new object(), game, "English"));
            Check(!catalog.Matches(sheet, items, new object(), "English"));
            Check(!catalog.Matches(sheet, items, game, "German"));
        });
        Test("querying Item IDs does not substitute them for fish or spear IDs", () =>
        {
            foreach (var category in new[] { "fishing_log", "spearfishing_log" })
            {
                var result = new ProgressionLookup(Small(category))
                    .Query(Args($$"""{"category":"{{category}}","ids":[4776,20144]}"""));
                Check(result.entries.Length == 0 && result.catalog_count == 3);
                var page = new ProgressionLookup(Small(category))
                    .Query(Args($$"""{"category":"{{category}}","limit":1}"""));
                Check(page.entries.Length == 1 && page.next_offset == 1 && page.completed_count == 1 &&
                    page.not_observed_complete_count == 1 && page.unknown_count == 1 && page.not_completed_count == 0);
            }
        });
        Test("unknown caught-before values cannot become false or numeric progress", () =>
        {
            foreach (var category in new[] { "fishing_log", "spearfishing_log" })
            {
                Throws<ArgumentException>(() => new ProgressionSnapshot(category, Time, true, "positive_observations_only",
                    [new(1, "Synthetic", "not_observed_complete", Completed: false, UnknownReason: "Unknown")]));
                Throws<ArgumentException>(() => new ProgressionSnapshot(category, Time, true, "positive_observations_only",
                    [new(1, "Synthetic", "completed", Completed: true, Progress: 1)]));
            }
        });
        Console.WriteLine($"{passed} fishing-log checks passed.");
    }

    private static unsafe void NativeMapping()
    {
        // Zeroed local struct plus managed helpers only: never Instance or live state.
        PlayerState synthetic = default;
        Check(synthetic.CaughtFishBitArray.BitCount == 1522 && synthetic.CaughtFish.Length == 191 &&
            synthetic.CaughtSpearfishBitArray.BitCount == 304 && synthetic.CaughtSpearfish.Length == 38);
        synthetic.CaughtFish[0] = 2; // FishParameter1, not Item4776.
        synthetic.CaughtFish[190] = 4; // Inclusive documented index1522.
        Check(synthetic.IsFishCaught(1) && !synthetic.IsFishCaught(2) && synthetic.IsFishCaught(1522));
        synthetic.CaughtSpearfish[0] = 1; // SpearfishingItem20000, not Item20144.
        synthetic.CaughtSpearfish[37] = 128; // Last physically safe index303.
        Check(synthetic.IsSpearfishCaught(20000) && !synthetic.IsSpearfishCaught(20001) &&
            synthetic.IsSpearfishCaught(20303));
    }

    private static unsafe void TsolyGarMapping()
    {
        // Public installed game data (2026.10.16): visible SpearfishingItem20268
        // references Item43828, named Tsoly Gar. Caught state below is synthetic.
        PlayerState synthetic = default;
        synthetic.CaughtSpearfish[33] = 0x10; // 20268 - 20000 = 268 = byte33, bit4.
        Check(FishingLogReader.IsSupportedId("spearfishing_log", 20268) &&
            synthetic.IsSpearfishCaught(20268) && !synthetic.IsSpearfishCaught(20267) &&
            !synthetic.IsSpearfishCaught(20269));
        var positive = new ProgressionLookup(TsolyGar(synthetic.IsSpearfishCaught(20268)));
        foreach (var arguments in new[]
        {
            """{"category":"spearfishing_log","query":"Tsoly Gar"}""",
            """{"category":"spearfishing_log","query":"tsoly gar","ids":[20268]}""",
        })
        {
            var result = positive.Query(Args(arguments));
            Check(result.matched_count == 1 && result.entries is [{ id: 20268, name: "Tsoly Gar", completed: true }]);
        }
        Check(positive.Query(Args("""{"category":"spearfishing_log","ids":[43828]}""")).matched_count == 0);
        synthetic.CaughtSpearfish[33] = 0;
        var unknown = new ProgressionLookup(TsolyGar(synthetic.IsSpearfishCaught(20268)))
            .Query(Args("""{"category":"spearfishing_log","query":"Tsoly Gar"}"""));
        Check(unknown.entries is [{ id: 20268, state: "not_observed_complete", completed: null }] &&
            unknown.not_completed_count == 0 && unknown.not_observed_complete_count == 1);
    }

    private static ProgressionSnapshot TsolyGar(bool syntheticCaught) =>
        FishingLogReader.Describe("spearfishing_log", Time, [new(20268, 43828, "Tsoly Gar")],
            _ => syntheticCaught);

    private static ProgressionSnapshot Small(string category) =>
        category == "fishing_log" ? FishingLogReader.Describe(category, Time,
            [new(1, 4776, "Synthetic fish"), new(1522, 52011, "Synthetic last fish"), new(1523, 52012, "Future fish")],
            id => id == 1) : FishingLogReader.Describe(category, Time,
            [new(20000, 20144, "Synthetic spear fish"), new(20288, 43848, "Synthetic last spear"), new(20304, 10, "Future spear")],
            id => id == 20000);

    internal static void Produce(string output)
    {
        var fixtures = new List<object>();
        void Add(string name, ProgressionSnapshot snapshot, string json)
        {
            var arguments = Args(json);
            fixtures.Add(new { name, arguments, result = new ProgressionLookup(snapshot).Query(arguments) });
        }
        foreach (var category in new[] { "fishing_log", "spearfishing_log" })
        {
            var snapshot = Small(category);
            Add(category + "_all_states", snapshot, $$"""{"category":"{{category}}"}""");
            Add(category + "_native_ids_not_item_ids", snapshot, $$"""{"category":"{{category}}","ids":[4776,20144]}""");
            Add(category + "_name", snapshot, $$"""{"category":"{{category}}","query":"synthetic last"}""");
            Add(category + "_negative_unknown", snapshot, $$"""{"category":"{{category}}","state":"not_observed_complete"}""");
            Add(category + "_first_page", snapshot, $$"""{"category":"{{category}}","limit":1}""");
            Add(category + "_last_page", snapshot, $$"""{"category":"{{category}}","offset":2,"limit":1}""");
            Add(category + "_unavailable", new(category, Time, false, "unavailable", [], "Unloaded"),
                $$"""{"category":"{{category}}"}""");
        }
        Add("spearfishing_tsoly_gar_positive", TsolyGar(true),
            """{"category":"spearfishing_log","query":"Tsoly Gar"}""");
        Add("spearfishing_tsoly_gar_unknown", TsolyGar(false),
            """{"category":"spearfishing_log","query":"Tsoly Gar","ids":[20268]}""");
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
