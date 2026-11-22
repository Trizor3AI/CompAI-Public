using CompAI;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args is ["--history-contract-fixture"])
{
    Console.WriteLine(JsonSerializer.Serialize(HistoryContractFixture()));
    return 0;
}
if (args is ["--history-reliability-fixture"])
{
    Console.WriteLine(JsonSerializer.Serialize(HistoryReliabilityTests.ContractFixture()));
    return 0;
}

var tests = new (string Name, Action Run)[]
{
    ("chat management: titles and pins preserve transcripts and manual order", ChatManagementTests.TitlesAndPins),
    ("chat management: pin retention and schema migration", ChatManagementTests.PinRetentionAndMigration),
    ("chat management: missing sidebar and unreadable transcripts retain pins across reloads", ChatManagementTests.RecoverPinMetadata),
    ("chat management: metadata failures preserve titles, pins and possible pinned history", ChatManagementTests.MetadataFailure),
    ("chat management: local search caches and refreshes completed messages", ChatManagementTests.Search),
    ("chat management: search reports its bounded message scan", ChatManagementTests.SearchBudget),
    ("chat management: explicit bulk deletion retains partial failures and unrelated data", ChatManagementTests.BulkDelete),
    ("chat management: background completion and cancellation preserve owner chat", ChatManagementTests.OwnerPersistence),
    ("storage reliability: debounce and final draft flush", StorageReliabilityTests.DraftDebounce),
    ("storage reliability: size and turn limits preserve overflow", StorageReliabilityTests.CapacityAndOverflow),
    ("storage reliability: failed saves preserve exports and navigation", StorageReliabilityTests.FailedSaveNavigation),
    ("storage reliability: failed recovery warns about memory-only work", StorageReliabilityTests.RecoveryFailure),
    ("storage reliability: export confirmation is transient and chat-scoped", StorageReliabilityTests.ExportNoticeScope),
    ("sent message links stay scoped to their chat", SentMessageLinks),
    ("long generated source titles remain readable across reloads", ChatManagementTests.LongSourceTitles),
    ("persist, switch and reload drafts and completed turns", PersistAndSwitch),
    ("30-day expiry uses stored activity, not load or selection", Expiry),
    ("ten recent chats and older local archives", Archives),
    ("corrupt UTF-8 and unrelated filenames are preserved", CorruptAndOutside),
    ("delete is limited to a selected GUID chat file", DeleteBoundary),
    ("unavailable storage falls back to warned in-memory chats", UnavailableStorage),
    ("active-index write failures preserve existing chats", IndexWriteFailure),
    ("chat summary snapshots refresh only when displayed data changes", SummarySnapshots),
    ("sidebar ordering preserves creation order and manual positions across toggles and reloads", SidebarOrderTests.CreationAndManualOrder),
    ("sidebar ordering migrates legacy activity deterministically without rewriting chats", SidebarOrderTests.LegacyMigration),
    ("sidebar ordering changes preserve immutable snapshots and transcript contents", SidebarOrderTests.SnapshotsAndContent),
    ("sidebar ordering retains expiry and deletion boundaries", SidebarOrderTests.ExpiryAndDeletion),
    ("sidebar ordering preserves corrupt and unsupported metadata", SidebarOrderTests.InvalidMetadata),
    ("sidebar ordering failures retain the previous mode and order", SidebarOrderTests.WriteFailure),
    ("history snapshots survive append, restore, clear and preservation at capacity", HistorySnapshots),
    ("each chat snapshots defaults and restores its own model", PerChatModels),
    ("chat providers roundtrip independently while legacy model pairs remain Codex", PerChatProviders),
    ("legacy migration retains history and original expiry", LegacyModelMigration),
    ("failed legacy model migration keeps readable history and original file", LegacyMigrationFailure),
    ("invalid saved models keep readable history without a fallback", InvalidSavedModels),
    ("model save failures retain the prior selection and disk contents", ModelSaveFailure),
    ("missing defaults and unavailable storage require explicit valid model saves", MissingDefaultAndStorage),
    ("recent context uses bounded complete pairs and caches selection", FlexibleContext),
    ("Phase 2 history excerpts and complete verbatim lookup pages", HistoryReliabilityTests.Run),
    ("chat notes persist independently with atomic failure handling", ChatNotes),
    ("schema-2 notes migration retains models, drafts and expiry", NotesMigration),
    ("older-chat lookup matches, paginates and bounds excerpts", HistoryLookup),
    ("maximum-length history lookup term survives escaped result budgeting", () => { HistoryContractFixture(); }),
    ("personality library persists stable IDs and protects built-ins", PersonalityLibrary),
    ("personality details migrate empty and retain frozen chat and clone copies", PersonalityDetailsPersistence),
    ("personality details lookup exposes only requested bounded sections", PersonalityDetailsQueries),
    ("official Armo personality exposes four bounded on-demand sections", OfficialArmoDetails),
    ("personality library corruption and save failures preserve disk and state", PersonalityLibraryFailures),
    ("chat personality snapshots survive library edits, deletion and reload", PersonalitySnapshots),
    ("answer personality names persist without relabelling older turns or AI history", AnswerPersonalityNames),
    ("answer personality names reject malformed metadata without changing history", InvalidAnswerPersonalityNames),
    ("schema-1 to schema-3 chats migrate to None without changing content or expiry", PersonalityMigration),
    ("malformed personalities preserve readable chats until explicitly repaired", InvalidSavedPersonalities),
    ("personality save failures preserve active snapshot and disk", PersonalitySaveFailure),
    ("preset routing restores the newest exact archived chat without changing its content", PresetRoutingArchives),
    ("preset routing requires exact valid snapshots and preserves new-chat defaults", PresetRoutingSnapshots),
    ("preset routing prefers the active chat on timestamp ties and saves its draft", PresetRoutingTies),
    ("preset routing failures retain unsaved work while allowing navigation", PresetRoutingFailures),
};
var failed = 0;
foreach (var (name, run) in tests.Where(test => args.Length == 0 || args.Any(filter => test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))))
{
    try { run(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
return failed == 0 ? 0 : 1;

static void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
}

static void SentMessageLinks()
{
    using var folder = new TempFolder();
    var store = new ChatStore(folder.Path);
    var first = store.ActiveId;
    const string url = "https://universalis.app/market/5121";
    store.ActiveHistory.Add("**582 gil** [Source: Universalis](" + url + ") " +
        "[invalid](file:///private.txt) [credentials](https://user:pass@example.org/)", "answer", [], null, false);
    Check(store.ActiveHistory.ContainsSource(url) && !store.ActiveHistory.ContainsSource("https://example.org/") &&
        !store.ActiveHistory.ContainsSource("file:///private.txt") &&
        !store.ActiveHistory.ContainsSource("https://user:pass@example.org/"),
        "only accepted HTTPS links in the sent message can open");
    Check(store.NewChat() != first && !store.ActiveHistory.ContainsSource(url), "another chat does not inherit user link membership");
    Check(store.Select(first) && store.ActiveHistory.ContainsSource(url), "saved user link survives returning to its chat");
}

static void AnswerPersonalityNames()
{
    using var folder = new TempFolder();
    var store = new ChatStore(folder.Path, defaultPersonality: () => PersonalityPresets.Armo);
    var id = store.ActiveId;
    var history = store.ActiveHistory;
    history.Add("Legacy question", "Legacy answer", [], null, false);
    history.Add("Named question", "Named answer", [], null, false, "Armo");
    Check(store.SaveActive(), "save named and unknown answers: " + store.Error);
    var path = System.IO.Path.Combine(folder.Path, id + ".json");
    var data = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    data["Turns"]![0]!.AsObject().Remove("PersonalityName");
    File.WriteAllText(path, data.ToJsonString());

    var reloaded = new ChatStore(folder.Path, defaultPersonality: () => PersonalityPresets.None);
    Check(reloaded.ActiveId == id && reloaded.ActiveHistory.View[0].PersonalityName is null &&
          reloaded.ActiveHistory.View[1].PersonalityName == "Armo", "old missing name stays unknown while named answer reloads");
    Check(reloaded.SetActivePersonality(new PersonalityPreset(Guid.NewGuid().ToString("N"), "New name", "New style")),
        "change active chat personality");
    Check(reloaded.ActiveHistory.View[0].PersonalityName is null && reloaded.ActiveHistory.View[1].PersonalityName == "Armo",
        "current personality never relabels previous answers");
    reloaded.ActiveHistory.Add("New question", "New answer", [], null, false, "New name");
    Check(reloaded.SetActivePersonality(PersonalityPresets.None), "save new answer and switch to None");
    reloaded.ActiveHistory.Add("Neutral question", "Neutral answer", [], null, false);
    Check(reloaded.SaveActive(), "save neutral answer");
    var final = new ChatStore(folder.Path);
    Check(final.ActiveHistory.View.Select(turn => turn.PersonalityName).SequenceEqual(new string?[] { null, "Armo", "New name", null }),
        "each completed answer retains its own name through further switches and reload");
    var context = JsonSerializer.Serialize(final.ActiveHistory.Context());
    var lookup = JsonSerializer.Serialize(ChatHistoryLookup.Query(final.ActiveHistory.Export(),
        JsonSerializer.SerializeToElement(new { query = "", offset = 0, limit = 5 })));
    Check(!context.Contains("PersonalityName") && !context.Contains("Armo") && !context.Contains("New name") &&
          !lookup.Contains("PersonalityName") && !lookup.Contains("Armo") && !lookup.Contains("New name"),
        "display-only names are excluded from both recent and older AI history");
}

static void InvalidAnswerPersonalityNames()
{
    var history = new ChatHistory();
    history.Add("Existing question", "Existing answer", [], null, false, new string('x', 40));
    var original = history.View;
    foreach (var invalid in new[] { "", " ", new string('x', 41), "Armo\n", "Armo\0", "Armo\u007f" })
    {
        try
        {
            history.Restore([new ChatTurn("Saved question", "Saved answer", [], null, false, invalid)]);
            throw new Exception("invalid saved answer name accepted");
        }
        catch (InvalidDataException) { }
        Check(ReferenceEquals(original, history.View), "invalid restore preserves existing history");
        try
        {
            history.Add("New question", "New answer", [], null, false, invalid);
            throw new Exception("invalid answer name accepted on append");
        }
        catch (ArgumentException) { }
        Check(ReferenceEquals(original, history.View), "invalid append preserves existing history");
    }
}

static void PresetRoutingArchives()
{
    using var folder = new TempFolder();
    var now = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    var store = new ChatStore(folder.Path, () => now);
    Check(store.SetActivePersonality(PersonalityPresets.Armo), "prepare older exact match");
    now = now.AddMinutes(1);
    store.NewChat();
    var expected = store.ActiveId;
    var model = new CompanionModelSettings("gpt-6-luna", "low");
    Check(store.SetActivePersonality(PersonalityPresets.Armo) && store.SetActiveModelSelection(model) &&
          store.SetActiveNotes("Archived notes"), "prepare newer exact match with own settings");
    store.ActiveHistory.Add("Archived question", "Archived answer", [], null, false);
    store.SetDraft("Archived draft");
    Check(store.SaveActive(), "persist archived content");
    var savedPath = System.IO.Path.Combine(folder.Path, expected + ".json");
    var savedBytes = File.ReadAllBytes(savedPath);
    var savedAt = now;
    for (var i = 0; i < 11; i++)
    {
        now = now.AddMinutes(1);
        store.NewChat();
    }
    Check(store.View.Single(x => x.Id == expected).IsArchived, "matching chat is outside the recent list");
    var previous = store.ActiveId;
    store.SetDraft("Unrelated unsent draft");
    now = now.AddMinutes(1);
    var count = store.View.Count;
    Check(store.ContinuePersonalityChat(PersonalityPresets.Armo) && store.ActiveId == expected &&
          store.View.Count == count && store.ActivePersonality == PersonalityPresets.Armo &&
          store.ActiveModelSelection == model && store.ActiveNotes == "Archived notes" &&
          store.ActiveDraft == "Archived draft" && store.ActiveHistory.View.Single().Answer == "Archived answer" &&
          store.View.Single(x => x.Id == expected).UpdatedAtUtc == savedAt &&
          File.ReadAllBytes(savedPath).SequenceEqual(savedBytes), "newest exact archive restores all content without rewriting it");
    var reloaded = new ChatStore(folder.Path, () => now);
    Check(reloaded.ActiveId == expected && reloaded.ActiveDraft == "Archived draft" &&
          reloaded.Select(previous) && reloaded.ActiveDraft == "Unrelated unsent draft",
        "routing and previous draft survive reload");
}

static void PresetRoutingSnapshots()
{
    using var folder = new TempFolder();
    var preset = new PersonalityPreset(Guid.NewGuid().ToString("N"), "Technical", "Be precise.");
    var store = new ChatStore(folder.Path, defaultPersonality: () => PersonalityPresets.None);
    foreach (var different in new[] { preset with { Id = Guid.NewGuid().ToString("N") },
        preset with { Name = "technical" }, preset with { Instructions = "Be precise. " } })
    {
        Check(store.SetActivePersonality(different), "prepare near match");
        store.NewChat();
    }
    var count = store.View.Count;
    Check(store.ContinuePersonalityChat(preset) && store.View.Count == count + 1 &&
          store.ActivePersonality == preset && store.ActiveDraft == "" && store.ActiveNotes == "" &&
          store.ActiveHistory.View.Count == 0, "ID, name and instructions must all match exactly");
    var exact = store.ActiveId;
    Check(store.ContinuePersonalityChat(preset with { }) && store.ActiveId == exact && store.View.Count == count + 1,
        "equivalent record reuses the current chat");
    Check(store.NewChat() != exact && store.ActivePersonality == PersonalityPresets.None,
        "explicit creation leaves ordinary new-chat default unchanged");
    var brokenId = store.ActiveId;
    var brokenPath = System.IO.Path.Combine(folder.Path, brokenId + ".json");
    var broken = JsonNode.Parse(File.ReadAllText(brokenPath))!.AsObject();
    broken["Personality"] = JsonSerializer.SerializeToNode(new PersonalityPreset("invalid", "Broken", "text"));
    File.WriteAllText(brokenPath, broken.ToJsonString());
    var bytes = File.ReadAllBytes(brokenPath);
    var reloaded = new ChatStore(folder.Path);
    Check(reloaded.ActiveId == brokenId && !reloaded.ActivePersonalityIsValid,
        "prepare malformed snapshot with neutral display fallback");
    Check(reloaded.ContinuePersonalityChat(PersonalityPresets.None) && reloaded.ActiveId != brokenId &&
          reloaded.ActivePersonalityIsValid && reloaded.View.Count == count + 2 &&
          File.ReadAllBytes(brokenPath).SequenceEqual(bytes), "invalid neutral fallback must never match None");
}

static void PresetRoutingTies()
{
    using var folder = new TempFolder();
    var now = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    var store = new ChatStore(folder.Path, () => now, defaultPersonality: () => PersonalityPresets.Armo);
    var first = store.ActiveId;
    var second = store.NewChat();
    Check(first != second && store.Select(first), "prepare equal timestamps with first active");
    store.SetDraft("Save without switching");
    Check(store.ContinuePersonalityChat(PersonalityPresets.Armo) && store.ActiveId == first &&
          new ChatStore(folder.Path, () => now).ActiveDraft == "Save without switching",
        "active match wins a tie and saves its draft even without a switch");
    Check(store.Select(second) && store.ContinuePersonalityChat(PersonalityPresets.Armo) && store.ActiveId == second,
        "tie preference follows the active chat instead of creation order or ID");
    now = now.AddMinutes(1);
    store.SetDraft("Newer activity");
    Check(store.SaveActive() && store.Select(first) && store.ContinuePersonalityChat(PersonalityPresets.Armo) &&
          store.ActiveId == second, "a strictly newer exact match wins over the active chat");
}

static void PresetRoutingFailures()
{
    using var folder = new TempFolder();
    var store = new ChatStore(folder.Path);
    var first = store.ActiveId;
    Check(store.SetActiveNotes("Keep notes"), "prepare notes");
    store.ActiveHistory.Add("Keep question", "Keep answer", [], null, false);
    Check(store.SaveActive(), "prepare history");
    var path = System.IO.Path.Combine(folder.Path, first + ".json");
    var bytes = File.ReadAllBytes(path);
    store.SetDraft("Keep unsent draft");
    Check(!store.ContinuePersonalityChat(new PersonalityPreset("invalid", "Bad", "text")) &&
          !store.ContinuePersonalityChat(null!) && store.ActiveId == first &&
          File.ReadAllBytes(path).SequenceEqual(bytes), "invalid presets cannot save or replace the current chat");
    using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        Check(store.ContinuePersonalityChat(PersonalityPresets.Armo) && store.ActiveId != first && store.Select(first) &&
              store.ActiveDraft == "Keep unsent draft" && store.ActiveNotes == "Keep notes" &&
              store.ActiveHistory.View.Single().Question == "Keep question" && store.View.Count == 2 &&
              File.ReadAllBytes(path).SequenceEqual(bytes), "draft-save failure keeps all current content while permitting a different chat");
    Check(store.SaveActive() && store.ContinuePersonalityChat(PersonalityPresets.Armo), "recover and prepare exact target");
    Check(store.Select(first), "restore current chat before index failure");
    var count = store.View.Count;
    var files = Directory.GetFiles(folder.Path, "*.json").Order().ToArray();
    var indexPath = System.IO.Path.Combine(folder.Path, "active.json");
    using (var locked = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        Check(store.ContinuePersonalityChat(PersonalityPresets.Armo) && store.ActiveId != first &&
              store.ContinuePersonalityChat(PersonalityPresets.None) && store.ActiveId == first,
            "index failure does not trap in-memory navigation");
        Check(store.ContinuePersonalityChat(new PersonalityPreset(Guid.NewGuid().ToString("N"), "New", "text")) &&
              store.ActiveId != first && store.View.Count == count + 1 &&
              Directory.GetFiles(folder.Path, "*.json").Length == files.Length + 1 &&
              Directory.GetFiles(folder.Path, "*.tmp").Length == 0,
            "index failure preserves the newly selected chat and cleans temporary files");
    }
    var blockedPath = System.IO.Path.Combine(folder.Path, "unavailable");
    File.WriteAllText(blockedPath, "keep");
    var unavailable = new ChatStore(blockedPath);
    var unavailableId = unavailable.ActiveId;
    unavailable.SetDraft("Keep memory draft");
    Check(unavailable.ContinuePersonalityChat(PersonalityPresets.Armo) && unavailable.ActiveId != unavailableId &&
          unavailable.Select(unavailableId) && unavailable.ActiveDraft == "Keep memory draft" &&
          unavailable.StorageInfo.UnsavedChats > 0 && File.ReadAllText(blockedPath) == "keep",
        "unavailable storage permits navigation with visible unsaved work");
}

static void PersonalityLibrary()
{
    using var folder = new TempFolder();
    var path = System.IO.Path.Combine(folder.Path, "personalities.json");
    var library = new PersonalityPresetStore(path);
    Check(library.Default == PersonalityPresets.Armo && library.Error is null &&
          library.Presets.SequenceEqual(new[] { PersonalityPresets.None, PersonalityPresets.Armo }),
        "new library exposes None and Armo with Armo as default");
    Check(PersonalityPresets.DisplayName(PersonalityPresets.None) == "Default" &&
          PersonalityPresets.DisplayName(PersonalityPresets.Armo) == "Armo" &&
          PersonalityPresets.None.Name == "None" && PersonalityPresets.IsValid(PersonalityPresets.None),
        "Default is a display label without changing the saved neutral snapshot");
    foreach (var schemaVersion in new[] { 1, 2, 3 })
    {
        var neutralPath = Path.Combine(folder.Path, $"neutral-{schemaVersion}.json");
        var settings = new JsonObject
        {
            ["SchemaVersion"] = schemaVersion,
            ["DefaultId"] = PersonalityPresets.None.Id,
            ["Presets"] = new JsonArray(),
        };
        if (schemaVersion > 1) settings["CommandsEnabled"] = false;
        File.WriteAllText(neutralPath, settings.ToJsonString());
        var original = File.ReadAllBytes(neutralPath);
        var neutral = new PersonalityPresetStore(neutralPath);
        Check(neutral.Error is null && neutral.Default == PersonalityPresets.None &&
              File.ReadAllBytes(neutralPath).SequenceEqual(original),
            "existing neutral defaults survive every supported schema without a rewrite");
    }
    var custom = library.Save(null, "Technical Ș", "Be precise.\nUse short examples.")!;
    Check(custom is not null && Guid.TryParseExact(custom.Id, "N", out _) && library.SetDefault(custom.Id),
        "custom preset receives a stable GUID and can become default");
    var before = library.Presets;
    var edited = library.Save(custom!.Id, "Edited", "Keep uncertainty explicit.")!;
    Check(edited.Id == custom.Id && library.Default == edited && before.Last() == custom,
        "edit retains stable ID and updates default without mutating prior library snapshots");
    var loaded = new PersonalityPresetStore(path);
    Check(loaded.Error is null && loaded.Default == edited && loaded.Presets.Last() == edited,
        "custom content and selected default survive reload");
    var bytes = File.ReadAllBytes(path);
    Check(loaded.Save(PersonalityPresets.Armo.Id, "Changed", "Changed") is null &&
          !loaded.Delete(PersonalityPresets.None.Id) && !loaded.Delete(PersonalityPresets.Armo.Id) &&
          loaded.Save(null, "bad\nname", "text") is null && loaded.Save(null, new string('x', 41), "text") is null &&
          loaded.Save(null, " ", "text") is null && loaded.Save(null, "Valid", "bad\0instruction") is null &&
          loaded.Save(null, "Valid", new string('x', 2001)) is null &&
          loaded.Save("../elsewhere", "Valid", "text") is null && !loaded.SetDefault("missing") &&
          File.ReadAllBytes(path).SequenceEqual(bytes), "built-ins and invalid values cannot modify the library");
    Check(loaded.Delete(edited.Id) && loaded.Default == PersonalityPresets.None &&
          new PersonalityPresetStore(path).Default == PersonalityPresets.None, "deleting default resets future chats to None");
    for (var i = 0; i < PersonalityPresetStore.MaxCustomCount; i++)
        Check(loaded.Save(null, "Preset " + i, "Instructions") is not null, "custom preset within count limit");
    bytes = File.ReadAllBytes(path);
    Check(loaded.Save(null, "Overflow", "Instructions") is null && loaded.Presets.Count == 52 &&
          File.ReadAllBytes(path).SequenceEqual(bytes), "51st custom preset must leave library unchanged");
}

static void PersonalityLibraryFailures()
{
    using var folder = new TempFolder();
    var path = System.IO.Path.Combine(folder.Path, "personalities.json");
    var library = new PersonalityPresetStore(path);
    var custom = library.Save(null, "Original", "original instructions")!;
    Check(library.SetDefault(custom.Id), "prepare custom library default");
    var original = File.ReadAllBytes(path);
    var view = library.Presets;
    using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        Check(library.Save(custom.Id, "Replacement", "replacement") is null &&
              library.Save(null, "New", "new") is null && !library.Delete(custom.Id) &&
              !library.SetDefault(PersonalityPresets.Armo.Id) && library.Error is not null &&
              library.Default == custom && ReferenceEquals(view, library.Presets) &&
              File.ReadAllBytes(path).SequenceEqual(original), "failed atomic writes preserve prior view, default and bytes");
    }
    Check(Directory.GetFiles(folder.Path, "*.tmp").Length == 0 &&
          library.SetDefault(PersonalityPresets.Armo.Id) && library.Error is null,
        "failed writes clean temporary files and later saves can recover");
    foreach (var corrupt in new[] { "not JSON", "{}", "{\"SchemaVersion\":2,\"DefaultId\":\"builtin:none\",\"Presets\":[]}",
        "{\"SchemaVersion\":1,\"DefaultId\":\"missing\",\"Presets\":[]}",
        "{\"SchemaVersion\":1,\"DefaultId\":\"builtin:none\",\"DefaultId\":\"builtin:armo\",\"Presets\":[]}",
        "{\"SchemaVersion\":1,\"DefaultId\":\"builtin:none\",\"Presets\":[null]}" })
    {
        File.WriteAllText(path, corrupt);
        var bytes = File.ReadAllBytes(path);
        var broken = new PersonalityPresetStore(path);
        Check(broken.Error is not null && broken.Default == PersonalityPresets.None && broken.Presets.Count == 2 &&
              broken.Save(null, "Do not replace", "instructions") is null &&
              !broken.SetDefault(PersonalityPresets.Armo.Id) && File.ReadAllBytes(path).SequenceEqual(bytes),
            "corrupt library must retain bytes, report error and block silent replacement");
    }
    File.WriteAllBytes(path, [0xff, 0xfe, 0xfa]);
    var invalidUtf8 = new PersonalityPresetStore(path);
    Check(invalidUtf8.Error is not null && invalidUtf8.Save(null, "New", "text") is null &&
          File.ReadAllBytes(path).SequenceEqual(new byte[] { 0xff, 0xfe, 0xfa }), "malformed UTF-8 is preserved");
}

static void PersonalityDetailsPersistence()
{
    using var folder = new TempFolder();
    var path = System.IO.Path.Combine(folder.Path, "personalities.json");
    var id = Guid.NewGuid().ToString("N");
    foreach (var version in new[] { 1, 2 })
    {
        var legacy = new JsonObject
        {
            ["SchemaVersion"] = version, ["DefaultId"] = id,
            ["Presets"] = new JsonArray(new JsonObject { ["Id"] = id, ["Name"] = "Legacy", ["Instructions"] = "Keep this core intact." }),
        };
        if (version == 2) legacy["CommandsEnabled"] = false;
        File.WriteAllText(path, legacy.ToJsonString());
        var loaded = new PersonalityPresetStore(path);
        Check(loaded.Error is null && loaded.Default.Details == PersonalityDetails.Empty &&
            loaded.Default.Instructions == "Keep this core intact.", "legacy presets acquire empty optional fields without rewriting core");
    }
    var library = new PersonalityPresetStore(path);
    var details = new PersonalityDetails("Private preference Ș", "Likes tools, dislikes queues", "Private opinion", "Fictional background");
    var original = library.Save(id, "Legacy", "Keep this core intact.", details)!;
    Check(original is not null && new PersonalityPresetStore(path).Default == original, "all detail sections persist and reload");
    var clone = library.Save(null, original!.Name + " copy", original.Instructions, original.Details)!;
    Check(clone is not null && clone.Id != original.Id && clone.Details == original.Details, "clones retain optional details with a new ID");
    var chatsPath = System.IO.Path.Combine(folder.Path, "chats");
    var chats = new ChatStore(chatsPath, defaultPersonality: () => library.Default);
    var activeId = chats.ActiveId;
    var edited = library.Save(id, "Legacy", original.Instructions, details with { Preferences = "New preference" })!;
    Check(edited != original && chats.ActivePersonality == original, "library edits cannot alter existing chat details");
    Check(library.Delete(id) && new ChatStore(chatsPath).ActivePersonality == original,
        "saved chat details survive library deletion and reload");
    var chatPath = System.IO.Path.Combine(chatsPath, activeId + ".json");
    var legacyChat = JsonNode.Parse(File.ReadAllText(chatPath))!.AsObject();
    legacyChat["Personality"]!.AsObject().Remove("Details");
    File.WriteAllText(chatPath, legacyChat.ToJsonString());
    var loadedChat = new ChatStore(chatsPath);
    Check(loadedChat.ActivePersonalityError is null && loadedChat.ActivePersonality.Details == PersonalityDetails.Empty &&
        loadedChat.ActivePersonality.Instructions == original.Instructions, "older chat snapshots retain core and get empty details");
    Check(library.Save(null, "Invalid", "core", details with { Opinions = new string('x', 2001) }) is null &&
        library.Save(null, "Invalid", "core", details with { Background = "bad\0text" }) is null,
        "oversized and NUL optional sections cannot be saved");
    var malformed = JsonSerializer.SerializeToNode(original)!.AsObject();
    malformed["Details"]!["Unknown"] = "unexpected";
    Check(PersonalityPresets.ReadSnapshot(JsonSerializer.SerializeToElement(malformed)) is null,
        "unknown optional fields fail closed");
}

static void PersonalityDetailsQueries()
{
    var text = new string('Ș', PersonalityPresets.MaxDetailChars);
    var details = new PersonalityDetails(Preferences: text, Opinions: "A fictional opinion", Background: "   ");
    var lookup = new PersonalityDetailsLookup(details);
    var summary = JsonSerializer.SerializeToElement(lookup.Summary);
    Check(lookup.HasSections && summary.GetProperty("sections").EnumerateArray().Select(x => x.GetString())
        .SequenceEqual(new[] { "preferences", "opinions" }) && !summary.ToString().Contains(text),
        "summary carries only available section names");
    var result = JsonSerializer.SerializeToElement(lookup.Query(JsonSerializer.SerializeToElement(new { section = "preferences" })));
    Check(result.GetProperty("text").GetString() == text && result.EnumerateObject().Count() == 2 &&
        JsonSerializer.SerializeToUtf8Bytes(result).Length <= 20000, "only requested complete section fits shared tool budget");
    details = details with { Preferences = "Changed later" };
    Check(JsonSerializer.SerializeToElement(lookup.Query(JsonSerializer.SerializeToElement(new { section = "preferences" })))
        .GetProperty("text").GetString() == text, "prepared lookup remains frozen after preset replacement");
    foreach (var invalid in new[] { "{}", "null", "{\"section\":\"background\"}", "{\"section\":\"unknown\"}",
        "{\"section\":\"preferences\",\"extra\":true}", "{\"section\":\"preferences\",\"section\":\"opinions\"}" })
    {
        using var json = JsonDocument.Parse(invalid);
        var rejected = false;
        try { lookup.Query(json.RootElement); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "malformed, duplicate or unavailable section request rejected");
    }
    Check(!new PersonalityDetailsLookup(PersonalityDetails.Empty).HasSections, "empty personalities have no lookup metadata");
}

static void OfficialArmoDetails()
{
    var armo = PersonalityPresets.Armo;
    Check(PersonalityPresets.IsValid(armo) && armo.Instructions.Length > 0 &&
        armo.Instructions.Contains("query_personality_details", StringComparison.Ordinal),
        "official core fits the existing limit and names its optional-detail tool");
    var sections = new (string Name, string Text)[]
    {
        ("preferences", armo.Details.Preferences), ("likes_dislikes", armo.Details.LikesDislikes),
        ("opinions", armo.Details.Opinions), ("background", armo.Details.Background),
    };
    var lookup = new PersonalityDetailsLookup(armo.Details);
    var summary = JsonSerializer.SerializeToElement(lookup.Summary);
    Check(lookup.HasSections && summary.GetProperty("sections").EnumerateArray().Select(x => x.GetString())
        .SequenceEqual(sections.Select(section => section.Name)) && summary.EnumerateObject().Count() == 1,
        "all four built-in sections are discoverable by name only");
    foreach (var (name, text) in sections)
    {
        Check(!string.IsNullOrWhiteSpace(text) && text.Length <= PersonalityPresets.MaxDetailChars &&
            !text.Contains('\r') && !summary.ToString().Contains(text, StringComparison.Ordinal),
            "official section has canonical line endings, fits its bound and is not copied into discovery");
        var result = JsonSerializer.SerializeToElement(lookup.Query(JsonSerializer.SerializeToElement(new { section = name })));
        Check(result.EnumerateObject().Count() == 2 && result.GetProperty("section").GetString() == name &&
            result.GetProperty("text").GetString() == text && JsonSerializer.SerializeToUtf8Bytes(result).Length <= 20000,
            "requested official section is returned in full within the shared result budget");
    }
    var roundTrip = PersonalityPresets.ReadSnapshot(JsonSerializer.SerializeToElement(armo));
    Check(roundTrip == armo && !new PersonalityDetailsLookup(PersonalityPresets.None.Details).HasSections,
        "official core and sections roundtrip together while Default stays neutral");
}

static void PersonalitySnapshots()
{
    using var folder = new TempFolder();
    var libraryPath = System.IO.Path.Combine(folder.Path, "personalities.json");
    var chatsPath = System.IO.Path.Combine(folder.Path, "chats");
    var library = new PersonalityPresetStore(libraryPath);
    var custom = library.Save(null, "Technical", "Use concise technical answers.")!;
    Check(library.SetDefault(custom.Id), "prepare custom new-chat default");
    var now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    var store = new ChatStore(chatsPath, () => now, defaultPersonality: () => library.Default);
    var first = store.ActiveId;
    Check(store.ActivePersonality == custom && store.ActivePersonalityError is null, "initial chat snapshots default");
    var edited = library.Save(custom.Id, "Edited", "A different style.")!;
    Check(store.ActivePersonality == custom && library.Default == edited, "library edit leaves open chat's instructions unchanged");
    now = now.AddMinutes(1);
    var second = store.NewChat();
    Check(second != first && store.ActivePersonality == edited && library.Delete(custom.Id), "later chat snapshots edited default");
    Check(store.ActivePersonality == edited && store.Select(first) && store.ActivePersonality == custom,
        "deleting library entry cannot modify either saved chat");
    var reloaded = new ChatStore(chatsPath, () => now, defaultPersonality: () => library.Default);
    Check(reloaded.ActiveId == first && reloaded.ActivePersonality == custom &&
          reloaded.Select(second) && reloaded.ActivePersonality == edited, "deleted library IDs remain valid chat snapshots on reload");
    var third = reloaded.NewChat();
    Check(third != second && reloaded.ActivePersonality == PersonalityPresets.None,
        "future chat observes neutral default after default preset deletion");
    var libraryBytes = File.ReadAllBytes(libraryPath);
    Check(reloaded.SetActivePersonality(PersonalityPresets.Armo) && reloaded.ActivePersonality == PersonalityPresets.Armo &&
          File.ReadAllBytes(libraryPath).SequenceEqual(libraryBytes), "per-chat selection does not modify global library or default");
    var fourth = reloaded.NewChat();
    Check(fourth != third && reloaded.ActivePersonality == PersonalityPresets.None &&
          reloaded.Select(third) && reloaded.ActivePersonality == PersonalityPresets.Armo,
        "chat personality change persists independently from next new chat");

    // An older built-in template is still a valid saved snapshot after an application update.
    var oldArmo = PersonalityPresets.Armo with
    {
        Instructions = "Earlier Armo style instructions.", Details = PersonalityDetails.Empty,
    };
    reloaded.SetDraft("Keep this unfinished question.");
    reloaded.ActiveHistory.Add("Earlier question", "Earlier answer", [], null, false, "Armo");
    Check(reloaded.SetActivePersonality(oldArmo), "prepare historical built-in snapshot");
    var oldPath = Path.Combine(chatsPath, reloaded.ActiveId + ".json");
    var oldData = JsonNode.Parse(File.ReadAllText(oldPath))!.AsObject();
    foreach (var withDetails in new[] { true, false })
    {
        var saved = oldData.DeepClone().AsObject();
        if (!withDetails) saved["Personality"]!.AsObject().Remove("Details");
        File.WriteAllText(oldPath, saved.ToJsonString());
        var oldBytes = File.ReadAllBytes(oldPath);
        var oldLoaded = new ChatStore(chatsPath, () => now, defaultPersonality: () => PersonalityPresets.Armo);
        Check(oldLoaded.ActiveId == reloaded.ActiveId && oldLoaded.ActivePersonality == oldArmo &&
            oldLoaded.ActivePersonalityError is null && File.ReadAllBytes(oldPath).SequenceEqual(oldBytes) &&
            !new PersonalityDetailsLookup(oldLoaded.ActivePersonality.Details).HasSections,
            "legacy Armo with empty or absent details stays exact without fallback or silent replacement");
        Check(oldLoaded.SetActivePersonality(PersonalityPresets.Armo) &&
            oldLoaded.ActivePersonality == PersonalityPresets.Armo &&
            oldLoaded.ActiveDraft == "Keep this unfinished question." &&
            oldLoaded.ActiveHistory.View.Single().Question == "Earlier question" &&
            oldLoaded.ActiveHistory.View.Single().Answer == "Earlier answer" &&
            File.ReadAllBytes(libraryPath).SequenceEqual(libraryBytes),
            "explicit Apply update replaces core and details while preserving draft, history and global default");
        var updated = new ChatStore(chatsPath, () => now, defaultPersonality: () => PersonalityPresets.None);
        Check(updated.ActivePersonality == PersonalityPresets.Armo &&
            new PersonalityDetailsLookup(updated.ActivePersonality.Details).HasSections &&
            updated.ActiveDraft == oldLoaded.ActiveDraft && updated.ActiveHistory.View.Count == 1,
            "explicit built-in update survives reload independently of new-chat defaults");
    }
}

static void PersonalityMigration()
{
    foreach (var schema in new[] { 1, 2, 3 })
    {
        using var folder = new TempFolder();
        var activity = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
        var chosen = new CompanionModelSettings("gpt-6.1-sol", "high");
        var store = new ChatStore(folder.Path, () => activity, () => chosen);
        var id = store.ActiveId;
        store.ActiveHistory.Add("old question", "old answer", [], "old scope", false);
        store.SetDraft("old draft");
        Check(store.SetActiveNotes("old notes"), "prepare personality migration fixture");
        var path = System.IO.Path.Combine(folder.Path, id + ".json");
        var data = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        data["SchemaVersion"] = schema;
        data.Remove("Personality");
        if (schema < 3) data.Remove("Notes");
        if (schema == 1) data.Remove("ModelSelection");
        File.WriteAllText(path, data.ToJsonString());
        var loaded = new ChatStore(folder.Path, () => activity.AddDays(20), () => chosen, () => PersonalityPresets.Armo);
        Check(loaded.ActiveId == id && loaded.ActivePersonality == PersonalityPresets.None &&
              loaded.ActivePersonalityError is null && loaded.ActiveModelSelection == chosen &&
              loaded.ActiveNotes == (schema == 3 ? "old notes" : "") && loaded.ActiveDraft == "old draft" &&
              loaded.ActiveHistory.View.Single().Answer == "old answer" && loaded.View.Single().UpdatedAtUtc == activity,
            "legacy migration always chooses None while preserving other stored content and original activity");
        using var saved = JsonDocument.Parse(File.ReadAllText(path));
        Check(saved.RootElement.GetProperty("SchemaVersion").GetInt32() == 4 &&
              saved.RootElement.GetProperty("Personality").GetProperty("Id").GetString() == PersonalityPresets.None.Id,
            "schema-4 migration persists explicit None snapshot");
        var expired = new ChatStore(folder.Path, () => activity.AddDays(31), defaultPersonality: () => PersonalityPresets.Armo);
        Check(expired.ActiveId != id && !File.Exists(path) && expired.ActivePersonality == PersonalityPresets.Armo,
            "migration retains expiry and replacement chat uses current default");
    }
}

static void InvalidSavedPersonalities()
{
    foreach (var invalid in new[] { "missing", "null", "{}", "42", "{\"Id\":\"builtin:none\",\"Name\":\"None\"}",
        "{\"Id\":\"builtin:none\",\"Name\":\"None\",\"Instructions\":\"non-neutral instructions\"}",
        "{\"Id\":\"not-a-guid\",\"Name\":\"Invalid\",\"Instructions\":\"text\"}" })
    {
        using var folder = new TempFolder();
        var store = new ChatStore(folder.Path);
        var id = store.ActiveId;
        store.ActiveHistory.Add("keep question", "keep answer", [], "scope", false);
        store.SetDraft("keep draft");
        Check(store.SetActiveNotes("keep notes"), "prepare malformed-personality fixture");
        var model = store.ActiveModelSelection;
        var path = System.IO.Path.Combine(folder.Path, id + ".json");
        var data = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (invalid == "missing") data.Remove("Personality");
        else data["Personality"] = JsonNode.Parse(invalid);
        File.WriteAllText(path, data.ToJsonString());
        var bytes = File.ReadAllBytes(path);
        var loaded = new ChatStore(folder.Path, defaultPersonality: () => PersonalityPresets.Armo);
        Check(loaded.ActiveId == id && loaded.ActivePersonality == PersonalityPresets.None && loaded.ActivePersonalityError is not null &&
              loaded.ActiveDraft == "keep draft" && loaded.ActiveNotes == "keep notes" && loaded.ActiveModelSelection == model &&
              loaded.ActiveHistory.View.Single().Answer == "keep answer" && File.ReadAllBytes(path).SequenceEqual(bytes),
            "malformed persona cannot discard readable chat, silently apply default or rewrite file on load");
        loaded.SetDraft("new draft");
        Check(loaded.SaveActive(), "malformed personality still permits saving draft");
        var again = new ChatStore(folder.Path);
        Check(again.ActivePersonalityError is not null && again.ActiveDraft == "new draft" &&
              again.ActiveNotes == "keep notes", "routine draft save retains malformed snapshot and its explicit error");
        Check(again.SetActivePersonality(PersonalityPresets.Armo) && again.ActivePersonalityError is null &&
              new ChatStore(folder.Path).ActivePersonality == PersonalityPresets.Armo, "explicit valid selection repairs saved snapshot");
    }
}

static void PersonalitySaveFailure()
{
    using var folder = new TempFolder();
    var now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    var store = new ChatStore(folder.Path, () => now);
    var path = System.IO.Path.Combine(folder.Path, store.ActiveId + ".json");
    var bytes = File.ReadAllBytes(path);
    var view = store.View;
    now = now.AddMinutes(1);
    using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        Check(!store.SetActivePersonality(PersonalityPresets.Armo) && store.ActivePersonality == PersonalityPresets.None &&
              store.ActivePersonalityError is not null && ReferenceEquals(view, store.View) &&
              File.ReadAllBytes(path).SequenceEqual(bytes), "failed personality save leaves previous snapshot, activity and bytes intact");
    }
    Check(!store.SetActivePersonality(new PersonalityPreset("not-a-guid", "Invalid", "text")) &&
          store.ActivePersonality == PersonalityPresets.None && File.ReadAllBytes(path).SequenceEqual(bytes),
        "invalid snapshot cannot mutate active chat or disk");
    Check(store.SetActivePersonality(PersonalityPresets.Armo) && store.ActivePersonalityError is null &&
          store.View.Single().UpdatedAtUtc == now && new ChatStore(folder.Path, () => now).ActivePersonality == PersonalityPresets.Armo,
        "successful retry clears error, refreshes activity and persists personality");
    var blocked = System.IO.Path.Combine(folder.Path, "blocked");
    File.WriteAllText(blocked, "keep");
    var unavailable = new ChatStore(blocked);
    Check(!unavailable.SetActivePersonality(PersonalityPresets.Armo) && unavailable.ActivePersonality == PersonalityPresets.None &&
          unavailable.ActivePersonalityError is not null && File.ReadAllText(blocked) == "keep", "unavailable storage refuses personality save");
}

