using System.Collections.Generic;

namespace CompAI;

internal static class ProgressionAccess
{
    internal static string[] Categories(CompanionAccessSettings settings)
    {
        var categories = new List<string>(8);
        if (settings.Achievements) categories.AddRange(["achievements", "achievement_progress"]);
        if (settings.AetherCurrents) categories.Add("aether_currents");
        if (settings.SharedFates) categories.Add("shared_fates");
        if (settings.Trust) categories.Add("trust");
        if (settings.VariantNotebook) categories.Add("variant_notebook");
        if (settings.GatheringLog) categories.Add("gathering_log");
        if (settings.FishingLog) categories.AddRange(["fishing_log", "spearfishing_log"]);
        if (settings.HuntingLog) categories.Add("hunting_log");
        if (settings.SightseeingLog) categories.Add("sightseeing_log");
        if (settings.ChallengeLog) categories.Add("challenge_log");
        if (settings.BattleChocobo) categories.Add("battle_chocobo");
        if (settings.Pvp) categories.Add("pvp");
        return categories.ToArray();
    }

    internal static bool Allows(CompanionAccessSettings settings, string category) => category switch
    {
        "achievements" or "achievement_progress" => settings.Achievements,
        "aether_currents" => settings.AetherCurrents,
        "shared_fates" => settings.SharedFates,
        "trust" => settings.Trust,
        "variant_notebook" => settings.VariantNotebook,
        "gathering_log" => settings.GatheringLog,
        "fishing_log" or "spearfishing_log" => settings.FishingLog,
        "hunting_log" => settings.HuntingLog,
        "sightseeing_log" => settings.SightseeingLog,
        "challenge_log" => settings.ChallengeLog,
        "battle_chocobo" => settings.BattleChocobo,
        "pvp" => settings.Pvp,
        _ => false,
    };
}
