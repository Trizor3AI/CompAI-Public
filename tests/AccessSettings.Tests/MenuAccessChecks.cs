using CompAI;
using System.Text.Json.Nodes;

internal static class MenuAccessChecks
{
    internal static void Run()
    {
        using var folder = new TestFolder();
        var keys = new[] { "shops", "glamour", "huntingLog", "sightseeingLog", "challengeLog" };
        var store = new CompanionAccessSettingsStore(folder.File);
        var prior = new CompanionAccessSettings(Inventory: true, Trust: true);
        Check(store.Save(prior));
        var legacy = JsonNode.Parse(File.ReadAllText(folder.File))!.AsObject();
        legacy["schemaVersion"] = 19;
        legacy.Remove("selectedMacro");
        foreach (var key in keys.Concat(ActivityAccessChecks.Fields).Concat(RegionalProgressAccessChecks.Fields.Concat(RegionalResourceAccessChecks.Fields))) legacy.Remove(key);
        foreach (var key in new[] { "tripleTriad", "battleChocobo", "pvp" }) legacy.Remove(key);
        File.WriteAllText(folder.File, legacy.ToJsonString());
        Check(new CompanionAccessSettingsStore(folder.File).Settings == prior);
        Check(new CompanionAccessSettingsStore(folder.File).Error is null);

        foreach (var key in keys)
        {
            store = new CompanionAccessSettingsStore(folder.File);
            var plugin = new Plugin(store);
            void Set(bool on)
            {
                switch (key)
                {
                    case "shops": plugin.SetAccess(true, false, false, shops: on); break;
                    case "glamour": plugin.SetAccess(true, false, false, glamour: on); break;
                    case "huntingLog": plugin.SetAccess(true, false, false, huntingLog: on); break;
                    case "sightseeingLog": plugin.SetAccess(true, false, false, sightseeingLog: on); break;
                    case "challengeLog": plugin.SetAccess(true, false, false, challengeLog: on); break;
                }
            }
            Set(true);
            var valid = File.ReadAllText(folder.File);
            Check(JsonNode.Parse(valid)![key]!.GetValue<bool>());
            Check(new CompanionAccessSettingsStore(folder.File).Settings == plugin.Access);
            foreach (var replacement in new[] { "null", "\"true\"", "1" })
            {
                File.WriteAllText(folder.File, valid.Replace($"\"{key}\":true", $"\"{key}\":{replacement}"));
                Check(new CompanionAccessSettingsStore(folder.File).Error is not null);
            }
            foreach (var bad in new[] { valid.Replace($",\"{key}\":true", ""),
                valid.Replace($"\"{key}\":true", $"\"{key}\":true,\"{key}\":false"),
                valid.WithoutActivityFields().Replace("\"schemaVersion\":26", "\"schemaVersion\":19").Replace(",\"tripleTriad\":false", "").Replace(",\"battleChocobo\":false", "").Replace(",\"pvp\":false", "") })
            {
                File.WriteAllText(folder.File, bad);
                var rejected = new CompanionAccessSettingsStore(folder.File);
                Check(rejected.Error is not null && rejected.Settings == new CompanionAccessSettings());
            }
            File.WriteAllText(folder.File, valid);
            plugin.SetBackgroundQuestion(true);
            var count = plugin.CancelCount;
            Set(false);
            Check(plugin.CancelCount == count + 1 && !plugin.AnyPendingQuestion && plugin.Access == prior);
        }
    }
    private static void Check(bool ok) { if (!ok) throw new Exception("Menu/log access boundary failed."); }
}
