using System.Collections.Immutable;
using System.Text.Json;
using CompAI;

namespace CompAI;

// This fixture links the production Plugin.Progression implementation. Native capture is forbidden.
public sealed partial class Plugin
{
    private readonly object stateGate = new();
    private bool contextDisposed;
    private bool contextCacheLoading;
    private ulong activeContentId;
    private ulong cacheOwnerContentId = 123;
    private int contextLoadEpoch = 1;
    private int currencyAreaEpoch = 0;
    private object? preparedQuestion;
    private int writes;
    private readonly TestCompanion companion = new();
    private readonly TestCache characterCache = new();
    private readonly RecentPvpCache recentPvp = new();
    private ulong recentReaderOwner;
    private static readonly TestClientState ClientState = new();
    private static readonly TestFramework Framework = new();
    private CompanionAccessSettings Access { get; set; } = new(SharedFates: true, Trust: true);
    private bool CanReadCurrentCharacter => !this.contextDisposed && !this.contextCacheLoading &&
        ClientState.IsLoggedIn && this.activeContentId == this.cacheOwnerContentId && this.activeContentId != 0;
    private static bool QuestLoading() => false;
    private static ulong ReadLoadedCharacterId() => throw new Exception("Saved lookup attempted a native identity read.");
    private void PersistContextUnderLock() => this.writes++;
    private void SetAccess(bool inventory, bool quests, bool web, bool? sharedFates = null, bool? trust = null,
        bool? huntingLog = null, bool? challengeLog = null, bool? battleChocobo = null, bool? pvp = null)
    {
        var previous = this.Access;
        this.Access = previous with { SharedFates = sharedFates ?? previous.SharedFates, Trust = trust ?? previous.Trust,
            HuntingLog = huntingLog ?? previous.HuntingLog, ChallengeLog = challengeLog ?? previous.ChallengeLog,
            BattleChocobo = battleChocobo ?? previous.BattleChocobo, Pvp = pvp ?? previous.Pvp };
        this.ApplyProgressionAccessChangeUnderLock(previous, this.Access);
    }

    private Plugin() => this.progressionReader = null!;

    internal static void CheckSavedProgressionGuards()
    {
        static JsonElement Args(string category) => JsonDocument.Parse("{\"category\":\"" + category + "\"}").RootElement.Clone();
        static void Check(bool value) { if (!value) throw new Exception("Saved plugin guard assertion failed."); }
        static void Cancelled(Action action)
        {
            try { action(); } catch (OperationCanceledException) { return; }
            throw new Exception("Saved context escaped a permission or identity change.");
        }
        ClientState.IsLoggedIn = false;
        var plugin = new Plugin();
        var at = DateTimeOffset.Parse("2026-10-20T12:00:00Z");
        plugin.observedProgression = [new("shared_fates", 0, at,
            [new(813, "Synthetic Zone", "observed", Rank: 3, Progress: 0, Required: 0)])];
        var query = plugin.CreateProgressionQueryUnderLock()!;
        var first = query(Args("shared_fates"));
        Check(first.Contains("saved_menu_observations") && first.Contains("observed_at_utc"));
        Check(query(Args("trust")).Contains("No saved observations"));
        plugin.observedProgression = [];
        Check(query(Args("shared_fates")) == first); // Current answer owns its immutable observation.
        plugin.contextLoadEpoch++;
        Cancelled(() => query(Args("shared_fates")));
        query = plugin.CreateProgressionQueryUnderLock()!;
        plugin.cacheOwnerContentId = 456;
        Cancelled(() => query(Args("shared_fates")));
        plugin.cacheOwnerContentId = 123;
        query = plugin.CreateProgressionQueryUnderLock()!;
        plugin.SetAccess(false, false, false, sharedFates: false);
        Cancelled(() => query(Args("shared_fates")));
        plugin.SetAccess(false, false, false, sharedFates: true);
        Cancelled(() => query(Args("shared_fates"))); // Regrant does not revive an old callback.
        var clear = plugin.PrepareProgressionClear("shared_fates")!;
        Check(!plugin.ClearSavedProgression(clear with { ContentId = 456 }));
        Check(plugin.ClearSavedProgression(clear) && !plugin.Access.SharedFates && plugin.writes == 1 &&
            plugin.companion.Cancelled == 1 && plugin.preparedQuestion is null);
        plugin.Access = new();
        Check(plugin.CreateProgressionQueryUnderLock() is null);
        plugin.PollObservedProgression(); // No permissions/offline cannot reach native state.
        Check(plugin.progressionReadError is null);
        plugin.Access = new(SharedFates: true);
        ClientState.IsLoggedIn = true;
        plugin.activeContentId = 0; // Login has not identified the new character yet.
        Check(plugin.CreateProgressionQueryUnderLock() is null);
        ClientState.IsLoggedIn = false;
        plugin.contextCacheLoading = true;
        Check(plugin.CreateProgressionQueryUnderLock() is null);
        plugin.contextCacheLoading = false;
        plugin.contextDisposed = true;
        Check(plugin.CreateProgressionQueryUnderLock() is null);
    }

