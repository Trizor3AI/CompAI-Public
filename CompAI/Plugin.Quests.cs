using System;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.IoC;
using Dalamud.Plugin.Services;

namespace CompAI;

public sealed partial class Plugin
{
    [PluginService] internal static ICondition Condition { get; private set; } = null!;

    private readonly QuestChangeTracker questChanges = new();
    private bool questWatching;
    private bool questDisposed;
    private int questWatchEpoch;
    private DateTimeOffset nextQuestCheck;
    private DateTimeOffset? questLastChecked;

    internal bool QuestAutoUpdating { get { lock (this.stateGate) return this.questWatching; } }
    internal DateTimeOffset? QuestLastChecked { get { lock (this.stateGate) return this.questLastChecked; } }

    internal void CaptureQuests()
    {
        int epoch;
        lock (this.stateGate)
        {
            if (!this.Access.Quests || this.questDisposed || this.contextDisposed || !this.CanReadCurrentCharacter ||
                this.quests.Begin() is not { } nextEpoch) return;
            epoch = nextEpoch;
            this.questWatching = true;
            ++this.questWatchEpoch;
            this.questChanges.Suspend();
        }
        try
        {
            var task = Framework.RunOnFrameworkThread(() => this.CaptureQuestsOnFramework(epoch));
            _ = task.ContinueWith(t =>
            {
                if (t.IsFaulted) this.QuestRefreshFailed(epoch, t.Exception!.GetBaseException());
                else if (t.IsCanceled) this.QuestRefreshFailed(epoch, new TaskCanceledException());
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
        catch (Exception ex) { this.QuestRefreshFailed(epoch, ex); }
    }

    private static bool QuestLoading() => Condition[ConditionFlag.BetweenAreas] || Condition[ConditionFlag.BetweenAreas51];

    private void CaptureQuestsOnFramework(int epoch, QuestStateStamp? expected = null)
    {
        try
        {
            ulong contentId;
            lock (this.stateGate)
            {
                if (!this.Access.Quests || this.questDisposed || this.contextDisposed || !this.questWatching || !this.quests.IsCurrent(epoch)) return;
                if (!this.CanReadCurrentCharacter)
                {
                    this.PauseQuestsForOffline("Character context is unavailable; keeping the saved quest cache.");
                    return;
                }
                if (QuestLoading()) { this.SuspendQuestTracking("Changing areas; showing the cached quests until loading finishes."); return; }
                contentId = this.activeContentId;
            }

            var before = this.questReader.ReadState(true);
            QuestSnapshot? completionBaseline;
            lock (this.stateGate)
            {
                if (this.questDisposed || this.contextDisposed || !this.questWatching || !this.quests.IsCurrent(epoch) ||
                    !this.CanReadCurrentCharacter || this.activeContentId != contentId) return;
                if (before.ContentId != contentId || !this.questChanges.SameCharacter(before))
                {
                    this.OnDetectedCharacterMismatch();
                    return;
                }
                // Explicit Refresh always checks natively. Automatic progress updates
                // can reuse only a same-character, committed completion-state match.
                completionBaseline = expected is not null && this.questChanges.CanReuseCompletion(before)
                    ? this.quests.View.Snapshot : null;
            }
            if (expected is not null && !expected.Equals(before))
                throw new InvalidOperationException("Quest state is still changing; automatic refresh will retry.");
            var captured = this.questReader.Capture(true, completionBaseline);
            var after = this.questReader.ReadState(ClientState.IsLoggedIn);
            lock (this.stateGate)
            {
                if (this.questDisposed || this.contextDisposed || !this.questWatching || !this.quests.IsCurrent(epoch) ||
                    !this.CanReadCurrentCharacter || this.activeContentId != contentId) return;
                if (after.ContentId != contentId)
                {
                    this.OnDetectedCharacterMismatch();
                    return;
                }
                if (QuestLoading() || !before.Equals(after))
                    throw new InvalidOperationException("Quest state changed during refresh; keeping the cached list until stable.");
                if (!this.questChanges.Commit(after))
                {
                    this.OnDetectedCharacterMismatch();
                    return;
                }
                this.quests.Complete(epoch, captured);
                this.questLastChecked = DateTimeOffset.UtcNow;
                this.nextQuestCheck = DateTimeOffset.UtcNow.AddSeconds(3);
                this.PersistContextUnderLock();
            }
        }
        catch (Exception ex) { this.QuestRefreshFailed(epoch, ex); }
    }

    private void PollQuestChanges()
    {
        int watchEpoch;
        ulong contentId;
        var now = DateTimeOffset.UtcNow;
        lock (this.stateGate)
        {
            if (!this.Access.Quests || this.questDisposed || this.contextDisposed || !this.questWatching ||
                this.quests.View.Pending || now < this.nextQuestCheck) return;
            this.nextQuestCheck = now.AddSeconds(3);
            watchEpoch = this.questWatchEpoch;
            // Keep early lifecycle changes under the same gate as the watch check:
            // an older callback must not suspend a new capture started after Clear.
            if (!this.CanReadCurrentCharacter)
            {
                this.PauseQuestsForOffline("Character context is unavailable; keeping the saved quest cache.");
                return;
            }
            if (QuestLoading()) { this.SuspendQuestTracking("Changing areas; showing the cached quests until loading finishes."); return; }
            contentId = this.activeContentId;
        }
        try
        {
            var state = this.questReader.ReadState(true);
            int? captureEpoch = null;
            lock (this.stateGate)
            {
                if (this.questDisposed || this.contextDisposed || !this.questWatching || watchEpoch != this.questWatchEpoch ||
                    !this.CanReadCurrentCharacter || this.activeContentId != contentId) return;
                if (state.ContentId != contentId)
                {
                    this.OnDetectedCharacterMismatch();
                    return;
                }
                switch (this.questChanges.Observe(state))
                {
                    case QuestChange.DifferentCharacter:
                        this.OnDetectedCharacterMismatch();
                        return;
                    case QuestChange.Settling:
                        this.quests.Suspend("Quest state is changing; keeping the cached list while it settles.");
                        this.nextQuestCheck = now.AddMilliseconds(250);
                        break;
                    case QuestChange.Unchanged:
                        this.quests.MarkCurrent();
                        this.questLastChecked = now;
                        break;
                    case QuestChange.Changed:
                        captureEpoch = this.quests.Begin();
                        break;
                }
            }
            if (captureEpoch is { } epoch) this.CaptureQuestsOnFramework(epoch, state);
        }
        catch (Exception ex)
        {
            lock (this.stateGate)
            {
                if (this.questDisposed || this.contextDisposed || !this.questWatching || watchEpoch != this.questWatchEpoch ||
                    !this.CanReadCurrentCharacter || this.activeContentId != contentId) return;
                this.questChanges.Suspend();
                this.quests.Suspend(QuestError(ex));
                this.nextQuestCheck = now.AddSeconds(3);
            }
        }
    }

    private void QuestRefreshFailed(int epoch, Exception ex)
    {
        lock (this.stateGate)
        {
            if (this.questDisposed || this.contextDisposed || !this.questWatching ||
                !this.CanReadCurrentCharacter || !this.quests.IsCurrent(epoch)) return;
            this.quests.Fail(epoch, QuestError(ex));
            this.questChanges.Suspend();
            this.nextQuestCheck = DateTimeOffset.UtcNow.AddSeconds(3);
        }
    }

    private static string QuestError(Exception ex) => ex is InvalidOperationException ? ex.Message :
        $"Quest refresh unavailable ({ex.GetType().Name}); automatic refresh will retry.";

    private void SuspendQuestTracking(string reason)
    {
        lock (this.stateGate)
        {
            if (this.questDisposed || this.contextDisposed || !this.questWatching) return;
            ++this.questWatchEpoch;
            this.questChanges.Suspend();
            this.quests.Suspend(reason);
            this.nextQuestCheck = DateTimeOffset.UtcNow.AddSeconds(1);
        }
    }

    private void PauseQuestsForOffline(string reason)
    {
        lock (this.stateGate)
        {
            if (this.questDisposed) return;
            ++this.questWatchEpoch;
            this.questWatching = false;
            this.questChanges.Clear();
            this.questLastChecked = null;
            this.quests.Suspend(reason);
        }
    }

    private void RestoreQuestCache(QuestSnapshot? snapshot, bool resume)
    {
        lock (this.stateGate)
        {
            if (this.questDisposed || this.contextDisposed) return;
            this.ResetQuestTracking("No saved quests for this character; capture quests to start automatic updates.");
            if (snapshot is null || this.quests.Begin() is not { } epoch) return;
            this.quests.Complete(epoch, snapshot);
            this.quests.Suspend("Saved quest cache restored; waiting for the character to be available.");
            if (resume) this.ResumeQuestTracking();
        }
    }

    private void ResumeQuestTracking()
    {
        lock (this.stateGate)
        {
            if (!this.Access.Quests || this.questDisposed || this.contextDisposed || !this.CanReadCurrentCharacter ||
                this.quests.View.Snapshot is null) return;
            ++this.questWatchEpoch;
            this.questWatching = true;
            // A disk snapshot has no trusted native state stamp. Require a full
            // capture after two stable polls before claiming that it is current.
            this.questChanges.Clear();
            this.questLastChecked = null;
            this.quests.Suspend("Saved quest cache; automatic refresh will verify it after loading.");
            this.nextQuestCheck = DateTimeOffset.UtcNow;
        }
    }

    private void ResetQuestTracking(string reason)
    {
        lock (this.stateGate)
        {
            ++this.questWatchEpoch;
            this.questWatching = false;
            this.questChanges.Clear();
            this.questLastChecked = null;
            this.quests.Clear(reason);
        }
    }

    private void DisposeQuestTracking()
    {
        lock (this.stateGate)
        {
            this.questDisposed = true;
            this.ResetQuestTracking("Reader unloaded.");
            this.quests.Dispose();
        }
    }
}
