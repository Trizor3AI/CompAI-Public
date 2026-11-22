using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace CompAI;

internal sealed record ReaderStatusRow(string Name, string State, string Details);
internal sealed record ReaderObservation(bool Available, string Details, DateTimeOffset ReadAt,
    ulong Owner, int Session, int AreaEpoch);

public sealed partial class Plugin
{
    // Metadata only; opening settings never captures game state, sends a question or writes a cache.
    private readonly Dictionary<string, ReaderObservation> readerObservations = new(StringComparer.Ordinal);

    private Func<JsonElement, string>? TrackReader(string name, Func<JsonElement, string>? query)
    {
        if (query is null) return null;
        var owner = this.activeContentId;
        var session = this.contextLoadEpoch;
        var area = this.currencyAreaEpoch;
        var accessEpoch = this.valuationAccessEpoch;
        return arguments =>
        {
            var result = query(arguments);
            // Status must never change the lookup result or permit an obsolete callback to restore metadata.
            try
            {
                using var parsed = JsonDocument.Parse(result);
                var root = parsed.RootElement;
                var key = arguments.TryGetProperty("category", out var category) && category.ValueKind == JsonValueKind.String
                    ? category.GetString()! : name;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("available", out var flag) ||
                    flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return result;
                var available = flag.ValueKind == JsonValueKind.True;
                var detail = root.TryGetProperty(available ? "coverage" : "unavailable_reason", out var value) &&
                    value.ValueKind == JsonValueKind.String ? value.GetString()! :
                    available ? "Last lookup returned data; individual entries may still be unknown." : "The last lookup was unavailable. Check the required game menu.";
                lock (this.stateGate)
                {
                    if (!this.contextDisposed && this.activeContentId == owner && this.contextLoadEpoch == session &&
                        this.currencyAreaEpoch == area && this.valuationAccessEpoch == accessEpoch && key.Length <= 64 &&
                        (this.readerObservations.ContainsKey(key) || this.readerObservations.Count < 64))
                        this.readerObservations[key] = new(available, detail.Length <= 1000 ? detail : detail[..1000],
                            DateTimeOffset.UtcNow, owner, session, area);
                }
            }
            catch (JsonException) { }
            return result;
        };
    }

