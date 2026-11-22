using System.Runtime.Loader;
using System.Text.Json;
using CompAI;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

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
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-10-20T16:00:00Z");
    private static int passed;
    internal static void Run()
    {
        Test("pinned book bounds differ from equipped slots", NativeBounds);
        Test("API uses book IDs and explicit Pet references supply names", () =>
        {
            var calls = new List<uint>();
            var result = MasterBestiaryReader.Describe(Time,
                [new(1, "Cu Sith", 47), new(5, "opo-opo", 52), new(50, "behemoth", 97)],
                id => { calls.Add(id); return new(true, true); }, expectedUnlockedCount: 3);
            Check(result.Available && calls.SequenceEqual([1u, 5u, 50u]));
            Check(result.Entries.Select(row => row.Id).SequenceEqual([1u, 5u, 50u]));
            Check(result.Entries[1].Name == "opo-opo");
        });
        Test("false stays unknown and a disagreement invalidates the whole result", () =>
        {
            var negative = MasterBestiaryReader.Describe(Time, [new(1, "Cu Sith", 47)], _ => new(false, false));
            Check(negative.Entries[0].State == "not_observed_unlocked" && negative.Entries[0].Unlocked is null);
            foreach (var pair in new[] { new BestiaryUnlockObservation(true, false), new(false, true) })
                Check(!MasterBestiaryReader.Describe(Time, [new(1, "Cu Sith", 47)], _ => pair).Available);
        });
        Test("unloaded and unsupported entries never call native", () =>
        {
            Check(!MasterBestiaryReader.Describe(Time, ThrowingDefinitions(), _ => throw new Exception(), loaded: false).Available);
            var result = MasterBestiaryReader.Describe(Time, [new(51, "Future familiar", 98), new(2, "Missing link", 0)],
                _ => throw new Exception("Unexpected native call"));
            Check(result.Entries.All(row => row.State == "unknown" && row.Unlocked is null));
        });
        Test("full public catalog requires exactly unique supported rows and Pet links", () =>
        {
            var definitions = Enumerable.Range(1, 50).Select(id => new BestiaryDefinition((uint)id, "Familiar " + id, (uint)(id + 100))).ToArray();
            Check(MasterBestiaryReader.IsSupportedCatalog(definitions));
            Check(!MasterBestiaryReader.IsSupportedCatalog(definitions.Take(49)));
            Check(!MasterBestiaryReader.IsSupportedCatalog(definitions.Append(new(51, "Future familiar", 151))));
            Check(!MasterBestiaryReader.IsSupportedCatalog(definitions.Select(row => row with { PetId = 101 })));
            Check(!MasterBestiaryReader.IsSupportedCatalog(definitions.Select(row => row.Id == 1 ? row with { Name = "Bad\nName" } : row)));
        });
        Test("invalid catalog rows and duplicate IDs fail before native reads", () =>
        {
            foreach (var rows in new BestiaryDefinition[][] {
                [], [new(0, "Zero ID", 47)], [new(1, "Bad\nName", 47)],
                [new(1, "First", 47), new(1, "Duplicate", 48)] })
                Check(!MasterBestiaryReader.Describe(Time, rows, _ => throw new Exception()).Available);
        });
        Test("catalog positives must match the received manager count", () =>
        {
            foreach (var count in new[] { -1, 0, 2, 51 })
                Check(!MasterBestiaryReader.Describe(Time, [new(1, "Cu Sith", 47)], _ => new(true, true), expectedUnlockedCount: count).Available);
            Check(MasterBestiaryReader.Describe(Time, [new(1, "Cu Sith", 47)], _ => new(true, true), expectedUnlockedCount: 1).Available);
        });
        Test("only the ordinary received stable notebook passes readiness", () =>
        {
            var ready = Ready();
            Check(MasterBestiaryReader.IsStable(ready, ready));
            foreach (var changed in new[] {
                ready with { State = XBMManager.DataState.None }, ready with { State = XBMManager.DataState.Requested },
                ready with { UsesContentPetData = true }, ready with { ContentId = 1 }, ready with { Source = 1 },
                ready with { MenuReady = false }, ready with { PlayerLoaded = false }, ready with { AddonId = 0 },
                ready with { RequestPetListRebuild = true }, ready with { RequestNotebookRebuild = true },
                ready with { RequestNotebookDetailRebuild = true }, ready with { NumUnlockedPets = 51 },
                ready with { NotebookFingerprint = "01000000000000" }, ready with { ManagerFingerprint = "" } })
                Check(!changed.IsReady && !MasterBestiaryReader.IsStable(ready, changed));
        });
        Test("owner pointers receipt flags count and buffer changes invalidate capture", () =>
        {
            var ready = Ready();
            foreach (var changed in new[] {
                ready with { PlayerAddress = 9 }, ready with { ManagerAddress = 9 }, ready with { NotebookAddress = 9 },
                ready with { PlayerContentId = 9 }, ready with { AddonId = 9 }, ready with { NumUnlockedPets = 1 },
                ready with { HasNewUnlockedPets = true }, ready with { IsNewPetListReceived = true },
                ready with { ManagerFingerprint = "01000000000000", NotebookFingerprint = "01000000000000" } })
                Check(changed.IsReady && !MasterBestiaryReader.IsStable(ready, changed));
        });
        Test("lookup IDs are book IDs and pagination preserves catalog counts", () =>
        {
            var lookup = new CollectionLookup(Small());
            Check(lookup.Query(Args("""{"category":"beastmaster_bestiary","ids":[47,52,97]}""")).entries.Length == 0);
            var result = lookup.Query(Args("""{"category":"beastmaster_bestiary","limit":1}"""));
            Check(result.catalog_count == 3 && result.observed_unlocked_count == 1 && result.not_observed_unlocked_count == 1 &&
                result.unknown_count == 1 && result.next_offset == 1 && result.coverage.Contains("receipt marker") &&
                result.source == "XBMManager.IsPetUnlocked; visible ordinary AgentXBMMonsterNotebook; Experimental.XBMPet.RowId/Pet.Name");
        });
        Test("unavailable counts remain null", () =>
        {
            var result = new CollectionLookup(CollectionSnapshot.Unavailable(MasterBestiaryReader.Category, "Unloaded"))
                .Query(Args("""{"category":"beastmaster_bestiary"}"""));
            Check(result.entries.Length == 0 && result.catalog_count is null && result.observed_unlocked_count is null &&
                result.not_observed_unlocked_count is null && result.unknown_count is null);
        });
        Console.WriteLine($"PASS {passed} Master's Bestiary checks");
    }
    private static unsafe void NativeBounds()
    {
        XBMManager manager = default;
        AgentXBMMonsterNotebook notebook = default;
        ActionManager actions = default;
        Check(manager.UnlockedPetsBitArray.BitCount == 50 && manager.UnlockedPets.Length == 7 &&
            notebook.UnlockedPetsBitArray.BitCount == 50 && notebook.UnlockedPets.Length == 7 && actions.BeastmasterPets.Length == 3);
    }
    private static BestiaryMenuObservation Ready() => new(1, 2, 3, 4, true, true, 5, XBMManager.DataState.Received,
        0, false, 0, 0, false, false, false, false, false, "00000000000000", "00000000000000");
    private static IEnumerable<BestiaryDefinition> ThrowingDefinitions() =>
        Enumerable.Range(0, 1).Select<int, BestiaryDefinition>(_ => throw new Exception("Unexpected definition enumeration"));
    private static CollectionSnapshot Small() => MasterBestiaryReader.Describe(Time,
        [new(1, "Cu Sith", 47), new(5, "opo-opo", 52), new(51, "Future familiar", 98)], id => new(id == 1, id == 1));
    internal static void Produce(string output)
    {
        object Fixture(string name, CollectionSnapshot snapshot, string json)
        {
            var arguments = Args(json);
            return new { name, arguments, result = new CollectionLookup(snapshot).Query(arguments) };
        }
        var fixtures = new[] {
            Fixture("bestiary_states", Small(), """{"category":"beastmaster_bestiary"}"""),
            Fixture("bestiary_book_id", Small(), """{"category":"beastmaster_bestiary","ids":[1]}"""),
            Fixture("bestiary_name", Small(), """{"category":"beastmaster_bestiary","query":"opo"}"""),
            Fixture("bestiary_negative", Small(), """{"category":"beastmaster_bestiary","state":"not_observed_unlocked"}"""),
            Fixture("bestiary_unknown", Small(), """{"category":"beastmaster_bestiary","state":"unknown"}"""),
            Fixture("bestiary_first_page", Small(), """{"category":"beastmaster_bestiary","limit":1}"""),
            Fixture("bestiary_last_page", Small(), """{"category":"beastmaster_bestiary","offset":2,"limit":2}"""),
            Fixture("bestiary_unavailable", CollectionSnapshot.Unavailable(MasterBestiaryReader.Category, "Unloaded"),
                """{"category":"beastmaster_bestiary"}"""),
            Fixture("bestiary_last_supported_id", MasterBestiaryReader.Describe(Time, [new(50, "behemoth", 97)], _ => new(true, true)),
                """{"category":"beastmaster_bestiary","ids":[50]}"""),
            Fixture("bestiary_disagreement", MasterBestiaryReader.Describe(Time, [new(1, "Cu Sith", 47)], _ => new(true, false)),
                """{"category":"beastmaster_bestiary"}"""),
        };
        var path = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(fixtures, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static JsonElement Args(string text) => JsonDocument.Parse(text).RootElement.Clone();
    private static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
    private static void Test(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
}