static object HistoryContractFixture()
{
    var term = new string('Q', 199) + "Z";
    ChatTurn[] turns = Enumerable.Range(0, 5).Select(_ => new ChatTurn(new string('ș', 2000),
        new string('ș', 30000) + term + new string('ș', 1000), [], new string('ș', 5000))).ToArray();
    var arguments = new { query = term, offset = 0, limit = 5 };
    var summary = ChatHistoryLookup.CreateSummary(turns);
    var result = ChatHistoryLookup.Query(turns, JsonSerializer.SerializeToElement(arguments));
    Check(result.rows.Length > 0 && result.rows.All(row => row.answer.Contains(term, StringComparison.Ordinal) &&
          row.answer.Length < 6000 && row.truncated) && result.next_offset == result.rows.Length &&
          JsonSerializer.SerializeToUtf8Bytes(result).Length <= ChatHistoryLookup.MaxBytes,
        "whole 200-character term near the end must survive excerpt shortening and escaped 20KB budgeting");
    return new { summary, arguments, result };
}

static void FlexibleContext()
{
    var history = new ChatHistory();
    for (var i = 0; i < 12; i++) history.Add("question " + i, "short answer Ș", [], "scope", false);
    var context = history.Context();
    var stats = history.ContextStats;
    Check(context.Length == 12 && stats.IncludedCount == 12 && stats.OmittedCount == 0 && stats.OmissionReason is null,
        "small complete exchanges should exceed the old four-turn cap");
    Check(stats.Bytes == JsonSerializer.SerializeToUtf8Bytes(context).Length && ReferenceEquals(stats, history.ContextStats),
        "selection byte count includes JSON escaping and is cached by revision");
    context[0] = new("mutated", "array");
    Check(history.Context()[0].Question == "question 0", "returned context arrays are defensive copies");
    history.Add("long", new string('a', 9000), [], null, false);
    Check(history.Context()[^1].Answer.Length == 9000 && !ReferenceEquals(stats, history.ContextStats),
        "an answer beyond 8000 characters can fit intact and invalidates cached selection");
    history.Add("oversized", new string('ș', 6000), [], null, false);
    Check(history.Context().Length == 1 && history.Context()[0].IsExcerpt &&
          history.ContextStats.OmissionReason == "latest_exchange_excerpt" && history.ContextStats.PartialCount == 1,
        "escaped newest exchange retains labelled continuity and reports its omission");
    history.Add("latest", "small", [], null, false);
    Check(history.Context().Length == 1 && history.ContextStats.OmittedCount == 14 &&
          history.ContextStats.OmissionReason == "history_byte_budget", "only consecutive newest complete pairs are selected");
    history.Restore([new("restored", "answer", [], null)]);
    Check(history.ContextStats.TotalCount == 1 && history.Context()[0].Question == "restored", "restore invalidates context cache");
    history.Clear();
    Check(history.ContextStats.Bytes == 2 && history.ContextStats.TotalCount == 0, "clear invalidates context cache");
}

