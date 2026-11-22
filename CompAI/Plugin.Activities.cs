using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CompAI;

public sealed partial class Plugin
{
    private readonly GoldSaucerReader goldSaucerReader;
    private readonly FreeCompanyReader freeCompanyReader;
    private readonly PvpSeriesRewardsReader pvpSeriesRewardsReader;
    private readonly MogpendiumReader mogpendiumReader;
    private readonly FieldRecordReader fieldRecordReader;
    private readonly PortableArchiveReader portableArchiveReader;
    private readonly MahjongReader mahjongReader;
    private readonly IslandCurrencyReader islandCurrencyReader;
    private readonly IslandProgressReader islandProgressReader;
    private readonly CosmicResearchReader cosmicResearchReader;
    private readonly IslandMaterialsReader islandMaterialsReader;
    private readonly CosmicMissionReader cosmicMissionReader;
    private readonly FreeCompanyCreditsReader freeCompanyCreditsReader;
    private readonly SelectedMacroReader selectedMacroReader;
    private readonly ActivityObservationCache activityObservations = new();
    private ulong activityOwner;
    private long? mogpendiumSeason;
    private DateTimeOffset nextActivityPoll;
    private string[] ActivityCategoriesUnderLock() => ActivityAccess.Categories(this.Access);
    private void ApplyActivityAccessChangeUnderLock(CompanionAccessSettings previous, CompanionAccessSettings next)
    {
        foreach (var category in ActivityAccess.Categories(previous).Except(ActivityAccess.Categories(next)))
            this.activityObservations.Clear(category);
        if (previous.CollectionTools && !next.CollectionTools) this.mogpendiumSeason = null;
    }
    private void ResetActivityObservationsUnderLock()
    {
        this.activityObservations.Reset();
        this.activityOwner = 0;
        this.mogpendiumSeason = null;
        this.nextActivityPoll = default;
    }
    private bool HasRecentActivityUnderLock(string category) => this.CanReadCurrentCharacter &&
        ActivityAccess.Allows(this.Access, category) && this.activityOwner == this.activeContentId &&
        this.activityObservations.HasRecent(category, DateTimeOffset.UtcNow);
    private void PollActivityObservations()
    {
        lock (this.stateGate)
        {
            if (!this.CanReadCurrentCharacter || QuestLoading()) { this.activityObservations.ResetCandidates(); return; }
            if (this.activityOwner != this.activeContentId)
            { this.activityObservations.Reset(); this.mogpendiumSeason = null; this.activityOwner = this.activeContentId; }
            var now = DateTimeOffset.UtcNow;
            if (now < this.nextActivityPoll) return;
            this.nextActivityPoll = now.AddSeconds(1);
            var categories = this.ActivityCategoriesUnderLock();
            if (categories.Length == 0) return;
            var owner = this.activeContentId;
            if (ReadLoadedCharacterId() != owner) { this.activityObservations.ResetCandidates(); return; }
            ActivitySnapshot? mogpendium = null;
            long? selectedSeason = null;
            if (this.Access.CollectionTools)
            {
                try
                {
                    using var timing = PerformanceDiagnostics.Measure("reader.activities.observe");
                    mogpendium = this.mogpendiumReader.Capture(out selectedSeason);
                }
                catch (Exception) { /* Failed or closed menus do not refresh an older observation. */ }
            }
            if (!this.CanReadCurrentCharacter || QuestLoading() || ReadLoadedCharacterId() != owner)
            { this.activityObservations.Reset(); this.mogpendiumSeason = null; return; }
            this.ApplyMogpendiumSeasonUnderLock(selectedSeason);
            foreach (var category in categories)
            {
                ActivitySnapshot reading;
                var archiveMenuObserved = false;
                var macroMenuObserved = false;
                try
                {
                    using var timing = PerformanceDiagnostics.Measure("reader.activities.observe");
                    reading = category switch
                    {
                        "field_record" => this.fieldRecordReader.Capture() ?? ActivitySnapshot.Unavailable(category, "Open Field Record and let the selected entry settle to refresh; saved observations last 15 minutes."),
                        "portable_archive" => this.portableArchiveReader.Capture(out archiveMenuObserved) ?? ActivitySnapshot.Unavailable(category,
                            archiveMenuObserved ? "The current Portable Archive selection could not be read. Select an entry and let it settle to refresh."
                                : "Open a supported Portable Archive entry and let it settle to refresh; saved observations last 15 minutes."),
                        "mahjong" => this.mahjongReader.Capture(),
                        "island_currencies" => CaptureIslandCurrencies(this.islandCurrencyReader.Capture()),
                        "island_progress" => CaptureIslandProgress(this.islandProgressReader.Capture()),
                        "cosmic_research" => CaptureCosmicResearch(this.cosmicResearchReader.Capture(ClientState.TerritoryType)),
                        "island_materials" => CaptureIslandMaterials(this.islandMaterialsReader.Capture()),
                        "cosmic_missions" => CaptureCosmicMissions(this.cosmicMissionReader.Capture(ClientState.TerritoryType)),
                        "free_company" => this.freeCompanyReader.Capture(),
                        "free_company_details" => this.freeCompanyReader.CaptureDetails(),
                        "free_company_credits" => CaptureFreeCompanyCredits(this.freeCompanyCreditsReader.Capture()),
                        "selected_macro" => CaptureSelectedMacro(this.selectedMacroReader.Capture(out macroMenuObserved)),
                        "pvp_series_rewards" => this.pvpSeriesRewardsReader.Capture(),
                        _ when MogpendiumContract.IsCategory(category) => mogpendium?.Category == category ? mogpendium :
                            ActivitySnapshot.Unavailable(category, category == "mogpendium_standard"
                                ? "No verified Standard Objectives view. Select a supported dungeon such as Halatali (Hard) and let it settle to refresh the full public catalog; observations last 15 minutes."
                                : "No verified observation of this Mogpendium tab. Open it and let it settle to refresh; saved observations last 15 minutes."),
                        _ => this.goldSaucerReader.Capture(category),
                    };
                }
                catch (Exception) { reading = ActivitySnapshot.Unavailable(category, "The menu could not be read safely. Reopen it to refresh."); }
                if (!this.CanReadCurrentCharacter || QuestLoading() || ReadLoadedCharacterId() != owner)
                { this.activityObservations.Reset(); this.mogpendiumSeason = null; return; }
                this.ObserveActivityUnderLock(reading, archiveMenuObserved || macroMenuObserved);
            }
        }
    }
    private void ObserveActivityUnderLock(ActivitySnapshot reading, bool selectionMenuObserved)
    {
        if (this.activityObservations.Observe(reading, replaceSelection:
                reading.Category is PortableArchiveContract.Category or SelectedMacroContract.Category && selectionMenuObserved))
            this.preparedQuestion = null;
    }
    private static ActivitySnapshot CaptureIslandCurrencies(IslandCurrencyObservation? observed) => observed is null
        ? ActivitySnapshot.Unavailable("island_currencies", "Open Islekeep's Index or the island export menu and let it settle to refresh cowries; saved observations last 15 minutes.")
        : new("island_currencies", observed.CapturedAtUtc, true,
        [
            new(1, "balances", observed.Seafarer.Name, Value: observed.Seafarer.Quantity, Unit: "cowries",
                ReferenceId: observed.Seafarer.ItemId, ReferenceKind: "item"),
            new(2, "balances", observed.Islander.Name, Value: observed.Islander.Quantity, Unit: "cowries",
                ReferenceId: observed.Islander.ItemId, ReferenceKind: "item"),
        ]);
    private static ActivitySnapshot CaptureFreeCompanyCredits(FreeCompanyCreditsObservation? observed) => observed is null
        ? ActivitySnapshot.Unavailable("free_company_credits", "Open Free Company Topics or the Company Credit item shop and let it settle to refresh the balance; saved observations last 15 minutes.")
        : new("free_company_credits", observed.CapturedAtUtc, true,
            [new(1, "credits", observed.Source == FreeCompanyCreditsSource.Topics
                ? "Company credits shown on Topics" : "Company credits shown in shop", Value: observed.Credits, Unit: "company credits")]);
    private static ActivitySnapshot CaptureSelectedMacro(SelectedMacroObservation? observed)
    {
        if (observed is null) return ActivitySnapshot.Unavailable(SelectedMacroContract.Category,
            "Open User Macros, select an Individual or Shared macro and let it settle; saved observations last 15 minutes.");
        if (observed.Set is < 0 or > 1 || observed.Index is < 0 or > 99 || observed.Lines.Count != 15)
            return ActivitySnapshot.Unavailable(SelectedMacroContract.Category, "The selected macro could not be read safely.");
        var group = observed.Set == 0 ? "individual" : "shared";
        var rows = new List<ActivityEntry>(16) { new(1, group, "Selected macro", Text: observed.Name, Slot: observed.Index + 1) };
        rows.AddRange(observed.Lines.Select((line, index) => new ActivityEntry((uint)index + 2, group,
            $"Line {index + 1}", Text: line, Slot: index + 1)));
        return new(SelectedMacroContract.Category, observed.CapturedAtUtc, true, rows);
    }
    private static ActivitySnapshot CaptureIslandMaterials(IslandMaterialsObservation? observed) => observed is null
        ? ActivitySnapshot.Unavailable("island_materials", "Open Isleventory and let its material list settle to refresh; saved observations last 15 minutes.")
        : new("island_materials", observed.CapturedAtUtc, true, observed.Items.Select(item =>
            new ActivityEntry(item.PouchRowId + 1, $"pouch_category_{item.CategoryId}", item.Name,
                Text: item.CategoryName, Value: item.Quantity, Unit: "items", ReferenceId: item.ItemId, ReferenceKind: "item")));
    private static ActivitySnapshot CaptureCosmicMissions(CosmicMissionObservation? observed) => observed is null
        ? ActivitySnapshot.Unavailable("cosmic_missions", "Open Cosmic Mission Log in a loaded Cosmic Exploration zone and let its list settle; saved observations last 15 minutes.")
        : new("cosmic_missions", observed.CapturedAtUtc, true, observed.Entries.OrderBy(entry => entry.MissionId).Select((entry, index) =>
            new ActivityEntry((uint)index + 1, $"class_job_{entry.ClassJobId}", $"{entry.JobName}: {entry.Name}",
                Text: entry.Medal, ReferenceId: entry.MissionId, ReferenceKind: "cosmic_mission", Slot: (int)entry.ClassJobId)));
    private static ActivitySnapshot CaptureIslandProgress(IslandProgressObservation? observed) => observed is null
        ? ActivitySnapshot.Unavailable("island_progress", "Open Islekeep's Index and let it settle to refresh rank/EXP; saved observations last 15 minutes.")
        : new("island_progress", observed.CapturedAtUtc, true,
        [
            new(1, "progression", "Sanctuary rank", Text: observed.IsMaximumRank ? "Maximum rank" : null,
                Value: observed.Rank, Unit: "rank"),
            new(2, "progression", "Island experience", Text: observed.IsMaximumRank ? "No further rank" : null,
                Value: observed.Experience, Required: observed.RequiredExperience, Unit: "experience"),
        ]);
    private static ActivitySnapshot CaptureCosmicResearch(CosmicResearchObservation? observed)
    {
        if (observed is null) return ActivitySnapshot.Unavailable("cosmic_research",
            "Open Cosmic Research Information in a loaded Cosmic Exploration zone and let it settle; saved observations last 15 minutes.");
        var entries = new List<ActivityEntry>(99);
        foreach (var job in observed.Jobs)
        {
            var first = (job.ToolClassId - 1) * 10 + 1;
            var group = $"class_job_{job.ClassJobId}";
            entries.Add(new(first, group, $"{job.Name}: current research stage", Text: job.CurrentStageName,
                Value: job.CurrentStage, Unit: "stage", ReferenceId: job.ClassJobId, ReferenceKind: "class_job"));
            entries.Add(new(first + 1, group, $"{job.Name}: unlocked research stage", Text: job.UnlockedStageName,
                Value: job.UnlockedStage, Unit: "stage", ReferenceId: job.ClassJobId, ReferenceKind: "class_job"));
            foreach (var value in job.Data)
                entries.Add(new(first + 1 + value.Type, group, $"{job.Name}: {value.Name}", Value: value.Quantity,
                    Unit: "research", ReferenceId: job.ClassJobId, ReferenceKind: "class_job", Slot: (int)value.Type));
        }
        return new("cosmic_research", observed.CapturedAtUtc, true, entries);
    }
    private void ApplyMogpendiumSeasonUnderLock(long? season)
    {
        if (season is null or < 1 or > ushort.MaxValue || this.mogpendiumSeason == season) return;
        this.activityObservations.ResetMogpendium();
        this.mogpendiumSeason = season;
        this.preparedQuestion = null;
    }
    private Func<JsonElement, string>? CreateActivityQueryUnderLock(Func<DateTimeOffset>? clock = null)
    {
        if (!this.CanReadCurrentCharacter) return null;
        var categories = this.ActivityCategoriesUnderLock();
        if (categories.Length == 0) return null;
        clock ??= () => DateTimeOffset.UtcNow;
        var owner = this.activeContentId;
        var session = this.contextLoadEpoch;
        var access = this.valuationAccessEpoch;
        var at = clock();
        var frozen = categories.ToDictionary(category => category, category => this.activityOwner == owner
            ? this.activityObservations.Freeze(category, at) : ActivitySnapshot.Unavailable(category, "No recent observation for this character. Open the relevant menu to refresh."), StringComparer.Ordinal);
        var lookups = frozen.ToDictionary(row => row.Key, row => new ActivityLookup(row.Value), StringComparer.Ordinal);
        return arguments =>
        {
            var options = ActivityLookup.ParseArguments(arguments);
            var category = options.Category;
            var continues = this.companion.CaptureQuestionContinuation();
            lock (this.stateGate)
            {
                if (!categories.Contains(category) || !ActivityAccess.Allows(this.Access, category) || !this.CanReadCurrentCharacter ||
                    QuestLoading() || this.activeContentId != owner || this.contextLoadEpoch != session ||
                    this.valuationAccessEpoch != access || !continues()) throw new OperationCanceledException();
                var value = frozen[category]; var now = clock();
                if (value.Available && (now < value.CapturedAtUtc || now - value.CapturedAtUtc >= ActivityObservationCache.Lifetime))
                    throw new IOException("Activity observation expired. Open its menu to refresh and send again.");
                return JsonSerializer.Serialize(lookups[category].Query(options));
            }
        };
    }
}
