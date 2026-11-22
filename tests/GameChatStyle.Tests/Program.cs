using CompAI;
using System.Text;
using System.Text.Json;

var tests = new (string Name, Action Run)[]
{
    ("default, save and reload RGB boundaries", RoundTrip),
    ("schema 1 migrates without losing the user colour", LegacyMigration),
    ("personality overrides persist independently and reset to automatic", PersonalityOverrides),
    ("new, cloned and built-in colour drafts wait for Save and survive failure", PersonalityDrafts),
    ("bad files retain their bytes and use the default", BadFiles),
    ("personality and file size bounds are enforced", Bounds),
    ("failed saves retain every previous live and saved colour", FailedSave),
    ("preset IDs deterministically select a readable varied palette", StablePalette),
};
var failures = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
return failures == 0 ? 0 : 1;

static void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
}

static void RoundTrip()
{
    using var folder = new TempFolder();
    var path = Path.Combine(folder.Path, "nested", "style.json");
    var store = new GameChatStyleStore(path);
    Check(store.UserColorRgb == 0xFFA65A && store.Error is null && !File.Exists(path), "missing file uses the orange default without a write");
    foreach (var rgb in new uint[] { 0x123456, 0x000000, 0xFFFFFF })
    {
        Check(store.SetUserColor(rgb) && store.UserColorRgb == rgb && store.Error is null, "save applies the colour");
        var reloaded = new GameChatStyleStore(path);
        Check(reloaded.UserColorRgb == rgb && reloaded.Error is null, "reload restores exact RGB");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        Check(document.RootElement.EnumerateObject().Count() == 3 &&
              document.RootElement.GetProperty("schemaVersion").GetInt32() == 2 &&
              document.RootElement.GetProperty("userColorRgb").GetUInt32() == rgb &&
              document.RootElement.GetProperty("personalityColors").EnumerateObject().Count() == 0,
              "disk schema is bounded and contains appearance only");
    }
    Check(Directory.GetFiles(Path.GetDirectoryName(path)!).Length == 1, "successful saves leave no temporary files");
}

static void LegacyMigration()
{
    using var folder = new TempFolder();
    var path = Path.Combine(folder.Path, "style.json");
    var legacy = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"userColorRgb\":1122867}");
    File.WriteAllBytes(path, legacy);
    var store = new GameChatStyleStore(path);
    Check(store.UserColorRgb == 0x112233 && store.Error is null && !store.HasPersonalityColor("builtin:armo") &&
          store.GetPersonalityColor("builtin:armo") == GameChatStyle.PersonalityColorRgb("builtin:armo"),
          "schema 1 retains the user RGB and supplies automatic personality colours");
    Check(File.ReadAllBytes(path).SequenceEqual(legacy), "loading schema 1 does not rewrite it");
    Check(store.SetPersonalityColor("builtin:armo", 0x123456), "first override migrates successfully");
    var reloaded = new GameChatStyleStore(path);
    Check(reloaded.UserColorRgb == 0x112233 && reloaded.GetPersonalityColor("builtin:armo") == 0x123456 &&
          reloaded.HasPersonalityColor("builtin:armo") && reloaded.Error is null, "migration preserves both choices");
    using (var document = JsonDocument.Parse(File.ReadAllBytes(path)))
        Check(document.RootElement.GetProperty("schemaVersion").GetInt32() == 2, "migration writes schema 2");
    File.WriteAllBytes(path, legacy);
    store = new GameChatStyleStore(path);
    Check(store.SetUserColor(0x445566), "a user-colour edit also migrates schema 1");
    using var migrated = JsonDocument.Parse(File.ReadAllBytes(path));
    Check(migrated.RootElement.GetProperty("schemaVersion").GetInt32() == 2 &&
          migrated.RootElement.GetProperty("personalityColors").EnumerateObject().Count() == 0,
          "migration without overrides writes an empty mapping");
}

