using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace CompAI;

internal sealed record ProgressionEntry(uint Id, string Name, string State, bool? Completed = null,
    int? Rank = null, long? Progress = null, long? Required = null, int? Level = null,
    uint? Experience = null, uint? ExperienceRequired = null, string? UnknownReason = null,
    DateTimeOffset? ObservedAtUtc = null, int? ObservedPage = null);

/// <summary>Bounded managed observations for one category in one answer; never retained across characters.</summary>
internal sealed class ProgressionSnapshot
{
    internal const int MaxEntries = 8192;
    internal const int MaxNameLength = 128;
    internal string Category { get; }
    internal DateTimeOffset CapturedAtUtc { get; }
    internal bool Available { get; }
    internal string Readiness { get; }
    internal string? UnavailableReason { get; }
    internal ReadOnlyCollection<ProgressionEntry> Entries { get; }
    internal int? MenuPage { get; }
    internal bool SavedMenu => this.Readiness == "saved_menu_observations";
    internal string Coverage => this.SavedMenu
        ? (this.Category == "achievement_progress"
            ? "Recent local achievement counter observations, retained for 15 minutes in this character session after the menu closes. "
            : "Saved local observations from previously visible menu pages, retained after closing or reloading. ") +
          "Each row is the latest saved observation of that ID and carries its own observed_at_utc and observed_page. " +
          "Different rows may come from different pages and times; captured_at_utc is the newest included observation. " +
          "Counts cover saved IDs only. Unvisited or unreadable pages and missing IDs remain unknown, never zero or locked. " +
          "These values are last known, not live or server-refreshed. Open the relevant page and wait for its data to settle to update it. " +
          (this.Category switch
          {
              "hunting_log" => "Hunting totals sum targets per task. Completed earlier-rank totals are derived from positive rank advancement and public task requirements, not live earlier-rank counters. Older saved rows without totals need a page refresh. Book/rank pages are retained independently.",
              "challenge_log" => "Challenge tabs expire after 15 minutes or Tuesday 08:00 UTC, whichever comes first. Reset receipt is unverified; missing flags stay unknown. Required is the public target; completed totals may be inferred from positive flags. Partial counters cover Eureka/Verminion and verified leve/retainer rows matched to their visible title and ratio.",
              "achievement_progress" => "Each source record carries its own achievement ID, which may differ from the menu selection. observed_page 0 means the loaded progress-record slot. These are counters, not completion or reward claims; no disk persistence.",
              _ => "Trust member eligibility, unlocks and glamour are not established.",
          })
        : CategoryCoverage(this.Category);

    internal ProgressionSnapshot(string category, DateTimeOffset capturedAtUtc, bool available,
        string readiness, IEnumerable<ProgressionEntry> entries, string? unavailableReason = null, int? menuPage = null)
    {
        ValidateCategory(category);
        ArgumentNullException.ThrowIfNull(entries);
        var copied = entries.Take(MaxEntries + 1).ToArray();
        var savedMenu = readiness == "saved_menu_observations" && category is "shared_fates" or "trust" or "hunting_log" or "challenge_log" or "achievement_progress";
        if (copied.Length > MaxEntries || (!available && copied.Length != 0) ||
            (available && category == "battle_chocobo" && copied.Length != 5) ||
            (available && category == "pvp" && copied.Length != 2) ||
            (available && category == "achievement_progress" && (savedMenu ? copied.Length is < 1 or > 64 : copied.Length != 1)) ||
            copied.Any(entry => !ValidEntry(category, entry)) ||
            copied.Select(entry => entry.Id).Distinct().Count() != copied.Length ||
            (!savedMenu && readiness != (available ? ReadyState(category) : "unavailable") &&
                !(available && category == "pvp" && readiness == "last_observed_profile")) ||
            (savedMenu && (!available || copied.Length == 0 || menuPage is not null)) ||
            (menuPage is not null && (!available || !ValidMenuPage(category, menuPage.Value))) ||
            copied.Any(entry => savedMenu
                ? ((category is "shared_fates" or "trust") && entry.State != "observed") || entry.ObservedAtUtc is null || entry.ObservedAtUtc == default(DateTimeOffset) ||
                  entry.ObservedAtUtc > capturedAtUtc || entry.ObservedPage is null || !ValidMenuPage(category, entry.ObservedPage.Value) ||
                  category == "hunting_log" && !HuntingEntryMatchesPage(entry, entry.ObservedPage.Value)
                : entry.ObservedAtUtc is not null || entry.ObservedPage is not null) ||
            (unavailableReason is not null && !ValidText(unavailableReason, 256)) ||
            (available && unavailableReason is not null))
            throw new ArgumentException("Progression observation is invalid or ambiguous.", nameof(entries));
        Category = category;
        CapturedAtUtc = capturedAtUtc.ToUniversalTime();
        Available = available;
        Readiness = readiness;
        UnavailableReason = available ? null : unavailableReason ?? "Progression state is unavailable.";
        Entries = Array.AsReadOnly(copied.OrderBy(entry => entry.Id).ToArray());
        MenuPage = menuPage;
    }