    internal static void CheckSavedLogAndRecentGuards()
    {
        static JsonElement Args(string category) => JsonDocument.Parse("{\"category\":\"" + category + "\"}").RootElement.Clone();
        static void Check(bool value) { if (!value) throw new Exception("Saved log/recent guard assertion failed."); }
        ClientState.IsLoggedIn = false;
        var plugin = new Plugin { Access = new(HuntingLog: true, ChallengeLog: true) };
        var now = DateTimeOffset.UtcNow;
        plugin.observedProgression = [
            new("hunting_log", 0, now.AddDays(-1), [new(10001, "Synthetic hunting", "completed", Completed: true, Rank: 1)]),
            new("challenge_log", 0, now, [new(47, "Synthetic challenge", "not_observed_complete", Progress: 1, Required: 3, UnknownReason: "Unverified negative.")])];
        var query = plugin.CreateProgressionQueryUnderLock()!;
        Check(query(Args("hunting_log")).Contains("saved_menu_observations"));
        Check(query(Args("challenge_log")).Contains("saved_menu_observations"));
        var clock = now;
        var expiring = plugin.CreateProgressionQueryUnderLock(() => clock)!;
        clock = now.AddMinutes(15);
        try { expiring(Args("challenge_log")); throw new Exception("Expired frozen challenge was returned."); }
        catch (IOException) { }
        Check(expiring(Args("hunting_log")).Contains("saved_menu_observations"));
        var clear = plugin.PrepareProgressionClear("hunting_log")!;
        Check(plugin.ClearSavedProgression(clear) && !plugin.Access.HuntingLog && plugin.Access.ChallengeLog);
        Check(plugin.observedProgression.Single().Category == "challenge_log");
        try { query(Args("challenge_log")); throw new Exception("Old log query survived permission epoch."); }
        catch (OperationCanceledException) { }
        plugin.observedProgression = [plugin.observedProgression[0] with { CapturedAtUtc = now.AddMinutes(-16) }];
        Check(plugin.CreateProgressionQueryUnderLock()!(Args("challenge_log")).Contains("No recent saved"));
        ClientState.IsLoggedIn = true;
        plugin.activeContentId = 123;
        plugin.Access = new(Achievements: true);
        plugin.recentAchievementOwner = 123;
        ProgressionSnapshot Row(DateTimeOffset at) => new("achievement_progress", at, true, "loaded_progress_record",
            [new(17, "Synthetic achievement", "observed", Progress: 5, Required: 10)]);
        plugin.recentAchievementProgress.Observe(Row(now.AddSeconds(-2)));
        plugin.recentAchievementProgress.Observe(Row(now.AddSeconds(-1)));
        Check(plugin.HasRecentAchievementProgressUnderLock());
        query = plugin.CreateProgressionQueryUnderLock()!;
        var first = query(Args("achievement_progress"));
        Check(first.Contains("saved_menu_observations") && first.Contains("Synthetic achievement"));
        clock = now;
        expiring = plugin.CreateProgressionQueryUnderLock(() => clock)!;
        clock = now.AddMinutes(15);
        try { expiring(Args("achievement_progress")); throw new Exception("Expired frozen achievement was returned."); }
        catch (IOException) { }
        plugin.currencyAreaEpoch++;
        plugin.ResetProgressionCaptureUnderLock();
        Check(plugin.HasRecentAchievementProgressUnderLock());
        Check(plugin.CreateProgressionQueryUnderLock()!(Args("achievement_progress")) == first);
        plugin.recentAchievementOwner = 456;
        Check(!plugin.HasRecentAchievementProgressUnderLock());
        Check(plugin.CreateProgressionQueryUnderLock()!(Args("achievement_progress")).Contains("No recent achievement"));
        plugin.ResetRecentAchievementProgressUnderLock();
        Check(!plugin.recentAchievementProgress.HasRecent(now));
        ClientState.IsLoggedIn = false;
    }

