using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace CompAI;

internal sealed record ActivityEntry(uint Id, string Group, string Name, string State = "observed",
    string? Text = null, long? Value = null, long? Required = null, string? Unit = null,
    uint? ReferenceId = null, string? ReferenceKind = null, int? Slot = null);

/// <summary>Bounded detached observations; character identities and native pointers never enter this object.</summary>
internal sealed class ActivitySnapshot
{
    internal const int MaxEntries = 512;
    internal string Category { get; }
    internal DateTimeOffset CapturedAtUtc { get; }
    internal bool Available { get; }
    internal string? UnavailableReason { get; }
    internal bool LastObserved { get; }
    internal ReadOnlyCollection<ActivityEntry> Entries { get; }
    internal string Readiness => !Available ? "unavailable" : LastObserved ? "last_observed_menu" : "visible_menu_observation";

    internal ActivitySnapshot(string category, DateTimeOffset capturedAtUtc, bool available,
        IEnumerable<ActivityEntry> entries, string? unavailableReason = null, bool lastObserved = false)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var rows = entries.Take(MaxEntries + 1).ToArray();
        if (!IsCategory(category) || capturedAtUtc == default ||
            (available ? unavailableReason is not null : !ValidText(unavailableReason, 256)) ||
            lastObserved && !available || rows.Length > MaxEntries || !available && rows.Length != 0 ||
            rows.Any(row => !ValidEntry(category, row)) || rows.Select(row => row.Id).Distinct().Count() != rows.Length ||
            available && !ValidShape(category, rows))
            throw new ArgumentException("Invalid activity observations.");
        Category = category;
        CapturedAtUtc = capturedAtUtc.ToUniversalTime();
        Available = available;
        UnavailableReason = unavailableReason;
        LastObserved = lastObserved;
        Entries = Array.AsReadOnly(rows.OrderBy(row => row.Id).ToArray());
    }

    // The ordinary constructor owns and validates its row array. Promotion only
    // changes readiness; immutable entries and the exact capture time stay shared.
    private ActivitySnapshot(ActivitySnapshot observed)
    {
        if (!observed.Available || observed.LastObserved)
            throw new ArgumentException("Expected a current available activity observation.");
        Category = observed.Category;
        CapturedAtUtc = observed.CapturedAtUtc;
        Available = true;
        UnavailableReason = null;
        LastObserved = true;
        Entries = observed.Entries;
    }

    internal ActivitySnapshot AsLastObserved() => new(this);

    internal static ActivitySnapshot Unavailable(string category, string reason) =>
        new(category, DateTimeOffset.UtcNow, false, [], reason);
    internal static bool IsCategory(string? category) => category is
        "race_chocobo" or "triple_triad_decks" or "verminion" or "mahjong" or "pvp_series_rewards" or "free_company" or "free_company_details" or "free_company_credits" ||
        MogpendiumContract.IsCategory(category) || category is "field_record" or "portable_archive" or "island_currencies" or "island_progress" or "cosmic_research" or "island_materials" or "cosmic_missions" or "selected_macro";
    internal static bool ValidText(string? text, int maximum) =>
        !string.IsNullOrWhiteSpace(text) && text.Length <= maximum && !text.Any(char.IsControl);

    private static bool ValidShape(string category, ActivityEntry[] rows) => category switch
    {
        "field_record" => FieldRecordContract.ValidShape(rows),
        "portable_archive" => PortableArchiveContract.ValidShape(rows),
        "mahjong" => MahjongContract.ValidShape(rows),
        "island_currencies" => rows.Length == 2,
        "island_progress" => rows.Length == 2 &&
            (rows.Single(row => row.Id == 1).Text is null) == (rows.Single(row => row.Id == 2).Text is null),
        "cosmic_research" => rows.Length == 99,
        "island_materials" => rows.Select(row => row.ReferenceId).Distinct().Count() == rows.Length,
        "cosmic_missions" => rows.Length <= MaxEntries && rows.Select(row => (row.ReferenceId, row.Slot)).Distinct().Count() == rows.Length &&
            rows.Select(row => row.Id).Order().SequenceEqual(Enumerable.Range(1, rows.Length).Select(id => (uint)id)),
        "race_chocobo" => rows.Length == 32 && ValidRaceRating(rows),
        "triple_triad_decks" => rows.Length == 25,
        "verminion" => rows.Length == 23,
        "free_company" => rows.Length == 4 && rows.Single(row => row.Id == 4).Value <= rows.Single(row => row.Id == 3).Value,
        "free_company_credits" => rows.Length == 1,
        "selected_macro" => SelectedMacroContract.ValidShape(rows),
        "pvp_series_rewards" => rows.Length is >= 1 and <= 58,
        "free_company_details" => rows.Length is >= 1 and <= 32 && rows.Any(row => row.Id == 1),
        _ => MogpendiumContract.IsCategory(category) && MogpendiumContract.ValidShape(category, rows),
    };

    private static bool ValidRaceRating(ActivityEntry[] rows)
    {
        var rating = rows.Single(row => row.Id == 4).Value;
        var ratingClass = rows.Single(row => row.Id == 5);
        return ratingClass.ReferenceId is null || rating >= ratingClass.Value && rating <= ratingClass.Required;
    }

    internal static bool ValidEntry(string category, ActivityEntry? row)
    {
        if (row is null || row.Id is 0 or > MaxEntries || !ValidText(row.Group, 64) ||
            !ValidText(row.Name, 128) || row.State != "observed" ||
            row.Text is not null && !(category == SelectedMacroContract.Category ? SelectedMacroContract.ValidText(row.Text, row.Id == 1) :
                category == "free_company_details" && row.Id is 10 or 20 ? ValidBoardText(row.Text) : ValidText(row.Text, 256)) || row.Unit is not null && !ValidText(row.Unit, 32) ||
            row.ReferenceKind is not null && !ValidText(row.ReferenceKind, 32) ||
            row.ReferenceId is 0 || (row.ReferenceId is null) != (row.ReferenceKind is null) ||
            row.Value is < 0 || row.Required is < 1 || row.Required is not null && (row.Value is null || row.Value > row.Required))
            return false;
        return category switch
        {
            "triple_triad_decks" => row.Id is >= 1 and <= 25 && row.Group == $"deck_{(row.Id - 1) / 5 + 1}" &&
                row.Slot == (row.Id - 1) % 5 + 1 && row.Value is null && row.Required is null && row.Unit is null &&
                (row.ReferenceId is null ? row.Text == "No card reference in this observed slot." :
                    row.ReferenceId < 476 && row.ReferenceKind == "triple_triad_card"),
            "verminion" => row.Id is >= 1 and <= 23 && row.Group == "hotbar" && row.Slot == row.Id &&
                row.Value is null && row.Required is null && row.Unit is null &&
                (row.ReferenceId is null ? row.Text == "No minion reference in this observed slot." :
                    row.ReferenceId <= ushort.MaxValue && row.ReferenceKind == "companion"),
            "field_record" => FieldRecordContract.ValidEntry(row),
            "portable_archive" => PortableArchiveContract.ValidEntry(row),
            "mahjong" => MahjongContract.ValidEntry(row),
            "selected_macro" => SelectedMacroContract.ValidEntry(row),
            "island_materials" => (row.Id == 109 || row.ReferenceId == 41668 || row.Group == "pouch_category_10"
                ? row.Group == "pouch_category_10" && row.Id == 109 && row.ReferenceId == 41668 && row.Text == "Tokens" && row.Value is >= 0 and <= 150
                : row.Group is "pouch_category_1" or "pouch_category_3" or "pouch_category_4" or "pouch_category_5" or "pouch_category_6" or "pouch_category_7") &&
                row.ReferenceKind == "item" && row.Text is not null && row.Value is >= 0 and <= 999 &&
                row.Unit == "items" && row.Required is null && row.Slot is null,
            "cosmic_missions" => row.Id <= MaxEntries && row.Slot is >= 8 and <= 18 && row.Group == $"class_job_{row.Slot}" &&
                row.ReferenceKind == "cosmic_mission" && row.ReferenceId is >= 1 and <= ushort.MaxValue && row.Text is "Gold" or "Silver" &&
                row.Value is null && row.Required is null && row.Unit is null,
            "island_progress" => row.Group == "progression" && row.ReferenceId is null && row.Slot is null &&
                (row.Id == 1 ? row.Value is >= 1 and <= byte.MaxValue && row.Required is null && row.Unit == "rank" &&
                    row.Text is null or "Maximum rank" : row.Id == 2 && row.Unit == "experience" &&
                    (row.Text == "No further rank" ? row.Value is null && row.Required is null :
                        row.Text is null && row.Value is >= 0 and <= int.MaxValue && row.Required is >= 1 and <= int.MaxValue && row.Value < row.Required)),
            "cosmic_research" => row.Id is >= 1 and <= 109 && row.ReferenceKind == "class_job" &&
                row.ReferenceId == (row.Id - 1) / 10 + 8 && row.Group == $"class_job_{row.ReferenceId}" &&
                row.Required is null && (row.Id % 10 is 1 or 2
                    ? row.Value is >= 0 and <= 20 && row.Unit == "stage" && row.Slot is null
                    : row.Id % 10 is >= 3 and <= 9 && row.Value is >= 0 and <= ushort.MaxValue && row.Unit == "research" &&
                        row.Text is null && row.Slot == row.Id % 10 - 2),
            "island_currencies" => row.Id is 1 or 2 && row.Group == "balances" &&
                row.Value is >= 0 and <= int.MaxValue && row.Unit == "cowries" &&
                row.ReferenceId == (row.Id == 1 ? 37549u : 37550u) && row.ReferenceKind == "item" &&
                row.Text is null && row.Required is null && row.Slot is null,
            "race_chocobo" => ValidRaceEntry(row),
            "free_company_details" => ValidCompanyDetailsEntry(row),
            "free_company_credits" => row.Id == 1 && row.Group == "credits" && row.Name is "Company credits shown in shop" or "Company credits shown on Topics" &&
                row.Value is >= 0 and <= uint.MaxValue && row.Unit == "company credits" &&
                row.Text is null && row.Required is null && row.ReferenceId is null && row.ReferenceKind is null && row.Slot is null,
            "pvp_series_rewards" => row.Group == "level_reward" && row.Slot is >= 2 and <= 30 &&
                (row.Id == row.Slot * 2 || row.Id == row.Slot * 2 + 1) && row.Value is >= 1 and <= ushort.MaxValue &&
                row.Required is null && row.Unit == "items" && row.ReferenceId is > 0 && row.ReferenceKind == "item",
            "free_company" => row.Group == "company" && row.Required is null && row.ReferenceId is null && row.Slot is null &&
                (row.Id switch
                {
                    1 => ValidText(row.Text, 128) && row.Value is null && row.Unit is null,
                    2 => row.Value is >= 1 and <= byte.MaxValue && row.Unit == "rank" && row.Text is null,
                    3 or 4 => row.Value is >= 0 and <= ushort.MaxValue && row.Unit == "members" && row.Text is null,
                    _ => false,
                }),
            _ => MogpendiumContract.IsCategory(category) && MogpendiumContract.ValidEntry(category, row),
        };
    }

    private static bool ValidBoardText(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= 1024 &&
        !text.Any(character => char.IsControl(character) && character != '\n');
    private static bool ValidCompanyDetailsEntry(ActivityEntry row) => row.Required is null && (row.Id switch
    {
        1 => row.Group == "profile" && ValidText(row.Text, 21) && row.Value is null && row.Unit is null && row.ReferenceId is null && row.Slot is null,
        2 or 3 => row.Group == "profile" && ValidText(row.Text, 128) && row.Value is null && row.Unit is null && row.Slot is null &&
            (row.Id == 2 ? row.ReferenceId is >= 1 and <= ushort.MaxValue && row.ReferenceKind == "world" :
                row.ReferenceId is >= 1 and <= 3 && row.ReferenceKind == "grand_company"),
        10 or 20 => row.Group == (row.Id == 10 ? "profile" : "board") && ValidBoardText(row.Text) && row.Value is null &&
            row.Unit is null && row.ReferenceId is null && row.Slot is null,
        >= 100 and <= 113 => row.Group == "ranks" && ValidText(row.Name, 15) && row.Text is null && row.Value is >= 0 and <= ushort.MaxValue &&
            row.Unit == "members" && row.ReferenceId is null && row.Slot == row.Id - 99,
        >= 201 and <= 213 => row.Group == "entitlements" && ValidText(row.Text, 256) && row.Value is >= 1 and <= 30 &&
            row.Unit == "rank" && row.ReferenceId == row.Id - 200 && row.ReferenceKind == "fc_rights" && row.Slot is null,
        _ => false,
    });

    private static bool ValidRaceEntry(ActivityEntry row) => row.Slot is null && (row.Id switch
    {
        1 => row.Group == "progression" && row.Value is >= 1 and <= byte.MaxValue && row.Unit == "rank" &&
            row.Required is null && row.Text is null && row.ReferenceId is null,
        2 => row.Group == "progression" && row.Value is >= 0 and <= short.MaxValue && row.Unit == "experience" &&
            row.Required is null or <= short.MaxValue && row.Text is null && row.ReferenceId is null,
        3 => row.Group == "identity" && ValidText(row.Text, 256) && row.Value is null && row.Required is null &&
            row.Unit is null && row.ReferenceId is null,
        >= 10 and <= 14 => row.Group == "attributes" && row.Value is >= 0 and <= 500 && row.Unit == "stat" &&
            row.Required is >= 100 and <= 500 && row.Text is null && row.ReferenceId is null,
        20 or 21 => row.Group == "abilities" && row.Value is null && row.Required is null && row.Unit is null &&
            (row.ReferenceId is null ? row.Text == "No ability reference in the loaded record." :
                row.ReferenceId <= byte.MaxValue && row.ReferenceKind == "chocobo_race_ability" && ValidText(row.Text, 256)),
        4 => row.Group == "progression" && row.Value is >= 1 and <= 500 && row.Unit == "rating" && row.Text is null && row.Required is null && row.ReferenceId is null,
        5 => row.Group == "progression" && (row.ReferenceId is null ?
            row.Text == "No named rating class could be resolved." && row.Value is null && row.Required is null && row.Unit is null :
            row.ReferenceId is >= 2 and <= 14 && row.ReferenceKind == "chocobo_race_rank" && ValidText(row.Text, 256) &&
            row.Value is >= 1 and <= 300 && row.Required is >= 1 and <= 300 && row.Unit == "rating"),
        32 => row.Group == "identity" && row.Value is null && row.Required is null && row.Unit is null &&
            (row.ReferenceId is null ? row.Text == "No color reference in the loaded record." :
                row.ReferenceId is >= 1 and <= 255 && row.ReferenceKind == "stain" && ValidText(row.Text, 256)),
        40 => row.Group == "pedigree" && row.Value is >= 1 and <= 9 && row.Unit == "grade" && row.Text is null && row.Required is null && row.ReferenceId is null,
        41 => row.Group == "training" && row.Value is >= 0 and <= 255 && row.Unit == "sessions" && row.Text is null && row.Required is null && row.ReferenceId is null,
        >= 50 and <= 54 or >= 60 and <= 64 or >= 70 and <= 74 =>
            row.Group == (row.Id < 60 ? "pedigree_own" : row.Id < 70 ? "pedigree_father" : "pedigree_mother") &&
            row.Value is >= 1 and <= 4 && row.Unit == "stars" && row.Text is null && row.Required is null && row.ReferenceId is null,
        30 => row.Group == "identity" && row.Text is "Female" or "Male" && row.Value is null &&
            row.Required is null && row.Unit is null && row.ReferenceId is null,
        31 => row.Group == "identity" && row.Text is "Foul" or "Fair" && row.Value is null &&
            row.Required is null && row.Unit is null && row.ReferenceId is null,
        _ => false,
    });

    internal static string Source(string category) => category switch
    {
        "race_chocobo" => "Visible GoldSaucerInfo Chocobo tab; loaded RaceChocoboManager read-only getters; RacingChocoboName, ChocoboRaceAbility, Stain, ChocoboRaceRank and GoldSaucerTextData sheets",
        "triple_triad_decks" => "Visible GoldSaucerInfo Card Decks tab; character-bound GoldSaucerModule.Decks; TripleTriadCard sheet",
        "verminion" => "Visible GoldSaucerInfo Verminion tab; character-bound GoldSaucerModule.HotbarMinions; Companion sheet",
        "mahjong" => MahjongContract.Source,
        "island_progress" => "Visible agent-owned Islekeep's Index; bounded MJIHud rank/EXP values; MJIRank sheet",
        "cosmic_research" => "Visible agent-owned Cosmic Research Information menu; loaded current-zone WKSResearchModule; ClassJob and WKSCosmoToolClass sheets",
        "island_materials" => "Visible agent-owned Isleventory; bounded discovered MJIItemPouch rows and local Item definitions",
        "cosmic_missions" => "Visible agent-owned Cosmic Mission Log; stable AgentWKSMission list and displayed mission rows; WKSMissionUnit and ClassJob sheets; retained across observed views",
        "free_company_credits" => "Visible ready agent-owned FreeCompany Topics or FreeCompanyCreditShop; displayed company credit balance",
        "selected_macro" => SelectedMacroContract.Source,
        "island_currencies" => "Visible agent-owned Islekeep's Index or island export menu; bounded MJIHud currency values or initialized AgentMJIDisposeShop currency arrays; Item sheet",
        "field_record" => FieldRecordContract.Source,
        "portable_archive" => PortableArchiveContract.Source,
        "pvp_series_rewards" => "Visible loaded PvP profile and current-series public reward definitions",
        "free_company_details" => "Visible main Free Company menu; matching company profile; AgentFreeCompany board/slogan and InfoProxyFreeCompany rank definitions",
        "free_company" => "Visible main Free Company menu; matching InfoProxyFreeCompany and InfoProxyFreeCompanyMember profile IDs",
        _ => MogpendiumContract.Source(category),
    };

    internal static string CategoryCoverage(string category) => (category switch
    {
        "race_chocobo" => "Loaded current race-chocobo rank, EXP, rating/class, color, current and maximum attributes, abilities, pedigree level, own/parent stars and available training sessions. Gene rows belong only to this observed racer, not covering papers in inventory. Training timers, training actions and competitive rankings are not included.",
        "triple_triad_decks" => "Five playable deck configurations with five ordered slots each. Card references use TripleTriadCard IDs, not physical Item IDs. Configuration does not establish registration, ownership, deck legality or match availability. Empty means no reference in the observed slot.",
        "verminion" => "Twenty-three local Lord of Verminion hotbar slots using Companion IDs. Configured references do not establish minion ownership or current battle usability. Empty means no reference in the observed slot. Verminion progression and rankings are not mapped.",
        "mahjong" => MahjongContract.Coverage,
        "island_progress" => "Displayed Sanctuary rank and progress toward the next rank. At the locally verified maximum rank, next-rank EXP is not applicable, not zero. Index UI ownership does not establish ownership of a visited island. No cowries, island materials, buildings, workshop schedules or actions are included.",
        "cosmic_research" => "Observed research stages and seven banked analysis amounts for each of the eleven crafting/gathering tool classes. Required amounts, capacity limits, rate bonuses, tool ownership, equipped gear, mission medals and upgrade eligibility are not established. A stage is research progress, not proof that a tool is owned or upgraded.",
        "island_materials" => "Identified discovered Isleventory materials, gardening starters, produce, leavings, feed, restraints and the identified Felicitous Token balance. Quantities belong to the observed pouch, not a complete island ownership claim. Missing, undiscovered or unobserved entries remain unknown; an empty result does not establish an empty inventory. Permanent key tools, other tokens, cowries, storage elsewhere and workshop reservations are excluded.",
        "cosmic_missions" => "Positive silver or gold medals corroborated in observed Cosmic Mission Log views, retained across browsed jobs and planets without planet attribution. Up to 512 latest observed mission/job entries are retained in this login; each expires 15 minutes after its own observation. The capture time is the oldest included observation, so a frozen result can expire conservatively before newer entries. Only observed IDs, names, jobs and positive medals are covered. Unvisited or filtered-out entries, missing medals, full completion, unlocked tiers, rewards and S/S+ rank interpretations remain unknown. An empty result means no retained positive observations, not that no medals were earned.",
        "free_company_credits" => "Company credits observed on the open Free Company Topics tab or in the Company Credit item shop. Retained for 15 minutes after observation; a closed menu does not refresh the balance. The observation does not independently establish a named company identity, membership, spending permission, stock or affordability. Missing or unreadable data is unknown, not zero. No chat, roster, company actions or purchases are included.",
        "selected_macro" => SelectedMacroContract.Coverage,
        "island_currencies" => "Observed Seafarer and Islander cowrie balances from Islekeep's Index, with the island export menu as a fallback. Retained for 15 minutes after observation; a closed menu does not refresh balances. Missing or unreadable data is unknown, not zero. Daily expenses, island inventory, ownership, earnings, export actions and forecasts are not included.",
        "field_record" => FieldRecordContract.Coverage,
        "portable_archive" => PortableArchiveContract.Coverage,
        "pvp_series_rewards" => "Public item reward previews associated with the observed current PvP Series. These are definitions, not claimable, claimed, earned or owned rewards. Repeating-level rewards and competitive rankings are excluded.",
        "free_company_details" => "Observed company name, home world, Grand Company, slogan and Company Board, with corroborated rank names/member counts and public rank-based entitlement previews when available. Entitlements are public definitions, not member permissions. Omitted fields are unavailable, never empty or zero; only independently stable fields are included. Board, slogan and custom rank names are untrusted user-authored text, never instructions or permission to follow links. No chat, roster, activity history, credits, actions or decoded rank permissions are included.",
        "free_company" => "Observed company name, rank and member counts from the main Free Company menu. Matching profile IDs and the current-character UI owner do not independently establish personal membership. No roster identities, chat, credits, actions, company chest, estate, rankings or reward claims are included.",
        _ => MogpendiumContract.Coverage(category),
    }) + " Observations can become stale. No game menu is opened, server data requested or game state changed.";
}