    internal static bool ValidMenuPage(string category, int page) => category switch
    {
        "shared_fates" => page is >= 0 and < 3,
        "trust" => page is >= 0 and < 80,
        "hunting_log" => page is >= 0 and < 60,
        "challenge_log" => page is >= 0 and < 15,
        "achievement_progress" or "pvp" => page == 0,
        _ => false,
    };

    internal static bool HuntingEntryMatchesPage(ProgressionEntry entry, int page)
    {
        var book = page / 5;
        var start = book switch
        {
            >= 0 and <= 6 => (uint)(book + 1) * 10000,
            7 => 260000u,
            >= 8 and <= 10 => (uint)(book - 7) * 1000000,
            11 => 290000u,
            _ => 0u,
        } + (uint)(page % 5) * 10;
        return ValidMenuPage("hunting_log", page) && entry.Id > start && entry.Id <= start + 10 &&
            (entry.Rank is null || entry.Rank == page % 5 + 1);
    }

    private static bool ValidEntry(string category, ProgressionEntry? entry)
    {
        if (entry is null || entry.Id == 0 || !ValidText(entry.Name, MaxNameLength) ||
            !IsState(category, entry.State) ||
            ((entry.State is "unknown" or "not_observed_complete") ?
                !ValidText(entry.UnknownReason, 256) : entry.UnknownReason is not null))
            return false;
        if (category is "hunting_log" or "challenge_log")
            return (category != "challenge_log" || entry.Id <= 104) &&
                entry.Completed == (entry.State == "completed" ? true : null) &&
                entry.Level is null && entry.Experience is null && entry.ExperienceRequired is null &&
                (entry.State == "unknown" ? entry.Rank is null && entry.Progress is null && entry.Required is null :
                    (category == "hunting_log" ? entry.Rank is >= 1 and <= 5 : entry.Rank is null) &&
                    (entry.Progress is null ? entry.Required is null || category == "challenge_log" && entry.Required is > 0 and <= int.MaxValue :
                        entry.Progress >= 0 && entry.Required > 0 && entry.Progress <= entry.Required &&
                        entry.Required <= (category == "hunting_log" ? 1020 : int.MaxValue) &&
                        (category != "challenge_log" || entry.State == "completed" || ChallengeCounterSupported(entry.Id) || ChallengeVisibleCounterSupported(entry.Id)) &&
                        (entry.State == "completed" ? entry.Progress == entry.Required : entry.Progress < entry.Required)));
        if (category == "aether_currents")
            return entry.Completed == (entry.State == "completed" ? true : null) &&
                entry.Rank is null && entry.Level is null && entry.Experience is null && entry.ExperienceRequired is null &&
                (entry.Progress is null ? entry.Required is null :
                    entry.State != "unknown" && entry.Required is >= 1 and <= 15 &&
                    entry.Progress >= 0 && entry.Progress <= entry.Required &&
                    (entry.State != "completed" || entry.Progress == entry.Required));
        if (category is "achievements" or "gathering_log" or "fishing_log" or "spearfishing_log" or "variant_notebook" or "sightseeing_log")
            return entry.Completed == (entry.State == "completed" ? true :
                    entry.State == "not_completed" ? false : null) &&
                entry.Rank is null && entry.Progress is null && entry.Required is null &&
                entry.Level is null && entry.Experience is null && entry.ExperienceRequired is null;
        if (entry.Completed is not null) return false;
        if (category == "battle_chocobo")
            return entry.State == "observed" && entry.Level is null && entry.Required is null && (entry.Id switch
            {
                1 => entry.Rank is >= 1 and <= 20 && entry.Progress is null && entry.Experience is not null &&
                    (entry.Rank == 20 ? entry.ExperienceRequired is null : entry.ExperienceRequired is > 0 && entry.Experience <= entry.ExperienceRequired),
                2 or 3 or 4 => entry.Rank is >= 0 and <= 10 && entry.Progress is null && entry.Experience is null && entry.ExperienceRequired is null,
                5 => entry.Rank is null && entry.Progress is >= 0 and <= byte.MaxValue && entry.Experience is null && entry.ExperienceRequired is null,
                _ => false,
            });
        if (category == "pvp")
            return entry.State == "observed" && entry.Id is 1 or 2 && entry.Rank is >= 0 and <= byte.MaxValue &&
                entry.Experience is not null && entry.Level is null && entry.Progress is null && entry.Required is null &&
                (entry.Id == 1 ? entry.ExperienceRequired is null : entry.Experience <= ushort.MaxValue &&
                    (entry.ExperienceRequired is null || entry.ExperienceRequired is > 0 and <= ushort.MaxValue && entry.Experience <= entry.ExperienceRequired));
        if (category == "achievement_progress")
            return entry.Rank is null && entry.Level is null && entry.Experience is null &&
                entry.ExperienceRequired is null && entry.Progress is >= 0 and <= uint.MaxValue &&
                entry.Required is > 0 and <= uint.MaxValue;
        if (category == "shared_fates")
            return entry.Level is null && entry.Experience is null && entry.ExperienceRequired is null &&
                (entry.State == "unknown" ? entry.Rank is null && entry.Progress is null && entry.Required is null :
                    entry.Rank is >= 1 and <= 10 && entry.Progress is >= 0 and <= ushort.MaxValue &&
                    entry.Required is >= 0 and <= ushort.MaxValue && entry.Progress <= entry.Required);
        return entry.Rank is null && entry.Progress is null && entry.Required is null &&
            (entry.State == "unknown" ? entry.Level is null && entry.Experience is null && entry.ExperienceRequired is null :
                entry.Level is >= 1 and <= 200 && entry.Experience is not null &&
                entry.ExperienceRequired is not null && entry.Experience <= entry.ExperienceRequired);
    }

