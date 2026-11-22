using System.Runtime.Loader;
using CompAI;

var libraryPath = Environment.GetEnvironmentVariable("DALAMUD_LIB_PATH") ??
                  Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                      "XIVLauncher", "addon", "Hooks", "dev");
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var path = Path.Combine(libraryPath, name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};

Tests.Run();
InventoryEventTests.Run();

internal static class Tests
{
    private static int passed;

    internal static void Run()
    {
        Test("loaded empty bags equal; unloaded bags are unavailable", () =>
        {
            var bags = EmptyBags();
            var empty = InventoryStateReader.CopyState(42, bags);
            Check(empty == InventoryStateReader.CopyState(42, EmptyBags()), "loaded empty bags equal");
            for (var bag = 0; bag < bags.Length; bag++)
            {
                var original = bags[bag];
                bags[bag] = original with { IsLoaded = false };
                Throws<InvalidOperationException>(() => InventoryStateReader.CopyState(42, bags));
                bags[bag] = new InventoryBagState(false, ReadOnlyMemory<InventorySlotState>.Empty);
                Throws<InvalidOperationException>(() => InventoryStateReader.CopyState(42, bags));
                bags[bag] = original;
            }
        });
        Test("each bag requires exactly 35 slots and four bags", () =>
        {
            var bags = EmptyBags();
            for (var bag = 0; bag < bags.Length; bag++)
            {
                var original = bags[bag];
                foreach (var size in new[] { 0, 1, 34, 36, 140 })
                {
                    bags[bag] = new InventoryBagState(true, new InventorySlotState[size]);
                    Throws<InvalidOperationException>(() => InventoryStateReader.CopyState(42, bags));
                }
                bags[bag] = original;
            }
            foreach (var size in new[] { 0, 3, 5 })
                Throws<InvalidOperationException>(() => InventoryStateReader.CopyState(42, new InventoryBagState[size]));
        });
        Test("empty slot metadata is normalized", () =>
        {
            var bags = EmptyBags();
            var baseline = InventoryStateReader.CopyState(42, bags);
            for (var bag = 0; bag < bags.Length; bag++)
                bags[bag] = new InventoryBagState(true,
                    Enumerable.Repeat(new InventorySlotState(999, 321, 87, true, true), 35).ToArray());
            var normalized = InventoryStateReader.CopyState(42, bags);
            Check(baseline == normalized && baseline.GetHashCode() == normalized.GetHashCode(), "unused empty-slot fields ignored");
        });
        Test("all 140 positions detect additions and removals", () =>
        {
            var bags = EmptyBags();
            var baseline = InventoryStateReader.CopyState(42, bags);
            for (var bag = 0; bag < bags.Length; bag++)
            {
                var slots = bags[bag].Slots.ToArray();
                bags[bag] = new InventoryBagState(true, slots);
                for (var slot = 0; slot < slots.Length; slot++)
                {
                    slots[slot] = new InventorySlotState(55, 55, 4, false);
                    Check(baseline != InventoryStateReader.CopyState(42, bags), $"bag {bag} slot {slot} added");
                    slots[slot] = InventorySlotState.Empty;
                    Check(baseline == InventoryStateReader.CopyState(42, bags), $"bag {bag} slot {slot} removed");
                }
            }
        });
        Test("same item movement changes slot and bag identity", () =>
        {
            var bags = EmptyBags();
            var firstSlots = bags[0].Slots.ToArray();
            var secondSlots = bags[1].Slots.ToArray();
            bags[0] = new InventoryBagState(true, firstSlots);
            bags[1] = new InventoryBagState(true, secondSlots);
            var item = new InventorySlotState(1_000_055, 55, 12, true);
            firstSlots[0] = item;
            var baseline = InventoryStateReader.CopyState(42, bags);
            firstSlots[0] = InventorySlotState.Empty;
            firstSlots[1] = item;
            Check(baseline != InventoryStateReader.CopyState(42, bags), "same-bag movement");
            firstSlots[1] = InventorySlotState.Empty;
            secondSlots[0] = item;
            Check(baseline != InventoryStateReader.CopyState(42, bags), "cross-bag movement");
        });
        Test("raw ID, base ID, quantity and HQ are independently compared", () =>
        {
            var bags = EmptyBags();
            var slots = bags[0].Slots.ToArray();
            bags[0] = new InventoryBagState(true, slots);
            var item = new InventorySlotState(55, 55, 12, false);
            slots[34] = item;
            var baseline = InventoryStateReader.CopyState(42, bags);
            foreach (var changed in new[] { item with { ItemId = 56 }, item with { BaseItemId = 56 },
                         item with { Quantity = 13 }, item with { IsHq = true } })
            {
                slots[34] = changed;
                Check(baseline != InventoryStateReader.CopyState(42, bags), "changed field detected");
            }
        });
        Test("identity, equality contract and defensive copy", () =>
        {
            var bags = EmptyBags();
            var slots = bags[0].Slots.ToArray();
            bags[0] = new InventoryBagState(true, slots);
            var baseline = InventoryStateReader.CopyState(42, bags);
            var equal = InventoryStateReader.CopyState(42, bags);
            Check(baseline.ContentId == 42 && baseline.Equals(equal) && baseline == equal &&
                baseline.GetHashCode() == equal.GetHashCode(), "equal content");
            Check(!baseline.Equals(null) && baseline != null && !baseline.Equals(new object()), "null and other object");
            Check(baseline != InventoryStateReader.CopyState(43, bags), "character identity");
            Throws<InvalidOperationException>(() => InventoryStateReader.CopyState(0, bags));
            slots[0] = new InventorySlotState(55, 55, 12, false);
            bags[1] = new InventoryBagState(false, ReadOnlyMemory<InventorySlotState>.Empty);
            Check(baseline == equal && baseline == InventoryStateReader.CopyState(42, EmptyBags()), "caller mutation cannot alter stamp");
            var stampInput = Enumerable.Repeat(InventorySlotState.Empty, 140).ToArray();
            var direct = new InventoryStateStamp(42, stampInput);
            stampInput[139] = new InventorySlotState(91, 91, 1, false);
            Check(direct == baseline, "stamp constructor owns its input");
            Throws<InvalidOperationException>(() => new InventoryStateStamp(0, stampInput));
            Throws<InvalidOperationException>(() => new InventoryStateStamp(42, stampInput.AsSpan(1)));
        });
        Console.WriteLine($"PASS {passed} inventory state fixture groups; no game state, native functions, or model calls used.");
    }

    private static InventoryBagState[] EmptyBags() => Enumerable.Range(0, 4)
        .Select(_ => new InventoryBagState(true, Enumerable.Repeat(InventorySlotState.Empty, 35).ToArray())).ToArray();
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }
    private static void Test(string name, Action action)
    {
        action(); passed++; Console.WriteLine("PASS " + name);
    }
}