    internal static void CheckRecentPvpQueryGuards()
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        static void Cancelled(Action action)
        {
            try { action(); } catch (OperationCanceledException) { return; }
            throw new Exception("Frozen PvP query escaped its owner or permission epoch.");
        }
        static ProgressionSnapshot Profile(DateTimeOffset time) => new("pvp", time, true, "visible_menu_observation",
            [new(1, "Synthetic PvP rank and cumulative EXP", "observed", Rank: 30, Experience: 123456),
             new(2, "Synthetic current Series rank and EXP", "observed", Rank: 12, Experience: 321)], menuPage: 0);
        var arguments = JsonDocument.Parse("""{"category":"pvp"}""").RootElement.Clone();
        var observedAt = DateTimeOffset.Parse("2026-10-23T12:00:00Z");
        var clock = observedAt;
        ClientState.IsLoggedIn = true;
        var plugin = new Plugin { Access = new(Pvp: true), activeContentId = 123, recentReaderOwner = 123 };
        try
        {
            Check(!plugin.recentPvp.Observe(Profile(observedAt.AddMilliseconds(-500))) &&
                plugin.recentPvp.Observe(Profile(observedAt)), "Synthetic profile must settle before caching.");
            var query = plugin.CreateProgressionQueryUnderLock(() => clock)!;
            var first = query(arguments);
            Check(first.Contains("last_observed_profile") && first.Contains("123456"),
                "Actual plugin query must serialize the managed profile snapshot without a native read.");
            plugin.recentPvp.Observe(ProgressionSnapshot.Unavailable("pvp", "Synthetic closed PvP Profile menu."));
            Check(plugin.CreateProgressionQueryUnderLock(() => clock)!(arguments) == first,
                "Closing the profile keeps the last-known managed snapshot available.");
            clock = observedAt.AddTicks(-1);
            try { query(arguments); throw new Exception("Frozen PvP snapshot survived a clock rollback before its observation."); }
            catch (IOException) { }
            clock = observedAt;
            Check(query(arguments) == first, "Returning to the observed time permits the still-fresh frozen snapshot.");
            clock = observedAt.AddMinutes(15);
            try { query(arguments); throw new Exception("Expired frozen PvP snapshot escaped its query-time TTL."); }
            catch (IOException) { }
            Check(plugin.CreateProgressionQueryUnderLock(() => clock)!(arguments).Contains("No recent PvP profile observation"),
                "New queries must return unavailable after cache expiry without native capture.");
            clock = observedAt;
            query = plugin.CreateProgressionQueryUnderLock(() => clock)!;
            plugin.activeContentId = plugin.cacheOwnerContentId = 456;
            Cancelled(() => query(arguments));
            Check(plugin.CreateProgressionQueryUnderLock(() => clock)!(arguments).Contains("No recent PvP profile for this character"),
                "Cached profile belonging to another character cannot be serialized.");
            plugin.activeContentId = plugin.cacheOwnerContentId = 123;
            plugin.contextLoadEpoch++;
            query = plugin.CreateProgressionQueryUnderLock(() => clock)!;
            plugin.SetAccess(false, false, false, pvp: false);
            Cancelled(() => query(arguments));
            Check(plugin.CreateProgressionQueryUnderLock(() => clock) is null, "Disabled PvP permission removes the query.");
            plugin.SetAccess(false, false, false, pvp: true);
            Cancelled(() => query(arguments)); // Regrant cannot revive a previously prepared callback.
            query = plugin.CreateProgressionQueryUnderLock(() => clock)!;
            plugin.contextLoadEpoch++;
            Cancelled(() => query(arguments));
            ClientState.IsLoggedIn = false;
            Check(plugin.CreateProgressionQueryUnderLock(() => clock) is null, "Personal PvP cache is unavailable while logged out.");
        }
        finally { ClientState.IsLoggedIn = false; }
    }

    private sealed class TestClientState { internal bool IsLoggedIn; internal uint TerritoryType => 1; }
    private sealed class TestCache { internal string? Error => null; }
    private sealed class TestCompanion
    {
        internal int Cancelled;
        internal Func<bool> CaptureQuestionContinuation() => () => true;
        internal void CancelAllQuestions() => this.Cancelled++;
    }
    private sealed class TestFramework
    {
        internal Task<T> RunOnFrameworkThread<T>(Func<T> callback) => throw new Exception("Saved lookup attempted a native framework read.");
    }
}

internal sealed record CompanionAccessSettings(bool Inventory = false, bool Quests = false, bool WebSearch = false,
    bool Achievements = false, bool AetherCurrents = false, bool SharedFates = false, bool Trust = false,
    bool GatheringLog = false, bool FishingLog = false, bool VariantNotebook = false,
    bool HuntingLog = false, bool SightseeingLog = false, bool ChallengeLog = false,
    bool BattleChocobo = false, bool Pvp = false);
internal static class PerformanceDiagnostics
{
    internal static IDisposable Measure(string name) => throw new Exception("Saved lookup attempted a native capture.");
}
