using CompAI;

var cases = new (string Name, Action Run)[]
{
    ("imports data without moving the authenticated profile", Migration),
    ("fresh installation uses the CompAI profile", Fresh),
    ("existing CompAI settings are never overwritten", Existing),
    ("interrupted import preserves originals and can retry", FailedImport),
    ("migration marker cannot supply an arbitrary profile path", InvalidMarker),
    ("migration refuses linked data directories", LinkedDirectory),
    ("reset after migration cannot restore old permissions or chats", ResetAfterMigration),
    ("existing migrated installs receive a reset-safe receipt", BackfillReceipt),
    ("invalid or oversized migration receipts stop legacy import", InvalidReceipt),
    ("migration refuses linked receipts", LinkedReceipt),
};
var failures = 0;
foreach (var (name, run) in cases)
{
    try { run(); Console.WriteLine("PASS " + name); }
    catch (SkippedCase ex) { Console.WriteLine("SKIP " + name + ": " + ex.Message); }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.GetBaseException().Message); }
}
return failures == 0 ? 0 : 1;

static void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
}

static void Migration()
{
    using var folder = new Fixture();
    folder.Write("Inspector/companion-access.json", "old permissions");
    folder.Write("Inspector/chats/active.json", "saved chat");
    folder.Write("Inspector/character-cache/sample.json", "saved character");
    folder.Write("Inspector/exports/example/inventory-pedigrees.json", "saved export");
    folder.Write("Inspector/companion/home/auth-placeholder", "synthetic credentials");
    folder.Write("Inspector/unrelated.txt", "not a plugin setting");
    var result = PluginStoragePaths.Prepare(folder.Current);
    Check(result.Configuration == folder.Current, "new configuration folder was not selected");
    Check(result.CompanionProfile == folder.At("Inspector/companion"), "credential profile moved");
    foreach (var file in new[] { "companion-access.json", "chats/active.json", "character-cache/sample.json", "exports/example/inventory-pedigrees.json" })
        Check(File.ReadAllText(folder.At("Inspector/" + file)) == File.ReadAllText(folder.At("CompAI/" + file)), "saved data changed");
    Check(!Directory.Exists(folder.At("CompAI/companion")), "credential files were copied");
    Check(!File.Exists(folder.At("CompAI/unrelated.txt")), "unrelated files were copied");
    Check(File.Exists(folder.At("Inspector/.compai-migration-complete")), "successful import did not leave a receipt");
    folder.Write("CompAI/companion-access.json", "changed permissions");
    var reloaded = PluginStoragePaths.Prepare(folder.Current);
    Check(result == reloaded, "reload changed the authenticated profile");
    Check(File.ReadAllText(folder.At("CompAI/companion-access.json")) == "changed permissions", "reload overwrote edited settings");
}

static void Fresh()
{
    using var folder = new Fixture();
    var result = PluginStoragePaths.Prepare(folder.Current);
    Check(result.CompanionProfile == folder.At("CompAI/companion"), "fresh profile used old identity");
}

static void Existing()
{
    using var folder = new Fixture();
    folder.Write("Inspector/companion-access.json", "old");
    folder.Write("CompAI/companion-access.json", "new");
    var result = PluginStoragePaths.Prepare(folder.Current);
    Check(result.CompanionProfile == folder.At("CompAI/companion"), "existing profile replaced");
    Check(File.ReadAllText(folder.At("CompAI/companion-access.json")) == "new", "existing data overwritten");
}

static void FailedImport()
{
    using var folder = new Fixture();
    folder.Write("Inspector/companion-access.json", "old");
    folder.Write("Inspector/companion", "blocked profile directory");
    ExpectFailure(() => PluginStoragePaths.Prepare(folder.Current));
    Check(!Directory.EnumerateFileSystemEntries(folder.Current).Any(), "failed import published partial settings");
    Check(File.ReadAllText(folder.At("Inspector/companion-access.json")) == "old", "original changed");
    Check(!File.Exists(folder.At("Inspector/.compai-migration-complete")), "failed import must remain retryable");
    File.Delete(folder.At("Inspector/companion"));
    PluginStoragePaths.Prepare(folder.Current);
    Check(File.ReadAllText(folder.At("CompAI/companion-access.json")) == "old", "retry did not finish");
}

static void ResetAfterMigration()
{
    using var folder = new Fixture();
    folder.Write("Inspector/companion-access.json", "old enabled permissions");
    folder.Write("Inspector/companion-personalities.json", "old enabled commands");
    folder.Write("Inspector/chats/active.json", "old chat");
    folder.Write("Inspector/companion/home/auth-placeholder", "synthetic credentials");
    PluginStoragePaths.Prepare(folder.Current);
    Directory.Delete(folder.Current, recursive: true); // Simulates Dalamud's reset in this disposable fixture.
    var reset = PluginStoragePaths.Prepare(folder.Current);
    Check(reset.CompanionProfile == folder.At("CompAI/companion"), "reset reused the old account profile");
    Check(!Directory.EnumerateFileSystemEntries(folder.Current).Any(), "reset imported old settings or chat data");
    Check(File.ReadAllText(folder.At("Inspector/companion-access.json")) == "old enabled permissions" &&
          File.ReadAllText(folder.At("Inspector/companion/home/auth-placeholder")) == "synthetic credentials",
        "reset changed the old settings or credentials");
    Check(PluginStoragePaths.Prepare(folder.Current) == reset, "a second reload restored the legacy profile");
}