    internal static ProgressionSnapshot Unavailable(string category, string reason) =>
        new(category, DateTimeOffset.UtcNow, false, "unavailable", [], reason);

    internal static void ValidateCategory(string category)
    {
        if (!IsCategory(category)) throw new ArgumentException("Unsupported progression category.", nameof(category));
    }

    internal static bool IsCategory(string? category) =>
        category is "achievements" or "achievement_progress" or "aether_currents" or "shared_fates" or "trust" or "gathering_log" or "fishing_log" or "spearfishing_log" or "variant_notebook" or "sightseeing_log" or "hunting_log" or "challenge_log" or "battle_chocobo" or "pvp";

    internal static bool ChallengeCounterSupported(uint id) => id == 47 || id is >= 56 and <= 67;
    internal static bool ChallengeVisibleCounterSupported(uint id) => id is 11 or 12 or 33 or 34;

    internal static bool IsState(string category, string state) => category switch
    {
        "achievements" => state is "completed" or "not_completed" or "unknown",
        "achievement_progress" => state == "observed",
        "battle_chocobo" or "pvp" => state == "observed",
        "aether_currents" or "gathering_log" or "fishing_log" or "spearfishing_log" or "variant_notebook" or "sightseeing_log" or "hunting_log" or "challenge_log" => state is "completed" or "not_observed_complete" or "unknown",
        "shared_fates" or "trust" => state is "observed" or "unknown",
        _ => false,
    };

    internal static string ReadyState(string category) => category switch
    {
        "achievements" => "loaded_completion_bitmap",
        "achievement_progress" => "loaded_progress_record",
        "battle_chocobo" => "loaded_companion_observation",
        "pvp" => "visible_menu_observation",
        "aether_currents" or "gathering_log" or "fishing_log" or "spearfishing_log" or "variant_notebook" or "sightseeing_log" => "positive_observations_only",
        "shared_fates" or "trust" or "hunting_log" or "challenge_log" => "visible_menu_observation",
        _ => throw new ArgumentException("Unsupported progression category.", nameof(category)),
    };

