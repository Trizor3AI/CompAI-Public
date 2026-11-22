using System;
using System.Linq;

namespace CompAI;

/// <summary>Managed observations for one login; owner binding is enforced by Plugin.</summary>
internal sealed class RecentReaderCache
{
    internal static readonly TimeSpan Retention = TimeSpan.FromMinutes(15);
    private GcSupplySnapshot? gcCandidate;
    private GcSupplySnapshot? gc;
    private string? gcUnavailableReason;
    private CollectionSnapshot? bestiaryCandidate;
    private CollectionSnapshot? bestiary;

    internal static bool Usable(DateTimeOffset captured, DateTimeOffset now) =>
        captured <= now && now - captured < Retention;
    internal void Reset()
    {
        this.gcCandidate = this.gc = null;
        this.gcUnavailableReason = null;
        this.bestiaryCandidate = this.bestiary = null;
    }
    internal void ResetCandidates() { this.gcCandidate = null; this.bestiaryCandidate = null; }
    internal void ClearGc() { this.gcCandidate = this.gc = null; this.gcUnavailableReason = null; }
    internal void ClearBestiary() { this.bestiaryCandidate = this.bestiary = null; }

    internal void Observe(GcSupplySnapshot snapshot)
    {
        if (!snapshot.Available)
        {
            this.gcCandidate = null;
            // Capture emits bounded, non-identifying reasons. Retain the reason
            // too, so a ready-looking menu is not reported merely as never opened.
            this.gcUnavailableReason = snapshot.UnavailableReason;
            return;
        }
        this.gcUnavailableReason = null;
        // Native pointers and eligibility never enter retained state. Eligibility
        // can change with an inventory move even while the menu stays open.
        var detached = new GcSupplySnapshot(snapshot.CapturedAtUtc, true,
            snapshot.Entries.Select(row => row with { TurnInAvailable = null }), lastObserved: true);
        var previous = this.gcCandidate;
        this.gcCandidate = detached;
        if (previous is not null && previous.Entries.SequenceEqual(detached.Entries) &&
            detached.CapturedAtUtc - previous.CapturedAtUtc >= TimeSpan.FromMilliseconds(250))
            this.gc = detached;
    }

    internal void Observe(CollectionSnapshot snapshot)
    {
        if (snapshot.Category != MasterBestiaryCategory) throw new ArgumentException("Unsupported recent collection.");
        if (!snapshot.Available) { this.bestiaryCandidate = null; return; }
        var detached = new CollectionSnapshot(snapshot.Category, snapshot.CapturedAtUtc, true,
            snapshot.Entries, lastObserved: true);
        var previous = this.bestiaryCandidate;
        this.bestiaryCandidate = detached;
        if (previous is not null && previous.Entries.SequenceEqual(detached.Entries) &&
            detached.CapturedAtUtc - previous.CapturedAtUtc >= TimeSpan.FromMilliseconds(250))
            this.bestiary = detached;
    }

    internal const string MasterBestiaryCategory = "beastmaster_bestiary";
    internal bool HasGc(DateTimeOffset now) => this.gc is { } value && Usable(value.CapturedAtUtc, now);
    internal bool HasBestiary(DateTimeOffset now) => this.bestiary is { } value && Usable(value.CapturedAtUtc, now);
    internal GcSupplySnapshot GcSupply(DateTimeOffset now) => this.HasGc(now) ? this.gc! :
        GcSupplySnapshot.Unavailable(this.gcUnavailableReason ??
            "No recent GC delivery observation. Open Supply or Provisioning and let it settle to refresh; observations expire after 15 minutes.");
    internal CollectionSnapshot Bestiary(DateTimeOffset now) => this.HasBestiary(now) ? this.bestiary! :
        CollectionSnapshot.Unavailable(MasterBestiaryCategory,
            "No recent Master's Bestiary observation. Open the ordinary notebook and let it settle to refresh; observations expire after 15 minutes.");
}
