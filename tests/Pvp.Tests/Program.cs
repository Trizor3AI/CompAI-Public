using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using CompAI;
using NativeProfile = FFXIVClientStructs.FFXIV.Client.Game.UI.PvPProfile;

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
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-10-23T15:00:00Z");
    private static int passed;
    private static PvpObservation Ready => new(101, 102, 103, 104, 987654321, true, true, true, 88, 2,
        17, 8123, 12, 9, 450);
    private static ProgressionSnapshot Snapshot(PvpObservation? value = null, DateTimeOffset? time = null) =>
        PvpReader.Describe(time ?? Time, value ?? Ready, value ?? Ready);

    internal static void Run()
    {
        Test("pinned native fields retain offsets and scalar types", () =>
        {
            foreach (var expected in new (string Name, Type Type, int Offset)[]
            {
                ("IsLoaded", typeof(bool), 0),
                ("ExperienceMaelstrom", typeof(uint), 4), ("ExperienceTwinAdder", typeof(uint), 8),
                ("ExperienceImmortalFlames", typeof(uint), 12),
                ("RankMaelstrom", typeof(byte), 16), ("RankTwinAdder", typeof(byte), 17),
                ("RankImmortalFlames", typeof(byte), 18), ("Series", typeof(byte), 36),
                ("SeriesCurrentRank", typeof(byte), 37), ("SeriesExperience", typeof(ushort), 40),
            })
            {
                Check(typeof(NativeProfile).GetField(expected.Name)?.FieldType == expected.Type);
                Check((int)Marshal.OffsetOf<NativeProfile>(expected.Name) == expected.Offset);
            }
            Check(typeof(NativeProfile).StructLayoutAttribute?.Size == 0x8C);
        });
        Test("current GC selection has no rank offset or cross-GC EXP mix", () =>
        {
            var expected = new[] { new PvpRankObservation(0, 11), new(17, 22), new(50, 33) };
            for (byte company = 1; company <= 3; company++)
                Check(PvpReader.SelectGrandCompany(company, 0, 11, 17, 22, 50, 33) == expected[company - 1]);
            Check(PvpReader.SelectGrandCompany(0, 1, 11, 2, 22, 3, 33) is null);
            Check(PvpReader.SelectGrandCompany(4, 1, 11, 2, 22, 3, 33) is null);
            Check(PvpReader.SelectGrandCompany(byte.MaxValue, 1, 11, 2, 22, 3, 33) is null);
        });
        Test("stable loaded profile produces only two named observations", () =>
        {
            var snapshot = Snapshot();
            Check(snapshot.Available && snapshot.Readiness == "visible_menu_observation" && snapshot.MenuPage == 0);
            Check(snapshot.Category == "pvp" && snapshot.CapturedAtUtc == Time && snapshot.Entries.Count == 2);
            Check(snapshot.Entries[0].Id == 1 && snapshot.Entries[0].Rank == 17 && snapshot.Entries[0].Experience == 8123);
            Check(snapshot.Entries[0].Name.Contains("cumulative", StringComparison.Ordinal));
            Check(snapshot.Entries[1].Id == 2 && snapshot.Entries[1].Rank == 9 && snapshot.Entries[1].Experience == 450);
            Check(snapshot.Entries.All(row => row.State == "observed" && row.Completed is null && row.Progress is null &&
                row.Required is null && row.Level is null && row.ExperienceRequired is null && row.UnknownReason is null));
        });
        Test("loaded zero rank and EXP are observations without completion or unlock claims", () =>
        {
            var zero = Snapshot(Ready with { Rank = 0, Experience = 0, SeriesRank = 0, SeriesExperience = 0 });
            Check(zero.Available && zero.Entries.All(row => row.Rank == 0 && row.Experience == 0 && row.Completed is null));
        });
        Test("native maximum values remain lossless without invented caps", () =>
        {
            var maximum = Snapshot(Ready with { Rank = byte.MaxValue, Experience = uint.MaxValue,
                SeriesRank = byte.MaxValue, SeriesExperience = ushort.MaxValue });
            Check(maximum.Entries[0].Rank == 255 && maximum.Entries[0].Experience == 4294967295U);
            Check(maximum.Entries[1].Rank == 255 && maximum.Entries[1].Experience == 65535);
        });
        Test("captured GC label persists and missing or unsafe public text uses explicit ID", () =>
        {
            var named = PvpReader.Describe(Time, Ready, Ready, "Synthetic Twin Adder");
            Check(named.Entries[0].Name.EndsWith("Synthetic Twin Adder", StringComparison.Ordinal));
            foreach (var name in new string?[] { null, "", "\nUnsafe", new('x', 128) })
            {
                var fallback = PvpReader.Describe(Time, Ready, Ready, name);
                Check(fallback.Available && fallback.Entries[0].Name.EndsWith("observed Grand Company 2", StringComparison.Ordinal));
            }
        });
        Test("unloaded missing and unsupported profile state never becomes zero", () =>
        {
            foreach (var sample in new[]
            {
                Ready with { PlayerAddress = 0 }, Ready with { ProfileAddress = 0 }, Ready with { AgentAddress = 0 },
                Ready with { AddonAddress = 0 }, Ready with { ContentId = 0 }, Ready with { PlayerLoaded = false },
                Ready with { ProfileLoaded = false }, Ready with { MenuReady = false }, Ready with { AddonId = 0 },
                Ready with { AddonId = 65536 }, Ready with { GrandCompany = 0 }, Ready with { GrandCompany = 4 },
                Ready with { Series = 0 },
            })
            {
                var snapshot = Snapshot(sample);
                Check(!snapshot.Available && snapshot.Entries.Count == 0 && snapshot.Readiness == "unavailable");
            }
        });
        Test("changed owner source menu series GC or counters reject the entire copy", () =>
        {
            foreach (var changed in new[]
            {
                Ready with { PlayerAddress = 11 }, Ready with { ProfileAddress = 12 }, Ready with { AgentAddress = 13 },
                Ready with { AddonAddress = 14 }, Ready with { ContentId = 15 }, Ready with { PlayerLoaded = false },
                Ready with { ProfileLoaded = false }, Ready with { MenuReady = false }, Ready with { AddonId = 89 },
                Ready with { GrandCompany = 3 }, Ready with { Rank = 18 }, Ready with { Experience = 8124 },
                Ready with { Series = 13 }, Ready with { SeriesRank = 10 }, Ready with { SeriesExperience = 451 },
            }) Check(!PvpReader.Describe(Time, Ready, changed).Available);
        });
        Test("wire excludes local identities pointers period and unsupported reward fields", () =>
        {
            var result = new ProgressionLookup(Snapshot()).Query(Args("""{"category":"pvp"}"""));
            var json = JsonSerializer.Serialize(result);
            Check(result.observed_count == 2 && result.completed_count == 0 && result.not_completed_count == 0);
            foreach (var forbidden in new[] { "987654321", "content_id", "ContentId", "Address", "series_id", "claimed_rank", "season" })
                Check(!json.Contains(forbidden, StringComparison.OrdinalIgnoreCase) || forbidden == "season" && json.Contains("seasonal leaderboard"));
            Check(result.entries.All(row => row.completed is null && row.level is null && row.progress is null && row.required is null));
        });
        Test("lookup filters are limited to known profile rows", () =>
        {
            var lookup = new ProgressionLookup(Snapshot());
            Check(lookup.Query(Args("""{"category":"pvp","ids":[2]}""")).entries.Single().id == 2);
            Check(lookup.Query(Args("""{"category":"pvp","query":"cumulative"}""")).entries.Single().id == 1);
            Check(lookup.Query(Args("""{"category":"pvp","offset":1,"limit":1}""")).entries.Single().id == 2);
            Throws(() => lookup.Query(Args("""{"category":"pvp","state":"completed"}""")));
        });
        Test("shared snapshot rejects malformed profile contracts", () =>
        {
            var rows = Snapshot().Entries.ToArray();
            foreach (var bad in new[]
            {
                rows[0] with { Rank = -1 }, rows[0] with { Rank = 256 }, rows[0] with { Experience = null },
                rows[0] with { Completed = true }, rows[0] with { Required = 5 }, rows[0] with { Level = 1 },
                rows[0] with { ExperienceRequired = 5 }, rows[0] with { Id = 3 },
            }) Throws(() => new ProgressionSnapshot("pvp", Time, true, "visible_menu_observation", [bad, rows[1]]));
            Throws(() => new ProgressionSnapshot("pvp", Time, true, "visible_menu_observation", [rows[0]]));
            Throws(() => new ProgressionSnapshot("pvp", Time, true, "visible_menu_observation", [rows[0], rows[1] with { Experience = 65536 }]));
        });
        Test("cache requires two sufficiently spaced equal observations", () =>
        {
            var cache = new RecentPvpCache();
            Check(!cache.Observe(Snapshot()));
            Check(!cache.Freeze(Time).Available);
            Check(!cache.Observe(Snapshot(time: Time.AddMilliseconds(249))));
            Check(!cache.Freeze(Time.AddMilliseconds(249)).Available);
            Check(cache.Observe(Snapshot(time: Time.AddMilliseconds(500))));
            Check(cache.Freeze(Time.AddMilliseconds(500)).Readiness == "last_observed_profile");
        });
        Test("closing or unloading preserves last known profile without refreshing age", () =>
        {
            var cache = SavedCache();
            Check(!cache.Observe(ProgressionSnapshot.Unavailable("pvp", "Synthetic closed menu")));
            var frozen = cache.Freeze(Time.AddMinutes(2));
            Check(frozen.Available && frozen.CapturedAtUtc == Time.AddSeconds(1) && frozen.MenuPage is null);
            Check(frozen.Readiness == "last_observed_profile");
            Check(!cache.Observe(Snapshot(time: Time.AddMinutes(2))));
        });
        Test("retention expires exactly at fifteen minutes and rejects backward time", () =>
        {
            var cache = SavedCache();
            Check(cache.HasRecent(Time.AddSeconds(1).AddMinutes(15).AddTicks(-1)));
            Check(!cache.Freeze(Time.AddSeconds(1).AddMinutes(15)).Available);
            Check(!cache.Freeze(Time).Available);
        });
        Test("reset clears saved and candidate while candidate-only reset keeps saved age", () =>
        {
            var cache = SavedCache();
            cache.ResetCandidate();
            Check(cache.Freeze(Time.AddSeconds(2)).Available);
            Check(!cache.Observe(Snapshot(time: Time.AddSeconds(3))));
            cache.Reset();
            Check(!cache.Freeze(Time.AddSeconds(3)).Available);
            Check(!cache.Observe(Snapshot(time: Time.AddSeconds(4))));
        });
        Test("changed counters need a fresh second observation and saved input is rejected", () =>
        {
            var cache = SavedCache();
            Check(!cache.Observe(Snapshot(Ready with { Experience = 9000 }, Time.AddSeconds(2))));
            Check(cache.Freeze(Time.AddSeconds(2)).Entries[0].Experience == 8123);
            Check(cache.Observe(Snapshot(Ready with { Experience = 9000 }, Time.AddSeconds(3))));
            Check(cache.Freeze(Time.AddSeconds(3)).Entries[0].Experience == 9000);
            Throws(() => cache.Observe(cache.Freeze(Time.AddSeconds(3))));
            Throws(() => cache.Observe(ProgressionSnapshot.Unavailable("achievements", "Synthetic other category")));
        });
        Console.WriteLine($"Passed {passed} PvP checks.");
    }

    private static RecentPvpCache SavedCache()
    {
        var cache = new RecentPvpCache();
        Check(!cache.Observe(Snapshot()));
        Check(cache.Observe(Snapshot(time: Time.AddSeconds(1))));
        return cache;
    }

    internal static void Produce(string output)
    {
        var queries = new[] { """{"category":"pvp"}""", """{"category":"pvp","ids":[1]}""",
            """{"category":"pvp","ids":[2]}""", """{"category":"pvp","query":"cumulative"}""",
            """{"category":"pvp","state":"observed","offset":1,"limit":1}""", """{"category":"pvp","limit":1}""" };
        var fixtures = queries.Select((query, i) => new { name = "pvp_" + i, arguments = Args(query),
            result = new ProgressionLookup(Snapshot()).Query(Args(query)) }).ToList();
        foreach (var item in new[]
        {
            ("pvp_zero", Snapshot(Ready with { Rank = 0, Experience = 0, SeriesRank = 0, SeriesExperience = 0 })),
            ("pvp_maximum", Snapshot(Ready with { Rank = 255, Experience = uint.MaxValue, SeriesRank = 255, SeriesExperience = ushort.MaxValue })),
            ("pvp_unavailable", ProgressionSnapshot.Unavailable("pvp", "Synthetic unloaded profile")),
            ("pvp_last_observed", SavedCache().Freeze(Time.AddMinutes(2))),
        }) fixtures.Add(new { name = item.Item1, arguments = Args(queries[0]),
            result = new ProgressionLookup(item.Item2).Query(Args(queries[0])) });
        var full = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, JsonSerializer.Serialize(fixtures, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    private static void Throws(Action action)
    {
        try { action(); } catch (ArgumentException) { return; } catch (InvalidOperationException) { return; }
        throw new Exception("Expected invalid input to fail closed");
    }
}
