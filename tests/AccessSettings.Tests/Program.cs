using CompAI;

var tests = new (string, Action)[]
{
    ("selected macro migrates off through schema25 without rewriting choices", SelectedMacroAccessChecks.Migration),
    ("schema26 selected macro requires a unique explicit boolean", SelectedMacroAccessChecks.Validation),
    ("selected macro grants revoke retained state and requests with failed saves closed", SelectedMacroAccessChecks.Transitions),
    ("schema 24 migrates Island materials and Cosmic missions off without rewriting choices", RegionalResourceAccessChecks.Migration),
    ("schema 26 Island material and Cosmic mission fields require exact unique booleans", RegionalResourceAccessChecks.Validation),
    ("Island materials and Cosmic missions revoke cached observations and requests, preserving fail-closed saves", RegionalResourceAccessChecks.Transitions),
    ("schema 23 migrates Island and Cosmic progression off without rewriting choices", RegionalProgressAccessChecks.Migration),
    ("schema 26 Island and Cosmic fields require exact unique booleans", RegionalProgressAccessChecks.Validation),
    ("Island and Cosmic grants revoke cached observations and requests, preserving fail-closed saves", RegionalProgressAccessChecks.Transitions),
    ("menu and log permissions migrate off, persist independently and revoke", MenuAccessChecks.Run),
    ("schema 21 migration leaves later activity permissions disabled", ActivityAccessChecks.Migration),
    ("schema 22 migration preserves old choices and leaves Mahjong off", ActivityAccessChecks.MahjongMigration),
    ("schema 26 activity fields validate exact unique booleans", ActivityAccessChecks.Validation),
    ("schema 26 activity grants revoke cancel and fail closed independently", ActivityAccessChecks.Transitions),
    ("missing settings default to no access without creating a file", Defaults),
    ("schema 26 permissions independently survive reload with Unicode paths", RoundTrip),
    ("schema 20 migration never implies Triple Triad battle-chocobo or PvP access", ReaderBatchAccessChecks.Migration),
    ("schema 26 reader grants require exact unique booleans and reject misplaced fields", ReaderBatchAccessChecks.Validation),
    ("new reader permissions grant revoke and cancel independently with failed saves closed", ReaderBatchAccessChecks.Transitions),
    ("progression permissions migrate off, validate and revoke independently", ProgressionAccessChecks.Run),
    ("book and log permissions migrate off, validate and revoke independently", BookAccessChecks.Run),
    ("Bestiary access migrates off and independently grants, validates and revokes", BestiaryAccessChecks.Run),
    ("Variant notebook access migrates off, validates, persists and revokes", VariantNotebookAccessChecks.Run),
    ("crafting and fishing notebook grants migrate off and revoke independently", NotebookAccessChecks.Run),
    ("new collectible permissions migrate off through schema 13 while preserving existing grants", CollectibleMigration),
    ("new collectible permissions require exact unique explicit schema 26 booleans", CollectibleValidation),
    ("new collectible permissions independently revoke concurrent requests and fail closed", CollectibleTransitions),
    ("collectible category mapping grants only explicitly permitted categories", CollectibleCategoryAccess),
    ("collections permission defaults off across schemas 1 through 12 without changing existing grants", CollectionsMigration),
    ("collections permission remains a unique explicit boolean in schema 26", CollectionsValidation),
    ("collections permission is independent, revokes concurrent requests and fails closed", CollectionsTransitions),
    ("armoury permission migrates off and requires a unique explicit schema 12 boolean", ArmouryPermissions),
    ("storage permissions migrate off and reject malformed or misplaced grants", StoragePermissions),
    ("storage permissions revoke concurrent requests independently and fail closed", StorageTransitions),
    ("target permission stays off when migrating every previous schema", TargetMigration),
    ("target permission requires schema 10 and a unique explicit boolean", TargetValidation),
    ("target permission is separate from NPC access and revokes all pending answers", TargetTransitions),
    ("GC permission migrates off, validates strictly and revokes concurrent access", GcSupplyPermissions),
    ("version 1 settings preserve access with newer permissions off", MigrateVersionOne),
    ("version 2 settings preserve all access choices with newer permissions off", MigrateVersionTwo),
    ("version 3 settings preserve all access choices with character and NPC permissions off", MigrateVersionThree),
    ("version 4 combined character/location grant migrates into both separate permissions", MigrateVersionFour),
    ("version 5 preserves all permission choices and defaults spoilers off", MigrateVersionFive),
    ("version 6 preserves existing access choices with currencies off", MigrateVersionSix),
    ("version 7 preserves existing access choices with duties off", MigrateVersionSeven),
    ("duty permission requires schema 8 and an explicit unique boolean field", InvalidDutyPermissions),
    ("duty permission is independent and revocation cancels background answers", DutyTransitions),
    ("failed duty save disables access and cancels active answers", FailedDutySave),
    ("currency permission requires schema 7 and an explicit unique boolean field", InvalidCurrencyPermissions),
    ("currency permission is independent and revocation cancels only active answers", CurrencyTransitions),
    ("failed currency save disables effective access and cancels an active answer", FailedCurrencySave),
    ("character, location and NPC permissions are independently granted and revoked durably", GameContextPermissions),
    ("malformed, unknown, duplicate and oversized settings fail closed and remain untouched", InvalidFiles),
    ("new permissions require schema 4 and explicit unique boolean fields", InvalidGameContextPermissions),
    ("location permission requires schema 5 and an explicit unique boolean field", InvalidLocationPermissions),
    ("spoiler preference requires schema 6 and an explicit unique boolean field", InvalidSpoilerPreferences),
    ("spoiler toggles preserve unrelated readers and cancel only when restrictive", SpoilerTransitions),
    ("failed spoiler save disables effective access and cancels an active answer", FailedSpoilerSave),
    ("failed atomic replacement preserves the good file and disables session access", FailedSave),
    ("replacement retry recovers only the documented unchanged-file failure", AccessSaveRetryChecks.TransientReplacement),
    ("replacement retry exhaustion preserves old choices and permits explicit recovery", AccessSaveRetryChecks.ExhaustionAndExplicitRecovery),
    ("replacement retry rejects unrelated and potentially destructive failure codes", AccessSaveRetryChecks.OtherErrorsAreNotRetried),
    ("replacement retry stops when either original input is missing", AccessSaveRetryChecks.MissingInputsAreNotRetried),
    ("replacement retry leaves a real read-only target untouched without retrying", AccessSaveRetryChecks.ReadOnlyTargetIsNotRetried),
    ("explicit save replaces corrupt settings and clears the error", Repair),
    ("context selection requires both permission and a ready matching character snapshot", ContextSelection),
    ("plugin sends and retries do not inherit game-chat delivery from command sends", SendOrigin),
};
var failures = 0;
foreach (var (name, test) in tests.Where(test => args.Length == 0 ||
    args.Any(filter => test.Item1.Contains(filter, StringComparison.OrdinalIgnoreCase))))
{
    try { test(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
return failures == 0 ? 0 : 1;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void SendOrigin()
{
    using var folder = new TestFolder();
    var store = new CompanionAccessSettingsStore(folder.File);
    Check(store.Save(new CompanionAccessSettings(GameChatReplies: true)), "enable command reply setting");
    var plugin = new Plugin(store);
    Check(plugin.SendChatQuestion("command", replyInGameChat: true) && plugin.LastReplyInGameChat &&
        plugin.GameChatInputCount == 1, "command send must request output and echo its input");
    Check(plugin.SendChatQuestion("plugin") && !plugin.LastReplyInGameChat && plugin.GameChatInputCount == 1,
        "plugin send must neither request game-chat output nor echo input");
    Check(plugin.RetryChatQuestion("test", "test") && !plugin.LastReplyInGameChat && plugin.GameChatInputCount == 1,
        "plugin retry must also remain local");
}

static void GcSupplyPermissions()
{
    using var folder = new TestFolder();
    const string legacy = "{\"schemaVersion\":8,\"inventory\":false,\"quests\":false,\"webSearch\":false,\"chatHistory\":false,\"gameChatReplies\":false,\"characterContext\":false,\"npcDialogue\":true,\"location\":false,\"spoilersAllowed\":false,\"currencies\":false,\"duties\":true}";
    File.WriteAllText(folder.File, legacy);
    var store = new CompanionAccessSettingsStore(folder.File);
    Check(store.Error is null && store.Settings.Duties && store.Settings.NpcDialogue && !store.Settings.GcSupply,
        "schema 8 must preserve existing grants without adding GC access");
    var modern = legacy.Replace("\"schemaVersion\":8", "\"schemaVersion\":9");
    foreach (var invalid in new[] { modern, legacy[..^1] + ",\"gcSupply\":true}",
        modern[..^1] + ",\"gcSupply\":null}", modern[..^1] + ",\"gcSupply\":true,\"gcSupply\":false}" })
    {
        File.WriteAllText(folder.File, invalid);
        var rejected = new CompanionAccessSettingsStore(folder.File);
        Check(rejected.Error is not null && rejected.Settings == new CompanionAccessSettings(), "invalid GC grants must fail closed");
        Check(File.ReadAllText(folder.File) == invalid, "invalid file must be preserved");
    }
    Check(store.Save(new(GcSupply: true)), "grant must save");
    var plugin = new Plugin(store);
    plugin.SetPendingQuestion(null);
    plugin.SetBackgroundQuestion(true);
    plugin.SetAccess(false, false, false, gcSupply: false);
    Check(!plugin.Access.GcSupply && plugin.CancelCount == 1 && plugin.UnrelatedReaderActions == 0,
        "revocation must cancel background use without touching other readers");
    Check(!new CompanionAccessSettingsStore(folder.File).Settings.GcSupply, "revocation must survive reload");
    Check(store.Save(new(GcSupply: true)), "restore grant for failed save case");
    plugin.SetBackgroundQuestion(true);
    File.SetAttributes(folder.File, FileAttributes.ReadOnly);
    try
    {
        plugin.SetAccess(false, false, false, gcSupply: false);
        Check(plugin.Access == new CompanionAccessSettings() && plugin.AccessSaveError is not null && plugin.CancelCount == 2,
            "failed GC save must disable effective access and cancel pending use");
    }
    finally { File.SetAttributes(folder.File, FileAttributes.Normal); }
}

static void Defaults()
{
    using var folder = new TestFolder();
    var store = new CompanionAccessSettingsStore(folder.File);
    Check(store.Settings == new CompanionAccessSettings() && store.Error is null, "missing file must be valid all-off state");
    Check(!File.Exists(folder.File), "loading must not write");
}

static string WithoutNewCollectibleFields(string contents)
{
    foreach (var field in new[] { "fashionAccessories", "facewear", "emotes", "orchestrion", "achievements", "aetherCurrents", "sharedFates", "trust", "blueMage", "gatheringLog", "bestiary", "craftingLog", "fishingLog" })
        contents = contents.Replace(",\"" + field + "\":false", "");
    return contents;
}

static void CollectibleMigration()
{
    using var folder = new TestFolder();
    var introduced = new (string Name, int Version)[]
    {
        ("inventory", 1), ("quests", 1), ("webSearch", 1), ("chatHistory", 2), ("gameChatReplies", 3),
        ("characterContext", 4), ("npcDialogue", 4), ("location", 5), ("spoilersAllowed", 6),
        ("currencies", 7), ("duties", 8), ("gcSupply", 9), ("targetInfo", 10),
        ("retainers", 11), ("saddlebag", 11), ("housing", 11), ("fcChest", 11), ("armoury", 12),
        ("collections", 13),
    };
    for (var version = 1; version <= 13; ++version)
    foreach (var enabled in new[] { false, true })
    {
        var legacy = new Dictionary<string, object> { ["schemaVersion"] = version };
        foreach (var field in introduced.Where(field => field.Version <= version)) legacy[field.Name] = enabled;
        var original = System.Text.Json.JsonSerializer.Serialize(legacy);
        File.WriteAllText(folder.File, original);
        var store = new CompanionAccessSettingsStore(folder.File);
        var expected = new CompanionAccessSettings(enabled, enabled, enabled, enabled && version >= 2,
            enabled && version >= 3, enabled && version >= 4, enabled && version >= 4,
            enabled && version >= 4, enabled && version >= 6, enabled && version >= 7,
            enabled && version >= 8, enabled && version >= 9, enabled && version >= 10,
            enabled && version >= 11, enabled && version >= 11, enabled && version >= 11,
            enabled && version >= 11, enabled && version >= 12, enabled && version >= 13);
        Check(store.Error is null && store.Settings == expected,
            $"schema {version} preserves existing grants without granting a new collectible category");
        Check(File.ReadAllText(folder.File) == original, "migration must not rewrite the prior settings");
        Check(store.Save(store.Settings), $"saving migrated schema {version} failed: {store.Error}");
        var upgraded = new CompanionAccessSettingsStore(folder.File);
        Check(upgraded.Error is null && upgraded.Settings == expected,
            $"saving migrated schema {version} must keep all four new permissions off: {upgraded.Error}");
        foreach (var field in new[] { "fashionAccessories", "facewear", "emotes", "orchestrion" })
        {
            legacy[field] = enabled;
            var misplaced = System.Text.Json.JsonSerializer.Serialize(legacy);
            File.WriteAllText(folder.File, misplaced);
            var rejected = new CompanionAccessSettingsStore(folder.File);
            Check(rejected.Error is not null && rejected.Settings == new CompanionAccessSettings(),
                $"schema {version} must reject even a false {field} field");
            Check(File.ReadAllText(folder.File) == misplaced, "rejected legacy settings must remain untouched");
            legacy.Remove(field);
        }
    }
}

static void CollectibleValidation()
{
    using var folder = new TestFolder();
    var store = new CompanionAccessSettingsStore(folder.File);
    var expected = new CompanionAccessSettings(Collections: true, FashionAccessories: true, Facewear: true,
        Emotes: true, Orchestrion: true);
    Check(store.Save(expected), "explicit collectible grants must save");
    var valid = File.ReadAllText(folder.File);
    using (var saved = System.Text.Json.JsonDocument.Parse(valid))
    {
        Check(saved.RootElement.GetProperty("schemaVersion").GetInt32() == 26 &&
            saved.RootElement.EnumerateObject().Count() == 55, "schema 26 has exactly 54 permissions and the schema field");
    }
    Check(new CompanionAccessSettingsStore(folder.File).Settings == expected, "collectible grants round trip");
    var invalid = new List<string>
    {
        valid.WithoutActivityFields().Replace("\"schemaVersion\":26", "\"schemaVersion\":13").Replace(",\"tripleTriad\":false", "").Replace(",\"battleChocobo\":false", "").Replace(",\"pvp\":false", "").Replace(",\"shops\":false", "").Replace(",\"glamour\":false", "").Replace(",\"huntingLog\":false", "").Replace(",\"sightseeingLog\":false", "").Replace(",\"challengeLog\":false", "").Replace(",\"variantNotebook\":false", ""),
        valid.Replace("\"schemaVersion\":26", "\"schemaVersion\":27"),
        valid[..^1] + ",\"extra\":false}",
    };
    foreach (var field in new[] { "fashionAccessories", "facewear", "emotes", "orchestrion" })
    {
        var marker = "\"" + field + "\":true";
        invalid.Add(valid.Replace("," + marker, ""));
        invalid.Add(valid.Replace(marker, "\"" + char.ToUpperInvariant(field[0]) + field[1..] + "\":true"));
        invalid.Add(valid.Replace(marker, marker + ",\"" + field + "\":false"));
        foreach (var value in new[] { "null", "\"true\"", "1", "{}", "[]" })
            invalid.Add(valid.Replace(marker, "\"" + field + "\":" + value));
    }
    foreach (var contents in invalid)
    {
        File.WriteAllText(folder.File, contents);
        var rejected = new CompanionAccessSettingsStore(folder.File);
        Check(rejected.Error is not null && rejected.Settings == new CompanionAccessSettings(),
            "missing, malformed, duplicate, misplaced or unknown collectible fields must fail closed");
        Check(File.ReadAllText(folder.File) == contents, "invalid settings must remain untouched");
    }
}

static void CollectibleTransitions()
{
    var cases = new (string Name, Func<CompanionAccessSettings, bool, CompanionAccessSettings> Configure,
        Action<Plugin, bool> Set)[]
    {
        ("fashionAccessories", (settings, value) => settings with { FashionAccessories = value },
            (plugin, value) => plugin.SetAccess(false, false, false, fashionAccessories: value)),
        ("facewear", (settings, value) => settings with { Facewear = value },
            (plugin, value) => plugin.SetAccess(false, false, false, facewear: value)),
        ("emotes", (settings, value) => settings with { Emotes = value },
            (plugin, value) => plugin.SetAccess(false, false, false, emotes: value)),
        ("orchestrion", (settings, value) => settings with { Orchestrion = value },
            (plugin, value) => plugin.SetAccess(false, false, false, orchestrion: value)),
    };
    foreach (var (name, configure, set) in cases)
    {
        using var folder = new TestFolder();
        var store = new CompanionAccessSettingsStore(folder.File);
        var enabled = new CompanionAccessSettings(Duties: true, Armoury: true, Collections: true,
            FashionAccessories: true, Facewear: true, Emotes: true, Orchestrion: true);
        var initial = configure(enabled, false);
        Check(store.Save(initial), "seed other independent grants");
        var plugin = new Plugin(store);
        set(plugin, true);
        Check(plugin.Access == enabled && plugin.CancelCount == 0 && plugin.UnrelatedReaderActions == 0,
            name + " grant must preserve every other permission without capturing other readers");
        plugin.SetAccess(false, false, false);
        Check(plugin.Access == enabled && new CompanionAccessSettingsStore(folder.File).Settings == enabled,
            "existing callers must preserve every collectible grant");
        plugin.SetPendingQuestion("active collectible question");
        plugin.SetBackgroundQuestion(true);
        set(plugin, false);
        Check(plugin.Access == initial && plugin.CancelCount == 1 && !plugin.AnyPendingQuestion &&
            plugin.UnrelatedReaderActions == 0, name + " revocation cancels active and background requests independently");
        Check(new CompanionAccessSettingsStore(folder.File).Settings == initial, "revocation must survive reload");
        set(plugin, true);
        set(plugin, false);
        Check(plugin.CancelCount == 1, "idle revocation must not invent a canceled request");
        set(plugin, true);
        plugin.SetBackgroundQuestion(true);
        File.SetAttributes(folder.File, FileAttributes.ReadOnly);
        try
        {
            set(plugin, false);
            Check(plugin.Access == new CompanionAccessSettings() && plugin.AccessSaveError is not null &&
                plugin.CancelCount == 2 && !plugin.AnyPendingQuestion,
                "failed collectible revocation must disable all effective access and cancel pending use");
            Check(new CompanionAccessSettingsStore(folder.File).Settings == enabled, "failed save preserves the durable file");
        }
        finally { File.SetAttributes(folder.File, FileAttributes.Normal); }
        plugin.SetAccess(false, false, false);
        Check(plugin.AccessSaveError is null && new CompanionAccessSettingsStore(folder.File).Settings == new CompanionAccessSettings(),
            "retry must durably record the effective off settings");
    }
}

static void CollectibleCategoryAccess()
{
    var cases = new (CompanionAccessSettings Settings, string[] Categories)[]
    {
        (new(), []),
        (new(Collections: true), ["mounts", "minions"]),
        (new(FashionAccessories: true), ["fashion_accessories"]),
        (new(Facewear: true), ["facewear"]),
        (new(Emotes: true), ["emotes"]),
        (new(Orchestrion: true), ["orchestrion"]),
        (new(YokaiCollection: true), []),
        (new(Collections: true, YokaiCollection: true), ["mounts", "minions"]),
        (new(Collections: true, FashionAccessories: true, Facewear: true, Emotes: true, Orchestrion: true),
            ["mounts", "minions", "fashion_accessories", "facewear", "emotes", "orchestrion"]),
    };
    foreach (var (settings, expected) in cases)
    {
        Check(CollectionAccess.Categories(settings).SequenceEqual(expected), "category mapping must preserve exact grant scope and order");
        foreach (var category in new[] { "mounts", "minions", "fashion_accessories", "facewear", "emotes", "orchestrion",
            "yokai_minions", "Mounts", "fashionAccessories", "all", "", "unknown" })
            Check(CollectionAccess.Allows(settings, category) == expected.Contains(category),
                "only an explicitly granted exact category may pass: " + category);
        Check(!CollectionAccess.Allows(settings, null!), "null categories fail closed");
    }
}

static void CollectionsMigration()
{
    using var folder = new TestFolder();
    var introduced = new (string Name, int Version)[]
    {
        ("inventory", 1), ("quests", 1), ("webSearch", 1), ("chatHistory", 2), ("gameChatReplies", 3),
        ("characterContext", 4), ("npcDialogue", 4), ("location", 5), ("spoilersAllowed", 6),
        ("currencies", 7), ("duties", 8), ("gcSupply", 9), ("targetInfo", 10),
        ("retainers", 11), ("saddlebag", 11), ("housing", 11), ("fcChest", 11), ("armoury", 12),
    };
    for (var version = 1; version <= 12; ++version)
    foreach (var enabled in new[] { false, true })
    {
        var legacy = new Dictionary<string, object> { ["schemaVersion"] = version };
        foreach (var field in introduced.Where(field => field.Version <= version)) legacy[field.Name] = enabled;
        var original = System.Text.Json.JsonSerializer.Serialize(legacy);
        File.WriteAllText(folder.File, original);
        var store = new CompanionAccessSettingsStore(folder.File);
        var expected = new CompanionAccessSettings(enabled, enabled, enabled, enabled && version >= 2,
            enabled && version >= 3, enabled && version >= 4, enabled && version >= 4,
            enabled && version >= 4, enabled && version >= 6, enabled && version >= 7,
            enabled && version >= 8, enabled && version >= 9, enabled && version >= 10,
            enabled && version >= 11, enabled && version >= 11, enabled && version >= 11,
            enabled && version >= 11, enabled && version >= 12);
        Check(store.Error is null && store.Settings == expected && !store.Settings.Collections,
            $"schema {version} must preserve existing choices with collections off");
        Check(File.ReadAllText(folder.File) == original, "migration must not rewrite legacy settings");
        Check(store.Save(store.Settings), $"saving migrated schema {version} failed: {store.Error}");
        var upgraded = new CompanionAccessSettingsStore(folder.File);
        Check(upgraded.Error is null && upgraded.Settings == expected,
            $"saving migrated schema {version} must preserve its grants with collections off: {upgraded.Error}");
        legacy["collections"] = enabled;
        var misplaced = System.Text.Json.JsonSerializer.Serialize(legacy);
        File.WriteAllText(folder.File, misplaced);
        var rejected = new CompanionAccessSettingsStore(folder.File);
        Check(rejected.Error is not null && rejected.Settings == new CompanionAccessSettings(),
            "older schemas must reject even a false collections field");
        Check(File.ReadAllText(folder.File) == misplaced, "rejected legacy settings remain untouched");
    }
}

static void CollectionsValidation()
{
    using var folder = new TestFolder();
    var store = new CompanionAccessSettingsStore(folder.File);
    var expected = new CompanionAccessSettings(Collections: true);
    Check(store.Save(expected), "explicit collections grant saves");
    var valid = File.ReadAllText(folder.File);
    Check(new CompanionAccessSettingsStore(folder.File).Settings == expected,
        "collections grants no other reader access");
    const string marker = "\"collections\":true";
    var invalid = new List<string>
    {
        valid.Replace("," + marker, ""),
        valid.Replace(marker, "\"Collections\":true"),
        valid.Replace(marker, marker + ",\"collections\":false"),
        valid.Replace(marker, marker + ",\"extra\":false"),
        valid.Replace("\"schemaVersion\":26", "\"schemaVersion\":27"),
    };
    foreach (var value in new[] { "null", "\"true\"", "1", "{}", "[]" })
        invalid.Add(valid.Replace(marker, "\"collections\":" + value));
    foreach (var contents in invalid)
    {
        File.WriteAllText(folder.File, contents);
        var rejected = new CompanionAccessSettingsStore(folder.File);
        Check(rejected.Error is not null && rejected.Settings == new CompanionAccessSettings(),
            "missing, malformed, duplicate or unsupported collections grants must fail closed");
        Check(File.ReadAllText(folder.File) == contents, "invalid settings must remain untouched");
    }
}

static void CollectionsTransitions()
{
    using var folder = new TestFolder();
    var store = new CompanionAccessSettingsStore(folder.File);
    var initial = new CompanionAccessSettings(Duties: true, Armoury: true);
    Check(store.Save(initial), "seed independent reader grants");
    var plugin = new Plugin(store);
    plugin.SetAccess(false, false, false, collections: true);
    Check(plugin.Access == initial with { Collections = true } && plugin.CancelCount == 0 &&
        plugin.UnrelatedReaderActions == 0, "enabling collections must neither capture nor change other readers");
    plugin.SetAccess(false, false, false);
    Check(plugin.Access.Collections && new CompanionAccessSettingsStore(folder.File).Settings.Collections,
        "existing callers must preserve the durable collections choice");
    plugin.SetPendingQuestion("active collections question");
    plugin.SetBackgroundQuestion(true);
    plugin.SetAccess(false, false, false, collections: false);
    Check(plugin.Access == initial && plugin.CancelCount == 1 && !plugin.AnyPendingQuestion &&
        plugin.UnrelatedReaderActions == 0, "collections revocation cancels active and background requests");
    Check(new CompanionAccessSettingsStore(folder.File).Settings == initial, "revocation must survive reload");
    plugin.SetAccess(false, false, false, collections: true);
    plugin.SetAccess(false, false, false, collections: false);
    Check(plugin.CancelCount == 1, "revocation without a pending request must not cancel");
    plugin.SetAccess(false, false, false, collections: true);
    plugin.SetBackgroundQuestion(true);
    File.SetAttributes(folder.File, FileAttributes.ReadOnly);
    try
    {
        plugin.SetAccess(false, false, false, collections: false);
        Check(plugin.Access == new CompanionAccessSettings() && plugin.AccessSaveError is not null &&
            plugin.CancelCount == 2 && !plugin.AnyPendingQuestion && plugin.UnrelatedReaderActions == 0,
            "failed restrictive save must disable effective access and cancel pending requests");
        Check(new CompanionAccessSettingsStore(folder.File).Settings == initial with { Collections = true },
            "failed save must preserve the previous durable choices");
    }
    finally { File.SetAttributes(folder.File, FileAttributes.Normal); }
    plugin.SetAccess(false, false, false, collections: false);
    Check(plugin.AccessSaveError is null && new CompanionAccessSettingsStore(folder.File).Settings == new CompanionAccessSettings(),
        "retrying the effective off settings must durably clear collections access");
}

static void StoragePermissions()
{
    using var folder = new TestFolder();
    var store = new CompanionAccessSettingsStore(folder.File);
    Check(store.Save(new(Inventory: true, TargetInfo: true)), "save current permissions");
    var modern = File.ReadAllText(folder.File);
    var legacy = WithoutNewCollectibleFields(modern).WithoutActivityFields().Replace("\"schemaVersion\":26", "\"schemaVersion\":10").Replace(",\"tripleTriad\":false", "").Replace(",\"battleChocobo\":false", "").Replace(",\"pvp\":false", "").Replace(",\"shops\":false", "").Replace(",\"glamour\":false", "").Replace(",\"huntingLog\":false", "").Replace(",\"sightseeingLog\":false", "").Replace(",\"challengeLog\":false", "").Replace(",\"variantNotebook\":false", "")
        .Replace(",\"armoury\":false", "").Replace(",\"collections\":false", "");
    var fields = new[] { "retainers", "saddlebag", "housing", "fcChest" };
    foreach (var field in fields) legacy = legacy.Replace(",\"" + field + "\":false", "");
    File.WriteAllText(folder.File, legacy);
    var migrated = new CompanionAccessSettingsStore(folder.File);
    Check(migrated.Error is null && migrated.Settings == new CompanionAccessSettings(Inventory: true, TargetInfo: true),
        "schema 10 keeps old permissions and grants no storage access");
    Check(File.ReadAllText(folder.File) == legacy, "loading must not rewrite legacy settings");
    foreach (var field in fields)
    {
        var marker = "\"" + field + "\":false";
        foreach (var invalid in new[] { modern.Replace("," + marker, ""),
            modern.Replace(marker, "\"" + field + "\":null"),
            modern.Replace(marker, "\"" + field + "\":1"),
            modern.Replace(marker, "\"" + field + "\":\"true\""),
            modern.Replace(marker, marker + ",\"" + field + "\":true"),
            legacy[..^1] + "," + marker + "}" })
        {
            File.WriteAllText(folder.File, invalid);
            var rejected = new CompanionAccessSettingsStore(folder.File);
            Check(rejected.Error is not null && rejected.Settings == new CompanionAccessSettings(),
                "invalid or older-schema storage grant must fail closed: " + field);
            Check(File.ReadAllText(folder.File) == invalid, "rejected file must remain untouched");
        }
    }
}

static void StorageTransitions()
{
    var grants = new (CompanionAccessSettings Settings, Action<Plugin> Revoke)[] {
        (new(Retainers: true), plugin => plugin.SetAccess(false, false, false, retainers: false)),
        (new(Saddlebag: true), plugin => plugin.SetAccess(false, false, false, saddlebag: false)),
        (new(Housing: true), plugin => plugin.SetAccess(false, false, false, housing: false)),
        (new(FcChest: true), plugin => plugin.SetAccess(false, false, false, fcChest: false)),
        (new(Armoury: true), plugin => plugin.SetAccess(false, false, false, armoury: false)),
    };
    foreach (var (grant, revoke) in grants)
    {
        using var folder = new TestFolder();
        var store = new CompanionAccessSettingsStore(folder.File);
        var initial = grant with { NpcDialogue = true };
        Check(store.Save(initial), "save storage grant");
        var plugin = new Plugin(store);
        plugin.SetAccess(false, false, false);
        Check(plugin.Access == initial, "omitted settings retain independent storage grant");
        plugin.SetPendingQuestion("first question"); plugin.SetBackgroundQuestion(true);
        revoke(plugin);
        Check(plugin.Access == new CompanionAccessSettings(NpcDialogue: true) && !plugin.AnyPendingQuestion &&
            plugin.CancelCount == 1 && plugin.UnrelatedReaderActions == 0,
            "revoke cancels active/background work without touching unrelated readers");
        Check(new CompanionAccessSettingsStore(folder.File).Settings == plugin.Access, "revocation survives restart");
        Check(store.Save(initial), "restore grant"); plugin.SetBackgroundQuestion(true);
        File.SetAttributes(folder.File, FileAttributes.ReadOnly);
        try
        {
            revoke(plugin);
            Check(plugin.Access == new CompanionAccessSettings() && plugin.AccessSaveError is not null &&
                !plugin.AnyPendingQuestion && plugin.CancelCount == 2, "failed revocation disables effective access and cancels pending answers");
            Check(new CompanionAccessSettingsStore(folder.File).Settings == initial, "failed save preserves durable original");
        }
        finally { File.SetAttributes(folder.File, FileAttributes.Normal); }
    }
}

static void ArmouryPermissions()
{
    using var folder = new TestFolder();
    var store = new CompanionAccessSettingsStore(folder.File);
    Check(store.Save(new(Inventory: true, Retainers: true)), "current permission fixture");
    var modern = File.ReadAllText(folder.File);
    var legacy = WithoutNewCollectibleFields(modern).WithoutActivityFields().Replace("\"schemaVersion\":26", "\"schemaVersion\":11").Replace(",\"tripleTriad\":false", "").Replace(",\"battleChocobo\":false", "").Replace(",\"pvp\":false", "").Replace(",\"shops\":false", "").Replace(",\"glamour\":false", "").Replace(",\"huntingLog\":false", "").Replace(",\"sightseeingLog\":false", "").Replace(",\"challengeLog\":false", "").Replace(",\"variantNotebook\":false", "")
        .Replace(",\"armoury\":false", "").Replace(",\"collections\":false", "");
    File.WriteAllText(folder.File, legacy);
    var migrated = new CompanionAccessSettingsStore(folder.File);
    Check(migrated.Error is null && migrated.Settings.Inventory && migrated.Settings.Retainers && !migrated.Settings.Armoury,
        "schema 11 retains grants without newly granting Armoury");
    foreach (var invalid in new[] {
        modern.Replace(",\"armoury\":false", ""),
        modern.Replace("\"armoury\":false", "\"armoury\":null"),
        modern.Replace("\"armoury\":false", "\"armoury\":\"true\""),
        modern.Replace("\"armoury\":false", "\"armoury\":1"),
        modern.Replace("\"armoury\":false", "\"armoury\":false,\"armoury\":true"),
        legacy[..^1] + ",\"armoury\":true}" })
    {
        File.WriteAllText(folder.File, invalid);
        var rejected = new CompanionAccessSettingsStore(folder.File);
        Check(rejected.Error is not null && rejected.Settings == new CompanionAccessSettings(),
            "invalid or misplaced Armoury permission fails closed");
        Check(File.ReadAllText(folder.File) == invalid, "invalid settings preserved");
    }
    Check(store.Save(new(Armoury: true)), "saving Armoury grant failed: " + store.Error);
    Check(new CompanionAccessSettingsStore(folder.File).Settings == new CompanionAccessSettings(Armoury: true),
        "Armoury grants no inventory, equipped or other storage access");
}

static void TargetMigration()
{
    using var folder = new TestFolder();
    var introduced = new (string Name, int Version)[]
    {
        ("inventory", 1), ("quests", 1), ("webSearch", 1), ("chatHistory", 2), ("gameChatReplies", 3),
        ("characterContext", 4), ("npcDialogue", 4), ("location", 5), ("spoilersAllowed", 6),
        ("currencies", 7), ("duties", 8), ("gcSupply", 9),
    };
    for (var version = 1; version <= 9; ++version)
    {
        var legacy = new Dictionary<string, object> { ["schemaVersion"] = version };
        foreach (var field in introduced.Where(field => field.Version <= version)) legacy[field.Name] = true;
        var original = System.Text.Json.JsonSerializer.Serialize(legacy);
        File.WriteAllText(folder.File, original);
        var store = new CompanionAccessSettingsStore(folder.File);
        Check(store.Error is null && !store.Settings.TargetInfo && store.Settings.Inventory && store.Settings.Quests &&
            store.Settings.WebSearch && store.Settings.NpcDialogue == (version >= 4),
            $"schema {version} must preserve its grants while leaving target permission off");
        Check(File.ReadAllText(folder.File) == original, "migration read must not alter the previous file");
        Check(store.Save(store.Settings) && !new CompanionAccessSettingsStore(folder.File).Settings.TargetInfo,
            "explicit format upgrade must not add player access");
        legacy["targetInfo"] = false;
        File.WriteAllText(folder.File, System.Text.Json.JsonSerializer.Serialize(legacy));
        Check(new CompanionAccessSettingsStore(folder.File).Error is not null,
            "older schemas must reject even a false newer target field");
    }
}

static void TargetValidation()
{
    using var folder = new TestFolder();
    var store = new CompanionAccessSettingsStore(folder.File);
    Check(store.Save(new(TargetInfo: true)), "modern target grant saves");
    var valid = File.ReadAllText(folder.File);
    Check(new CompanionAccessSettingsStore(folder.File).Settings.TargetInfo &&
        !new CompanionAccessSettingsStore(folder.File).Settings.NpcDialogue, "target grant never grants NPC access");
    foreach (var invalid in new[]
    {
        valid.Replace(",\"targetInfo\":true", ""),
        valid.Replace("\"targetInfo\":true", "\"targetInfo\":null"),
        valid.Replace("\"targetInfo\":true", "\"targetInfo\":\"true\""),
        valid.Replace("\"targetInfo\":true", "\"targetInfo\":1"),
        valid.Replace("\"targetInfo\":true", "\"targetInfo\":true,\"targetInfo\":false"),
        valid.Replace("\"schemaVersion\":26", "\"schemaVersion\":27"),
    })
    {
        File.WriteAllText(folder.File, invalid);
        var rejected = new CompanionAccessSettingsStore(folder.File);
        Check(rejected.Error is not null && rejected.Settings == new CompanionAccessSettings(), "invalid target settings fail closed");
        Check(File.ReadAllText(folder.File) == invalid, "invalid target settings remain untouched");
    }
    Check(store.Save(new(TargetInfo: true)) && new CompanionAccessSettingsStore(folder.File).Settings.TargetInfo,
        "explicit repair must persist the chosen target grant");
}

static void TargetTransitions()
{
    using var folder = new TestFolder();
    var store = new CompanionAccessSettingsStore(folder.File);
    var initial = new CompanionAccessSettings(NpcDialogue: true, GcSupply: true);
    Check(store.Save(initial), "independent existing grants save");
    var plugin = new Plugin(store);
    plugin.SetAccess(false, false, false, targetInfo: true);
    Check(plugin.Access == initial with { TargetInfo = true } && plugin.CancelCount == 0 && plugin.UnrelatedReaderActions == 0,
        "target grant changes no other reader and does not cancel pending work");
    plugin.SetAccess(false, false, false);
    Check(plugin.Access.TargetInfo && new CompanionAccessSettingsStore(folder.File).Settings.TargetInfo,
        "callers omitting target permission preserve its durable choice");
    plugin.SetPendingQuestion("active target question");
    plugin.SetBackgroundQuestion(true);
    plugin.SetAccess(false, false, false, targetInfo: false);
    Check(plugin.Access == initial && plugin.CancelCount == 1 && plugin.UnrelatedReaderActions == 0 &&
        !plugin.AnyPendingQuestion && !new CompanionAccessSettingsStore(folder.File).Settings.TargetInfo,
        "revocation cancels active and background answers without revoking NPC dialogue");
    plugin.SetAccess(false, false, false, targetInfo: true);
    plugin.SetPendingQuestion(null);
    plugin.SetBackgroundQuestion(true);
    File.SetAttributes(folder.File, FileAttributes.ReadOnly);
    try
    {
        plugin.SetAccess(false, false, false, targetInfo: false);
        Check(plugin.Access == new CompanionAccessSettings() && plugin.AccessSaveError is not null &&
            plugin.CancelCount == 2 && !plugin.AnyPendingQuestion, "failed revocation disables all effective access and cancels background use");
        Check(new CompanionAccessSettingsStore(folder.File).Settings.TargetInfo, "failed replacement preserves the previous durable file");
    }
    finally { File.SetAttributes(folder.File, FileAttributes.Normal); }
    plugin.SetAccess(false, false, false, targetInfo: false);
    Check(plugin.AccessSaveError is null && !new CompanionAccessSettingsStore(folder.File).Settings.TargetInfo,
        "retry save clears the error and durably records target revocation");
}

static void RoundTrip()
{
    using var folder = new TestFolder();
    var store = new CompanionAccessSettingsStore(folder.File);
    // Check each independently serialized field in both directions without thousands
    // of redundant durable writes; there are no combination-specific save rules.
    const ulong allPermissions = (1UL << 54) - 1;
    var masks = new[] { 0UL, allPermissions }.Concat(Enumerable.Range(0, 54).Select(bit => 1UL << bit))
        .Concat(Enumerable.Range(0, 54).Select(bit => allPermissions ^ (1UL << bit)));
    foreach (var mask in masks)
    {
        var expected = new CompanionAccessSettings((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0,
            (mask & 8) != 0, (mask & 16) != 0, (mask & 32) != 0, (mask & 64) != 0, (mask & 128) != 0,
            (mask & 256) != 0, (mask & 512) != 0, (mask & 1024) != 0, (mask & 2048) != 0, (mask & 4096) != 0,
            (mask & 8192) != 0, (mask & 16384) != 0, (mask & 32768) != 0, (mask & 65536) != 0,
            (mask & 131072) != 0, (mask & 262144) != 0, (mask & 524288) != 0, (mask & 1048576) != 0,
            (mask & 2097152) != 0, (mask & 4194304) != 0, (mask & 8388608) != 0, (mask & 16777216) != 0,
            (mask & 33554432) != 0, (mask & 67108864) != 0, (mask & 134217728) != 0, (mask & 268435456) != 0, (mask & 536870912) != 0, (mask & 1073741824UL) != 0, (mask & 2147483648UL) != 0, (mask & 4294967296UL) != 0, (mask & (1UL << 33)) != 0, (mask & (1UL << 34)) != 0, (mask & (1UL << 35)) != 0, (mask & (1UL << 36)) != 0, (mask & (1UL << 37)) != 0,
            (mask & (1UL << 38)) != 0, (mask & (1UL << 39)) != 0, (mask & (1UL << 40)) != 0,
            TripleTriadDecks: (mask & (1UL << 41)) != 0, RaceChocobo: (mask & (1UL << 42)) != 0,
            Verminion: (mask & (1UL << 43)) != 0, FreeCompany: (mask & (1UL << 44)) != 0,
            OccultRecords: (mask & (1UL << 45)) != 0, YokaiCollection: (mask & (1UL << 46)) != 0,
            CollectionTools: (mask & (1UL << 47)) != 0, Mahjong: (mask & (1UL << 48)) != 0,
            IslandProgress: (mask & (1UL << 49)) != 0, CosmicResearch: (mask & (1UL << 50)) != 0,
            IslandMaterials: (mask & (1UL << 51)) != 0, CosmicMissions: (mask & (1UL << 52)) != 0,
            SelectedMacro: (mask & (1UL << 53)) != 0);
        Check(store.Save(expected) && store.Settings == expected && store.Error is null,
            $"successful save must update effective state (mask {mask}): {store.Error}");
        using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(folder.File));
        Check(saved.RootElement.GetProperty("schemaVersion").GetInt32() == 26, "saved settings must use version 26");
        Check(saved.RootElement.GetProperty("mahjong").GetBoolean() == expected.Mahjong,
            "Mahjong permission must persist independently");
        Check(saved.RootElement.GetProperty("islandProgress").GetBoolean() == expected.IslandProgress &&
            saved.RootElement.GetProperty("cosmicResearch").GetBoolean() == expected.CosmicResearch,
            "Island and Cosmic progression permissions must persist independently");
        Check(saved.RootElement.GetProperty("tripleTriad").GetBoolean() == expected.TripleTriad &&
            saved.RootElement.GetProperty("battleChocobo").GetBoolean() == expected.BattleChocobo &&
            saved.RootElement.GetProperty("pvp").GetBoolean() == expected.Pvp,
            "new reader permissions must each persist independently");
        Check(saved.RootElement.GetProperty("collections").GetBoolean() == expected.Collections,
            "collections permission must persist independently");
        Check(saved.RootElement.GetProperty("fashionAccessories").GetBoolean() == expected.FashionAccessories &&
            saved.RootElement.GetProperty("facewear").GetBoolean() == expected.Facewear &&
            saved.RootElement.GetProperty("emotes").GetBoolean() == expected.Emotes &&
            saved.RootElement.GetProperty("orchestrion").GetBoolean() == expected.Orchestrion,
            "each new collectible permission must persist independently");
        Check(saved.RootElement.GetProperty("retainers").GetBoolean() == expected.Retainers &&
            saved.RootElement.GetProperty("saddlebag").GetBoolean() == expected.Saddlebag &&
            saved.RootElement.GetProperty("housing").GetBoolean() == expected.Housing &&
            saved.RootElement.GetProperty("fcChest").GetBoolean() == expected.FcChest &&
            saved.RootElement.GetProperty("armoury").GetBoolean() == expected.Armoury,
            "all storage grants must persist independently");
        Check(saved.RootElement.GetProperty("targetInfo").GetBoolean() == expected.TargetInfo, "target grant must persist independently");
        Check(saved.RootElement.GetProperty("gcSupply").GetBoolean() == expected.GcSupply, "GC grant must persist independently");
        Check(saved.RootElement.GetProperty("duties").GetBoolean() == expected.Duties,
            "duty permission must be persisted explicitly");
        Check(saved.RootElement.GetProperty("currencies").GetBoolean() == expected.Currencies,
            "currency permission must be persisted explicitly");
        Check(saved.RootElement.GetProperty("spoilersAllowed").GetBoolean() == expected.SpoilersAllowed,
            "spoiler preference must be persisted explicitly");
        Check(saved.RootElement.GetProperty("gameChatReplies").GetBoolean() == expected.GameChatReplies,
            "local game-chat preference must be persisted explicitly");
        Check(saved.RootElement.GetProperty("characterContext").GetBoolean() == expected.CharacterContext &&
            saved.RootElement.GetProperty("npcDialogue").GetBoolean() == expected.NpcDialogue &&
            saved.RootElement.GetProperty("location").GetBoolean() == expected.Location,
            "character, location and NPC permissions must be persisted explicitly");
        var restored = new CompanionAccessSettingsStore(folder.File);
        Check(restored.Settings == expected && restored.Error is null, "all boolean choices must round trip");
        Check(Directory.GetFiles(folder.Path, "*.tmp").Length == 0, "no leftover temporary file");
    }
}

static void MigrateVersionOne()
{
    using var folder = new TestFolder();
    const string legacy = "{\"schemaVersion\":1,\"inventory\":true,\"quests\":false,\"webSearch\":true}";
    File.WriteAllText(folder.File, legacy);
    var store = new CompanionAccessSettingsStore(folder.File);
    Check(store.Settings == new CompanionAccessSettings(true, false, true, false, false) && store.Error is null,
        "legacy access choices must load with history lookup and game-chat replies disabled");
    Check(File.ReadAllText(folder.File) == legacy, "loading migration must not rewrite the existing file");
    Check(store.Save(store.Settings with { ChatHistory = true }), "explicit history permission must save");
    Check(new CompanionAccessSettingsStore(folder.File).Settings.ChatHistory, "new permission survives reload");
    Check(store.Save(store.Settings with { ChatHistory = false }), "revocation must save");
    Check(!new CompanionAccessSettingsStore(folder.File).Settings.ChatHistory, "revocation survives reload");
}

static void MigrateVersionTwo()
{
    using var folder = new TestFolder();
    for (var mask = 0; mask < 16; mask++)
    {
        var expected = new CompanionAccessSettings((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0, (mask & 8) != 0, false);
        var legacy = System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            inventory = expected.Inventory,
            quests = expected.Quests,
            webSearch = expected.WebSearch,
            chatHistory = expected.ChatHistory,
        });
        File.WriteAllText(folder.File, legacy);
        var store = new CompanionAccessSettingsStore(folder.File);
        Check(store.Settings == expected && store.Error is null, "version 2 must preserve access and leave game-chat replies off");
        Check(File.ReadAllText(folder.File) == legacy, "loading migration must not rewrite the existing file");
        Check(store.Save(store.Settings), "explicit save must upgrade legacy settings");
        var restored = new CompanionAccessSettingsStore(folder.File);
        Check(restored.Settings == expected && restored.Error is null, "legacy settings must survive upgrade with game-chat replies off");
    }
}

static void MigrateVersionThree()
{
    using var folder = new TestFolder();
    for (var mask = 0; mask < 32; mask++)
    {
        var expected = new CompanionAccessSettings((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0,
            (mask & 8) != 0, (mask & 16) != 0);
        var legacy = System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = 3,
            inventory = expected.Inventory,
            quests = expected.Quests,
            webSearch = expected.WebSearch,
            chatHistory = expected.ChatHistory,
            gameChatReplies = expected.GameChatReplies,
        });
        File.WriteAllText(folder.File, legacy);
        var store = new CompanionAccessSettingsStore(folder.File);
        Check(store.Settings == expected && store.Error is null,
            "version 3 must preserve every previous choice with character and NPC permissions off");
        Check(File.ReadAllText(folder.File) == legacy, "loading migration must not rewrite the existing file");
        Check(store.Save(store.Settings), "explicit save must upgrade version 3 settings");
        var restored = new CompanionAccessSettingsStore(folder.File);
        Check(restored.Settings == expected && restored.Error is null,
            "version 3 permissions must survive schema upgrade without enabling new access");
    }
}

static void GameContextPermissions()
{
    using var folder = new TestFolder();
    var store = new CompanionAccessSettingsStore(folder.File);
    var previous = new CompanionAccessSettings(true, false, true, false, true);
    Check(store.Save(previous), "seed existing permissions");
    for (var mask = 0; mask < 8; mask++)
    {
        var expected = previous with
        { CharacterContext = (mask & 1) != 0, NpcDialogue = (mask & 2) != 0, Location = (mask & 4) != 0 };
        Check(store.Save(expected), "new permissions must save independently");
        var restored = new CompanionAccessSettingsStore(folder.File);
        Check(restored.Settings == expected && restored.Error is null,
            "granting or revoking new permissions must survive reload and preserve existing choices");
    }
}

static void MigrateVersionFour()
{
    using var folder = new TestFolder();
    for (var mask = 0; mask < 128; mask++)
    {
        var expected = new CompanionAccessSettings((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0,
            (mask & 8) != 0, (mask & 16) != 0, (mask & 32) != 0, (mask & 64) != 0, (mask & 32) != 0);
        var legacy = System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = 4, inventory = expected.Inventory, quests = expected.Quests,
            webSearch = expected.WebSearch, chatHistory = expected.ChatHistory,
            gameChatReplies = expected.GameChatReplies, characterContext = expected.CharacterContext,
            npcDialogue = expected.NpcDialogue,
        });
        File.WriteAllText(folder.File, legacy);
        var store = new CompanionAccessSettingsStore(folder.File);
        Check(store.Settings == expected && store.Error is null,
            "version 4 must preserve the combined character/location grant and every other choice");
        Check(File.ReadAllText(folder.File) == legacy, "migration must not rewrite without a save");
        Check(store.Save(store.Settings) && new CompanionAccessSettingsStore(folder.File).Settings == expected,
            "split grant survives the schema upgrade");
    }
}

