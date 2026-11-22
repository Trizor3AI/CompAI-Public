using System.Runtime.Loader;
using CompAI;
using NativeAchievement = FFXIVClientStructs.FFXIV.Client.Game.UI.Achievement;

var libraryPath = Environment.GetEnvironmentVariable("DALAMUD_LIB_PATH") ??
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "addon", "Hooks", "dev");
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var path = Path.Combine(libraryPath, name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
Tests.Run();

internal static class Tests
{
    private static int passed;
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-10-20T09:00:00Z");

    internal static void Run()
    {
        Test("installed completion bitmap bounds and documented readiness", NativeReadiness);
        Test("loaded completion preserves both true and false", () =>
        {
            var snapshot = AchievementReader.Describe(Time,
                [new(1, "Synthetic complete"), new(2, "Synthetic incomplete")], id => id == 1);
            Check(snapshot.Available && snapshot.Category == "achievements" &&
                snapshot.Readiness == "loaded_completion_bitmap" && snapshot.CapturedAtUtc == Time);
            Check(snapshot.Entries[0].State == "completed" && snapshot.Entries[0].Completed == true &&
                snapshot.Entries[0].UnknownReason is null);
            Check(snapshot.Entries[1].State == "not_completed" && snapshot.Entries[1].Completed == false &&
                snapshot.Entries[1].UnknownReason is null);
            Check(snapshot.Entries.All(row => row.Rank is null && row.Progress is null && row.Required is null &&
                row.Level is null && row.Experience is null && row.ExperienceRequired is null));
        });
        Test("only supported sheet IDs reach the completion API", () =>
        {
            var calls = new List<uint>();
            var snapshot = AchievementReader.Describe(Time,
                [new(1, "First"), new(4077, "Last supported"), new(4078, "Outside bitmap"),
                 new(8192, "Future definition"), new(uint.MaxValue, "Outside native integer")],
                id => { calls.Add(id); return id == 4077; });
            Check(calls.SequenceEqual([1u, 4077u]));
            Check(snapshot.Entries.Single(row => row.Id == 1).Completed == false);
            Check(snapshot.Entries.Single(row => row.Id == 4077).Completed == true);
            Check(snapshot.Entries.Where(row => row.Id >= AchievementReader.CompletionBitCount)
                .All(row => row.State == "unknown" && row.Completed is null && row.UnknownReason is not null));
        });
        Test("unloaded state never enumerates catalog or invokes completion", () =>
        {
            var enumerated = false;
            IEnumerable<AchievementDefinition> Unreadable()
            {
                enumerated = true;
                yield return new(1, "Synthetic");
            }
            var snapshot = AchievementReader.Describe(Time, Unreadable(),
                _ => throw new Exception("Unloaded completion must not be read."), loaded: false);
            Check(!enumerated && !snapshot.Available && snapshot.Readiness == "unavailable" && snapshot.Entries.Count == 0 &&
                snapshot.UnavailableReason!.Contains("menu", StringComparison.Ordinal));
        });
        Test("invalid catalogs never reach the completion API", () =>
        {
            foreach (var definitions in new AchievementDefinition[][]
            {
                [],
                [new(0, "Zero")],
                [new(1, "Duplicate"), new(1, "Duplicate again")],
                [new(1, "")],
                [new(1, " \t ")],
                [new(1, "Control\nname")],
                [new(1, new string('x', ProgressionSnapshot.MaxNameLength + 1))],
                [null!],
            })
            {
                var snapshot = AchievementReader.Describe(Time, definitions,
                    _ => throw new Exception("Invalid catalog must not read completion."));
                Check(!snapshot.Available && snapshot.Entries.Count == 0);
            }
        });
        Test("catalog traversal is bounded before native calls", () =>
        {
            var enumerated = 0;
            IEnumerable<AchievementDefinition> Unbounded()
            {
                for (uint id = 1; ; id++)
                {
                    enumerated++;
                    yield return new(id, "Synthetic");
                }
            }
            var snapshot = AchievementReader.Describe(Time, Unbounded(),
                _ => throw new Exception("Oversize catalog must not read completion."));
            Check(!snapshot.Available && enumerated == ProgressionSnapshot.MaxEntries + 1);
        });
        Test("valid names and observations remain immutable managed copies", () =>
        {
            var input = new AchievementDefinition[] { new(2, "Synthetic beta"), new(1, "Synthetic alpha") };
            var snapshot = AchievementReader.Describe(Time, input, _ => true);
            input[0] = new(3, "Changed");
            Check(snapshot.Entries.Select(row => row.Id).SequenceEqual([1u, 2u]));
            Check(snapshot.Entries.Single(row => row.Id == 2).Name == "Synthetic beta");
        });
        Test("unknown achievement completion cannot be coerced to false", () =>
        {
            Throws<ArgumentException>(() => new ProgressionSnapshot("achievements", Time, true,
                "loaded_completion_bitmap", [new(4078, "Unsupported", "unknown", Completed: false, UnknownReason: "Unsupported")]));
            Throws<ArgumentException>(() => new ProgressionSnapshot("achievements", Time, true,
                "loaded_completion_bitmap", [new(1, "Incomplete", "not_completed")]));
        });
        Test("achievement observation rejects unrelated numerical progress", () =>
        {
            Throws<ArgumentException>(() => new ProgressionSnapshot("achievements", Time, true,
                "loaded_completion_bitmap", [new(1, "Synthetic", "completed", Completed: true, Progress: 10)]));
        });
        Console.WriteLine($"{passed} achievement checks passed.");
    }

    private static unsafe void NativeReadiness()
    {
        // This is a zeroed local struct. Only managed readiness/array accessors
        // execute; no Instance(), IsComplete(), requests or game process are used.
        NativeAchievement synthetic = default;
        Check(synthetic.CompletedAchievementsBitArray.BitCount == AchievementReader.CompletionBitCount);
        Check(synthetic.CompletedAchievements.Length == 510);
        synthetic.State = NativeAchievement.AchievementState.Invalid;
        Check(!synthetic.IsLoaded());
        synthetic.State = NativeAchievement.AchievementState.Requested;
        Check(!synthetic.IsLoaded());
        synthetic.State = NativeAchievement.AchievementState.Loaded;
        Check(synthetic.IsLoaded());
        synthetic.State = (NativeAchievement.AchievementState)3;
        Check(!synthetic.IsLoaded());
    }

    private static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
    private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
}
