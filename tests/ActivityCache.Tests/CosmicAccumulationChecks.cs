namespace CompAI;

public sealed partial class Plugin
{
    private static ActivitySnapshot CosmicView(DateTimeOffset time, params (uint Mission, int Job, string Medal)[] rows) =>
        new("cosmic_missions", time, true, rows.Select((row, index) =>
            new ActivityEntry((uint)index + 1, $"class_job_{row.Job}", $"Synthetic job {row.Job}: mission {row.Mission}",
                Text: row.Medal, ReferenceId: row.Mission, ReferenceKind: "cosmic_mission", Slot: row.Job)));

    private static void SettleCosmic(ActivityObservationCache cache, DateTimeOffset time,
        params (uint Mission, int Job, string Medal)[] rows)
    {
        cache.Observe(CosmicView(time.AddMilliseconds(-250), rows), replaceSelection: true);
        Check(cache.Observe(CosmicView(time, rows), replaceSelection: true), "Cosmic stable pair merges");
    }

    internal static void RunCosmicAccumulationChecks()
    {
        const string category = "cosmic_missions";
        var start = ObservationTime;
        var cache = new ActivityObservationCache();
        Check(!cache.Observe(CosmicView(start, (20, 8, "Silver"))), "one Cosmic sample cannot publish");
        Check(!cache.Observe(CosmicView(start.AddMilliseconds(249), (20, 8, "Silver"))), "Cosmic settling minimum applies");
        Check(!cache.HasRecent(category, start.AddMilliseconds(249)), "unsettled Cosmic view is unavailable");
        SettleCosmic(cache, start.AddSeconds(1), (20, 8, "Silver"));
        var first = cache.Freeze(category, start.AddSeconds(1));
        SettleCosmic(cache, start.AddMinutes(1), (15, 18, "Gold"), (20, 9, "Silver"));
        var combined = cache.Freeze(category, start.AddMinutes(1));
        Check(combined.LastObserved && combined.CapturedAtUtc == start.AddSeconds(1) && combined.Entries.Count == 3,
            "browsing retains earlier jobs at their original age");
        Check(combined.Entries.Select(row => (row.ReferenceId, row.Slot)).SequenceEqual(new (uint?, int?)[] { (15, 18), (20, 8), (20, 9) }) &&
            combined.Entries.Select(row => row.Id).SequenceEqual(new uint[] { 1, 2, 3 }),
            "Cosmic identity includes mission and job; frozen rows have deterministic sequential IDs");
        Check(first.Entries.Count == 1 && first.Entries[0].Text == "Silver" &&
            ((IList<ActivityEntry>)combined.Entries).IsReadOnly, "previous Cosmic freezes remain detached and immutable");
        SettleCosmic(cache, start.AddMinutes(2), (20, 8, "Gold"));
        Check(cache.Freeze(category, start.AddMinutes(2)).Entries.Single(row => row.ReferenceId == 20 && row.Slot == 8).Text == "Gold" &&
            first.Entries[0].Text == "Silver", "latest positive medal replaces only its key without changing a frozen answer");
        SettleCosmic(cache, start.AddMinutes(3));
        cache.Observe(ActivitySnapshot.Unavailable(category, "Synthetic unreadable view"), replaceSelection: true);
        Check(cache.Freeze(category, start.AddMinutes(3)).Entries.Count == 3,
            "empty or unreadable new views never clear accumulated positive evidence");
        Console.WriteLine("PASS Cosmic accumulation settles views, merges mission/job keys and retains detached positive evidence");

        cache = new();
        SettleCosmic(cache, start, (1, 8, "Silver"));
        SettleCosmic(cache, start.AddMinutes(10), (2, 18, "Gold"));
        var beforeExpiry = cache.Freeze(category, start.AddMinutes(15).AddTicks(-1));
        Check(beforeExpiry.Entries.Count == 2 && beforeExpiry.CapturedAtUtc == start, "new job does not refresh older rows");
        var afterExpiry = cache.Freeze(category, start.AddMinutes(15));
        Check(afterExpiry.Entries.Count == 1 && afterExpiry.Entries[0].ReferenceId == 2 &&
            afterExpiry.CapturedAtUtc == start.AddMinutes(10), "fresh freeze prunes only individually expired rows");
        Check(beforeExpiry.Entries.Count == 2, "expiry pruning cannot mutate an earlier freeze");
        cache.ResetCandidates();
        Check(!cache.Observe(CosmicView(start.AddMinutes(16), (3, 9, "Gold"))) &&
            cache.Freeze(category, start.AddMinutes(16)).Entries.Single().ReferenceId == 2,
            "candidate reset preserves saved evidence but requires a fresh stable pair");
        Check(!cache.HasRecent(category, start.AddMinutes(25)) && !cache.Freeze(category, start.AddMinutes(24)).Available,
            "expired Cosmic entries cannot be resurrected by a backward clock");
        SettleCosmic(cache, start.AddMinutes(30), (4, 9, "Gold"));
        Check(!cache.HasRecent(category, start.AddMinutes(30).AddTicks(-1)) &&
            !cache.HasRecent(category, start.AddMinutes(30)), "future Cosmic observations are pruned fail-closed");
        SettleCosmic(cache, start.AddMinutes(31));
        var empty = cache.Freeze(category, start.AddMinutes(31));
        Check(empty.Available && empty.LastObserved && empty.Entries.Count == 0, "verified empty view remains available without a negative medal claim");
        Check(!cache.HasRecent(category, start.AddMinutes(46)), "empty view evidence also expires");
        Console.WriteLine("PASS Cosmic timestamps expire per entry, preserve candidate resets and reject future observations");

        cache = new();
        for (var batch = 0; batch < 4; batch++)
            SettleCosmic(cache, start.AddSeconds(batch), Enumerable.Range(batch * 128 + 1, 128)
                .Select(id => ((uint)id, 8, "Silver")).ToArray());
        Check(cache.Freeze(category, start.AddSeconds(3)).Entries.Count == 512, "Cosmic accumulator admits multiple bounded views");
        SettleCosmic(cache, start.AddSeconds(4), (513, 8, "Gold"));
        var capped = cache.Freeze(category, start.AddSeconds(4));
        Check(capped.Entries.Count == 512 && capped.Entries[0].ReferenceId == 2 && capped.Entries[^1].ReferenceId == 513 &&
            capped.Entries[^1].Id == 512, "Cosmic cap evicts oldest entries with deterministic mission/job tie breaking");
        cache.Observe(CosmicView(start.AddSeconds(5), (514, 8, "Gold")));
        cache.Reset();
        Check(!cache.HasRecent(category, start.AddSeconds(6)) &&
            !cache.Observe(CosmicView(start.AddSeconds(6), (514, 8, "Gold"))), "identity/session reset clears accumulated and candidate evidence");
        Check(capped.Entries.Count == 512, "reset never mutates a detached frozen aggregate");
        Console.WriteLine("PASS Cosmic retention is capped at512 with deterministic eviction and complete reset");

        using var folder = new ActivityTestFolder();
        var store = new CompanionAccessSettingsStore(folder.File);
        Check(store.Save(new(CosmicMissions: true)), "persist synthetic Cosmic grant");
        var plugin = new Plugin(store) { activityOwner = 123 };
        SettleCosmic(plugin.activityObservations, start, (1, 8, "Silver"));
        SettleCosmic(plugin.activityObservations, start.AddMinutes(10), (2, 18, "Gold"));
        var clock = start.AddMinutes(11);
        var frozenQuery = plugin.CreateActivityQueryUnderLock(() => clock)!;
        clock = start.AddMinutes(15);
        Throws<IOException>(() => frozenQuery(Arguments(category)));
        Check(Available(plugin.CreateActivityQueryUnderLock(() => clock)!(Arguments(category))) &&
            plugin.activityObservations.Freeze(category, clock).Entries.Single().ReferenceId == 2,
            "frozen aggregate expires conservatively while a fresh question retains newer observations");
        plugin.activeContentId = 456;
        Throws<OperationCanceledException>(() => frozenQuery(Arguments(category)));
        Check(!Available(plugin.CreateActivityQueryUnderLock(() => clock)!(Arguments(category))),
            "different character cannot read accumulated evidence");
        plugin.activeContentId = 123;
        plugin.SetAccess(false, false, true, gameChatReplies: true);
        Check(Available(plugin.CreateActivityQueryUnderLock(() => clock)!(Arguments(category))) &&
            plugin.activityObservations.Freeze(category, clock).Entries.Single().Text == "Gold",
            "changing web or game-chat preferences preserves the observed Cosmic medal");
        plugin.SetAccess(false, false, false, cosmicMissions: false);
        plugin.SetAccess(false, false, false, cosmicMissions: true);
        Check(!plugin.activityObservations.HasRecent(category, clock), "permission revoke/regrant clears all accumulated evidence");
        Console.WriteLine("PASS Cosmic frozen queries preserve expiry, character and permission boundaries");
    }
}
