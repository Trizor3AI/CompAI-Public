using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using CompAI;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

var libraryPath = Environment.GetEnvironmentVariable("DALAMUD_LIB_PATH") ??
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "addon", "Hooks", "dev");
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var path = Path.Combine(libraryPath, name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
Checks.Run();
if (args is ["--produce-fixtures", var output]) Checks.Produce(output);
if (args is ["--produce-fixtures", var publicOutput, "--verify-public-catalog", var sqpack])
{
    AetherCurrentChecks.VerifyPublicCatalog(sqpack);
    Checks.Produce(publicOutput);
}

internal static class Checks
{
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-10-20T12:00:00Z");
    private static int passed;
    internal static void Run()
    {
        Test("pinned fixed-array bounds", NativeBounds);
        AetherCurrentChecks.Run(Test);
        Test("saved pages settle before capture and hidden or revoked reads reset candidates", ProgressionCacheChecks.Settling);
        Test("saved pages preserve coverage timestamps and latest member observations", ProgressionCacheChecks.MergeAndFreeze);
        Test("saved observations reject malformed pages and keep optional wire fields separate", ProgressionCacheChecks.ValidationAndWire);
        Test("saved hunting pages merge independently and challenge tabs expire at TTL and weekly reset", ProgressionCacheChecks.LogRetention);
        Test("recent achievement records retain source IDs across closure and expire within the character session", ProgressionCacheChecks.RecentAchievements);
        Test("saved plugin queries stay offline frozen character-bound and revocable", Plugin.CheckSavedProgressionGuards);
        Test("saved log and recent achievement queries preserve expiry ownership clear and zone boundaries", Plugin.CheckSavedLogAndRecentGuards);
        Test("cached PvP queries survive menu closure but reject expiry owner changes and revoked callbacks", Plugin.CheckRecentPvpQueryGuards);
        Test("aether zone IDs include final row without direct bit indexing", () =>
        {
            var calls = new List<uint>();
            var currentCalls = new List<uint>();
            var result = ProgressionReader.DescribeAetherCurrents(Time,
                [new(1, "Synthetic North", [2818048]), new(31, "Synthetic South", [2818488]), new(32, "Future Zone")],
                id => { calls.Add(id); return id == 1; },
                id => { currentCalls.Add(id); return id == 2818048; });
            Check(calls.SequenceEqual([1u, 31u]));
            Check(currentCalls.SequenceEqual([2818048u, 2818488u]));
            Check(result.Entries.Select(row => row.State).SequenceEqual(["completed", "not_observed_complete", "unknown"]));
            Check(result.Entries[1].Completed is null && result.Entries[2].Completed is null);
            Check(result.Entries[1].Progress == 0 && result.Entries[1].Required == 1 && result.Entries[2].Progress is null);
        });
        Test("unloaded aether source never invokes native callback", () =>
        {
            var result = ProgressionReader.DescribeAetherCurrents(Time, [new(1, "Synthetic Zone")],
                _ => throw new Exception("Called while unloaded"), _ => throw new Exception("Called while unloaded"), loaded: false);
            Check(!result.Available && result.Entries.Count == 0);
        });
        Test("hidden menu sources expose no rows including synthetic nonzero leftovers", () =>
        {
            Check(!ProgressionReader.DescribeSharedFates(Time, [new(813, "Stale zone", 3, 0, 0)], false).Available);
            Check(!ProgressionReader.DescribeTrust(Time, [new(1, "Stale member", 100, 0, 0)], false).Available);
        });
        Test("Shared FATE rank zero is unavailable while loaded zero progress is observable", () =>
        {
            Check(!ProgressionReader.DescribeSharedFates(Time, [new(813, "Synthetic Zone", 0, 0, 6)]).Available);
            var observed = Fates();
            Check(observed.Available && observed.Entries[0].Progress == 0 && observed.Entries[0].Rank == 1);
        });
        Test("Shared FATE invalid counts and duplicate territory IDs fail closed", () =>
        {
            Check(!ProgressionReader.DescribeSharedFates(Time, [new(1, "Synthetic", 2, 7, 6)]).Available);
            Check(!ProgressionReader.DescribeSharedFates(Time, [new(1, "First", 1, 0, 6), new(1, "Again", 2, 0, 60)]).Available);
            Check(!ProgressionReader.DescribeSharedFates(Time,
                Enumerable.Range(1, 7).Select(id => new FateProgressObservation((uint)id, "Synthetic", 1, 0, 6))).Available);
        });
        Test("Shared FATE public thresholds define rank caps without native MaxRank", () =>
        {
            Check(ProgressionReader.DefineFateZone(813, "Synthetic ShB", 6, 60, 0).MaxRank == 3);
            Check(ProgressionReader.DefineFateZone(956, "Synthetic EW", 6, 60, 0).MaxRank == 3);
            Check(ProgressionReader.DefineFateZone(1187, "Synthetic DT", 6, 20, 40).MaxRank == 4);
            foreach (var ranks in new[] { (0, 0, 0), (0, 60, 0), (6, 0, 40) })
                Throws<InvalidOperationException>(() => ProgressionReader.DefineFateZone(813, "Synthetic", (byte)ranks.Item1, (byte)ranks.Item2, (byte)ranks.Item3));
            Throws<InvalidOperationException>(() => ProgressionReader.DefineFateZone(0, "Synthetic", 6, 60, 0));
            Throws<InvalidOperationException>(() => ProgressionReader.DefineFateZone(813, "", 6, 60, 0));
        });
        Test("Shared FATE capped native rows accept zero MaxRank and empty next-rank text", () =>
        {
            var maxed = NativeMaxedFates();
            Check(maxed.Available && maxed.Entries.Count == 6 && maxed.Entries.All(row =>
                row.Rank == 3 && row.Progress == 0 && row.Required == 0));
            var dawntrail = NativeFate(1187, 4, 0, 0, "Rank 4", "", 4);
            Check(dawntrail.Rank == 4 && dawntrail.Progress == 0 && dawntrail.Required == 0);
            var retainedCounts = NativeFate(813, 3, 60, 60, "Rank 3", "");
            Check(retainedCounts.Progress == 60 && retainedCounts.Required == 60);
            var progressing = NativeFate(813, 2, 17, 60, "Rank 2", "17/60");
            Check(progressing.Progress == 17 && progressing.Required == 60);
        });
        Test("Shared FATE native rows still reject unpopulated text and unsupported numeric state", () =>
        {
            Throws<InvalidOperationException>(() => NativeFate(813, 0, 0, 0, "", ""));
            Throws<InvalidOperationException>(() => NativeFate(813, 3, 0, 0, "", ""));
            Throws<InvalidOperationException>(() => NativeFate(813, 2, 0, 60, "Rank 2", ""));
            Throws<InvalidOperationException>(() => NativeFate(813, 4, 0, 0, "Rank 4", ""));
            Throws<InvalidOperationException>(() => NativeFate(813, 3, 1, 0, "Rank 3", ""));
            Throws<InvalidOperationException>(() => NativeFate(813, 3, 0, 0, "Rank 3", new string('x', 129)));
            Throws<InvalidOperationException>(() => NativeFate(813, 3, 0, 0, "Rank 3", "bad\u0001text"));
        });
        Test("Trust unpopulated levels and impossible experience fail closed", () =>
        {
            Check(!ProgressionReader.DescribeTrust(Time, [new(1, "Synthetic", 0, 0, 0)]).Available);
            Check(!ProgressionReader.DescribeTrust(Time, [new(1, "Synthetic", 71, 101, 100)]).Available);
            Check(Trust().Entries[0].Experience == 0 && Trust().Entries[0].Level == 71);
        });
        Test("Trust duplicate member IDs and excess groups fail closed", () =>
        {
            Check(!ProgressionReader.DescribeTrust(Time, [new(1, "One", 71, 0, 100), new(1, "Again", 72, 1, 100)]).Available);
            Check(!ProgressionReader.DescribeTrust(Time,
                Enumerable.Range(1, 13).Select(id => new TrustProgressObservation((uint)id, "Synthetic", 71, 0, 100))).Available);
        });
        Test("snapshot rejects invented negative and mixed-category numeric fields", () =>
        {
            Throws<ArgumentException>(() => new ProgressionSnapshot("aether_currents", Time, true, "positive_observations_only",
                [new(1, "Synthetic", "not_observed_complete", Completed: false, UnknownReason: "Unknown")]));
            Throws<ArgumentException>(() => new ProgressionSnapshot("shared_fates", Time, true, "visible_menu_observation",
                [new(1, "Synthetic", "observed", Rank: 1, Progress: 0, Required: 6, Level: 71)]));
            Throws<ArgumentException>(() => new ProgressionSnapshot("trust", Time, true, "visible_menu_observation",
                [new(1, "Synthetic", "unknown", Level: 71, UnknownReason: "Unknown")]));
        });
        Test("snapshot copies entries and normalizes time", () =>
        {
            var source = new List<ProgressionEntry> { new(2, "Second", "completed", true), new(1, "First", "not_completed", false) };
            var snapshot = new ProgressionSnapshot("achievements", Time.ToOffset(TimeSpan.FromHours(3)), true, "loaded_completion_bitmap", source);
            source.Clear();
            Check(snapshot.Entries.Count == 2 && snapshot.Entries[0].Id == 1 && snapshot.CapturedAtUtc.Offset == TimeSpan.Zero);
        });
        Test("query filters compose and counts remain full snapshot counts", () =>
        {
            var result = new ProgressionLookup(Currents()).Query(Args("""{"category":"aether_currents","query":"synthetic north","ids":[1,2]}"""));
            Check(result.entries.Length == 1 && result.entries[0].id == 1 && result.catalog_count == 3 &&
                result.completed_count == 1 && result.not_observed_complete_count == 1 && result.unknown_count == 1);
        });
        Test("query pagination progresses and exact IDs do not become native reads", () =>
        {
            var lookup = new ProgressionLookup(Currents());
            var first = lookup.Query(Args("""{"category":"aether_currents","limit":1}"""));
            var last = lookup.Query(Args("""{"category":"aether_currents","offset":2,"limit":1}"""));
            Check(first.next_offset == 1 && first.truncated && last.next_offset is null && !last.truncated);
            Check(lookup.Query(Args("""{"category":"aether_currents","ids":[4294967295]}""")).entries.Length == 0);
        });
        Test("unavailable counts remain null and no data is synthesized", () =>
        {
            var result = new ProgressionLookup(ProgressionSnapshot.Unavailable("trust", "Menu closed."))
                .Query(Args("""{"category":"trust","offset":10}"""));
            Check(!result.available && result.catalog_count is null && result.observed_count is null &&
                result.unknown_count is null && result.entries.Length == 0 && result.matched_count == 0);
        });
        Test("strict query validation rejects duplicates cross-category states and malformed bounds", () =>
        {
            foreach (var json in new[] { """{}""", """{"category":"trust","category":"trust"}""",
                """{"category":"trust","state":"completed"}""", """{"category":"aether_currents","state":"not_completed"}""",
                """{"category":"shared_fates","ids":[1,1]}""", """{"category":"trust","ids":[0]}""",
                """{"category":"trust","limit":51}""", """{"category":"trust","offset":8193}""",
                """{"category":"trust","extra":true}""", """{"category":"mount_speed"}""" })
                Throws<ArgumentException>(() => ProgressionLookup.ValidateArguments(Args(json)));
        });
        Test("Unicode output is byte-bounded and continuation has no gaps", () =>
        {
            var lookup = new ProgressionLookup(Large());
            var first = lookup.Query(Args("""{"category":"achievements"}"""));
            Check(JsonSerializer.SerializeToUtf8Bytes(first).Length <= ProgressionLookup.MaxBytes &&
                first.truncated && first.entries.Length > 0 && first.next_offset == first.entries.Length);
            var next = lookup.Query(Args($$"""{"category":"achievements","offset":{{first.next_offset}}}"""));
            Check(next.entries[0].id == first.entries[^1].id + 1);
        });
        Console.WriteLine($"PASS {passed} progression checks");
    }

    private static unsafe void NativeBounds()
    {
        PlayerState player = default;
        AgentFateProgress fate = default;
        AgentDawnInterface.DawnMemberData trust = default;
        Check(player.UnlockedAetherCurrentCompFlgSetsBitArray.BitCount == ProgressionReader.AetherZoneBitCount);
        Check(player.UnlockedAetherCurrentsBitArray.BitCount == ProgressionReader.AetherCurrentBitCount &&
            player.UnlockedAetherCurrents.Length == ProgressionReader.AetherCurrentByteCount &&
            player.UnlockedAetherCurrentCompFlgSets.Length == ProgressionReader.AetherZoneByteCount);
        Check(fate.Tabs.Length == ProgressionReader.FateTabCount && fate.Tabs[0].Zones.Length == ProgressionReader.FateZoneCount);
        Check(trust.MemberCounts.Length == ProgressionReader.TrustGroupCount &&
            trust.Members.Length == ProgressionReader.TrustGroupCount * ProgressionReader.TrustMembersPerGroup);
    }

    private static ProgressionSnapshot Currents() => ProgressionReader.DescribeAetherCurrents(Time,
        [new(1, "Synthetic North", [2818048, 2818051]), new(2, "Synthetic South", [2818063, 2818064, 2818068]),
            new(32, "Future Synthetic Zone")], id => id == 1, id => id is 2818048 or 2818051 or 2818064);
    private static ProgressionSnapshot Fates() => ProgressionReader.DescribeSharedFates(Time,
        [new(813, "Synthetic Zone", 1, 0, 6), new(815, "Synthetic Max Zone", 3, 0, 0)]);
    private static ProgressionSnapshot NativeMaxedFates() => ProgressionReader.DescribeSharedFates(Time,
        Enumerable.Range(813, 6).Select(id => NativeFate((uint)id, 3, 0, 0, "Rank 3", "")));
    private static unsafe FateProgressObservation NativeFate(uint id, byte rank, ushort progress, ushort needed,
        string rankText, string progressText, int cap = 3)
    {
        var rankBytes = Encoding.UTF8.GetBytes(rankText + "\0");
        var progressBytes = Encoding.UTF8.GetBytes(progressText + "\0");
        fixed (byte* rankBuffer = rankBytes, progressBuffer = progressBytes)
        {
            FateProgressZone zone = default;
            zone.TerritoryTypeId = id;
            zone.CurrentRank = rank;
            zone.MaxRank = 0;
            zone.FateProgress = progress;
            zone.NeededFates = needed;
            zone.RankText.StringPtr = rankBuffer;
            zone.RankText.StringLength = rankBytes.Length - 1;
            zone.RankText.BufUsed = zone.RankText.BufSize = rankBytes.Length;
            zone.ProgressText.StringPtr = progressBuffer;
            zone.ProgressText.StringLength = progressBytes.Length - 1;
            zone.ProgressText.BufUsed = zone.ProgressText.BufSize = progressBytes.Length;
            return ProgressionReader.ReadFateZone(&zone, new(id, "Synthetic Zone " + id, cap));
        }
    }
    private static ProgressionSnapshot Trust() => ProgressionReader.DescribeTrust(Time,
        [new(1, "Synthetic Member", 71, 0, 100), new(2, "Synthetic Max Member", 100, 0, 0)]);
    private static ProgressionSnapshot Large() => new("achievements", Time, true, "loaded_completion_bitmap",
        Enumerable.Range(1, 100).Select(id => new ProgressionEntry((uint)id, new string('語', 128), "unknown",
            UnknownReason: new string('語', 256))));
    internal static void Produce(string output)
    {
        object Fixture(string name, ProgressionSnapshot snapshot, string json)
        {
            var arguments = Args(json);
            return new { name, arguments, result = new ProgressionLookup(snapshot).Query(arguments) };
        }
        var fixtures = new[]
        {
            Fixture("achievements", AchievementReader.Describe(Time, [new(1, "Synthetic Completed"), new(2, "Synthetic Incomplete"),
                new(4078, "Unsupported Synthetic")], id => id == 1), """{"category":"achievements"}"""),
            Fixture("aether_currents", Currents(), """{"category":"aether_currents"}"""),
            Fixture("aether_negative", Currents(), """{"category":"aether_currents","state":"not_observed_complete"}"""),
            Fixture("aether_zero_positive", AetherCurrentChecks.Counts(false, _ => false), """{"category":"aether_currents"}"""),
            Fixture("aether_all_positive_unconfirmed", AetherCurrentChecks.Counts(false, _ => true), """{"category":"aether_currents"}"""),
            Fixture("aether_inconsistent_unavailable", AetherCurrentChecks.Counts(true, id => id == 2818048), """{"category":"aether_currents"}"""),
            Fixture("shared_fates", Fates(), """{"category":"shared_fates"}"""),
            Fixture("shared_fates_maxed_native", NativeMaxedFates(), """{"category":"shared_fates"}"""),
            Fixture("shared_fates_saved", ProgressionCacheChecks.SavedFates(), """{"category":"shared_fates"}"""),
            Fixture("shared_fates_saved_filtered", ProgressionCacheChecks.SavedFates(), """{"category":"shared_fates","ids":[813]}"""),
            Fixture("trust_saved", ProgressionCacheChecks.SavedTrust(), """{"category":"trust"}"""),
            Fixture("hunting_saved", ProgressionCacheChecks.SavedHunting(), """{"category":"hunting_log"}"""),
            Fixture("challenge_saved", ProgressionCacheChecks.SavedChallenge(), """{"category":"challenge_log"}"""),
            Fixture("achievement_progress_saved", ProgressionCacheChecks.SavedAchievementProgress(), """{"category":"achievement_progress"}"""),
            Fixture("trust", Trust(), """{"category":"trust"}"""),
            Fixture("first_page", Currents(), """{"category":"aether_currents","limit":1}"""),
            Fixture("last_page", Currents(), """{"category":"aether_currents","offset":2,"limit":2}"""),
            Fixture("no_match", Currents(), """{"category":"aether_currents","ids":[4294967295]}"""),
            Fixture("unavailable", ProgressionSnapshot.Unavailable("trust", "Menu is closed."), """{"category":"trust","offset":10}"""),
            Fixture("unicode_bound", Large(), """{"category":"achievements"}"""),
        };
        var path = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(fixtures, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static JsonElement Args(string text) => JsonDocument.Parse(text).RootElement.Clone();
    private static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
    private static void Test(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}
