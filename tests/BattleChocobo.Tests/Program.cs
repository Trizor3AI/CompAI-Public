using System.Runtime.Loader;
using System.Text.Json;
using CompAI;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

var libraryPath = Environment.GetEnvironmentVariable("DALAMUD_LIB_PATH") ??
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "addon", "Hooks", "dev");
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var path = Path.Combine(libraryPath, name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
Checks.Run();
if (args is ["--produce-fixtures", var output]) Checks.Produce(output);

internal static class Checks
{
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-10-23T12:00:00Z");
    private static readonly BattleChocoboObservation Sample = new(8, 1234, 2, 5, 0, 7);
    private static int passed;

    internal static void Run()
    {
        Test("native copy uses the exact battle-companion fields and skill order", NativeCopy);
        Test("rank catalog validates every supported rank and skill domain", () =>
        {
            var ranks = BattleChocoboReader.BuildCatalog(Ranks().Reverse(), Skills().Reverse());
            Check(ranks.Length == 20 && ranks[0].Rank == 1 && ranks[^1] is { Rank: 20, ExperienceRequired: 0 });
            Throws(() => BattleChocoboReader.BuildCatalog(Ranks().Skip(1), Skills()));
            Throws(() => BattleChocoboReader.BuildCatalog(Ranks().Append(Ranks()[0]), Skills()));
            Throws(() => BattleChocoboReader.BuildCatalog(Ranks().Select(row => row.Rank == 20 ? row with { ExperienceRequired = 1 } : row), Skills()));
            Throws(() => BattleChocoboReader.BuildCatalog(Ranks(), Skills().Where(row => row.Id != 10)));
            Throws(() => BattleChocoboReader.BuildCatalog(Ranks(), Skills().Select(row => row.Id == 3 ? row with { Level = 2 } : row)));
        });
        Test("catalog identity includes both sheets game data and client language", () =>
        {
            var rankSheet = new object(); var skillSheet = new object(); var gameData = new object();
            var catalog = new BattleChocoboCatalog(rankSheet, skillSheet, gameData, "English", Ranks());
            Check(catalog.Matches(rankSheet, skillSheet, gameData, "English"));
            Check(!catalog.Matches(new object(), skillSheet, gameData, "English"));
            Check(!catalog.Matches(rankSheet, new object(), gameData, "English"));
            Check(!catalog.Matches(rankSheet, skillSheet, new object(), "English"));
            Check(!catalog.Matches(rankSheet, skillSheet, gameData, "Japanese"));
        });
        Test("five fixed metric IDs preserve distinct fields and real zero skill ranks", () =>
        {
            var snapshot = Describe(Sample);
            Check(snapshot.Available && snapshot.Readiness == "loaded_companion_observation" && snapshot.Entries.Count == 5);
            Check(snapshot.Entries.Select(row => row.Id).SequenceEqual([1u, 2u, 3u, 4u, 5u]));
            Check(snapshot.Entries[0] is { Rank: 8, Experience: 1234, ExperienceRequired: 8000, Progress: null });
            Check(snapshot.Entries[1].Rank == 2 && snapshot.Entries[2].Rank == 5 && snapshot.Entries[3].Rank == 0);
            Check(snapshot.Entries[4] is { Progress: 7, Required: null, Rank: null });
            Check(snapshot.Entries.All(row => row.Completed is null && row.Level is null && row.State == "observed"));
        });
        Test("rank zero and unsupported fields never become locked or empty owned values", () =>
        {
            foreach (var invalid in new[]
            {
                Sample with { Rank = 0 }, Sample with { Rank = 21 }, Sample with { DefenderRank = -1 },
                Sample with { DefenderRank = 11 }, Sample with { AttackerRank = 11 }, Sample with { HealerRank = 11 },
                Sample with { UnspentSkillPoints = -1 }, Sample with { UnspentSkillPoints = 256 },
            }) Check(!Describe(invalid).Available && Describe(invalid).Entries.Count == 0);
        });
        Test("native byte skill point bound is preserved without an invented allocation formula", () =>
        {
            Check(Describe(Sample with { UnspentSkillPoints = 0 }).Entries[4].Progress == 0);
            Check(Describe(Sample with { UnspentSkillPoints = 255 }).Entries[4].Progress == 255);
        });
        Test("experience is checked against the current public rank target", () =>
        {
            Check(Describe(Sample with { Experience = 8000 }).Available);
            Check(!Describe(Sample with { Experience = 8001 }).Available);
            Check(!Describe(Sample with { Experience = uint.MaxValue }).Available);
        });
        Test("maximum rank retains bounded observed EXP without a next-rank target", () =>
        {
            var capped = Describe(Sample with { Rank = 20, Experience = 19000 });
            Check(capped.Available && capped.Entries[0] is { Rank: 20, Experience: 19000, ExperienceRequired: null });
            Check(!Describe(Sample with { Rank = 20, Experience = 19001 }).Available);
        });
        Test("unloaded owner changes and any changed copied field reject the entire observation", () =>
        {
            Check(!BattleChocoboReader.Describe(Time, Ranks(), Sample, Sample, characterLoaded: false).Available);
            Check(!BattleChocoboReader.Describe(Time, Ranks(), Sample, Sample, ownerStable: false).Available);
            Check(!BattleChocoboReader.Describe(Time, Ranks(), null, null).Available);
            foreach (var changed in new[]
            {
                Sample with { Rank = 9 }, Sample with { Experience = 1235 }, Sample with { DefenderRank = 3 },
                Sample with { AttackerRank = 6 }, Sample with { HealerRank = 1 }, Sample with { UnspentSkillPoints = 8 },
            }) Check(!BattleChocoboReader.Describe(Time, Ranks(), Sample, changed).Available);
        });
        Test("invalid or duplicate rank definitions fail closed before lookup", () =>
        {
            Check(!BattleChocoboReader.Describe(Time, Ranks().Skip(1), Sample, Sample).Available);
            Check(!BattleChocoboReader.Describe(Time, Enumerable.Repeat(Ranks()[0], 20), Sample, Sample).Available);
            Check(!BattleChocoboReader.Describe(Time, Ranks().Select(row => row.Rank == 8 ? row with { ExperienceRequired = 0 } : row), Sample, Sample).Available);
        });
        Test("lookup filtering pagination and unavailable counts keep their existing semantics", () =>
        {
            var lookup = new ProgressionLookup(Describe(Sample));
            var page = lookup.Query(Args("""{"category":"battle_chocobo","state":"observed","limit":2}"""));
            Check(page.observed_count == 5 && page.completed_count == 0 && page.entries.Length == 2 && page.next_offset == 2);
            var healer = lookup.Query(Args("""{"category":"battle_chocobo","query":"healer"}"""));
            Check(healer.entries.Length == 1 && healer.entries[0] is { id: 4, rank: 0 });
            Check(lookup.Query(Args("""{"category":"battle_chocobo","ids":[999]}""")).matched_count == 0);
            var unavailable = new ProgressionLookup(Describe(Sample with { Rank = 0 })).Query(Args("""{"category":"battle_chocobo"}"""));
            Check(!unavailable.available && unavailable.catalog_count is null && unavailable.observed_count is null && unavailable.entries.Length == 0);
        });
        Test("snapshot schema rejects metric field confusion and non-observed states", () =>
        {
            var rows = Describe(Sample).Entries.ToArray();
            Throws(() => new ProgressionSnapshot("battle_chocobo", Time, true, "loaded_companion_observation",
                rows.Select(row => row.Id == 2 ? row with { Experience = 1 } : row)));
            Throws(() => new ProgressionSnapshot("battle_chocobo", Time, true, "loaded_companion_observation",
                rows.Select(row => row.Id == 5 ? row with { Rank = 7 } : row)));
            Throws(() => new ProgressionSnapshot("battle_chocobo", Time, true, "loaded_companion_observation",
                rows.Select(row => row.Id == 1 ? row with { State = "completed", Completed = true } : row)));
        });
        Console.WriteLine($"Passed {passed} battle-chocobo checks.");
    }

    private static unsafe void NativeCopy()
    {
        var companion = new CompanionInfo { Rank = 8, CurrentXP = 1234, SkillPoints = 7 };
        Check(companion.Levels.Length == 3);
        companion.Levels[0] = 2; companion.Levels[1] = 5; companion.Levels[2] = 0;
        var copied = BattleChocoboReader.CopyObservation(&companion);
        Check(copied == Sample);
        companion.CurrentXP = 999; companion.Levels[0] = 10;
        Check(copied == Sample);
        // No spawned companion pointer, names, TimeLeft, Stars or feed data is required or copied.
        Throws(() => BattleChocoboReader.CopyObservation(null));
    }

    private static BattleChocoboRankDefinition[] Ranks() => Enumerable.Range(1, 20)
        .Select(rank => new BattleChocoboRankDefinition((uint)rank, rank == 20 ? 0u : (uint)rank * 1000)).ToArray();
    private static BattleChocoboSkillDefinition[] Skills() => Enumerable.Range(0, 11)
        .Select(level => new BattleChocoboSkillDefinition((uint)level, level)).ToArray();
    private static ProgressionSnapshot Describe(BattleChocoboObservation sample) =>
        BattleChocoboReader.Describe(Time, Ranks(), sample, sample);

    internal static void Produce(string output)
    {
        var queries = new[]
        {
            """{"category":"battle_chocobo"}""",
            """{"category":"battle_chocobo","ids":[1,5]}""",
            """{"category":"battle_chocobo","query":"healer"}""",
            """{"category":"battle_chocobo","state":"observed","limit":2}""",
            """{"category":"battle_chocobo","offset":4,"limit":1}""",
            """{"category":"battle_chocobo","ids":[999]}""",
        };
        var fixtures = queries.Select((query, i) => new { name = "battle_chocobo_" + i, arguments = Args(query),
            result = new ProgressionLookup(Describe(Sample)).Query(Args(query)) }).ToList();
        fixtures.Add(new { name = "battle_chocobo_unavailable", arguments = Args(queries[0]),
            result = new ProgressionLookup(ProgressionSnapshot.Unavailable("battle_chocobo", "Synthetic unloaded character")).Query(Args(queries[0])) });
        var full = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, JsonSerializer.Serialize(fixtures, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    private static void Throws(Action action)
    {
        try { action(); } catch (ArgumentException) { return; } catch (InvalidOperationException) { return; }
        throw new Exception("Expected invalid input to fail closed");
    }
}
