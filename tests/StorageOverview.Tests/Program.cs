using System.Text.Json;
using CompAI;

var cases = new (string Name, Action Run)[]
{
    ("known categories count metadata without reading contents or provider profile", Counts),
    ("missing and unreadable categories remain distinct", MissingAndErrors),
    ("entry and recursion budgets return explicit partial counts", Limits),
    ("linked category roots, ancestors and exported entries are excluded", Links),
    ("refresh is explicit, off-caller, cached and coalesced", BackgroundRefresh),
    ("unload cancels without publishing stale results", Disposal),
    ("refresh failures preserve prior results without exception text", Failure),
    ("refresh diagnostics contain only numeric timing", Diagnostics),
};
var failures = 0;
foreach (var (name, run) in cases)
{
    try { run(); Console.WriteLine("PASS " + name); }
    catch (SkippedCase ex) { Console.WriteLine("SKIP " + name + ": " + ex.Message); }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.GetBaseException().Message); }
}
return failures == 0 ? 0 : 1;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static StorageCategoryView Category(StorageOverviewView view, StorageCategory category) =>
    view.Categories.Single(x => x.Category == category);

static void Counts()
{
    using var fixture = new Fixture();
    fixture.Write("CompAI/companion-access.json", 1);
    fixture.Write("CompAI/companion-model.json", 2);
    fixture.Write("CompAI/companion-personalities.json", 3);
    fixture.Write("CompAI/game-chat-style.json", 4);
    fixture.Write("CompAI/.legacy-inspector-profile", 5);
    fixture.Write("CompAI/companion-beta.json", 6);
    fixture.Write("CompAI/unrelated-private-file.txt", 100);
    fixture.Write("CompAI/chats/active.json", 2);
    fixture.Write("CompAI/chats/synthetic-chat.json", 10);
    fixture.Write("CompAI/character-cache/synthetic.json", 20);
    fixture.Write("CompAI/character-cache/synthetic.json.bak", 5);
    fixture.Write("CompAI/character-cache/synthetic.json.quarantine", 7);
    fixture.Write("CompAI/character-cache/unexpected/ignored.json", 9);
    fixture.Write("CompAI/exports/chats/synthetic.json", 3);
    fixture.Write("CompAI/exports/inventory-pedigrees-synthetic/list.json", 4);
    fixture.Write("CompAI/exports/inventory-pedigrees-synthetic/list.txt", 5);
    fixture.Write("Inspector/companion/home/synthetic-secret", 1_000);
    // Exclusive file sharing denies content opens. Metadata is still measurable.
    using var locked = new FileStream(fixture.At("CompAI/chats/synthetic-chat.json"), FileMode.Open,
        FileAccess.ReadWrite, FileShare.None);
    var view = StorageOverviewScanner.Scan(fixture.Paths, CancellationToken.None);
    var settings = Category(view, StorageCategory.Settings);
    Check(settings.Complete && settings.FileCount == 6 && settings.Bytes == 21 && settings.FolderCount == 0, "settings scope or totals changed");
    var chats = Category(view, StorageCategory.Chats);
    Check(chats.Complete && chats.FileCount == 2 && chats.Bytes == 12, "chat metadata could not be measured with content locked");
    var cache = Category(view, StorageCategory.CharacterCache);
    Check(!cache.Complete && cache.FileCount == 3 && cache.Bytes == 32 && cache.FolderCount == 1, "cache counted nested data or missed recovery bytes");
    var exports = Category(view, StorageCategory.Exports);
    Check(exports.Complete && exports.FileCount == 3 && exports.Bytes == 12 && exports.FolderCount == 2, "export totals incorrect");
    var profile = Category(view, StorageCategory.CompanionProfile);
    Check(profile.Path == fixture.At("Inspector/companion") && profile.Bytes is null && profile.FileCount is null && !profile.Complete, "legacy profile location or exclusion changed");
    Check(!JsonSerializer.Serialize(view).Contains("synthetic-secret"), "private filenames leaked into the view");
    Check(view.RefreshedAtUtc is not null && view.ElapsedMilliseconds >= 0, "refresh metadata missing");
}

