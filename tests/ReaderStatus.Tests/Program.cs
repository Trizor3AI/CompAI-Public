using CompAI;
using System.Text.RegularExpressions;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var accessConstructor = typeof(CompanionAccessSettings).GetConstructors()
    .Single(constructor => constructor.GetParameters().Length > 1);
var allAccess = (CompanionAccessSettings)accessConstructor
    .Invoke(Enumerable.Repeat<object>(true, accessConstructor.GetParameters().Length).ToArray());

var plugin = new Plugin { Access = allAccess };
var statuses = plugin.ReaderStatuses();
var settingsPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../CompAI/SettingsWindow.cs"));
var rows = Regex.Matches(File.ReadAllText(settingsPath),
    "ReaderAccessOption\\(readerStatuses, \\\"([^\\\"]+)\\\", \\\"[^\\\"]+\\\", ref (\\w+)")
    .Select(match => (Key: match.Groups[1].Value, Permission: match.Groups[2].Value)).ToArray();
Check(rows.Length == 49 && rows.Select(row => row.Permission).Distinct().Count() == 49,
    "Every reader permission must have one inline status row.");
Check(rows.Select(row => row.Key).Distinct().Count() == 49 && rows.All(row => statuses.ContainsKey(row.Key)),
    "Every inline reader status must resolve by stable key, independently of its displayed label.");
Check(rows.Single(row => row.Permission == "characterContext").Key == "character_details" &&
    rows.Single(row => row.Permission == "collections").Key == "collections" &&
    rows.Single(row => row.Permission == "bestiary").Key == "beastmaster_bestiary" &&
    rows.Single(row => row.Permission == "glamour").Key == "glamour", "Renamed and combined readers must match their permissions.");
Check(rows.Single(row => row.Permission == "tripleTriad").Key == "triple_triad" &&
    rows.Single(row => row.Permission == "battleChocobo").Key == "battle_chocobo" &&
    rows.Single(row => row.Permission == "pvp").Key == "pvp", "New reader permissions must resolve distinct status keys.");
foreach (var (permission, key) in new[] { ("tripleTriadDecks", "triple_triad_decks"), ("raceChocobo", "race_chocobo"),
    ("verminion", "verminion"), ("freeCompany", "free_company"), ("occultRecords", "occult_records"),
    ("collectionTools", "collection_tools"), ("mahjong", "mahjong"),
    ("islandProgress", "island_progress"), ("cosmicResearch", "cosmic_research"),
    ("islandMaterials", "island_materials"), ("cosmicMissions", "cosmic_missions"), ("selectedMacro", "selected_macro") })
    Check(rows.Single(row => row.Permission == permission).Key == key, "New reader permission must resolve its exact status key: " + permission);
Check(!statuses.ContainsKey("yokai_minions") && rows.All(row => row.Permission != "yokaiCollection"),
    "Retired Yo-kai settings must not expose a redundant reader or status.");
Console.WriteLine("PASS: all 49 inline reader permission keys resolve; redundant Yo-kai reader retired");

plugin.Access = new();
Check(plugin.ReaderStatuses().Count == 0, "Disabled readers must not expose saved or observed metadata.");
plugin.Access = allAccess;
plugin.Ready = false;
Check(plugin.ReaderStatuses().Values.All(row => row.State == "Log in"), "Unloaded readers must not claim current readiness.");
plugin.Ready = true;
Check(plugin.ReaderStatuses()["currencies"].State == "On demand" &&
    plugin.ReaderStatuses()["shops"].State == "Open to refresh" &&
    plugin.ReaderStatuses()["triple_triad"].State == "On demand" &&
    plugin.ReaderStatuses()["battle_chocobo"].State == "On demand" &&
    plugin.ReaderStatuses()["pvp"].State == "Open to refresh" &&
    plugin.ReaderStatuses()["island_progress"].State == "Open to refresh" &&
    plugin.ReaderStatuses()["cosmic_research"].State == "Open to refresh", "Default status must retain the reader's capture model.");
Console.WriteLine("PASS: disabled, logged-out, on-demand and menu statuses remain distinct");

var macroStatus = plugin.ReaderStatuses()["selected_macro"];
Check(macroStatus.State == "Open to refresh" && macroStatus.Details.Contains("select one macro") &&
    macroStatus.Details.Contains("name and 15 lines") && macroStatus.Details.Contains("only when the assistant requests") &&
    macroStatus.Details.Contains("15 minutes in this login") && macroStatus.Details.Contains("never executed"),
    "Selected macro status must state its selected-only, on-request, same-login retention and no-execution limits.");
plugin.Recent.Add("selected_macro");
Check(plugin.ReaderStatuses()["selected_macro"].State == "Recent" &&
    plugin.ReaderStatuses()["selected_macro"].Details.Contains("Last-known"), "Retained macro status must preserve freshness limits.");