static void ChatNotes()
{
    using var folder = new TempFolder();
    var now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    var store = new ChatStore(folder.Path, () => now);
    var first = store.ActiveId;
    now = now.AddMinutes(1);
    Check(store.SetActiveNotes("Goal: maximum pedigree Ș") && store.ActiveNotes == "Goal: maximum pedigree Ș" &&
          store.View.Single().UpdatedAtUtc == now, "notes save is explicit persisted activity");
    var path = System.IO.Path.Combine(folder.Path, first + ".json");
    var before = File.ReadAllBytes(path);
    var savedAt = now;
    now = now.AddMinutes(1);
    using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        Check(!store.SetActiveNotes("replacement") && store.ActiveNotes == "Goal: maximum pedigree Ș" &&
              store.ActiveNotesError is not null && store.View.Single().UpdatedAtUtc == savedAt &&
              File.ReadAllBytes(path).SequenceEqual(before), "failed notes save keeps previous notes, activity and disk bytes");
    }
    Check(!store.SetActiveNotes(new string('x', 2001)) && !store.SetActiveNotes("bad\0notes") &&
          File.ReadAllBytes(path).SequenceEqual(before), "invalid notes cannot alter disk or current notes");
    var second = store.NewChat();
    Check(second != first && store.ActiveNotes == "", "new chat must not inherit another chat's notes");
    Check(store.SetActiveNotes("other notes") && store.Select(first) && store.ActiveNotes == "Goal: maximum pedigree Ș",
        "notes are per chat");
    var loaded = new ChatStore(folder.Path, () => now);
    Check(loaded.ActiveId == first && loaded.ActiveNotes == "Goal: maximum pedigree Ș" &&
          loaded.SetActiveNotes("") && loaded.ActiveNotesError is null &&
          new ChatStore(folder.Path, () => now).ActiveNotes == "", "notes survive reload and explicit clearing");
}

