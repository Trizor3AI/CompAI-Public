using System.Runtime.Loader;
using System.Text.Json;
using CompAI;
using FFXIVClientStructs.FFXIV.Client.Game;

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
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-10-19T16:00:00Z");
    private static int passed;
    internal static void Run()
    {
        Test("native bounds are structure guards, not Recipe ID bounds", NativeBounds);
        Test("public catalog identities and language must remain the same", () =>
        {
            var notebook = new object(); var recipe = new object(); var item = new object(); var game = new object();
            var catalog = new CraftingLogCatalog(notebook, recipe, item, game, 1, []);
            Check(catalog.Matches(notebook, recipe, item, game, 1));
            Check(!catalog.Matches(new object(), recipe, item, game, 1));
            Check(!catalog.Matches(notebook, new object(), item, game, 1));
            Check(!catalog.Matches(notebook, recipe, new object(), game, 1));
            Check(!catalog.Matches(notebook, recipe, item, new object(), 1));
            Check(!catalog.Matches(notebook, recipe, item, game, 2));
        });
        Test("strict arguments require a public name or bounded unique IDs", () =>
        {
            foreach (var text in new[] {
                """{"query":"ab"}""", """{"ids":[1]}""", """{"query":"","ids":[4294967295]}""",
                """{"query":"ab","ids":[1,2],"offset":65535,"limit":50}""", """{"query":"  ab  ","limit":1}""" })
                CraftingLogLookup.ValidateArguments(Args(text));
            foreach (var text in new[] {
                """{}""", """{"query":""}""", """{"query":"x","ids":[1]}""", """{"query":"\nxx"}""",
                """{"query":"ab","state":"completed"}""", """{"query":"ab","offset":65536}""",
                """{"ids":[0]}""", """{"ids":[1,1]}""", """{"ids":[-1]}""", """{"ids":[4294967296]}""",
                """{"ids":[1.0]}""", """{"ids":[]}""", """{"ids":[true]}""",
                """{"query":"ab","limit":0}""", """{"query":"ab","limit":51}""",
                """{"query":"ab","query":"cd"}""", """{"query":null}""" })
                Throws<ArgumentException>(() => CraftingLogLookup.ValidateArguments(Args(text)));
            Throws<ArgumentException>(() => CraftingLogLookup.ValidateArguments(Args(JsonSerializer.Serialize(new { query = new string('a', 101) }))));
            Throws<ArgumentException>(() => CraftingLogLookup.ValidateArguments(Args(JsonSerializer.Serialize(new { ids = Enumerable.Range(1, 21).ToArray() }))));
        });
        Test("late notebook recipes remain queryable beyond the old snapshot and native bit counts", () =>
        {
            var calls = new List<uint>();
            var session = Session(Catalog(13892), calls);
            Check(calls.Count == 0);
            var result = session.Query(Args("""{"query":"Recipe","offset":13890,"limit":2}"""));
            Check(result.catalog_count == 13892 && result.matched_count == 13892 && result.entries.Length == 2);
            Check(calls.SequenceEqual([13891u, 13892u]) && result.next_offset is null);
            var high = Session([new(30241, "Heavy Wolfram Helm"), new(38282, "Stonewashed Baggy Twill Trousers")], calls)
                .Query(Args("""{"ids":[38282]}"""));
            Check(high.entries.Single().id == 38282 && calls.Last() == 38282);
        });
        Test("combined name and ID filters run before pagination and native calls", () =>
        {
            var calls = new List<uint>();
            var session = Session([new(1, "Bronze Ingot"), new(2, "Bronze Hatchet"), new(3, "Iron Ingot")], calls);
            var result = session.Query(Args("""{"query":"bronze","ids":[1,3]}"""));
            Check(calls.SequenceEqual([1u]) && result.matched_count == 1 && result.catalog_count == 3);
            Check(session.Query(Args("""{"query":"Unlisted Name"}""")).entries.Length == 0 && calls.Count == 1);
            Check(session.Query(Args("""{"ids":[999]}""")).matched_count == 0 && calls.Count == 1);
        });
        Test("only selected rows are observed and each native page is at most50", () =>
        {
            var calls = new List<uint>();
            var pages = new List<int>();
            var session = new CraftingLogSession(Catalog(1000), ids =>
            {
                pages.Add(ids.Count); calls.AddRange(ids);
                return Observation(ids, _ => true);
            });
            var first = session.Query(Args("""{"query":"Recipe","offset":100,"limit":50}"""));
            Check(calls.Count == first.entries.Length && calls.All(id => id is > 100 and <= 150));
            Check(first.matched_count == 1000 && pages.All(count => count <= 50));
            var next = session.Query(Args(JsonSerializer.Serialize(new { query = "Recipe", offset = first.next_offset, limit = 50 })));
            Check(calls.Distinct().Count() == first.entries.Length + next.entries.Length && pages.All(count => count <= 50));
        });
        Test("repeated IDs preserve full observation and timestamp across overlapping queries", () =>
        {
            var calls = new List<uint>();
            var pageNumber = 0;
            var session = new CraftingLogSession(Catalog(3), ids =>
            {
                calls.AddRange(ids);
                return new(Time.AddSeconds(++pageNumber), ids.ToDictionary(id => id, _ => (bool?)(pageNumber == 1)));
            });
            var first = session.Query(Args("""{"ids":[1,2]}"""));
            var second = session.Query(Args("""{"ids":[2,3]}"""));
            var third = session.Query(Args("""{"ids":[1,2]}"""));
            Check(calls.SequenceEqual([1u, 2u, 3u]));
            Check(first.entries[1] == second.entries[0] && first.entries.SequenceEqual(third.entries));
            Check(second.entries[1].completed is null && second.entries[1].state == "not_observed_complete");
            Check(second.entries[0].captured_at_utc != second.entries[1].captured_at_utc);
        });
        Test("positive false and unsupported observations retain distinct nullable states", () =>
        {
            var result = Small().Query(Args("""{"query":"Recipe"}"""));
            Check(result.entries[0].state == "completed" && result.entries[0].completed == true && result.entries[0].unknown_reason is null);
            Check(result.entries[1].state == "not_observed_complete" && result.entries[1].completed is null && result.entries[1].unknown_reason is not null);
            Check(result.entries[2].state == "unknown" && result.entries[2].completed is null && result.entries[2].unknown_reason is not null);
        });
        Test("byte limit trims the public page before observations and preserves pagination", () =>
        {
            var calls = new List<uint>();
            var definitions = Enumerable.Range(1, 50).Select(id => new CraftingLogDefinition((uint)id, "名" + new string('界', 127))).ToArray();
            var session = Session(definitions, calls);
            var first = session.Query(Args("""{"query":"名界","limit":50}"""));
            Check(first.entries.Length is > 0 and < 50 && first.truncated && first.next_offset == first.entries.Length);
            Check(calls.Count == first.entries.Length && JsonSerializer.SerializeToUtf8Bytes(first).Length <= CraftingLogLookup.MaxBytes);
            var next = session.Query(Args(JsonSerializer.Serialize(new { query = "名界", offset = first.next_offset, limit = 50 })));
            Check(next.entries[0].id == first.entries[^1].id + 1);
        });
        Test("600-row answer budget rejects the whole new page without partial reads", () =>
        {
            var calls = new List<uint>();
            var session = Session(Catalog(620), calls);
            for (var start = 1; start <= 600; start += 20)
                session.Query(Args(JsonSerializer.Serialize(new { ids = Enumerable.Range(start, 20).ToArray() })));
            Check(calls.Count == 600);
            Throws<InvalidOperationException>(() => session.Query(Args("""{"ids":[1,601]}""")));
            Check(calls.Count == 600 && session.Query(Args("""{"ids":[1]}""")).entries.Length == 1);
        });
        Test("initial unavailability is frozen with null catalog count and no rows", () =>
        {
            var session = CraftingLogSession.Unavailable("Character unavailable");
            var first = session.Query(Args("""{"ids":[1]}"""));
            var second = session.Query(Args("""{"query":"Recipe","offset":5}"""));
            Check(!first.available && first.catalog_count is null && first.matched_count == 0 && first.entries.Length == 0 &&
                first.next_offset is null && !first.truncated);
            Check(first.unavailable_reason == second.unavailable_reason && first.readiness == second.readiness &&
                first.coverage == second.coverage && first.source == second.source);
        });
        Test("capture failures poison the answer rather than changing available metadata", () =>
        {
            var reads = 0;
            var session = new CraftingLogSession(Catalog(2), ids =>
            {
                if (++reads > 1) throw new InvalidOperationException("Synthetic owner change");
                return Observation(ids, _ => true);
            });
            Check(session.Query(Args("""{"ids":[1]}""")).available);
            Throws<InvalidOperationException>(() => session.Query(Args("""{"ids":[2]}""")));
            Throws<InvalidOperationException>(() => session.Query(Args("""{"ids":[1]}""")));
            Check(reads == 2);
        });
        Test("partial unexpected or duplicate observation domains cannot enter the cache", () =>
        {
            foreach (var returned in new[] { new Dictionary<uint, bool?>(), new Dictionary<uint, bool?> { [2] = true },
                new Dictionary<uint, bool?> { [1] = true, [2] = true } })
            {
                var session = new CraftingLogSession(Catalog(2), _ => new(Time, returned));
                Throws<InvalidOperationException>(() => session.Query(Args("""{"ids":[1]}""")));
                Throws<InvalidOperationException>(() => session.Query(Args("""{"ids":[1]}""")));
            }
        });
        Test("catalog shape duplicate IDs and unsafe text fail closed without native reads", () =>
        {
            foreach (var rows in new CraftingLogDefinition[][] {
                [], [new(0, "Zero")], [new(1, "")], [new(1, "Bad\nName")],
                [new(1, "Duplicate"), new(1, "Duplicate")], [new(1, new string('a',129))] })
                Throws<ArgumentException>(() => new CraftingLogSession(rows, _ => throw new Exception()));
            Throws<ArgumentException>(() => new CraftingLogSession(Catalog(65537), _ => throw new Exception()));
        });
        Test("page counts describe definitions and offset past matches does not imply missing completion", () =>
        {
            var calls = new List<uint>();
            var result = Session(Catalog(10), calls).Query(Args("""{"query":"Recipe","offset":100,"limit":1}"""));
            Check(result.available && result.catalog_count == 10 && result.matched_count == 10 && result.entries.Length == 0 &&
                result.next_offset is null && !result.truncated && calls.Count == 0);
            Check(result.coverage.Contains("not completed or missing recipes") && result.coverage.Length <= 1000);
        });
        Console.WriteLine($"PASS {passed} crafting-log checks");
    }
    private static unsafe void NativeBounds()
    {
        QuestManager quests = default;
        Check(quests.CompletedRecipesBitArray.BitCount == CraftingLogReader.CompletedRecipeBitCount &&
            quests.CompletedRecipes.Length == CraftingLogReader.CompletedRecipeByteCount);
        Check(typeof(QuestManager).GetMethod(nameof(QuestManager.IsRecipeComplete))!.GetParameters()[0].ParameterType == typeof(uint));
        Check(new CraftingLogOwner(1,2,3).IsReady && !new CraftingLogOwner(1,0,3).IsReady);
    }
    private static IEnumerable<CraftingLogDefinition> Catalog(int count) =>
        Enumerable.Range(1, count).Select(id => new CraftingLogDefinition((uint)id, "Recipe " + id));
    private static CraftingLogPageObservation Observation(IReadOnlyList<uint> ids, Func<uint, bool?> observed) =>
        new(Time, ids.ToDictionary(id => id, observed));
    private static CraftingLogSession Session(IEnumerable<CraftingLogDefinition> catalog, List<uint> calls) =>
        new(catalog, ids => { calls.AddRange(ids); return Observation(ids, _ => true); });
    private static CraftingLogSession Small() => new(Catalog(3), ids => Observation(ids, id => id switch { 1 => true, 2 => false, _ => null }));
    internal static void Produce(string output)
    {
        object Fixture(string name, CraftingLogSession session, string json)
        {
            var arguments = Args(json);
            return new { name, arguments, result = session.Query(arguments) };
        }
        var shared = Small();
        var fixtures = new[] {
            Fixture("crafting_states", shared, """{"query":"Recipe"}"""),
            Fixture("crafting_repeated_overlap", shared, """{"ids":[1,2]}"""),
            Fixture("crafting_name_id_intersection", shared, """{"query":"Recipe 2","ids":[1,2]}"""),
            Fixture("crafting_first_page", Small(), """{"query":"Recipe","limit":1}"""),
            Fixture("crafting_last_page", Small(), """{"query":"Recipe","offset":2,"limit":2}"""),
            Fixture("crafting_no_match", Small(), """{"ids":[999]}"""),
            Fixture("crafting_past_end", Small(), """{"query":"Recipe","offset":100}"""),
            Fixture("crafting_unavailable", CraftingLogSession.Unavailable("Character unavailable"), """{"ids":[1]}"""),
            Fixture("crafting_large_catalog", Session(Catalog(13892), []), """{"query":"Recipe","offset":13890,"limit":2}"""),
            Fixture("crafting_high_recipe_id", Session([new(30241, "Heavy Wolfram Helm"), new(38282, "Stonewashed Baggy Twill Trousers")], []),
                """{"ids":[38282]}"""),
            Fixture("crafting_byte_page", Session(Enumerable.Range(1,50).Select(id => new CraftingLogDefinition((uint)id, "名" + new string('界',127))), []),
                """{"query":"名界","limit":50}"""),
        };
        var path = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(fixtures, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static JsonElement Args(string text) => JsonDocument.Parse(text).RootElement.Clone();
    private static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
    private static void Test(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
