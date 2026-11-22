using System.Runtime.Loader;
using System.Text.Json;
using CompAI;

var libraryPath = Environment.GetEnvironmentVariable("DALAMUD_LIB_PATH") ??
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "addon", "Hooks", "dev");
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var path = Path.Combine(libraryPath, name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
Tests.Run();
if (args is ["--produce-fixtures", var path]) Tests.WriteProducerFixtures(path);

internal static class Tests
{
    private static int passed;
    private static NpcTargetEntry Player(string name = "Synthetic Player", string? world = "Example World") =>
        new(name, null, "player", new(world is null ? null : 23, world, 19, "Paladin", 90, NpcTargetSnapshot.LodestoneSearch(name, world)));
    internal static void Run()
    {
        Test("every native kind obeys independent grants and always excludes aetherytes", () =>
        {
            for (var kind = 0; kind <= 255; kind++)
            {
                var npc = kind is 2 or 3;
                var other = kind == 1 || kind == 4 || kind is >= 6 and <= 17;
                Check(NpcTargetReader.SupportedKind((byte)kind) == npc);
                Check(NpcTargetReader.SupportedKind((byte)kind, false, true) == other);
                Check(NpcTargetReader.SupportedKind((byte)kind, true, true) == (npc || other));
                Check(!NpcTargetReader.SupportedKind((byte)kind, false, false));
            }
        });
        Test("NPC payload omits player identities coordinates and inferred services", () =>
        {
            var snapshot = new NpcTargetSnapshot(DateTimeOffset.UtcNow, new("Synthetic Merchant", 123, "event_npc"),
                ownerContentId: 900, targetObjectId: 901, targetAddress: 902);
            var lookup = new NpcTargetLookup(snapshot);
            var result = lookup.Query(Args("{}"));
            Check(result.available && result.target is { data_id: 123, kind: "event_npc", player: null });
            Check(lookup.Summary.allow_npcs && !lookup.Summary.allow_players_and_objects);
            var json = JsonSerializer.SerializeToElement(result);
            Check(json.GetProperty("target").EnumerateObject().Select(field => field.Name).SequenceEqual(["name", "data_id", "kind", "player"]));
            Check(!json.TryGetProperty("OwnerContentId", out _) && !json.TryGetProperty("TargetObjectId", out _));
        });
        Test("players and objects require Target Info and cannot borrow NPC permission", () =>
        {
            Throws(() => new NpcTargetSnapshot(DateTimeOffset.UtcNow, Player()));
            Throws(() => new NpcTargetSnapshot(DateTimeOffset.UtcNow, new("Synthetic", 123, "event_object")));
            Throws(() => new NpcTargetSnapshot(DateTimeOffset.UtcNow, new("Synthetic NPC", 123, "event_npc"),
                allowNpcs: false, allowPlayersAndObjects: true));
            var player = new NpcTargetLookup(new(DateTimeOffset.UtcNow, Player(),
                allowNpcs: false, allowPlayersAndObjects: true)).Query(Args("{}"));
            Check(player.available && player.target is { data_id: null, kind: "player", player.level: 90 });
            var other = new NpcTargetLookup(new(DateTimeOffset.UtcNow, new("Synthetic Object", null, "event_object"),
                allowNpcs: false, allowPlayersAndObjects: true)).Query(Args("{}"));
            Check(other.target is { data_id: null, player: null });
        });
        Test("Lodestone link encodes only the visible name and home world", () =>
        {
            var entry = Player("Synthetic O'Name &?", "Example World");
            Check(entry.Player!.LodestoneSearchUrl ==
                "https://na.finalfantasyxiv.com/lodestone/character/?q=Synthetic%20O%27Name%20%26%3F&worldname=Example%20World");
            var result = new NpcTargetLookup(new(DateTimeOffset.UtcNow, entry, allowPlayersAndObjects: true)).Query(Args("{}"));
            Check(result.target!.player!.home_world_name == "Example World" && result.target.player.class_job_name == "Paladin");
            Throws(() => new NpcTargetSnapshot(DateTimeOffset.UtcNow, entry with
                { Player = entry.Player with { LodestoneSearchUrl = "https://na.finalfantasyxiv.com/lodestone/character/12345/" } },
                allowPlayersAndObjects: true));
        });
        Test("unknown player fields stay null and missing home-world name prevents a link", () =>
        {
            var entry = new NpcTargetEntry("Synthetic", null, "player", new(null, null, null, null, null, null));
            var result = new NpcTargetLookup(new(DateTimeOffset.UtcNow, entry, allowPlayersAndObjects: true)).Query(Args("{}"));
            Check(result.target!.player is { home_world_name: null, class_job_name: null, level: null, lodestone_search_url: null });
            Throws(() => new NpcTargetSnapshot(DateTimeOffset.UtcNow, entry with
                { Player = entry.Player! with { HomeWorldName = "Example World" } }, allowPlayersAndObjects: true));
        });
        Test("unavailable and stale targets contain no cached target", () =>
        {
            foreach (var snapshot in new[] { NpcTargetSnapshot.Unavailable("No target selected"),
                         new NpcTargetSnapshot(DateTimeOffset.UtcNow.AddSeconds(-31), new("Synthetic", 1, "battle_npc")) })
            {
                var result = new NpcTargetLookup(snapshot).Query(Args("{}"));
                Check(!result.available && result.target is null && result.unavailable_reason is not null);
            }
        });
        Test("all tool arguments rejected except empty object", () =>
        {
            NpcTargetLookup.ValidateArguments(Args("{}"));
            foreach (var argument in new[] { "null", "[]", "{\"id\":1}", "{\"view\":\"location\"}" })
                Throws(() => NpcTargetLookup.ValidateArguments(Args(argument)));
        });
        Test("unsupported kinds identifiers and names cannot enter a snapshot", () =>
        {
            foreach (var target in new NpcTargetEntry[] { new("Aetheryte", 1, "aetheryte"), new("Future", 1, "future"),
                         new("Synthetic", 0, "event_npc"), new("bad\nname", 1, "event_npc"), new(new('x', 129), 1, "battle_npc"),
                         Player() with { DataId = 999 }, Player() with { Player = null } })
                Throws(() => new NpcTargetSnapshot(DateTimeOffset.UtcNow, target, allowPlayersAndObjects: true));
        });
        Test("maximum multilingual player payload fits protocol bound", () =>
        {
            var label = new string('界', 128);
            var entry = new NpcTargetEntry(label, null, "player",
                new(23, label, 19, label, 255, NpcTargetSnapshot.LodestoneSearch(label, label)));
            var result = new NpcTargetLookup(new(DateTimeOffset.UtcNow, entry, allowPlayersAndObjects: true)).Query(Args("{}"));
            Check(JsonSerializer.SerializeToUtf8Bytes(result).Length <= NpcTargetLookup.MaxBytes);
        });
        Console.WriteLine($"{passed} selected-target fixture groups passed; live target transitions require in-game verification.");
    }
    internal static void WriteProducerFixtures(string output)
    {
        static object Fixture(string name, NpcTargetSnapshot snapshot)
        { var arguments = Args("{}"); var lookup = new NpcTargetLookup(snapshot); return new { name, summary = lookup.Summary, arguments, result = lookup.Query(arguments) }; }
        var fixtures = new[] { Fixture("event_npc", new(DateTimeOffset.UtcNow, new("Synthetic Merchant", 123, "event_npc"))),
            Fixture("battle_npc", new(DateTimeOffset.UtcNow, new("Synthetic Enemy", 456, "battle_npc"))),
            Fixture("player", new(DateTimeOffset.UtcNow, Player(), allowNpcs: false, allowPlayersAndObjects: true)),
            Fixture("event_object", new(DateTimeOffset.UtcNow, new("Synthetic Object", 789, "event_object"), allowNpcs: false, allowPlayersAndObjects: true)),
            Fixture("unavailable", NpcTargetSnapshot.Unavailable("Select a permitted supported target")),
            Fixture("stale", new(DateTimeOffset.UtcNow.AddSeconds(-31), new("Synthetic", 789, "event_npc"))) };
        var path = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(fixtures));
    }
    private static JsonElement Args(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private static void Check(bool value) { if (!value) throw new Exception("Assertion failed"); }
    private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    private static void Throws(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Expected invalid input rejection"); }
}
