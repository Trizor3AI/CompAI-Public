using System.Text.Json;
using CompAI;

internal static class ProgressionAccessChecks
{
    internal static void Run()
    {
        using var folder = new TestFolder();
        var prior = new CompanionAccessSettings(Collections: true, Orchestrion: true, Duties: true);
        var store = new CompanionAccessSettingsStore(folder.File);
        Check(store.Save(prior), "seed current schema");
        var modern = File.ReadAllText(folder.File);
        var legacy = modern.WithoutActivityFields().Replace("\"schemaVersion\":26", "\"schemaVersion\":14").Replace(",\"tripleTriad\":false", "").Replace(",\"battleChocobo\":false", "").Replace(",\"pvp\":false", "").Replace(",\"shops\":false", "").Replace(",\"glamour\":false", "").Replace(",\"huntingLog\":false", "").Replace(",\"sightseeingLog\":false", "").Replace(",\"challengeLog\":false", "").Replace(",\"variantNotebook\":false", "");
        foreach (var field in new[] { "achievements", "aetherCurrents", "sharedFates", "trust", "blueMage", "gatheringLog", "bestiary", "craftingLog", "fishingLog" })
            legacy = legacy.Replace(",\"" + field + "\":false", "");
        File.WriteAllText(folder.File, legacy);
        store = new CompanionAccessSettingsStore(folder.File);
        Check(store.Error is null && store.Settings == prior &&
            ProgressionAccess.Categories(store.Settings).Length == 0, "schema14 never grants progression");
        Check(File.ReadAllText(folder.File) == legacy, "migration must not rewrite");

        var cases = new (string Field, string Category, Action<Plugin, bool> Set)[]
        {
            ("achievements", "achievements", (p, v) => p.SetAccess(false, false, false, achievements: v)),
            ("aetherCurrents", "aether_currents", (p, v) => p.SetAccess(false, false, false, aetherCurrents: v)),
            ("sharedFates", "shared_fates", (p, v) => p.SetAccess(false, false, false, sharedFates: v)),
            ("trust", "trust", (p, v) => p.SetAccess(false, false, false, trust: v)),
        };
        var plugin = new Plugin(store);
        foreach (var (field, category, set) in cases)
        {
            set(plugin, true);
            Check(plugin.AccessSaveError is null && ProgressionAccess.Categories(plugin.Access).SequenceEqual(category == "achievements" ? new[] { category, "achievement_progress" } : new[] { category }),
                "only explicitly enabled category is granted");
            foreach (var candidate in cases.Select(row => row.Category).Append("achievement_progress").Append("mount_speed").Append(null!))
                Check(ProgressionAccess.Allows(plugin.Access, candidate) == (candidate == category || category == "achievements" && candidate == "achievement_progress"), "unknown/other category denied");
            var saved = File.ReadAllText(folder.File);
            Check(new CompanionAccessSettingsStore(folder.File).Settings == plugin.Access, "grant survives reload");
            var marker = "\"" + field + "\":true";
            foreach (var bad in new[]
            {
                saved.Replace("," + marker, ""),
                saved.Replace(marker, marker + ",\"" + field + "\":false"),
                saved.Replace(marker, "\"" + field + "\":null"),
                saved.WithoutActivityFields().Replace("\"schemaVersion\":26", "\"schemaVersion\":14").Replace(",\"tripleTriad\":false", "").Replace(",\"battleChocobo\":false", "").Replace(",\"pvp\":false", "").Replace(",\"shops\":false", "").Replace(",\"glamour\":false", "").Replace(",\"huntingLog\":false", "").Replace(",\"sightseeingLog\":false", "").Replace(",\"challengeLog\":false", "").Replace(",\"variantNotebook\":false", ""),
            })
            {
                File.WriteAllText(folder.File, bad);
                var denied = new CompanionAccessSettingsStore(folder.File);
                Check(denied.Error is not null && denied.Settings == new CompanionAccessSettings(), "invalid grant fails closed");
            }
            File.WriteAllText(folder.File, saved);
            plugin.SetBackgroundQuestion(true);
            var previousCancel = plugin.CancelCount;
            set(plugin, false);
            Check(plugin.CancelCount == previousCancel + 1 && !plugin.AnyPendingQuestion, "revocation cancels pending questions");
            Check(plugin.Access == prior && ProgressionAccess.Categories(plugin.Access).Length == 0, "revocation preserves unrelated access");
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
