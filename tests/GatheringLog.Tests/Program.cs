using System.Runtime.Loader;
using System.Text.Json;
using CompAI;
using FFXIVClientStructs.FFXIV.Client.Game;

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
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-10-20T12:00:00Z");
    private static int passed;

    internal static void Run()
    {
        Test("pinned structure shape is distinct from accepted row IDs", NativeShape);
        Test("GatheringItem IDs are not Item IDs or raw bitmap indexes", () =>
        {
            var calls = new List<uint>();
            var result = GatheringLogReader.Describe(Time,
                [new(1, 42, "Synthetic shard"), new(817, 49207, "Synthetic log"),
                 new(10187, 43917, "Synthetic collectable"), new(65535, 99, "Last typed ID")],
                id => { calls.Add(id); return id is 817 or 10187; });
            Check(calls.SequenceEqual([1u, 817u, 10187u, 65535u]));
            Check(result.Entries.Single(row => row.Id == 1).Completed is null);
            Check(result.Entries.Single(row => row.Id == 817).Completed == true);
            Check(result.Entries.Single(row => row.Id == 10187).Completed == true);
        });
        Test("positive negative and unsupported evidence stay distinct", () =>
        {
            var result = Small();
            Check(result.Category == "gathering_log" && result.Readiness == "positive_observations_only");
            Check(result.Entries.Select(row => row.State).SequenceEqual(["completed", "not_observed_complete", "unknown"]));
            Check(result.Entries[0].Completed == true && result.Entries[0].UnknownReason is null);
            Check(result.Entries.Skip(1).All(row => row.Completed is null && row.UnknownReason is not null));
        });
        Test("unloaded gathering state does not enumerate or call native methods", () =>
        {
            var enumerated = false;
            IEnumerable<GatheringLogDefinition> Rows()
            {
                enumerated = true;
                yield return new(1, 42, "Synthetic");
            }
            var result = GatheringLogReader.Describe(Time, Rows(),
                _ => throw new Exception("Unloaded callback"), loaded: false);
            Check(!enumerated && !result.Available && result.Entries.Count == 0);
        });
        Test("invalid catalog IDs names and duplicates fail before observation", () =>
        {
            foreach (var rows in new GatheringLogDefinition[][]
            {
                [], [new(0, 42, "Zero")], [new(1, 0, "No Item")], [null!],
                [new(1, 42, "Duplicate"), new(1, 43, "Conflicting duplicate")],
                [new(1, 42, "")], [new(1, 42, "Control\nname")],
                [new(1, 42, new string('x', 129))],
            })
                Check(!GatheringLogReader.Describe(Time, rows, _ => throw new Exception("Invalid callback")).Available);
        });
        Test("different gathering IDs for the same item remain separate log entries", () =>
        {
            var result = GatheringLogReader.Describe(Time,
                [new(1, 42, "Synthetic"), new(2, 42, "Synthetic")], id => id == 1);
            Check(result.Entries.Count == 2 && result.Entries[0].Completed == true && result.Entries[1].Completed is null);
        });
        Test("catalog traversal is bounded before any native call", () =>
        {
            var enumerated = 0;
            IEnumerable<GatheringLogDefinition> Rows()
            {
                for (uint id = 1; ; id++)
                {
                    enumerated++;
                    yield return new(id, id + 100, "Synthetic");
                }
            }
            Check(!GatheringLogReader.Describe(Time, Rows(), _ => throw new Exception("Oversize callback")).Available);
            Check(enumerated == ProgressionSnapshot.MaxEntries + 1);
        });
        Test("gathering negatives cannot become incomplete or numeric progress", () =>
        {
            Throws<ArgumentException>(() => new ProgressionSnapshot("gathering_log", Time, true, "positive_observations_only",
                [new(1, "Synthetic", "not_completed", Completed: false)]));
            Throws<ArgumentException>(() => new ProgressionSnapshot("gathering_log", Time, true, "positive_observations_only",
                [new(1, "Synthetic", "not_observed_complete", Completed: false, UnknownReason: "Unknown")]));
            Throws<ArgumentException>(() => new ProgressionSnapshot("gathering_log", Time, true, "positive_observations_only",
                [new(1, "Synthetic", "completed", Completed: true, Progress: 1)]));
        });
        Test("name ID and state filters preserve catalog counts and paging", () =>
        {
            var lookup = new ProgressionLookup(Small());
            var exact = lookup.Query(Args("""{"category":"gathering_log","ids":[817,49207]}"""));
            Check(exact.entries.Length == 1 && exact.entries[0].id == 817 && exact.catalog_count == 3);
            var first = lookup.Query(Args("""{"category":"gathering_log","limit":1}"""));
            Check(first.next_offset == 1 && first.truncated && first.completed_count == 1 &&
                first.not_observed_complete_count == 1 && first.unknown_count == 1 && first.not_completed_count == 0);
            var filtered = lookup.Query(Args("""{"category":"gathering_log","query":"synthetic log","state":"not_observed_complete"}"""));
            Check(filtered.entries.Length == 1 && filtered.entries[0].id == 817);
        });
        Test("unavailable counts stay null instead of zero", () =>
        {
            var result = new ProgressionLookup(ProgressionSnapshot.Unavailable("gathering_log", "Unloaded"))
                .Query(Args("""{"category":"gathering_log","offset":5}"""));
            Check(!result.available && result.catalog_count is null && result.completed_count is null &&
                result.not_observed_complete_count is null && result.unknown_count is null && result.entries.Length == 0);
        });
        Console.WriteLine($"{passed} gathering-log checks passed.");
    }

    private static unsafe void NativeShape()
    {
        // Zeroed local memory; no Instance(), native completion method or running process.
        QuestManager synthetic = default;
        Check(synthetic.GatheredGatheringItemsBitArray.BitCount == GatheringLogReader.GatheredBitCount);
        Check(synthetic.GatheredGatheringItems.Length == 102);
    }

    private static ProgressionSnapshot Small() => GatheringLogReader.Describe(Time,
        [new(1, 42, "Synthetic shard"), new(817, 49207, "Synthetic log"), new(65536, 99, "Unsupported future item")],
        id => id == 1);

    internal static void Produce(string output)
    {
        static object Fixture(string name, ProgressionSnapshot snapshot, string json)
        {
            var arguments = Args(json);
            return new { name, arguments, result = new ProgressionLookup(snapshot).Query(arguments) };
        }
        var fixtures = new[]
        {
            Fixture("gathering_all_states", Small(), """{"category":"gathering_log"}"""),
            Fixture("gathering_ids_are_not_item_ids", Small(), """{"category":"gathering_log","ids":[817,49207]}"""),
            Fixture("gathering_name", Small(), """{"category":"gathering_log","query":"synthetic log"}"""),
            Fixture("gathering_negative_unknown", Small(), """{"category":"gathering_log","state":"not_observed_complete"}"""),
            Fixture("gathering_first_page", Small(), """{"category":"gathering_log","limit":1}"""),
            Fixture("gathering_last_page", Small(), """{"category":"gathering_log","offset":2,"limit":1}"""),
            Fixture("gathering_unavailable", new("gathering_log", Time, false, "unavailable", [], "Unloaded"),
                """{"category":"gathering_log","offset":5}"""),
            Fixture("gathering_high_valid_ids", GatheringLogReader.Describe(Time,
                [new(10187, 43917, "Synthetic collectable")], _ => true), """{"category":"gathering_log","ids":[10187]}"""),
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
