using CompAI;
using System.Text.Json.Nodes;

internal static class RegionalResourceAccessChecks
{
    internal static readonly string[] Fields = ["islandMaterials", "cosmicMissions"];
    private static readonly (string Field, Func<CompanionAccessSettings, bool, CompanionAccessSettings> Configure,
        Action<Plugin, bool> Set)[] Cases =
    [
        ("islandMaterials", (s, v) => s with { IslandMaterials = v },
            (p, v) => p.SetAccess(false, false, false, islandMaterials: v)),
        ("cosmicMissions", (s, v) => s with { CosmicMissions = v },
            (p, v) => p.SetAccess(false, false, false, cosmicMissions: v)),
    ];

    internal static void Migration()
    {
        using var folder = new TestFolder();
        var constructor = typeof(CompanionAccessSettings).GetConstructors()
            .Single(value => value.GetParameters().Length > 1);
        var oldGrants = (CompanionAccessSettings)constructor.Invoke(
            Enumerable.Repeat<object>(true, constructor.GetParameters().Length).ToArray());
        oldGrants = oldGrants with { IslandMaterials = false, CosmicMissions = false, SelectedMacro = false };
        foreach (var expected in new[] { new CompanionAccessSettings(), oldGrants })
        {
            var store = new CompanionAccessSettingsStore(folder.File);
            Check(store.Save(expected), "seed old choices");
            var legacy = JsonNode.Parse(File.ReadAllText(folder.File))!.AsObject();
            legacy["schemaVersion"] = 24;
            legacy.Remove("selectedMacro");
            foreach (var field in Fields) legacy.Remove(field);
            var oldText = legacy.ToJsonString();
            File.WriteAllText(folder.File, oldText);
            store = new CompanionAccessSettingsStore(folder.File);
            Check(store.Error is null && store.Settings == expected &&
                !store.Settings.IslandMaterials && !store.Settings.CosmicMissions,
                "schema 24 preserves every prior choice without inferring Island materials or Cosmic missions from other Island/Cosmic grants");
            Check(File.ReadAllText(folder.File) == oldText, "migration must not rewrite old settings on load");
            foreach (var field in Fields)
            foreach (var value in new[] { true, false })
            {
                var misplaced = JsonNode.Parse(oldText)!.AsObject();
                misplaced[field] = value;
                var input = misplaced.ToJsonString();
                File.WriteAllText(folder.File, input);
                var rejected = new CompanionAccessSettingsStore(folder.File);
                Check(rejected.Error is not null && rejected.Settings == new CompanionAccessSettings(),
                    "older schema rejects new field even when false: " + field);
                Check(File.ReadAllText(folder.File) == input, "rejected migration input stays unchanged");
            }
            File.WriteAllText(folder.File, oldText);
            store = new CompanionAccessSettingsStore(folder.File);
            Check(store.Save(store.Settings) && new CompanionAccessSettingsStore(folder.File).Settings == expected,
                "explicit current save does not broaden migrated grants");
        }
    }

    internal static void Validation()
    {
        using var folder = new TestFolder();
        var store = new CompanionAccessSettingsStore(folder.File);
        Check(store.Save(new(Currencies: true, IslandMaterials: true, CosmicMissions: true)), "seed current fields");
        var valid = File.ReadAllText(folder.File);
        foreach (var field in Fields)
        {
            var marker = $"\"{field}\":true";
            var invalid = new[]
            {
                valid.Replace("," + marker, ""),
                valid.Replace(marker, marker + $",\"{field}\":false"),
                valid.Replace(marker, $"\"{char.ToUpperInvariant(field[0]) + field[1..]}\":true"),
                valid.Replace("\"schemaVersion\":26", "\"schemaVersion\":24"),
                valid.Replace("\"schemaVersion\":26", "\"schemaVersion\":27"),
            }.ToList();
            invalid.AddRange(new[] { "null", "\"true\"", "1", "{}", "[]" }
                .Select(value => valid.Replace(marker, $"\"{field}\":{value}")));
            foreach (var input in invalid)
            {
                Check(input != valid, "invalid fixture must actually alter saved JSON");
                File.WriteAllText(folder.File, input);
                var rejected = new CompanionAccessSettingsStore(folder.File);
                Check(rejected.Error is not null && rejected.Settings == new CompanionAccessSettings(),
                    "malformed regional resource field fails closed: " + field);
                Check(File.ReadAllText(folder.File) == input, "malformed file remains untouched");
            }
        }
    }

