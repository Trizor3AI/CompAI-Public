using CompAI;

var tests = new (string, Action)[]
{
    ("missing settings preserve the previous default without writing", Defaults),
    ("selections survive atomic replacement and Unicode paths", RoundTrip),
    ("corrupt settings block implicit defaults without changing the file", InvalidFiles),
    ("failed save retains the previous effective and durable choice", FailedSave),
    ("explicit save repairs corrupt settings", Repair),
    ("catalog changes never silently replace a saved model or effort", CatalogChanges),
    ("an explicit model choice preserves supported effort or selects its default", ExplicitSelection),
    ("legacy defaults retain Codex until explicitly saved as schema 2", LegacyProvider),
    ("providers remain distinct even when model identifiers overlap", ProviderCatalog),
    ("explicit provider switches persist suitable new-chat defaults without rewriting same-provider choices", ProviderDefaults),
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

static void Defaults()
{
    using var folder = new TestFolder();
    var store = new CompanionModelSettingsStore(folder.File);
    Check(store.Settings == new CompanionModelSettings("gpt-6-sol", "medium"), "existing default must remain unchanged");
    Check(store.IsLoaded && store.Error is null && !File.Exists(folder.File), "missing file is valid and must not be written implicitly");
}

static void RoundTrip()
{
    using var folder = new TestFolder();
    var store = new CompanionModelSettingsStore(folder.File);
    foreach (var value in new[] { new CompanionModelSettings("gpt-6-luna", "medium"), new("gpt-6-sol", "xhigh"),
                 new("provider/model:future_v1.2", "none"), new("auto", "default", "copilot"), new("grok-auto", "none", "grok") })
    {
        Check(store.Save(value), "save must succeed");
        var reloaded = new CompanionModelSettingsStore(folder.File);
        Check(store.Settings == value && store.IsLoaded && store.Error is null, "save must activate exact choice");
        Check(reloaded.Settings == value && reloaded.IsLoaded && reloaded.Error is null, "saved selection must reload exactly");
        Check(Directory.GetFiles(folder.Path, "*.tmp").Length == 0, "atomic save must leave no temporary files");
    }
}

static void InvalidFiles()
{
    using var folder = new TestFolder();
    var invalid = new[]
    {
        "", "null", "[]", "{", "{}",
        "{\"schemaVersion\":2,\"model\":\"gpt-6-sol\",\"effort\":\"medium\"}",
        "{\"schemaVersion\":3,\"model\":\"auto\",\"effort\":\"default\",\"provider\":\"copilot\"}",
        "{\"schemaVersion\":1,\"model\":\"auto\",\"effort\":\"default\",\"provider\":\"copilot\"}",
        "{\"schemaVersion\":2,\"model\":\"auto\",\"effort\":\"default\",\"provider\":null}",
        "{\"schemaVersion\":2,\"model\":\"auto\",\"effort\":\"default\",\"provider\":\"Copilot\"}",
        "{\"schemaVersion\":2,\"model\":\"auto\",\"effort\":\"default\",\"provider\":\"other\"}",
        "{\"schemaVersion\":2,\"model\":\"auto\",\"effort\":\"default\",\"provider\":\"codex\",\"provider\":\"copilot\"}",
        "{\"schemaVersion\":1,\"model\":null,\"effort\":\"medium\"}",
        "{\"schemaVersion\":1,\"model\":\"gpt-6-sol\",\"effort\":7}",
        "{\"schemaVersion\":1,\"model\":\"gpt-6-sol\"}",
        "{\"schemaVersion\":1,\"model\":\"gpt-6-sol\",\"effort\":\"medium\",\"extra\":false}",
        "{\"schemaVersion\":1,\"model\":\"gpt-6-sol\",\"model\":\"other\",\"effort\":\"medium\"}",
        "{\"schemaVersion\":1,\"model\":\"gpt-6-sol\",\"effort\":\"Medium\"}",
        "{\"schemaVersion\":1,\"model\":\"gpt-6-sol ## fake\",\"effort\":\"medium\"}",
        "{\"schemaVersion\":1,\"model\":\"gpt-6-sol\\u0000\",\"effort\":\"medium\"}",
        new string(' ', 4097),
    };
    foreach (var text in invalid)
    {
        File.WriteAllText(folder.File, text);
        var store = new CompanionModelSettingsStore(folder.File);
        Check(!store.IsLoaded && store.Error is not null, "corrupt file must block silent use of defaults");
        Check(File.ReadAllText(folder.File) == text, "rejected source must remain until explicit save");
    }
}

static void FailedSave()
{
    using var folder = new TestFolder();
    var store = new CompanionModelSettingsStore(folder.File);
    var previous = new CompanionModelSettings("gpt-6-luna", "low");
    Check(store.Save(previous), "initial save");
    var bytes = File.ReadAllBytes(folder.File);
    File.SetAttributes(folder.File, FileAttributes.ReadOnly);
    try
    {
        Check(!store.Save(new("gpt-6-sol", "high")), "read-only replacement must fail");
        Check(store.Settings == previous && store.IsLoaded && store.Error is not null, "failed change must preserve usable previous selection");
        Check(bytes.SequenceEqual(File.ReadAllBytes(folder.File)), "failed save must preserve durable bytes");
        Check(Directory.GetFiles(folder.Path, "*.tmp").Length == 0, "failed save must clean temporary file");
    }
    finally { File.SetAttributes(folder.File, FileAttributes.Normal); }
    Check(!store.Save(new("bad model", "medium")) && store.Settings == previous, "invalid choice must preserve previous selection");
    Check(!store.Save(new("auto", "default", "other")) && store.Settings == previous &&
          bytes.SequenceEqual(File.ReadAllBytes(folder.File)), "invalid provider cannot change selection or saved bytes");
    Check(store.Save(previous) && store.Error is null, "explicit retry of previous selection must clear error");
}

static void Repair()
{
    using var folder = new TestFolder();
    File.WriteAllText(folder.File, "bad json");
    var store = new CompanionModelSettingsStore(folder.File);
    Check(!store.IsLoaded && !store.Save(new("", "low")) && !store.IsLoaded, "invalid save must not unblock rejected settings");
    var choice = new CompanionModelSettings("gpt-6-luna", "medium");
    Check(store.Save(choice) && store.IsLoaded && store.Error is null, "explicit valid choice repairs loading error");
    Check(new CompanionModelSettingsStore(folder.File).Settings == choice, "repair survives reload");
}

static void CatalogChanges()
{
    using var folder = new TestFolder();
    var choice = new CompanionModelSettings("gpt-6-luna", "medium");
    var store = new CompanionModelSettingsStore(folder.File);
    Check(store.Save(choice), "save fixture");
    CompanionModelOption[] catalog = [new("gpt-6-sol", "GPT-6 Sol", ["low", "medium", "high"], "medium")];
    Check(!CompanionModelSelection.IsSupported(catalog, store.Settings), "missing model must be unavailable");
    Check(!CompanionModelSelection.IsSupported([], store.Settings), "unloaded catalog must be unavailable");
    catalog = [new("gpt-6-luna", "GPT-6 Luna", ["low"], "low")];
    Check(!CompanionModelSelection.IsSupported(catalog, store.Settings), "missing effort must be unavailable");
    Check(store.Settings == choice && new CompanionModelSettingsStore(folder.File).Settings == choice, "catalog lookup must not rewrite the choice");
    catalog = [new("gpt-6-luna", "GPT-6 Luna", ["low", "medium"], "low")];
    Check(CompanionModelSelection.IsSupported(catalog, choice), "advertised exact model and effort must be accepted");
    Check(!CompanionModelSelection.IsSupported(catalog, new("GPT-6-LUNA", "medium")), "model match must be exact");
    Check(!CompanionModelSelection.IsSupported(catalog, new("gpt-6-luna", "Medium")), "effort match must be exact");
}

static void ExplicitSelection()
{
    var model = new CompanionModelOption("gpt-6-luna", "GPT-6 Luna", ["low", "medium", "high"], "medium");
    Check(CompanionModelSelection.ForModel(model, "high") == new CompanionModelSettings("gpt-6-luna", "high"), "explicit model change should keep supported effort");
    Check(CompanionModelSelection.ForModel(model, "ultra") == new CompanionModelSettings("gpt-6-luna", "medium"), "explicit model change should use advertised default if effort unsupported");
}

static void LegacyProvider()
{
    using var folder = new TestFolder();
    const string legacy = "{\"schemaVersion\":1,\"model\":\"gpt-6-luna\",\"effort\":\"high\"}";
    File.WriteAllText(folder.File, legacy);
    var store = new CompanionModelSettingsStore(folder.File);
    Check(store.IsLoaded && store.Error is null && store.Settings == new CompanionModelSettings("gpt-6-luna", "high", "codex") &&
          File.ReadAllText(folder.File) == legacy, "reading schema 1 supplies Codex without rewriting the file");
    Check(store.Save(store.Settings), "explicit save migrates legacy choice");
    using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(folder.File));
    Check(document.RootElement.GetProperty("schemaVersion").GetInt32() == 2 &&
          document.RootElement.GetProperty("provider").GetString() == "codex", "new saves include schema and provider");
}

