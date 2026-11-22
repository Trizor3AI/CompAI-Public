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
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static readonly GlamourCabinetDefinition[] Cabinet = [new(0, 101), new(5, 102), new(5047, 103)];
    private static readonly GlamourOutfitDefinition[] Outfits = [new(500, [0, 0, 201, 202, 203, 204, 205, 0, 0, 0, 206])];
    private static int passed;
    private static string? Name(uint id) => id is > 0 and < 600 ? $"Synthetic item {id}" : null;
    private static GlamourStorageSlot[] Slots() => new GlamourStorageSlot[GlamourReader.DresserSlots];
    private static uint[] Plates() => new uint[GlamourReader.PlateCount * GlamourReader.PlateSlots];

    internal static void Run()
    {
        Test("installed typed storage and plate bounds", () =>
        {
            var manager = new MirageManager();
            Check(manager.PrismBoxItemIds.Length == GlamourReader.DresserSlots);
            Check(manager.GlamourPlates.Length == GlamourReader.PlateCount);
            Check(manager.GlamourPlates[0].ItemIds.Length == GlamourReader.PlateSlots);
        });
        Test("armoire row zero and high cabinet IDs are positive membership with no quantity", () =>
        {
            var snapshot = GlamourReader.DescribeArmoire(Time, Cabinet, [true, false, true], [true, false, true], Name);
            Check(snapshot.Available && snapshot.Entries.Select(row => row.ItemId).SequenceEqual([101u, 103u]));
            Check(snapshot.Entries.All(row => row.Quantity is null && row.Role == "stored" && row.IsHq is null));
            Check(snapshot.Entries.Select(row => row.Slot).SequenceEqual([0, 5047]));
            Check(snapshot.Entries.Select(row => row.Id).SequenceEqual([1u, 2u]));
        });
        Test("armoire unloaded changing or malformed state fails closed", () =>
        {
            Check(!GlamourReader.DescribeArmoire(Time, Cabinet, [false, false, false], [false, false, false], Name, false).Available);
            Check(!GlamourReader.DescribeArmoire(Time, Cabinet, [true, false, false], [false, false, false], Name).Available);
            Check(!GlamourReader.DescribeArmoire(Time, [new(0, 101), new(0, 102)], [true, true], [true, true], Name).Available);
            Check(!GlamourReader.DescribeArmoire(Time, [new(0, 999)], [true], [true], Name).Available);
            var empty = GlamourReader.DescribeArmoire(Time, Cabinet, [false, false, false], [false, false, false], Name);
            Check(empty.Available && empty.Entries.Count == 0 && empty.Readiness.Contains("unknown"));
        });
        Test("dresser reads final slot independently of visible pages and deduplicates membership", () =>
        {
            var slots = Slots(); slots[0] = new(101, 0); slots[300] = new(1_000_101, 0); slots[799] = new(102, 0);
            var snapshot = GlamourReader.DescribeDresser(Time, Outfits, slots, slots, Name);
            Check(snapshot.Available && snapshot.Entries.Select(row => row.ItemId).SequenceEqual([101u, 102u]));
            Check(snapshot.Entries.All(row => row.Role == "stored" && row.Quantity is null && row.IsHq is null));
            Check(snapshot.Readiness.Contains("all_800_storage_slots"));
        });
        Test("outfit pieces use explicit columns and positive bits only", () =>
        {
            var slots = Slots(); slots[0] = new(500, (1 << 2) | (1 << 10)); slots[1] = new(201, 0);
            var snapshot = GlamourReader.DescribeDresser(Time, Outfits, slots, slots, Name);
            Check(snapshot.Available && snapshot.Entries.Select(row => row.ItemId).SequenceEqual([201u, 206u]));
            Check(snapshot.Entries.All(row => row.ItemId != 500 && row.ItemId != 202));
            slots[0] = new(500, 0);
            var partial = GlamourReader.DescribeDresser(Time, Outfits, slots, slots, Name);
            Check(partial.Available && partial.Entries.Count == 1 && partial.Readiness.Contains("absent pieces unknown"));
        });
        Test("dresser changes unknown names and impossible outfit states fail closed", () =>
        {
            var first = Slots(); var second = Slots(); first[2] = new(101, 0);
            Check(!GlamourReader.DescribeDresser(Time, Outfits, first, second, Name).Available);
            Check(!GlamourReader.DescribeDresser(Time, Outfits, first, first, Name, false).Available);
            foreach (var invalid in new GlamourStorageSlot[] { new(0, 1), new(101, 1), new(500, 1), new(500, 1 << 11), new(999, 0), new(2_000_101, 0) })
            {
                var slots = Slots(); slots[0] = invalid;
                Check(!GlamourReader.DescribeDresser(Time, Outfits, slots, slots, Name).Available);
            }
            Check(!GlamourReader.DescribeDresser(Time, [new(500, [])], Slots(), Slots(), Name).Available);
            var emptySet = Slots(); emptySet[0] = new(500, 0);
            Check(!GlamourReader.DescribeDresser(Time, [new(500, new uint[11])], emptySet, emptySet, Name).Available);
        });
        Test("plate references retain repeated locations but never claim ownership or quantity", () =>
        {
            var plates = Plates(); plates[0] = 101; plates[12] = 101; plates[^1] = 1_000_102;
            var snapshot = GlamourReader.DescribePlates(Time, plates, plates, Name);
            Check(snapshot.Available && snapshot.Entries.Count == 3);
            Check(snapshot.Entries.All(row => row.Role == "appearance_reference" && row.Quantity is null && row.UnknownReason is not null));
            Check(snapshot.Entries[^1] is { ItemId: 102, GroupId: 19, Slot: 11 });
        });
        Test("unready changing malformed and unknown plates fail closed", () =>
        {
            var first = Plates(); var second = Plates(); first[0] = 101;
            Check(!GlamourReader.DescribePlates(Time, first, second, Name).Available);
            Check(!GlamourReader.DescribePlates(Time, first, first, Name, false).Available);
            Check(!GlamourReader.DescribePlates(Time, first[..^1], first[..^1], Name).Available);
            first[0] = 999; Check(!GlamourReader.DescribePlates(Time, first, first, Name).Available);
            first[0] = 2_000_101; Check(!GlamourReader.DescribePlates(Time, first, first, Name).Available);
            Check(GlamourReader.DescribePlates(Time, Plates(), Plates(), Name).Entries.Count == 0);
        });
        Test("twenty full saved plates fit the shared reader budget without losing ownership caveats", () =>
        {
            var slots = Plates(); Array.Fill(slots, 101u);
            var snapshot = GlamourReader.DescribePlates(Time, slots, slots, _ => new string('A', 128));
            var lookup = new MenuLookup(snapshot);
            var offset = 0; var rows = 0; var bytes = 0; var pages = 0;
            do
            {
                var page = lookup.Query(Args(JsonSerializer.Serialize(new { category = "glamour_plates", offset })));
                bytes += JsonSerializer.SerializeToUtf8Bytes(page).Length; pages++; rows += page.entries.Length;
                Check(page.coverage.Contains("not proof") && page.entries.All(row => row.role == "appearance_reference" &&
                    row.quantity is null && row.unknown_reason is null));
                offset = page.next_offset ?? -1;
            } while (offset >= 0);
            Check(rows == 240 && pages <= 6 && bytes < 120000);
            var special = new MenuSnapshot(snapshot.Category, Time, true, snapshot.Source, snapshot.Readiness,
                [snapshot.Entries[0] with { UnknownReason = "Specific unsupported appearance metadata." }]);
            Check(new MenuLookup(special).Query(Args("""{"category":"glamour_plates"}""")).entries[0].unknown_reason is not null);
        });
        Test("lookup keeps separate category semantics and explicit unavailable coverage", () =>
        {
            var result = new MenuLookup(Dresser()).Query(Args("""{"category":"glamour_dresser","item_ids":[201]}"""));
            Check(result.available && result.entries.Length == 1 && result.entries[0].quantity is null && result.entries[0].role == "stored");
            var unavailable = GlamourReader.DescribePlates(Time, Plates(), Plates(), Name, false);
            var query = new MenuLookup(unavailable).Query(Args("""{"category":"glamour_plates"}"""));
            Check(!query.available && query.catalog_count is null && query.entries.Length == 0 && query.unavailable_reason is not null);
            Check(query.coverage.Contains("not proof"));
        });
        Console.WriteLine($"Glamour checks passed: {passed}");
    }

    private static MenuSnapshot Dresser()
    {
        var slots = Slots(); slots[0] = new(101, 0); slots[799] = new(500, (1 << 2) | (1 << 10));
        return GlamourReader.DescribeDresser(Time, Outfits, slots, slots, Name);
    }

    internal static void Produce(string output)
    {
        var plateRows = Plates(); plateRows[0] = 101; plateRows[12] = 101;
        var snapshots = new[] {
            GlamourReader.DescribeArmoire(Time, Cabinet, [true, false, true], [true, false, true], Name),
            Dresser(), GlamourReader.DescribePlates(Time, plateRows, plateRows, Name),
            GlamourReader.DescribeArmoire(Time, Cabinet, [false, false, false], [false, false, false], Name, false),
            GlamourReader.DescribeDresser(Time, Outfits, Slots(), Slots(), Name, false),
            GlamourReader.DescribePlates(Time, Plates(), Plates(), Name, false),
        };
        var fixtures = snapshots.Select((snapshot, index) =>
        {
            var arguments = Args(JsonSerializer.Serialize(new { category = snapshot.Category }));
            return new { name = "glamour_" + index, arguments, result = new MenuLookup(snapshot).Query(arguments) };
        }).ToArray();
        var full = Path.GetFullPath(output); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, JsonSerializer.Serialize(fixtures, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Glamour producer fixtures: {fixtures.Length}");
    }
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
}
