namespace CompAI;

internal enum QuestChange { Settling, Unchanged, Changed, DifferentCharacter }

// Called under Plugin's state gate. A changed native state must be seen twice
// before replacing the cached snapshot, including after an area transition.
internal sealed class QuestChangeTracker
{
    private QuestStateStamp? applied;
    private QuestStateStamp? candidate;
    private ulong contentId;

    internal bool SameCharacter(QuestStateStamp state) => this.contentId == 0 || this.contentId == state.ContentId;

    // Only an in-session committed capture is a completion baseline. A restored
    // disk snapshot has no applied stamp and therefore always needs native checks.
    internal bool CanReuseCompletion(QuestStateStamp state) =>
        state.Equals(this.candidate) && state.HasSameCompletionState(this.applied);

    internal QuestChange Observe(QuestStateStamp state)
    {
        if (!this.SameCharacter(state)) return QuestChange.DifferentCharacter;
        this.contentId = state.ContentId;
        if (!state.Equals(this.candidate))
        {
            this.candidate = state;
            return QuestChange.Settling;
        }
        return state.Equals(this.applied) ? QuestChange.Unchanged : QuestChange.Changed;
    }

    internal bool Commit(QuestStateStamp state)
    {
        if (!this.SameCharacter(state)) return false;
        this.contentId = state.ContentId;
        this.applied = state;
        this.candidate = state;
        return true;
    }

    internal void Suspend() => this.candidate = null;

    internal void Clear()
    {
        this.contentId = 0;
        this.applied = null;
        this.candidate = null;
    }
}
