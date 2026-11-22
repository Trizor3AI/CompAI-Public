using System.Text.Json;
using CompAI;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();
static JsonElement Wire(object value) => JsonSerializer.SerializeToElement(value);
static JsonElement Query(GameContextLookup lookup, string view) => Wire(lookup.Query(Args($"{{\"view\":\"{view}\"}}")));
static void Reject(Action action)
{
    try { action(); } catch (InvalidOperationException) { return; }
    throw new Exception("Expected refusal");
}
static void Fields(JsonElement data, params string[] expected) =>
    Check(data.EnumerateObject().Select(x => x.Name).Order().SequenceEqual(expected.Order()), "Exact wire fields");

var now = DateTimeOffset.Parse("2026-10-11T12:00:00Z");
var character = new GameCharacterContext("Example Player", 19, "Paladin", "PLD", 55, 1234, 10000);
var location = new GameLocationContext(132, "New Gridania", "Visiting World", "Home World");
var line = new NpcDialogueContext("Guide", "Salut, aventurier! Știi unde mergi?");
var secondLine = new NpcDialogueContext("Guide", "You have arrived.");
var cache = new RecentNpcDialogue();
cache.Observe(line, now);
cache.Observe(null, now.AddSeconds(10));
Check(cache.Line == line && !cache.Visible && cache.ObservedAt == now && !cache.FromPreviousArea,
    "Closed line remains explicitly historical in the same area");
cache.Observe(line, now);
cache.OnAreaChanged(now.AddSeconds(20));
Check(cache.Line == line && !cache.Visible && cache.FromPreviousArea && cache.ObservedAt == now,
    "Area change preserves the line and original timestamp, marking previous area");
cache.Observe(line, now.AddSeconds(21));
Check(!cache.Visible && cache.FromPreviousArea && cache.ObservedAt == now,
    "A lingering old Talk addon does not reset the age or previous-area label");
cache.Observe(null, now.AddSeconds(22));
cache.OnAreaChanged(now.AddMinutes(3));
cache.Expire(now.AddMinutes(5));
Check(cache.Line is null && cache.ObservedAt is null && !cache.FromPreviousArea,
    "Repeated zoning does not extend the five-minute retention");
cache.Observe(line, now);
cache.OnAreaChanged(now.AddSeconds(1));
cache.Observe(null, now.AddSeconds(2));
cache.Observe(line, now.AddSeconds(3));
Check(cache.Visible && !cache.FromPreviousArea && cache.ObservedAt == now.AddSeconds(3),
    "Identical dialogue reopened after Talk closes is a new observation");
cache.Observe(null, now.AddSeconds(3));
cache.OnAreaChanged(now.AddSeconds(4));
cache.Observe(line, now.AddSeconds(5));
Check(cache.Visible && !cache.FromPreviousArea && cache.ObservedAt == now.AddSeconds(5),
    "An already-closed line can be freshly reopened identically after an area change");
cache.OnAreaChanged(now.AddSeconds(6));
cache.Observe(secondLine, now.AddSeconds(7));
Check(cache.Visible && !cache.FromPreviousArea && cache.Line == secondLine && cache.ObservedAt == now.AddSeconds(7),
    "A new Talk line replaces the previous-area line and is current again");
cache.Observe(secondLine, now.AddMinutes(8));
Check(cache.Visible && cache.ObservedAt == now.AddMinutes(8), "A still-visible current-area line stays freshly observed");
cache.Clear();
Check(cache.Line is null && !cache.Visible && !cache.FromPreviousArea, "Permission/logout/session clear removes dialogue");
cache.Observe(line, now);
cache.Expire(now.AddSeconds(-1));
Check(cache.Line is null, "A backwards clock cannot retain a future observation");
Console.WriteLine("PASS dialogue survives area changes, retains age, expires, and updates with explicit previous-area state");

