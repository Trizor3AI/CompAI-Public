using CompAI;
using System.Text.Json.Nodes;

internal static class ReaderBatchAccessChecks
{
    private static readonly string[] Fields = ["tripleTriad", "battleChocobo", "pvp"];
    private static readonly CompanionAccessSettings Prior = new(Collections: true, Armoury: true, Trust: true,
        Duties: true, Shops: true, ChallengeLog: true, VariantNotebook: true);
    private static readonly (string Field, string Category, Func<CompanionAccessSettings, bool, CompanionAccessSettings> Configure,
        Action<Plugin, bool> Set)[] Cases =
    [
        ("tripleTriad", "triple_triad", (s, value) => s with { TripleTriad = value },
            (p, value) => p.SetAccess(false, false, false, tripleTriad: value)),
        ("battleChocobo", "battle_chocobo", (s, value) => s with { BattleChocobo = value },
            (p, value) => p.SetAccess(false, false, false, battleChocobo: value)),
        ("pvp", "pvp", (s, value) => s with { Pvp = value },
            (p, value) => p.SetAccess(false, false, false, pvp: value)),
    ];

    internal static void Migration()
    {
        using var folder = new TestFolder();
        foreach (var expected in new[] { new CompanionAccessSettings(), Prior })
        {
            var store = new CompanionAccessSettingsStore(folder.File);
            Check(store.Save(expected), "seed current settings");
            var legacy = Legacy20(File.ReadAllText(folder.File));
            File.WriteAllText(folder.File, legacy);
            store = new CompanionAccessSettingsStore(folder.File);
            Check(store.Error is null && store.Settings == expected && !store.Settings.TripleTriad &&
                !store.Settings.BattleChocobo && !store.Settings.Pvp, "schema20 preserves choices with all three new grants off");
            Check(File.ReadAllText(folder.File) == legacy, "loading migration must not rewrite the original");
            Check(!CollectionAccess.Allows(store.Settings, "triple_triad") &&
                !ProgressionAccess.Allows(store.Settings, "battle_chocobo") && !ProgressionAccess.Allows(store.Settings, "pvp"),
                "general collections and progression grants cannot imply any new reader");
            Check(store.Save(store.Settings) && new CompanionAccessSettingsStore(folder.File).Settings == expected,
                "explicit save migrates durably without broadening access");
        }
    }

    internal static void Validation()
    {
        using var folder = new TestFolder();
        var store = new CompanionAccessSettingsStore(folder.File);
        Check(store.Save(Prior with { TripleTriad = true, BattleChocobo = true, Pvp = true }), "seed enabled new permissions");
        var valid = File.ReadAllText(folder.File);
        var legacy = Legacy20(valid);
        foreach (var field in Fields)
        {
            var marker = $"\"{field}\":true";
            var invalid = new List<string>
            {
                valid.Replace("," + marker, ""),
                valid.Replace(marker, marker + $",\"{field}\":false"),
                valid.Replace(marker, $"\"{char.ToUpperInvariant(field[0]) + field[1..]}\":true"),
                legacy[..^1] + $",\"{field}\":true}}",
                legacy[..^1] + $",\"{field}\":false}}",
                valid.Replace("\"schemaVersion\":26", "\"schemaVersion\":27"),
            };
            foreach (var value in new[] { "null", "\"true\"", "1", "{}", "[]" })
                invalid.Add(valid.Replace(marker, $"\"{field}\":{value}"));
            foreach (var contents in invalid)
            {
                File.WriteAllText(folder.File, contents);
                var rejected = new CompanionAccessSettingsStore(folder.File);
                Check(rejected.Error is not null && rejected.Settings == new CompanionAccessSettings(),
                    field + " missing malformed duplicate old-schema or future-schema field must fail closed");
                Check(File.ReadAllText(folder.File) == contents, "rejected input must stay untouched");
            }
        }
    }

    internal static void Transitions()
    {
        foreach (var (field, category, configure, set) in Cases)
        {
            using var folder = new TestFolder();
            var all = Prior with { TripleTriad = true, BattleChocobo = true, Pvp = true };
            var initial = configure(all, false);
            var store = new CompanionAccessSettingsStore(folder.File);
            Check(store.Save(initial), "seed independent grants");
            var plugin = new Plugin(store);
            plugin.SetPendingQuestion(null);
            set(plugin, true);
            Check(plugin.AccessSaveError is null && plugin.Access == all && plugin.CancelCount == 0 &&
                new CompanionAccessSettingsStore(folder.File).Settings == all, field + " grant must save independently");
            var oldCategories = CollectionAccess.Categories(initial).Concat(ProgressionAccess.Categories(initial));
            var newCategories = CollectionAccess.Categories(all).Concat(ProgressionAccess.Categories(all));
            Check(newCategories.Except(oldCategories).SequenceEqual([category]), "only the exact new category is exposed");
            plugin.SetAccess(false, false, false);
            Check(plugin.Access == all, "older callers preserve explicit grants when new arguments are omitted");
            plugin.SetPendingQuestion("foreground reader question");
            plugin.SetBackgroundQuestion(true);
            set(plugin, false);
            Check(plugin.Access == initial && plugin.CancelCount == 1 && !plugin.AnyPendingQuestion &&
                plugin.UnrelatedReaderActions == 0 && new CompanionAccessSettingsStore(folder.File).Settings == initial,
                field + " revocation cancels all pending answers and preserves independent grants");
            set(plugin, true);
            set(plugin, false);
            Check(plugin.CancelCount == 1, "idle revoke does not invent a cancellation");
            set(plugin, true);
            plugin.SetBackgroundQuestion(true);
            File.SetAttributes(folder.File, FileAttributes.ReadOnly);
            try
            {
                set(plugin, false);
                Check(plugin.AccessSaveError is not null && plugin.Access == new CompanionAccessSettings() &&
                    plugin.CancelCount == 2 && !plugin.AnyPendingQuestion, "failed durable save disables effective access and cancels use");
                Check(new CompanionAccessSettingsStore(folder.File).Settings == all, "failed replacement preserves previous durable choices");
            }
            finally { File.SetAttributes(folder.File, FileAttributes.Normal); }
        }
    }

    private static string Legacy20(string current)
    {
        var document = JsonNode.Parse(current)!.AsObject();
        document["schemaVersion"] = 20;
        document.Remove("selectedMacro");
        foreach (var field in Fields.Concat(ActivityAccessChecks.Fields).Concat(RegionalProgressAccessChecks.Fields.Concat(RegionalResourceAccessChecks.Fields))) document.Remove(field);
        return document.ToJsonString();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
