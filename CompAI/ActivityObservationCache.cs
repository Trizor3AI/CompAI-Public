using System;
using System.Collections.Generic;
using System.Linq;

namespace CompAI;

internal sealed class ActivityObservationCache
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private const string CosmicCategory = "cosmic_missions";
    private readonly Dictionary<string, ActivitySnapshot> candidates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActivitySnapshot> saved = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> failures = new(StringComparer.Ordinal);
    private readonly Dictionary<(uint MissionId, int JobId), (ActivityEntry Entry, DateTimeOffset ObservedAt)> cosmicEntries = new();
    private ActivitySnapshot? cosmicEmptyObservation;
    internal void Reset()
    {
        this.candidates.Clear(); this.saved.Clear(); this.failures.Clear();
        this.cosmicEntries.Clear(); this.cosmicEmptyObservation = null;
    }
    internal void ResetCandidates() => this.candidates.Clear();
    internal void Clear(string category)
    {
        this.candidates.Remove(category);
        this.saved.Remove(category);
        this.failures.Remove(category);
        if (category == CosmicCategory)
        {
            this.cosmicEntries.Clear();
            this.cosmicEmptyObservation = null;
        }
    }
    internal void ResetMogpendium()
    {
        // A historical season selection must never inherit another event's saved tabs.
        foreach (var category in this.candidates.Keys.Concat(this.saved.Keys).Concat(this.failures.Keys)
                     .Where(MogpendiumContract.IsCategory).Distinct().ToArray())
            this.Clear(category);
    }
    internal bool HasRecent(string category, DateTimeOffset now)
    {
        if (category == CosmicCategory)
        {
            this.PruneCosmic(now);
            return this.cosmicEntries.Count != 0 || this.cosmicEmptyObservation is not null;
        }
        return this.saved.TryGetValue(category, out var value) && Recent(value.CapturedAtUtc, now);
    }
    internal bool Observe(ActivitySnapshot value, bool replaceSelection = false)
    {
        if (value.LastObserved) throw new ArgumentException("Expected a current activity observation.");
        // An open selection changing or failing to read must not fall back to a
        // different saved entry. Closed menus still retain the last good read.
        var removed = false;
        if (replaceSelection && value.Category != CosmicCategory && this.saved.TryGetValue(value.Category, out var saved) &&
            (!value.Available || !saved.Entries.SequenceEqual(value.Entries)))
        {
            removed = this.saved.Remove(value.Category);
            this.failures[value.Category] = "The selected entry changed; waiting for a stable observation.";
        }
        if (!value.Available)
        {
            this.candidates.Remove(value.Category);
            this.failures[value.Category] = value.UnavailableReason!;
            return removed;
        }
        var before = this.candidates.GetValueOrDefault(value.Category);
        this.candidates[value.Category] = value;
        if (before is null || value.CapturedAtUtc - before.CapturedAtUtc < TimeSpan.FromMilliseconds(250) ||
            !before.Entries.SequenceEqual(value.Entries)) return removed;
        if (value.Category == CosmicCategory) this.MergeCosmic(value);
        else this.saved[value.Category] = value.AsLastObserved();
        this.failures.Remove(value.Category);
        return true;
    }
    internal ActivitySnapshot Freeze(string category, DateTimeOffset now)
    {
        if (this.HasRecent(category, now))
        {
            if (category != CosmicCategory) return this.saved[category];
            if (this.cosmicEntries.Count == 0) return this.cosmicEmptyObservation!;
            // The wire has one timestamp: use the oldest covered observation so a
            // frozen answer expires conservatively rather than refreshing old rows.
            return new(category, this.cosmicEntries.Values.Min(row => row.ObservedAt), true,
                this.cosmicEntries.OrderBy(row => row.Key.MissionId).ThenBy(row => row.Key.JobId)
                    .Select((row, index) => row.Value.Entry with { Id = (uint)index + 1 }), lastObserved: true);
        }
        return ActivitySnapshot.Unavailable(category, this.failures.GetValueOrDefault(category) ??
            "No recent observation. Open the relevant menu tab and let it load to refresh; observations last 15 minutes in this login.");
    }

    private static bool Recent(DateTimeOffset observedAt, DateTimeOffset now) =>
        observedAt <= now && now - observedAt < Lifetime;

    private void PruneCosmic(DateTimeOffset now)
    {
        foreach (var key in this.cosmicEntries.Where(row => !Recent(row.Value.ObservedAt, now)).Select(row => row.Key).ToArray())
            this.cosmicEntries.Remove(key);
        if (this.cosmicEmptyObservation is { } empty && !Recent(empty.CapturedAtUtc, now))
            this.cosmicEmptyObservation = null;
    }

    private void MergeCosmic(ActivitySnapshot value)
    {
        this.PruneCosmic(value.CapturedAtUtc);
        if (value.Entries.Count == 0) this.cosmicEmptyObservation = value.AsLastObserved();
        foreach (var row in value.Entries)
            this.cosmicEntries[(row.ReferenceId!.Value, row.Slot!.Value)] = (row, value.CapturedAtUtc);
        // Mission IDs and jobs identify retained positive evidence only; no planet
        // or missing-medal status is inferred from a changed or empty view.
        foreach (var key in this.cosmicEntries.OrderBy(row => row.Value.ObservedAt)
                     .ThenBy(row => row.Key.MissionId).ThenBy(row => row.Key.JobId)
                     .Take(Math.Max(0, this.cosmicEntries.Count - ActivitySnapshot.MaxEntries)).Select(row => row.Key).ToArray())
            this.cosmicEntries.Remove(key);
    }
}
