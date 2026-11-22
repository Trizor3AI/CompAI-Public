using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CompAI;

public sealed partial class Plugin
{
    private readonly ProgressionReader progressionReader;
    private ImmutableArray<ProgressionMenuCapture> observedProgression = ImmutableArray<ProgressionMenuCapture>.Empty;
    private ImmutableArray<ProgressionMenuCapture> persistedProgression = ImmutableArray<ProgressionMenuCapture>.Empty;
    private readonly ProgressionMenuTracker progressionTracker = new();
    private DateTimeOffset nextProgressionCheck;
    private int progressionAccessEpoch;
    private string? progressionReadError;
    private readonly RecentAchievementProgressCache recentAchievementProgress = new();
    private ulong recentAchievementOwner;

    private void ResetRecentAchievementProgressUnderLock()
    {
        this.recentAchievementProgress.Clear();
        this.recentAchievementOwner = 0;
    }

    internal bool HasRecentAchievementProgressUnderLock() => this.CanReadCurrentCharacter &&
        this.recentAchievementOwner == this.activeContentId && this.recentAchievementProgress.HasRecent(DateTimeOffset.UtcNow);

    private bool CanUseSavedProgressionUnderLock => !this.contextDisposed && !this.contextCacheLoading &&
        this.cacheOwnerContentId != 0 && (!ClientState.IsLoggedIn || this.activeContentId == this.cacheOwnerContentId);

    private string[] ProgressionQueryCategoriesUnderLock() => ProgressionAccess.Categories(this.Access)
        .Where(category => ProgressionMenuCache.IsMenu(category) ? this.CanUseSavedProgressionUnderLock : this.CanReadCurrentCharacter)
        .ToArray();

    private void ResetProgressionCaptureUnderLock()
    {
        this.progressionTracker.Reset();
        this.recentAchievementProgress.ResetCandidate();
        this.nextProgressionCheck = default;
        this.progressionReadError = null;
    }

    private void ApplyProgressionAccessChangeUnderLock(CompanionAccessSettings previous, CompanionAccessSettings next)
    {
        if (previous.SharedFates == next.SharedFates && previous.Trust == next.Trust &&
            previous.HuntingLog == next.HuntingLog && previous.ChallengeLog == next.ChallengeLog &&
            previous.Achievements == next.Achievements && previous.BattleChocobo == next.BattleChocobo && previous.Pvp == next.Pvp) return;
        if (previous.Achievements != next.Achievements) this.ResetRecentAchievementProgressUnderLock();
        ++this.progressionAccessEpoch;
        this.ResetProgressionCaptureUnderLock();
    }

    // The framework tick only checks opt-in visible menus. Native readers never open or request a menu.
    private void PollObservedProgression()
    {
        lock (this.stateGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (now < this.nextProgressionCheck) return;
            this.nextProgressionCheck = now.AddMilliseconds(500);
            if (!ClientState.IsLoggedIn || this.activeContentId != this.recentAchievementOwner)
                this.ResetRecentAchievementProgressUnderLock();
            var retained = ProgressionMenuCache.Prune(this.observedProgression, now);
            if (retained != this.observedProgression)
            {
                this.observedProgression = retained;
                this.preparedQuestion = null;
                this.PersistContextUnderLock();
            }
            if (!this.CanReadCurrentCharacter || QuestLoading() ||
                (!this.Access.SharedFates && !this.Access.Trust && !this.Access.HuntingLog && !this.Access.ChallengeLog && !this.Access.Achievements))
            { this.progressionTracker.Reset(); return; }
            var owner = this.activeContentId;
            if (ReadLoadedCharacterId() != owner) { this.progressionTracker.Reset(); return; }
            var changed = false;
            foreach (var category in new[] { "shared_fates", "trust", "hunting_log", "challenge_log", "achievement_progress" })
            {
                if (!ProgressionAccess.Allows(this.Access, category)) { this.progressionTracker.Reset(category); continue; }
                try
                {
                    var reading = this.progressionReader.Capture(category);
                    if (ReadLoadedCharacterId() != owner || !this.CanReadCurrentCharacter || QuestLoading())
                    { this.progressionTracker.Reset(); return; }
                    if (category == "achievement_progress")
                    {
                        this.recentAchievementOwner = owner;
                        if (this.recentAchievementProgress.Observe(reading)) this.preparedQuestion = null;
                        continue;
                    }
                    var capture = this.progressionTracker.Observe(reading);
                    if (capture is null) continue;
                    var merged = ProgressionMenuCache.Merge(this.observedProgression, capture);
                    changed |= merged != this.observedProgression;
                    this.observedProgression = merged;
                    this.progressionReadError = null;
                }
                catch (Exception ex)
                {
                    this.progressionTracker.Reset(category);
                    if (category == "achievement_progress") this.recentAchievementProgress.ResetCandidate();
                    this.progressionReadError = $"Progression observation unavailable ({ex.GetType().Name}); previous saved observations retained.";
                }
            }
            if (changed)
            {
                this.preparedQuestion = null;
                this.PersistContextUnderLock();
            }
        }
    }

