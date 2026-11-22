using CompAI;

internal static class BookAccessChecks
{
    internal static void Run()
    {
        using var folder = new TestFolder();
        var prior = new CompanionAccessSettings(Collections: true, Achievements: true, Trust: true);
        var store = new CompanionAccessSettingsStore(folder.File);
        Check(store.Save(prior), "seed modern settings");
        var legacy = File.ReadAllText(folder.File).WithoutActivityFields().Replace("\"schemaVersion\":26", "\"schemaVersion\":15").Replace(",\"tripleTriad\":false", "").Replace(",\"battleChocobo\":false", "").Replace(",\"pvp\":false", "").Replace(",\"shops\":false", "").Replace(",\"glamour\":false", "").Replace(",\"huntingLog\":false", "").Replace(",\"sightseeingLog\":false", "").Replace(",\"challengeLog\":false", "").Replace(",\"variantNotebook\":false", "")
            .Replace(",\"blueMage\":false", "").Replace(",\"gatheringLog\":false", "").Replace(",\"bestiary\":false", "").Replace(",\"craftingLog\":false", "").Replace(",\"fishingLog\":false", "");
        File.WriteAllText(folder.File, legacy);
        store = new CompanionAccessSettingsStore(folder.File);
        Check(store.Error is null && store.Settings == prior && !store.Settings.BlueMage && !store.Settings.GatheringLog, "schema15 does not grant spellbook access");
        Check(File.ReadAllText(folder.File) == legacy, "migration must not rewrite");
        Check(!CollectionAccess.Allows(store.Settings, "blue_mage_spells"), "mount/minion permission does not grant spells");
        var plugin = new Plugin(store);
        plugin.SetAccess(false, false, false, blueMage: true);
        Check(plugin.AccessSaveError is null && plugin.Access == prior with { BlueMage = true }, "independent grant");
        Check(CollectionAccess.Categories(plugin.Access).SequenceEqual(new[] { "mounts", "minions", "blue_mage_spells" }),
            "category advertised only by its own permission");
        Check(new CompanionAccessSettingsStore(folder.File).Settings == plugin.Access, "grant persists");
        var valid = File.ReadAllText(folder.File);
        foreach (var bad in new[]
        {
            valid.Replace(",\"blueMage\":true", ""),
            valid.Replace("\"blueMage\":true", "\"blueMage\":true,\"blueMage\":false"),
            valid.Replace("\"blueMage\":true", "\"blueMage\":null"),
            valid.WithoutActivityFields().Replace("\"schemaVersion\":26", "\"schemaVersion\":15").Replace(",\"tripleTriad\":false", "").Replace(",\"battleChocobo\":false", "").Replace(",\"pvp\":false", "").Replace(",\"shops\":false", "").Replace(",\"glamour\":false", "").Replace(",\"huntingLog\":false", "").Replace(",\"sightseeingLog\":false", "").Replace(",\"challengeLog\":false", "").Replace(",\"variantNotebook\":false", ""),
        })
        {
            File.WriteAllText(folder.File, bad);
            var denied = new CompanionAccessSettingsStore(folder.File);
            Check(denied.Error is not null && denied.Settings == new CompanionAccessSettings(), "malformed/old-schema grants fail closed");
        }
        File.WriteAllText(folder.File, valid);
        plugin.SetBackgroundQuestion(true);
        var cancelled = plugin.CancelCount;
        plugin.SetAccess(false, false, false, blueMage: false);
        Check(plugin.CancelCount == cancelled + 1 && !plugin.AnyPendingQuestion && plugin.Access == prior,
            "revocation cancels private questions and preserves unrelated settings");
        Check(new CompanionAccessSettingsStore(folder.File).Settings == prior, "revocation persists");
        plugin.SetAccess(false, false, false, gatheringLog: true);
        Check(plugin.AccessSaveError is null && !plugin.Access.BlueMage && plugin.Access.GatheringLog &&
            ProgressionAccess.Categories(plugin.Access).SequenceEqual(new[] { "achievements", "achievement_progress", "trust", "gathering_log" }), "log access is independent of spells and other readers");
        Check(new CompanionAccessSettingsStore(folder.File).Settings == prior with { GatheringLog = true }, "log grant persists");
        var logValid = File.ReadAllText(folder.File);
        foreach (var bad in new[]
        {
            logValid.Replace(",\"gatheringLog\":true", ""),
            logValid.Replace("\"gatheringLog\":true", "\"gatheringLog\":true,\"gatheringLog\":false"),
            logValid.Replace("\"gatheringLog\":true", "\"gatheringLog\":null"),
            logValid.WithoutActivityFields().Replace("\"schemaVersion\":26", "\"schemaVersion\":15").Replace(",\"tripleTriad\":false", "").Replace(",\"battleChocobo\":false", "").Replace(",\"pvp\":false", "").Replace(",\"shops\":false", "").Replace(",\"glamour\":false", "").Replace(",\"huntingLog\":false", "").Replace(",\"sightseeingLog\":false", "").Replace(",\"challengeLog\":false", "").Replace(",\"variantNotebook\":false", ""),
        })
        {
            File.WriteAllText(folder.File, bad);
            var denied = new CompanionAccessSettingsStore(folder.File);
            Check(denied.Error is not null && denied.Settings == new CompanionAccessSettings(), "invalid log grants fail closed");
        }
        File.WriteAllText(folder.File, logValid);
        plugin.SetBackgroundQuestion(true);
        cancelled = plugin.CancelCount;
        plugin.SetAccess(false, false, false, gatheringLog: false);
        Check(plugin.CancelCount == cancelled + 1 && !plugin.AnyPendingQuestion && plugin.Access == prior, "log revocation cancels requests");
        Check(new CompanionAccessSettingsStore(folder.File).Settings == prior, "log revocation persists");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
