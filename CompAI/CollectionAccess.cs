using System.Collections.Generic;

namespace CompAI;

internal static class CollectionAccess
{
    internal static string[] Categories(CompanionAccessSettings settings)
    {
        var categories = new List<string>(8);
        if (settings.Collections)
        {
            categories.Add("mounts");
            categories.Add("minions");
        }
        if (settings.FashionAccessories) categories.Add("fashion_accessories");
        if (settings.Facewear) categories.Add("facewear");
        if (settings.Emotes) categories.Add("emotes");
        if (settings.Orchestrion) categories.Add("orchestrion");
        if (settings.BlueMage) categories.Add("blue_mage_spells");
        if (settings.Bestiary) categories.Add("beastmaster_bestiary");
        if (settings.TripleTriad) categories.Add("triple_triad");
        if (settings.OccultRecords) categories.Add("occult_records");
        // The retired Yo-kai setting remains readable for configuration compatibility.
        // Its minions and medals use the ordinary collection/inventory permissions.
        if (settings.CollectionTools) categories.Add("collection_tools");
        return categories.ToArray();
    }

    internal static bool Allows(CompanionAccessSettings settings, string category) => category switch
    {
        "mounts" or "minions" => settings.Collections,
        "fashion_accessories" => settings.FashionAccessories,
        "facewear" => settings.Facewear,
        "emotes" => settings.Emotes,
        "orchestrion" => settings.Orchestrion,
        "blue_mage_spells" => settings.BlueMage,
        "beastmaster_bestiary" => settings.Bestiary,
        "triple_triad" => settings.TripleTriad,
        "occult_records" => settings.OccultRecords,
        "collection_tools" => settings.CollectionTools,
        _ => false,
    };
}
