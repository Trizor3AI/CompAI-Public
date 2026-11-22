using CompAI;

var tests = new (string Name, Action Run)[]
{
    ("new and changed states need two equal observations", TwoObservations),
    ("applied state never requests another capture", AppliedState),
    ("observation does not commit a failed or skipped capture", CommitSemantics),
    ("rapid A to B to C settles only C", RapidChanges),
    ("suspension requires fresh observations without losing applied state", Suspension),
    ("different character cannot observe or commit until clear", SessionBoundary),
    ("clear discards pending and applied state", ClearState),
    ("stamp equality includes completion and every active field", StateEquality),
    ("stamp owns its bytes and equal copies hash equally", OwnedBytes),
    ("active-only progress reuses completion until clear or changed completion bytes", CompletionReuse),
};
var failed = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} quest change tests passed (offline synthetic state; no native/game/network/model calls).");
return failed == 0 ? 0 : 1;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void CompletionReuse()
{
    var tracker = new QuestChangeTracker();
    Check(!tracker.CanReuseCompletion(State()), "no completion baseline before a native capture");
    tracker.Commit(State());
    tracker.Observe(State(2));
    tracker.Observe(State(2));
    Check(tracker.CanReuseCompletion(State(2)), "active sequence alone keeps completion baseline");
    Check(!tracker.CanReuseCompletion(State(3)), "unobserved state cannot reuse completion");
    Check(!tracker.CanReuseCompletion(State(2, 200)), "character mismatch cannot reuse completion");
    tracker.Suspend();
    Check(!tracker.CanReuseCompletion(State(2)), "suspension discards the candidate");
    tracker.Observe(State(2));
    tracker.Observe(State(2));
    Check(tracker.CanReuseCompletion(State(2)), "stable same completion bytes after area load");
    for (var index = 0; index < QuestCatalog.ExpectedCompletionBytes; index++)
    {
        var completed = new byte[QuestCatalog.ExpectedCompletionBytes];
        completed[index] = 1;
        var changed = new QuestStateStamp(100, completed, Active(2));
        tracker.Observe(changed);
        tracker.Observe(changed);
        Check(!tracker.CanReuseCompletion(changed), $"changed completion byte {index} forces native checks");
    }
    tracker.Clear();
    tracker.Observe(State(2));
    tracker.Observe(State(2));
    Check(!tracker.CanReuseCompletion(State(2)), "reload/permission clear cannot trust restored snapshots");
}

static QuestStateStamp State(byte sequence = 1, ulong contentId = 100) =>
    new(contentId, new byte[QuestCatalog.ExpectedCompletionBytes], Active(sequence));

static byte[] Active(byte sequence)
{
    var active = new byte[QuestStateStamp.ExpectedActiveBytes];
    active[0] = 42; // Synthetic quest ID; remaining bytes use the documented stamp layout.
    active[2] = sequence;
    return active;
}

static void Observe(QuestChangeTracker tracker, QuestStateStamp state, QuestChange expected) =>
    Check(tracker.Observe(state) == expected, $"expected {expected} for content ID {state.ContentId}");

static void TwoObservations()
{
    var tracker = new QuestChangeTracker();
    Observe(tracker, State(), QuestChange.Settling);
    Observe(tracker, State(), QuestChange.Changed);
    Check(tracker.Commit(State()), "initial commit rejected");
    Observe(tracker, State(2), QuestChange.Settling);
    Observe(tracker, State(2), QuestChange.Changed);
}

static void AppliedState()
{
    var tracker = new QuestChangeTracker();
    // An explicit verified capture may establish the baseline before any poll.
    Check(tracker.Commit(State()), "explicit baseline commit rejected");
    for (var i = 0; i < 10; i++)
        Observe(tracker, State(), QuestChange.Unchanged);
    Observe(tracker, State(2), QuestChange.Settling);
    Observe(tracker, State(), QuestChange.Settling);
    Observe(tracker, State(), QuestChange.Unchanged);
}

static void CommitSemantics()
{
    var tracker = new QuestChangeTracker();
    tracker.Commit(State());
    Observe(tracker, State(2), QuestChange.Settling);
    Observe(tracker, State(2), QuestChange.Changed);
    Observe(tracker, State(2), QuestChange.Changed);
    // A failed capture does not become the applied baseline just by being observed.
    tracker.Suspend();
    Observe(tracker, State(2), QuestChange.Settling);
    Observe(tracker, State(2), QuestChange.Changed);
    Check(tracker.Commit(State(2)), "successful capture commit rejected");
    Observe(tracker, State(2), QuestChange.Unchanged);
}

