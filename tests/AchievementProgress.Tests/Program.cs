using System.Runtime.Loader;
using System.Text.Json;
using CompAI;
using NativeAchievement = FFXIVClientStructs.FFXIV.Client.Game.UI.Achievement;

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
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-10-20T15:00:00Z");
    private static int passed;
    private static AchievementProgressObservation Ready => new(1, 2, 3, 42, true, true, 7,
        NativeAchievement.AchievementState.Loaded, 105, 3, 10);

    internal static void Run()
    {
        Test("pinned progress fields retain uint values and loaded state", NativeShape);
        Test("loaded record is keyed by its own achievement and does not imply completion", () =>
        {
            var snapshot = Snapshot();
            var row = snapshot.Entries.Single();
            Check(snapshot.Available && snapshot.Category == "achievement_progress" &&
                snapshot.Readiness == "loaded_progress_record" && snapshot.CapturedAtUtc == Time);
            Check(row.Id == 105 && row.Name == "Synthetic achievement" && row.State == "observed" &&
                row.Progress == 3 && row.Required == 10 && row.Completed is null &&
                row.Rank is null && row.Level is null && row.Experience is null &&
                row.ExperienceRequired is null && row.UnknownReason is null);
        });
        Test("zero progress is valid only with a loaded positive target", () =>
        {
            Check(Snapshot(Ready with { Current = 0 }).Entries.Single().Progress == 0);
            Check(!Snapshot(Ready with { Maximum = 0 }).Available);
        });
        Test("uint maximum and progress above target remain lossless", () =>
        {
            var row = Snapshot(Ready with { Current = uint.MaxValue, Maximum = 1 }).Entries.Single();
            Check(row.Progress == 4294967295L && row.Required == 1 && row.Completed is null);
            Check(Snapshot(Ready with { Maximum = uint.MaxValue }).Entries.Single().Required == 4294967295L);
        });
        Test("missing menu character pointer and native readiness never become zero counters", () =>
        {
            foreach (var sample in new[]
            {
                Ready with { PlayerAddress = 0 }, Ready with { AchievementAddress = 0 },
                Ready with { AgentAddress = 0 }, Ready with { ContentId = 0 },
                Ready with { PlayerLoaded = false }, Ready with { MenuReady = false },
                Ready with { AddonId = 0 }, Ready with { AchievementId = 0 },
                Ready with { State = NativeAchievement.AchievementState.Invalid },
                Ready with { State = NativeAchievement.AchievementState.Requested },
                Ready with { State = (NativeAchievement.AchievementState)3 },
            })
            {
                var snapshot = Snapshot(sample);
                Check(!snapshot.Available && snapshot.Readiness == "unavailable" && snapshot.Entries.Count == 0);
            }
        });
        Test("changed owners pointers menu IDs readiness or counters reject the entire observation", () =>
        {
            foreach (var changed in new[]
            {
                Ready with { PlayerAddress = 4 }, Ready with { AchievementAddress = 4 },
                Ready with { AgentAddress = 4 }, Ready with { ContentId = 43 },
                Ready with { PlayerLoaded = false }, Ready with { MenuReady = false },
                Ready with { AddonId = 8 }, Ready with { State = NativeAchievement.AchievementState.Requested },
                Ready with { AchievementId = 106 }, Ready with { Current = 4 },
                Ready with { Maximum = 11 },
            })
            {
                Check(!AchievementProgressReader.Describe(Time, Ready, changed,
                    new(105, "Synthetic achievement")).Available);
                Check(!AchievementProgressReader.Describe(Time, changed, Ready,
                    new(105, "Synthetic achievement")).Available);
            }
        });
        Test("missing wrong or invalid names cannot relabel cached progress", () =>
        {
            foreach (var definition in new AchievementProgressDefinition?[]
            {
                null, new(106, "Different achievement"), new(0, "Zero"),
                new(105, ""), new(105, "Bad\nname"), new(105, new string('a', 129)),
            })
                Check(!AchievementProgressReader.Describe(Time, Ready, Ready, definition).Available);
        });
        Test("progress record IDs are not restricted by completion bitmap bounds", () =>
        {
            var future = Ready with { AchievementId = 5000 };
            Check(AchievementProgressReader.Describe(Time, future, future,
                new(5000, "Synthetic future definition")).Entries.Single().Id == 5000);
        });
        Test("name ID and paging filters retain the one-record coverage", () =>
        {
            var lookup = new ProgressionLookup(Snapshot());
            var matching = lookup.Query(Args("""{"category":"achievement_progress","ids":[105],"query":"synthetic","state":"observed"}"""));
            Check(matching.catalog_count == 1 && matching.observed_count == 1 &&
                matching.entries.Single().id == 105 && matching.completed_count == 0);
            foreach (var json in new[]
            {
                """{"category":"achievement_progress","ids":[106]}""",
                """{"category":"achievement_progress","query":"different"}""",
                """{"category":"achievement_progress","offset":1,"limit":1}""",
            })
            {
                var empty = lookup.Query(Args(json));
                Check(empty.available && empty.catalog_count == 1 && empty.observed_count == 1 &&
                    empty.entries.Length == 0 && empty.next_offset is null && !empty.truncated);
            }
        });
        Test("unavailable observation has null counts rather than missing-entry zero", () =>
        {
            var result = new ProgressionLookup(Snapshot(Ready with { MenuReady = false }))
                .Query(Args("""{"category":"achievement_progress"}"""));
            Check(!result.available && result.catalog_count is null && result.observed_count is null &&
                result.completed_count is null && result.entries.Length == 0);
        });
        Test("counter schema rejects unbounded ambiguous or completion-shaped observations", () =>
        {
            foreach (var row in new ProgressionEntry[]
            {
                new(105, "Synthetic", "observed", Progress: -1, Required: 1),
                new(105, "Synthetic", "observed", Progress: 0, Required: 0),
                new(105, "Synthetic", "observed", Progress: 4294967296L, Required: 1),
                new(105, "Synthetic", "observed", Progress: 0, Required: 4294967296L),
                new(105, "Synthetic", "observed", Progress: 1),
                new(105, "Synthetic", "observed", Progress: 1, Required: 2, Completed: false),
                new(105, "Synthetic", "observed", Progress: 1, Required: 2, Rank: 1),
                new(105, "Synthetic", "unknown", UnknownReason: "Unknown"),
            })
                Throws<ArgumentException>(() => new ProgressionSnapshot("achievement_progress", Time,
                    true, "loaded_progress_record", [row]));
            Throws<ArgumentException>(() => new ProgressionSnapshot("achievement_progress", Time,
                true, "loaded_progress_record", []));
            Throws<ArgumentException>(() => new ProgressionSnapshot("achievement_progress", Time,
                true, "loaded_progress_record",
                [new(105, "First", "observed", Progress: 1, Required: 2),
                 new(106, "Second", "observed", Progress: 1, Required: 2)]));
        });
        Console.WriteLine($"{passed} achievement-progress checks passed.");
    }

    private static unsafe void NativeShape()
    {
        // Local synthetic memory only: no Instance, native function or request.
        NativeAchievement synthetic = default;
        synthetic.ProgressRequestState = NativeAchievement.AchievementState.Loaded;
        synthetic.ProgressAchievementId = 105;
        synthetic.ProgressCurrent = uint.MaxValue;
        synthetic.ProgressMax = uint.MaxValue;
        Check((int)synthetic.ProgressRequestState == 2 && synthetic.ProgressAchievementId == 105 &&
            synthetic.ProgressCurrent == 4294967295L && synthetic.ProgressMax == 4294967295L);
    }

    private static ProgressionSnapshot Snapshot(AchievementProgressObservation? observation = null)
    {
        var value = observation ?? Ready;
        return AchievementProgressReader.Describe(Time, value, value, new(105, "Synthetic achievement"));
    }

    internal static void Produce(string output)
    {
        static object Fixture(string name, ProgressionSnapshot snapshot, string json)
        {
            var arguments = Args(json);
            return new { name, arguments, result = new ProgressionLookup(snapshot).Query(arguments) };
        }
        var fixtures = new[]
        {
            Fixture("achievement_progress_observed", Snapshot(), """{"category":"achievement_progress"}"""),
            Fixture("achievement_progress_zero", Snapshot(Ready with { Current = 0 }), """{"category":"achievement_progress"}"""),
            Fixture("achievement_progress_uint_max", Snapshot(Ready with { Current = uint.MaxValue, Maximum = uint.MaxValue }),
                """{"category":"achievement_progress"}"""),
            Fixture("achievement_progress_above_target", Snapshot(Ready with { Current = uint.MaxValue, Maximum = 1 }),
                """{"category":"achievement_progress"}"""),
            Fixture("achievement_progress_exact", Snapshot(), """{"category":"achievement_progress","ids":[105],"state":"observed"}"""),
            Fixture("achievement_progress_name", Snapshot(), """{"category":"achievement_progress","query":"synthetic"}"""),
            Fixture("achievement_progress_other_id", Snapshot(), """{"category":"achievement_progress","ids":[106]}"""),
            Fixture("achievement_progress_page_end", Snapshot(), """{"category":"achievement_progress","offset":1,"limit":1}"""),
            Fixture("achievement_progress_unavailable", Snapshot(Ready with { MenuReady = false }),
                """{"category":"achievement_progress"}"""),
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