static void MigrateVersionFive()
{
    using var folder = new TestFolder();
    for (var mask = 0; mask < 256; mask++)
    {
        var expected = new CompanionAccessSettings((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0,
            (mask & 8) != 0, (mask & 16) != 0, (mask & 32) != 0, (mask & 64) != 0, (mask & 128) != 0);
        var legacy = System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = 5, inventory = expected.Inventory, quests = expected.Quests,
            webSearch = expected.WebSearch, chatHistory = expected.ChatHistory,
            gameChatReplies = expected.GameChatReplies, characterContext = expected.CharacterContext,
            npcDialogue = expected.NpcDialogue, location = expected.Location,
        });
        File.WriteAllText(folder.File, legacy);
        var store = new CompanionAccessSettingsStore(folder.File);
        Check(store.Settings == expected && !store.Settings.SpoilersAllowed && store.Error is null,
            "version 5 must preserve all previous choices and keep spoilers off");
        Check(File.ReadAllText(folder.File) == legacy, "loading legacy settings must not rewrite them");
        Check(store.Save(store.Settings) && new CompanionAccessSettingsStore(folder.File).Settings == expected,
            "legacy choices must survive a schema 7 save");
    }
}

static void MigrateVersionSix()
{
    using var folder = new TestFolder();
    foreach (var mask in new[] { 0, 1, 255, 511 })
    {
        var expected = new CompanionAccessSettings((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0,
            (mask & 8) != 0, (mask & 16) != 0, (mask & 32) != 0, (mask & 64) != 0, (mask & 128) != 0,
            (mask & 256) != 0);
        var legacy = System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = 6, inventory = expected.Inventory, quests = expected.Quests,
            webSearch = expected.WebSearch, chatHistory = expected.ChatHistory,
            gameChatReplies = expected.GameChatReplies, characterContext = expected.CharacterContext,
            npcDialogue = expected.NpcDialogue, location = expected.Location, spoilersAllowed = expected.SpoilersAllowed,
        });
        File.WriteAllText(folder.File, legacy);
        var store = new CompanionAccessSettingsStore(folder.File);
        Check(store.Settings == expected && !store.Settings.Currencies && store.Error is null,
            "version 6 must preserve choices without inferring currency permission from inventory");
        Check(File.ReadAllText(folder.File) == legacy, "loading legacy settings must not rewrite them");
        Check(store.Save(store.Settings) && new CompanionAccessSettingsStore(folder.File).Settings == expected,
            "legacy choices must survive a schema 7 save");
    }
}