static void NotesMigration()
{
    using var folder = new TempFolder();
    var activity = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
    var store = new ChatStore(folder.Path, () => activity);
    var id = store.ActiveId;
    var choice = new CompanionModelSettings("gpt-6.1-sol", "high");
    store.ActiveHistory.Add("old question", "old answer", [], "old scope", false);
    store.SetDraft("old draft");
    Check(store.SetActiveModelSelection(choice), "prepare schema-2 fixture");
    var path = System.IO.Path.Combine(folder.Path, id + ".json");
    var data = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    data["SchemaVersion"] = 2;
    data.Remove("Notes");
    File.WriteAllText(path, data.ToJsonString());
    var loaded = new ChatStore(folder.Path, () => activity.AddDays(20));
    Check(loaded.ActiveNotes == "" && loaded.ActiveId == id && loaded.ActiveModelSelection == choice &&
          loaded.ActiveDraft == "old draft" && loaded.ActiveHistory.View.Single().Answer == "old answer" &&
          loaded.View.Single().UpdatedAtUtc == activity, "schema-2 notes migration preserves all existing content and activity");
    using var migrated = JsonDocument.Parse(File.ReadAllText(path));
    Check(migrated.RootElement.GetProperty("SchemaVersion").GetInt32() == 4 &&
          migrated.RootElement.GetProperty("Notes").GetString() == "", "schema-4 stores explicit empty notes");
    var expired = new ChatStore(folder.Path, () => activity.AddDays(31));
    Check(expired.ActiveId != id && !File.Exists(path), "notes migration must not extend expiry");
}

