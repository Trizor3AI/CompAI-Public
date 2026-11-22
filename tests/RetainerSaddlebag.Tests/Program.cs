using System.Runtime.Loader;
using CompAI;
using Dalamud.Game.Inventory;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "addon", "Hooks", "dev", name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
Tests.Run();

internal static unsafe class Tests
{
    private static int passed;
    private static void Check(bool ok) { if (!ok) throw new Exception("Assertion failed."); }
    private static void Test(string label, Action run) { run(); passed++; Console.WriteLine("PASS " + label); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new Exception("Expected unavailable result.");
    }
    private static StorageObservedItem Item(int slot, InventoryType type, int quantity = 1, uint id = 5121) =>
        new(false, (GameInventoryType)type, slot, id, id, quantity, false);
    private static StorageObservedItem[] Bag(InventoryType type, int size = 20)
    {
        var rows = Enumerable.Repeat(new StorageObservedItem(true, 0, 0, 0, 0, 0, false), size).ToArray();
        rows[3] = Item(3, type, 7);
        return rows;
    }
    internal static void Run()
    {
        Test("only observed saddlebag tab selected; expanded includes both", () =>
        {
            Check(RetainerSaddlebagReader.SaddlebagTypes(false, 0).SequenceEqual(new[] { InventoryType.SaddleBag1, InventoryType.SaddleBag2 }));
            Check(RetainerSaddlebagReader.SaddlebagTypes(false, 1).SequenceEqual(new[] { InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2 }));
            Check(RetainerSaddlebagReader.SaddlebagTypes(false, 2).Length == 0);
            Check(RetainerSaddlebagReader.SaddlebagTypes(true, -1).Length == 4);
        });
        Test("native loaded/type/size guards reject unavailable storage", () =>
        {
            var item = new InventoryItem();
            var bag = new InventoryContainer { Items = &item, Type = InventoryType.SaddleBag1, Size = 35, IsLoaded = true };
            Check(RetainerSaddlebagReader.ContainerReady(&bag, InventoryType.SaddleBag1, 35, 35));
            bag.IsLoaded = false;
            Check(!RetainerSaddlebagReader.ContainerReady(&bag, InventoryType.SaddleBag1, 35, 35));
            bag.IsLoaded = true;
            Check(!RetainerSaddlebagReader.ContainerReady(&bag, InventoryType.RetainerPage1, 35, 35));
            bag.Size = 36;
            Check(!RetainerSaddlebagReader.ContainerReady(&bag, InventoryType.SaddleBag1, 35, 35));
        });
        Test("selected retainer needs ready exact unique named identity", () =>
        {
            var manager = new RetainerManager { IsReady = true, LastSelectedRetainerId = 42 };
            manager.Retainers[2].RetainerId = 42;
            "Synthetic"u8.CopyTo(manager.Retainers[2].Name);
            Check(RetainerSaddlebagReader.TryRetainer(&manager, out var id, out var name) && id == 42 && name == "Synthetic");
            manager.Retainers[3].RetainerId = 42;
            Check(!RetainerSaddlebagReader.TryRetainer(&manager, out _, out _));
            manager.Retainers[3].RetainerId = 0;
            manager.IsReady = false;
            Check(!RetainerSaddlebagReader.TryRetainer(&manager, out _, out _));
        });
        Test("rows preserve physical slot, stack and same-name duplicates", () =>
        {
            var slots = Bag(InventoryType.SaddleBag1, 35);
            slots[4] = Item(4, InventoryType.SaddleBag1, 2);
            var rows = RetainerSaddlebagReader.DescribeItems(slots, (GameInventoryType)InventoryType.SaddleBag1, _ => "Synthetic item");
            Check(rows.Length == 2 && rows[0].Slot == 3 && rows[0].Quantity == 7 && rows[1].Slot == 4);
            Check(rows.All(x => x.UnitPriceGil is null));
        });
        Test("wrong slot or container is rejected", () =>
        {
            var slots = Bag(InventoryType.RetainerPage1);
            slots[3] = Item(4, InventoryType.RetainerPage1);
            Reject(() => RetainerSaddlebagReader.DescribeItems(slots, (GameInventoryType)InventoryType.RetainerPage1, _ => "Item"));
            slots[3] = Item(3, InventoryType.RetainerPage2);
            Reject(() => RetainerSaddlebagReader.DescribeItems(slots, (GameInventoryType)InventoryType.RetainerPage1, _ => "Item"));
        });
        Test("market price joins exact physical listing and quantity", () =>
        {
            var slots = Bag(InventoryType.RetainerMarket);
            var prices = new ulong[20]; prices[3] = 582;
            var rows = RetainerSaddlebagReader.DescribeItems(slots, (GameInventoryType)InventoryType.RetainerMarket,
                _ => "Synthetic ore", [new(3, 5121, 7)], prices);
            Check(rows.Single().UnitPriceGil == 582 && rows.Single().Quantity == 7);
            Reject(() => RetainerSaddlebagReader.DescribeItems(slots, (GameInventoryType)InventoryType.RetainerMarket,
                _ => "Synthetic ore", [new(4, 5121, 7)], prices));
            Reject(() => RetainerSaddlebagReader.DescribeItems(slots, (GameInventoryType)InventoryType.RetainerMarket,
                _ => "Synthetic ore", [new(3, 5121, 8)], prices));
        });
        Test("unavailable price stays unknown instead of zero", () =>
        {
            Check(RetainerSaddlebagReader.UnitPrice(0) is null && RetainerSaddlebagReader.UnitPrice(ulong.MaxValue) is null);
            Check(RetainerSaddlebagReader.UnitPrice(999999999) == 999999999);
        });
        Test("empty observed market valid, unmatched visible offer rejected", () =>
        {
            var slots = Enumerable.Repeat(new StorageObservedItem(true, 0, 0, 0, 0, 0, false), 20).ToArray();
            var prices = new ulong[20];
            Check(RetainerSaddlebagReader.DescribeItems(slots, (GameInventoryType)InventoryType.RetainerMarket, _ => "Item", [], prices).Length == 0);
            Reject(() => RetainerSaddlebagReader.DescribeItems(slots, (GameInventoryType)InventoryType.RetainerMarket,
                _ => "Item", [new(1, 5121, 1)], prices));
        });
        Test("sale list native rows are bounded and duplicate slots rejected", () =>
        {
            var agent = new AgentRetainer { SellListEntryCount = 1 };
            agent.SellListEntries[0].ItemId = 5121;
            agent.SellListEntries[0].Quantity = 7;
            agent.SellListEntries[0].InventorySlot = 3;
            Check(RetainerSaddlebagReader.CopySaleRows(&agent).Single() == new RetainerSaleRow(3, 5121, 7));
            agent.SellListEntryCount = 2;
            agent.SellListEntries[1] = agent.SellListEntries[0];
            var failed = false;
            try { RetainerSaddlebagReader.CopySaleRows(&agent); } catch (InvalidOperationException) { failed = true; }
            Check(failed);
        });
        Test("personal and retainer crystals keep all positive balances but omit observed zeros", () =>
        {
            foreach (var type in new[] { InventoryType.Crystals, InventoryType.RetainerCrystals })
            {
                var crystals = Enumerable.Range(0, 18).Select(slot => Item(slot, type, slot == 17 ? 9999 : 0, (uint)(slot + 1))).ToArray();
                var rows = RetainerSaddlebagReader.DescribeItems(crystals, (GameInventoryType)type, _ => "Synthetic cluster");
                Check(rows.Length == 1 && rows[0].Slot == 17 && rows[0].BaseItemId == 18 && rows[0].Quantity == 9999);
                Reject(() => RetainerSaddlebagReader.DescribeItems(crystals[..17], (GameInventoryType)type, _ => "Partial"));
            }
        });
        Test("key item namespace preserves EventItem IDs and rejects normal or normalized IDs", () =>
        {
            var slots = Bag(InventoryType.KeyItems, 105);
            slots[3] = Item(3, InventoryType.KeyItems, 1, 2000001);
            var rows = RetainerSaddlebagReader.DescribeItems(slots, (GameInventoryType)InventoryType.KeyItems,
                id => id == 2000001 ? "Synthetic quest letter" : throw new Exception("Wrong definition ID"));
            Check(rows.Single().BaseItemId == 2000001 && rows.Single().Name == "Synthetic quest letter");
            slots[3] = slots[3] with { BaseItemId = 1 };
            Reject(() => RetainerSaddlebagReader.DescribeItems(slots, (GameInventoryType)InventoryType.KeyItems, _ => "Wrong"));
            slots[3] = Item(3, InventoryType.KeyItems, 1, 5121);
            Reject(() => RetainerSaddlebagReader.DescribeItems(slots, (GameInventoryType)InventoryType.KeyItems, _ => "Wrong"));
        });
        Console.WriteLine($"{passed} focused storage-reader checks passed (synthetic only).");
    }
}