plugin.Access = allAccess with { SelectedMacro = false };
Check(!plugin.ReaderStatuses().ContainsKey("selected_macro"), "All other grants combined must not reveal selected macro metadata.");
plugin.Access = new(SelectedMacro: true);
Check(plugin.ReaderStatuses().Count == 1 && plugin.ReaderStatuses()["selected_macro"].State == "Recent",
    "Selected macro access must expose only its own status.");
plugin.Recent.Remove("selected_macro");
Check(plugin.ReaderStatuses()["selected_macro"].State == "Open to refresh", "Expired or cleared macro observations request a refresh.");
plugin.Access = allAccess;
Console.WriteLine("PASS: selected macro status enforces independent permission, retention and no-execution limits");

plugin.Recent.Add("pvp");
statuses = plugin.ReaderStatuses();
Check(statuses["pvp"].State == "Recent" && statuses["pvp"].Details.Contains("15 minutes") &&
    statuses["pvp"].Details.Contains("Last-known") && statuses["battle_chocobo"].State == "On demand",
    "Recent PvP evidence must describe its retention without implying another reader was captured.");
plugin.Access = allAccess with { Pvp = false };
Check(!plugin.ReaderStatuses().ContainsKey("pvp"), "Disabled PvP must not expose retained status metadata.");
plugin.Access = allAccess;
plugin.Recent.Remove("pvp");
Check(plugin.ReaderStatuses()["pvp"].State == "Open to refresh", "Expired or cleared PvP status must request a refresh.");
Console.WriteLine("PASS: PvP recent status preserves its TTL and hides metadata when disabled");

plugin.Recent.Add("free_company_credits");
Check(plugin.ReaderStatuses()["free_company"].State == "Recent" &&
    plugin.ReaderStatuses()["free_company"].Details.Contains("not joined to a named company"),
    "A retained FC item-shop balance must make the existing FC row recent without inventing company identity.");
plugin.Access = allAccess with { FreeCompany = false };
Check(!plugin.ReaderStatuses().ContainsKey("free_company"), "FC credit status must hide when its shared grant is revoked.");
plugin.Access = allAccess;
plugin.Recent.Remove("free_company_credits");
Check(plugin.ReaderStatuses()["free_company"].State == "Open to refresh", "Cleared FC balance cannot retain recent status.");
Console.WriteLine("PASS: FC credit observation shares the existing FC status and permission");

foreach (var (key, revoke) in new (string, Func<CompanionAccessSettings, CompanionAccessSettings>)[]
{
    ("triple_triad_decks", a => a with { TripleTriadDecks = false }),
    ("race_chocobo", a => a with { RaceChocobo = false }),
    ("verminion", a => a with { Verminion = false }),
    ("mahjong", a => a with { Mahjong = false }),
    ("field_record", a => a with { CollectionTools = false }),
    ("portable_archive", a => a with { CollectionTools = false }),
    ("free_company", a => a with { FreeCompany = false }),
})
{
    Check(plugin.ReaderStatuses()[key].State == "Open to refresh", "Unobserved menu activity requests an initial refresh.");
    plugin.Recent.Add(key);
    var state = plugin.ReaderStatuses()[key];
    Check(state.State == "Recent" && state.Details.Contains("15 minutes") && state.Details.Contains("Last-known"),
        "Recent activity status retains time and freshness limits.");
    plugin.Access = revoke(allAccess);
    Check(!plugin.ReaderStatuses().ContainsKey(key), "Disabled activity cannot reveal cached status.");
    plugin.Access = allAccess;
    plugin.Recent.Remove(key);
}
Console.WriteLine("PASS: each activity permission independently exposes recent retention and hides disabled metadata");

foreach (var (key, revoke, initialState) in new (string, Func<CompanionAccessSettings, CompanionAccessSettings>, string)[]
{
    ("island_progress", a => a with { IslandProgress = false }, "Open to refresh"),
    ("cosmic_research", a => a with { CosmicResearch = false }, "Open to refresh"),
    ("island_materials", a => a with { IslandMaterials = false }, "Open to refresh"),
    ("cosmic_missions", a => a with { CosmicMissions = false }, "Open to refresh"),
})
{
    Check(plugin.ReaderStatuses()[key].State == initialState, "Regional progression status preserves its capture model.");
    plugin.Recent.Add(key);
    var state = plugin.ReaderStatuses()[key];
    Check(state.State == "Recent" && state.Details.Contains("15 minutes") && state.Details.Contains("Last-known"),
        "Regional progress retains capture-time limits without promising live state.");
    plugin.Access = revoke(allAccess);
    Check(!plugin.ReaderStatuses().ContainsKey(key), "Disabled regional progression must hide cached metadata.");
    plugin.Access = allAccess;
    plugin.Recent.Remove(key);
    Check(plugin.ReaderStatuses()[key].State == initialState, "Cleared or expired regional progress returns to its capture model.");
}
Check(plugin.ReaderStatuses()["island_progress"].Details.Contains("do not establish island ownership"),
    "Displayed Index progression must not claim current island ownership.");