    internal static string Source(string category) => category switch
    {
        "achievements" => "Achievement.IsLoaded/IsComplete; Achievement sheet RowId/Name",
        "achievement_progress" => "Achievement.ProgressRequestState/ProgressAchievementId/ProgressCurrent/ProgressMax; Achievement sheet RowId/Name",
        "aether_currents" => "PlayerState.IsAetherCurrentZoneComplete/IsAetherCurrentUnlocked; AetherCurrentCompFlgSet/AetherCurrent/TerritoryType/PlaceName sheets",
        "shared_fates" => "AgentFateProgress selected visible tab; FateProgressUI/TerritoryType/PlaceName sheets",
        "trust" => "AgentDawn selected visible member group; DawnMemberEntry",
        "gathering_log" => "QuestManager.IsGatheringItemGathered; GatheringNotebookList/GatheringItem/Item sheets",
        "fishing_log" => "PlayerState.IsFishCaught; FishParameter.IsInLog/RowId/Item.Name",
        "spearfishing_log" => "PlayerState.IsSpearfishCaught; SpearfishingItem.IsVisible/RowId/Item.Name",
        "variant_notebook" => "PlayerState.CompletedVVDNotebookContentsBitArray (VVDNotebookContents RowId - 1); VVDNotebookSeries/VVDNotebookContents sheets",
        "sightseeing_log" => "PlayerState.IsAdventureComplete (Adventure RowId - 0x210000); Adventure sheet RowId/Name",
        "hunting_log" => "AgentMonsterNote selected visible book/rank; MonsterNoteManager rank/target counts; MonsterNote/MonsterNoteTarget/BNpcName sheets",
        "challenge_log" => "ContentsNote loaded visible menu DisplayIds/DisplayStatuses/IsContentNoteComplete; ContentsNote/ContentsNoteCategory sheets",
        "battle_chocobo" => "UIState.Buddy.CompanionInfo; BuddyRank/BuddySkill sheets",
        "pvp" => "PvPProfile.IsLoaded; current Grand Company rank/EXP and current Series rank/EXP",
        _ => throw new ArgumentException("Unsupported progression category.", nameof(category)),
    };

