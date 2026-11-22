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
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-10-20T12:00:00Z");
    private static int passed;
    internal static void Run()
    {
        Test("installed native bounds match 49 bits in 7 bytes", () =>
        {
            var player = new PlayerState();
            Check(player.CompletedVVDNotebookContents.Length == VariantNotebookReader.CompletionByteCount);
            Check(player.CompletedVVDNotebookContentsBitArray.BitCount == VariantNotebookReader.CompletionBitCount);
        });
        Test("catalog reuse requires both sheet identities game data and client language", () =>
        {
            var contents = new object(); var series = new object(); var gameData = new object();
            var catalog = new VariantNotebookCatalog(contents, series, gameData, "English", Catalog());
            Check(catalog.Matches(contents, series, gameData, "English"));
            Check(!catalog.Matches(new object(), series, gameData, "English"));
            Check(!catalog.Matches(contents, new object(), gameData, "English"));
            Check(!catalog.Matches(contents, series, new object(), "English"));
            Check(!catalog.Matches(contents, series, gameData, "Japanese"));
        });
        Test("catalog derives global IDs and per-series record numbers from explicit links", () =>
        {
            var rows = Catalog();
            Check(rows.Length == 49);
            Check(rows[12] is { Id: 13, SeriesId: 2, RecordNumber: 1 });
            Check(rows[48] is { Id: 49, SeriesId: 4, RecordNumber: 13 });
            Check(rows[12].Name == "Synthetic series 2 — Record 1: Synthetic title 13");
            // A reordered public link is honored instead of deriving the record number from its ID.
            var groups = Groups();
            var ids = groups[1].ContentIds.ToArray();
            (ids[0], ids[1]) = (ids[1], ids[0]);
            groups[1] = groups[1] with { ContentIds = ids };
            var moved = VariantNotebookReader.BuildCatalog(Contents(), groups);
            Check(moved[12].RecordNumber == 2 && moved[13].RecordNumber == 1);
        });
        Test("zero-based bits map first byte edges and final notebook ID", () =>
        {
            var bits = new byte[] { 0x81, 0x01, 0, 0, 0, 0x80, 0x01 };
            var snapshot = VariantNotebookReader.Describe(Time, Catalog(), bits, bits);
            Check(snapshot.Entries.Where(row => row.Completed == true).Select(row => row.Id).SequenceEqual([1u, 8u, 9u, 48u, 49u]));
            Check(snapshot.Entries.Where(row => row.Completed is null).All(row => row.State == "not_observed_complete" && row.UnknownReason is not null));
        });
        Test("all bits set is bounded to named records and zero bits stay unknown", () =>
        {
            var all = Enumerable.Repeat((byte)255, 7).ToArray();
            var positive = VariantNotebookReader.Describe(Time, Catalog(), all, all);
            Check(positive.Entries.Count == 49 && positive.Entries.All(row => row.Completed == true));
            var empty = VariantNotebookReader.Describe(Time, Catalog(), new byte[7], new byte[7]);
            Check(empty.Available && empty.Entries.All(row => row.Completed is null));
        });
        Test("unloaded source and unstable or short bitmaps fail before catalog enumeration", () =>
        {
            IEnumerable<VariantNotebookDefinition> Forbidden() { throw new Exception("Enumerated unavailable catalog"); }
            Check(!VariantNotebookReader.Describe(Time, ForbiddenLazy(), [], [], loaded: false).Available);
            Check(!VariantNotebookReader.Describe(Time, ForbiddenLazy(), new byte[6], new byte[6]).Available);
            Check(!VariantNotebookReader.Describe(Time, ForbiddenLazy(), new byte[7], new byte[] { 1, 0, 0, 0, 0, 0, 0 }).Available);
            IEnumerable<VariantNotebookDefinition> ForbiddenLazy() { foreach (var row in Forbidden()) yield return row; }
        });
        Test("invalid catalog and series links fail closed", () =>
        {
            Throws(() => VariantNotebookReader.BuildCatalog(Contents().Skip(1), Groups()));
            Throws(() => VariantNotebookReader.BuildCatalog(Contents().Append(new(50, "Future entry")), Groups()));
            Throws(() => VariantNotebookReader.BuildCatalog(Contents().Select(row => row.Id == 49 ? row with { Id = 48 } : row), Groups()));
            Throws(() => VariantNotebookReader.BuildCatalog(Contents(), Groups().Take(3)));
            foreach (var replacement in new uint[][]
            {
                [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 50, 0],
                [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 11, 0],
                [1, 0, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 0],
                [1, 2],
            })
            {
                var groups = Groups(); groups[0] = groups[0] with { ContentIds = replacement };
                Throws(() => VariantNotebookReader.BuildCatalog(Contents(), groups));
            }
            var invalid = Groups(); invalid[0] = invalid[0] with { Name = "Bad\nname" };
            Throws(() => VariantNotebookReader.BuildCatalog(Contents(), invalid));
        });
        Test("catalog enumeration is bounded even for future input", () =>
        {
            var enumerated = 0;
            IEnumerable<VariantNotebookContent> Rows() { for (uint id = 1; ; id++) { enumerated++; yield return new(id, "Synthetic"); } }
            Throws(() => VariantNotebookReader.BuildCatalog(Rows(), Groups()));
            Check(enumerated == 50);
        });
        Test("snapshot copies bits and forbids negative or numeric completion claims", () =>
        {
            var bits = new byte[] { 1, 0, 0, 0, 0, 0, 1 };
            var snapshot = VariantNotebookReader.Describe(Time, Catalog(), bits, bits);
            bits[0] = 0; bits[6] = 0;
            Check(snapshot.Entries[0].Completed == true && snapshot.Entries[48].Completed == true);
            Throws(() => new ProgressionSnapshot("variant_notebook", Time, true, "positive_observations_only", [new(1, "Synthetic", "not_completed", false)]));
            Throws(() => new ProgressionSnapshot("variant_notebook", Time, true, "positive_observations_only", [new(1, "Synthetic", "completed", true, Progress: 1)]));
        });
        Test("invalid IDs and duplicated record identities never index bits", () =>
        {
            foreach(var definitions in new VariantNotebookDefinition[][]
            {
                [new(0, 1, 1, "Zero")], [new(50, 4, 13, "Future")], [new(uint.MaxValue, 1, 1, "Overflow")],
                [new(1, 1, 1, "One"), new(2, 1, 1, "Same displayed identity")],
            }) Check(!VariantNotebookReader.Describe(Time, definitions, new byte[7], new byte[7]).Available);
        });
        Test("lookup filters keep displayed record numbers separate from global IDs", () =>
        {
            var snapshot = Small();
            var byId = new ProgressionLookup(snapshot).Query(Args("""{"category":"variant_notebook","ids":[13]}"""));
            Check(byId.entries.Single().name.Contains("series 2 — Record 1:"));
            var byName = new ProgressionLookup(snapshot).Query(Args("""{"category":"variant_notebook","query":"series 4 record 13"}"""));
            Check(byName.entries.Single().id == 49);
            Check(byId.catalog_count == 49 && byId.not_completed_count == 0 && byId.not_observed_complete_count == 47);
            Check(!JsonSerializer.Serialize(byId).Contains("observed_at_utc"));
        });
        Console.WriteLine($"Passed {passed} Variant notebook checks.");
    }

    private static VariantNotebookContent[] Contents() => Enumerable.Range(1, 49).Select(id => new VariantNotebookContent((uint)id, $"Synthetic title {id}")).ToArray();
    private static VariantNotebookSeriesDefinition[] Groups() => Enumerable.Range(1, 4).Select(id =>
        new VariantNotebookSeriesDefinition((uint)id, $"Synthetic series {id}", Enumerable.Range(0, 13)
            .Select(index => index == 12 && id != 4 ? 0u : (uint)((id - 1) * 12 + index + 1)).ToArray())).ToArray();
    private static VariantNotebookDefinition[] Catalog() => VariantNotebookReader.BuildCatalog(Contents(), Groups());
    private static ProgressionSnapshot Small()
    {
        var bits = new byte[] { 1, 0, 0, 0, 0, 0, 1 };
        return VariantNotebookReader.Describe(Time, Catalog(), bits, bits);
    }
    internal static void Produce(string output)
    {
        var queries = new[] {
            """{"category":"variant_notebook"}""", """{"category":"variant_notebook","ids":[1,13,49]}""",
            """{"category":"variant_notebook","query":"series 4 record 13"}""",
            """{"category":"variant_notebook","state":"completed"}""",
            """{"category":"variant_notebook","state":"not_observed_complete","limit":2}""",
            """{"category":"variant_notebook","offset":48,"limit":1}""",
        };
        var fixtures = queries.Select((query, i) => new { name = "variant_" + i, arguments = Args(query), result = new ProgressionLookup(Small()).Query(Args(query)) }).ToList();
        var unavailable = ProgressionSnapshot.Unavailable("variant_notebook", "Synthetic unloaded character");
        fixtures.Add(new { name = "variant_unavailable", arguments = Args(queries[0]), result = new ProgressionLookup(unavailable).Query(Args(queries[0])) });
        var full = Path.GetFullPath(output); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
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
