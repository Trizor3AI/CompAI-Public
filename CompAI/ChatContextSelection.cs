namespace CompAI;

internal static class ChatContextSelection
{
    internal static CompanionAccessSettings Create(CompanionAccessSettings settings, bool characterReady,
        bool hasInventory, bool hasQuests) => new(
            Inventory: settings.Inventory && characterReady && hasInventory,
            Quests: settings.Quests && characterReady && hasQuests,
            WebSearch: settings.WebSearch);
}