for (var mask = 0; mask < 8; mask++)
{
    var allowCharacter = (mask & 1) != 0;
    var allowDialogue = (mask & 2) != 0;
    var allowLocation = (mask & 4) != 0;
    var lookup = new GameContextLookup(now, allowCharacter, allowDialogue, character, line, now, true, null, null,
        allowLocation, location);
    var summary = Wire(lookup.Summary);
    Fields(summary, "scope", "captured_at_utc", "character_allowed", "dialogue_allowed", "location_allowed",
        "character_available", "dialogue_available", "location_available");
    Check(summary.GetProperty("character_available").GetBoolean() == allowCharacter &&
        summary.GetProperty("dialogue_available").GetBoolean() == allowDialogue &&
        summary.GetProperty("location_available").GetBoolean() == allowLocation, "All permissions filter summary independently");
    Check(!summary.ToString().Contains(character.Name) && !summary.ToString().Contains(line.Text) &&
        !summary.ToString().Contains(location.LocationName) && !summary.ToString().Contains(location.CurrentWorldName!) &&
        !summary.ToString().Contains(location.HomeWorldName!), "Initial summary contains no reader data");
    foreach (var (view, permitted) in new[] { ("character", allowCharacter), ("dialogue", allowDialogue), ("location", allowLocation) })
    {
        if (!permitted) { Reject(() => Query(lookup, view)); continue; }
        var data = Query(lookup, view).GetProperty("data");
        if (view == "character")
        {
            Fields(data, "name", "class_job_id", "class_job_name", "abbreviation", "level", "experience", "next_level_experience");
            Check(data.GetProperty("experience").GetInt32() == 1234, "Character values preserved without location");
        }
        else if (view == "location")
        {
            Fields(data, "territory_id", "location_name", "current_world_name", "home_world_name");
            Check(data.GetProperty("location_name").GetString() == "New Gridania", "Location values preserved without character info");
            Check(data.GetProperty("current_world_name").GetString() == "Visiting World" &&
                data.GetProperty("home_world_name").GetString() == "Home World", "Visited and home worlds remain distinct");
        }
        else
        {
            Fields(data, "speaker", "text", "observed_at_utc", "is_visible", "from_previous_area");
            Check(data.GetProperty("text").GetString() == line.Text, "Unicode preserved without identity or location");
        }
    }
    Reject(() => lookup.Query(Args("{\"view\":\"character\",\"view\":\"dialogue\"}")));
    Reject(() => lookup.Query(Args("{\"view\":\"location\",\"extra\":true}")));
    Reject(() => lookup.Query(Args("{\"view\":\"other\"}")));
}
Console.WriteLine("PASS metadata-only summary, exact schemas and all 8 independent permission combinations");

foreach (var worlds in new[] { (Current: (string?)null, Home: (string?)null),
             (Current: (string?)null, Home: "Home World"), (Current: "Current World", Home: (string?)null),
             (Current: "Same World", Home: "Same World") })
{
    var worldLocation = location with { CurrentWorldName = worlds.Current, HomeWorldName = worlds.Home };
    var worldLookup = new GameContextLookup(now, false, false, null, null, null, false, null, null, true, worldLocation);
    var worldData = Query(worldLookup, "location").GetProperty("data");
    Check(worldData.GetProperty("current_world_name").GetString() == worlds.Current &&
        worldData.GetProperty("home_world_name").GetString() == worlds.Home,
        "World names preserve independent unknowns without substitution or a fabricated visit status");
}
Console.WriteLine("PASS unknown, partial, same-world and visiting-world location snapshots");

var unavailable = new GameContextLookup(now, true, true, null, null, null, false, "Not loaded.", "No NPC dialogue.",
    true, null, "Location is loading.");
var result = Query(unavailable, "location");
Check(!result.GetProperty("available").GetBoolean() && result.GetProperty("data").ValueKind == JsonValueKind.Null &&
    result.GetProperty("unavailable_reason").GetString() == "Location is loading.", "Unavailable location does not fabricate data");
var historical = new GameContextLookup(now.AddSeconds(10), false, true, character, line, now, true, null, null,
    false, location, dialogueFromPreviousArea: true);
