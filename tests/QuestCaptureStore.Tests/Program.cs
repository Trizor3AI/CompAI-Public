using System.Collections.Concurrent;
using CompAI;

var tests = new (string Name, Func<Task> Run)[]
{
    ("initial view has no saved snapshot or pending work", () => Run(InitialView)),
    ("duplicate begin leaves the existing capture current", () => Run(DuplicateBegin)),
    ("concurrent begins admit exactly one capture", () => Run(ConcurrentBegin)),
    ("clear defeats an already queued completion and failure", ClearQueuedWork),
    ("explicit lifecycle clear erases memory and invalidates pending captures", () => Run(LifecycleInvalidation)),
    ("zoning preserves saved data and rejects queued completion and failure", SuspendQueuedWork),
    ("suspending without a saved capture cannot invent one", () => Run(SuspendWithoutSnapshot)),
    ("new character session cannot receive the previous session's result", () => Run(SessionSwitch)),
    ("refresh keeps saved data and retry success replaces a failed stale cache", () => Run(RetryAndFailure)),
    ("failed initial capture remains empty and can recover", () => Run(InitialFailure)),
    ("matching state marks saved data current without replacing the snapshot", () => Run(MarkCurrent)),
    ("matching state cannot finish or refresh pending work", () => Run(MarkCurrentWhilePending)),
    ("finished capture ignores duplicate completion and late failure", () => Run(FinishedCapture)),
    ("dispose defeats queued work and prevents future capture", DisposeQueuedWork),
    ("dispose clears a previously saved result", () => Run(DisposeSavedResult)),
};
var failed = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} quest publication tests passed (offline; snapshot token stand-in).");
return failed == 0 ? 0 : 1;

static Task Run(Action run) { run(); return Task.CompletedTask; }
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static int Begin(QuestCaptureStore store) => store.Begin() ?? throw new Exception("capture was unexpectedly refused");
static void Empty(QuestCaptureStore store, string reason, bool stale = false)
{
    var view = store.View;
    Check(view.Snapshot is null && !view.Pending && view.UnavailableReason == reason && view.Stale == stale,
        $"expected empty view with reason '{reason}', got {view}");
}
static void Pending(QuestCaptureStore store, int epoch, QuestSnapshot? cached = null, bool stale = false)
{
    var view = store.View;
    Check(ReferenceEquals(view.Snapshot, cached) && view.Pending && view.UnavailableReason is null
        && view.Stale == stale && store.IsCurrent(epoch),
        "pending capture must retain the previous snapshot and freshness, with no previous error");
}
static void Saved(QuestCaptureStore store, QuestSnapshot expected)
{
    var view = store.View;
    Check(ReferenceEquals(view.Snapshot, expected) && !view.Pending && view.UnavailableReason is null && !view.Stale,
        "expected the saved capture to be published as current");
}
static void Stale(QuestCaptureStore store, QuestSnapshot expected, string reason)
{
    var view = store.View;
    Check(ReferenceEquals(view.Snapshot, expected) && !view.Pending && view.UnavailableReason == reason && view.Stale,
        $"expected the cached capture marked stale with reason '{reason}', got {view}");
}

static void InitialView()
{
    using var store = new QuestCaptureStore();
    Empty(store, "Not captured yet.");
    Check(!store.IsCurrent(0), "initial epoch is not pending work");
    store.Complete(0, new("unscheduled"));
    store.Fail(0, "unscheduled failure");
    store.MarkCurrent();
    Empty(store, "Not captured yet.");
}

static void DuplicateBegin()
{
    using var store = new QuestCaptureStore();
    var epoch = Begin(store);
    var pending = store.View;
    Check(store.Begin() is null, "duplicate begin must be rejected while a capture is pending");
    Check(ReferenceEquals(store.View, pending), "duplicate begin replaced pending state");
    Pending(store, epoch);
    var result = new QuestSnapshot("first");
    store.Complete(epoch, result);
    Saved(store, result);
    var next = Begin(store);
    Check(next != epoch && !store.IsCurrent(epoch), "retry must use a distinct publication epoch");
    Pending(store, next, result);
}

static void ConcurrentBegin()
{
    using var store = new QuestCaptureStore();
    var accepted = new ConcurrentBag<int>();
    Parallel.For(0, 32, _ => { if (store.Begin() is { } epoch) accepted.Add(epoch); });
    Check(accepted.Count == 1, "concurrent capture requests admitted more than one reader");
    Pending(store, accepted.Single());
}

