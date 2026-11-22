using CompAI;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class SelectedMacroAccessChecks
{
    internal static void Migration()
    {
        using var folder = new TestFolder();
        var introduced = new (string Name, int Version)[]
        {
            ("inventory", 1), ("quests", 1), ("webSearch", 1), ("chatHistory", 2), ("gameChatReplies", 3),
            ("characterContext", 4), ("npcDialogue", 4), ("location", 5), ("spoilersAllowed", 6),
            ("currencies", 7), ("duties", 8), ("gcSupply", 9), ("targetInfo", 10),
            ("retainers", 11), ("saddlebag", 11), ("housing", 11), ("fcChest", 11), ("armoury", 12),
            ("collections", 13), ("fashionAccessories", 14), ("facewear", 14), ("emotes", 14), ("orchestrion", 14),
            ("achievements", 15), ("aetherCurrents", 15), ("sharedFates", 15), ("trust", 15),
            ("blueMage", 16), ("gatheringLog", 16), ("bestiary", 17), ("craftingLog", 18), ("fishingLog", 18),
            ("variantNotebook", 19), ("shops", 20), ("glamour", 20), ("huntingLog", 20), ("sightseeingLog", 20),
            ("challengeLog", 20), ("tripleTriad", 21), ("battleChocobo", 21), ("pvp", 21),
            ("tripleTriadDecks", 22), ("raceChocobo", 22), ("verminion", 22), ("freeCompany", 22),
            ("occultRecords", 22), ("yokaiCollection", 22), ("collectionTools", 22), ("mahjong", 23),
            ("islandProgress", 24), ("cosmicResearch", 24), ("islandMaterials", 25), ("cosmicMissions", 25),
        };
        for (var version = 1; version <= 25; ++version)
        foreach (var enabled in new[] { false, true })
        {
            var legacy = new Dictionary<string, object> { ["schemaVersion"] = version };
            foreach (var field in introduced.Where(field => field.Version <= version)) legacy[field.Name] = enabled;
            var original = JsonSerializer.Serialize(legacy);
            File.WriteAllText(folder.File, original);
            var store = new CompanionAccessSettingsStore(folder.File);
            Check(store.Error is null && !store.Settings.SelectedMacro,
                $"schema {version} loads with selected macro off even when earlier grants are enabled");
            foreach (var property in typeof(CompanionAccessSettings).GetProperties())
            {
                var field = char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
                var expected = enabled && (legacy.ContainsKey(field) || field == "location" && version == 4);
                Check((bool)property.GetValue(store.Settings)! == expected,
                    $"schema {version} preserves prior choice {field}");
            }
            Check(File.ReadAllText(folder.File) == original, "loading never rewrites prior choices");
            var migrated = store.Settings;
            Check(store.Save(migrated) && new CompanionAccessSettingsStore(folder.File).Settings == migrated,
                "explicit current save preserves all migrated choices");
            foreach (var value in new[] { false, true })
            {
                legacy["selectedMacro"] = value;
                var misplaced = JsonSerializer.Serialize(legacy);
                File.WriteAllText(folder.File, misplaced);
                var rejected = new CompanionAccessSettingsStore(folder.File);
                Check(rejected.Error is not null && rejected.Settings == new CompanionAccessSettings(),
                    $"schema {version} rejects the selected macro field even when false");
                Check(File.ReadAllText(folder.File) == misplaced, "rejected file remains unchanged");
            }
        }
    }

    internal static void Validation()
    {
        using var folder = new TestFolder();
        var store = new CompanionAccessSettingsStore(folder.File);
        foreach (var enabled in new[] { false, true })
        {
            var expected = new CompanionAccessSettings(CharacterContext: true, CosmicMissions: true, SelectedMacro: enabled);
            Check(store.Save(expected), "save explicit selected macro choice");
            var valid = File.ReadAllText(folder.File);
            var document = JsonNode.Parse(valid)!.AsObject();
            Check(document["schemaVersion"]!.GetValue<int>() == 26 && document.Count == 55 &&
                document["selectedMacro"]!.GetValue<bool>() == enabled &&
                new CompanionAccessSettingsStore(folder.File).Settings == expected, "exact schema26 round trip");
            var marker = "\"selectedMacro\":" + (enabled ? "true" : "false");
            var invalid = new[]
            {
                valid.Replace("," + marker, ""),
                valid.Replace(marker, marker + ",\"selectedMacro\":false"),
                valid.Replace("\"selectedMacro\"", "\"SelectedMacro\""),
                valid.Replace("\"schemaVersion\":26", "\"schemaVersion\":25"),
                valid.Replace("\"schemaVersion\":26", "\"schemaVersion\":27"),
            }.Concat(new[] { "null", "1", "\"true\"", "{}", "[]" }
                .Select(value => valid.Replace(marker, "\"selectedMacro\":" + value)));
            foreach (var input in invalid)
            {
                Check(input != valid, "invalid fixture actually changes saved JSON");
                File.WriteAllText(folder.File, input);
                var rejected = new CompanionAccessSettingsStore(folder.File);
                Check(rejected.Error is not null && rejected.Settings == new CompanionAccessSettings(),
                    "missing, duplicate, misplaced, unknown-case and non-boolean grants fail closed");
                Check(File.ReadAllText(folder.File) == input, "invalid settings stay untouched");
            }
        }
    }

    internal static void Transitions()
    {
        using var folder = new TestFolder();
        var initial = new CompanionAccessSettings(CharacterContext: true, Currencies: true, FreeCompany: true,
            IslandMaterials: true, CosmicMissions: true);
        var enabled = initial with { SelectedMacro = true };
        var store = new CompanionAccessSettingsStore(folder.File);
        Check(store.Save(initial), "seed independent prior grants");
        var plugin = new Plugin(store);
        plugin.SetPendingQuestion(null);
        plugin.SeedReaderObservation("selected_macro");
        plugin.SetAccess(false, false, false, selectedMacro: true);
        Check(plugin.Access == enabled && plugin.AccessSaveError is null && plugin.CancelCount == 0 &&
            new CompanionAccessSettingsStore(folder.File).Settings == enabled && plugin.UnrelatedReaderActions == 0,
            "grant is durable without unrelated capture or permission changes");
        Check(plugin.AccessEpoch == 1 && plugin.RecentReaderResetCount == 1 && !plugin.HasReaderObservation,
            "grant invalidates old callbacks and retained metadata");
        plugin.SeedReaderObservation("selected_macro");
        plugin.SetAccess(false, false, false);
        Check(plugin.Access == enabled && plugin.AccessEpoch == 1 && plugin.HasReaderObservation,
            "omitting the appended argument preserves the grant without a reset");
        plugin.SetPendingQuestion("synthetic foreground request");
        plugin.SetBackgroundQuestion(true);
        plugin.SetAccess(false, false, false, selectedMacro: false);
        Check(plugin.Access == initial && new CompanionAccessSettingsStore(folder.File).Settings == initial &&
            plugin.CancelCount == 1 && !plugin.AnyPendingQuestion && !plugin.HasReaderObservation &&
            plugin.RecentReaderResetCount == 2 && plugin.AccessEpoch == 2 && plugin.UnrelatedReaderActions == 0,
            "revoke preserves unrelated grants and cancels both request paths while clearing retained state");
        plugin.SetAccess(false, false, false, selectedMacro: true);
        plugin.SetBackgroundQuestion(true);
        plugin.SeedReaderObservation("selected_macro");
        var previousBytes = File.ReadAllBytes(folder.File);
        File.SetAttributes(folder.File, FileAttributes.ReadOnly);
        try
        {
            plugin.SetAccess(false, false, false, selectedMacro: false);
            Check(plugin.AccessSaveError is not null && plugin.Access == new CompanionAccessSettings() &&
                plugin.CancelCount == 2 && !plugin.AnyPendingQuestion && !plugin.HasReaderObservation &&
                plugin.RecentReaderResetCount == 4 && plugin.AccessEpoch == 4,
                "failed save disables effective grants, clears retained state and cancels use");
            Check(previousBytes.SequenceEqual(File.ReadAllBytes(folder.File)) &&
                new CompanionAccessSettingsStore(folder.File).Settings == enabled, "failed save preserves exact durable choices");
        }
        finally { File.SetAttributes(folder.File, FileAttributes.Normal); }
        plugin.SetAccess(false, false, false);
        Check(plugin.AccessSaveError is null && plugin.Access == new CompanionAccessSettings() &&
            new CompanionAccessSettingsStore(folder.File).Settings == new CompanionAccessSettings(),
            "explicit retry from fail-closed state does not resurrect old grants");
    }

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }
}