plugin.Access = new(Currencies: true);
Check(!plugin.ReaderStatuses().ContainsKey("island_progress") && !plugin.ReaderStatuses().ContainsKey("cosmic_research"),
    "The existing currency grant must never expose either independent progression status.");
plugin.Access = new(IslandProgress: true, CosmicResearch: true);
Check(!plugin.ReaderStatuses().ContainsKey("island_materials") && !plugin.ReaderStatuses().ContainsKey("cosmic_missions"),
    "Existing regional progression grants must never expose materials or mission medal metadata.");
plugin.Access = allAccess;
Check(plugin.ReaderStatuses()["island_materials"].Details.Contains("island ownership are not established") &&
    plugin.ReaderStatuses()["island_materials"].Details.Contains("undiscovered items"),
    "Observed materials must preserve ownership and inventory completeness limits.");
Check(plugin.ReaderStatuses()["cosmic_missions"].Details.Contains("positive Silver and Gold") &&
    plugin.ReaderStatuses()["cosmic_missions"].Details.Contains("planet attribution remain unknown") &&
    plugin.ReaderStatuses()["cosmic_missions"].Details.Contains("does not prove none are owned"),
    "Mission status must preserve positive-only observations and unknown planet/completeness.");
Console.WriteLine("PASS: Island and Cosmic progression statuses enforce independent access, retention and ownership limits");

plugin.Observe("mounts", true);
statuses = plugin.ReaderStatuses();
Check(statuses["collections"].State == "Mixed" && statuses["collections"].Details.Contains("Mounts: Read") &&
    statuses["collections"].Details.Contains("Minions: On demand"), "One mount lookup must not mark minions as read.");
plugin.Observe("minions", true);
Check(plugin.ReaderStatuses()["collections"].State == "Read", "Matching sub-reader states should compact to one label.");
plugin.Observe("spearfishing_log", false);
statuses = plugin.ReaderStatuses();
Check(statuses["fishing_log"].State == "Mixed" && statuses["fishing_log"].Details.Contains("Spearfishing log: Unavailable") &&
    statuses["fishing_log"].Details.Contains("Fishing log: On demand"), "Spearfishing status must reach the combined fishing permission.");
Console.WriteLine("PASS: grouped collections and fishing preserve each sub-reader's lookup evidence");

plugin.SavedMenus.Add("armoire");
plugin.Recent.Add("achievement_progress");
statuses = plugin.ReaderStatuses();
Check(statuses["glamour"].State == "Mixed" && statuses["glamour"].Details.Contains("Armoire: Saved") &&
    statuses["glamour"].Details.Contains("Glamour dresser: Open to refresh"), "A saved armoire must not imply a saved dresser.");
Check(statuses["achievements"].State == "Mixed" && statuses["achievements"].Details.Contains("Achievement counters: Recent"),
    "Recent counters must stay visible alongside achievement completion status.");
plugin.SavedMenus.UnionWith(["glamour_dresser", "glamour_plates"]);
Check(plugin.ReaderStatuses()["glamour"].State == "Saved", "All saved glamour readers should share one compact state.");
Console.WriteLine("PASS: combined glamour and achievements retain partial saved/recent coverage");

foreach (var invalidate in new Action[] { plugin.ChangeIdentity, plugin.ChangeSession, plugin.ChangeArea })
{
    plugin.Observe("currencies", true);
    Check(plugin.ReaderStatuses()["currencies"].State == "Read", "Current metadata must be visible.");
    invalidate();
    Check(plugin.ReaderStatuses()["currencies"].State == "On demand", "Obsolete metadata must not survive owner/session/area changes.");
}
Console.WriteLine("PASS: owner, session and area changes reject stale lookup status");

plugin.snapshot = new object();
plugin.quests.View.Snapshot = new object();
plugin.observedStorage.Add(new("armoury"));
plugin.observedProgression.Add(new("hunting_log"));
plugin.observedProgression.Add(new("challenge_log", Usable: false));
statuses = plugin.ReaderStatuses();
Check(statuses["inventory"].State == "Saved" && statuses["quests"].State == "Saved" &&
    statuses["armoury"].State == "Saved" && statuses["hunting_log"].State == "Saved", "Basic and log readers must retain saved evidence.");
Check(statuses["challenge_log"].State == "Open to refresh", "Expired challenge data must not be labelled recent.");
plugin.SavedOwner = false;
statuses = plugin.ReaderStatuses();
Check(statuses["inventory"].State == "On demand" && statuses["quests"].State == "On demand" &&
    statuses["armoury"].State == "On demand" && statuses["hunting_log"].State == "Open to refresh",
    "Saved evidence must require its current owner.");
Console.WriteLine("PASS: saved basic-reader and log statuses preserve ownership and expiry limits");
