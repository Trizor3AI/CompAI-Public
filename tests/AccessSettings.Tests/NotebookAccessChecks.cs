using CompAI;

internal static class NotebookAccessChecks
{
    internal static void Run()
    {
        using var folder = new TestFolder();
        var prior = new CompanionAccessSettings(GatheringLog: true, Bestiary: true, Collections: true);
        var store = new CompanionAccessSettingsStore(folder.File);
        Check(store.Save(prior), "seed current schema");
        var legacy = File.ReadAllText(folder.File).WithoutActivityFields().Replace("\"schemaVersion\":26", "\"schemaVersion\":17").Replace(",\"tripleTriad\":false", "").Replace(",\"battleChocobo\":false", "").Replace(",\"pvp\":false", "").Replace(",\"shops\":false", "").Replace(",\"glamour\":false", "").Replace(",\"huntingLog\":false", "").Replace(",\"sightseeingLog\":false", "").Replace(",\"challengeLog\":false", "").Replace(",\"variantNotebook\":false", "")
            .Replace(",\"craftingLog\":false", "").Replace(",\"fishingLog\":false", "");
        File.WriteAllText(folder.File, legacy);
        store = new CompanionAccessSettingsStore(folder.File);
        Check(store.Error is null && store.Settings == prior && !store.Settings.CraftingLog && !store.Settings.FishingLog,
            "old grants cannot enable crafting or fishing");
        Check(File.ReadAllText(folder.File) == legacy, "migration does not rewrite");
        var plugin = new Plugin(store);
        foreach (var (field, enabled, set) in new (string, CompanionAccessSettings, Action<bool>)[]
        {
            ("craftingLog", prior with { CraftingLog = true }, v => plugin.SetAccess(false, false, false, craftingLog: v)),
            ("fishingLog", prior with { FishingLog = true }, v => plugin.SetAccess(false, false, false, fishingLog: v)),
        })
        {
            set(true);
            Check(plugin.AccessSaveError is null && plugin.Access == enabled, "independent grant");
            Check(new CompanionAccessSettingsStore(folder.File).Settings == enabled, "grant survives reload");
            Check(ProgressionAccess.Allows(enabled, "fishing_log") == enabled.FishingLog &&
                ProgressionAccess.Allows(enabled, "spearfishing_log") == enabled.FishingLog,
                "fishing grant includes both distinct log categories");
            Check(ProgressionAccess.Categories(enabled).SequenceEqual(enabled.FishingLog ?
                new[] { "gathering_log", "fishing_log", "spearfishing_log" } : new[] { "gathering_log" }),
                "crafting grant cannot broaden progression");
            var saved = File.ReadAllText(folder.File);
            var marker = "\"" + field + "\":true";
            foreach (var invalid in new[]
            {
                saved.Replace("," + marker, ""),
                saved.Replace(marker, marker + ",\"" + field + "\":false"),
                saved.Replace(marker, "\"" + field + "\":null"),
                saved.WithoutActivityFields().Replace("\"schemaVersion\":26", "\"schemaVersion\":17").Replace(",\"tripleTriad\":false", "").Replace(",\"battleChocobo\":false", "").Replace(",\"pvp\":false", "").Replace(",\"shops\":false", "").Replace(",\"glamour\":false", "").Replace(",\"huntingLog\":false", "").Replace(",\"sightseeingLog\":false", "").Replace(",\"challengeLog\":false", "").Replace(",\"variantNotebook\":false", ""),
            })
            {
                File.WriteAllText(folder.File, invalid);
                var denied = new CompanionAccessSettingsStore(folder.File);
                Check(denied.Error is not null && denied.Settings == new CompanionAccessSettings(), "invalid grant fails closed");
            }
            File.WriteAllText(folder.File, saved);
            plugin.SetBackgroundQuestion(true);
            var cancelled = plugin.CancelCount;
            set(false);
            Check(plugin.CancelCount == cancelled + 1 && !plugin.AnyPendingQuestion && plugin.Access == prior,
                "revocation cancels private questions without changing other grants");
            Check(new CompanionAccessSettingsStore(folder.File).Settings == prior, "revocation persists");
            set(true);
            plugin.SetBackgroundQuestion(true);
            cancelled = plugin.CancelCount;
            File.SetAttributes(folder.File, FileAttributes.ReadOnly);
            try
            {
                set(false);
                Check(plugin.AccessSaveError is not null && plugin.Access == new CompanionAccessSettings() &&
                    plugin.CancelCount == cancelled + 1 && !plugin.AnyPendingQuestion, "failed save revokes effective access");
            }
            finally { File.SetAttributes(folder.File, FileAttributes.Normal); }
            Check(store.Save(prior), "restore unrelated grants after failure");
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