static void HistoryLookup()
{
    static JsonElement Args(string query, int offset = 0, int limit = 5) =>
        JsonSerializer.SerializeToElement(new { query, offset, limit });
    ChatTurn[] turns = [new("Breeding choice", "First answer", [], "inventory historical"),
        new("other", "irrelevant", [], null), new("Another BREEDING choice", "Last answer", [], null)];
    var page = ChatHistoryLookup.Query(turns, Args("breeding choice", limit: 1));
    Check(page.scope == "current_chat_history" && page.exchange_count == 3 && page.total_matches == 2 &&
          page.rows.Single().index == 1 && page.next_offset == 1, "AND matching uses stable original indices and pagination");
    var next = ChatHistoryLookup.Query(turns, Args("breeding choice", page.next_offset!.Value));
    Check(next.rows.Single().index == 3 && next.next_offset is null, "page offset is matched rows, not original index");
    Check(ChatHistoryLookup.Query(turns, Args("inventory")).rows.Single().context_label == "inventory historical",
        "context labels are searchable but remain labelled historical text");
    Check(ChatHistoryLookup.Query(turns, Args("", 100)).rows.Length == 0 &&
          ChatHistoryLookup.Query([], Args("")).exchange_count == 0, "empty and past-end pages terminate");
    ChatTurn[] huge = Enumerable.Range(0, 5).Select(_ => new ChatTurn(new string('ș', 2000),
        new string('ș', 30000) + " target-near-end " + new string('ș', 1000), [], new string('ș', 5000))).ToArray();
    var excerpt = ChatHistoryLookup.Query(huge, Args("target-near-end"));
    Check(excerpt.rows.Length > 0 && excerpt.next_offset == excerpt.rows.Length &&
          JsonSerializer.SerializeToUtf8Bytes(excerpt).Length <= 20000 &&
          excerpt.rows.All(row => row.truncated && row.answer.Contains("target-near-end") &&
              row.answer.Length <= 6000 && row.context_label!.Length <= 1000),
        "large escaped results preserve matching excerpts, fit budget and make pagination progress");
    foreach (var invalid in new[] { "{}", "{\"query\":\"x\",\"offset\":-1,\"limit\":1}",
        "{\"query\":\"x\",\"offset\":0,\"limit\":6}", "{\"query\":\"x\",\"offset\":0,\"limit\":1,\"file\":\"other\"}",
        "{\"query\":\"x\",\"query\":\"y\",\"offset\":0,\"limit\":1}" })
    {
        using var document = JsonDocument.Parse(invalid);
        try { ChatHistoryLookup.Query(turns, document.RootElement); throw new Exception("invalid arguments accepted"); }
        catch (ArgumentException) { }
    }
}