static void PersonalityOverrides()
{
    using var folder = new TempFolder();
    var path = Path.Combine(folder.Path, "style.json");
    var store = new GameChatStyleStore(path);
    var customId = "abcdef0123456789abcdef0123456789";
    var untouchedId = "1234567890abcdef1234567890abcdef";
    Check(store.GetPersonalityColor(customId) == GameChatStyle.PersonalityColorRgb(customId) &&
          !store.HasPersonalityColor(customId), "new personalities use stable automatic colours");
    Check(store.SetUserColor(0xAABBCC) && store.SetPersonalityColor("builtin:none", 0) &&
          store.SetPersonalityColor("builtin:armo", 0xFFFFFF) && store.SetPersonalityColor(customId, 0x123456),
          "built-in and custom IDs accept exact RGB including black and white");
    Check(store.SetUserColor(0x334455), "a later user-colour edit succeeds");
    store = new GameChatStyleStore(path);
    Check(store.UserColorRgb == 0x334455 && store.GetPersonalityColor("builtin:none") == 0 &&
          store.GetPersonalityColor("builtin:armo") == 0xFFFFFF && store.GetPersonalityColor(customId) == 0x123456 &&
          store.HasPersonalityColor("builtin:none") && store.HasPersonalityColor("builtin:armo") && store.HasPersonalityColor(customId),
          "reloading retains all overrides through a user-colour edit");
    Check(store.GetPersonalityColor(untouchedId) == GameChatStyle.PersonalityColorRgb(untouchedId) &&
          !store.HasPersonalityColor(untouchedId), "an override does not affect other IDs");
    Check(store.SetPersonalityColor(customId, null) && !store.HasPersonalityColor(customId) &&
          store.GetPersonalityColor(customId) == GameChatStyle.PersonalityColorRgb(customId), "reset returns to automatic immediately");
    store = new GameChatStyleStore(path);
    Check(!store.HasPersonalityColor(customId) && store.GetPersonalityColor(customId) == GameChatStyle.PersonalityColorRgb(customId) &&
          store.GetPersonalityColor("builtin:none") == 0 && store.GetPersonalityColor("builtin:armo") == 0xFFFFFF &&
          store.UserColorRgb == 0x334455, "reset persists while retaining all other choices");
    using var document = JsonDocument.Parse(File.ReadAllBytes(path));
    Check(document.RootElement.GetProperty("personalityColors").EnumerateObject().Count() == 2 &&
          !document.RootElement.GetProperty("personalityColors").TryGetProperty(customId, out _), "reset removes the stored override");
    Check(store.SetPersonalityColor(customId, null) && !store.HasPersonalityColor(customId), "reset is repeatable");
}

static void BadFiles()
{
    using var folder = new TempFolder();
    var path = Path.Combine(folder.Path, "style.json");
    var invalid = new[]
    {
        "not JSON", "null", "{}", "[]",
        "{\"schemaVersion\":2,\"userColorRgb\":1}",
        "{\"schemaVersion\":3,\"userColorRgb\":1,\"personalityColors\":{}}",
        "{\"schemaVersion\":1,\"userColorRgb\":1,\"personalityColors\":{}}",
        "{\"schemaVersion\":1,\"userColorRgb\":-1}",
        "{\"schemaVersion\":1,\"userColorRgb\":16777216}",
        "{\"schemaVersion\":1,\"userColorRgb\":1.5}",
        "{\"schemaVersion\":1,\"userColorRgb\":\"white\"}",
        "{\"schemaVersion\":1,\"userColorRgb\":1,\"userColorRgb\":2}",
        "{\"schemaVersion\":1,\"schemaVersion\":1,\"userColorRgb\":1}",
        "{\"schemaVersion\":1,\"userColorRgb\":1,\"extra\":true}",
        "{\"schemaVersion\":2,\"userColorRgb\":1,\"personalityColors\":null}",
        "{\"schemaVersion\":2,\"userColorRgb\":1,\"personalityColors\":[]}",
        "{\"schemaVersion\":2,\"userColorRgb\":1,\"personalityColors\":{},\"personalityColors\":{}}",
        "{\"schemaVersion\":2,\"userColorRgb\":1,\"personalityColors\":{},\"extra\":true}",
        "{\"schemaVersion\":2,\"userColorRgb\":1,\"personalityColors\":{\"builtin:armo\":1,\"builtin:armo\":2}}",
        "{\"schemaVersion\":2,\"userColorRgb\":1,\"personalityColors\":{\"builtin:armo\":1,\"builtin:\\u0061rmo\":2}}",
        new string(' ', 16 * 1024 + 1),
        JsonSerializer.Serialize(new { schemaVersion = 2, userColorRgb = 1, personalityColors = Enumerable.Range(0, 257).ToDictionary(i => i.ToString("x32"), _ => 1) }),
    }.Concat(new[] { "-1", "16777216", "1.5", "\"white\"", "null", "{}", "true" }.Select(value =>
        "{\"schemaVersion\":2,\"userColorRgb\":1,\"personalityColors\":{\"builtin:armo\":" + value + "}}"))
    .Concat(new[] { "", "builtin:unknown", "builtin:Armo", "0123456789ABCDEF0123456789ABCDEF", "0123456789abcdef0123456789abcdeg", "01234567-89ab-cdef-0123-456789abcdef" }.Select(id =>
        JsonSerializer.Serialize(new { schemaVersion = 2, userColorRgb = 1, personalityColors = new Dictionary<string, uint> { [id] = 1 } })))
    .Select(Encoding.UTF8.GetBytes).Append(new byte[] { 0xFF, 0xFE, 0x01 });
    foreach (var bytes in invalid)
    {
        File.WriteAllBytes(path, bytes);
        var store = new GameChatStyleStore(path);
        Check(store.UserColorRgb == 0xFFA65A && !store.HasPersonalityColor("builtin:armo") && store.Error is not null,
            "bad file falls back with a visible error and no partially loaded overrides");
        Check(File.ReadAllBytes(path).SequenceEqual(bytes), "bad file is preserved on load");
    }
}