    internal static void Transitions()
    {
        foreach (var (field, configure, set) in Cases)
        {
            using var folder = new TestFolder();
            var all = new CompanionAccessSettings(Currencies: true, Mahjong: true, IslandProgress: true, CosmicResearch: true, IslandMaterials: true, CosmicMissions: true);
            var initial = configure(all, false);
            var store = new CompanionAccessSettingsStore(folder.File);
            Check(store.Save(initial), "seed independent permissions");
            var plugin = new Plugin(store);
            plugin.SetPendingQuestion(null);
            plugin.SeedReaderObservation(field);
            set(plugin, true);
            Check(plugin.Access == all && plugin.AccessSaveError is null && plugin.CancelCount == 0 &&
                new CompanionAccessSettingsStore(folder.File).Settings == all,
                "grant is independently durable: " + field);
            Check(plugin.RecentReaderResetCount == 1 && !plugin.HasReaderObservation && plugin.AccessEpoch == 1,
                "changed grant clears retained-reader and lookup metadata and invalidates callbacks");
            plugin.SeedReaderObservation(field);
            plugin.SetAccess(false, false, false);
            Check(plugin.Access == all && plugin.HasReaderObservation && plugin.RecentReaderResetCount == 1,
                "omitted appended arguments preserve grants without a spurious reset");
            plugin.SetPendingQuestion("synthetic foreground use");
            plugin.SetBackgroundQuestion(true);
            set(plugin, false);
            Check(plugin.Access == initial && plugin.CancelCount == 1 && !plugin.AnyPendingQuestion &&
                new CompanionAccessSettingsStore(folder.File).Settings == initial && plugin.UnrelatedReaderActions == 0,
                "revoke cancels both request paths without changing unrelated grants: " + field);
            Check(plugin.RecentReaderResetCount == 2 && !plugin.HasReaderObservation && plugin.AccessEpoch == 2,
                "revoke drops cached metadata and invalidates earlier callbacks");
            set(plugin, true);
            plugin.SetBackgroundQuestion(true);
            plugin.SeedReaderObservation(field);
            var previousBytes = File.ReadAllBytes(folder.File);
            File.SetAttributes(folder.File, FileAttributes.ReadOnly);
            try
            {
                set(plugin, false);
                Check(plugin.AccessSaveError is not null && plugin.Access == new CompanionAccessSettings() &&
                    plugin.CancelCount == 2 && !plugin.AnyPendingQuestion && !plugin.HasReaderObservation &&
                    plugin.RecentReaderResetCount == 4 && plugin.AccessEpoch == 4,
                    "failed save clears effective grants and retained state and cancels use");
                Check(previousBytes.SequenceEqual(File.ReadAllBytes(folder.File)) &&
                    new CompanionAccessSettingsStore(folder.File).Settings == all,
                    "failed save retains exact durable prior choices");
            }
            finally { File.SetAttributes(folder.File, FileAttributes.Normal); }
            plugin.SetAccess(false, false, false);
            Check(plugin.AccessSaveError is null && plugin.Access == new CompanionAccessSettings() &&
                new CompanionAccessSettingsStore(folder.File).Settings == new CompanionAccessSettings(),
                "retry from fail-closed effective settings never revives durable old grants");
        }
    }

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new Exception(reason);
    }
}
