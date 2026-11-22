using CompAI;
using System.Text.Json;

var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
var folder = Path.GetFullPath(Path.Combine(temporaryRoot, "compai-shortcuts-" + Guid.NewGuid().ToString("N")));
if (!folder.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture path escaped temporary root");
Directory.CreateDirectory(folder);
try
{
    void Check(bool value, string message) { if (!value) throw new Exception(message); }
    var path = Path.Combine(folder, "personalities.json");
    var legacy = "{\"SchemaVersion\":1,\"DefaultId\":\"builtin:armo\",\"Presets\":[]}";
    File.WriteAllText(path, legacy);
    var store = new PersonalityPresetStore(path);
    Check(!store.CommandsEnabled && store.Default == PersonalityPresets.Armo && File.ReadAllText(path) == legacy,
        "loading legacy settings must not enable commands or rewrite the file");
    Check(store.SetCommandsEnabled(true), "enable commands");
    using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
        Check(doc.RootElement.GetProperty("SchemaVersion").GetInt32() == 3 &&
              doc.RootElement.GetProperty("CommandsEnabled").GetBoolean(), "persist current-schema toggle");
    var custom = store.Save(null, "New style", "short")!;
    Check(custom is not null && store.SetDefault(custom.Id) && store.Delete(custom.Id) &&
          new PersonalityPresetStore(path).CommandsEnabled, "library mutations must preserve the command choice");
    var before = File.ReadAllBytes(path);
    using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        Check(!store.SetCommandsEnabled(false) && store.CommandsEnabled &&
              File.ReadAllBytes(path).SequenceEqual(before), "failed toggle write retains previous state and disk");
    Check(store.SetCommandsEnabled(false) && !new PersonalityPresetStore(path).CommandsEnabled,
        "disable persists after write recovers");
    foreach (var invalid in new[] {
        "{\"SchemaVersion\":2,\"DefaultId\":\"builtin:armo\",\"Presets\":[]}",
        "{\"SchemaVersion\":2,\"DefaultId\":\"builtin:armo\",\"Presets\":[],\"CommandsEnabled\":\"true\"}",
        "{\"SchemaVersion\":1,\"DefaultId\":\"builtin:armo\",\"Presets\":[],\"CommandsEnabled\":true}",
        "{\"SchemaVersion\":2,\"DefaultId\":\"builtin:armo\",\"Presets\":[],\"CommandsEnabled\":true,\"CommandsEnabled\":false}" })
    {
        File.WriteAllText(path, invalid);
        var broken = new PersonalityPresetStore(path);
        Check(!broken.CommandsEnabled && broken.Error is not null && !broken.SetCommandsEnabled(true) &&
              File.ReadAllText(path) == invalid, "malformed settings must not register or replace existing data");
    }
    Console.WriteLine("PASS shortcut opt-in migration, persistence and failed-write recovery");

    PersonalityPreset Preset(string name) => new(Guid.NewGuid().ToString("N"), name, "");
    Check(PresetCommands.FromName("Știință") == "/stiinta" && PresetCommands.FromName("Armo copy") == "/armocopy" &&
          PresetCommands.FromName("Guide-2") == "/guide2", "predictable names including Romanian accents");
    Check(new[] { "a", "123", "say/hi", "Emote!", "😺", new string('a', 33) }.All(n => PresetCommands.FromName(n) is null),
        "unsupported names must not produce partial or surprising shortcuts");
    var presets = new[] { PersonalityPresets.Armo, PersonalityPresets.None, Preset("Guide 2"), Preset("Guide-2"),
        Preset("Teleport"), Preset("Otherplugin"), Preset("CompAI"), Preset("Hello") };
    var plans = PresetCommands.Plan(presets, new[] { "/TELEPORT" }, new[] { "/OtherPlugin" });
    Check(plans.Single(p => p.PresetId == PersonalityPresets.Armo.Id).UnavailableReason is null &&
          plans.Count(p => p.UnavailableReason is null) == 2, "only unambiguous, unreserved names may register");
    Check(plans.Where(p => p.Command == "/guide2").All(p => p.UnavailableReason!.Contains("multiple")),
        "both normalized duplicates must be disabled, irrespective of library order");
    Check(PresetCommands.Plan(presets.Reverse(), new[] { "/TELEPORT" }, new[] { "/OtherPlugin" })
        .OrderBy(p => p.PresetId).SequenceEqual(plans.OrderBy(p => p.PresetId)), "reordering cannot choose a different duplicate winner");
    Console.WriteLine("PASS shortcut naming and game/plugin/duplicate collision policy");
    return 0;
}
finally { Directory.Delete(folder, true); }
