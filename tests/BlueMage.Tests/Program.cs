using System.Runtime.Loader;
using System.Text.Json;
using CompAI;
using FFXIVClientStructs.FFXIV.Client.Game;
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
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-10-20T16:00:00Z");
    private static int passed;
    internal static void Run()
    {
        Test("pinned learned-link and equipped-array bounds are distinct", NativeBounds);
        Test("displayed spell numbers map to independent action and unlock-link IDs", () =>
        {
            var calls = new List<uint>();
            var snapshot = BlueMageReader.Describe(Time,
                [new(25, "Snort", 1, 11383, 102), new(1, "Water Cannon", 3, 11385, 104),
                 new(124, "Being Mortal", 124, 34582, 461)],
                link => { calls.Add(link); return link is 104 or 461; });
            Check(calls.SequenceEqual([102u, 104u, 461u]));
            Check(snapshot.Entries.Select(row => row.Id).SequenceEqual([1u, 25u, 124u]));
            Check(snapshot.Entries[0].Name == "Water Cannon" && snapshot.Entries[0].Unlocked == true);
            Check(snapshot.Entries[1].Name == "Snort" && snapshot.Entries[1].Unlocked is null);
            Check(snapshot.Entries[2].Unlocked == true);
        });
        Test("zero missing and unsupported mappings never become native calls", () =>
        {
            var calls = new List<uint>();
            var result = BlueMageReader.Describe(Time,
                [new(1, "Last valid link", 1, 11385, 735), new(2, "Zero link", 2, 11386, 0),
                 new(3, "No link", 3, 11387, null), new(4, "Past bound", 4, 11388, 736),
                 new(5, "No action", 5, 0, 105), new(6, "No Aoz row", 0, 11390, 106),
                 new(7, "Oversize Aoz row", 4097, 11391, 107), new(256, "Oversize number", 8, 11392, 108)],
                link => { calls.Add(link); return true; });
            Check(calls.SequenceEqual([735u]));
            Check(result.Entries.Count(row => row.State == "unknown") == 7);
        });
        Test("unloaded spell state exposes no remembered rows and never calls native", () =>
        {
            var result = BlueMageReader.Describe(Time, [new(1, "Water Cannon", 3, 11385, 104)],
                _ => throw new Exception("Unexpected native call"), loaded: false);
            Check(!result.Available && result.Entries.Count == 0);
        });
        Test("native false stays unknown rather than claiming unlearned", () =>
        {
            var result = BlueMageReader.Describe(Time, [new(1, "Water Cannon", 3, 11385, 104)], _ => false);
            Check(result.Entries[0].State == "not_observed_unlocked" && result.Entries[0].Unlocked is null &&
                result.Entries[0].UnknownReason!.Contains("does not mean unlearned"));
        });
        Test("ambiguous displayed numbers invalid text and oversized catalogs fail closed", () =>
        {
            Check(!BlueMageReader.Describe(Time, [new(1, "First", 1, 1, 102), new(1, "Second", 2, 2, 103)], _ => true).Available);
            Check(!BlueMageReader.Describe(Time, [new(1, "Invalid\nName", 1, 1, 102)], _ => true).Available);
            Check(!BlueMageReader.Describe(Time, [new(0, "Zero Number", 1, 1, 102)], _ => true).Available);
            Check(!BlueMageReader.Describe(Time,
                Enumerable.Range(1, 4097).Select(id => new BlueMageDefinition((uint)id, "Oversize", (uint)id, (uint)id, 102)),
                _ => true).Available);
        });
        Test("ID filters use displayed number and never accept another ID domain implicitly", () =>
        {
            var lookup = new CollectionLookup(Small());
            var water = lookup.Query(Args("""{"category":"blue_mage_spells","ids":[1]}"""));
            Check(water.entries.Length == 1 && water.entries[0].name == "Water Cannon");
            Check(lookup.Query(Args("""{"category":"blue_mage_spells","ids":[3,104,11385]}""")).entries.Length == 0);
        });
        Test("all three observation states page without overstating missing totals", () =>
        {
            var lookup = new CollectionLookup(Small());
            var first = lookup.Query(Args("""{"category":"blue_mage_spells","limit":1}"""));
            var last = lookup.Query(Args("""{"category":"blue_mage_spells","offset":2,"limit":2}"""));
            Check(first.catalog_count == 3 && first.observed_unlocked_count == 1 &&
                first.not_observed_unlocked_count == 1 && first.unknown_count == 1 && first.next_offset == 1);
            Check(last.entries[0].state == "unknown" && last.next_offset is null && !last.truncated);
            Check(first.coverage.Contains("not equipped") && first.source.Contains("displayed spellbook number"));
        });
        Test("unavailable output keeps all catalog and observation counts unknown", () =>
        {
            var result = new CollectionLookup(CollectionSnapshot.Unavailable("blue_mage_spells", "Unloaded"))
                .Query(Args("""{"category":"blue_mage_spells"}"""));
            Check(result.catalog_count is null && result.observed_unlocked_count is null &&
                result.not_observed_unlocked_count is null && result.unknown_count is null && result.entries.Length == 0);
        });
        Console.WriteLine($"PASS {passed} Blue Mage checks");
    }

    private static unsafe void NativeBounds()
    {
        UIState ui = default;
        ActionManager actionManager = default;
        Check(ui.UnlockLinksBitArray.BitCount == BlueMageReader.UnlockLinkBitCount);
        Check(actionManager.BlueMageActions.Length == 24);
    }
    private static CollectionSnapshot Small() => BlueMageReader.Describe(Time,
        [new(25, "Snort", 1, 11383, 102), new(1, "Water Cannon", 3, 11385, 104),
         new(124, "Unsupported Synthetic Spell", 124, 34582, null)], link => link == 104);
    internal static void Produce(string output)
    {
        object Fixture(string name, CollectionSnapshot snapshot, string json)
        {
            var arguments = Args(json);
            return new { name, arguments, result = new CollectionLookup(snapshot).Query(arguments) };
        }
        var fixtures = new[]
        {
            Fixture("blue_mage_states", Small(), """{"category":"blue_mage_spells"}"""),
            Fixture("blue_mage_display_number", Small(), """{"category":"blue_mage_spells","ids":[1]}"""),
            Fixture("blue_mage_name", Small(), """{"category":"blue_mage_spells","query":"snort"}"""),
            Fixture("blue_mage_negative", Small(), """{"category":"blue_mage_spells","state":"not_observed_unlocked"}"""),
            Fixture("blue_mage_unknown", Small(), """{"category":"blue_mage_spells","state":"unknown"}"""),
            Fixture("blue_mage_first_page", Small(), """{"category":"blue_mage_spells","limit":1}"""),
            Fixture("blue_mage_last_page", Small(), """{"category":"blue_mage_spells","offset":2,"limit":2}"""),
            Fixture("blue_mage_unavailable", CollectionSnapshot.Unavailable("blue_mage_spells", "Unloaded"),
                """{"category":"blue_mage_spells"}"""),
            Fixture("blue_mage_nonlinear_link", BlueMageReader.Describe(Time,
                [new(124, "Being Mortal", 124, 34582, 461)], link => link == 461),
                """{"category":"blue_mage_spells"}"""),
        };
        var path = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(fixtures, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static JsonElement Args(string text) => JsonDocument.Parse(text).RootElement.Clone();
    private static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
    private static void Test(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
}
