using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using CompAI;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

var libraryPath = Environment.GetEnvironmentVariable("DALAMUD_LIB_PATH") ??
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "addon", "Hooks", "dev");
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var path = Path.Combine(libraryPath, name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
Checks.Run();
if (args is ["--produce-fixtures", var output]) Checks.Produce(output);
if (args is ["--public-catalog", var sqpack]) PublicCatalogProbe.Run(sqpack);

internal static class Checks
{
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-10-23T10:00:00Z");
    private static int passed;
    internal static void Run()
    {
        Test("installed native array bounds and owner gate", NativeBounds);
        Test("native getter routing uses detached copies and preserves parentage ordering", GoldSaucerCopyChecks.Run);
        Test("loaded race observation projects displayed current/max stats and full pedigree", () =>
        {
            var race = Race();
            var result = GoldSaucerReader.DescribeRace(Time, race, race, id => $"Name {id}", id => $"Ability {id}", ColorName, RatingClass);
            Check(result.Available && result.Entries.Count == 32);
            Check(result.Entries.Single(r => r.Id == 2).Required == 600);
            Check(result.Entries.Single(r => r.Id == 10).Value == 141 && result.Entries.Single(r => r.Id == 10).Required == 300 && result.Entries.Single(r => r.Id == 10).Unit == "stat");
            Check(result.Entries.Single(r => r.Id == 20).ReferenceId == 7);
            Check(result.Entries.Single(r => r.Id == 21).ReferenceId is null);
            Check(result.Entries.Single(r => r.Id == 4).Value == 67);
            Check(result.Entries.Single(r => r.Id == 5).ReferenceId == 5 && result.Entries.Single(r => r.Id == 5).Text == "R-80");
            Check(result.Entries.Single(r => r.Id == 32).ReferenceId == 37 && result.Entries.Single(r => r.Id == 32).ReferenceKind == "stain");
            Check(result.Entries.Single(r => r.Id == 40).Value == 4 && result.Entries.Single(r => r.Id == 41).Value == 8);
            Check(result.Entries.Single(r => r.Id == 61).Value == 1 && result.Entries.Single(r => r.Id == 71).Value == 2);
            Check(!JsonSerializer.Serialize(new ActivityLookup(result).Query(Args("""{"category":"race_chocobo"}"""))).Contains("Owner"));
        });
        Test("race changes and unsupported values discard every row", () =>
        {
            var race = Race();
            foreach (var bad in new[] { race with { Rank = 0 }, race with { Current = race.Current with { MaximumSpeed = 501 } }, race with { Experience = -1 },
                race with { Experience = 601 }, race with { NameFirst = 0 }, race with { Pedigree = 10 },
                race with { Maximum = race.Maximum with { Stamina = 99 } }, race with { Rating = 501 },
                race with { FatherStars = race.FatherStars with { Acceleration = 0 } },
                race with { MotherStars = race.MotherStars with { Cunning = 5 } } })
                Check(!GoldSaucerReader.DescribeRace(Time, bad, bad, _ => "Name", _ => "Ability", ColorName, RatingClass).Available);
            Check(!GoldSaucerReader.DescribeRace(Time, race, race with { Rank = 18 }, _ => "Name", _ => "Ability", ColorName, RatingClass).Available);
            Check(!GoldSaucerReader.DescribeRace(Time, race, race, _ => "bad\nname", _ => "Ability", ColorName, RatingClass).Available);
        });
        Test("rating ranges are inclusive and ambiguity or unresolved public names remain unknown", () =>
        {
            var classes = new GoldSaucerRaceClass[] { new(2, 1, 20, "Maiden"), new(3, 21, 40, "R-40"), new(14, 241, 300, "R-300") };
            Check(GoldSaucerReader.ResolveRatingClass(20, classes)?.Id == 2 && GoldSaucerReader.ResolveRatingClass(21, classes)?.Id == 3);
            Check(GoldSaucerReader.ResolveRatingClass(300, classes)?.Id == 14 && GoldSaucerReader.ResolveRatingClass(301, classes) is null);
            Check(GoldSaucerReader.ResolveRatingClass(30, [.. classes, new(4, 25, 40, "Overlap")]) is null);
            Check(GoldSaucerReader.ResolveRatingClass(30, [new(17, 21, 60, "Unnamed alternate")]) is null);
            var race = Race() with { Rating = 301, Color = 0 };
            var result = GoldSaucerReader.DescribeRace(Time, race, race, _ => "Name", _ => "Ability", _ => null, RatingClass);
            Check(result.Available && result.Entries.Single(r => r.Id == 5).ReferenceId is null);
            Check(result.Entries.Single(r => r.Id == 32).Text == "No color reference in the loaded record.");
            race = race with { Color = 37 };
            result = GoldSaucerReader.DescribeRace(Time, race, race, _ => "Name", _ => "Ability", _ => null, RatingClass);
            Check(result.Entries.Single(r => r.Id == 32).Text == "Unresolved chocobo color #37");
        });
        Test("resolved rating class rejects a contradictory racer rating", () =>
        {
            var race = Race();
            var current = GoldSaucerReader.DescribeRace(Time, race, race, _ => "Name", _ => "Ability", ColorName, RatingClass);
            // Each row remains individually valid: rating 20 cannot belong to the resolved R-80 class (61..80).
            var contradictory = current.Entries.Select(row => row.Id == 4 ? row with { Value = 20 } : row);
            Throws(() => new ActivitySnapshot("race_chocobo", Time, true, contradictory));
        });
        Test("deck ordering and repeated references are retained without ownership inference", () =>
        {
            var config = Decks();
            var result = GoldSaucerReader.DescribeConfiguration(Time, "triple_triad_decks", config, config, 7, id => $"Card {id}");
            Check(result.Entries.Count == 25 && result.Entries[0].Group == "deck_1" && result.Entries[5].Group == "deck_2");
            Check(result.Entries[0].ReferenceId == 1 && result.Entries[1].ReferenceId == 1 && result.Entries[0].Text == "Synthetic deck");
            Check(result.Entries[4].Slot == 5 && result.Entries[24].Slot == 5 && result.Entries[24].ReferenceId is null);
            config.References[0] = 5;
            Check(result.Entries[0].ReferenceId == 1);
        });
        Test("owner mismatch changing arrays malformed names and unsupported card IDs fail closed", () =>
        {
            var first = Decks(); var other = Decks();
            Check(!GoldSaucerReader.StableConfiguration(first, other, 8, "triple_triad_decks"));
            other.References[24] = 475;
            Check(!GoldSaucerReader.StableConfiguration(first, other, 7, "triple_triad_decks"));
            first.References[0] = 476;
            Check(!GoldSaucerReader.StableConfiguration(first, first, 7, "triple_triad_decks"));
            first = Decks(); first.Names[0] = Enumerable.Repeat((byte)65, 48).ToArray();
            Check(!GoldSaucerReader.DescribeConfiguration(Time, "triple_triad_decks", first, first, 7, _ => "Card").Available);
            Check(GoldSaucerReader.DecodeDeckName([0xFF, 0, .. new byte[46]]) is null);
            Check(GoldSaucerReader.DecodeDeckName([10, 0, .. new byte[46]]) is null);
        });
        Test("Verminion references use Companion namespace and exactly 23 ordered slots", () =>
        {
            var config = new GoldSaucerConfiguration(7, new ushort[23], []); config.References[22] = 599;
            var result = GoldSaucerReader.DescribeConfiguration(Time, "verminion", config, config, 7, _ => "Synthetic minion");
            Check(result.Entries.Count == 23 && result.Entries[22].ReferenceKind == "companion" && result.Entries[22].Slot == 23);
            Check(result.Entries[0].Text == "No minion reference in this observed slot.");
            Check(!GoldSaucerReader.StableConfiguration(config with { References = new ushort[24] }, config, 7, "verminion"));
        });
        Test("lookup filtering paging and metadata do not imply missing or claimed state", () =>
        {
            var config = Decks();
            var lookup = new ActivityLookup(GoldSaucerReader.DescribeConfiguration(Time, "triple_triad_decks", config, config, 7, id => $"Card {id}"));
            var page = lookup.Query(Args("""{"category":"triple_triad_decks","group":"deck_1","limit":2}"""));
            Check(page.entry_count == 25 && page.matched_count == 5 && page.entries.Length == 2 && page.next_offset == 2);
            var exact = lookup.Query(Args("""{"category":"triple_triad_decks","query":"Synthetic","ids":[1,2]}"""));
            Check(exact.entries.Length == 2 && exact.next_offset is null && exact.readiness == "visible_menu_observation");
            var unavailable = new ActivityLookup(ActivitySnapshot.Unavailable("mahjong", "Unmapped installed fields"))
                .Query(Args("""{"category":"mahjong"}"""));
            Check(!unavailable.available && unavailable.entry_count is null && unavailable.entries.Length == 0);
        });
        Test("strict query shape rejects duplicates unsupported fields and out of bounds", () =>
        {
            var lookup = new ActivityLookup(ActivitySnapshot.Unavailable("mahjong", "Unmapped installed fields"));
            foreach (var json in new[] { "[]", "{}", """{"category":"invalid"}""", """{"category":"mahjong","limit":51}""",
                """{"category":"mahjong","ids":[1,1]}""", """{"category":"mahjong","category":"mahjong"}""",
                """{"category":"mahjong","offset":513}""", """{"category":"mahjong","state":"all"}""",
                """{"category":"mahjong","ids":[0]}""", """{"category":"mahjong","limit":"2"}""",
                """{"category":"mahjong","query":"bad\nquery"}""", """{"category":"mahjong","group":null}""" })
            {
                Throws(() => ActivityLookup.ValidateArguments(Args(json)));
                Throws(() => ActivityLookup.Category(Args(json)));
                Throws(() => ActivityLookup.ParseArguments(Args(json)));
                Throws(() => lookup.Query(Args(json)));
            }
        });
        Test("parsed queries own validated options and preserve filtering paging and category checks", () =>
        {
            var config = Decks();
            var lookup = new ActivityLookup(GoldSaucerReader.DescribeConfiguration(Time, "triple_triad_decks", config, config, 7, id => $"Card {id}"));
            const string json = """{"category":"triple_triad_decks","query":" Synthetic ","group":"deck_1","ids":[1,2,3,4,5],"offset":1,"limit":2}""";
            ActivityArguments options;
            using (var document = JsonDocument.Parse(json)) options = ActivityLookup.ParseArguments(document.RootElement);
            var page = lookup.Query(options); // The original JSON document is already disposed.
            Check(page.entries.Select(row => row.id).SequenceEqual([2u, 3u]) && page.matched_count == 5 && page.next_offset == 3);
            Check(JsonSerializer.Serialize(page) == JsonSerializer.Serialize(lookup.Query(Args(json))));
            Check(JsonSerializer.Serialize(page) == JsonSerializer.Serialize(lookup.Query(options)));
            Throws(() => lookup.Query(ActivityLookup.ParseArguments(Args("""{"category":"mahjong"}"""))));
            Throws(() => lookup.Query(Args("""{"category":"mahjong"}""")));
        });
        Test("last-observed promotion shares only validated immutable entries and preserves source time", () =>
        {
            var config = Decks();
            var rows = GoldSaucerReader.DescribeConfiguration(Time, "triple_triad_decks", config, config, 7, id => $"Card {id}").Entries.ToArray();
            var current = new ActivitySnapshot("triple_triad_decks", Time, true, rows.Reverse());
            var saved = current.AsLastObserved();
            Check(!current.LastObserved && saved.LastObserved && saved.CapturedAtUtc == current.CapturedAtUtc);
            Check(ReferenceEquals(current.Entries, saved.Entries) && saved.Entries[0].Id == 1);
            rows[0] = rows[0] with { Name = "Changed external array" };
            Check(current.Entries[0].Name != rows[0].Name && saved.Entries[0].Name != rows[0].Name);
            var writeRejected = false;
            try { ((IList<ActivityEntry>)saved.Entries)[0] = rows[0]; }
            catch (NotSupportedException) { writeRejected = true; }
            Check(writeRejected && ReferenceEquals(current.Entries[0], saved.Entries[0]));
            Throws(() => ActivitySnapshot.Unavailable("mahjong", "Unmapped installed fields").AsLastObserved());
            Throws(() => saved.AsLastObserved());
        });
        Test("retained snapshots expire and reject future capture times", () =>
        {
            var race = Race();
            var current = GoldSaucerReader.DescribeRace(Time, race, race, _ => "Name", _ => "Ability", ColorName, RatingClass);
            var saved = new ActivityLookup(new("race_chocobo", Time, true, current.Entries, lastObserved: true));
            Check(saved.IsUsable(Time) && saved.IsUsable(Time.AddMinutes(14)) && !saved.IsUsable(Time.AddMinutes(15)) && !saved.IsUsable(Time.AddSeconds(-1)));
            Throws(() => new ActivitySnapshot("race_chocobo", Time, true, current.Entries.Skip(1)));
            Throws(() => new ActivitySnapshot("triple_triad_decks", Time, true, []));
            Throws(() => new ActivitySnapshot("mahjong", Time, true, []));
        });
        Console.WriteLine($"PASS {passed} activity checks");
    }

    private static unsafe void NativeBounds()
    {
        GoldSaucerModule module = default;
        Check(module.Decks.Length == 10 && module.HotbarMinions.Length == 23 && module.Decks[0].Cards.Length == 5 && module.Decks[0].Name.Length == 48);
        module.CharacterContentId = 7;
        module.Decks[0].Cards[0] = 475;
        module.Decks[5].Cards[0] = 476; // Non-playable allocation must not enter validation or observations.
        module.Decks[9].Cards[4] = ushort.MaxValue;
        Check(GoldSaucerReader.CopyConfiguration(&module, 8, "triple_triad_decks") is null);
        var decks = GoldSaucerReader.CopyConfiguration(&module, 7, "triple_triad_decks")!;
        Check(decks.References[0] == 475 && decks.References.Length == 25 && decks.Names.Length == 5);
        Check(GoldSaucerReader.DescribeConfiguration(Time, "triple_triad_decks", decks, decks, 7, _ => "Card").Entries.Count == 25);
        RaceChocoboManager native = default;
        Check(GoldSaucerReader.CopyRace(&native) is null);
        native.State = RaceChocoboManager.RaceChocoboState.Requested;
        Check(GoldSaucerReader.CopyRace(&native) is null);
        native.State = RaceChocoboManager.RaceChocoboState.Loaded;
        Check(GoldSaucerReader.CopyRace(&native) is null);
    }

    private static GoldSaucerRace Race() => new(17, 150, 600, 1, 2, new(141, 90, 85, 132, 77), new(300, 220, 220, 300, 220), new(4, 2, 2, 4, 2), new(4, 1, 2, 3, 2), new(3, 2, 1, 4, 1), 7, 0, true, false, 4, 67, 37, 8);
    private static string ColorName(uint id) => $"Synthetic color {id}";
    private static GoldSaucerRaceClass? RatingClass(int rating) => GoldSaucerReader.ResolveRatingClass(rating, [new(5, 61, 80, "R-80")]);
    private static GoldSaucerConfiguration Decks()
    {
        var names = Enumerable.Range(0, 5).Select(_ => new byte[48]).ToArray();
        foreach (var name in names) Encoding.UTF8.GetBytes("Synthetic deck").CopyTo(name, 0);
        var cards = new ushort[25]; cards[0] = cards[1] = 1; cards[4] = 475;
        return new(7, cards, names);
    }
    internal static void Produce(string output)
    {
        object Fixture(string name, ActivitySnapshot snapshot, string query)
        { var arguments = Args(query); return new { name, arguments, result = new ActivityLookup(snapshot).Query(arguments) }; }
        var race = Race(); var decks = Decks(); var minions = new GoldSaucerConfiguration(7, new ushort[23], []); minions.References[0] = 5;
        var fixtures = new[]
        {
            Fixture("race_chocobo", GoldSaucerReader.DescribeRace(Time, race, race, id => $"Synthetic name {id}", id => $"Synthetic ability {id}", ColorName, RatingClass), """{"category":"race_chocobo"}"""),
            Fixture("triple_triad_decks", GoldSaucerReader.DescribeConfiguration(Time, "triple_triad_decks", decks, decks, 7, id => $"Synthetic card {id}"), """{"category":"triple_triad_decks"}"""),
            Fixture("verminion", GoldSaucerReader.DescribeConfiguration(Time, "verminion", minions, minions, 7, id => $"Synthetic minion {id}"), """{"category":"verminion"}"""),
            Fixture("mahjong_unavailable", ActivitySnapshot.Unavailable("mahjong", "Unmapped installed fields"), """{"category":"mahjong"}"""),
        };
        File.WriteAllText(output, JsonSerializer.Serialize(fixtures));
        Console.WriteLine($"Wrote {fixtures.Length} synthetic C# activity fixtures");
    }
    private static JsonElement Args(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
    private static void Test(string name, Action action) { action(); ++passed; Console.WriteLine("PASS " + name); }
    private static void Check(bool condition) { if (!condition) throw new Exception("Fixture failed"); }
    private static void Throws(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Expected rejection"); }
}