static async Task ClearQueuedWork()
{
    using var store = new QuestCaptureStore();
    var saved = new QuestSnapshot("saved before clear");
    store.Complete(Begin(store), saved);
    var epoch = Begin(store);
    Pending(store, epoch, saved);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var completion = CompleteWhenReleased(store, epoch, release.Task, new("cleared result"));
    var failure = FailWhenReleased(store, epoch, release.Task, "cleared failure");
    Check(!completion.IsCompleted && !failure.IsCompleted, "callbacks must remain queued until after clear");
    store.Clear();
    Check(!store.IsCurrent(epoch), "clear must invalidate the queued read before it begins");
    Empty(store, "Quest capture cleared.");
    release.SetResult();
    await Task.WhenAll(completion, failure).WaitAsync(TimeSpan.FromSeconds(5));
    Empty(store, "Quest capture cleared.");
}

static void LifecycleInvalidation()
{
    // Plugin's login/logout handlers call Clear with these reasons. This verifies
    // the publication contract, not live Dalamud event delivery.
    foreach (var reason in new[]
    {
        "Logged out; quest capture cleared.",
        "Character logged in; capture quests after loading.",
    })
    {
        using var store = new QuestCaptureStore();
        var savedEpoch = Begin(store);
        store.Complete(savedEpoch, new("saved before lifecycle boundary"));
        store.Clear(reason);
        Empty(store, reason);
        var pendingEpoch = Begin(store);
        store.Clear(reason);
        Check(!store.IsCurrent(pendingEpoch), "lifecycle boundary left queued work current");
        store.Complete(savedEpoch, new("old saved result"));
        store.Complete(pendingEpoch, new("old queued result"));
        store.Fail(pendingEpoch, "old queued failure");
        Empty(store, reason);
    }
}

static async Task SuspendQueuedWork()
{
    using var store = new QuestCaptureStore();
    var cached = new QuestSnapshot("saved before zoning");
    store.Complete(Begin(store), cached);
    const string reason = "Area changed; checking quests after loading.";
    store.Suspend(reason);
    Stale(store, cached, reason);

    var epoch = Begin(store);
    Pending(store, epoch, cached, stale: true);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var completion = CompleteWhenReleased(store, epoch, release.Task, new("pre-zone callback"));
    var failure = FailWhenReleased(store, epoch, release.Task, "pre-zone failure");
    Check(!completion.IsCompleted && !failure.IsCompleted, "callbacks must remain queued until after suspension");
    store.Suspend(reason);
    Check(!store.IsCurrent(epoch), "suspension must invalidate pending work");
    Stale(store, cached, reason);

    var next = Begin(store);
    Check(next != epoch, "capture after zoning must use a new epoch");
    release.SetResult();
    await Task.WhenAll(completion, failure).WaitAsync(TimeSpan.FromSeconds(5));
    Pending(store, next, cached, stale: true);
    var updated = new QuestSnapshot("capture after zoning");
    store.Complete(next, updated);
    Saved(store, updated);
}

static void SuspendWithoutSnapshot()
{
    using var store = new QuestCaptureStore();
    var pending = Begin(store);
    store.Suspend("Loading.");
    Empty(store, "Loading.", stale: true);
    store.MarkCurrent();
    store.Complete(pending, new("late initial capture"));
    store.Fail(pending, "late initial failure");
    Empty(store, "Loading.", stale: true);
    var next = Begin(store);
    Pending(store, next, stale: true);
    var snapshot = new QuestSnapshot("first post-load capture");
    store.Complete(next, snapshot);
    Saved(store, snapshot);
}

static void SessionSwitch()
{
    using var store = new QuestCaptureStore();
    var previousCharacter = Begin(store);
    store.Clear("Logged out; quest capture cleared.");
    store.Clear("Character logged in; capture quests after loading.");
    var nextCharacter = Begin(store);
    Check(previousCharacter != nextCharacter, "new character must get a new epoch");
    store.Complete(previousCharacter, new("previous character"));
    store.Fail(previousCharacter, "previous character read failed");
    Pending(store, nextCharacter);
    var current = new QuestSnapshot("current character");
    store.Complete(nextCharacter, current);
    store.Complete(previousCharacter, new("late previous character"));
    store.Fail(previousCharacter, "late previous character failure");
    Saved(store, current);
}

