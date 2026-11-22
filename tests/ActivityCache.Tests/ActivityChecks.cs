using System.Text.Json;

namespace CompAI;

public sealed partial class Plugin
{
    private static readonly DateTimeOffset ObservationTime = DateTimeOffset.Parse("2026-10-23T16:00:00Z");
    private static readonly (string Category, Func<CompanionAccessSettings, bool, CompanionAccessSettings> Grant,
        Action<Plugin, bool> Set)[] ActivityCases =
    [
        ("island_progress", (s,v)=>s with { IslandProgress=v }, (p,v)=>p.SetAccess(false,false,false,islandProgress:v)),
        ("cosmic_research", (s,v)=>s with { CosmicResearch=v }, (p,v)=>p.SetAccess(false,false,false,cosmicResearch:v)),
        ("island_materials", (s,v)=>s with { IslandMaterials=v }, (p,v)=>p.SetAccess(false,false,false,islandMaterials:v)),
        ("cosmic_missions", (s,v)=>s with { CosmicMissions=v }, (p,v)=>p.SetAccess(false,false,false,cosmicMissions:v)),
        ("selected_macro", (s,v)=>s with { SelectedMacro=v }, (p,v)=>p.SetAccess(false,false,false,selectedMacro:v)),
        ("island_currencies", (s,v)=>s with { Currencies=v }, (p,v)=>p.SetAccess(false,false,false,currencies:v)),
        ("mahjong", (s,v)=>s with { Mahjong=v }, (p,v)=>p.SetAccess(false,false,false,mahjong:v)),
        ("field_record", (s,v)=>s with { CollectionTools=v }, (p,v)=>p.SetAccess(false,false,false,collectionTools:v)),
        ("portable_archive", (s,v)=>s with { CollectionTools=v }, (p,v)=>p.SetAccess(false,false,false,collectionTools:v)),
        ("triple_triad_decks", (s,v)=>s with { TripleTriadDecks=v }, (p,v)=>p.SetAccess(false,false,false,tripleTriadDecks:v)),
        ("race_chocobo", (s,v)=>s with { RaceChocobo=v }, (p,v)=>p.SetAccess(false,false,false,raceChocobo:v)),
        ("verminion", (s,v)=>s with { Verminion=v }, (p,v)=>p.SetAccess(false,false,false,verminion:v)),
        ("free_company", (s,v)=>s with { FreeCompany=v }, (p,v)=>p.SetAccess(false,false,false,freeCompany:v)),
        ("free_company_details", (s,v)=>s with { FreeCompany=v }, (p,v)=>p.SetAccess(false,false,false,freeCompany:v)),
        ("free_company_credits", (s,v)=>s with { FreeCompany=v }, (p,v)=>p.SetAccess(false,false,false,freeCompany:v)),
        ("pvp_series_rewards", (s,v)=>s with { Pvp=v }, (p,v)=>p.SetAccess(false,false,false,pvp:v)),
        ("mogpendium_standard", (s,v)=>s with { CollectionTools=v }, (p,v)=>p.SetAccess(false,false,false,collectionTools:v)),
        ("mogpendium_weekly", (s,v)=>s with { CollectionTools=v }, (p,v)=>p.SetAccess(false,false,false,collectionTools:v)),
        ("mogpendium_minimog", (s,v)=>s with { CollectionTools=v }, (p,v)=>p.SetAccess(false,false,false,collectionTools:v)),
        ("mogpendium_ultimog", (s,v)=>s with { CollectionTools=v }, (p,v)=>p.SetAccess(false,false,false,collectionTools:v)),
    ];

