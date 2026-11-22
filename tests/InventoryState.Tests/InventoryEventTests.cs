using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using CompAI;
using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;

internal static unsafe class InventoryEventTests
{
    private static InventoryItem.InventoryItemVirtualTable* fixtureTable;
    private static int passed;

    internal static void Run()
    {
        // The installed API's location properties use virtual accessors. Supply
        // local callbacks so real Dalamud event records can be tested without
        // resolving or invoking any game function or reading a game process.
        InventoryItem.InventoryItemVirtualTable table = default;
        table.GetInventoryType = &ReadContainer;
        table.GetSlot = &ReadSlot;
        fixtureTable = &table;
        try
        {
            Test("known unrelated containers can bypass invalidation", () =>
            {
                foreach (var container in Enum.GetValues<GameInventoryType>())
                {
                    if (container is GameInventoryType.Inventory1 or GameInventoryType.Inventory2 or
                        GameInventoryType.Inventory3 or GameInventoryType.Inventory4 or
                        GameInventoryType.Invalid or GameInventoryType.Unknown2006 or GameInventoryType.Unknown2008)
                        continue;
                    Check(!Affects(Added(container), Removed(container), Changed(container, container)), container.ToString());
                }
                Check(!Affects(Added(GameInventoryType.SaddleBag1), Removed(GameInventoryType.PremiumSaddleBag2),
                    Changed(GameInventoryType.Currency, GameInventoryType.Currency)), "mixed unrelated batch");
            });
            Test("each carried bag invalidates for additions, removals and changes", () =>
            {
                foreach (var bag in CarriedBags())
                foreach (var change in new[] { Added(bag), Removed(bag), Changed(bag, bag) })
                {
                    Check(Affects(change), bag.ToString());
                    Check(Affects(Added(GameInventoryType.SaddleBag1), change,
                        Removed(GameInventoryType.Currency)), "mixed relevant batch");
                }
            });
            Test("raw cross-boundary moves, splits and merges invalidate in both directions", () =>
            {
                foreach (var bag in CarriedBags())
                {
                    var outside = GameInventoryType.SaddleBag1;
                    Check(Affects(Removed(bag), Added(outside)), "move out");
                    Check(Affects(Removed(outside), Added(bag)), "move in");
                    Check(Affects(Changed(bag, bag), Added(outside)), "split out");
                    Check(Affects(Changed(outside, outside), Added(bag)), "split in");
                    Check(Affects(Removed(bag), Changed(outside, outside)), "merge out");
                    Check(Affects(Removed(outside), Changed(bag, bag)), "merge in");
                }
            });
            Test("both changed states and consistent locations are required", () =>
            {
                Check(Affects(Changed(GameInventoryType.Inventory4, GameInventoryType.Currency)), "old carried location");
                Check(Affects(Changed(GameInventoryType.Currency, GameInventoryType.Inventory4)), "new carried location");
                Check(Affects(Changed(GameInventoryType.SaddleBag1, GameInventoryType.SaddleBag2)), "inconsistent outside container");
                Check(Affects(Create<InventoryItemChangedArgs>(Item(GameInventoryType.Currency, 0),
                    Item(GameInventoryType.Currency, 1))), "inconsistent outside slot");
            });
            Test("unknown containers and event discriminants remain conservative", () =>
            {
                foreach (var unknown in new[] { GameInventoryType.Invalid, GameInventoryType.Unknown2006,
                    GameInventoryType.Unknown2008, (GameInventoryType)0x7fffffff })
                {
                    Check(Affects(Added(unknown), Added(GameInventoryType.Currency)), "unknown added location");
                    Check(Affects(Removed(unknown)), "unknown removed location");
                    Check(Affects(Changed(unknown, GameInventoryType.Currency)), "unknown old location");
                    Check(Affects(Changed(GameInventoryType.Currency, unknown)), "unknown new location");
                }
                var malformed = Added(GameInventoryType.Currency);
                typeof(InventoryEventArgs).GetField("<Type>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(malformed, (GameInventoryEvent)999);
                Check(Affects(malformed), "unsupported discriminator");
            });
            Test("unexpected complex events remain conservative", () =>
            {
                foreach (var target in new[] { GameInventoryType.SaddleBag2, GameInventoryType.Inventory2 })
                {
                    var sourceEvent = Removed(GameInventoryType.SaddleBag1);
                    var targetEvent = Added(target);
                    Check(Affects(Create<InventoryItemMovedArgs>(sourceEvent, targetEvent)), "unexpected move");
                    Check(Affects(Create<InventoryItemSplitArgs>(sourceEvent, targetEvent)), "unexpected split");
                    Check(Affects(Create<InventoryItemMergedArgs>(sourceEvent, targetEvent)), "unexpected merge");
                }
            });
            Test("null, empty and incomplete batches invalidate", () =>
            {
                Check(InventoryEventFilter.MayAffectCarriedBags(null), "null batch");
                Check(Affects(), "empty batch");
                Check(Affects(new InventoryEventArgs[] { null! }), "null member");
                Check(Affects(Added(GameInventoryType.Currency), null!), "mixed null member");
                Check(Affects(Create<InventoryItemAddedArgs>(default(GameInventoryItem))), "default item");
                Check(InventoryEventFilter.MayAffectCarriedBags(new OddBatch(2, [Added(GameInventoryType.Currency)])), "short enumeration");
                Check(InventoryEventFilter.MayAffectCarriedBags(new OddBatch(1,
                    [Added(GameInventoryType.Currency), Added(GameInventoryType.SaddleBag1)])), "long enumeration");
                Check(InventoryEventFilter.MayAffectCarriedBags(new OddBatch(1, [])), "empty enumeration with nonzero count");
            });
            Test("failed enumeration preserves invalidation", () =>
            {
                Check(InventoryEventFilter.MayAffectCarriedBags(new OddBatch(1, [], throwOnCount: true)), "count failure");
                Check(InventoryEventFilter.MayAffectCarriedBags(new OddBatch(1, [], throwOnEnumerate: true)), "enumeration failure");
            });
            Console.WriteLine($"PASS {passed} inventory event fixture groups; real API 15 event types with synthetic location accessors; no game state or model calls.");
        }
        finally { fixtureTable = null; }
    }

    [UnmanagedCallersOnly]
    private static InventoryType ReadContainer(InventoryItem* item) => item->Container;

    [UnmanagedCallersOnly]
    private static ushort ReadSlot(InventoryItem* item) => (ushort)item->Slot;

    private static GameInventoryItem Item(GameInventoryType container, short slot = 0)
    {
        InventoryItem item = default;
        item.VirtualTable = fixtureTable;
        item.Container = (InventoryType)container;
        item.Slot = slot;
        item.ItemId = 55;
        item.Quantity = 1;
        return Create<GameInventoryItem>(item);
    }

    private static T Create<T>(params object[] arguments) => (T)Activator.CreateInstance(typeof(T),
        BindingFlags.Instance | BindingFlags.NonPublic, null, arguments, null)!;
    private static InventoryEventArgs Added(GameInventoryType container) => Create<InventoryItemAddedArgs>(Item(container));
    private static InventoryEventArgs Removed(GameInventoryType container) => Create<InventoryItemRemovedArgs>(Item(container));
    private static InventoryEventArgs Changed(GameInventoryType before, GameInventoryType after) =>
        Create<InventoryItemChangedArgs>(Item(before), Item(after));
    private static bool Affects(params InventoryEventArgs[] events) => InventoryEventFilter.MayAffectCarriedBags(events);
    private static GameInventoryType[] CarriedBags() => [GameInventoryType.Inventory1, GameInventoryType.Inventory2,
        GameInventoryType.Inventory3, GameInventoryType.Inventory4];
    private static void Check(bool result, string message) { if (!result) throw new InvalidOperationException(message); }
    private static void Test(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }

    private sealed class OddBatch(int count, InventoryEventArgs[] events, bool throwOnCount = false,
        bool throwOnEnumerate = false) : IReadOnlyCollection<InventoryEventArgs>
    {
        public int Count => throwOnCount ? throw new InvalidOperationException("Synthetic count failure") : count;
        public IEnumerator<InventoryEventArgs> GetEnumerator() => throwOnEnumerate
            ? throw new InvalidOperationException("Synthetic enumeration failure")
            : ((IEnumerable<InventoryEventArgs>)events).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();
    }
}