static void MigrateVersionSeven()
{
    using var folder = new TestFolder();
    foreach (var enabled in new[] { false, true })
    {
        var expected = new CompanionAccessSettings(enabled, enabled, enabled, enabled, enabled,
            enabled, enabled, enabled, enabled, enabled);
        var store = new CompanionAccessSettingsStore(folder.File);
        Check(store.Save(expected), "prepare prior permission choices");
        var legacy = System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = 7, inventory = expected.Inventory, quests = expected.Quests,
            webSearch = expected.WebSearch, chatHistory = expected.ChatHistory, gameChatReplies = expected.GameChatReplies,
            characterContext = expected.CharacterContext, npcDialogue = expected.NpcDialogue,
            location = expected.Location, spoilersAllowed = expected.SpoilersAllowed, currencies = expected.Currencies,
        });
        File.WriteAllText(folder.File, legacy);
        var migrated = new CompanionAccessSettingsStore(folder.File);
        Check(migrated.Settings == expected && !migrated.Settings.Duties && migrated.Error is null,
            "schema 7 must preserve every permission without inferring duty access from quests");
        Check(File.ReadAllText(folder.File) == legacy, "loading must not rewrite old settings");
        Check(migrated.Save(migrated.Settings) && new CompanionAccessSettingsStore(folder.File).Settings == expected,
            "current schema save must preserve the migrated choices");
    }
}

