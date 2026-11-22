using System;

namespace CompAI;

public sealed partial class Plugin
{
    private readonly RecentReaderCache recentReaders = new();
    private readonly RecentPvpCache recentPvp = new();
    private ulong recentReaderOwner;
    private DateTimeOffset nextRecentReaderPoll;

    private void PollRecentReaders()
    {
        lock (this.stateGate)
        {
            if (!this.CanReadCurrentCharacter || QuestLoading())
            {
                this.recentReaders.ResetCandidates();
                this.recentPvp.ResetCandidate();
                return;
            }
            if (this.recentReaderOwner != this.activeContentId)
            {
                this.recentReaders.Reset();
                this.recentPvp.Reset();
                this.recentReaderOwner = this.activeContentId;
            }
            var now = DateTimeOffset.UtcNow;
            if (now < this.nextRecentReaderPoll) return;
            this.nextRecentReaderPoll = now.AddSeconds(1);
            var owner = this.activeContentId;
            if (ReadLoadedCharacterId() != owner) { this.recentReaders.ResetCandidates(); this.recentPvp.ResetCandidate(); return; }
            using var timing = PerformanceDiagnostics.Measure("reader.recent_menus");
            if (!this.Access.GcSupply) this.recentReaders.ClearGc();
            else this.recentReaders.Observe(this.gcSupplyReader.Capture());
            if (!this.Access.Bestiary) this.recentReaders.ClearBestiary();
            else this.recentReaders.Observe(this.collectionReader.Capture(RecentReaderCache.MasterBestiaryCategory));
            if (!this.Access.Pvp) this.recentPvp.Reset();
            else if (this.recentPvp.Observe(this.progressionReader.Capture("pvp"))) this.preparedQuestion = null;
            if (ReadLoadedCharacterId() != owner) { this.recentReaders.Reset(); this.recentPvp.Reset(); }
        }
    }

    private void ApplyRecentReaderAccessChangeUnderLock(CompanionAccessSettings previous, CompanionAccessSettings next)
    {
        if (previous.GcSupply && !next.GcSupply) this.recentReaders.ClearGc();
        if (previous.Bestiary && !next.Bestiary) this.recentReaders.ClearBestiary();
        if (previous.Pvp && !next.Pvp) this.recentPvp.Reset();
    }

    private void ResetRecentReadersUnderLock()
    {
        this.recentReaders.Reset();
        this.recentPvp.Reset();
        this.ResetActivityObservationsUnderLock();
        this.recentReaderOwner = 0;
        this.nextRecentReaderPoll = default;
    }

    private bool HasRecentReaderUnderLock(string category) => category switch
    {
        "achievement_progress" => this.HasRecentAchievementProgressUnderLock(),
        "pvp" => this.CanReadCurrentCharacter && this.Access.Pvp && this.recentReaderOwner == this.activeContentId &&
            this.recentPvp.HasRecent(DateTimeOffset.UtcNow),
        "gc_supply" => this.CanReadCurrentCharacter && this.Access.GcSupply && this.recentReaderOwner == this.activeContentId &&
            this.recentReaders.HasGc(DateTimeOffset.UtcNow),
        RecentReaderCache.MasterBestiaryCategory => this.CanReadCurrentCharacter && this.Access.Bestiary && this.recentReaderOwner == this.activeContentId &&
            this.recentReaders.HasBestiary(DateTimeOffset.UtcNow),
        _ => false,
    };

    private GcSupplySnapshot RecentGcSupplyUnderLock() => this.recentReaderOwner == this.activeContentId
        ? this.recentReaders.GcSupply(DateTimeOffset.UtcNow)
        : GcSupplySnapshot.Unavailable("Open Supply or Provisioning and let it settle to refresh this character's requests.");
    private CollectionSnapshot RecentBestiaryUnderLock() => this.recentReaderOwner == this.activeContentId
        ? this.recentReaders.Bestiary(DateTimeOffset.UtcNow)
        : CollectionSnapshot.Unavailable(RecentReaderCache.MasterBestiaryCategory,
            "Open the ordinary Master's Bestiary and let it settle to refresh this character's observations.");
}