result = Query(historical, "dialogue");
Check(result.GetProperty("data").GetProperty("from_previous_area").GetBoolean() &&
    !result.GetProperty("data").GetProperty("is_visible").GetBoolean(), "Previous-area dialogue cannot claim current visibility");
Check(!result.ToString().Contains(character.Name) && !result.ToString().Contains(location.LocationName),
    "Dialogue-only lookup omits identity and named location");
Console.WriteLine("PASS unavailable context and previous-area dialogue privacy");

var access = new CompanionAccessSettings(CharacterContext: true, NpcDialogue: true, Location: true);
var reader = new GameContextReader { Reading = new(now, character, line, null, null, location) };
var plugin = new Plugin(reader, access);
plugin.Poll();
plugin.Poll();
Check(reader.Calls.Count == 1 && reader.Calls[0] == (true, true, true), "Polling uses bounded deadlines");
var frozenBeforeArea = plugin.Freeze()!;
var originalObservation = Query(frozenBeforeArea, "dialogue").GetProperty("data").GetProperty("observed_at_utc").GetDateTimeOffset();
plugin.AreaChanged();
Check(reader.DialogueCacheClears == 1, "Area transition clears the native Talk decode cache");
Check(!Query(plugin.Freeze()!, "location").GetProperty("available").GetBoolean(),
    "Area transition clears the previous zone and world names before a fresh reading");
Plugin.LoadedId = 0;
plugin.Poll();
Check(!Query(plugin.Freeze()!, "location").GetProperty("available").GetBoolean(),
    "Unloaded PlayerState cannot restore the previous location/world snapshot");
plugin.SetReady(false);
plugin.Poll();
plugin.SetReady(true);
Plugin.LoadedId = 101;
reader.Reading = reader.Reading with { Dialogue = null };
plugin.MakePollDue();
plugin.Poll();
result = Query(plugin.Freeze()!, "dialogue");
Check(result.GetProperty("available").GetBoolean() && result.GetProperty("data").GetProperty("from_previous_area").GetBoolean() &&
    result.GetProperty("data").GetProperty("observed_at_utc").GetDateTimeOffset() == originalObservation &&
    !result.GetProperty("data").GetProperty("is_visible").GetBoolean() && plugin.MismatchCount == 0,
    "Production poll survives a zone change, transient zero identity and unavailable readiness without a fresh Talk line");
Check(!Query(frozenBeforeArea, "dialogue").GetProperty("data").GetProperty("from_previous_area").GetBoolean(),
    "An answer's frozen managed snapshot is unaffected by later mutations");
plugin.ChangeAccess(access with { CharacterContext = false });
Check(Query(plugin.Freeze()!, "dialogue").GetProperty("available").GetBoolean(), "Revoking character info preserves dialogue");
Check(Query(plugin.Freeze()!, "location").GetProperty("data").GetProperty("current_world_name").GetString() == "Visiting World",
    "Location permission alone permits world names after character permission is revoked");
Reject(() => Query(plugin.Freeze()!, "character"));
plugin.ChangeAccess(access with { CharacterContext = false, Location = false });
Check(Query(plugin.Freeze()!, "dialogue").GetProperty("available").GetBoolean(), "Revoking location preserves dialogue");
Reject(() => Query(plugin.Freeze()!, "location"));
plugin.ChangeAccess(access with { CharacterContext = false });
Check(!Query(plugin.Freeze()!, "location").GetProperty("available").GetBoolean(),
    "Re-enabling location cannot resurrect revoked world names");
plugin.ChangeAccess(new());
Check(reader.DialogueCacheClears == 2, "NPC permission loss clears the decode cache");
Check(plugin.Freeze() is null, "All access off yields no lookup snapshot");
plugin.ChangeAccess(new(NpcDialogue: true));
Check(reader.DialogueCacheClears == 3, "NPC permission re-enable starts with an empty decode cache");
Check(!Query(plugin.Freeze()!, "dialogue").GetProperty("available").GetBoolean(), "Re-enabling NPC access cannot restore revoked dialogue");
Console.WriteLine("PASS production polling lifecycle, zone-loading retention, immutable snapshots and independent revocation");