static void InvalidDutyPermissions()
{
    using var folder = new TestFolder();
    const string prefix = "{\"schemaVersion\":8,\"inventory\":true,\"quests\":true,\"webSearch\":true,\"chatHistory\":true,\"gameChatReplies\":true,\"characterContext\":true,\"npcDialogue\":true,\"location\":true,\"spoilersAllowed\":true,\"currencies\":true";
    var invalid = new List<string> { prefix + "}", prefix + ",\"Duties\":true}",
        prefix + ",\"duties\":true,\"duties\":false}", prefix + ",\"duties\":true,\"extra\":false}",
        prefix.Replace("\"schemaVersion\":8", "\"schemaVersion\":9") + ",\"duties\":false}",
        prefix.Replace(",\"currencies\":true", "") + ",\"duties\":false}" };
    foreach (var nonBoolean in new[] { "null", "\"true\"", "1", "{}", "[]" })
        invalid.Add(prefix + $",\"duties\":{nonBoolean}}}");
    for (var schema = 1; schema <= 7; schema++)
    {
        var legacy = $"{{\"schemaVersion\":{schema},\"inventory\":true,\"quests\":true,\"webSearch\":true";
        if (schema >= 2) legacy += ",\"chatHistory\":true";
        if (schema >= 3) legacy += ",\"gameChatReplies\":true";
        if (schema >= 4) legacy += ",\"characterContext\":true,\"npcDialogue\":true";
        if (schema >= 5) legacy += ",\"location\":true";
        if (schema >= 6) legacy += ",\"spoilersAllowed\":true";
        if (schema >= 7) legacy += ",\"currencies\":true";
        invalid.Add(legacy + ",\"duties\":true}");
    }
    foreach (var contents in invalid)
    {
        File.WriteAllText(folder.File, contents);
        var store = new CompanionAccessSettingsStore(folder.File);
        Check(store.Settings == new CompanionAccessSettings() && store.Error is not null,
            "invalid duty permission must fail closed");
        Check(File.ReadAllText(folder.File) == contents, "rejected duty settings must remain untouched");
    }
}