static void PerChatModels()
{
    using var folder = new TempFolder();
    var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    var originalDefault = new CompanionModelSettings("gpt-6-sol", "medium");
    var nextDefault = new CompanionModelSettings("gpt-6.1-sol", "high");
    var personalChoice = new CompanionModelSettings("gpt-6-astra", "xhigh");
    var currentDefault = originalDefault;
    var store = new ChatStore(folder.Path, () => now, () => currentDefault);
    var first = store.ActiveId;
    Check(store.ActiveModelSelection == originalDefault && store.ActiveModelError is null, "first chat snapshots initial default");
    currentDefault = nextDefault;
    Check(store.ActiveModelSelection == originalDefault, "changing default must not mutate existing chat");
    now = now.AddMinutes(1);
    var second = store.NewChat();
    Check(first != second && store.ActiveModelSelection == nextDefault, "new chat snapshots latest default");
    var previousView = store.View;
    now = now.AddMinutes(1);
    Check(store.SetActiveModelSelection(personalChoice) && store.ActiveModelSelection == personalChoice &&
          store.View.Single(x => x.Id == second).UpdatedAtUtc == now && !ReferenceEquals(previousView, store.View),
        "explicit model edit persists choice and refreshes activity");
    Check(currentDefault == nextDefault, "chat edit must not modify global default");
    Check(store.Select(first) && store.ActiveModelSelection == originalDefault, "switching restores first model");
    var firstPath = System.IO.Path.Combine(folder.Path, first + ".json");
    var firstBytes = File.ReadAllBytes(firstPath);
    var reloaded = new ChatStore(folder.Path, () => now, () => new CompanionModelSettings("new-default", "low"));
    Check(reloaded.ActiveId == first && reloaded.ActiveModelSelection == originalDefault &&
          File.ReadAllBytes(firstPath).SequenceEqual(firstBytes), "reload retains saved choice without rewriting a current-schema chat");
    Check(reloaded.Select(second) && reloaded.ActiveModelSelection == personalChoice, "second saved model reloads independently");
    reloaded.NewChat();
    Check(reloaded.ActiveModelSelection == new CompanionModelSettings("new-default", "low"), "later new chat uses reload default");
}

static void PerChatProviders()
{
    using var folder = new TempFolder();
    var now = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    var currentDefault = new CompanionModelSettings("gpt-6-sol", "medium");
    var store = new ChatStore(folder.Path, () => now, () => currentDefault);
    var first = store.ActiveId;
    store.ActiveHistory.Add("Keep question", "Keep answer", [], null, false);
    store.SetDraft("Keep draft");
    Check(store.SaveActive(), "save legacy-provider fixture");
    var path = System.IO.Path.Combine(folder.Path, first + ".json");
    var data = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    data["ModelSelection"]!.AsObject().Remove("Provider");
    File.WriteAllText(path, data.ToJsonString());
    var legacyBytes = File.ReadAllBytes(path);

    currentDefault = new CompanionModelSettings("auto", "default", "copilot");
    var loaded = new ChatStore(folder.Path, () => now, () => currentDefault);
    Check(loaded.ActiveId == first && loaded.ActiveModelSelection == new CompanionModelSettings("gpt-6-sol", "medium", "codex") &&
          loaded.ActiveHistory.View.Single().Answer == "Keep answer" && loaded.ActiveDraft == "Keep draft" &&
          File.ReadAllBytes(path).SequenceEqual(legacyBytes), "old pairs retain Codex and content without rewriting on load");
    var second = loaded.NewChat();
    Check(loaded.ActiveModelSelection == currentDefault, "new chat snapshots Copilot default");
    var thirdChoice = new CompanionModelSettings("grok-auto", "none", "grok");
    Check(loaded.SetActiveModelSelection(thirdChoice) && currentDefault.Provider == "copilot", "chat override preserves defaults");
    var reloaded = new ChatStore(folder.Path, () => now, () => currentDefault);
    Check(reloaded.ActiveId == second && reloaded.ActiveModelSelection == thirdChoice &&
          reloaded.Select(first) && reloaded.ActiveModelSelection?.Provider == "codex" &&
          reloaded.ActiveHistory.View.Single().Answer == "Keep answer" && reloaded.ActiveDraft == "Keep draft",
        "saved providers and original transcript survive switches and reloads");
    reloaded.NewChat();
    Check(reloaded.ActiveModelSelection == currentDefault, "later new chats still use unchanged Copilot default");
}

static void LegacyModelMigration()
{
    using var folder = new TempFolder();
    var activity = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
    var store = new ChatStore(folder.Path, () => activity);
    var first = store.ActiveId;
    store.ActiveHistory.Add("Legacy question", "Legacy answer", [], "Scope kept", false);
    store.SetDraft("legacy draft Ș");
    Check(store.SaveActive(), "legacy fixture save");
    var second = store.NewChat();
    foreach (var id in new[] { first, second })
    {
        var path = System.IO.Path.Combine(folder.Path, id + ".json");
        var data = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        data["SchemaVersion"] = 1;
        data.Remove("ModelSelection");
        File.WriteAllText(path, data.ToJsonString());
    }
    var now = activity.AddDays(20);
    var chosen = new CompanionModelSettings("migration-default", "high");
    var calls = 0;
    var migrated = new ChatStore(folder.Path, () => now, () => { calls++; return chosen; });
    Check(calls == 1 && migrated.ActiveId == second && migrated.View.All(x => x.UpdatedAtUtc == activity),
        "migration should snapshot startup default once and retain all activity timestamps");
    Check(migrated.Select(first) && migrated.ActiveModelSelection == chosen && migrated.ActiveDraft == "legacy draft Ș" &&
          migrated.ActiveHistory.View.Single().ContextLabel == "Scope kept", "migration preserves readable history, scope and draft");
    foreach (var id in new[] { first, second })
    {
        using var data = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(folder.Path, id + ".json")));
        Check(data.RootElement.GetProperty("SchemaVersion").GetInt32() == 4 &&
              data.RootElement.GetProperty("UpdatedAtUtc").GetDateTimeOffset() == activity &&
              data.RootElement.GetProperty("ModelSelection").GetProperty("Model").GetString() == chosen.Model,
            "schema-4 migration persists model without extending expiry");
    }
    var again = new ChatStore(folder.Path, () => now, () => new CompanionModelSettings("changed-default", "low"));
    Check(again.Select(first) && again.ActiveModelSelection == chosen, "later defaults cannot alter migrated choice");
    var expired = new ChatStore(folder.Path, () => activity.AddDays(31));
    Check(!expired.View.Any(x => x.Id == first || x.Id == second), "migration must not reset 30-day expiry");
}