    internal static string CategoryCoverage(string category) => category switch
    {
        "battle_chocobo" => "Battle chocobo client observations: row 1 rank/current EXP/next-rank EXP target (no target at rank 20); rows 2/3/4 Defender/Attacker/Healer ranks; row 5 unspent skill points in progress. No companion-specific hydration marker exists; loaded-character and stable native copies do not establish server freshness. Training, feeding, rank-cap unlocks, individual skills, racing and pedigree are excluded. Rank zero is unavailable, not proof of no companion.",
        "pvp" => "Personal PvP observations from a loaded profile. Row 1 identifies the Grand Company observed at capture with its rank and cumulative PvP EXP; row 2 is the Series rank and Series EXP observed then, with a verified public PvPSeriesLevel next-level EXP target when available. No competitive tier, seasonal leaderboard, match history, reward or claim inference. Client loading does not establish server freshness. Open PvP Profile to refresh; a stable observation is retained for 15 minutes in this login after closing. Missing data stays unknown.",
        "sightseeing_log" => "Named local Adventure catalog. Completed is a positive discovered-vista observation, keyed by Adventure RowId, " +
            "not a displayed sightseeing number. Native false remains unknown because negative-state readiness is unverified. " +
            "No menu-open requirement. Conditions, emotes, coordinates, unlocks, rewards and story text are not included; no server requests are made.",
        "hunting_log" => "Only ten tasks in the selected visible Hunting Log book/rank, including tasks hidden by its filter. " +
            "IDs are MonsterNote RowIds; rank is the selected displayed rank. Current-rank progress/required sum the validated target counters/requirements " +
            "within each task, not one enemy's kills. For completed earlier ranks, progress equals the public task requirement, inferred from positive rank advancement; " +
            "it is not a live reading of those earlier target counters. " +
            "Future ranks and mismatches remain unknown. Not observed complete is not verified incomplete. Counts cover this subset only; no Hunt bills, " +
            "rewards, availability or current server freshness are established. No independent character hydration marker exists; wait for the menu to populate.",
        "challenge_log" => "Only rows in the selected visible, loaded Challenge Log tab. Positive flags establish observed completion; unset flags are unknown. " +
            "Loaded state does not verify weekly reset receipt or server freshness. Required is the public objective target. " +
            "For completed objectives without supported counters, progress=required is inferred from the flag, not a live counter. " +
            "Partial counters cover corroborated Eureka/Verminion objectives and verified leve/retainer rows; title and ratio must match native values " +
            "in two stable samples on their matching tab. Other partial counters, rewards and claims are unknown, never zero. " +
            "No menu is opened or server data requested. Reopen after a reset; saved observations are last known, not live.",
        "achievements" => "Named local achievement catalog. Completion is read only when the main completion bitmap is loaded. " +
            "False means absent from that loaded client bitmap at capture time; no current server freshness or obtainability is implied. " +
            "Unsupported IDs remain unknown. Counters, rewards and reward claims are not included; no server request or menu opening is performed.",
        "achievement_progress" => "Only the latest loaded achievement progress record, identified by its own Achievement RowId and name, " +
            "while the Achievements menu is already visible and ready. The record may belong to a previous menu selection; " +
            "current selection and current server freshness are not verified. Progress and required are the observed counter and target, " +
            "not completion or reward-claim evidence. Other achievement counters are unavailable, never zero. " +
            "No menu is opened and no server data is requested.",
        "aether_currents" => "Named local aether-current zones. Completed is a positive current-character native observation. " +
            "Progress, when present, counts positively observed discoveries among the Required current definitions linked to that zone. " +
            "Zero means none were positively observed, not a verified empty log; fewer than Required is a lower bound, not an exact missing count. " +
            "A full discovery count alone does not establish the separate zone-completion flag. Not observed complete is not verified incomplete; " +
            "negative readiness is unverified. Required follows the installed zone membership, not a fixed expansion-wide total. " +
            "Individual names, locations, menu numbering and mount speed are not included.",
        "shared_fates" => "Only validated zones in the selected, already visible Shared FATE tab. Counts cover this observed subset, " +
            "not all regions. Rank and progress are menu observations at capture time. Unloaded or invalid data is unavailable, never zero. " +
            "No independent per-character hydration marker is available; menu observations may lag while loading. Compare after the menu has fully populated.",
        "trust" => "Only members in the selected, already visible Trust member group. Counts cover this observed subset, not every " +
            "possible member. Levels and experience are menu observations; member availability, unlocks and glamour flags remain unverified. " +
            "No independent per-character hydration marker is available; menu observations may lag while loading. Compare after the menu has fully populated.",
        "gathering_log" => "Named ordinary items referenced by the local gathering notebook, keyed by GatheringItem RowId. " +
            "Completed means a positive gathered-before observation. A native false remains unknown because log-specific negative readiness is unverified. " +
            "Counts cover this named notebook catalog only, not inventory, gathering availability, fishing, or every GatheringItem row. " +
            "No menu is opened and no server data is requested.",
        "fishing_log" => "Named FishParameter entries explicitly included in the fishing log, keyed by FishParameter RowId. " +
            "Completed means a positive caught-before observation. Native false remains unknown because negative-state readiness is unverified. " +
            "Counts cover this named log catalog only, not inventory, all fish definitions, current availability, catch size, records, bait or fishing conditions. " +
            "No menu is opened and no server data is requested.",
        "spearfishing_log" => "Named SpearfishingItem entries marked visible in the local log, keyed by SpearfishingItem RowId. " +
            "Completed means a positive caught-before observation. Native false remains unknown because negative-state readiness is unverified. " +
            "Counts cover this named visible log catalog only, not inventory, hidden entries, current availability, catch size or records. " +
            "No menu is opened and no server data is requested.",
        "variant_notebook" => "Named Variant notebook records linked by the local notebook series, keyed by VVDNotebookContents RowId. " +
            "Record numbers are positions in each series Contents list, not global row IDs. Completed means a positive notebook completion bit. " +
            "Unset bits remain unknown because notebook-specific negative readiness is unverified; no fresh server state is promised. " +
            "Counts cover this named notebook catalog only. Criterion clears, duty unlocks, reward claims, story text and route instructions are not included. " +
            "No menu is opened and no server data is requested.",
        _ => throw new ArgumentException("Unsupported progression category.", nameof(category)),
    };

    internal static bool ValidText(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);
}