static void PersonalityDrafts()
{
    using var folder = new TempFolder();
    var path = Path.Combine(folder.Path, "style.json");
    var store = new GameChatStyleStore(path);
    var draft = new PersonalityReplyColorDraft();
    var createdId = "abcdef0123456789abcdef0123456789";
    var clonedId = "1234567890abcdef1234567890abcdef";
    draft.Load(null, null);
    draft.Select(0x123456);
    Check(draft.HasEdits && draft.DisplayRgb == 0x123456 && !File.Exists(path),
        "a new personality can choose its colour before an ID or storage file exists");
    draft.SetPresetId(createdId);
    Directory.CreateDirectory(path);
    Check(!store.SetPersonalityColor(createdId, draft.Override) && draft.HasEdits && draft.DisplayRgb == 0x123456 &&
        !store.HasPersonalityColor(createdId), "failed initial colour save retains the chosen draft without a live override");
    Directory.Delete(path);
    Check(store.SetPersonalityColor(createdId, draft.Override), "retained colour retries using the created personality ID");
    draft.MarkSaved();
    Check(!draft.HasEdits && new GameChatStyleStore(path).GetPersonalityColor(createdId) == 0x123456,
        "the chosen colour persists and becomes clean only after success");

    draft.Clone();
    Check(draft.HasEdits && draft.DisplayRgb == 0x123456, "cloning carries the visible colour into the unsaved draft");
    draft.SetPresetId(clonedId);
    Check(store.SetPersonalityColor(clonedId, draft.Override), "clone colour saves independently");
    draft.MarkSaved();
    draft.UseAutomatic();
    Check(draft.HasEdits && draft.Override is null && draft.DisplayRgb == GameChatStyle.PersonalityColorRgb(clonedId) &&
        store.GetPersonalityColor(clonedId) == 0x123456, "automatic selection is a draft until Save");
    draft.Load(clonedId, store.GetPersonalityColor(clonedId));
    Check(!draft.HasEdits && draft.DisplayRgb == 0x123456, "discard restores the saved colour");

    draft.Load("builtin:armo", null);
    draft.Select(0x654321);
    Check(draft.HasEdits && store.SetPersonalityColor("builtin:armo", draft.Override), "built-in colour can save without editing instructions");
    draft.MarkSaved();
    draft.UseAutomatic();
    Check(draft.HasEdits && store.SetPersonalityColor("builtin:armo", draft.Override), "built-in automatic reset saves");
    draft.MarkSaved();
    Check(!draft.HasEdits && !new GameChatStyleStore(path).HasPersonalityColor("builtin:armo") &&
        store.GetPersonalityColor(createdId) == 0x123456 && store.GetPersonalityColor(clonedId) == 0x123456,
        "saved reset clears only its own override");
    draft.Clone();
    Check(draft.HasEdits && draft.Override == GameChatStyle.PersonalityColorRgb("builtin:armo"),
        "cloning an automatic colour preserves the source's visible shade");

    draft.Load(createdId, store.GetPersonalityColor(createdId));
    for (var i = 0; i < 10; i++)
    {
        var previous = draft.DisplayRgb;
        draft.Randomize();
        var colour = draft.DisplayRgb;
        Check(colour != previous && colour <= 0xFFFFFF && (colour >> 16) >= 144 &&
            ((colour >> 8) & 255) >= 144 && (colour & 255) >= 144,
            "each random press changes to a bright colour");
    }
    var selected = draft.DisplayRgb;
    Check(store.GetPersonalityColor(createdId) == 0x123456 && draft.HasEdits,
        "random colour stays a draft until Save");
    Check(store.SetPersonalityColor(createdId, draft.Override), "random colour saves");
    draft.MarkSaved();
    Check(!draft.HasEdits && new GameChatStyleStore(path).GetPersonalityColor(createdId) == selected,
        "random choice remains stable across reloads");
}