static void InvalidSavedModels()
{
    foreach (var invalid in new[] { "null", "{}", "42", "{\"Model\":\"gpt-6-sol\"}",
                 "{\"Model\":\"bad model\",\"Effort\":\"high\"}",
                 "{\"Model\":\"gpt-6-sol\",\"Effort\":7}",
                 "{\"Model\":\"gpt-6-sol\",\"Effort\":\"HIGH\"}",
                 "{\"Model\":\"auto\",\"Effort\":\"default\",\"Provider\":null}",
                 "{\"Model\":\"auto\",\"Effort\":\"default\",\"Provider\":\"Copilot\"}",
                 "{\"Model\":\"auto\",\"Effort\":\"default\",\"Provider\":\"unknown\"}",
                 "{\"Model\":\"auto\",\"Effort\":\"default\",\"Provider\":\"codex\",\"Provider\":\"copilot\"}" })
    {
        using var folder = new TempFolder();
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var store = new ChatStore(folder.Path, () => now);
        var id = store.ActiveId;
        store.ActiveHistory.Add("keep question", "keep answer", [], null, false);
        store.SetDraft("keep draft");
        Check(store.SaveActive(), "prepare invalid-model fixture");
        var path = System.IO.Path.Combine(folder.Path, id + ".json");
        var json = File.ReadAllText(path);
        using var data = JsonDocument.Parse(json);
        File.WriteAllText(path, json.Replace(data.RootElement.GetProperty("ModelSelection").GetRawText(), invalid, StringComparison.Ordinal));
        var originalBytes = File.ReadAllBytes(path);
        var loaded = new ChatStore(folder.Path, () => now, () => new CompanionModelSettings("must-not-fallback", "low"));
        Check(loaded.ActiveId == id && loaded.ActiveModelSelection is null && loaded.ActiveModelError is not null &&
              loaded.ActiveDraft == "keep draft" && loaded.ActiveHistory.View.Single().Answer == "keep answer" &&
              originalBytes.SequenceEqual(File.ReadAllBytes(path)), "invalid model must block choice while preserving readable file");
        loaded.SetDraft("edited draft");
        Check(loaded.SaveActive(), "invalid model should not prevent saving a draft");
        var stillInvalid = new ChatStore(folder.Path, () => now);
        Check(stillInvalid.ActiveModelSelection is null && stillInvalid.ActiveDraft == "edited draft" &&
              stillInvalid.ActiveHistory.View.Count == 1, "routine draft save cannot silently repair invalid model");
        var repaired = new CompanionModelSettings("gpt-6-sol", "high");
        Check(stillInvalid.SetActiveModelSelection(repaired) && stillInvalid.ActiveModelError is null,
            "explicit valid selection repairs model only after saving");
        var final = new ChatStore(folder.Path, () => now);
        Check(final.ActiveModelSelection == repaired && final.ActiveHistory.View.Count == 1 && final.ActiveDraft == "edited draft",
            "repaired choice and retained content survive reload");
    }
}

static void LegacyMigrationFailure()
{
    using var folder = new TempFolder();
    var activity = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    var store = new ChatStore(folder.Path, () => activity);
    var id = store.ActiveId;
    store.ActiveHistory.Add("keep question", "keep answer", [], null, false);
    store.SetDraft("keep draft");
    Check(store.SaveActive(), "prepare legacy fixture");
    var path = System.IO.Path.Combine(folder.Path, id + ".json");
    var data = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    data["SchemaVersion"] = 1;
    data.Remove("ModelSelection");
    File.WriteAllText(path, data.ToJsonString());
    var before = File.ReadAllBytes(path);
    var chosen = new CompanionModelSettings("gpt-6-astra", "high");
    ChatStore failed;
    using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        failed = new ChatStore(folder.Path, () => activity.AddDays(20), () => chosen);
        Check(failed.ActiveId == id && failed.ActiveModelSelection is null && failed.ActiveModelError is not null &&
              failed.ActiveHistory.View.Single().Answer == "keep answer" && failed.ActiveDraft == "keep draft" &&
              failed.View.Single().UpdatedAtUtc == activity && File.ReadAllBytes(path).SequenceEqual(before),
            "failed migration must retain readable legacy chat, original activity and bytes without an unsaved model fallback");
    }
    Check(failed.SetActiveModelSelection(chosen) && failed.ActiveModelError is null &&
          new ChatStore(folder.Path, () => activity.AddDays(20)).ActiveModelSelection == chosen,
        "explicit selection can persist the recovered legacy chat after storage recovers");
}

static void ModelSaveFailure()
{
    using var folder = new TempFolder();
    var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    var store = new ChatStore(folder.Path, () => now);
    var oldChoice = store.ActiveModelSelection;
    var originalTime = store.View.Single().UpdatedAtUtc;
    var path = System.IO.Path.Combine(folder.Path, store.ActiveId + ".json");
    var before = File.ReadAllBytes(path);
    now = now.AddMinutes(1);
    var requested = new CompanionModelSettings("gpt-6-astra", "high");
    // Permit reads but prevent replacing the existing file, emulating an atomic-save failure.
    using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        Check(!store.SetActiveModelSelection(requested) && store.ActiveModelSelection == oldChoice &&
              store.ActiveModelError is not null && store.View.Single().UpdatedAtUtc == originalTime &&
              File.ReadAllBytes(path).SequenceEqual(before), "failed write must retain selection, activity and original bytes");
    }
    Check(Directory.GetFiles(folder.Path, "*.tmp").Length == 0, "failed save should clean its temporary file");
    Check(!store.SetActiveModelSelection(new CompanionModelSettings("bad model", "high")) &&
          store.ActiveModelSelection == oldChoice && File.ReadAllBytes(path).SequenceEqual(before),
        "invalid requested model must fail without touching existing choice");
    Check(store.SetActiveModelSelection(requested) && store.ActiveModelError is null &&
          new ChatStore(folder.Path, () => now).ActiveModelSelection == requested, "successful retry clears error and persists selection");
}

static void MissingDefaultAndStorage()
{
    using var folder = new TempFolder();
    CompanionModelSettings? defaultChoice = null;
    var store = new ChatStore(folder.Path, defaultModelSelection: () => defaultChoice);
    var first = store.ActiveId;
    Check(store.ActiveModelSelection is null && store.ActiveModelError is not null, "missing default should fail closed");
    defaultChoice = new CompanionModelSettings("gpt-6-sol", "high");
    Check(store.ActiveModelSelection is null, "fixed default does not silently repair existing chat");
    var second = store.NewChat();
    Check(first != second && store.ActiveModelSelection == defaultChoice && store.Select(first) && store.ActiveModelSelection is null,
        "missing default is persisted per chat while later chats can use a valid default");
    var loaded = new ChatStore(folder.Path, defaultModelSelection: () => defaultChoice);
    Check(loaded.ActiveId == first && loaded.ActiveModelSelection is null, "saved null must not be replaced by reload default");
    Check(loaded.SetActiveModelSelection(defaultChoice) && loaded.ActiveModelError is null, "explicit repair persists choice");
    var blocked = System.IO.Path.Combine(folder.Path, "blocked");
    File.WriteAllText(blocked, "keep");
    var unavailable = new ChatStore(blocked);
    var oldChoice = unavailable.ActiveModelSelection;
    Check(!unavailable.SetActiveModelSelection(new CompanionModelSettings("gpt-6-astra", "high")) &&
          unavailable.ActiveModelSelection == oldChoice && unavailable.ActiveModelError is not null && File.ReadAllText(blocked) == "keep",
        "unavailable storage must visibly refuse a model save and retain prior selection");
}

static void SummarySnapshots()
{
    using var folder = new TempFolder();
    var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    var store = new ChatStore(folder.Path, () => now);
    var first = store.ActiveId;
    var initial = store.View;
    Check(ReferenceEquals(initial, store.View), "unchanged summaries should reuse their snapshot");
    store.SetDraft("draft");
    Check(ReferenceEquals(initial, store.View), "typing does not change displayed summaries");
    now = now.AddMinutes(1);
    Check(store.SaveActive(), "save changed draft");
    var savedDraft = store.View;
    Check(!ReferenceEquals(initial, savedDraft) && savedDraft[0].UpdatedAtUtc == now &&
          initial[0].UpdatedAtUtc < now, "saved activity must refresh without mutating the old summary");
    Check(store.SaveActive() && ReferenceEquals(savedDraft, store.View), "no-op saves must reuse the view");
    store.ActiveHistory.Add("Title from first question", "answer", [], null, false);
    Check(store.SaveActive() && store.View[0].Title == "Title from first question" && savedDraft[0].Title == "New chat",
        "saved titles must refresh without mutating earlier summaries");
    for (var i = 0; i < 10; i++) { now = now.AddMinutes(1); store.NewChat(); }
    var archived = store.View;
    Check(archived.Count == 11 && archived.Single(x => x.Id == first).IsArchived, "new chats must refresh archive membership");
    Check(store.Select(first) && ReferenceEquals(archived, store.View), "selection alone does not change summaries");
    store.SetDraft("updated draft");
    now = now.AddMinutes(1);
    Check(store.SaveActive() && store.View[0].Id == first && !store.View[0].IsArchived &&
          archived.Single(x => x.Id == first).IsArchived, "activity must reorder archives while old snapshots remain stable");
    var beforeDelete = store.View;
    var inactive = beforeDelete.Last().Id;
    Check(store.Delete(inactive) && store.View.Count == 10 && beforeDelete.Count == 11 &&
          !store.View.Any(x => x.Id == inactive), "inactive deletion must refresh membership");
    var beforeActiveDelete = store.View;
    Check(store.Delete(first) && store.ActiveId != first && store.View.Count == 9 &&
          beforeActiveDelete.Any(x => x.Id == first), "active deletion must refresh membership without mutating prior views");
    foreach (var chat in store.View.Skip(1).ToArray()) Check(store.Delete(chat.Id), "remove extra chats");
    var finalChat = store.View;
    Check(store.Delete(store.ActiveId) && store.View.Count == 1 && store.View[0].Id != finalChat[0].Id,
        "deleting the last chat must replace the cached summary");
}

static void HistorySnapshots()
{
    var history = new ChatHistory();
    var empty = history.View;
    Check(ReferenceEquals(empty, history.View), "unchanged history should reuse its snapshot");
    history.Add("first", "answer", [], null, false);
    var first = history.View;
    Check(empty.Count == 0 && first.Count == 1 && ReferenceEquals(first, history.View),
        "append must publish a new stable snapshot");
    history.Add("second", "answer", [], null, false);
    var second = history.View;
    Check(first.Count == 1 && second.Count == 2, "append must retain earlier snapshot contents");
    history.Restore([new ChatTurn("restored", "answer", [], null)]);
    var restored = history.View;
    Check(restored.Single().Question == "restored" && second.Count == 2, "restore must replace the view without changing prior snapshots");
    history.Clear();
    Check(history.View.Count == 0 && restored.Single().Question == "restored", "clear must publish an empty view");
    for (var i = 0; i < ChatHistory.MaxStoredTurns; i++) history.Add("q" + i, "answer", [], null, false);
    var full = history.View;
    history.Add("newest", "answer", [], null, false);
    Check(history.View.Count == ChatHistory.MaxStoredTurns + 1 && history.View[0].Question == "q0" &&
          history.View[^1].Question == "newest" && full[0].Question == "q0" && full[^1].Question != "newest",
        "an already completed answer beyond capacity must preserve every turn and the earlier snapshot");
}