    internal static void RunActivityChecks()
    {
        Check(ActivityAccess.Categories(new()).Length == 0, "all activities default off");
        Check(ActivityAccess.Categories(new(Collections: true, TripleTriad: true, BattleChocobo: true,
            FcChest: true, OccultRecords: true, YokaiCollection: true)).Length == 0,
            "related and collection grants cannot imply activity access");
        foreach (var (category, grant, _) in ActivityCases)
            Check(ActivityAccess.Categories(grant(new(), true)).SequenceEqual(category.StartsWith("free_company", StringComparison.Ordinal)
                ? ["free_company", "free_company_details", "free_company_credits"] : (MogpendiumContract.IsCategory(category) || category is "field_record" or "portable_archive")
                    ? ["field_record", "portable_archive", "mogpendium_standard", "mogpendium_weekly", "mogpendium_minimog", "mogpendium_ultimog"] : [category]), "explicit category grant: " + category);
        Console.WriteLine("PASS activity mapping preserves independent FC and Collection tools grants");

        // Cosmic rows have independent timestamps and detached aggregation; its focused checks cover those semantics.
        foreach (var (category, _, _) in ActivityCases.Where(item => item.Category != "cosmic_missions"))
        {
            var cache = new ActivityObservationCache();
            Check(!cache.Observe(Observed(category, ObservationTime)), "first observation is candidate only");
            Check(!cache.Observe(Observed(category, ObservationTime.AddMilliseconds(249))), "too-close observation cannot publish");
            Check(!cache.Freeze(category, ObservationTime.AddMilliseconds(249)).Available, "candidate is unavailable");
            var reading = Observed(category, ObservationTime.AddMilliseconds(500));
            Check(cache.Observe(reading), "settled pair publishes");
            var savedAt = ObservationTime.AddMilliseconds(500);
            var saved = cache.Freeze(category, savedAt);
            Check(saved.LastObserved && saved.CapturedAtUtc == savedAt, "last-known snapshot keeps exact capture time");
            Check(ReferenceEquals(reading.Entries, saved.Entries) && !reading.LastObserved,
                "promotion shares only validated immutable rows without changing the current observation");
            Throws<ArgumentException>(() => cache.Observe(saved));
            Check(!cache.Observe(ActivitySnapshot.Unavailable(category, "Synthetic menu closed")), "closing is not a new observation");
            Check(cache.Freeze(category, savedAt.AddMinutes(2)).CapturedAtUtc == savedAt, "closing retains age");
            Check(cache.HasRecent(category, savedAt.AddMinutes(15).AddTicks(-1)), "unexpired boundary");
            Check(!cache.HasRecent(category, savedAt.AddMinutes(15)), "expires exactly at 15 minutes");
            Check(!cache.HasRecent(category, savedAt.AddTicks(-1)), "backward clock rejects future observation");
            cache.ResetCandidates();
            Check(!cache.Observe(Observed(category, savedAt.AddSeconds(3))), "candidate reset needs new pair");
            Check(cache.Freeze(category, savedAt.AddSeconds(3)).CapturedAtUtc == savedAt, "candidate-only reset retains old observation age");
            cache.Reset();
            Check(!cache.Freeze(category, savedAt).Available && !cache.Observe(Observed(category, savedAt)), "full reset clears saved and candidate");
        }
        Console.WriteLine("PASS activity caches require settling, retain after close, expire and reset without extending age");

        foreach (var category in new[] { "portable_archive", "selected_macro" })
        {
            using var folder = new ActivityTestFolder();
            var store = new CompanionAccessSettingsStore(folder.File);
            Check(store.Save(new(CollectionTools: true, SelectedMacro: true)), "persist selection grants");
            var plugin = new Plugin(store);
            plugin.Seed(category, ObservationTime);
            var now = ObservationTime.AddSeconds(3);
            var rows = Observed(category, now).Entries.Select(row => row.Id == 1
                ? row with { Text = "A different selected entry" } : row).ToArray();
            plugin.preparedQuestion = new object();
            plugin.ObserveActivityUnderLock(new(category, now, true, rows), selectionMenuObserved: true);
            Check(plugin.preparedQuestion is null && !plugin.activityObservations.Freeze(category, now).Available,
                "new open selection immediately invalidates old saved entry and prepared question while settling");
            plugin.ObserveActivityUnderLock(new(category, now.AddSeconds(1), true, rows), selectionMenuObserved: true);
            var saved = plugin.activityObservations.Freeze(category, now.AddSeconds(1));
            Check(saved.Available && saved.LastObserved && saved.Entries[0].Text == rows[0].Text,
                "second stable selection sample replaces the prior entry");
            plugin.preparedQuestion = new object();
            plugin.ObserveActivityUnderLock(ActivitySnapshot.Unavailable(category, "Synthetic closed menu"), selectionMenuObserved: false);
            Check(plugin.preparedQuestion is not null && plugin.activityObservations.Freeze(category, now.AddMinutes(1)).CapturedAtUtc == saved.CapturedAtUtc,
                "closing after successful selection retains that exact observation without refreshing age");
            plugin.ObserveActivityUnderLock(ActivitySnapshot.Unavailable(category, "Synthetic unreadable open selection"), selectionMenuObserved: true);
            Check(plugin.preparedQuestion is null && !plugin.activityObservations.Freeze(category, now.AddMinutes(1)).Available,
                "unreadable open selection cannot silently fall back to old entry");
            plugin.ObserveActivityUnderLock(ActivitySnapshot.Unavailable(category, "Synthetic closed menu"), selectionMenuObserved: false);
            Check(!plugin.activityObservations.Freeze(category, now.AddMinutes(2)).Available,
                "closing an unreadable selection does not resurrect discarded entry");
        }
        Console.WriteLine("PASS Archive and macro selection changes invalidate stale fallback and prepared questions, while close retains a valid selection");

        {
            var cache = new ActivityObservationCache();
            const string category = "triple_triad_decks";
            Check(!cache.Observe(Observed(category, ObservationTime)), "refresh first candidate");
            Check(cache.Observe(Observed(category, ObservationTime.AddSeconds(1))), "refresh stable pair");
            var original = cache.Freeze(category, ObservationTime.AddSeconds(1));
            var later = Observed(category, ObservationTime.AddSeconds(2));
            Check(cache.Observe(later), "unchanged stable content still refreshes capture time");
            var refreshed = cache.Freeze(category, ObservationTime.AddSeconds(2));
            Check(refreshed.CapturedAtUtc == later.CapturedAtUtc && ReferenceEquals(refreshed.Entries, later.Entries),
                "refresh promotes the current validated reading");
            Check(original.CapturedAtUtc == ObservationTime.AddSeconds(1) && !ReferenceEquals(original, refreshed),
                "frozen earlier snapshot keeps its own capture time");
            Check(!cache.Observe(ActivitySnapshot.Unavailable(category, "Synthetic menu closed")), "closed menu cannot refresh");
            Check(cache.HasRecent(category, later.CapturedAtUtc.AddMinutes(15).AddTicks(-1)) &&
                !cache.HasRecent(category, later.CapturedAtUtc.AddMinutes(15)), "latest observation still expires exactly");
        }
        Console.WriteLine("PASS unchanged stable observations refresh age while previous frozen snapshots retain their timestamp");

        foreach (var (category, grant, set) in ActivityCases)
        {
            using var folder = new ActivityTestFolder();
            var store = new CompanionAccessSettingsStore(folder.File);
            Check(store.Save(grant(new(), true)), "persist synthetic grant: " + store.Error);
            var plugin = new Plugin(store);
            plugin.SetPendingQuestion(null);
            var clock = ObservationTime;
            var args = Arguments(category);
            plugin.Seed(category, ObservationTime);
            var query = plugin.CreateActivityQueryUnderLock(() => clock)!;
            var original = query(args);
            Check(original.Contains("last_observed_menu") && JsonDocument.Parse(original).RootElement.GetProperty("available").GetBoolean(),
                "actual callback returns frozen managed observations without native capture");
            plugin.activityObservations.Observe(ActivitySnapshot.Unavailable(category, "Synthetic menu closed"));
            Check(plugin.CreateActivityQueryUnderLock(() => clock)!(args) == original, "closing menu keeps managed result");
            clock = ObservationTime.AddMinutes(15).AddTicks(-1); Check(query(args) == original, "frozen callback lives until exact deadline");
            clock = ObservationTime.AddMinutes(15); Throws<IOException>(() => query(args));
            Check(!Available(plugin.CreateActivityQueryUnderLock(() => clock)!(args)), "fresh callback reflects cache expiration");
            clock = ObservationTime.AddTicks(-1); Throws<IOException>(() => query(args));
            clock = ObservationTime;
            plugin.activityObservations.Reset();
            Check(query(args) == original, "existing answer owns detached snapshot after cache replacement");
            var absent = plugin.CreateActivityQueryUnderLock(() => clock)!;
            Check(!Available(absent(args)), "absence is explicit");
            plugin.Seed(category, ObservationTime);
            Check(!Available(absent(args)), "frozen absence cannot gain later observations");
            Check(Available(plugin.CreateActivityQueryUnderLock(() => clock)!(args)), "new answer can see refreshed evidence");

            query = plugin.CreateActivityQueryUnderLock(() => clock)!;
            plugin.activeContentId = 456;
            Throws<OperationCanceledException>(() => query(args));
            Check(!Available(plugin.CreateActivityQueryUnderLock(() => clock)!(args)), "new owner cannot see previous character's cache");
            plugin.activeContentId = 123;
            query = plugin.CreateActivityQueryUnderLock(() => clock)!;
            plugin.contextLoadEpoch++;
            Throws<OperationCanceledException>(() => query(args));
            query = plugin.CreateActivityQueryUnderLock(() => clock)!;
            plugin.ready = false;
            Throws<OperationCanceledException>(() => query(args));
            Check(plugin.CreateActivityQueryUnderLock(() => clock) is null, "unloaded character has no activity callback");
            plugin.ready = true;
            loading = true;
            Throws<OperationCanceledException>(() => query(args));
            plugin.PollActivityObservations(); // Loading returns before synthetic readers/native identity stub.
            loading = false;
            plugin.companion.ContinuationAllowed = false;
            Throws<OperationCanceledException>(() => query(args));
            plugin.companion.ContinuationAllowed = true;

            query = plugin.CreateActivityQueryUnderLock(() => clock)!;
            var otherCategory = ActivityCases.First(item => item.Category != category).Category;
            Throws<OperationCanceledException>(() => query(Arguments(otherCategory)));
            plugin.SetPendingQuestion("synthetic foreground question");
            plugin.SetBackgroundQuestion(true);
            set(plugin, false); // Actual production Plugin.Access increments its epoch and resets activity state.
            Check(plugin.CancelCount == 1 && !plugin.AnyPendingQuestion, "revoke cancels concurrent requests");
            Throws<OperationCanceledException>(() => query(args));
            Check(plugin.CreateActivityQueryUnderLock(() => clock) is null, "revoked sole permission removes callback");
            set(plugin, true);
            Throws<OperationCanceledException>(() => query(args));
            Check(!Available(plugin.CreateActivityQueryUnderLock(() => clock)!(args)), "regrant does not resurrect cache");
            plugin.Seed(category, ObservationTime);
            var fresh = plugin.CreateActivityQueryUnderLock(() => clock)!;
            Check(Available(fresh(args)), "regrant requires fresh settled observation");
            plugin.contextDisposed = true;
            Throws<OperationCanceledException>(() => fresh(args));
            Check(plugin.CreateActivityQueryUnderLock(() => clock) is null, "disposed plugin cannot produce a callback");
        }
        Console.WriteLine("PASS actual query/access integration: frozen presence/absence, TTL, owner/session, loading, continuation, revoke and regrant");

        using (var folder = new ActivityTestFolder())
        {
            var store = new CompanionAccessSettingsStore(folder.File);
            Check(store.Save(new(TripleTriadDecks: true)), "save synthetic query grant");
            var plugin = new Plugin(store);
            plugin.Seed("triple_triad_decks", ObservationTime);
            var query = plugin.CreateActivityQueryUnderLock(() => ObservationTime)!;
            foreach (var json in new[] { "[]", "{}", """{"category":"triple_triad_decks","category":"triple_triad_decks"}""",
                """{"category":"triple_triad_decks","ids":[1,1]}""", """{"category":"triple_triad_decks","limit":51}""",
                """{"category":"triple_triad_decks","state":"all"}""" })
            {
                using var invalid = JsonDocument.Parse(json);
                Throws<ArgumentException>(() => query(invalid.RootElement));
            }
            using var args = JsonDocument.Parse("""{"category":"triple_triad_decks","group":"deck_1","ids":[1,2,3,4,5],"offset":1,"limit":2}""");
            using var page = JsonDocument.Parse(query(args.RootElement));
            var result = page.RootElement;
            Check(result.GetProperty("entries").EnumerateArray().Select(row => row.GetProperty("id").GetUInt32()).SequenceEqual([2u, 3u]) &&
                result.GetProperty("matched_count").GetInt32() == 5 && result.GetProperty("next_offset").GetInt32() == 3,
                "actual callback preserves validated group, IDs, offset and limit");
            Throws<OperationCanceledException>(() => query(Arguments("race_chocobo")));
        }
        Console.WriteLine("PASS actual activity callback rejects malformed arguments and preserves filtered pagination");

        using (var folder = new ActivityTestFolder())
        {
            var store = new CompanionAccessSettingsStore(folder.File);
            Check(store.Save(new(CollectionTools: true, FreeCompany: true)), "save event isolation grants");
            var plugin = new Plugin(store);
            var standard = new ActivitySnapshot("mogpendium_standard", ObservationTime, true, MogRows("mogpendium_standard", 28));
            plugin.ApplyMogpendiumSeasonUnderLock(28);
            plugin.Seed("free_company", ObservationTime);
            plugin.Seed("mogpendium_standard", ObservationTime);
            plugin.Seed("mogpendium_weekly", ObservationTime);
            plugin.ApplyMogpendiumSeasonUnderLock(28);
            Check(plugin.activityObservations.HasRecent("mogpendium_weekly", ObservationTime), "same event keeps visited tabs");
            var oldQuery = plugin.CreateActivityQueryUnderLock(() => ObservationTime)!;
            var oldAnswer = oldQuery(Arguments("mogpendium_standard"));
            var different = new ActivitySnapshot("mogpendium_ultimog", ObservationTime, true, MogRows("mogpendium_ultimog", 29));
            // A verified new event heading clears the old event even if its pending
            // detail pane has no readable objective yet.
            plugin.ApplyMogpendiumSeasonUnderLock(29);
            Check(!plugin.activityObservations.HasRecent("mogpendium_standard", ObservationTime) &&
                !plugin.activityObservations.HasRecent("mogpendium_weekly", ObservationTime), "event switch clears all prior Mog tabs");
            Check(plugin.activityObservations.HasRecent("free_company", ObservationTime), "event switch retains unrelated FC observation");
            Check(!plugin.activityObservations.Observe(different), "new event needs a fresh stable pair");
            Check(oldQuery(Arguments("mogpendium_standard")) == oldAnswer, "already-started answer retains its explicitly old-event snapshot");
            Check(!Available(plugin.CreateActivityQueryUnderLock(() => ObservationTime)!(Arguments("mogpendium_standard"))),
                "new answer cannot inherit previous event's standard catalog");
            plugin.ResetActivityObservationsUnderLock();
            Check(plugin.mogpendiumSeason is null, "session reset clears remembered season");
        }
        Console.WriteLine("PASS Mogpendium event switches clear only Mog tabs and preserve frozen answer identity");
    }