static void DutyTransitions()
{
    using var folder = new TestFolder();
    var store = new CompanionAccessSettingsStore(folder.File);
    var initial = new CompanionAccessSettings(Currencies: true);
    Check(store.Save(initial), "seed independent currency grant");
    var plugin = new Plugin(store);
    plugin.SetAccess(false, false, false, duties: true);
    Check(plugin.Access == initial with { Duties = true } && plugin.CancelCount == 0 && plugin.UnrelatedReaderActions == 0,
        "duty grant must be independent from quests and must not capture other readers");
    plugin.SetAccess(false, false, false);
    Check(plugin.Access.Duties && new CompanionAccessSettingsStore(folder.File).Settings.Duties, "duty grant must persist");
    plugin.SetPendingQuestion(null);
    plugin.SetBackgroundQuestion(true);
    plugin.SetAccess(false, false, false, duties: false);
    Check(plugin.Access == initial && plugin.CancelCount == 1 && plugin.UnrelatedReaderActions == 0,
        "duty revocation must cancel a background answer even when the selected chat is idle");
    Check(!new CompanionAccessSettingsStore(folder.File).Settings.Duties, "duty revocation must persist");
    plugin.SetAccess(false, false, false, duties: true);
    plugin.SetAccess(false, false, false, duties: false);
    Check(plugin.CancelCount == 1, "idle revocation must not invent a canceled question");
}

