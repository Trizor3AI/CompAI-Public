using CompAI;

internal static class VariantNotebookAccessChecks
{
    internal static void Run()
    {
        using var folder = new TestFolder();
        var prior = new CompanionAccessSettings(Trust: true, Duties: true, FishingLog: true);
        var store = new CompanionAccessSettingsStore(folder.File);
        Check(store.Save(prior), "seed settings");
        var legacy = File.ReadAllText(folder.File).WithoutActivityFields().Replace("\"schemaVersion\":26", "\"schemaVersion\":18").Replace(",\"tripleTriad\":false", "").Replace(",\"battleChocobo\":false", "").Replace(",\"pvp\":false", "").Replace(",\"shops\":false", "").Replace(",\"glamour\":false", "").Replace(",\"huntingLog\":false", "").Replace(",\"sightseeingLog\":false", "").Replace(",\"challengeLog\":false", "")
            .Replace(",\"variantNotebook\":false", "");
        File.WriteAllText(folder.File, legacy);
        store = new CompanionAccessSettingsStore(folder.File);
        Check(store.Error is null && store.Settings == prior && !store.Settings.VariantNotebook,
            "previous permissions never imply Variant notebook access");
        Check(File.ReadAllText(folder.File) == legacy, "loading old settings does not rewrite them");
        var plugin = new Plugin(store);
        plugin.SetAccess(false, false, false, variantNotebook: true);
        var granted = prior with { VariantNotebook = true };
        Check(plugin.Access == granted && new CompanionAccessSettingsStore(folder.File).Settings == granted,
            "independent grant survives reload and preserves unrelated readers");
        Check(ProgressionAccess.Allows(granted, "variant_notebook") &&
            !ProgressionAccess.Allows(prior, "variant_notebook") &&
            ProgressionAccess.Categories(granted).Except(ProgressionAccess.Categories(prior)).SequenceEqual(["variant_notebook"]),
            "only the exact notebook category is newly available");
        var valid = File.ReadAllText(folder.File);
        foreach (var malformed in new[]
        {
            valid.Replace(",\"variantNotebook\":true", ""),
            valid.Replace("\"variantNotebook\":true", "\"variantNotebook\":null"),
            valid.Replace("\"variantNotebook\":true", "\"variantNotebook\":\"true\""),
            valid.Replace("\"variantNotebook\":true", "\"variantNotebook\":true,\"variantNotebook\":false"),
            valid.WithoutActivityFields().Replace("\"schemaVersion\":26", "\"schemaVersion\":18").Replace(",\"tripleTriad\":false", "").Replace(",\"battleChocobo\":false", "").Replace(",\"pvp\":false", "").Replace(",\"shops\":false", "").Replace(",\"glamour\":false", "").Replace(",\"huntingLog\":false", "").Replace(",\"sightseeingLog\":false", "").Replace(",\"challengeLog\":false", ""),
            valid.Replace("\"schemaVersion\":26", "\"schemaVersion\":27"),
        })
        {
            File.WriteAllText(folder.File, malformed);
            var rejected = new CompanionAccessSettingsStore(folder.File);
            Check(rejected.Error is not null && rejected.Settings == new CompanionAccessSettings(),
                "malformed, old-schema and unknown-schema grants fail closed");
        }
        File.WriteAllText(folder.File, valid);
        plugin.SetBackgroundQuestion(true);
        var cancelled = plugin.CancelCount;
        plugin.SetAccess(false, false, false, variantNotebook: false);
        Check(plugin.CancelCount == cancelled + 1 && !plugin.AnyPendingQuestion && plugin.Access == prior &&
            new CompanionAccessSettingsStore(folder.File).Settings == prior, "revocation cancels answers and persists");
        plugin.SetAccess(false, false, false, variantNotebook: true);
        plugin.SetBackgroundQuestion(true);
        cancelled = plugin.CancelCount;
        File.SetAttributes(folder.File, FileAttributes.ReadOnly);
        try
        {
            plugin.SetAccess(false, false, false, variantNotebook: false);
            Check(plugin.AccessSaveError is not null && plugin.Access == new CompanionAccessSettings() &&
                plugin.CancelCount == cancelled + 1 && !plugin.AnyPendingQuestion, "failed save revokes effective access");
        }
        finally { File.SetAttributes(folder.File, FileAttributes.Normal); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