    private void Seed(string category, DateTimeOffset time)
    {
        this.activityOwner = this.activeContentId;
        this.activityObservations.ResetCandidates();
        Check(!this.activityObservations.Observe(Observed(category, time.AddMilliseconds(-500))), "seed first candidate");
        Check(this.activityObservations.Observe(Observed(category, time)), "seed settled snapshot");
    }
    private static ActivitySnapshot Observed(string category, DateTimeOffset time) => new(category, time, true, category switch
    {
        "island_progress" => [new(1,"progression","Sanctuary rank",Value:19,Unit:"rank"),
            new(2,"progression","Island experience",Value:42000,Required:100000,Unit:"experience")],
        "cosmic_research" => CaptureCosmicResearch(CosmicObservation(time)).Entries.ToArray(),
        "island_materials" => CaptureIslandMaterials(MaterialObservation(time)).Entries.ToArray(),
        "cosmic_missions" => CaptureCosmicMissions(MissionObservation(time)).Entries.ToArray(),
        "free_company_credits" => CaptureFreeCompanyCredits(new(time, 12345)).Entries.ToArray(),
        "selected_macro" => CaptureSelectedMacro(MacroObservation(time)).Entries.ToArray(),
        "island_currencies" => [new(1,"balances","Seafarer's Cowrie",Value:12345,Unit:"cowries",ReferenceId:37549,ReferenceKind:"item"),
            new(2,"balances","Islander's Cowrie",Value:678,Unit:"cowries",ReferenceId:37550,ReferenceKind:"item")],
        "field_record" => [new(1,"selection","Selected record number",Value:2,Unit:"record"),
            new(2,"selection","Displayed record name",Text:"???"),
            new(3,"selection","Displayed registration",Text:FieldRecordContract.Unavailable),
            new(4,"collection","Displayed registered total",Value:23,Required:50,Unit:"records")],
        "portable_archive" => [new(1,"selection","Selected archive entry",Text:"Synthetic archive entry",ReferenceId:226,ReferenceKind:"description_string"),
            new(2,"selection","Selected archive page",Value:0,Unit:"subrow",ReferenceId:12,ReferenceKind:"description_page")],
        "mahjong" => Enumerable.Range(1,6).Select(i=>new ActivityEntry((uint)i,"statistics",MahjongContract.Name((uint)i)!,Text:"Synthetic display")).ToArray(),
        "triple_triad_decks" => Enumerable.Range(1, 25).Select(id => new ActivityEntry((uint)id,
            $"deck_{(id - 1) / 5 + 1}", "Synthetic deck card", ReferenceId: 1,
            ReferenceKind: "triple_triad_card", Slot: (id - 1) % 5 + 1)).ToArray(),
        "race_chocobo" => [new(1, "progression", "Synthetic racing rank", Value: 5, Unit: "rank"),
            new(2, "progression", "Synthetic racing EXP", Value: 200, Required: 500, Unit: "experience"),
            new(3, "identity", "Synthetic racing name", Text: "Synthetic Bird"),
            new(4, "progression", "Rating", Value: 100, Unit: "rating"),
            new(5, "progression", "Rating class", Text: "No named rating class could be resolved."),
            new(10, "attributes", "Maximum speed", Value: 250, Required: 500, Unit: "stat"),
            new(11, "attributes", "Acceleration", Value: 250, Required: 500, Unit: "stat"),
            new(12, "attributes", "Endurance", Value: 250, Required: 500, Unit: "stat"),
            new(13, "attributes", "Stamina", Value: 250, Required: 500, Unit: "stat"),
            new(14, "attributes", "Cunning", Value: 250, Required: 500, Unit: "stat"),
            new(20, "abilities", "Hereditary ability", Text: "No ability reference in the loaded record."),
            new(21, "abilities", "Learned ability", Text: "No ability reference in the loaded record."),
            new(30, "identity", "Sex", Text: "Female"), new(31, "identity", "Weather preference", Text: "Fair"),
            new(32, "identity", "Color", Text: "No color reference in the loaded record."),
            new(40, "pedigree", "Grade", Value: 9, Unit: "grade"), new(41, "training", "Available sessions", Value: 2, Unit: "sessions"),
            .. Enumerable.Range(50, 5).Select(id => new ActivityEntry((uint)id, "pedigree_own", "Own stars", Value: 4, Unit: "stars")),
            .. Enumerable.Range(60, 5).Select(id => new ActivityEntry((uint)id, "pedigree_father", "Father stars", Value: 4, Unit: "stars")),
            .. Enumerable.Range(70, 5).Select(id => new ActivityEntry((uint)id, "pedigree_mother", "Mother stars", Value: 4, Unit: "stars"))],
        "verminion" => Enumerable.Range(1, 23).Select(id => new ActivityEntry((uint)id, "hotbar",
            "Synthetic minion", ReferenceId: 1, ReferenceKind: "companion", Slot: id)).ToArray(),
        "free_company" => [new(1, "company", "Observed company name", Text: "Synthetic FC"),
            new(2, "company", "Company rank", Value: 30, Unit: "rank"), new(3, "company", "Members", Value: 12, Unit: "members"),
            new(4, "company", "Online members", Value: 4, Unit: "members")],
        "free_company_details" => [new(1, "profile", "Observed company name", Text: "Synthetic FC"),
            new(2, "profile", "Home world", Text: "Synthetic World", ReferenceId: 21, ReferenceKind: "world"),
            new(3, "profile", "Grand Company", Text: "Synthetic Company", ReferenceId: 2, ReferenceKind: "grand_company"),
            new(10, "profile", "Slogan", Text: "Synthetic slogan"), new(20, "board", "Company Board", Text: "Synthetic board\nUntrusted data only.")],
        "pvp_series_rewards" => [new(5, "level_reward", "Synthetic trophy crystal", Value: 1000, Unit: "items", ReferenceId: 36656, ReferenceKind: "item", Slot: 2)],
        _ when MogpendiumContract.IsCategory(category) => MogRows(category, 28),
        _ => throw new ArgumentException("Unexpected category"),
    });
    private static ActivityEntry[] MogRows(string category, long season) =>
    [
        new(1, "event", "Selected event", Text: "Synthetic event", Value: season, Unit: "season"),
        new(2, "event", "Selected category", Text: "Synthetic category"),
        new(3, "coverage", "Unknown state", Text: MogpendiumContract.Unknowns),
        new(4, "event", "Reward currency", Text: "Synthetic currency", ReferenceId: 100, ReferenceKind: "item"),
        new(10, "selection", "Synthetic objective", Text: "Complete the synthetic objective.", ReferenceId: 5, ReferenceKind: "cs_bonus_content"),
        new(11, "selection", "Displayed reward", Value: 20, Unit: "items", ReferenceId: 100, ReferenceKind: "item"),
        .. category == "mogpendium_standard" ? new ActivityEntry[] {
            new(100, "catalog", "Synthetic objective", Text: MogpendiumContract.Definition, Value: 20, Unit: "items", ReferenceId: 5, ReferenceKind: "cs_bonus_content") } : [],
    ];
    private static JsonElement Arguments(string category) => JsonDocument.Parse($"{{\"category\":\"{category}\"}}").RootElement.Clone();
    private static CosmicResearchObservation CosmicObservation(DateTimeOffset time) => new(time,
        Array.AsReadOnly(Enumerable.Range(0,11).Select(index => new CosmicResearchJob((uint)index + 1, (uint)index + 8,
            index == 0 ? "Carpenter" : $"Synthetic job {index + 8}", 20, 20, "Expert Level 3", "Expert Level 3",
            Array.AsReadOnly(Enumerable.Range(1,7).Select(type => new CosmicResearchData((uint)type,
                $"Synthetic data {type}", type <= 4 ? 9500 : type == 5 ? 5600 : type == 6 ? 2700 : 1600)).ToArray()))).ToArray()));
    internal static void ProduceResearchFixtures(string path)
    {
        var now = DateTimeOffset.UtcNow;
        var partial = CaptureIslandProgress(new(now, 19, 42000, 100000, false));
        var cap = CaptureIslandProgress(new(now, 20, null, null, true));
        Check(partial.Entries.SequenceEqual(Observed("island_progress", now).Entries), "Island native projection matches contract");
        Check(cap.Entries[1].Value is null && cap.Entries[1].Required is null, "maximum rank never projects zero EXP");
        Throws<ArgumentException>(() => CaptureIslandProgress(new(now,20,0,0,true)));
        Throws<ArgumentException>(() => CaptureIslandProgress(new(now,19,100000,100000,false)));
        Throws<ArgumentException>(() => new ActivitySnapshot("island_progress", now, true,
            [cap.Entries[0],partial.Entries[1]]));
        var cosmic = CaptureCosmicResearch(CosmicObservation(now));
        Check(cosmic.Entries.Count == 99 && cosmic.Entries.All(row => row.Required is null), "Cosmic projection has99banked/stage rows without invented targets");
        Throws<ArgumentException>(() => new ActivitySnapshot("cosmic_research", now, true, cosmic.Entries.Skip(1)));
        Throws<ArgumentException>(() => new ActivitySnapshot("cosmic_research", now, true,
            cosmic.Entries.Select(row => row.Id == 3 ? row with { ReferenceId = 9 } : row)));
        var fixtures = new Dictionary<string, object>();
        void Add(string name, ActivitySnapshot snapshot, string arguments)
        {
            var args = JsonDocument.Parse(arguments).RootElement.Clone();
            fixtures[name] = new { arguments = args, result = new ActivityLookup(snapshot.Available ? snapshot.AsLastObserved() : snapshot).Query(args) };
        }
        Add("island_partial",partial,"{\"category\":\"island_progress\"}");
        Add("island_cap",cap,"{\"category\":\"island_progress\"}");
        Add("island_unavailable",CaptureIslandProgress(null),"{\"category\":\"island_progress\"}");
        Add("cosmic_all",cosmic,"{\"category\":\"cosmic_research\"}");
        Add("cosmic_filtered",cosmic,"{\"category\":\"cosmic_research\",\"group\":\"class_job_8\"}");
        Add("cosmic_unavailable",CaptureCosmicResearch(null),"{\"category\":\"cosmic_research\"}");
        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(fixtures));
        Console.WriteLine("PASS Island/Cosmic production projections, bounded contracts and fresh producer");
    }
    internal static void ProduceIslandFixtures(string path)
    {
        const string category = "island_currencies";
        var now = DateTimeOffset.UtcNow;
        var reading = CaptureIslandCurrencies(new(now, new(37549,"Seafarer's Cowrie",12345), new(37550,"Islander's Cowrie",678)));
        Check(reading.Entries.SequenceEqual(Observed(category, now).Entries), "native projection preserves paired balances");
        var zero = CaptureIslandCurrencies(new(now, new(37549,"Seafarer's Cowrie",0), new(37550,"Islander's Cowrie",0)));
        Check(!CaptureIslandCurrencies(null).Available, "unready native read remains unavailable");
        Throws<ArgumentException>(() => new ActivitySnapshot(category, now, true, [reading.Entries[0]]));
        Throws<ArgumentException>(() => new ActivitySnapshot(category, now, true,
            reading.Entries.Select(row => row.Id == 2 ? row with { ReferenceId = 37549 } : row)));
        Throws<ArgumentException>(() => new ActivitySnapshot(category, now, true,
            reading.Entries.Select(row => row with { Value = (long)int.MaxValue + 1 })));
        var options = ActivityLookup.ParseArguments(Arguments(category));
        var result = new Dictionary<string, object>
        {
            ["all"] = new ActivityLookup(reading.AsLastObserved()).Query(options),
            ["zero"] = new ActivityLookup(zero.AsLastObserved()).Query(options),
            ["unavailable"] = new ActivityLookup(CaptureIslandCurrencies(null)).Query(options),
        };
        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(result));
        Console.WriteLine("PASS Island production projection, paired contract and current C# producer");
    }
    private static bool Available(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.GetProperty("available").GetBoolean(); }
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}

