using System.Collections.Immutable;
using System.Text.Json;
using CompAI;

internal static class ProgressionCacheChecks
{
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-10-20T12:00:00Z");
    private static ProgressionSnapshot Fate(DateTimeOffset at, int page = 0, uint id = 813, int progress = 2) =>
        ProgressionReader.DescribeSharedFates(at, [new(id, "Synthetic Zone", 2, progress, 60)], menuPage: page);
    private static ProgressionMenuCapture Page(DateTimeOffset at, int page = 0, uint id = 813, int progress = 2) =>
        ProgressionMenuCache.FromSnapshot(Fate(at, page, id, progress))!;
    private static void Check(bool value) { if (!value) throw new Exception("Saved progression assertion failed."); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidDataException) { return; }
        throw new Exception("Malformed saved progression was accepted.");
    }

    internal static void Settling()
    {
        var tracker = new ProgressionMenuTracker();
        Check(tracker.Observe(Fate(Time)) is null);
        Check(tracker.Observe(Fate(Time.AddMilliseconds(100))) is null);
        Check(tracker.Observe(Fate(Time.AddMilliseconds(500))) is not null);
        Check(tracker.Observe(Fate(Time.AddSeconds(1), progress: 3)) is null);
        Check(tracker.Observe(Fate(Time.AddSeconds(1.5), progress: 3)) is not null);
        Check(tracker.Observe(ProgressionSnapshot.Unavailable("shared_fates", "Menu closed.")) is null);
        Check(tracker.Observe(Fate(Time.AddSeconds(2), progress: 3)) is null);
        tracker.Reset(); // Permission, identity and zone transitions reset this same tracker.
        Check(tracker.Observe(Fate(Time.AddSeconds(3), progress: 3)) is null);
        Check(tracker.Observe(Fate(Time.AddSeconds(3.5), page: 1, id: 956)) is null);
        Check(tracker.Observe(Fate(Time.AddSeconds(4), page: 1, id: 956)) is not null);
    }

    internal static ProgressionSnapshot SavedFates() => ProgressionMenuCache.Freeze(
        [Page(Time), Page(Time.AddDays(1), 1, 956)], "shared_fates");
    internal static ProgressionSnapshot SavedTrust()
    {
        var first = ProgressionMenuCache.FromSnapshot(ProgressionReader.DescribeTrust(Time,
            [new(1, "Synthetic Member", 71, 1, 100), new(2, "Synthetic Older Member", 72, 2, 100)], menuPage: 0))!;
        var second = ProgressionMenuCache.FromSnapshot(ProgressionReader.DescribeTrust(Time.AddDays(1),
            [new(1, "Synthetic Member", 72, 3, 100)], menuPage: 1))!;
        return ProgressionMenuCache.Freeze([first, second], "trust");
    }

    internal static void MergeAndFreeze()
    {
        ImmutableArray<ProgressionMenuCapture> pages = [Page(Time)];
        Check(ProgressionMenuCache.Merge(pages, Page(Time.AddSeconds(30))) == pages);
        Check(ProgressionMenuCache.Merge(pages, Page(Time.AddSeconds(-1), progress: 4)) == pages);
        var refreshed = ProgressionMenuCache.Merge(pages, Page(Time.AddSeconds(61)));
        Check(refreshed[0].CapturedAtUtc == Time.AddSeconds(61) && pages[0].CapturedAtUtc == Time);
        var changed = ProgressionMenuCache.Merge(pages, Page(Time.AddSeconds(1), progress: 4));
        Check(changed[0].Entries[0].Progress == 4 && pages[0].Entries[0].Progress == 2);
        var saved = SavedFates();
        Check(saved.SavedMenu && saved.Entries.Count == 2 && saved.CapturedAtUtc == Time.AddDays(1));
        Check(saved.Entries[0].ObservedAtUtc == Time && saved.Entries[0].ObservedPage == 0);
        Check(saved.Entries[1].ObservedAtUtc == Time.AddDays(1) && saved.Entries[1].ObservedPage == 1);
        var trust = SavedTrust();
        Check(trust.Entries.Count == 2 && trust.Entries[0].Level == 72 && trust.Entries[0].ObservedPage == 1 &&
            trust.Entries[1].ObservedAtUtc == Time);
        Check(!ProgressionMenuCache.Freeze([], "trust").Available);
        Check(ProgressionMenuCache.FromSnapshot(ProgressionSnapshot.Unavailable("trust", "Closed")) is null);
    }

    internal static void ValidationAndWire()
    {
        var page = Page(Time);
        foreach (var invalid in new[] { page with { PageIndex = 3 }, page with { CapturedAtUtc = default },
            page with { Category = "achievements" }, page with { Entries = [] },
            page with { Entries = [page.Entries[0], page.Entries[0]] },
            page with { Entries = [page.Entries[0] with { ObservedAtUtc = Time, ObservedPage = 0 }] },
            page with { Entries = [page.Entries[0] with { Rank = 0 }] } })
            Reject(() => ProgressionMenuCache.Validate([invalid]));
        Reject(() => ProgressionMenuCache.Validate([page, page]));
        Reject(() => ProgressionMenuCache.Validate(default));
        var args = JsonDocument.Parse("{\"category\":\"shared_fates\"}").RootElement;
        var live = JsonSerializer.Serialize(new ProgressionLookup(Fate(Time)).Query(args));
        Check(!live.Contains("observed_at_utc") && !live.Contains("observed_page"));
        var saved = JsonSerializer.Serialize(new ProgressionLookup(SavedFates()).Query(args));
        Check(saved.Contains("saved_menu_observations") && saved.Contains("observed_at_utc") && saved.Contains("observed_page"));
        Check(saved.Contains("Saved local ") && saved.Contains("Last known", StringComparison.OrdinalIgnoreCase));
    }

    internal static ProgressionSnapshot SavedHunting()
    {
        var first = new ProgressionMenuCapture("hunting_log", 0, Time,
            [new(10001, "Synthetic hunting I", "not_observed_complete", Rank: 1, Progress: 2, Required: 5, UnknownReason: "No completion established.")]);
        var second = new ProgressionMenuCapture("hunting_log", 5, Time.AddSeconds(1),
            [new(20001, "Synthetic hunting II", "completed", Completed: true, Rank: 1)]);
        return ProgressionMenuCache.Freeze(ProgressionMenuCache.Merge([first], second), "hunting_log", Time.AddMinutes(2));
    }

    internal static ProgressionSnapshot SavedChallenge(DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        return ProgressionMenuCache.Freeze([
            new("challenge_log", 0, at.AddSeconds(-1), [new(1, "Synthetic battle", "completed", Completed: true)]),
            new("challenge_log", 10, at, [new(47, "Synthetic Verminion", "not_observed_complete", Progress: 1, Required: 3, UnknownReason: "No positive flag.")])],
            "challenge_log", at);
    }

    internal static void LogRetention()
    {
        var hunt = SavedHunting();
        Check(hunt.Entries.Count == 2 && hunt.Entries[0].Progress == 2 && hunt.Entries[1].ObservedPage == 5);
        var one = new ProgressionMenuCapture("hunting_log", 0, Time,
            [new(10001, "Synthetic hunting", "completed", Completed: true, Rank: 1)]);
        Reject(() => ProgressionMenuCache.Validate([one with { PageIndex = 5 }]));
        Reject(() => ProgressionMenuCache.Validate([one with { Entries = [one.Entries[0] with { Rank = 2 }] }]));
        Check(ProgressionMenuCache.Freeze([one], "hunting_log", Time.AddYears(1)).Available);
        var challenge = new ProgressionMenuCapture("challenge_log", 2, Time,
            [new(47, "Synthetic challenge", "completed", Completed: true, Progress: 3, Required: 3)]);
        Check(ProgressionMenuCache.Freeze([challenge], "challenge_log", Time.AddMinutes(14)).Available);
        Check(!ProgressionMenuCache.Freeze([challenge], "challenge_log", Time.AddMinutes(15)).Available);
        Check(!ProgressionMenuCache.Freeze([challenge], "challenge_log", Time.AddSeconds(-1)).Available);
        var reset = DateTimeOffset.Parse("2026-10-27T08:00:00Z");
        var nearReset = challenge with { CapturedAtUtc = reset.AddSeconds(-1) };
        Check(ProgressionMenuCache.IsUsable(nearReset, reset.AddMilliseconds(-1)));
        Check(!ProgressionMenuCache.IsUsable(nearReset, reset));
        ProgressionSnapshot Visible(DateTimeOffset at) => new("challenge_log", at, true, "visible_menu_observation", nearReset.Entries, menuPage: 2);
        var tracker = new ProgressionMenuTracker();
        Check(tracker.Observe(Visible(reset.AddSeconds(-2))) is null);
        Check(tracker.Observe(Visible(reset.AddSeconds(-1))) is not null);
        Check(tracker.Observe(Visible(reset)) is null && tracker.Observe(Visible(reset.AddSeconds(1))) is null);
        tracker.Observe(ProgressionSnapshot.Unavailable("challenge_log", "Menu closed"));
        Check(tracker.Observe(Visible(reset.AddSeconds(2))) is null);
        Check(tracker.Observe(Visible(reset.AddSeconds(3))) is not null);
        Check(ProgressionMenuCache.Prune([one, nearReset], reset).SequenceEqual([one]));
        var merged = SavedChallenge(Time);
        Check(merged.Entries.Count == 2 && merged.Entries[0].Completed == true && merged.Entries[1].Progress == 1);
        Check(merged.Entries[1].Completed is null && merged.Entries[1].ObservedPage == 10);
    }

    internal static ProgressionSnapshot SavedAchievementProgress(DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var cache = new RecentAchievementProgressCache();
        foreach (var id in new uint[] { 1, 2 })
        {
            cache.Observe(new("achievement_progress", at.AddSeconds(-2), true, "loaded_progress_record",
                [new(id, "Synthetic achievement " + id, "observed", Progress: id, Required: 10)]));
            cache.Observe(new("achievement_progress", at.AddSeconds(-1), true, "loaded_progress_record",
                [new(id, "Synthetic achievement " + id, "observed", Progress: id, Required: 10)]));
        }
        return cache.Freeze(at);
    }

    internal static void RecentAchievements()
    {
        var cache = new RecentAchievementProgressCache();
        ProgressionSnapshot Row(DateTimeOffset at, uint id = 1) => new("achievement_progress", at, true, "loaded_progress_record",
            [new(id, "Synthetic achievement " + id, "observed", Progress: 4, Required: 10)]);
        Check(!cache.Observe(Row(Time)) && !cache.HasRecent(Time));
        Check(cache.Observe(Row(Time.AddSeconds(1))) && cache.HasRecent(Time.AddSeconds(1)));
        cache.Observe(ProgressionSnapshot.Unavailable("achievement_progress", "Menu closed"));
        Check(cache.HasRecent(Time.AddMinutes(14)) && !cache.HasRecent(Time.AddMinutes(16)));
        Check(cache.Freeze(Time.AddMinutes(14)).Entries.Single().Progress == 4);
        Check(!cache.Freeze(Time.AddMinutes(16)).Available && !cache.Freeze(Time.AddSeconds(-1)).Available);
        cache.ResetCandidate(); // A zone transition discards only the pending stability comparison.
        Check(cache.HasRecent(Time.AddMinutes(2)));
        Check(!cache.Observe(Row(Time.AddSeconds(2), 2)));
        Check(cache.Observe(Row(Time.AddSeconds(3), 2)));
        Check(cache.Freeze(Time.AddMinutes(1)).Entries.Select(entry => entry.Id).SequenceEqual([1u, 2u]));
        for (uint id = 3; id <= 70; id++)
        { cache.Observe(Row(Time.AddSeconds(id), id)); cache.Observe(Row(Time.AddSeconds(id + 0.5), id)); }
        Check(cache.Freeze(Time.AddMinutes(2)).Entries.Count == 64);
        cache.Clear();
        Check(!cache.HasRecent(Time.AddMinutes(2)));
    }
}