static void RetryAndFailure()
{
    using var store = new QuestCaptureStore();
    var oldEpoch = Begin(store);
    var oldSnapshot = new QuestSnapshot("old saved data");
    store.Complete(oldEpoch, oldSnapshot);
    Saved(store, oldSnapshot);
    var retryEpoch = Begin(store);
    Pending(store, retryEpoch, oldSnapshot);
    store.Fail(oldEpoch, "stale failure");
    Pending(store, retryEpoch, oldSnapshot);
    store.Fail(retryEpoch, "reader unavailable");
    Stale(store, oldSnapshot, "reader unavailable");
    store.Complete(retryEpoch, new("completion after failure"));
    store.Complete(oldEpoch, oldSnapshot);
    Stale(store, oldSnapshot, "reader unavailable");
    var recoveredEpoch = Begin(store);
    Pending(store, recoveredEpoch, oldSnapshot, stale: true);
    store.Complete(retryEpoch, new("late retry completion"));
    store.Fail(retryEpoch, "late retry failure");
    Pending(store, recoveredEpoch, oldSnapshot, stale: true);
    var recovered = new QuestSnapshot("recovered data");
    store.Complete(recoveredEpoch, recovered);
    Saved(store, recovered);
}

static void InitialFailure()
{
    using var store = new QuestCaptureStore();
    var epoch = Begin(store);
    store.Fail(epoch, "reader unavailable");
    Empty(store, "reader unavailable", stale: true);
    store.MarkCurrent();
    store.Complete(epoch, new("completion after failure"));
    Empty(store, "reader unavailable", stale: true);
    var next = Begin(store);
    Pending(store, next, stale: true);
    var recovered = new QuestSnapshot("first successful capture");
    store.Complete(next, recovered);
    Saved(store, recovered);
}

static void MarkCurrent()
{
    using var store = new QuestCaptureStore();
    var cached = new QuestSnapshot("original snapshot and timestamp");
    store.Complete(Begin(store), cached);
    store.Suspend("Loading.");
    Stale(store, cached, "Loading.");
    store.MarkCurrent();
    Saved(store, cached);
    store.Fail(Begin(store), "temporary read failure");
    Stale(store, cached, "temporary read failure");
    store.MarkCurrent();
    Saved(store, cached);
    store.Clear();
    store.MarkCurrent();
    Empty(store, "Quest capture cleared.");
}

static void MarkCurrentWhilePending()
{
    using var store = new QuestCaptureStore();
    var cached = new QuestSnapshot("cached data");
    store.Complete(Begin(store), cached);
    store.Suspend("Loading.");
    var epoch = Begin(store);
    var pending = store.View;
    store.MarkCurrent();
    Check(ReferenceEquals(store.View, pending), "matching state altered a pending refresh");
    Pending(store, epoch, cached, stale: true);
    var refreshed = new QuestSnapshot("newly captured data");
    store.Complete(epoch, refreshed);
    Saved(store, refreshed);
}

static void FinishedCapture()
{
    using var store = new QuestCaptureStore();
    var epoch = Begin(store);
    var result = new QuestSnapshot("successful result");
    store.Complete(epoch, result);
    Check(!store.IsCurrent(epoch), "completed epoch cannot publish twice");
    store.Complete(epoch, new("duplicate result"));
    store.Fail(epoch, "late task failure");
    Saved(store, result);
}

static async Task DisposeQueuedWork()
{
    var store = new QuestCaptureStore();
    var epoch = Begin(store);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var completion = CompleteWhenReleased(store, epoch, release.Task, new("unloaded reader result"));
    var failure = FailWhenReleased(store, epoch, release.Task, "unloaded reader failure");
    store.Dispose();
    Check(!store.IsCurrent(epoch) && store.Begin() is null, "disposed reader admitted more work");
    Empty(store, "Reader unloaded.");
    release.SetResult();
    await Task.WhenAll(completion, failure).WaitAsync(TimeSpan.FromSeconds(5));
    Empty(store, "Reader unloaded.");
    store.Dispose();
    Check(store.Begin() is null, "repeated disposal re-enabled capture");
    Empty(store, "Reader unloaded.");
}

static void DisposeSavedResult()
{
    var store = new QuestCaptureStore();
    var epoch = Begin(store);
    store.Complete(epoch, new("saved before unload"));
    store.Dispose();
    Empty(store, "Reader unloaded.");
    store.Complete(epoch, new("late after unload"));
    store.Fail(epoch, "late failure after unload");
    store.Suspend("late suspension after unload");
    store.MarkCurrent();
    Empty(store, "Reader unloaded.");
}

static async Task CompleteWhenReleased(QuestCaptureStore store, int epoch, Task release, QuestSnapshot result)
{
    await release;
    // Deliberately call publication even without the caller's IsCurrent fast-path:
    // the store must reject stale in-flight work on its own.
    store.Complete(epoch, result);
}

static async Task FailWhenReleased(QuestCaptureStore store, int epoch, Task release, string reason)
{
    await release;
    store.Fail(epoch, reason);
}