// No game/native assemblies or live services are referenced by this test project.
internal sealed class FieldRecordReader { internal ActivitySnapshot? Capture() => throw new Exception("Unexpected native reader call"); }
internal sealed class PortableArchiveReader
{
    internal ActivitySnapshot? Capture(out bool menuObserved)
    { menuObserved = false; throw new Exception("Unexpected native reader call"); }
}
internal sealed class MahjongReader { internal ActivitySnapshot Capture() => throw new Exception("Unexpected native reader call"); }
internal sealed class IslandCurrencyReader { internal IslandCurrencyObservation? Capture() => throw new Exception("Unexpected native reader call"); }
internal sealed class IslandProgressReader { internal IslandProgressObservation? Capture() => throw new Exception("Unexpected native reader call"); }
internal sealed class IslandMaterialsReader { internal IslandMaterialsObservation? Capture() => throw new Exception("Unexpected native reader call"); }
internal sealed record IslandMaterialBalance(uint ItemId, string Name, int Quantity, uint PouchRowId, uint CategoryId, string CategoryName);
internal sealed record IslandMaterialsObservation(DateTimeOffset CapturedAtUtc, System.Collections.ObjectModel.ReadOnlyCollection<IslandMaterialBalance> Items);
internal sealed class CosmicMissionReader { internal CosmicMissionObservation? Capture(uint territory) => throw new Exception("Unexpected native reader call"); }
internal sealed record CosmicMissionEntry(uint MissionId, string Name, uint ClassJobId, string JobName, string Medal);
internal sealed record CosmicMissionObservation(DateTimeOffset CapturedAtUtc, System.Collections.ObjectModel.ReadOnlyCollection<CosmicMissionEntry> Entries);
internal sealed record IslandProgressObservation(DateTimeOffset CapturedAtUtc, int Rank, int? Experience, int? RequiredExperience, bool IsMaximumRank);
internal sealed class CosmicResearchReader { internal CosmicResearchObservation? Capture(uint territory) => throw new Exception("Unexpected native reader call"); }
internal sealed record CosmicResearchData(uint Type, string Name, int Quantity);
internal sealed record CosmicResearchJob(uint ToolClassId, uint ClassJobId, string Name, int CurrentStage, int UnlockedStage,
    string? CurrentStageName, string? UnlockedStageName, System.Collections.ObjectModel.ReadOnlyCollection<CosmicResearchData> Data);