    internal string SavedProgressionStatus(string category)
    {
        lock (this.stateGate)
        {
            if (this.contextCacheLoading) return "Loading this character's saved observations...";
            if (this.characterCache.Error is { } error) return error;
            if (this.progressionReadError is { } readError) return readError;
            var now = DateTimeOffset.UtcNow;
            var pages = this.observedProgression.Where(page => page.Category == category && ProgressionMenuCache.IsUsable(page, now)).ToArray();
            return pages.Length == 0 ? "No saved observations. Open the menu with access enabled and let the page settle."
                : $"{pages.SelectMany(page => page.Entries).Select(entry => entry.Id).Distinct().Count()} saved entries from {pages.Length} observed page(s). " +
                  $"Latest observation: {pages.Max(page => page.CapturedAtUtc).ToLocalTime():g}. Last known, not live." +
                  (category == "challenge_log" ? " Expires after 15 minutes or Tuesday 08:00 UTC." : "");
        }
    }

    internal ProgressionClearRequest? PrepareProgressionClear(string category)
    {
        lock (this.stateGate)
            return !this.contextDisposed && !this.contextCacheLoading && this.cacheOwnerContentId != 0 && ProgressionMenuCache.IsMenu(category)
                ? new(this.cacheOwnerContentId, this.contextLoadEpoch, category) : null;
    }

    internal bool ClearSavedProgression(ProgressionClearRequest request)
    {
        lock (this.stateGate)
        {
            if (this.contextDisposed || this.contextCacheLoading || !ProgressionMenuCache.IsMenu(request.Category) ||
                request.ContentId == 0 || request.ContentId != this.cacheOwnerContentId || request.ContextEpoch != this.contextLoadEpoch) return false;
            this.SetAccess(this.Access.Inventory, this.Access.Quests, this.Access.WebSearch,
                sharedFates: request.Category == "shared_fates" ? false : null, trust: request.Category == "trust" ? false : null,
                huntingLog: request.Category == "hunting_log" ? false : null, challengeLog: request.Category == "challenge_log" ? false : null);
            this.observedProgression = this.observedProgression.Where(page => page.Category != request.Category).ToImmutableArray();
            this.ResetProgressionCaptureUnderLock();
            this.preparedQuestion = null;
            this.companion.CancelAllQuestions();
            this.PersistContextUnderLock();
            return true;
        }
    }

