using System.Numerics;
using System.Runtime.Loader;
using CompAI;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

var libraryPath = Environment.GetEnvironmentVariable("DALAMUD_LIB_PATH") ??
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "addon", "Hooks", "dev");
AssemblyLoadContext.Default.Resolving += (context, name) =>
{ var file = Path.Combine(libraryPath, name.Name + ".dll"); return File.Exists(file) ? context.LoadFromAssemblyPath(file) : null; };
var passed = 0;
void Check(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
var now = DateTimeOffset.UtcNow;
var selected = new InventorySlotState(123, 123, 1, false);
var selection = new InventoryHighlightSelection("Inventory2", 7, 123, 123, 1, false);
InventoryStateStamp Stamp(uint other = 456)
{
    var slots = Enumerable.Repeat(InventorySlotState.Empty, 140).ToArray();
    slots[42] = selected; slots[43] = new(other, other, 1, false);
    return new(900, slots);
}
InventoryHighlightState Started()
{
    var state = new InventoryHighlightState();
    Check(state.Start(selection, 900, 10, 15, Stamp(), selected, now));
    return state;
}
var rectangle = new InventoryHighlightRectangle(new(10, 10), new(50, 50), true);
InventoryHighlightFrame Frame(DateTimeOffset? captured = null) => new(captured ?? now, [rectangle], new Vector2(200, 20), "Synthetic highlight");

Test("exact slot identity rejects wrong bag slot ID quality quantity and owner", () =>
{
    foreach (var candidate in new[] { selection with { Bag = "SaddleBag1" }, selection with { Slot = 35 }, selection with { ItemId = 456 },
                 selection with { BaseItemId = 456 }, selection with { Quantity = 2 }, selection with { IsHq = true } })
        Check(!new InventoryHighlightState().Start(candidate, 900, 10, 15, Stamp(), selected, now));
    Check(!new InventoryHighlightState().Start(selection, 901, 10, 15, Stamp(), selected, now));
    Check(!new InventoryHighlightState().Start(selection, 0, 10, 15, Stamp(), selected, now));
});
Test("independent feature access owner loading session and timeout gates clear", () =>
{
    Check(!Started().Check(false, true, 900, 10, false, now));
    Check(!Started().Check(true, false, 900, 10, false, now));
    Check(!Started().Check(true, true, 901, 10, false, now));
    Check(!Started().Check(true, true, 900, 11, false, now));
    Check(!Started().Check(true, true, 900, 10, true, now));
    Check(!Started().Check(true, true, 900, 10, false, now.AddSeconds(15)));
    Check(Started().Check(true, true, 900, 10, false, now.AddSeconds(14)));
});
Test("any carried inventory change cancels rather than reassociates by item ID", () =>
{
    var state = Started(); Check(!state.Publish(Stamp(), Stamp(789), Frame())); Check(!state.Active && state.Frame is null);
    state = Started(); Check(!state.Publish(Stamp(789), Stamp(789), Frame())); Check(!state.Active);
    state = Started(); state.Clear("Synthetic same-ID move event"); Check(!state.Active && state.Baseline is null);
});
Test("draw data expires after250ms and at request timeout", () =>
{
    var state = Started(); Check(state.Publish(Stamp(), Stamp(), Frame()));
    Check(state.VisibleFrame(now.AddMilliseconds(249)) is not null);
    Check(state.VisibleFrame(now.AddMilliseconds(251)) is null);
    Check(state.VisibleFrame(now.AddTicks(-1)) is null);
    Check(state.VisibleFrame(now.AddSeconds(15)) is null);
});
Test("armed request is not visibly highlighted until an actual current draw", () =>
{
    var state = Started(); Check(state.Active && !state.IsVisible(now));
    var frame = Frame(); Check(state.Publish(Stamp(), Stamp(), frame));
    Check(!state.IsVisible(now)); state.MarkDrawn(frame, now); Check(state.IsVisible(now));
    Check(!state.IsVisible(now.AddMilliseconds(251)));
    var hidden = Frame() with { Slots = [], Status = "No visible selected slot" };
    Check(state.Publish(Stamp(), Stamp(), hidden)); Check(!state.IsVisible(now));
    state.MarkDrawn(frame, now); Check(!state.IsVisible(now));
    state.Clear(); Check(!state.IsVisible(now));
});
Test("hidden transparent unloaded or hidden-root tooltip is not shown", () =>
{
    Check(!InventoryHighlightReader.TooltipShown(true, false, 255, true));
    Check(!InventoryHighlightReader.TooltipShown(true, true, 0, true));
    Check(!InventoryHighlightReader.TooltipShown(false, true, 255, true));
    Check(!InventoryHighlightReader.TooltipShown(true, true, 255, false));
    Check(InventoryHighlightReader.TooltipShown(true, true, 255, true));
});
Test("tooltip bindings use installed inventory-item enums and all four carried bags", () =>
{
    AtkTooltipManager.AtkTooltipInfo Info(int bag, short slot)
    {
        var info = new AtkTooltipManager.AtkTooltipInfo { Type = AtkTooltipType.Item, ParentId = 12 };
        info.AtkTooltipArgs.ItemArgs.Kind = DetailKind.InventoryItem;
        info.AtkTooltipArgs.ItemArgs.InventoryType = (InventoryType)bag;
        info.AtkTooltipArgs.ItemArgs.Slot = slot;
        return info;
    }
    for (var bag = 0; bag < 4; bag++)
        foreach (var slot in new short[] { 0, 34 })
        { Check(InventoryHighlightReader.CarriedBinding(Info(bag, slot), 12, 11, out var type, out var index)); Check((int)type == bag && index == slot); }
    Check(!InventoryHighlightReader.CarriedBinding(Info(4, 0), 12, 11, out _, out _));
    Check(!InventoryHighlightReader.CarriedBinding(Info(0, -1), 12, 11, out _, out _));
    Check(!InventoryHighlightReader.CarriedBinding(Info(0, 35), 12, 11, out _, out _));
    var wrong = Info(0, 0); wrong.AtkTooltipArgs.ItemArgs.Kind = DetailKind.Item;
    Check(!InventoryHighlightReader.CarriedBinding(wrong, 12, 11, out _, out _));
    wrong = Info(0, 0); wrong.Type = AtkTooltipType.Text;
    Check(!InventoryHighlightReader.CarriedBinding(wrong, 12, 11, out _, out _));
    Check(!InventoryHighlightReader.CarriedBinding(Info(0, 0), 99, 98, out _, out _));
});
Test("selected tooltip must match exact bag slot and current item, not a same-ID card", () =>
{
    bool Match(DetailKind kind = DetailKind.InventoryItem, uint type = 49, uint slot = 7,
        uint itemId = 123, ulong hovered = 123, bool dirty = false) =>
        InventoryHighlightReader.SelectedTooltipIdentity(selection, kind, type, slot, itemId, hovered, dirty);
    Check(Match());
    Check(!Match(type: 48)); Check(!Match(slot: 8));
    Check(!Match(type: 69)); Check(!Match(type: 0));
    Check(!Match(itemId: 456)); Check(!Match(hovered: 456));
    Check(!Match(kind: DetailKind.Item)); Check(!Match(dirty: true));
    var hq = selection with { ItemId = 1_000_123, IsHq = true };
    Check(InventoryHighlightReader.SelectedTooltipIdentity(hq, DetailKind.InventoryItem, 49, 7, 1_000_123, 1_000_123, false));
    Check(!InventoryHighlightReader.SelectedTooltipIdentity(hq, DetailKind.InventoryItem, 49, 7, 123, 123, false));
});
Test("selected tooltip ends highlight while unmatched tooltips preserve it and stale state still wins", () =>
{
    var state = Started();
    var unchanged = Frame();
    Check(state.Publish(Stamp(), Stamp(), unchanged));
    state.MarkDrawn(unchanged, now); Check(state.Active && state.IsVisible(now));
    var found = Frame() with { Slots = [], SelectedTooltipVisible = true };
    Check(!state.Publish(Stamp(), Stamp(), found)); Check(!state.Active && state.Frame is null && state.Status.Contains("tooltip opened"));
    state = Started();
    Check(!state.Publish(Stamp(), Stamp(789), found)); Check(state.Status.Contains("Inventory changed"));
});
Test("page selection follows sorted exact slots for normal expanded and full inventories", () =>
{
    var order = Enumerable.Range(0, 140).ToArray();
    for (var visualPage = 0; visualPage < 4; visualPage++)
    {
        var position = visualPage * 35 + 3;
        var original = Array.IndexOf(order, 42);
        (order[position], order[original]) = (order[original], order[position]);
        Check(InventoryHighlightReader.TargetTab(0, selection, order) == visualPage);
        Check(InventoryHighlightReader.TargetTab(1, selection, order) == visualPage / 2);
        Check(InventoryHighlightReader.TargetTab(2, selection, order) == 0);
    }
    Check(InventoryHighlightReader.TargetTab(3, selection, order) == -1);
    Check(InventoryHighlightReader.TargetTab(0, selection with { Bag = "SaddleBag1" }, order) == -1);
    Check(InventoryHighlightReader.TargetTab(0, selection, order.AsSpan(0, 139)) == -1);
    order[0] = 42; Check(InventoryHighlightReader.TargetTab(0, selection, order) == -1);
    Array.Fill(order, 1); Check(InventoryHighlightReader.TargetTab(0, selection, order) == -1);
});
Test("expanded grids map both tabs while normal and full layouts remain bounded", () =>
{
    for (var page = 0; page < 4; page++)
    {
        Check(InventoryHighlightReader.VisualPage(0, page, "InventoryGrid") == page);
        Check(InventoryHighlightReader.VisualPage(1, page / 2, "InventoryGrid" + page % 2) == page);
        Check(InventoryHighlightReader.VisualPage(2, 0, "InventoryGrid" + page + "E") == page);
    }
    Check(InventoryHighlightReader.VisualPage(0, 4, "InventoryGrid") == -1);
    Check(InventoryHighlightReader.VisualPage(1, 2, "InventoryGrid0") == -1);
    Check(InventoryHighlightReader.VisualPage(1, -1, "InventoryGrid1") == -1);
    Check(InventoryHighlightReader.VisualPage(1, 0, "InventoryGrid0E") == -1);
    Check(InventoryHighlightReader.VisualPage(2, 1, "InventoryGrid0E") == -1);
    Check(InventoryHighlightReader.VisualPage(3, 0, "InventoryGrid") == -1);
    Check(InventoryHighlightReader.VisualSlot(2) == -1 && InventoryHighlightReader.VisualSlot(38) == -1);
    for (uint slot = 0; slot < 35; slot++) Check(InventoryHighlightReader.VisualSlot(slot + 3) == slot);
});
Test("native sorter maps a permutation to physical slots without item-ID reassociation", () =>
{
    unsafe
    {
        var entries = stackalloc ItemOrderModuleSorterItemEntry[140];
        var pointers = stackalloc Pointer<ItemOrderModuleSorterItemEntry>[140];
        for (var i = 0; i < 140; i++)
        {
            // Move every physical slot to another page/position, including selected 42.
            var physical = (i + 42) % 140;
            entries[i] = new() { Page = (ushort)(physical / 35), Slot = (ushort)(physical % 35) };
            pointers[i] = entries + i;
        }
        var sorter = new ItemOrderModuleSorter
        { InventoryType = InventoryType.Inventory1, ItemsPerPage = 35, SortFunctionIndex = -1, PercentComplete = 100 };
        sorter.Items.WithOps.First = pointers;
        sorter.Items.WithOps.Last = pointers + 140;
        sorter.Items.WithOps.End = pointers + 140;
        Span<int> order = stackalloc int[140];
        Check(InventoryHighlightReader.CopySortOrder(&sorter, order));
        Check(order[0] == 42 && order[35] == 77 && order[139] == 41);
        var oldOrder = order.ToArray();
        (entries[0], entries[1]) = (entries[1], entries[0]);
        Check(InventoryHighlightReader.CopySortOrder(&sorter, order));
        Check(!order.SequenceEqual(oldOrder)); // The reader rejects a map changed during capture.
        var first = entries[0]; entries[0] = entries[1];
        Check(!InventoryHighlightReader.CopySortOrder(&sorter, order));
        entries[0] = first;
        entries[0].Page = 4; Check(!InventoryHighlightReader.CopySortOrder(&sorter, order));
        entries[0] = first; entries[0].Slot = 35; Check(!InventoryHighlightReader.CopySortOrder(&sorter, order));
        entries[0] = first; pointers[0] = null; Check(!InventoryHighlightReader.CopySortOrder(&sorter, order));
        pointers[0] = entries;
        sorter.Items.WithOps.Last = pointers + 139; Check(!InventoryHighlightReader.CopySortOrder(&sorter, order));
        sorter.Items.WithOps.Last = pointers + 140;
        sorter.ItemsPerPage = 70; Check(!InventoryHighlightReader.CopySortOrder(&sorter, order));
        sorter.ItemsPerPage = 35;
        sorter.InventoryType = InventoryType.Crystals; Check(!InventoryHighlightReader.CopySortOrder(&sorter, order));
        sorter.InventoryType = InventoryType.Inventory1;
        sorter.SortFunctionIndex = 0; Check(!InventoryHighlightReader.CopySortOrder(&sorter, order));
        sorter.SortFunctionIndex = -1; sorter.PercentComplete = 0;
        Check(InventoryHighlightReader.CopySortOrder(&sorter, order)); // Saved order, no new sort required.
        sorter.Items.WithOps.End = pointers + 139; Check(!InventoryHighlightReader.CopySortOrder(&sorter, order));
        Check(!InventoryHighlightReader.CopySortOrder(null, order));
    }
});
Test("unsupported geometry duplicate selection and unbounded frames clear", () =>
{
    foreach (var frame in new[] { Frame() with { Slots = [rectangle with { Max = new(float.NaN, 10) }] },
                 Frame() with { Slots = [rectangle with { Max = new(9999, 9999) }] },
                 Frame() with { Slots = [rectangle, rectangle] },
                 Frame() with { Slots = [rectangle with { Selected = false }] },
                 Frame() with { Slots = Enumerable.Repeat(rectangle with { Selected = false }, 141).ToArray() } })
    { var state = Started(); Check(!state.Publish(Stamp(), Stamp(), frame)); Check(!state.Active); }
    var supported = Started(); Check(supported.Publish(Stamp(), Stamp(), Frame() with { Slots = [] }));
    Check(supported.VisibleFrame(now)!.Slots.Length == 0);
});
Test("manual clear removes every retained observation", () =>
{
    var state = Started(); Check(state.Publish(Stamp(), Stamp(), Frame())); state.Clear();
    Check(!state.Active && state.Selection is null && state.Baseline is null && state.VisibleFrame(now) is null && state.OwnerId == 0);
});
var testRoot = Path.GetFullPath(".test-tmp");
var temp = Path.Combine(testRoot, "inventory-highlight-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    Test("beta default is off and explicit choice duration persist", () =>
    {
        var path = Path.Combine(temp, "settings.json"); var store = new CompanionBetaSettingsStore(path);
        Check(!store.Settings.InventoryHighlight && store.Settings.InventoryHighlightSeconds == 15 && store.Error is null);
        if (!store.Save(new(true, 30))) throw new Exception(store.Error);
        var loaded = new CompanionBetaSettingsStore(path);
        Check(loaded.Settings.InventoryHighlight && loaded.Settings.InventoryHighlightSeconds == 30 && loaded.Error is null);
        Check(loaded.Save(new(false, 5))); Check(!new CompanionBetaSettingsStore(path).Settings.InventoryHighlight);
    });
    Test("shop beta is independent and schema one migration never grants it", () =>
    {
        var path = Path.Combine(temp, "shop-settings.json");
        File.WriteAllText(path, "{\"schemaVersion\":1,\"inventoryHighlight\":true,\"inventoryHighlightSeconds\":30}");
        var store = new CompanionBetaSettingsStore(path);
        Check(store.Error is null && store.Settings.InventoryHighlight && store.Settings.InventoryHighlightSeconds == 30 &&
            !store.Settings.ShopHighlight);
        Check(store.Save(store.Settings with { InventoryHighlight = false, ShopHighlight = true }));
        var loaded = new CompanionBetaSettingsStore(path);
        Check(loaded.Error is null && !loaded.Settings.InventoryHighlight && loaded.Settings.ShopHighlight &&
            loaded.Settings.InventoryHighlightSeconds == 30);
        foreach (var json in new[] {
            "{\"schemaVersion\":1,\"inventoryHighlight\":true,\"inventoryHighlightSeconds\":15,\"shopHighlight\":true}",
            "{\"schemaVersion\":2,\"inventoryHighlight\":true,\"inventoryHighlightSeconds\":15,\"shopHighlight\":1}",
            "{\"schemaVersion\":3,\"inventoryHighlight\":true,\"inventoryHighlightSeconds\":15,\"shopHighlight\":true}" })
        {
            File.WriteAllText(path, json); loaded = new(path);
            Check(loaded.Error is not null && !loaded.Settings.InventoryHighlight && !loaded.Settings.ShopHighlight);
        }
        var directory = Path.Combine(temp, "shop-blocked"); Directory.CreateDirectory(directory);
        store = new(directory);
        Check(!store.Save(new(true, 15, true)) && !store.Settings.InventoryHighlight && !store.Settings.ShopHighlight);
    });
    Test("malformed duplicate future oversized and nonboolean settings fail closed", () =>
    {
        var path = Path.Combine(temp, "invalid.json");
        foreach (var json in new[] { "{", "{}", "[]", new string('x', 2049),
            "{\"schemaVersion\":2,\"inventoryHighlight\":true,\"inventoryHighlightSeconds\":15}",
            "{\"schemaVersion\":1,\"inventoryHighlight\":true,\"inventoryHighlightSeconds\":61}",
            "{\"schemaVersion\":1,\"inventoryHighlight\":1,\"inventoryHighlightSeconds\":15}",
            "{\"schemaVersion\":1,\"inventoryHighlight\":true,\"inventoryHighlightSeconds\":15,\"inventoryHighlight\":true}" })
        { File.WriteAllText(path, json); var store = new CompanionBetaSettingsStore(path); Check(!store.Settings.InventoryHighlight && store.Error is not null); }
    });
    Test("failed save and invalid duration cannot enable the experiment", () =>
    {
        var path = Path.Combine(temp, "directory"); Directory.CreateDirectory(path);
        var store = new CompanionBetaSettingsStore(path); Check(!store.Save(new(true, 15)) && !store.Settings.InventoryHighlight && store.Error is not null);
        store = new(Path.Combine(temp, "duration.json"));
        Check(!store.Save(new(true, 4)) && !store.Settings.InventoryHighlight);
        Check(!store.Save(new(true, 61)) && !store.Settings.InventoryHighlight);
        Check(store.Save(new(true, 60)) && store.Settings.InventoryHighlight);
    });
}
finally
{
    if (Path.GetDirectoryName(Path.GetFullPath(temp)) != testRoot) throw new Exception("Unexpected test cleanup target");
    Directory.Delete(temp, recursive: true);
}
Console.WriteLine($"{passed} inventory-highlight fixture groups passed; no live UI or game actions executed.");