static void PersistAndSwitch()
{
    using var folder = new TempFolder();
    var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    var store = new ChatStore(folder.Path, () => now);
    var first = store.ActiveId;
    store.ActiveHistory.Add("Where is my **ore**?", "Use [guide](https://na.finalfantasyxiv.com/lodestone/playguide/)",
        [new QuestionSource("Guide", "https://na.finalfantasyxiv.com/lodestone/playguide/", true)],
        "Scope: carried Inventory1-4", true);
    store.SetDraft("unfinished first draft Ș");
    now = now.AddMinutes(1);
    var second = store.NewChat();
    Check(first != second && store.View.Count == 2 && store.ActiveHistory.View.Count == 0,
        "new chat should preserve prior tab and start empty");
    Check(File.ReadAllText(System.IO.Path.Combine(folder.Path, first + ".json"), Encoding.UTF8).Contains("Ș", StringComparison.Ordinal),
        "disk JSON should retain UTF-8 Unicode without escape growth");
    store.SetDraft("unfinished second draft");
    store.ActiveHistory.Add("Follow-up", "Answer two", [], null, false);
    now = now.AddMinutes(1);
    Check(store.Select(first), "select first chat");
    Check(store.ActiveDraft == "unfinished first draft Ș" && store.ActiveHistory.ContainsInventory &&
          store.ActiveHistory.Context().Length == 1 && store.ActiveHistory.ContainsSource("https://na.finalfantasyxiv.com/lodestone/playguide/"),
        "first chat should restore draft, scope and source");
    Check(store.ActiveHistory.View[0].ContextLabel == "Scope: carried Inventory1-4", "scope label should persist");
    var reloaded = new ChatStore(folder.Path, () => now.AddMinutes(1));
    Check(reloaded.ActiveId == first && reloaded.View.Count == 2, "active selection and tabs should survive reload");
    Check(reloaded.ActiveHistory.View.Count == 1 && reloaded.ActiveHistory.Context()[0].Answer.EndsWith("Scope: carried Inventory1-4"),
        "completed turn and transmitted qualifier should survive reload");
    Check(reloaded.View.Single(x => x.Id == first).Title == "Where is my **ore**?", "first question should supply title");
    Check(reloaded.Select(second) && reloaded.ActiveDraft == "unfinished second draft" &&
          reloaded.ActiveHistory.View.Single().Question == "Follow-up", "second chat should restore independently");
}

static void Expiry()
{
    using var folder = new TempFolder();
    var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    var first = new ChatStore(folder.Path, () => now);
    first.ActiveHistory.Add("old", "answer", [], null, false);
    Check(first.SaveActive(), "save first chat");
    var id = first.ActiveId;
    var path = System.IO.Path.Combine(folder.Path, id + ".json");
    var original = File.ReadAllBytes(path);
    now = now.AddDays(20);
    var onDay20 = new ChatStore(folder.Path, () => now);
    Check(onDay20.ActiveId == id && onDay20.Select(id) && onDay20.SaveActive(), "load/select should retain chat");
    Check(original.SequenceEqual(File.ReadAllBytes(path)), "load and no-op save must not retouch stored activity");
    now = now.AddDays(11);
    var expired = new ChatStore(folder.Path, () => now);
    Check(expired.ActiveId != id && !File.Exists(path) && !expired.View.Any(x => x.Id == id),
        "chat untouched for 31 days should expire on load");
}

static void Archives()
{
    using var folder = new TempFolder();
    var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    var store = new ChatStore(folder.Path, () => now);
    for (var i = 0; i < 11; i++) { now = now.AddMinutes(1); store.NewChat(); }
    Check(store.View.Count == 12 && store.View.Count(x => !x.IsArchived) == 10 &&
          store.View.Count(x => x.IsArchived) == 2, "only ten newest chats should be recent");
    var archived = store.View.Last().Id;
    Check(File.Exists(System.IO.Path.Combine(folder.Path, archived + ".json")), "archive must remain on disk");
    var reloaded = new ChatStore(folder.Path, () => now.AddMinutes(1));
    Check(reloaded.View.Count == 12 && reloaded.Select(archived) && reloaded.ActiveId == archived,
        "older archived chat should be selectable after reload");
}

static void CorruptAndOutside()
{
    using var folder = new TempFolder();
    var badName = Guid.NewGuid().ToString("N") + ".json";
    var badPath = System.IO.Path.Combine(folder.Path, badName);
    File.WriteAllBytes(badPath, [0xff, 0xfe, 0xfa]);
    var malformedUtf8 = System.IO.Path.Combine(folder.Path, Guid.NewGuid().ToString("N") + ".json");
    var validJson = Encoding.UTF8.GetBytes("{\"SchemaVersion\":1,\"Id\":\"00000000000000000000000000000000\",\"Title\":\"bad\",\"Draft\":\"x\"}");
    validJson[^3] = 0xff; // Invalid byte within the otherwise JSON-shaped draft string.
    File.WriteAllBytes(malformedUtf8, validJson);
    var unrelated = System.IO.Path.Combine(folder.Path, "outside.json");
    File.WriteAllText(unrelated, "leave me alone", Encoding.UTF8);
    var store = new ChatStore(folder.Path);
    Check(store.Error is not null && File.ReadAllBytes(badPath).SequenceEqual(new byte[] { 0xff, 0xfe, 0xfa }),
        "corrupt chat must report an error and remain untouched");
    Check(File.ReadAllBytes(malformedUtf8).SequenceEqual(validJson), "malformed UTF-8 must never be normalized and overwritten");
    Check(File.Exists(unrelated) && !store.View.Any(x => x.Id == "outside"),
        "non-GUID filename must be ignored and preserved");
}

static void UnavailableStorage()
{
    using var folder = new TempFolder();
    var blocked = System.IO.Path.Combine(folder.Path, "not-a-directory");
    File.WriteAllText(blocked, "file occupies requested directory");
    var store = new ChatStore(blocked);
    Check(store.Error is not null && store.View.Count == 1, "constructor should warn and start an in-memory chat");
    store.ActiveHistory.Add("question", "answer", [], null, false);
    Check(!store.SaveActive() && store.StorageInfo.UnsavedChats == 1, "in-memory session must not claim successful persistence");
    var first = store.ActiveId;
    var second = store.NewChat();
    Check(first != second && store.Select(first) && store.ActiveHistory.View.Count == 1,
        "in-memory store should support new and switched chats");
    Check(File.ReadAllText(blocked) == "file occupies requested directory", "blocked path should remain untouched");
}

static void IndexWriteFailure()
{
    using var folder = new TempFolder();
    var store = new ChatStore(folder.Path);
    var original = store.ActiveId;
    var originalPath = System.IO.Path.Combine(folder.Path, original + ".json");
    store.ActiveHistory.Add("keep", "existing answer", [], null, false);
    Check(store.SaveActive(), "precondition: original chat saved");
    // A directory at the index path makes atomic replacement fail without touching chat files.
    Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "active.json"));
    Check(store.NewChat() != original && store.ActiveId != original && store.View.Count == 2 &&
          Directory.GetFiles(folder.Path, "*.json").Length == 3 && File.Exists(originalPath) && store.Select(original),
        "failed index save preserves both chats and permits selection");
    Check(!store.Delete(original) && store.ActiveId == original && store.View.Count == 2 &&
          File.Exists(originalPath) && Directory.GetFiles(folder.Path, "*.json").Length == 3,
        "failed active deletion must leave old chat and avoid an orphan replacement");
    Check(store.Error is not null, "storage failure should be visible");

    using var secondFolder = new TempFolder();
    var secondStore = new ChatStore(secondFolder.Path);
    var firstId = secondStore.ActiveId;
    var secondId = secondStore.NewChat();
    var indexPath = System.IO.Path.Combine(secondFolder.Path, "active.json");
    File.Delete(indexPath);
    Directory.CreateDirectory(indexPath);
    Check(!secondStore.Delete(secondId) && secondStore.ActiveId == secondId &&
          secondStore.View.Count == 2 &&
          File.Exists(System.IO.Path.Combine(secondFolder.Path, firstId + ".json")) &&
          File.Exists(System.IO.Path.Combine(secondFolder.Path, secondId + ".json")),
        "failed index switch must not delete active chat when another chat exists");
}

static void DeleteBoundary()
{
    using var folder = new TempFolder();
    var store = new ChatStore(folder.Path);
    var first = store.ActiveId;
    var second = store.NewChat();
    var outside = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(folder.Path)!, "outside-" + Guid.NewGuid().ToString("N"));
    File.WriteAllText(outside, "keep");
    try
    {
        Check(!store.Delete("../" + System.IO.Path.GetFileName(outside)) && File.Exists(outside),
            "path-like IDs must not reach filesystem deletion");
        Check(store.Delete(first) && store.View.Count == 1 && store.ActiveId == second &&
              !File.Exists(System.IO.Path.Combine(folder.Path, first + ".json")),
            "delete should remove only named inactive chat");
    }
    finally { File.Delete(outside); }
}

sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FFXIVChatStore-" + Guid.NewGuid().ToString("N"));
    public TempFolder() => Directory.CreateDirectory(this.Path);
    public void Dispose() => Directory.Delete(this.Path, recursive: true);
}

namespace CompAI
{
    internal readonly record struct QuestionSource(string Title, string Url, bool IsBundled = false);
}