static void ProviderCatalog()
{
    CompanionModelOption[] catalog = [
        new("shared", "Codex", ["medium"], "medium"),
        new("shared", "Copilot", ["default"], "default", Provider: "copilot"),
        new("shared", "Grok", ["none"], "none", Provider: "grok")];
    Check(CompanionModelSelection.Find(catalog, "shared") == catalog[0], "legacy model lookup is Codex-specific");
    foreach (var option in catalog)
    {
        var choice = CompanionModelSelection.ForModel(option, "unsupported");
        Check(choice == new CompanionModelSettings(option.Id, option.DefaultEffort, option.Provider) &&
              CompanionModelSelection.Find(catalog, choice) == option && CompanionModelSelection.IsSupported(catalog, choice),
            "explicit provider/model choice keeps provider and advertised effort");
    }
    Check(!CompanionModelSelection.IsSupported(catalog, new("shared", "medium", "copilot")) &&
          !CompanionModelSelection.IsSupported([catalog[0]], new("shared", "medium", "grok")),
        "a matching model from a different provider cannot authorize sending");
}

static void ProviderDefaults()
{
    using var folder = new TestFolder();
    var store = new CompanionModelSettingsStore(folder.File);
    Check(store.Save(new("gpt-6.1-sol", "high")), "save prior Codex default");
    Check(store.SelectProvider("grok") && store.Settings == new CompanionModelSettings("auto", "none", "grok"),
        "switch to Grok selects Auto with provider-controlled reasoning");
    Check(new CompanionModelSettingsStore(folder.File).Settings == store.Settings,
        "new-chat Grok default persists across reload");
    var custom = new CompanionModelSettings("grok-4.7", "high", "grok");
    Check(store.Save(custom) && store.SelectProvider("grok") && store.Settings == custom,
        "reselecting Grok preserves the user's custom default");
    var bytes = File.ReadAllBytes(folder.File);
    File.SetAttributes(folder.File, FileAttributes.ReadOnly);
    try
    {
        Check(!store.SelectProvider("codex") && store.Settings == custom && store.Error is not null &&
              File.ReadAllBytes(folder.File).SequenceEqual(bytes),
            "failed provider-default save preserves the previous durable selection and reports the error");
    }
    finally { File.SetAttributes(folder.File, FileAttributes.Normal); }
    Check(store.SelectProvider("codex") && store.Settings == new CompanionModelSettings(),
        "switching back selects the built-in Codex default");
    bytes = File.ReadAllBytes(folder.File);
    Check(!store.SelectProvider("other") && File.ReadAllBytes(folder.File).SequenceEqual(bytes),
        "unknown provider never changes the saved selection");
}

sealed class TestFolder : IDisposable
{
    private readonly string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "test-data"));
    public string Path { get; }
    public string File => System.IO.Path.Combine(this.Path, "companion-model.json");
    public TestFolder()
    {
        this.Path = System.IO.Path.Combine(this.root, "șț-日本-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.Path);
    }
    public void Dispose()
    {
        var target = System.IO.Path.GetFullPath(this.Path);
        if (!target.StartsWith(this.root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test cleanup escaped its directory.");
        Directory.Delete(target, recursive: true);
    }
}