static void Bounds()
{
    using var folder = new TempFolder();
    var path = Path.Combine(folder.Path, "style.json");
    var colors = Enumerable.Range(0, 256).ToDictionary(i => i.ToString("x32"), i => (uint)i);
    File.WriteAllText(path, JsonSerializer.Serialize(new { schemaVersion = 2, userColorRgb = 0x123456, personalityColors = colors }));
    var store = new GameChatStyleStore(path);
    Check(store.Error is null && colors.All(pair => store.HasPersonalityColor(pair.Key) && store.GetPersonalityColor(pair.Key) == pair.Value),
        "exactly 256 overrides load");
    var original = File.ReadAllBytes(path);
    Check(!store.SetPersonalityColor("builtin:armo", 1) && !store.HasPersonalityColor("builtin:armo") &&
          File.ReadAllBytes(path).SequenceEqual(original), "a 257th override is rejected without changing disk");
    Check(store.SetPersonalityColor(0.ToString("x32"), 0xFFFFFF) && store.SetUserColor(0x334455),
        "existing choices can change at the entry limit");
    Check(store.SetPersonalityColor(1.ToString("x32"), null) && store.SetPersonalityColor("builtin:armo", 0xAABBCC),
        "reset releases space for a new override");
    var bounded = File.ReadAllText(path).PadRight(16 * 1024, ' ');
    Check(bounded.Length == 16 * 1024, "fixture reaches the file limit");
    File.WriteAllText(path, bounded, new UTF8Encoding(false));
    store = new GameChatStyleStore(path);
    Check(store.Error is null && store.UserColorRgb == 0x334455 && store.GetPersonalityColor("builtin:armo") == 0xAABBCC,
        "a valid file of exactly 16 KiB loads");
    File.AppendAllText(path, " ");
    store = new GameChatStyleStore(path);
    Check(store.Error is not null && store.UserColorRgb == GameChatStyleStore.DefaultUserColorRgb && !store.HasPersonalityColor("builtin:armo") &&
          new FileInfo(path).Length == 16 * 1024 + 1, "one byte over the limit is rejected and preserved");
}

