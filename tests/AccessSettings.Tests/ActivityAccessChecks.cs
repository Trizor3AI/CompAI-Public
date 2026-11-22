using CompAI;
using System.Text.Json.Nodes;

internal static class ActivityAccessChecks
{
    internal static readonly string[] Fields = ["tripleTriadDecks", "raceChocobo", "verminion", "freeCompany", "occultRecords", "yokaiCollection", "collectionTools", "mahjong"];
    private static readonly CompanionAccessSettings Prior = new(Collections: true, TripleTriad: true, Pvp: true,
        BattleChocobo: true, Trust: true, FcChest: true);
    private static readonly (string Field, Func<CompanionAccessSettings, bool, CompanionAccessSettings> Configure, Action<Plugin, bool> Set)[] Cases =
    [
        ("tripleTriadDecks", (s, v) => s with { TripleTriadDecks = v }, (p, v) => p.SetAccess(false, false, false, tripleTriadDecks: v)),
        ("raceChocobo", (s, v) => s with { RaceChocobo = v }, (p, v) => p.SetAccess(false, false, false, raceChocobo: v)),
        ("verminion", (s, v) => s with { Verminion = v }, (p, v) => p.SetAccess(false, false, false, verminion: v)),
        ("freeCompany", (s, v) => s with { FreeCompany = v }, (p, v) => p.SetAccess(false, false, false, freeCompany: v)),
        ("occultRecords", (s, v) => s with { OccultRecords = v }, (p, v) => p.SetAccess(false, false, false, occultRecords: v)),
        ("yokaiCollection", (s, v) => s with { YokaiCollection = v }, (p, v) => p.SetAccess(false, false, false, yokaiCollection: v)),
        ("collectionTools", (s, v) => s with { CollectionTools = v }, (p, v) => p.SetAccess(false, false, false, collectionTools: v)),
        ("mahjong", (s, v) => s with { Mahjong = v }, (p, v) => p.SetAccess(false, false, false, mahjong: v)),
    ];

    internal static string WithoutActivityFields(this string current)
    {
        var document = JsonNode.Parse(current)!.AsObject();
        document.Remove("selectedMacro");
        foreach (var field in Fields.Concat(RegionalProgressAccessChecks.Fields.Concat(RegionalResourceAccessChecks.Fields))) document.Remove(field);
        return document.ToJsonString();
    }

    internal static void Migration()
    {
        using var folder = new TestFolder();
        foreach (var expected in new[] { new CompanionAccessSettings(), Prior })
        {
            var store = new CompanionAccessSettingsStore(folder.File);
            Check(store.Save(expected), "seed current settings");
            var old = File.ReadAllText(folder.File).WithoutActivityFields().Replace("\"schemaVersion\":26", "\"schemaVersion\":21");
            File.WriteAllText(folder.File, old);
            store = new CompanionAccessSettingsStore(folder.File);
            Check(store.Error is null && store.Settings == expected, "schema21 preserves old grants and leaves later activity grants off");
            Check(File.ReadAllText(folder.File) == old, "loading migration does not write");
            foreach (var field in Fields)
            foreach (var value in new[] { "true", "false" })
            {
                var misplaced = old[..^1] + $",\"{field}\":{value}}}";
                File.WriteAllText(folder.File, misplaced);
                var rejected = new CompanionAccessSettingsStore(folder.File);
                Check(rejected.Error is not null && rejected.Settings == new CompanionAccessSettings(), "old schemas cannot carry new grant " + field);
            }
            File.WriteAllText(folder.File, old);
            store = new CompanionAccessSettingsStore(folder.File);
            Check(store.Save(store.Settings) && new CompanionAccessSettingsStore(folder.File).Settings == expected,
                "explicit current save does not broaden migrated access");
        }
    }