static void FailedDutySave()
{
    using var folder = new TestFolder();
    var store = new CompanionAccessSettingsStore(folder.File);
    var initial = new CompanionAccessSettings(Duties: true);
    Check(store.Save(initial), "seed duty grant");
    var plugin = new Plugin(store);
    File.SetAttributes(folder.File, FileAttributes.ReadOnly);
    try
    {
        plugin.SetAccess(false, false, false, duties: false);
        Check(plugin.Access == new CompanionAccessSettings() && plugin.AccessSaveError is not null &&
            plugin.CancelCount == 1 && plugin.UnrelatedReaderActions == 0,
            "failed duty save must disable effective access and cancel the active question");
        Check(new CompanionAccessSettingsStore(folder.File).Settings == initial, "failed replacement must preserve the prior durable file");
    }
    finally { File.SetAttributes(folder.File, FileAttributes.Normal); }
}

static void InvalidCurrencyPermissions()
{
    using var folder = new TestFolder();
    const string prefix = "{\"schemaVersion\":7,\"inventory\":true,\"quests\":true,\"webSearch\":true,\"chatHistory\":true,\"gameChatReplies\":true,\"characterContext\":true,\"npcDialogue\":true,\"location\":true,\"spoilersAllowed\":true";
    var invalid = new List<string>
    {
        prefix + "}",
        prefix + ",\"Currencies\":true}",
        prefix + ",\"currencies\":true,\"currencies\":false}",
        prefix + ",\"currencies\":true,\"extra\":false}",
        prefix.Replace("\"schemaVersion\":7", "\"schemaVersion\":8") + ",\"currencies\":false}",
        prefix.Replace(",\"spoilersAllowed\":true", "") + ",\"currencies\":false}",
    };
    foreach (var nonBoolean in new[] { "null", "\"true\"", "1", "{}", "[]" })
        invalid.Add(prefix + $",\"currencies\":{nonBoolean}}}");
    for (var schema = 1; schema <= 6; schema++)
    {
        var legacy = $"{{\"schemaVersion\":{schema},\"inventory\":true,\"quests\":true,\"webSearch\":true";
        if (schema >= 2) legacy += ",\"chatHistory\":true";
        if (schema >= 3) legacy += ",\"gameChatReplies\":true";
        if (schema >= 4) legacy += ",\"characterContext\":true,\"npcDialogue\":true";
        if (schema >= 5) legacy += ",\"location\":true";
        if (schema >= 6) legacy += ",\"spoilersAllowed\":true";
        invalid.Add(legacy + ",\"currencies\":true}");
    }
    foreach (var contents in invalid)
    {
        File.WriteAllText(folder.File, contents);
        var store = new CompanionAccessSettingsStore(folder.File);
        Check(store.Settings == new CompanionAccessSettings() && store.Error is not null,
            "invalid currency permission must fail closed");
        Check(File.ReadAllText(folder.File) == contents, "rejected settings must remain untouched");
    }
}