static void MissingAndErrors()
{
    using var fixture = new Fixture();
    var empty = StorageOverviewScanner.Scan(fixture.Paths, CancellationToken.None);
    foreach (var row in empty.Categories.Where(x => x.Category != StorageCategory.CompanionProfile))
        Check(row.Complete && row.FileCount == 0 && row.Bytes == 0, "absent category was not zero");
    fixture.Write("CompAI/chats", 20);
    var failed = Category(StorageOverviewScanner.Scan(fixture.Paths, CancellationToken.None), StorageCategory.Chats);
    Check(!failed.Complete && failed.Bytes is null && failed.Status.Contains("could not be read"), "invalid root was presented as an empty folder");
    Check(!failed.Status.Contains(fixture.Root), "error text exposed private path");
    Check(!StorageOverviewScanner.CheckDirectoryPath(fixture.At("never-created")), "nonexistent folder was openable");
    try { StorageOverviewScanner.CheckDirectoryPath(fixture.At("CompAI/chats")); }
    catch (IOException) { return; }
    throw new Exception("a file could be opened as a storage folder");
}

static void Limits()
{
    using var fixture = new Fixture();
    for (var i = 0; i < 10; i++) fixture.Write($"CompAI/chats/{i}.json", 1);
    fixture.Write("CompAI/exports/deeper/nested/secret.json", 30);
    var limited = StorageOverviewScanner.Scan(fixture.Paths, CancellationToken.None, new(MaximumEntries: 8));
    var chats = Category(limited, StorageCategory.Chats);
    Check(!chats.Complete && chats.FileCount == 2 && chats.Bytes == 2 && chats.Status.Contains("entry limit"), "entry limit was not enforced globally");
    var later = Category(limited, StorageCategory.Exports);
    Check(!later.Complete && later.Bytes is null, "unvisited category was presented as empty");
    var shallow = Category(StorageOverviewScanner.Scan(fixture.Paths, CancellationToken.None,
        new(MaximumDepth: 0)), StorageCategory.Exports);
    Check(!shallow.Complete && shallow.FileCount == 0 && shallow.FolderCount == 1 && shallow.Status.Contains("depth limit"), "recursion depth limit failed");
}

static void Links()
{
    using var fixture = new Fixture();
    fixture.Write("outside/hidden.json", 700);
    fixture.Write("CompAI/exports/safe.json", 3);
    var exportedLink = fixture.At("CompAI/exports/link");
    MakeDirectoryLink(exportedLink, fixture.At("outside"));
    try
    {
        var exports = Category(StorageOverviewScanner.Scan(fixture.Paths, CancellationToken.None), StorageCategory.Exports);
        Check(!exports.Complete && exports.Bytes == 3 && exports.FileCount == 1 && exports.FolderCount == 0, "linked export contents counted");
        ExpectLinked(() => StorageOverviewScanner.CheckDirectoryPath(exportedLink));
        ExpectLinked(() => StorageOverviewScanner.CheckDirectoryPath(Path.Combine(exportedLink, "child")));
        MakeDirectoryLink(fixture.At("CompAI/chats"), fixture.At("outside"));
        try
        {
            var chats = Category(StorageOverviewScanner.Scan(fixture.Paths, CancellationToken.None), StorageCategory.Chats);
            Check(!chats.Complete && chats.Bytes is null, "linked category root followed");
        }
        finally { Directory.Delete(fixture.At("CompAI/chats")); }
        MakeDirectoryLink(fixture.At("CompAI/companion-access.json"), fixture.At("outside"));
        try
        {
            var settings = Category(StorageOverviewScanner.Scan(fixture.Paths, CancellationToken.None), StorageCategory.Settings);
            Check(!settings.Complete && settings.Bytes == 0 && settings.FileCount == 0, "linked setting path counted");
        }
        finally { Directory.Delete(fixture.At("CompAI/companion-access.json")); }
    }
    finally { Directory.Delete(exportedLink); }
}

static void MakeDirectoryLink(string path, string destination)
{
    try { Directory.CreateSymbolicLink(path, destination); return; }
    catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException) { }
    // Junction creation does not require the symbolic-link privilege. Both are
    // reparse points and must be rejected by the production metadata scanner.
    var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in new[] { "/c", "mklink", "/J", path, destination }) start.ArgumentList.Add(argument);
    using var process = System.Diagnostics.Process.Start(start)!;
    process.WaitForExit();
    if (process.ExitCode != 0) throw new SkippedCase("the host cannot create a test symbolic link or junction");
}

static void ExpectLinked(Action action)
{
    try { action(); }
    catch (IOException) { return; }
    throw new Exception("a linked path was accepted");
}