    internal static void MahjongMigration()
    {
        using var folder = new TestFolder();
        foreach (var expected in new[] { new CompanionAccessSettings(), Prior with
        {
            TripleTriadDecks = true, RaceChocobo = true, Verminion = true, FreeCompany = true,
            OccultRecords = true, YokaiCollection = true, CollectionTools = true,
        } })
        {
            var store = new CompanionAccessSettingsStore(folder.File);
            Check(store.Save(expected), "seed prior grants");
            var document = JsonNode.Parse(File.ReadAllText(folder.File))!.AsObject();
            document.Remove("mahjong"); document["schemaVersion"] = 22;
            document.Remove("selectedMacro");
            foreach (var field in RegionalProgressAccessChecks.Fields.Concat(RegionalResourceAccessChecks.Fields)) document.Remove(field);
            var legacy = document.ToJsonString();
            File.WriteAllText(folder.File, legacy);
            store = new CompanionAccessSettingsStore(folder.File);
            Check(store.Error is null && store.Settings == expected && !store.Settings.Mahjong,
                "schema22 preserves every prior choice and defaults Mahjong off");
            Check(File.ReadAllText(folder.File) == legacy, "schema22 loading never rewrites durable choices");
            foreach (var value in new[] { true, false })
            {
                document["mahjong"] = value;
                File.WriteAllText(folder.File, document.ToJsonString());
                var rejected = new CompanionAccessSettingsStore(folder.File);
                Check(rejected.Error is not null && rejected.Settings == new CompanionAccessSettings(),
                    "schema22 rejects even a false Mahjong field");
            }
            File.WriteAllText(folder.File, legacy);
            store = new CompanionAccessSettingsStore(folder.File);
            Check(store.Save(store.Settings) && new CompanionAccessSettingsStore(folder.File).Settings == expected,
                "explicit schema26 migration preserves prior choices without granting Mahjong");
        }
    }

    internal static void Validation()
    {
        using var folder = new TestFolder();
        var store = new CompanionAccessSettingsStore(folder.File);
        var all = Cases.Aggregate(Prior, (s, item) => item.Configure(s, true));
        Check(store.Save(all), "seed enabled settings");
        var valid = File.ReadAllText(folder.File);
        foreach (var field in Fields)
        {
            var marker = $"\"{field}\":true";
            var invalid = new[] { valid.Replace("," + marker, ""), valid.Replace(marker, marker + $",\"{field}\":false"),
                valid.Replace(marker, $"\"{char.ToUpperInvariant(field[0]) + field[1..]}\":true"),
                valid.Replace("\"schemaVersion\":26", "\"schemaVersion\":27") }.ToList();
            invalid.AddRange(new[] { "null", "\"true\"", "1", "{}", "[]" }.Select(value => valid.Replace(marker, $"\"{field}\":{value}")));
            foreach (var input in invalid)
            {
                File.WriteAllText(folder.File, input);
                var rejected = new CompanionAccessSettingsStore(folder.File);
                Check(rejected.Error is not null && rejected.Settings == new CompanionAccessSettings(), "malformed grant must fail closed: " + field);
                Check(File.ReadAllText(folder.File) == input, "invalid input remains untouched");
            }
        }
    }

    internal static void Transitions()
    {
        foreach (var (field, configure, set) in Cases)
        {
            using var folder = new TestFolder();
            var all = Cases.Aggregate(Prior, (s, item) => item.Configure(s, true));
            var initial = configure(all, false);
            var store = new CompanionAccessSettingsStore(folder.File);
            Check(store.Save(initial), "seed independent grants");
            var plugin = new Plugin(store);
            plugin.SetPendingQuestion(null);
            set(plugin, true);
            Check(plugin.Access == all && plugin.AccessSaveError is null && plugin.CancelCount == 0 &&
                new CompanionAccessSettingsStore(folder.File).Settings == all, "independent durable grant: " + field);
            plugin.SetAccess(false, false, false);
            Check(plugin.Access == all, "omitted new arguments preserve grants");
            plugin.SetPendingQuestion("synthetic request");
            plugin.SetBackgroundQuestion(true);
            set(plugin, false);
            Check(plugin.Access == initial && plugin.CancelCount == 1 && !plugin.AnyPendingQuestion &&
                new CompanionAccessSettingsStore(folder.File).Settings == initial, "revocation cancels foreground and background use: " + field);
            set(plugin, true);
            plugin.SetBackgroundQuestion(true);
            File.SetAttributes(folder.File, FileAttributes.ReadOnly);
            try
            {
                set(plugin, false);
                Check(plugin.AccessSaveError is not null && plugin.Access == new CompanionAccessSettings() &&
                    plugin.CancelCount == 2 && !plugin.AnyPendingQuestion, "failed save disables effective grants and cancels use: " + field);
                Check(new CompanionAccessSettingsStore(folder.File).Settings == all, "failed save preserves old durable choices");
            }
            finally { File.SetAttributes(folder.File, FileAttributes.Normal); }
        }
    }

    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
}