var reloaded = new Plugin(new GameContextReader { Reading = new(now, character, line, null, null, location) }, access);
Check(!Query(reloaded.Freeze()!, "location").GetProperty("available").GetBoolean(),
    "A fresh plugin instance never reloads a historical world snapshot");

foreach (var category in new[] { "character", "location", "dialogue", "none" })
{
    var singleAccess = new CompanionAccessSettings(CharacterContext: category == "character",
        Location: category == "location", NpcDialogue: category == "dialogue");
    var singleReader = new GameContextReader { Reading = new(now, character, line, null, null, location) };
    var singlePlugin = new Plugin(singleReader, singleAccess);
    singlePlugin.Poll();
    if (category == "none") Check(singleReader.Calls.Count == 0, "All-off polling makes no game reader calls");
    else
    {
        Check(singleReader.Calls.Single() == (category == "character", category == "dialogue", category == "location"),
            "Only the independently enabled native reader branch is requested");
        Check(Query(singlePlugin.Freeze()!, category).GetProperty("available").GetBoolean(), "Single-category capture is usable");
        singlePlugin.EndSession();
        Check(singleReader.DialogueCacheClears == 1, "Session end clears the decode cache");
        Check(!Query(singlePlugin.Freeze()!, category).GetProperty("available").GetBoolean(), "Ending session clears all live context");
    }
}
var changedReader = new GameContextReader { Reading = new(now, character, line, null, null, location) };
var changedPlugin = new Plugin(changedReader, access);
changedPlugin.Poll();
changedPlugin.MakePollDue();
Plugin.LoadedId = 202;
changedPlugin.Poll();
Check(changedPlugin.MismatchCount == 1 && !Query(changedPlugin.Freeze()!, "dialogue").GetProperty("available").GetBoolean(),
    "Confirmed different character follows the full context-clear path");
Check(changedReader.DialogueCacheClears == 1, "Character mismatch clears the decode cache");
Console.WriteLine("PASS independent reader dispatch, all-off no reads, session clearing and confirmed character mismatch");

var talkReader = new GameContextReader { Reading = new(now, null, line, null, null) };
var talkPlugin = new Plugin(talkReader, new(NpcDialogue: true));
talkPlugin.Poll();
talkReader.Reading = talkReader.Reading with { Dialogue = secondLine };
talkPlugin.MakePollDue();
talkPlugin.Poll();
Check(talkPlugin.DialogueHistory().Lines.Count == 2, "Production reader retains multiple Talk pages");
talkPlugin.AreaChanged();
Plugin.LoadedId = 0;
talkPlugin.Poll();
Plugin.LoadedId = 101;
talkPlugin.MakePollDue();
talkPlugin.Poll();
Check(talkPlugin.DialogueHistory().Lines.Count == 2 &&
    talkPlugin.DialogueHistory().Lines.All(l => l.FromPreviousArea),
    "Old Talk page after zoning cannot duplicate or relabel retained history");
talkReader.Reading = talkReader.Reading with { Dialogue = null, DialogueUnavailable = "No visible Talk window." };
talkPlugin.MakePollDue();
talkPlugin.Poll();
talkReader.Reading = talkReader.Reading with { Dialogue = secondLine, DialogueUnavailable = null };
talkPlugin.MakePollDue();
talkPlugin.Poll();
Check(talkPlugin.DialogueHistory().Lines.Count == 3 && !talkPlugin.DialogueHistory().Lines.Last().FromPreviousArea,
    "Confirmed Talk close permits identical text as a newly opened page");
talkPlugin.ChangeAccess(new(NpcDialogue: true, Location: true));
Check(talkPlugin.DialogueHistory().Lines.Count == 3, "Unrelated permission preserves Talk history");
talkPlugin.ChangeAccess(new(Location: true));
Check(talkPlugin.DialogueHistory().Lines.Count == 0, "Dialogue revocation clears all observed pages");
Console.WriteLine("PASS production multi-page history, zone retention, reopen and independent revocation");