static void BackfillReceipt()
{
    using var folder = new Fixture();
    folder.Write("CompAI/.legacy-inspector-profile", "CompAI uses the existing Inspector companion profile.\n");
    folder.Write("CompAI/companion-access.json", "current permissions");
    folder.Write("Inspector/companion-access.json", "old permissions");
    var loaded = PluginStoragePaths.Prepare(folder.Current);
    Check(loaded.CompanionProfile == folder.At("Inspector/companion"), "backfill moved the authenticated profile");
    Check(File.Exists(folder.At("Inspector/.compai-migration-complete")), "pre-receipt install was not upgraded");
    Check(File.ReadAllText(folder.At("CompAI/companion-access.json")) == "current permissions", "backfill changed current settings");
    Directory.Delete(folder.Current, recursive: true);
    var reset = PluginStoragePaths.Prepare(folder.Current);
    Check(reset.CompanionProfile == folder.At("CompAI/companion") &&
          !File.Exists(folder.At("CompAI/companion-access.json")), "backfilled install imported settings after reset");
}

static void InvalidReceipt()
{
    foreach (var contents in new[] { "invalid", new string('x', 257) })
    {
        using var folder = new Fixture();
        folder.Write("Inspector/companion-access.json", "old permissions");
        folder.Write("Inspector/.compai-migration-complete", contents);
        ExpectFailure(() => PluginStoragePaths.Prepare(folder.Current));
        Check(!Directory.EnumerateFileSystemEntries(folder.Current).Any(), "invalid receipt allowed import");
        Check(File.ReadAllText(folder.At("Inspector/.compai-migration-complete")) == contents, "invalid receipt was overwritten");
        folder.Write("CompAI/.legacy-inspector-profile", "CompAI uses the existing Inspector companion profile.\n");
        ExpectFailure(() => PluginStoragePaths.Prepare(folder.Current));
    }
    using var blocked = new Fixture();
    blocked.Write("Inspector/companion-access.json", "old permissions");
    Directory.CreateDirectory(blocked.At("Inspector/.compai-migration-complete"));
    ExpectFailure(() => PluginStoragePaths.Prepare(blocked.Current));
}

static void LinkedReceipt()
{
    using var folder = new Fixture();
    folder.Write("Inspector/companion-access.json", "old permissions");
    folder.Write("outside/receipt", "CompAI legacy import completed; version 1.\n");
    var link = folder.At("Inspector/.compai-migration-complete");
    try { File.CreateSymbolicLink(link, folder.At("outside/receipt")); }
    catch (UnauthorizedAccessException) { throw new SkippedCase("OS requires additional privilege to create a symbolic link"); }
    catch (IOException ex) when ((ex.HResult & 0xFFFF) == 1314) { throw new SkippedCase("OS requires additional privilege to create a symbolic link"); }
    try
    {
        ExpectFailure(() => PluginStoragePaths.Prepare(folder.Current));
        Check(!Directory.EnumerateFileSystemEntries(folder.Current).Any(), "linked receipt allowed import");
    }
    finally { File.Delete(link); }
}

static void InvalidMarker()
{
    using var folder = new Fixture();
    folder.Write("CompAI/.legacy-inspector-profile", "../../elsewhere");
    ExpectFailure(() => PluginStoragePaths.Prepare(folder.Current));
}

static void LinkedDirectory()
{
    using var folder = new Fixture();
    folder.Write("Inspector/companion-access.json", "old");
    Directory.CreateDirectory(folder.At("outside"));
    var link = folder.At("Inspector/chats");
    try { Directory.CreateSymbolicLink(link, folder.At("outside")); }
    catch (UnauthorizedAccessException) { throw new SkippedCase("OS requires additional privilege to create a symbolic link"); }
    catch (IOException ex) when ((ex.HResult & 0xFFFF) == 1314) { throw new SkippedCase("OS requires additional privilege to create a symbolic link"); }
    try
    {
        ExpectFailure(() => PluginStoragePaths.Prepare(folder.Current));
        Check(!Directory.EnumerateFileSystemEntries(folder.Current).Any(), "linked settings were published");
    }
    finally { Directory.Delete(link); }
}

static void ExpectFailure(Action action)
{
    try { action(); }
    catch (IOException) { return; }
    throw new Exception("Expected migration to stop safely");
}

sealed class Fixture : IDisposable
{
    private readonly string root = Path.Combine(Path.GetFullPath(".test-tmp"), "compai-migration-" + Guid.NewGuid().ToString("N"));
    internal string Current => this.At("CompAI");
    internal string At(string path) => Path.Combine(this.root, path.Replace('/', Path.DirectorySeparatorChar));
    internal void Write(string path, string text)
    {
        var target = this.At(path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, text);
    }
    public void Dispose()
    {
        if (Directory.Exists(this.root)) Directory.Delete(this.root, recursive: true);
    }
}

sealed class SkippedCase(string message) : Exception(message);