    internal IReadOnlyDictionary<string, ReaderStatusRow> ReaderStatuses()
    {
        lock (this.stateGate)
        {
            var rows = new Dictionary<string, ReaderStatusRow>(StringComparer.Ordinal);
            var a = this.Access;
            var savedOwner = this.CanUseSavedProgressionUnderLock;
            var canRead = this.CanReadCurrentCharacter;
            void Add(bool enabled, string key, string label, string action, bool menu = false, bool saved = false, bool recent = false)
            {
                if (!enabled) return;
                var state = recent ? "Recent" : saved ? "Saved" : canRead ? menu ? "Open to refresh" : "On demand" : "Log in";
                var detail = action;
                if (recent) detail = "Last-known recent observations; unseen entries remain unknown. " + detail;
                else if (saved) detail = "Last-known saved observations; unseen entries remain unknown. " + detail;
                else if (!canRead) detail = "Log in and let the character finish loading. " + detail;
                else if (this.readerObservations.TryGetValue(key, out var observed) && observed.Owner == this.activeContentId &&
                    observed.Session == this.contextLoadEpoch && observed.AreaEpoch == this.currencyAreaEpoch)
                {
                    state = observed.Available ? "Read" : "Unavailable";
                    detail = observed.Details + "\n\nLast lookup: " + observed.ReadAt.ToLocalTime().ToString("g") +
                        ". This is a lookup result, not a live readiness check.\n" + action;
                }
                rows.Add(key, new(label, state, detail));
            }
            bool Stored(string category) => savedOwner && this.observedStorage.Any(c => c.Category == category);
            Add(a.CharacterContext, "character_details", "Character and equipment", "Ask about your character, jobs or equipment.");
            Add(a.Location, "location", "Location", "Ask about your current zone or world.");
            Add(a.NpcDialogue, "npc_dialogue", "NPC dialogue", "Speak with an NPC to collect permitted dialogue.");
            Add(a.TargetInfo, "npc_target", "Selected target", "Select the target, then ask about it.");
            Add(a.SelectedMacro, "selected_macro", "Selected macro", "Open User Macros and select one macro to refresh its name and 15 lines. Used only when the assistant requests it; retained for 15 minutes in this login. No other macros are read, and macros are never executed.", true,
                recent: this.HasRecentActivityUnderLock("selected_macro"));
            Add(a.Inventory, "inventory", "Inventory", "Carried bags update as you play.", saved: savedOwner && this.snapshot is not null);
            Add(a.Currencies, "currencies", "Currencies", "Ask about a supported currency. Cosmic credits need the loaded exploration zone. Islekeep's Index or the island export menu refreshes cowries, retained for 15 minutes; other activity balances may be unsupported." +
                (this.HasRecentActivityUnderLock("island_currencies") ? " A recent island cowrie observation is available." : ""));
            Add(a.Retainers, "retainers", "Retainers", "Open the retainer inventory or sale list to refresh observed pages.", true, Stored("retainers"));
            Add(a.Saddlebag, "saddlebag", "Saddlebags", "Open the saddlebag to refresh observed contents.", true, Stored("saddlebag"));
            Add(a.Housing, "housing", "Housing", "Open the appropriate furnishing menu to refresh this property's observations.", true, Stored("housing"));
            Add(a.FcChest, "fc_chest", "FC chest", "Open the chest and visit the tabs you want refreshed.", true, Stored("fc_chest"));
            Add(a.Armoury, "armoury", "Armoury Chest", "Loaded Armoury slots refresh automatically.", saved: Stored("armoury"));
            Add(a.Quests, "quests", "Quest journal", "Quest observations update locally while playing.", saved: savedOwner && this.quests.View.Snapshot is not null);
            Add(a.Duties, "duties", "Duties", "Ask about supported duty unlocks or clears.");
            Add(a.GcSupply, "gc_supply", "GC deliveries", "Open Supply or Provisioning to refresh. Observations last 15 minutes in this login; completion and bonuses remain unknown.", true,
                recent: this.HasRecentReaderUnderLock("gc_supply"));
            Add(a.Collections, "minions", "Minions", "Ask about a named minion; unconfirmed entries remain unknown.");
            Add(a.Collections, "mounts", "Mounts", "Ask about a named collectible. Unconfirmed unlocks stay unknown.");
            Add(a.FashionAccessories, "fashion_accessories", "Fashion accessories", "Ask about a named accessory.");
            Add(a.Facewear, "facewear", "Facewear", "Ask about a named style.");
            Add(a.Emotes, "emotes", "Emotes", "Ask about a named emote. Unlock does not establish current usability.");
            Add(a.Orchestrion, "orchestrion", "Orchestrion", "Ask about registered music, separately from physical rolls.");
            Add(a.TripleTriad, "triple_triad", "Triple Triad cards", "Ask about registered cards; unconfirmed cards remain unknown.");
            Add(a.CollectionTools, "collection_tools", "Collection menu access", "Observed tool availability only; this does not read each tool's contents.");
            foreach (var (category, label) in new[] {
                ("mogpendium_standard", "Mogpendium Standard Objectives"), ("mogpendium_weekly", "Mogpendium Weekly Objectives"),
                ("mogpendium_minimog", "Mogpendium Minimog Challenges"), ("mogpendium_ultimog", "Mogpendium Ultimog Challenge") })
                Add(a.CollectionTools, category, label,
                    "Open this Mogpendium tab and let it settle to refresh. Retained for 15 minutes in this login; switching events clears the previous event's tabs. " +
                    (category switch
                    {
                        "mogpendium_standard" => "Select a supported dungeon such as Halatali (Hard) to refresh the full public catalog. Unsupported selected views remain unavailable; no personal completion.",
                        "mogpendium_weekly" => "Observed week/duty pairs and supported selected details. Open the category and let it settle; an individual challenge click is not required. Completion and reward claims remain unknown.",
                        "mogpendium_minimog" => "Observed week/choice labels and supported selected details. Open the category and let it settle; an individual challenge click is not required. Generic labels do not reveal hidden instructions or rewards. Completion and claims remain unknown.",
                        _ => "Selected objective and displayed reward amounts only; progress and reward claims are unknown.",
                    }), true,
                    recent: this.HasRecentActivityUnderLock(category));
            Add(a.CollectionTools, "field_record", "Field Record", "Open a selected Field Record entry and let it settle. Selected number/name/registration and displayed total only; retained 15 minutes in this login. Unseen entries remain unknown.", true,
                recent: this.HasRecentActivityUnderLock("field_record"));
            Add(a.CollectionTools, "portable_archive", "Portable Archive", "Open a supported Portable Archive entry and let it settle. Selected title only; retained 15 minutes in this login. Other entries and complete ownership remain unknown.", true,
                recent: this.HasRecentActivityUnderLock("portable_archive"));
            Add(a.Mahjong, "mahjong", "Mahjong", "Open Gold Saucer → Mahjong. Displayed personal statistics; retained 15 minutes in this login.", true,
                recent: this.HasRecentActivityUnderLock("mahjong"));
            Add(a.OccultRecords, "occult_records", "Occult Records", "Ask about unlocked records; unconfirmed entries remain unknown.");
            Add(a.TripleTriadDecks, "triple_triad_decks", "Triple Triad decks", "Open Gold Saucer → Card Decks; retained for 15 minutes in this login.", true,
                recent: this.HasRecentActivityUnderLock("triple_triad_decks"));
            Add(a.RaceChocobo, "race_chocobo", "Racing chocobo", "Open Gold Saucer → Chocobo; retained for 15 minutes in this login.", true,
                recent: this.HasRecentActivityUnderLock("race_chocobo"));
            Add(a.Verminion, "verminion", "Verminion hotbar", "Open Gold Saucer → Lord of Verminion; retained for 15 minutes in this login.", true,
                recent: this.HasRecentActivityUnderLock("verminion"));
            Add(a.FreeCompany, "free_company", "Free Company info", "Open the main Free Company menu for profile/board. Topics or the Company Credit item shop supplies the displayed balance. Observations last 15 minutes in this login; the balance is not joined to a named company or spending permission.", true,
                recent: this.HasRecentActivityUnderLock("free_company") || this.HasRecentActivityUnderLock("free_company_details") ||
                    this.HasRecentActivityUnderLock("free_company_credits"));
            Add(a.BattleChocobo, "battle_chocobo", "Battle chocobo", "Ask about rank, EXP, role ranks or unspent skill points.");
            Add(a.Pvp, "pvp", "PvP progression", "Open PvP Profile to refresh. Observations last 15 minutes in this login.", true,
                recent: this.HasRecentReaderUnderLock("pvp"));
            Add(a.IslandProgress, "island_progress", "Island Sanctuary progress", "Open the visible Islekeep's Index for displayed Sanctuary rank and EXP. Observations last 15 minutes in this login and do not establish island ownership. Materials are not included; cowries use Currencies.", true,
                recent: this.HasRecentActivityUnderLock("island_progress"));
            Add(a.CosmicResearch, "cosmic_research", "Cosmic research", "Open Cosmic Research Information to refresh all 11 jobs' stages and banked research. Retained for 15 minutes after closing in this login. Required amounts, caps, rates and upgrade eligibility are not established.", true,
                recent: this.HasRecentActivityUnderLock("cosmic_research"));
            Add(a.IslandMaterials, "island_materials", "Island materials", "Open Isleventory to refresh discovered materials and Felicitous Token. Kept for 15 minutes in this login. Key tools, other tokens, undiscovered items, a complete inventory and island ownership are not established.", true,
                recent: this.HasRecentActivityUnderLock("island_materials"));
            Add(a.CosmicMissions, "cosmic_missions", "Cosmic mission medals", "Browse Cosmic Mission Log views to retain observed Silver and Gold medals across jobs and planets. Up to 512 mission/job entries; each lasts 15 minutes after observation in this login. Planet labels and unvisited or missing medals remain unknown.", true,
                recent: this.HasRecentActivityUnderLock("cosmic_missions"));
            Add(a.Achievements, "achievements", "Achievements", "Open Achievements to load completion, then ask about a named entry.", true);
            Add(a.Achievements, "achievement_progress", "Achievement counters", "Select the entry and let its counter load. The counter's own achievement is retained for 15 minutes in this login.", true,
                recent: this.HasRecentReaderUnderLock("achievement_progress"));
            Add(a.AetherCurrents, "aether_currents", "Aether current zones", "Ask about zone completion and confirmed discoveries. Unconfirmed currents remain unknown.");
            Add(a.SharedFates, "shared_fates", "Shared FATE", "Open each page and let it settle to refresh.", true,
                savedOwner && this.observedProgression.Any(p => p.Category == "shared_fates"));
            Add(a.Trust, "trust", "Trust", "Open each group and let it settle to refresh.", true,
                savedOwner && this.observedProgression.Any(p => p.Category == "trust"));
            Add(a.VariantNotebook, "variant_notebook", "Variant notebook", "Ask about a series or record; unconfirmed entries remain unknown.");
            Add(a.BlueMage, "blue_mage_spells", "Blue Mage spellbook", "Ask by spell name or displayed spell number.");
            Add(a.Bestiary, "beastmaster_bestiary", "Master's Bestiary", "Open the normal Bestiary to refresh. Observations last 15 minutes in this login.", true,
                recent: this.HasRecentReaderUnderLock("beastmaster_bestiary"));
            Add(a.CraftingLog, "crafting_log", "Crafting log", "Ask about a recipe by name or Recipe ID.");
            Add(a.FishingLog, "fishing_log", "Fishing log", "Ask about a named fish; unconfirmed caught-before flags stay unknown.");
            Add(a.FishingLog, "spearfishing_log", "Spearfishing log", "Ask about a named spearfish; unconfirmed caught-before flags stay unknown.");
            Add(a.GatheringLog, "gathering_log", "Gathering log", "Ask about a mining or botany log item.");
            Add(a.Shops, "shops", "NPC shops and exchanges", "Open a supported shop or exchange to refresh. Only the last shop is retained, for 5 minutes. Unsupported costs remain unknown.", true,
                recent: this.HasSavedMenuUnderLock("shops"));
            Add(a.Glamour, "armoire", "Armoire", "Open the armoire and let it load to refresh saved observations.", true, this.HasSavedMenuUnderLock("armoire"));
            Add(a.Glamour, "glamour_dresser", "Glamour dresser", "Open the dresser and let it load to refresh saved observations.", true, this.HasSavedMenuUnderLock("glamour_dresser"));
            Add(a.Glamour, "glamour_plates", "Glamour plates", "Open the plate editor with edits saved to refresh. Plate references are not extra owned items.", true, this.HasSavedMenuUnderLock("glamour_plates"));
            Add(a.HuntingLog, "hunting_log", "Hunting log", "Open each book and rank to refresh its saved observations.", true,
                savedOwner && this.observedProgression.Any(p => p.Category == "hunting_log" && ProgressionMenuCache.IsUsable(p, DateTimeOffset.UtcNow)));
            Add(a.SightseeingLog, "sightseeing_log", "Sightseeing log", "Ask about named sightseeing entries.");
            Add(a.ChallengeLog, "challenge_log", "Challenge log", "Open each tab to refresh. Observations last 15 minutes, up to Tuesday 08:00 UTC reset.", true,
                recent: savedOwner && this.observedProgression.Any(p => p.Category == "challenge_log" && ProgressionMenuCache.IsUsable(p, DateTimeOffset.UtcNow)));
            // A permission can cover several readers. Preserve their distinct evidence in the hover
            // rather than letting one successful lookup imply that the entire permission was read.
            void Group(string key, string label, params string[] categories)
            {
                var members = categories.Where(rows.ContainsKey).Select(category => rows[category]).ToArray();
                if (members.Length == 0) return;
                var state = members.All(row => row.State == members[0].State) ? members[0].State : "Mixed";
                rows[key] = new(label, state, string.Join("\n\n", members.Select(row =>
                    row.Name + ": " + row.State + "\n" + row.Details)));
            }
            Group("collections", "Mounts and minions", "mounts", "minions");
            Group("collection_tools", "Collection tools and Mogpendium", "collection_tools", "mogpendium_standard",
                "mogpendium_weekly", "mogpendium_minimog", "mogpendium_ultimog");
            Group("achievements", "Achievements", "achievements", "achievement_progress");
            Group("glamour", "Glamour storage and plates", "armoire", "glamour_dresser", "glamour_plates");
            Group("fishing_log", "Fishing and spearfishing logs", "fishing_log", "spearfishing_log");
            return rows;
        }
    }
}