static void RapidChanges()
{
    var tracker = new QuestChangeTracker();
    tracker.Commit(State(1));
    Observe(tracker, State(2), QuestChange.Settling);
    Observe(tracker, State(3), QuestChange.Settling);
    Observe(tracker, State(3), QuestChange.Changed);
    Check(tracker.Commit(State(3)), "settled C commit rejected");
    Observe(tracker, State(3), QuestChange.Unchanged);
    Observe(tracker, State(2), QuestChange.Settling);
}

static void Suspension()
{
    var tracker = new QuestChangeTracker();
    tracker.Commit(State());
    tracker.Suspend();
    Observe(tracker, State(), QuestChange.Settling);
    Observe(tracker, State(), QuestChange.Unchanged);
    Observe(tracker, State(2), QuestChange.Settling);
    tracker.Suspend();
    Observe(tracker, State(2), QuestChange.Settling);
    Observe(tracker, State(2), QuestChange.Changed);
}

static void SessionBoundary()
{
    var tracker = new QuestChangeTracker();
    Observe(tracker, State(), QuestChange.Settling);
    Check(!tracker.SameCharacter(State(contentId: 200)), "new character accepted in old session");
    Observe(tracker, State(contentId: 200), QuestChange.DifferentCharacter);
    Check(!tracker.Commit(State(contentId: 200)), "foreign character committed over pending state");
    Observe(tracker, State(), QuestChange.Changed);
    tracker.Commit(State());
    tracker.Suspend();
    Check(!tracker.Commit(State(contentId: 200)), "suspension lost the session boundary");
    Observe(tracker, State(), QuestChange.Settling);
    Observe(tracker, State(), QuestChange.Unchanged);
    tracker.Clear();
    Check(tracker.SameCharacter(State(contentId: 200)), "clear did not permit a new session");
    Observe(tracker, State(contentId: 200), QuestChange.Settling);
    Observe(tracker, State(contentId: 200), QuestChange.Changed);
    Check(tracker.Commit(State(contentId: 200)), "new character could not commit after clear");
    Check(!tracker.Commit(State()), "late previous character committed over new session");
    Observe(tracker, State(contentId: 200), QuestChange.Unchanged);
}

static void ClearState()
{
    var tracker = new QuestChangeTracker();
    tracker.Commit(State());
    Observe(tracker, State(2), QuestChange.Settling);
    tracker.Clear();
    Observe(tracker, State(), QuestChange.Settling);
    Observe(tracker, State(), QuestChange.Changed);
    tracker.Clear();
    Observe(tracker, State(2), QuestChange.Settling);
    Observe(tracker, State(2), QuestChange.Changed);
}

static void StateEquality()
{
    var baseline = State();
    Check(baseline == State() && baseline.Equals((object)State()), "separate equal byte copies compare unequal");
    Check(baseline != State(contentId: 200), "different character stamps compare equal");
    Check(!State().Equals((QuestStateStamp?)null), "null stamp compares equal");
    // These are stamp bytes, not calls to the native copier. Its mapping has separate tests.
    foreach (var offset in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, QuestStateStamp.ExpectedActiveBytes - 1 })
    {
        var active = Active(1);
        active[offset] ^= 1;
        var changed = new QuestStateStamp(100, new byte[QuestCatalog.ExpectedCompletionBytes], active);
        Check(baseline != changed, $"active byte {offset} missing from equality");
        var tracker = new QuestChangeTracker();
        tracker.Commit(baseline);
        Observe(tracker, changed, QuestChange.Settling);
        Observe(tracker, changed, QuestChange.Changed);
    }
    foreach (var offset in new[] { 0, QuestCatalog.ExpectedCompletionBytes / 2, QuestCatalog.ExpectedCompletionBytes - 1 })
    {
        var completed = new byte[QuestCatalog.ExpectedCompletionBytes];
        completed[offset] = 1;
        var changed = new QuestStateStamp(100, completed, Active(1));
        Check(baseline != changed, $"completion byte {offset} missing from equality");
        var tracker = new QuestChangeTracker();
        tracker.Commit(baseline);
        Observe(tracker, changed, QuestChange.Settling);
        Observe(tracker, changed, QuestChange.Changed);
    }
}

static void OwnedBytes()
{
    var completed = new byte[QuestCatalog.ExpectedCompletionBytes];
    var active = Active(1);
    var state = new QuestStateStamp(100, completed, active);
    var baseline = State();
    Check(state.GetHashCode() == baseline.GetHashCode(), "equal stamps have different hash codes");
    completed[^1] = 1;
    active[2] = 99;
    active[^1] = 1;
    Check(state == baseline, "source buffer mutations changed a previously copied stamp");
    Check(state.GetHashCode() == baseline.GetHashCode(), "source buffer mutations changed stamp hash");
}
