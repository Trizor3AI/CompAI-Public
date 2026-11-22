using System;
using System.Collections.Immutable;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CompAI;

// Managed page copies only. The surrounding CharacterCacheData supplies the ContentId.
internal sealed record ProgressionMenuCapture(string Category, int PageIndex, DateTimeOffset CapturedAtUtc,
    ImmutableArray<ProgressionEntry> Entries);

internal sealed record ProgressionClearRequest(ulong ContentId, int ContextEpoch, string Category);

internal static class ProgressionMenuCache
{
    internal const int MaxPages = 158; // 3 FATE tabs, 80 Trust groups, 60 hunting book/ranks and 15 challenge tabs.
    internal static bool IsMenu(string category) => category is "shared_fates" or "trust" or "hunting_log" or "challenge_log";
    internal static readonly TimeSpan ChallengeRetention = TimeSpan.FromMinutes(15);

    // Official 2.2 notes: Challenge Log resets Tuesday 08:00 GMT. This limits retention,
    // not proof that the client has received the reset.
    // https://de.finalfantasyxiv.com/lodestone/topics/detail/250fa2fc9b3709d18dca7ee733c851473cb34da5
    internal static DateTimeOffset ChallengeWeekStart(DateTimeOffset at)
    {
        var utc = at.ToUniversalTime();
        var day = new DateTimeOffset(utc.Year, utc.Month, utc.Day, 8, 0, 0, TimeSpan.Zero);
        var start = day.AddDays(-(((int)day.DayOfWeek - (int)DayOfWeek.Tuesday + 7) % 7));
        return start > utc ? start.AddDays(-7) : start;
    }

    internal static bool IsUsable(ProgressionMenuCapture page, DateTimeOffset now) =>
        page.CapturedAtUtc <= now && (page.Category != "challenge_log" ||
            now - page.CapturedAtUtc < ChallengeRetention && ChallengeWeekStart(page.CapturedAtUtc) == ChallengeWeekStart(now));

    internal static ImmutableArray<ProgressionMenuCapture> Prune(ImmutableArray<ProgressionMenuCapture> pages, DateTimeOffset now) =>
        pages.All(page => IsUsable(page, now)) ? pages : pages.Where(page => IsUsable(page, now)).ToImmutableArray();

    internal static void Validate(ImmutableArray<ProgressionMenuCapture> pages)
    {
        if (pages.IsDefault || pages.Length > MaxPages ||
            pages.Any(page => page is null) ||
            pages.Select(page => (page.Category, page.PageIndex)).Distinct().Count() != pages.Length)
            throw new InvalidDataException("Invalid saved progression pages.");
        foreach (var page in pages)
        {
            if (!ProgressionSnapshot.ValidMenuPage(page.Category, page.PageIndex) || page.CapturedAtUtc == default ||
                page.Entries.IsDefaultOrEmpty || page.Entries.Length > (page.Category switch
                    { "shared_fates" => 6, "trust" => 12, "hunting_log" => 10, "challenge_log" => 19, _ => 0 }) ||
                page.Entries.Any(entry => entry is null ||
                    (page.Category is "shared_fates" or "trust") && entry.State != "observed") ||
                page.Category == "hunting_log" && page.Entries.Any(entry => !ProgressionSnapshot.HuntingEntryMatchesPage(entry, page.PageIndex)))
                throw new InvalidDataException("Invalid saved progression page.");
            _ = new ProgressionSnapshot(page.Category, page.CapturedAtUtc, true, "visible_menu_observation",
                page.Entries, menuPage: page.PageIndex);
        }
    }

    internal static ProgressionMenuCapture? FromSnapshot(ProgressionSnapshot snapshot) =>
        snapshot.Available && snapshot.Readiness == "visible_menu_observation" &&
        snapshot.MenuPage is { } page && ProgressionSnapshot.ValidMenuPage(snapshot.Category, page)
            ? new(snapshot.Category, page, snapshot.CapturedAtUtc, snapshot.Entries.ToImmutableArray()) : null;

    internal static bool SameContent(ProgressionMenuCapture a, ProgressionMenuCapture b) =>
        a.Category == b.Category && a.PageIndex == b.PageIndex && a.Entries.SequenceEqual(b.Entries);

    internal static ImmutableArray<ProgressionMenuCapture> Merge(ImmutableArray<ProgressionMenuCapture> pages,
        ProgressionMenuCapture capture)
    {
        Validate([capture]);
        var old = pages.FirstOrDefault(page => page.Category == capture.Category && page.PageIndex == capture.PageIndex);
        if (old is not null && (capture.CapturedAtUtc <= old.CapturedAtUtc ||
                SameContent(old, capture) && capture.CapturedAtUtc - old.CapturedAtUtc < TimeSpan.FromMinutes(1)))
            return pages;
        var merged = pages.Where(page => page.Category != capture.Category || page.PageIndex != capture.PageIndex)
            .Append(capture).OrderBy(page => page.Category, StringComparer.Ordinal).ThenBy(page => page.PageIndex).ToImmutableArray();
        Validate(merged);
        return merged;
    }

