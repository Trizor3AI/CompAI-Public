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
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-10-23T10:00:00Z");
    private static int passed;
    private static OccultRecordDefinition[] Occult => [new(2, "Synthetic record two", 633), new(1, "Synthetic record one", 507)];
    private static YokaiMinionDefinition[] Yokai => [new(14, "Synthetic minion fourteen", 392), new(15, "Synthetic minion fifteen", 391)];
    private static CollectionToolDefinition[] Tools => [new(2, "Synthetic Field Record tool", 3), new(3, "Synthetic Medallium tool", 2)];

    internal static void Run()
    {
        Test("installed native bounds match supported unlock domains", NativeBounds);
        Test("Occult Record uses UnlockLink rather than notebook row or position", () =>
        {
            var calls = new List<uint>();
            var result = OccultRecordReader.Describe(Time, Occult, id => { calls.Add(id); return id == 633; });
            Check(calls.SequenceEqual([507u, 507u, 633u, 633u]) && result.Entries[1].Unlocked == true);
            Check(result.Entries[0].Unlocked is null && result.Entries[0].State == "not_observed_unlocked");
        });
        Test("Yo-kai joins the nonsequential Companion IDs", () =>
        {
            var calls = new List<uint>();
            var result = YokaiCollectionReader.Describe(Time, Yokai, id => { calls.Add(id); return id == 391; });
            Check(calls.SequenceEqual([392u, 392u, 391u, 391u]) && result.Entries[1].Id == 15 && result.Entries[1].Unlocked == true);
            Check(result.Entries[0].Unlocked is null);
        });
        Test("Collection tools pass McGuffin RowId rather than UIData ID", () =>
        {
            var calls = new List<uint>();
            var result = CollectionToolReader.Describe(Time, Tools, id => { calls.Add(id); return id == 2; });
            Check(calls.SequenceEqual([2u, 2u, 3u, 3u]) && result.Entries[0].Unlocked == true && result.Entries[1].Unlocked is null);
        });
        Test("invalid mappings never call native helpers", () =>
        {
            bool Unexpected(uint _) => throw new Exception("Native helper called for invalid mapping");
            Check(!OccultRecordReader.Describe(Time, [new(1, "Bad", 736)], Unexpected).Available);
            Check(!OccultRecordReader.Describe(Time, [new(1, "Bad", 0)], Unexpected).Available);
            Check(!YokaiCollectionReader.Describe(Time, [new(1, "Bad", 600)], Unexpected).Available);
            Check(!YokaiCollectionReader.Describe(Time, [new(1, "Bad", 0)], Unexpected).Available);
            Check(!CollectionToolReader.Describe(Time, [new(256, "Bad", 2)], Unexpected).Available);
            Check(!CollectionToolReader.Describe(Time, [new(1, "Bad", 0)], Unexpected).Available);
        });
        Test("ambiguous duplicate IDs and invalid text fail closed", () =>
        {
            Throws(() => OccultRecordReader.BuildCatalog([new(1, "A", 507), new(2, "B", 507)]));
            Throws(() => YokaiCollectionReader.BuildCatalog([new(1, "A", 200), new(1, "B", 201)]));
            Throws(() => YokaiCollectionReader.BuildCatalog([new(1, "A", 200), new(2, "B", 200)]));
            Throws(() => CollectionToolReader.BuildCatalog([new(1, "A", 1), new(2, "B", 1)]));
            Throws(() => OccultRecordReader.BuildCatalog([new(1, "Bad\nname", 507)]));
        });
        Test("unloaded and changing native state discard complete snapshots", () =>
        {
            bool Unexpected(uint _) => throw new Exception("Read while unloaded");
            Check(!OccultRecordReader.Describe(Time, Occult, Unexpected, false).Available);
            Check(!YokaiCollectionReader.Describe(Time, Yokai, Unexpected, false).Available);
            Check(!CollectionToolReader.Describe(Time, Tools, Unexpected, false).Available);
            var calls = 0;
            Check(!OccultRecordReader.Describe(Time, Occult, _ => ++calls != 4).Available);
            calls = 0;
            Check(!YokaiCollectionReader.Describe(Time, Yokai, _ => ++calls != 4).Available);
            calls = 0;
            var changed = CollectionToolReader.Describe(Time, Tools, _ => ++calls != 4);
            Check(!changed.Available && changed.Entries.Count == 0);
        });
        Test("empty and oversized catalogs fail closed", () =>
        {
            Throws(() => OccultRecordReader.BuildCatalog([]));
            Throws(() => YokaiCollectionReader.BuildCatalog([]));
            Throws(() => CollectionToolReader.BuildCatalog([]));
            Throws(() => OccultRecordReader.BuildCatalog(Enumerable.Range(1, 256).Select(i => new OccultRecordDefinition((uint)i, "A", (uint)i))));
            Throws(() => YokaiCollectionReader.BuildCatalog(Enumerable.Range(1, 129).Select(i => new YokaiMinionDefinition((uint)i, "A", (uint)i))));
        });
        Test("queries retain explicit limited coverage and never produce false unlocks", () =>
        {
            foreach (var snapshot in Samples())
            {
                var result = Query(snapshot);
                Check(result.available && result.entries.Length == 2 && result.entries.All(row => row.unlocked is not false));
                Check(result.readiness == "positive_observations_only" && result.source.Length > 0 && result.coverage.Length > 0);
            }
        });
        Console.WriteLine($"Collection-menu checks passed: {passed}.");
    }

    private static unsafe void NativeBounds()
    {
        UIState ui = default;
        Check(ui.UnlockLinksBitArray.BitCount == OccultRecordReader.UnlockLinkBitCount);
        Check(ui.UnlockedCompanionsBitArray.BitCount == YokaiCollectionReader.CompanionBitCount);
    }
    private static CollectionSnapshot[] Samples() =>
    [
        OccultRecordReader.Describe(Time, Occult, id => id == 633),
        YokaiCollectionReader.Describe(Time, Yokai, id => id == 391),
        CollectionToolReader.Describe(Time, Tools, id => id == 2),
    ];
    private static CollectionLookupResult Query(CollectionSnapshot snapshot)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { category = snapshot.Category }));
        return new CollectionLookup(snapshot).Query(document.RootElement);
    }
    internal static void Produce(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(Samples().ToDictionary(snapshot => snapshot.Category, Query)));
        Console.WriteLine($"Wrote synthetic collection-menu fixtures: {fullPath}");
    }
    private static void Test(string name, Action run) { run(); ++passed; Console.WriteLine("PASS " + name); }
    private static void Check(bool condition) { if (!condition) throw new Exception("Check failed"); }
    private static void Throws(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new Exception("Expected invalid public catalog rejection");
    }
}
