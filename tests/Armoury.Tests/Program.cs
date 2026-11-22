using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using CompAI;
using Dalamud.Game.Inventory;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "XIVLauncher", "addon", "Hooks", "dev", name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
Tests.Run(args);

internal static unsafe class Tests
{
    private static int passed;
    private static void Check(bool value) { if (!value) throw new Exception("Armoury assertion failed."); }
    private static void Test(string name, Action run) { run(); passed++; Console.WriteLine("PASS " + name); }
    private static void Reject(Action run)
    {
        try { run(); } catch (InvalidOperationException) { return; }
        throw new Exception("Expected unavailable capture.");
    }
    private static ArmouryObservedItem Item(int slot = 3, InventoryType type = InventoryType.ArmoryMainHand,
        int quantity = 1, bool hq = true)
    {
        var native = new InventoryItem { Container = type, Slot = (short)slot, ItemId = 5121, Quantity = quantity,
            Flags = hq ? InventoryItem.ItemFlags.HighQuality : 0 };
        native.Materia[1] = 27;
        native.MateriaGrades[1] = 9;
        native.Materia[3] = 999;
        native.MateriaGrades[3] = 11;
        // Native materia spans use the inspected fixed struct, with no vtable or game singleton calls.
        return new(false, (GameInventoryType)native.Container, native.Slot,
            native.ItemId + (hq ? 1000000u : 0u), native.ItemId, native.Quantity, hq,
            native.Materia.ToArray(), native.MateriaGrades.ToArray());
    }
    private static ArmouryObservedItem[] Slots()
    {
        var result = Enumerable.Repeat(new ArmouryObservedItem(true, 0, 0, 0, 0, 0, false, [], []), 50).ToArray();
        result[3] = Item();
        return result;
    }
    private static StorageItem[] Describe(ArmouryObservedItem[] slots, InventoryType type = InventoryType.ArmoryMainHand) =>
        ArmouryReader.DescribeItems(slots, type, _ => "Synthetic weapon",
            (id, grade) => id == 27 && grade == 9 ? (33931, "Synthetic materia X") : (null, null));