static void CurrencyTransitions()
{
    foreach (var readersEnabled in new[] { false, true })
    {
        using var folder = new TestFolder();
        var initial = new CompanionAccessSettings(readersEnabled, readersEnabled, true, true, true,
            readersEnabled, readersEnabled, readersEnabled, true);
        var store = new CompanionAccessSettingsStore(folder.File);
        Check(store.Save(initial), "seed permissions for currency transitions");
        var plugin = new Plugin(store);
        plugin.SetAccess(initial.Inventory, initial.Quests, initial.WebSearch, currencies: true);
        Check(plugin.Access == initial with { Currencies = true } && plugin.CancelCount == 0 &&
            plugin.UnrelatedReaderActions == 0, "enabling currencies must neither capture unrelated readers nor cancel");
        Check(new CompanionAccessSettingsStore(folder.File).Settings.Currencies, "explicit currency grant must be durable");
        plugin.SetAccess(initial.Inventory, initial.Quests, initial.WebSearch);
        Check(plugin.Access.Currencies, "existing callers must preserve currency permission");
        plugin.SetAccess(initial.Inventory, initial.Quests, initial.WebSearch, currencies: false);
        Check(plugin.Access == initial && plugin.CancelCount == 1 && plugin.UnrelatedReaderActions == 0,
            "currency revocation must cancel an active answer without clearing unrelated readers");
        Check(!new CompanionAccessSettingsStore(folder.File).Settings.Currencies, "currency revocation must be durable");
        plugin.SetAccess(initial.Inventory, initial.Quests, initial.WebSearch, currencies: true);
        plugin.SetPendingQuestion(null);
        plugin.SetAccess(initial.Inventory, initial.Quests, initial.WebSearch, currencies: false);
        Check(plugin.CancelCount == 1, "revocation with no active question must not cancel");
    }
}

static void FailedCurrencySave()
{
    using var folder = new TestFolder();
    var store = new CompanionAccessSettingsStore(folder.File);
    var currencyOnly = new CompanionAccessSettings(Currencies: true);
    Check(store.Save(currencyOnly), "seed currency-only access");
    var plugin = new Plugin(store);
    File.SetAttributes(folder.File, FileAttributes.ReadOnly);
    try
    {
        plugin.SetAccess(false, false, false, currencies: false);
        Check(plugin.Access == new CompanionAccessSettings() && plugin.AccessSaveError is not null &&
            plugin.CancelCount == 1 && plugin.UnrelatedReaderActions == 0,
            "failed currency revocation must cancel while keeping unrelated readers untouched");
        Check(new CompanionAccessSettingsStore(folder.File).Settings == currencyOnly,
            "failed replacement must preserve previous durable choices");
    }
    finally { File.SetAttributes(folder.File, FileAttributes.Normal); }
}

static void InvalidFiles()
{
    using var folder = new TestFolder();
    var invalid = new[]
    {
        "", "null", "[]", "{", "{}",
        "{\"schemaVersion\":2,\"inventory\":true,\"quests\":true,\"webSearch\":true}",
        "{\"schemaVersion\":2,\"inventory\":true,\"quests\":true,\"webSearch\":true,\"chatHistory\":null}",
        "{\"schemaVersion\":3,\"inventory\":true,\"quests\":true,\"webSearch\":true,\"chatHistory\":true}",
        "{\"schemaVersion\":3,\"inventory\":true,\"quests\":true,\"webSearch\":true,\"gameChatReplies\":true}",
        "{\"schemaVersion\":3,\"inventory\":true,\"quests\":true,\"webSearch\":true,\"chatHistory\":true,\"gameChatReplies\":null}",
        "{\"schemaVersion\":3,\"inventory\":true,\"quests\":true,\"webSearch\":true,\"chatHistory\":true,\"gameChatReplies\":\"true\"}",
        "{\"schemaVersion\":3,\"inventory\":true,\"quests\":true,\"webSearch\":true,\"chatHistory\":true,\"gameChatReplies\":1}",
        "{\"schemaVersion\":3,\"inventory\":true,\"quests\":true,\"webSearch\":true,\"chatHistory\":true,\"gameChatReplies\":{}}",
        "{\"schemaVersion\":3,\"inventory\":true,\"quests\":true,\"webSearch\":true,\"chatHistory\":true,\"gameChatReplies\":true,\"gameChatReplies\":false}",
        "{\"schemaVersion\":3,\"inventory\":true,\"quests\":true,\"webSearch\":true,\"chatHistory\":true,\"GameChatReplies\":true}",
        "{\"schemaVersion\":3,\"inventory\":true,\"quests\":true,\"webSearch\":true,\"chatHistory\":true,\"gameChatReplies\":true,\"extra\":false}",
        "{\"schemaVersion\":4,\"inventory\":true,\"quests\":true,\"webSearch\":true,\"chatHistory\":true,\"gameChatReplies\":true}",
        "{\"schemaVersion\":2,\"inventory\":true,\"quests\":true,\"webSearch\":true,\"chatHistory\":true,\"gameChatReplies\":false}",
        "{\"schemaVersion\":1,\"inventory\":true,\"quests\":true,\"webSearch\":true,\"gameChatReplies\":false}",
        "{\"schemaVersion\":1,\"inventory\":true,\"quests\":true,\"webSearch\":true,\"chatHistory\":true}",
        "{\"schemaVersion\":1,\"inventory\":\"true\",\"quests\":true,\"webSearch\":true}",
        "{\"schemaVersion\":1,\"inventory\":true,\"quests\":null,\"webSearch\":true}",
        "{\"schemaVersion\":1,\"inventory\":true,\"quests\":true}",
        "{\"schemaVersion\":1,\"inventory\":true,\"quests\":true,\"webSearch\":true,\"extra\":false}",
        "{\"schemaVersion\":1,\"inventory\":true,\"inventory\":false,\"quests\":true,\"webSearch\":true}",
        "{\"SchemaVersion\":1,\"inventory\":true,\"quests\":true,\"webSearch\":true}",
        new string(' ', 4097),
    };
    foreach (var contents in invalid)
    {
        File.WriteAllText(folder.File, contents);
        var store = new CompanionAccessSettingsStore(folder.File);
        Check(store.Settings == new CompanionAccessSettings() && store.Error is not null, "invalid data must disable every category");
        Check(File.ReadAllText(folder.File) == contents, "invalid source must be preserved until explicit save");
    }
}

static void InvalidGameContextPermissions()
{
    using var folder = new TestFolder();
    const string prefix = "{\"schemaVersion\":4,\"inventory\":true,\"quests\":true,\"webSearch\":true,\"chatHistory\":true,\"gameChatReplies\":true";
    var invalid = new List<string>
    {
        prefix + "}",
        prefix + ",\"characterContext\":true}",
        prefix + ",\"npcDialogue\":true}",
        prefix + ",\"CharacterContext\":true,\"npcDialogue\":true}",
        prefix + ",\"characterContext\":true,\"NpcDialogue\":true}",
        prefix + ",\"characterContext\":true,\"npcDialogue\":true,\"extra\":false}",
        prefix.Replace("\"schemaVersion\":4", "\"schemaVersion\":5") + ",\"characterContext\":true,\"npcDialogue\":true}",
    };
    foreach (var field in new[] { "characterContext", "npcDialogue" })
    {
        var otherField = field == "characterContext" ? "npcDialogue" : "characterContext";
        foreach (var nonBoolean in new[] { "null", "\"true\"", "1", "{}", "[]" })
            invalid.Add(prefix + $",\"{field}\":{nonBoolean},\"{otherField}\":true}}");
        invalid.Add(prefix + $",\"{field}\":true,\"{field}\":false,\"{otherField}\":true}}");
        for (var schema = 1; schema <= 3; schema++)
        {
            var legacy = $"{{\"schemaVersion\":{schema},\"inventory\":true,\"quests\":true,\"webSearch\":true";
            if (schema >= 2) legacy += ",\"chatHistory\":true";
            if (schema >= 3) legacy += ",\"gameChatReplies\":true";
            invalid.Add(legacy + $",\"{field}\":false}}");
        }
    }
    foreach (var contents in invalid)
    {
        File.WriteAllText(folder.File, contents);
        var store = new CompanionAccessSettingsStore(folder.File);
        Check(store.Settings == new CompanionAccessSettings() && store.Error is not null,
            "invalid new permission fields must disable every category");
        Check(File.ReadAllText(folder.File) == contents, "rejected source must remain untouched");
    }
}