    internal static ProgressionSnapshot Freeze(ImmutableArray<ProgressionMenuCapture> pages, string category, DateTimeOffset? now = null)
    {
        if (!IsMenu(category)) throw new ArgumentException("Unsupported saved progression category.", nameof(category));
        var entries = pages.Where(page => page.Category == category && IsUsable(page, now ?? DateTimeOffset.UtcNow))
            .SelectMany(page => page.Entries.Select(entry => entry with
                { ObservedAtUtc = page.CapturedAtUtc, ObservedPage = page.PageIndex }))
            .GroupBy(entry => entry.Id).Select(group => group.OrderByDescending(entry => entry.ObservedAtUtc)
                .ThenBy(entry => entry.ObservedPage).First()).ToArray();
        return entries.Length == 0
            ? ProgressionSnapshot.Unavailable(category,
                category == "challenge_log"
                    ? "No recent saved Challenge Log tabs. Open the relevant tab with access enabled to refresh it; observations expire after 15 minutes or at the weekly reset."
                    : "No saved observations for this character. Open the relevant menu page with access enabled and wait for its data to settle.")
            : new(category, entries.Max(entry => entry.ObservedAtUtc!.Value), true, "saved_menu_observations", entries);
    }
}

internal sealed class ProgressionMenuTracker
{
    private readonly Dictionary<string, ProgressionMenuCapture> candidates = new(StringComparer.Ordinal);
    private bool challengeNeedsReopen;
    internal void Reset() => this.candidates.Clear();
    internal void Reset(string category) => this.candidates.Remove(category);

    internal ProgressionMenuCapture? Observe(ProgressionSnapshot snapshot)
    {
        var capture = ProgressionMenuCache.FromSnapshot(snapshot);
        if (capture is null)
        {
            this.Reset(snapshot.Category);
            if (snapshot.Category == "challenge_log") this.challengeNeedsReopen = false;
            return null;
        }
        if (snapshot.Category == "challenge_log")
        {
            if (this.candidates.TryGetValue(snapshot.Category, out var earlier) &&
                ProgressionMenuCache.ChallengeWeekStart(earlier.CapturedAtUtc) != ProgressionMenuCache.ChallengeWeekStart(capture.CapturedAtUtc))
                this.challengeNeedsReopen = true;
            if (this.challengeNeedsReopen) return null;
        }
        if (!this.candidates.TryGetValue(snapshot.Category, out var previous) ||
            !ProgressionMenuCache.SameContent(previous, capture) || capture.CapturedAtUtc < previous.CapturedAtUtc)
        {
            this.candidates[snapshot.Category] = capture;
            return null;
        }
        return capture.CapturedAtUtc - previous.CapturedAtUtc >= TimeSpan.FromMilliseconds(250) ? capture : null;
    }
}

// Short-lived session data: the native progress record supplies its own achievement ID.
// It is deliberately separate from disk-backed progression pages.
internal sealed class RecentAchievementProgressCache
{
    internal static readonly TimeSpan Retention = TimeSpan.FromMinutes(15);
    private readonly Dictionary<uint, ProgressionEntry> entries = new();
    private ProgressionSnapshot? candidate;
    internal void Clear() { this.entries.Clear(); this.candidate = null; }
    internal void ResetCandidate() => this.candidate = null;
    internal bool HasRecent(DateTimeOffset now) => this.entries.Values.Any(entry => Usable(entry, now));
    private static bool Usable(ProgressionEntry entry, DateTimeOffset now) =>
        entry.ObservedAtUtc <= now && now - entry.ObservedAtUtc < Retention;

    internal bool Observe(ProgressionSnapshot snapshot)
    {
        if (snapshot.Category != "achievement_progress" || !snapshot.Available ||
            snapshot.Readiness != "loaded_progress_record" || snapshot.Entries.Count != 1)
        { this.candidate = null; return false; }
        foreach (var key in this.entries.Where(pair => !Usable(pair.Value, snapshot.CapturedAtUtc)).Select(pair => pair.Key).ToArray())
            this.entries.Remove(key);
        var row = snapshot.Entries[0];
        if (this.candidate is not { } previous || previous.Entries[0] != row || snapshot.CapturedAtUtc < previous.CapturedAtUtc)
        { this.candidate = snapshot; return false; }
        if (snapshot.CapturedAtUtc - previous.CapturedAtUtc < TimeSpan.FromMilliseconds(250)) return false;
        if (this.entries.TryGetValue(row.Id, out var old) &&
            old with { ObservedAtUtc = null, ObservedPage = null } == row && snapshot.CapturedAtUtc - old.ObservedAtUtc < TimeSpan.FromMinutes(1))
            return false;
        this.entries[row.Id] = row with { ObservedAtUtc = snapshot.CapturedAtUtc, ObservedPage = 0 };
        while (this.entries.Count > 64)
            this.entries.Remove(this.entries.Values.OrderBy(entry => entry.ObservedAtUtc).ThenBy(entry => entry.Id).First().Id);
        return true;
    }

    internal ProgressionSnapshot Freeze(DateTimeOffset now)
    {
        var rows = this.entries.Values.Where(entry => Usable(entry, now)).ToArray();
        return rows.Length == 0 ? ProgressionSnapshot.Unavailable("achievement_progress",
            "No recent achievement counters. Open the relevant achievement and let its progress record load; it is then retained for 15 minutes after closing the menu.")
            : new("achievement_progress", rows.Max(entry => entry.ObservedAtUtc!.Value), true, "saved_menu_observations", rows);
    }
}