    // Saved menu observations are frozen at question preparation; other readers capture once on demand.
    private Func<JsonElement, string>? CreateProgressionQueryUnderLock(Func<DateTimeOffset>? clock = null)
    {
        clock ??= () => DateTimeOffset.UtcNow;
        var categories = new HashSet<string>(this.ProgressionQueryCategoriesUnderLock(), StringComparer.Ordinal);
        if (categories.Count == 0) return null;
        var owner = this.activeContentId;
        var savedOwner = this.cacheOwnerContentId;
        var accessEpoch = this.progressionAccessEpoch;
        var session = this.contextLoadEpoch;
        var territory = ClientState.TerritoryType;
        var areaEpoch = this.currencyAreaEpoch;
        var frozenAt = clock();
        var pvp = this.recentReaderOwner == owner ? this.recentPvp.Freeze(frozenAt) :
            ProgressionSnapshot.Unavailable("pvp", "No recent PvP profile for this character; open PvP Profile to refresh.");
        var pvpExpires = pvp.Available ? pvp.CapturedAtUtc.Add(RecentPvpCache.Retention) : DateTimeOffset.MaxValue;
        var recentAchievement = this.recentAchievementOwner == owner
            ? this.recentAchievementProgress.Freeze(frozenAt) : ProgressionSnapshot.Unavailable("achievement_progress", "No recent achievement counters for this character.");
        var achievementExpires = !recentAchievement.Available ? DateTimeOffset.MaxValue :
            recentAchievement.Entries.Min(entry => entry.ObservedAtUtc!.Value).Add(RecentAchievementProgressCache.Retention);
        var challengeRows = this.observedProgression.Where(page => page.Category == "challenge_log" && ProgressionMenuCache.IsUsable(page, frozenAt)).ToArray();
        var challengeExpires = challengeRows.Length == 0 ? DateTimeOffset.MaxValue :
            new[] { challengeRows.Min(page => page.CapturedAtUtc).Add(ProgressionMenuCache.ChallengeRetention),
                ProgressionMenuCache.ChallengeWeekStart(frozenAt).AddDays(7) }.Min();
        bool Permitted(string category) => categories.Contains(category) && ProgressionAccess.Allows(this.Access, category) &&
            (category is not ("achievement_progress" or "pvp" or "battle_chocobo") || accessEpoch == this.progressionAccessEpoch) &&
            session == this.contextLoadEpoch && (ProgressionMenuCache.IsMenu(category)
                ? this.CanUseSavedProgressionUnderLock && savedOwner == this.cacheOwnerContentId && accessEpoch == this.progressionAccessEpoch
                : this.CanReadCurrentCharacter && !QuestLoading() && owner == this.activeContentId &&
                  territory == ClientState.TerritoryType && areaEpoch == this.currencyAreaEpoch);
        void RequireFresh(string category)
        {
            var now = clock();
            if (category == "challenge_log" && now >= challengeExpires ||
                category == "achievement_progress" && now >= achievementExpires ||
                category == "pvp" && pvp.Available && (now < pvp.CapturedAtUtc || now >= pvpExpires))
                throw new IOException("The saved menu observation expired during this question. Open its page to refresh it, then send again.");
        }
        var captured = new Dictionary<string, ProgressionLookup>(StringComparer.Ordinal);
        foreach (var category in categories.Where(ProgressionMenuCache.IsMenu))
            captured[category] = new(ProgressionMenuCache.Freeze(this.observedProgression, category, frozenAt));
        if (categories.Contains("achievement_progress")) captured["achievement_progress"] = new(recentAchievement);
        if (categories.Contains("pvp")) captured["pvp"] = new(pvp);
        var queryGate = new object();
        return arguments =>
        {
            ProgressionLookup.ValidateArguments(arguments);
            var category = ProgressionLookup.Category(arguments);
            var questionContinues = this.companion.CaptureQuestionContinuation();
            lock (queryGate)
            {
                lock (this.stateGate)
                    if (!Permitted(category) || !questionContinues()) throw new OperationCanceledException();
                RequireFresh(category);
                if (!captured.TryGetValue(category, out var lookup))
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var token = deadline.Token;
                    try
                    {
                        lookup = Framework.RunOnFrameworkThread(() =>
                        {
                            lock (this.stateGate)
                            {
                                token.ThrowIfCancellationRequested();
                                if (!Permitted(category) || !questionContinues() || ReadLoadedCharacterId() != owner)
                                    throw new OperationCanceledException();
                                using var timing = PerformanceDiagnostics.Measure("reader.progression");
                                var reading = this.progressionReader.Capture(category);
                                token.ThrowIfCancellationRequested();
                                if (!Permitted(category) || !questionContinues() || ReadLoadedCharacterId() != owner)
                                    throw new OperationCanceledException();
                                return new ProgressionLookup(reading);
                            }
                        }).WaitAsync(token).GetAwaiter().GetResult();
                        captured.Add(category, lookup);
                    }
                    catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                    {
                        lock (this.stateGate)
                            if (!Permitted(category) || !questionContinues()) throw;
                        throw new TimeoutException("Progression reader timed out.");
                    }
                }
                lock (this.stateGate)
                {
                    if (!Permitted(category) || !questionContinues()) throw new OperationCanceledException();
                    RequireFresh(category);
                    return JsonSerializer.Serialize(lookup.Query(arguments));
                }
            }
        };
    }
}