static void FailedSave()
{
    using var folder = new TempFolder();
    var path = Path.Combine(folder.Path, "style.json");
    var store = new GameChatStyleStore(path);
    var customId = "abcdef0123456789abcdef0123456789";
    Check(store.SetUserColor(0xAABBCC) && store.SetPersonalityColor("builtin:armo", 0x334455) &&
          store.SetPersonalityColor(customId, 0x778899), "initial saves");
    var original = File.ReadAllBytes(path);
    Check(!store.SetUserColor(0x1000000) && store.UserColorRgb == 0xAABBCC && store.Error is not null &&
          File.ReadAllBytes(path).SequenceEqual(original), "out-of-range colour is rejected without changing live or saved colour");
    foreach (var invalidId in new[] { "", "builtin:unknown", "builtin:Armo", "0123456789ABCDEF0123456789ABCDEF", "0123456789abcdef0123456789abcdeg", "01234567-89ab-cdef-0123-456789abcdef", null! })
        Check(!store.SetPersonalityColor(invalidId, 1) && !store.SetPersonalityColor(invalidId, null) &&
              File.ReadAllBytes(path).SequenceEqual(original), "invalid IDs are rejected for both set and reset");
    Check(!store.SetPersonalityColor(customId, 0x1000000) && store.GetPersonalityColor(customId) == 0x778899 &&
          File.ReadAllBytes(path).SequenceEqual(original), "invalid personality RGB preserves the previous override");
    if (OperatingSystem.IsWindows())
    {
        using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Check(!store.SetUserColor(0x112233) && store.UserColorRgb == 0xAABBCC && store.Error is not null &&
              File.ReadAllBytes(path).SequenceEqual(original), "a locked-file save failure retains the previous live and saved colour");
        Check(!store.SetPersonalityColor(customId, 0x112233) && !store.SetPersonalityColor("builtin:armo", null) &&
              !store.SetPersonalityColor("builtin:none", 0xFFFFFF) && store.UserColorRgb == 0xAABBCC &&
              store.GetPersonalityColor(customId) == 0x778899 && store.GetPersonalityColor("builtin:armo") == 0x334455 &&
              store.HasPersonalityColor("builtin:armo") && !store.HasPersonalityColor("builtin:none") &&
              File.ReadAllBytes(path).SequenceEqual(original), "locked-file override, reset and insertion failures preserve every choice");
    }
    // A directory at the destination forces replacement to fail without relying on platform permissions.
    File.Delete(path);
    Directory.CreateDirectory(path);
    Check(!store.SetUserColor(0x112233) && store.UserColorRgb == 0xAABBCC && store.Error is not null,
        "storage failure retains the previous live choice and reports an error");
    Check(!store.SetPersonalityColor(customId, 0x112233) && !store.SetPersonalityColor("builtin:armo", null) &&
          store.GetPersonalityColor(customId) == 0x778899 && store.GetPersonalityColor("builtin:armo") == 0x334455,
          "storage failure retains existing overrides and reset choices");
    Check(Directory.GetFiles(folder.Path).Length == 0, "failed save cleans its temporary file");
    Directory.Delete(path);
    File.WriteAllBytes(path, original);
    Check(store.SetUserColor(0x778899) && store.Error is null && new GameChatStyleStore(path).UserColorRgb == 0x778899,
        "a later successful save clears the error and persists");
    var reloaded = new GameChatStyleStore(path);
    Check(reloaded.GetPersonalityColor(customId) == 0x778899 && reloaded.GetPersonalityColor("builtin:armo") == 0x334455 &&
          reloaded.HasPersonalityColor("builtin:armo"), "recovered user-colour save retains every override");
}

static void StablePalette()
{
    var expectedPalette = new HashSet<uint> { 0x8FD8FF, 0x9FE3B1, 0xD9B3FF, 0xFFADC9, 0xF4E49D, 0x9FE7E5, 0xBFC6FF, 0xCBE795 };
    var observed = new HashSet<uint>();
    foreach (var id in Enumerable.Range(0, 64).Select(number => number.ToString("x32")).Append("builtin:none"))
    {
        var first = GameChatStyle.PersonalityColorRgb(id);
        Check(first == GameChatStyle.PersonalityColorRgb(new string(id.ToCharArray())), "equivalent stable IDs return the same colour");
        Check(expectedPalette.Contains(first), "all results stay in the fixed palette");
        observed.Add(first);
    }
    Check(observed.SetEquals(expectedPalette), "IDs exercise every palette colour");
    foreach (var rgb in observed)
    {
        var red = (rgb >> 16) & 255;
        var green = (rgb >> 8) & 255;
        var blue = rgb & 255;
        Check(Math.Min(red, Math.Min(green, blue)) >= 0x8F && Math.Max(red, Math.Max(green, blue)) >= 0xD8,
            "all persona colours are bright pastels");
    }
    // Pin exact assignments so a later hash or palette edit cannot silently change existing presets.
    Check(GameChatStyle.PersonalityColorRgb("builtin:none") == 0xF4E49D, "None colour stays stable");
    Check(GameChatStyle.PersonalityColorRgb("builtin:armo") == 0xFFB8D0, "Armo uses her requested pastel pink outside the custom palette");
}

sealed class TempFolder : IDisposable
{
    internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FFXIVGameChatStyle-" + Guid.NewGuid().ToString("N"));
    internal TempFolder() => Directory.CreateDirectory(this.Path);
    public void Dispose() => Directory.Delete(this.Path, recursive: true);
}
