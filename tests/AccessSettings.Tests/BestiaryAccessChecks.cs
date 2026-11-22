using CompAI;

internal static class BestiaryAccessChecks
{
    internal static void Run()
    {
        using var folder = new TestFolder();
        var prior = new CompanionAccessSettings(Collections: true, BlueMage: true, GatheringLog: true, Achievements: true);
        var store = new CompanionAccessSettingsStore(folder.File);
        Check(store.Save(prior), "seed modern settings");
        var modern = File.ReadAllText(folder.File);
        var legacy = modern.WithoutActivityFields().Replace("\"schemaVersion\":26", "\"schemaVersion\":16").Replace(",\"tripleTriad\":false", "").Replace(",\"battleChocobo\":false", "").Replace(",\"pvp\":false", "").Replace(",\"shops\":false", "").Replace(",\"glamour\":false", "").Replace(",\"huntingLog\":false", "").Replace(",\"sightseeingLog\":false", "").Replace(",\"challengeLog\":false", "").Replace(",\"variantNotebook\":false", "")
            .Replace(",\"bestiary\":false", "").Replace(",\"craftingLog\":false", "").Replace(",\"fishingLog\":false", "");
        File.WriteAllText(folder.File, legacy);
        store = new CompanionAccessSettingsStore(folder.File);
        Check(store.Error is null && store.Settings == prior && !store.Settings.Bestiary,
            "schema16 preserves grants without Bestiary access");
        Check(File.ReadAllText(folder.File) == legacy, "loading does not rewrite");
        Check(!CollectionAccess.Allows(store.Settings, "beastmaster_bestiary"),
            "other collection permissions cannot grant Bestiary access");
        var plugin = new Plugin(store);
        plugin.SetAccess(false, false, false, bestiary: true);
        Check(plugin.AccessSaveError is null && plugin.Access == prior with { Bestiary = true }, "independent grant");
        Check(CollectionAccess.Categories(plugin.Access).SequenceEqual(
            new[] { "mounts", "minions", "blue_mage_spells", "beastmaster_bestiary" }), "advertised category");
        Check(new CompanionAccessSettingsStore(folder.File).Settings == plugin.Access, "grant persists");
        var valid = File.ReadAllText(folder.File);
        foreach (var bad in new[]
        {
            valid.Replace(",\"bestiary\":true", ""),
            valid.Replace("\"bestiary\":true", "\"bestiary\":true,\"bestiary\":false"),
            valid.Replace("\"bestiary\":true", "\"bestiary\":null"),
            valid.WithoutActivityFields().Replace("\"schemaVersion\":26", "\"schemaVersion\":16").Replace(",\"tripleTriad\":false", "").Replace(",\"battleChocobo\":false", "").Replace(",\"pvp\":false", "").Replace(",\"shops\":false", "").Replace(",\"glamour\":false", "").Replace(",\"huntingLog\":false", "").Replace(",\"sightseeingLog\":false", "").Replace(",\"challengeLog\":false", "").Replace(",\"variantNotebook\":false", ""),
        })
        {
            File.WriteAllText(folder.File, bad);
            var denied = new CompanionAccessSettingsStore(folder.File);
            Check(denied.Error is not null && denied.Settings == new CompanionAccessSettings(), "invalid grant fails closed");
        }
        File.WriteAllText(folder.File, valid);
        plugin.SetBackgroundQuestion(true);
        var cancelled = plugin.CancelCount;
        plugin.SetAccess(false, false, false, bestiary: false);
        Check(plugin.CancelCount == cancelled + 1 && !plugin.AnyPendingQuestion && plugin.Access == prior,
            "revocation cancels questions and preserves unrelated grants");
        Check(new CompanionAccessSettingsStore(folder.File).Settings == prior, "revocation persists");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