    internal static void Run(string[] args)
    {
        Test("disabled Armoury permission performs no native reads", () =>
            Check(new ArmouryReader(null!, null!).Capture(123, false).Count == 0));
        Test("inspected allowlist contains twelve physical containers without worn gear or gearsets", () =>
        {
            Check(ArmouryReader.Types.Length == 12 && ArmouryReader.Types.Distinct().Count() == 12);
            Check(!ArmouryReader.Types.Contains(InventoryType.EquippedItems) &&
                !ArmouryReader.Types.Contains(InventoryType.ArmoryWaist) &&
                !ArmouryReader.Types.Contains(InventoryType.RetainerEquippedItems));
            Check((int)InventoryType.ArmoryMainHand == 3500 && (int)InventoryType.ArmoryRings == 3300);
        });
        Test("loaded container and exact owner guards reject unknown sources", () =>
        {
            var native = new InventoryItem();
            var container = new InventoryContainer { Items = &native, Size = 50, IsLoaded = true, Type = InventoryType.ArmoryMainHand };
            Check(ArmouryReader.ContainerReady(&container, container.Type));
            container.IsLoaded = false; Check(!ArmouryReader.ContainerReady(&container, container.Type));
            container.IsLoaded = true; container.Size = 51; Check(!ArmouryReader.ContainerReady(&container, container.Type));
            container.Size = 50; Check(!ArmouryReader.ContainerReady(&container, InventoryType.ArmoryRings));
            var player = new PlayerState { IsLoaded = true, ContentId = 123 };
            Check(ArmouryReader.OwnerReady(&player, 123) && !ArmouryReader.OwnerReady(&player, 456) &&
                !ArmouryReader.OwnerReady(&player, 0) && !ArmouryReader.OwnerReady(null, 123));
            player.IsLoaded = false; Check(!ArmouryReader.OwnerReady(&player, 123));
        });
        Test("installed inventory wrappers own value copies and equality includes materia and grades", () =>
        {
            GameInventoryItem Wrap(InventoryItem native) => (GameInventoryItem)typeof(GameInventoryItem)
                .GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, [typeof(InventoryItem)], null)!
                .Invoke([native]);
            var native = new InventoryItem { Container = InventoryType.ArmoryMainHand, Slot = 3, ItemId = 5121, Quantity = 1 };
            native.Materia[1] = 27; native.MateriaGrades[1] = 9;
            var before = Wrap(native);
            var independent = Wrap(native);
            Check(before.Equals(independent));
            native.Materia[1] = 28;
            Check(before.Equals(independent) && !before.Equals(Wrap(native)));
            native.Materia[1] = 27; native.MateriaGrades[1] = 10;
            Check(!before.Equals(Wrap(native)));
            native.MateriaGrades[1] = 9; native.Quantity = 2;
            Check(!before.Equals(Wrap(native)));
        });
        Test("native scalar copies preserve HQ physical slot and all materia gaps", () =>
        {
            var row = Describe(Slots()).Single();
            Check(row.Slot == 3 && row.Quantity == 1 && row.IsHq && row.ItemId == 1005121 && row.BaseItemId == 5121);
            Check(row.Materia is { Length: 5 } && row.Materia[0].IsEmpty && row.Materia[2].IsEmpty &&
                row.Materia[1] == new StorageMateria(1, 27, 9, 33931, "Synthetic materia X", false) &&
                row.Materia[3] == new StorageMateria(3, 999, 11, null, null, false));
        });
        Test("incorrect slot type quantity and unexpected capacity fail closed", () =>
        {
            var slots = Slots();
            slots[3] = Item(slot: 4); Reject(() => Describe(slots));
            slots[3] = Item(type: InventoryType.EquippedItems); Reject(() => Describe(slots));
            slots[3] = Item(quantity: 2); Reject(() => Describe(slots));
            Reject(() => Describe(new ArmouryObservedItem[51]));
            Reject(() => Describe(Slots(), InventoryType.ArmoryWaist));
            Reject(() => ArmouryReader.CopyMateria(new ushort[4], new byte[5], (_, _) => (null, null)));
            Reject(() => ArmouryReader.CopyMateria([27, 0, 0, 0, 0], [21, 0, 0, 0, 0], (_, _) => (null, null)));
            Check(Describe(Enumerable.Repeat(new ArmouryObservedItem(true, 0, 0, 0, 0, 0, false, [], []), 50).ToArray()).Length == 0);
        });
        var capture = new StorageContainerCapture("armoury", "personal", "Armoury Chest", "ArmoryMainHand",
            50, Describe(Slots()), DateTimeOffset.UtcNow);
        Test("managed snapshots validate materia and compare cloned arrays by content", () =>
        {
            Check(StorageSnapshot.Valid(capture));
            var copy = StorageSnapshot.CloneItems(capture.Items);
            Check(StorageSnapshot.SameItems(copy, capture.Items) && !ReferenceEquals(copy[0].Materia, capture.Items[0].Materia));
            copy[0].Materia![1] = copy[0].Materia![1] with { Grade = 10 };
            Check(!StorageSnapshot.SameItems(copy, capture.Items));
            Check(!StorageSnapshot.Valid(capture with { Items = [capture.Items[0] with { Materia = null }] }));
            Check(!StorageSnapshot.Valid(capture with { Items = [capture.Items[0] with { Quantity = 2 }] }));
            Check(!StorageSnapshot.Valid(capture with { Items = [capture.Items[0] with { ItemId = 5121 }] }));
            Check(!StorageSnapshot.Valid(capture with { Items = [capture.Items[0] with { UnitPriceGil = 100 }] }));
            Check(StorageBrowser.ContainerLabel(capture.Container) == "Main hands" &&
                StorageBrowser.CategoryLabel("armoury") == "Armoury Chest");
        });
        var fixtures = new List<object>();
        JsonElement Query(string name, StorageLookup lookup, string view = "items", string category = "armoury")
        {
            var arguments = JsonSerializer.SerializeToElement(new { view, category, query = "", source = "", offset = 0, limit = 30 });
            var result = lookup.Query(arguments);
            fixtures.Add(new { name, summary = lookup.Summary, arguments, result });
            return JsonSerializer.SerializeToElement(result);
        }
        Test("frozen lookup owns materia copies and permissions reject Armoury queries", () =>
        {
            var lookup = new StorageLookup([capture], ["armoury"]);
            var before = Query("armoury items", lookup).GetRawText();
            capture.Items[0].Materia![1] = capture.Items[0].Materia![1] with { Name = "Changed after freezing" };
            Check(before == Query("armoury frozen", lookup).GetRawText());
            Query("armoury containers", lookup, "containers");
            var denied = new StorageLookup([capture], []);
            try { Query("denied", denied); throw new Exception("Permission accepted."); } catch (ArgumentException) { }
            Check(Query("unknown armoury", new StorageLookup([], ["armoury"]), "containers").GetProperty("total_matches").GetInt32() == 0);
            Query("observed empty armoury", new StorageLookup([capture with { Items = [] }], ["armoury"]), "containers");
        });
        Test("Armoury cannot enter a sale valuation snapshot", () =>
        {
            try { _ = new ValuationLookup([], ["armoury"]); throw new Exception("Armoury valuation accepted."); }
            catch (ArgumentException) { }
        });
        if (args.Length == 2 && args[0] == "--write-fixture")
            File.WriteAllText(args[1], JsonSerializer.Serialize(fixtures));
        Console.WriteLine($"{passed} Armoury groups passed.");
    }
}
