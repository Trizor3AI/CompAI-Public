using System.Linq;

namespace CompAI;

internal static class ActivityAccess
{
    internal static string[] Categories(CompanionAccessSettings access) => new[]
    {
        (access.TripleTriadDecks, "triple_triad_decks"), (access.RaceChocobo, "race_chocobo"),
        (access.Mahjong, "mahjong"), (access.Verminion, "verminion"), (access.FreeCompany, "free_company"),
        (access.Pvp, "pvp_series_rewards"), (access.FreeCompany, "free_company_details"),
        (access.FreeCompany, "free_company_credits"),
        (access.Currencies, "island_currencies"),
        (access.IslandProgress, "island_progress"), (access.CosmicResearch, "cosmic_research"),
        (access.IslandMaterials, "island_materials"), (access.CosmicMissions, "cosmic_missions"),
        (access.SelectedMacro, "selected_macro"),
        (access.CollectionTools, "field_record"), (access.CollectionTools, "portable_archive"),
        (access.CollectionTools, "mogpendium_standard"), (access.CollectionTools, "mogpendium_weekly"),
        (access.CollectionTools, "mogpendium_minimog"), (access.CollectionTools, "mogpendium_ultimog"),
    }.Where(row => row.Item1).Select(row => row.Item2).ToArray();
    internal static bool Allows(CompanionAccessSettings access, string category) => Categories(access).Contains(category);
}
