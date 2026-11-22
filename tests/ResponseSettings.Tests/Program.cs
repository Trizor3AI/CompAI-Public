using CompAI;

var tests = new (string, Action)[]
{
    ("automatic reasoning tiers and every fixed limit remain bounded", TimeoutPolicy),
    ("missing settings use Automatic without creating a file", MissingSettings),
    ("all supported choices survive durable replacement and reload", RoundTrip),
    ("invalid files use Automatic and preserve the rejected source", InvalidFiles),
    ("failed persistence retains the previous effective and durable limit", FailedSave),
    ("an explicit valid save repairs corrupt settings", Repair),
};
var failures = 0;
foreach (var (name, test) in tests)
{
    try { test(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
return failures == 0 ? 0 : 1;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void TimeoutPolicy()
{
    foreach (var effort in new string?[] { null, "", "none", "minimal", "low", "medium", "default", "auto", "future-level", "High", "XHIGH" })
        Check(CompanionResponseTimeout.ResolveSeconds(0, effort) == 300, "ordinary or unknown effort must use five minutes");
    foreach (var effort in new[] { "high", "xhigh", "max", "ultra" })
        Check(CompanionResponseTimeout.ResolveSeconds(0, effort) == 600, "high effort must use ten minutes");
    for (var minutes = 1; minutes <= 30; minutes++)
        foreach (var effort in new string?[] { null, "low", "medium", "high", "ultra", "default" })
            Check(CompanionResponseTimeout.ResolveSeconds(minutes, effort) == minutes * 60, "fixed limit must override every effort");
    foreach (var minutes in new[] { int.MinValue, -1, 31, int.MaxValue })
    {
        var rejected = false;
        try { CompanionResponseTimeout.ResolveSeconds(minutes, "medium"); }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected, "invalid limits must be rejected rather than changing the requested duration");
    }
    Check(CompanionResponseTimeout.MinSeconds == 60 && CompanionResponseTimeout.MaxSeconds == 1800 &&
          CompanionResponseTimeout.OuterGraceSeconds == 60, "request and outer process bounds");
}

static void MissingSettings()
{
    using var folder = new TestFolder();
    var store = new CompanionResponseSettingsStore(folder.File);
    Check(store.Settings.Minutes == 0 && store.Error is null && !File.Exists(folder.File),
        "missing preference must use Automatic without an error or implicit write");
}

static void RoundTrip()
{
    using var folder = new TestFolder();
    var store = new CompanionResponseSettingsStore(folder.File);
    for (var minutes = 0; minutes <= 30; minutes++)
    {
        Check(store.Save(new(minutes)), "valid choice must save");
        var reloaded = new CompanionResponseSettingsStore(folder.File);
        Check(store.Settings.Minutes == minutes && store.Error is null &&
              reloaded.Settings.Minutes == minutes && reloaded.Error is null, "every exact choice must reload");
        Check(Directory.GetFiles(folder.Path, "*.tmp").Length == 0, "durable replacement must leave no temporary file");
    }
}

static void InvalidFiles()
{
    using var folder = new TestFolder();
    foreach (var source in new[]
    {
        "", "null", "[]", "{", "{}", "{\"schemaVersion\":1}", "{\"minutes\":5}",
        "{\"schemaVersion\":2,\"minutes\":5}", "{\"schemaVersion\":\"1\",\"minutes\":5}",
        "{\"schemaVersion\":1,\"minutes\":-1}", "{\"schemaVersion\":1,\"minutes\":31}",
        "{\"schemaVersion\":1,\"minutes\":5.5}", "{\"schemaVersion\":1,\"minutes\":\"5\"}",
        "{\"schemaVersion\":1,\"minutes\":null}", "{\"schemaVersion\":1,\"minutes\":true}",
        "{\"schemaVersion\":1,\"minutes\":5,\"minutes\":10}",
        "{\"schemaVersion\":1,\"schemaVersion\":1,\"minutes\":5}",
        "{\"schemaVersion\":1,\"minutes\":5,\"extra\":false}", new string(' ', 1025),
    })
    {
        File.WriteAllText(folder.File, source);
        var store = new CompanionResponseSettingsStore(folder.File);
        Check(store.Settings.Minutes == 0 && store.Error is not null, "bad file must visibly use Automatic");
        Check(File.ReadAllText(folder.File) == source, "rejected source must remain unchanged until explicit save");
    }
}

static void FailedSave()
{
    using var folder = new TestFolder();
    var store = new CompanionResponseSettingsStore(folder.File);
    Check(store.Save(new(7)), "initial save");
    var original = File.ReadAllBytes(folder.File);
    File.SetAttributes(folder.File, FileAttributes.ReadOnly);
    try
    {
        Check(!store.Save(new(20)), "read-only replacement must fail");
        Check(store.Settings.Minutes == 7 && store.Error is not null, "failed publication must retain prior effective limit");
        Check(original.SequenceEqual(File.ReadAllBytes(folder.File)), "failed save must retain prior durable bytes");
        Check(Directory.GetFiles(folder.Path, "*.tmp").Length == 0, "failed save must remove its temporary file");
    }
    finally { File.SetAttributes(folder.File, FileAttributes.Normal); }
    foreach (var invalid in new[] { -1, 31, int.MaxValue })
        Check(!store.Save(new(invalid)) && store.Settings.Minutes == 7 &&
              original.SequenceEqual(File.ReadAllBytes(folder.File)), "invalid limits must leave both prior choices intact");
    Check(store.Save(new(12)) && store.Error is null && new CompanionResponseSettingsStore(folder.File).Settings.Minutes == 12,
        "a later successful save must clear the failure");
}

static void Repair()
{
    using var folder = new TestFolder();
    File.WriteAllText(folder.File, "corrupt");
    var store = new CompanionResponseSettingsStore(folder.File);
    Check(store.Save(new(0)), "explicit Automatic selection must replace a corrupt file");
    var reloaded = new CompanionResponseSettingsStore(folder.File);
    Check(reloaded.Settings.Minutes == 0 && reloaded.Error is null, "repaired preference must reload normally");
}

sealed class TestFolder : IDisposable
{
    private readonly string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "test-data"));
    public string Path { get; }
    public string File => System.IO.Path.Combine(this.Path, "companion-response.json");

    public TestFolder()
    {
        this.Path = System.IO.Path.Combine(this.root, "șț-日本-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.Path);
    }

    public void Dispose()
    {
        var resolved = System.IO.Path.GetFullPath(this.Path);
        if (!resolved.StartsWith(this.root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test cleanup escaped its root.");
        Directory.Delete(resolved, recursive: true);
    }
}