static void InvalidLocationPermissions()
{
    using var folder = new TestFolder();
    const string prefix = "{\"schemaVersion\":5,\"inventory\":true,\"quests\":true,\"webSearch\":true,\"chatHistory\":true,\"gameChatReplies\":true,\"characterContext\":true,\"npcDialogue\":true";
    var invalid = new List<string>
    {
        prefix + "}",
        prefix + ",\"Location\":true}",
        prefix + ",\"location\":true,\"location\":false}",
        prefix + ",\"location\":true,\"extra\":false}",
        prefix.Replace("\"schemaVersion\":5", "\"schemaVersion\":6") + ",\"location\":true}",
        prefix.Replace(",\"characterContext\":true", "") + ",\"location\":true}",
        prefix.Replace(",\"npcDialogue\":true", "") + ",\"location\":true}",
    };
    foreach (var nonBoolean in new[] { "null", "\"true\"", "1", "{}", "[]" })
        invalid.Add(prefix + $",\"location\":{nonBoolean}}}");
    for (var schema = 1; schema <= 4; schema++)
    {
        var legacy = $"{{\"schemaVersion\":{schema},\"inventory\":true,\"quests\":true,\"webSearch\":true";
        if (schema >= 2) legacy += ",\"chatHistory\":true";
        if (schema >= 3) legacy += ",\"gameChatReplies\":true";
        if (schema >= 4) legacy += ",\"characterContext\":true,\"npcDialogue\":true";
        invalid.Add(legacy + ",\"location\":false}");
    }
    foreach (var contents in invalid)
    {
        File.WriteAllText(folder.File, contents);
        var store = new CompanionAccessSettingsStore(folder.File);
        Check(store.Settings == new CompanionAccessSettings() && store.Error is not null,
            "invalid location permission must fail closed");
        Check(File.ReadAllText(folder.File) == contents, "rejected settings must remain untouched");
    }
}

static void InvalidSpoilerPreferences()
{
    using var folder = new TestFolder();
    const string prefix = "{\"schemaVersion\":6,\"inventory\":true,\"quests\":true,\"webSearch\":true,\"chatHistory\":true,\"gameChatReplies\":true,\"characterContext\":true,\"npcDialogue\":true,\"location\":true";
    var invalid = new List<string>
    {
        prefix + "}",
        prefix + ",\"SpoilersAllowed\":true}",
        prefix + ",\"spoilersAllowed\":true,\"spoilersAllowed\":false}",
        prefix + ",\"spoilersAllowed\":true,\"extra\":false}",
        prefix.Replace("\"schemaVersion\":6", "\"schemaVersion\":7") + ",\"spoilersAllowed\":false}",
        prefix.Replace(",\"location\":true", "") + ",\"spoilersAllowed\":false}",
    };
    foreach (var nonBoolean in new[] { "null", "\"true\"", "1", "{}", "[]" })
        invalid.Add(prefix + $",\"spoilersAllowed\":{nonBoolean}}}");
    for (var schema = 1; schema <= 5; schema++)
    {
        var legacy = $"{{\"schemaVersion\":{schema},\"inventory\":true,\"quests\":true,\"webSearch\":true";
        if (schema >= 2) legacy += ",\"chatHistory\":true";
        if (schema >= 3) legacy += ",\"gameChatReplies\":true";
        if (schema >= 4) legacy += ",\"characterContext\":true,\"npcDialogue\":true";
        if (schema >= 5) legacy += ",\"location\":true";
        invalid.Add(legacy + ",\"spoilersAllowed\":false}");
    }
    foreach (var contents in invalid)
    {
        File.WriteAllText(folder.File, contents);
        var store = new CompanionAccessSettingsStore(folder.File);
        Check(store.Settings == new CompanionAccessSettings() && store.Error is not null,
            "invalid spoiler fields must fail closed");
        Check(File.ReadAllText(folder.File) == contents, "rejected settings must remain untouched");
    }
}

static void SpoilerTransitions()
{
    foreach (var readersEnabled in new[] { false, true })
    {
        using var folder = new TestFolder();
        var initial = new CompanionAccessSettings(readersEnabled, readersEnabled, true, true, true,
            readersEnabled, readersEnabled, readersEnabled);
        var store = new CompanionAccessSettingsStore(folder.File);
        Check(store.Save(initial), "seed permissions for transition test");
        var plugin = new Plugin(store);
        plugin.SetAccess(initial.Inventory, initial.Quests, initial.WebSearch, spoilersAllowed: true);
        Check(plugin.Access == initial with { SpoilersAllowed = true } && plugin.CancelCount == 0,
            "enabling spoilers must preserve permissions and the answer already in progress");
        Check(plugin.UnrelatedReaderActions == 0, "spoiler enable changed unrelated readers");
        plugin.SetAccess(initial.Inventory, initial.Quests, false);
        Check(plugin.Access.SpoilersAllowed, "existing callers omitting spoiler preference must preserve it");
        var cancellationsBefore = plugin.CancelCount;
        plugin.SetPendingQuestion("still answering");
        plugin.SetAccess(initial.Inventory, initial.Quests, false, spoilersAllowed: false);
        Check(!plugin.Access.SpoilersAllowed && plugin.CancelCount == cancellationsBefore + 1 &&
            plugin.UnrelatedReaderActions == 0, "restricting spoilers must cancel the answer without clearing readers");
        Check(!new CompanionAccessSettingsStore(folder.File).Settings.SpoilersAllowed,
            "spoiler revocation must survive reload");
        plugin.SetAccess(initial.Inventory, initial.Quests, false, spoilersAllowed: true);
        plugin.SetPendingQuestion(null);
        cancellationsBefore = plugin.CancelCount;
        plugin.SetAccess(initial.Inventory, initial.Quests, false, spoilersAllowed: false);
        Check(plugin.CancelCount == cancellationsBefore, "no active question must not trigger cancellation");
    }
}

static void FailedSpoilerSave()
{
    using var folder = new TestFolder();
    var store = new CompanionAccessSettingsStore(folder.File);
    var allEnabled = new CompanionAccessSettings(true, true, true, true, true, true, true, true, true);
    Check(store.Save(allEnabled), "seed enabled spoiler policy");
    var plugin = new Plugin(store);
    File.SetAttributes(folder.File, FileAttributes.ReadOnly);
    try
    {
        plugin.SetAccess(true, true, true, spoilersAllowed: false);
        Check(plugin.Access == new CompanionAccessSettings() && plugin.AccessSaveError is not null &&
            plugin.CancelCount == 1, "failed restrictive save must fail closed and cancel the active answer");
        Check(new CompanionAccessSettingsStore(folder.File).Settings == allEnabled,
            "failed replacement must preserve previous durable file");
    }
    finally { File.SetAttributes(folder.File, FileAttributes.Normal); }
}

static void FailedSave()
{
    using var folder = new TestFolder();
    var store = new CompanionAccessSettingsStore(folder.File);
    var enabled = new CompanionAccessSettings(true, true, true, true, true, true, true, true);
    Check(store.Save(enabled), "seed valid file");
    var previous = File.ReadAllBytes(folder.File);
    File.SetAttributes(folder.File, FileAttributes.ReadOnly);
    try
    {
        Check(!store.Save(new()), "read-only target must reject replacement");
        Check(store.Settings == new CompanionAccessSettings() && store.Error is not null, "failed revocation must turn off effective session access");
        Check(previous.SequenceEqual(File.ReadAllBytes(folder.File)), "failed write must not corrupt saved file");
        Check(Directory.GetFiles(folder.Path, "*.tmp").Length == 0, "failed write cleans temporary file");
        Check(!store.Save(enabled) && store.Settings == new CompanionAccessSettings(), "failed enabling must also remain off");
    }
    finally { File.SetAttributes(folder.File, FileAttributes.Normal); }
    Check(store.Save(new()) && store.Error is null, "retry must persist disabled state and clear error");
    Check(new CompanionAccessSettingsStore(folder.File).Settings == new CompanionAccessSettings(), "successful revocation survives reload");
}

static void Repair()
{
    using var folder = new TestFolder();
    File.WriteAllText(folder.File, "bad json");
    var store = new CompanionAccessSettingsStore(folder.File);
    Check(store.Error is not null && store.Save(new(true, false, true)), "explicit save may replace rejected file");
    Check(store.Error is null && new CompanionAccessSettingsStore(folder.File).Settings == new CompanionAccessSettings(true, false, true), "repair must be durable");
}

static void ContextSelection()
{
    for (var mask = 0; mask < 8; mask++)
    {
        var settings = new CompanionAccessSettings((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0);
        for (var availability = 0; availability < 8; availability++)
        {
            var ready = (availability & 1) != 0;
            var inventory = (availability & 2) != 0;
            var quests = (availability & 4) != 0;
            var selected = ChatContextSelection.Create(settings, ready, inventory, quests);
            Check(selected.Inventory == (settings.Inventory && ready && inventory), "inventory requires permission, character and snapshot");
            Check(selected.Quests == (settings.Quests && ready && quests), "quests require permission, character and snapshot");
            Check(selected.WebSearch == settings.WebSearch, "web permission is independent of local game context");
        }
    }
}

sealed class TestFolder : IDisposable
{
    private readonly string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", ".test-tmp", "access-settings"));
    public string Path { get; }
    public string File => System.IO.Path.Combine(this.Path, "companion-access.json");
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