internal sealed record CosmicResearchObservation(DateTimeOffset CapturedAtUtc, System.Collections.ObjectModel.ReadOnlyCollection<CosmicResearchJob> Jobs);
internal sealed record IslandCurrencyBalance(uint ItemId, string Name, int Quantity);
internal sealed record IslandCurrencyObservation(DateTimeOffset CapturedAtUtc, IslandCurrencyBalance Seafarer, IslandCurrencyBalance Islander);
internal sealed class GoldSaucerReader { internal ActivitySnapshot Capture(string _) => throw new Exception("Unexpected native reader call"); }
internal sealed class FreeCompanyReader
{
    internal ActivitySnapshot Capture() => throw new Exception("Unexpected native reader call");
    internal ActivitySnapshot CaptureDetails() => throw new Exception("Unexpected native reader call");
}
internal sealed class FreeCompanyCreditsReader { internal FreeCompanyCreditsObservation? Capture() => throw new Exception("Unexpected native reader call"); }
internal enum FreeCompanyCreditsSource { ItemShop, Topics }
internal sealed record FreeCompanyCreditsObservation(DateTimeOffset CapturedAtUtc, uint Credits, FreeCompanyCreditsSource Source = FreeCompanyCreditsSource.ItemShop);
internal sealed class SelectedMacroReader { internal SelectedMacroObservation? Capture(out bool observed) => throw new Exception("Unexpected native reader call"); }
internal sealed record SelectedMacroObservation(DateTimeOffset CapturedAtUtc, int Set, int Index, string Name, IReadOnlyList<string> Lines);
internal sealed class PvpSeriesRewardsReader { internal ActivitySnapshot Capture() => throw new Exception("Unexpected native reader call"); }
internal sealed class MogpendiumReader { internal ActivitySnapshot? Capture(out long? season) => throw new Exception("Unexpected native reader call"); }
internal static class PerformanceDiagnostics
{
    internal static IDisposable Measure(string _) => new Noop();
    private sealed class Noop : IDisposable { public void Dispose() { } }
}
internal sealed class ActivityTestFolder : IDisposable
{
    private readonly string directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../../.test-tmp/activity-cache", Guid.NewGuid().ToString("N")));
    internal string File => Path.Combine(this.directory, "access.json");
    internal ActivityTestFolder() => Directory.CreateDirectory(this.directory);
    public void Dispose() { if (System.IO.File.Exists(this.File)) System.IO.File.Delete(this.File); Directory.Delete(this.directory); }
}
