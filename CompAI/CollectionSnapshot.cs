using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace CompAI;

internal sealed record CollectionEntry(uint Id, string Name, string State, bool? Unlocked, string? UnknownReason);

/// <summary>One selected category, frozen in memory for one question. False never means verified locked.</summary>
internal sealed class CollectionSnapshot
{
    internal const int MaxEntries = 4096;
    internal const int MaxNameLength = 128;
    internal const string Coverage = "Named local sheet rows for the selected category. " +
        "Unlocked means a positive current-character native observation. Not observed unlocked is a native false " +
        "with unverified negative-state readiness; it does not mean locked or missing. Unknown means no supported " +
        "observation. Counts cover this named local catalog only, not every obtainable collectible or server state. " +
        "No matching definition does not establish locked or unowned state.";

    internal string Category { get; }
    internal DateTimeOffset CapturedAtUtc { get; }
    internal bool Available { get; }
    internal bool LastObserved { get; }
    internal string? UnavailableReason { get; }
    internal ReadOnlyCollection<CollectionEntry> Entries { get; }

    internal CollectionSnapshot(string category, DateTimeOffset capturedAtUtc, bool available,
        IEnumerable<CollectionEntry> entries, string? unavailableReason = null, bool lastObserved = false)
    {
        ValidateCategory(category);
        if (lastObserved && (category != "beastmaster_bestiary" || !available))
            throw new ArgumentException("Only available Bestiary observations can be retained.");
        ArgumentNullException.ThrowIfNull(entries);
        var copied = entries.Take(MaxEntries + 1).ToArray();
        if (copied.Length > MaxEntries || copied.Any(entry => entry is null || entry.Id == 0 ||
            !ValidText(entry.Name, MaxNameLength) || entry.State is not ("unlocked" or "not_observed_unlocked" or "unknown") ||
            entry.Unlocked != (entry.State == "unlocked" ? true : null) ||
            (entry.State == "unlocked" ? entry.UnknownReason is not null : !ValidText(entry.UnknownReason, 256))) ||
            copied.Select(entry => entry.Id).Distinct().Count() != copied.Length || (!available && copied.Length != 0))
            throw new ArgumentException("Collection observation is invalid or ambiguous.", nameof(entries));
        if (unavailableReason is not null && !ValidText(unavailableReason, 256) || available && unavailableReason is not null)
            throw new ArgumentException("Invalid collection availability reason.", nameof(unavailableReason));
        Category = category;
        CapturedAtUtc = capturedAtUtc.ToUniversalTime();
        Available = available;
        LastObserved = lastObserved;
        UnavailableReason = available ? null : unavailableReason ?? "Collection state is unavailable.";
        Entries = Array.AsReadOnly(copied.OrderBy(entry => entry.Id).ToArray());
    }

    internal static CollectionSnapshot Unavailable(string category, string reason) =>
        new(category, DateTimeOffset.UtcNow, false, [], reason);

    internal static void ValidateCategory(string category)
    {
        if (!IsCategory(category))
            throw new ArgumentException("Unsupported collection category.", nameof(category));
    }

    internal static bool IsCategory(string? category) =>
        category is "mounts" or "minions" or "fashion_accessories" or "facewear" or "emotes" or "orchestrion" or "blue_mage_spells" or "beastmaster_bestiary" or "triple_triad" or "occult_records" or "yokai_minions" or "collection_tools";

    internal static string CategoryCoverage(string category) => category == "beastmaster_bestiary" ?
        "Named Master's Bestiary entries, keyed by XBMPet book ID, not general Pet IDs or assigned battlehorn slots. " +
        "Unlocked means a positive permanent familiar observation while the ordinary notebook is visible and the manager reports received data. " +
        "Native false remains unknown. No independent character-owner receipt marker is available; menu state may lag during loading. " +
        "Mirage, temporary content pets, abilities and habitats are not included." : Coverage + (category switch
    {
        "mounts" or "minions" => "",
        "triple_triad" => " Triple Triad IDs are resident card IDs, not physical inventory card items. Positive unlocks establish registered cards only; decks, NPC victories and card acquisition sources are not included. No menu needs to remain open.",
        "occult_records" => " Occult Record IDs are MKDLore RowIds. Positive unlock links establish unlocked records; story text and acquisition sources are excluded. No menu needs to remain open.",
        "yokai_minions" => " Yo-kai IDs are YKW RowIds, mapped to Companion rows. Positive observations establish registered Yo-kai minions only; weapons, medals, event progress and reward claims are excluded. No menu needs to remain open.",
        "collection_tools" => " IDs are McGuffin RowIds. These are access flags for named Collection tools such as Mogpendium, Portable Archive and Field Record, not records inside those tools or their completion. No menu needs to remain open.",
        "fashion_accessories" => " Fashion accessories describe Ornament unlocks, not held items or current use.",
        "facewear" => " Facewear uses one representative Glasses RowId per mapped GlassesStyle, named by GlassesStyle. " +
            "Counts are style representatives; colour variants are not separate unlocks or proof of held items.",
        "emotes" => " Emote unlocks do not establish current usability or ownership of an emote-teaching item.",
        "orchestrion" => " Orchestrion unlocks describe registered music, not ownership of roll items.",
        "blue_mage_spells" => " Blue Mage entries cover the named local spellbook catalog; id is the displayed spellbook number " +
            "from AozActionTransient.Number. Unlocked means learned, not equipped, usable now or currently on the Blue Mage job. Native false remains unknown.",
        _ => throw new ArgumentException("Invalid collection category.", nameof(category)),
    });

    internal static string Source(string category) => category switch
    {
        "mounts" => "PlayerState.IsMountUnlocked; Mount sheet RowId/Singular",
        "minions" => "UIState.IsCompanionUnlocked; Companion sheet RowId/Singular",
        "fashion_accessories" => "PlayerState.IsOrnamentUnlocked; Ornament sheet RowId/Singular",
        "facewear" => "PlayerState.IsGlassesUnlocked; Glasses sheet RowId/Style; GlassesStyle sheet Name",
        "emotes" => "UIState.IsEmoteUnlocked; Emote sheet RowId/Name",
        "orchestrion" => "PlayerState.IsOrchestrionRollUnlocked; Orchestrion sheet RowId/Name",
        "blue_mage_spells" => "UIState.IsUnlockLinkUnlocked; AozAction.Action/Action.UnlockLink; id=AozActionTransient.Number (displayed spellbook number)",
        "beastmaster_bestiary" => "XBMManager.IsPetUnlocked; visible ordinary AgentXBMMonsterNotebook; Experimental.XBMPet.RowId/Pet.Name",
        "triple_triad" => "UIState.IsTripleTriadCardUnlocked; TripleTriadCardResident.RowId + TripleTriadCard.RowId/Name",
        "occult_records" => "UIState.IsUnlockLinkUnlocked; MKDLore.RowId/Name/UnlockLink",
        "yokai_minions" => "UIState.IsCompanionUnlocked; YKW.RowId/Companion; Companion.RowId/Singular",
        "collection_tools" => "PlayerState.IsMcGuffinUnlocked; McGuffin.RowId/UIData; McGuffinUIData.Name",
        _ => throw new ArgumentException("Invalid collection category.", nameof(category)),
    };

    internal static bool ValidText(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);
}