static void BackgroundRefresh()
{
    using var fixture = new Fixture();
    using var started = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    var calls = 0;
    var caller = Environment.CurrentManagedThreadId;
    var worker = caller;
    using var service = new StorageOverviewService(fixture.Paths, token =>
    {
        Interlocked.Increment(ref calls);
        worker = Environment.CurrentManagedThreadId;
        started.Set();
        release.Wait(token);
        return StorageOverviewScanner.Scan(fixture.Paths, token);
    });
    var initial = service.View;
    for (var i = 0; i < 10_000; i++) Check(ReferenceEquals(initial, service.View), "view reads allocate new snapshots");
    Check(calls == 0 && initial.RefreshedAtUtc is null && !Directory.Exists(fixture.Root), "view read performed filesystem work");
    Check(service.Refresh() && started.Wait(TimeSpan.FromSeconds(5)), "background refresh did not start");
    Check(worker != caller && service.View.Busy, "scan ran on caller or busy state missing");
    Check(!service.Refresh() && calls == 1, "duplicate refresh was not coalesced");
    release.Set();
    WaitUntil(() => !service.View.Busy);
    Check(service.View.RefreshedAtUtc is not null && service.View.Error is null, "refresh result not published");
    Check(service.Refresh(), "completed refresh cannot be repeated explicitly");
    WaitUntil(() => !service.View.Busy);
    Check(calls == 2, "repeat refresh missing");
}

static void Disposal()
{
    using var fixture = new Fixture();
    using var started = new ManualResetEventSlim();
    using var finished = new ManualResetEventSlim();
    using var service = new StorageOverviewService(fixture.Paths, token =>
    {
        started.Set();
        try { token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); return StorageOverviewScanner.Empty(fixture.Paths); }
        finally { finished.Set(); }
    });
    Check(service.Refresh() && started.Wait(TimeSpan.FromSeconds(5)), "refresh failed to start");
    service.Dispose();
    Check(finished.Wait(TimeSpan.FromSeconds(5)), "unload did not cancel refresh");
    Check(!service.View.Busy && service.View.RefreshedAtUtc is null && !service.Refresh(), "disposed service published result or accepted work");
}

static void Failure()
{
    using var fixture = new Fixture();
    var calls = 0;
    using var service = new StorageOverviewService(fixture.Paths, token =>
    {
        if (++calls > 1) throw new IOException("private-secret-path-and-content");
        return StorageOverviewScanner.Scan(fixture.Paths, token);
    });
    service.Refresh();
    WaitUntil(() => !service.View.Busy);
    var previous = service.View;
    service.Refresh();
    WaitUntil(() => !service.View.Busy);
    Check(service.View.Error?.Contains("IOException") == true && !service.View.Error.Contains("private-secret"), "raw exception text exposed");
    Check(ReferenceEquals(previous.Categories, service.View.Categories) && previous.RefreshedAtUtc == service.View.RefreshedAtUtc, "failed refresh discarded previous results");
}

static void Diagnostics()
{
    using var fixture = new Fixture();
    PerformanceDiagnostics.Clear();
    using var service = new StorageOverviewService(fixture.Paths);
    service.Refresh();
    WaitUntil(() => !service.View.Busy);
    var report = PerformanceDiagnostics.Report;
    Check(report.Contains("Storage overview refresh") && report.Contains("1 samples"), "numeric refresh timing not recorded");
    Check(!report.Contains(fixture.Root) && !report.Contains("Inspector") && !report.Contains("Private provider profile"), "storage paths or rows entered copyable diagnostics");
}

static void WaitUntil(Func<bool> predicate)
{
    Check(SpinWait.SpinUntil(predicate, TimeSpan.FromSeconds(5)), "timed out waiting for refresh");
}

sealed class Fixture : IDisposable
{
    private readonly string parent = Path.GetFullPath(".test-tmp");
    internal string Root { get; } = Path.Combine(Path.GetFullPath(".test-tmp"), "compai-storage-overview-" + Guid.NewGuid().ToString("N"));
    internal PluginStoragePaths Paths => new(this.At("CompAI"), this.At("Inspector/companion"));
    internal string At(string relative) => Path.Combine(this.Root, relative.Replace('/', Path.DirectorySeparatorChar));
    internal void Write(string relative, int bytes)
    {
        var path = this.At(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }
    public void Dispose()
    {
        if (!this.Root.StartsWith(this.parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new Exception("Fixture cleanup escaped the test workspace.");
        if (Directory.Exists(this.Root)) Directory.Delete(this.Root, recursive: true);
    }
}

sealed class SkippedCase(string message) : Exception(message);
