using System.Runtime.Loader;
using System.Reflection;
using System.Runtime.InteropServices;
using CompAI;
using Dalamud.Game.Inventory;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

var libraryPath = Environment.GetEnvironmentVariable("DALAMUD_LIB_PATH") ??
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "addon", "Hooks", "dev");
AssemblyLoadContext.Default.Resolving += (context, name) =>
{ var path = Path.Combine(libraryPath, name.Name + ".dll"); return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null; };
Tests.Run();

internal static unsafe class Tests
{
    private static void Check(bool value) { if (!value) throw new Exception("Assertion failed."); }
    private static void Throws(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new Exception("Expected rejection.");
    }
    internal static void Run()
    {
        var indoor = HousingFcStorageReader.HousingTypes(true);
        var outdoor = HousingFcStorageReader.HousingTypes(false);
        Check(indoor.Length == 23 && outdoor.Length == 4 && indoor.Distinct().Count() == 23);
        Check(indoor.All(type => HousingFcStorageReader.Capacity(type) == 50));
        Check(outdoor.All(type => HousingFcStorageReader.Capacity(type) == 40));
        Check(!indoor.Intersect(outdoor).Any());
        Throws(() => HousingFcStorageReader.Capacity(InventoryType.Inventory1));
        Console.WriteLine("PASS storage allowlist separates indoor, outdoor and FC containers");

        var container = new InventoryContainer();
        var native = new InventoryItem();
        container.Type = InventoryType.FreeCompanyPage1; container.Size = 50; container.Items = &native;
        Check(!HousingFcStorageReader.ContainerReady(&container, InventoryType.FreeCompanyPage1, 50));
        container.IsLoaded = true;
        Check(HousingFcStorageReader.ContainerReady(&container, InventoryType.FreeCompanyPage1, 50));
        Check(!HousingFcStorageReader.ContainerReady(&container, InventoryType.FreeCompanyPage2, 50));
        container.Size = 500;
        Check(!HousingFcStorageReader.ContainerReady(&container, InventoryType.FreeCompanyPage1, 50));
        Console.WriteLine("PASS unloaded and changed container layouts are not empty inventories");

        var house = new HouseId { WorldId = 1, TerritoryTypeId = 339, Unit = (HouseUnit)(byte)5 };
        Check(HousingFcStorageReader.ValidHouse(house));
        house.WardIndex = 63; Check(!HousingFcStorageReader.ValidHouse(house));
        house.WardIndex = 2; house.RoomNumber = 0x3FF; Check(!HousingFcStorageReader.ValidHouse(house));
        house.RoomNumber = 0; house.WorldId = 0; Check(!HousingFcStorageReader.ValidHouse(house));
        Check(!HousingFcStorageReader.ValidHouse(default));
        Console.WriteLine("PASS malformed and workshop house identities are rejected");

        var company = new InfoProxyFreeCompany { Id = 10 };
        "Synthetic FC"u8.CopyTo(company.Name);
        var member = new InfoProxyCommonList.CharacterData { ContentId = 99 };
        var members = new InfoProxyFreeCompanyMember { FreeCompanyId = 10, CharData = &member, EntryCount = 1 };
        Check(HousingFcStorageReader.TryCompany(&company, &members, 99, out var id, out var label));
        Check(id == 10 && label == "FC chest — Synthetic FC");
        Check(!HousingFcStorageReader.TryCompany(&company, &members, 100, out _, out _));
        members.FreeCompanyId = 11;
        Check(!HousingFcStorageReader.TryCompany(&company, &members, 99, out _, out _));
        members.FreeCompanyId = 10; members.EntryCount = 513;
        Check(!HousingFcStorageReader.TryCompany(&company, &members, 99, out _, out _));
        members.EntryCount = 1; members.CharData = null;
        Check(!HousingFcStorageReader.TryCompany(&company, &members, 99, out _, out _));
        Console.WriteLine("PASS local FC membership and matching proxy identity are required");

        InventoryItem.InventoryItemVirtualTable table = default;
        table.IsEmpty = (delegate* unmanaged<InventoryItem*, bool>)(delegate* unmanaged<InventoryItem*, byte>)&IsEmpty;
        table.GetInventoryType = &ReadContainer;
        table.GetSlot = &ReadSlot;
        table.GetItemId = &ReadItemId;
        table.GetQuantity = &ReadQuantity;
        table.GetFlags = &ReadFlags;
        var items = new GameInventoryItem[50];
        for (short slot = 0; slot < items.Length; slot++)
        {
            var item = new InventoryItem { VirtualTable = &table, Container = InventoryType.HousingInteriorPlacedItems1, Slot = slot };
            items[slot] = Create(item);
        }
        var rows = HousingFcStorageReader.DescribeItems(items,
            (GameInventoryType)InventoryType.HousingInteriorPlacedItems1, _ => "Unused");
        Check(rows.Length == 0);
        Throws(() => HousingFcStorageReader.DescribeItems(new GameInventoryItem[40],
            (GameInventoryType)InventoryType.HousingInteriorPlacedItems1, _ => "Unused"));
        Console.WriteLine("PASS complete empty container differs from unsupported-sized data");
        var furniture = new InventoryItem { VirtualTable = &table, Container = InventoryType.HousingInteriorPlacedItems1,
            Slot = 7, ItemId = 123, Quantity = 1 };
        items[7] = Create(furniture);
        rows = HousingFcStorageReader.DescribeItems(items, (GameInventoryType)furniture.Container, _ => "Synthetic furnishing");
        Check(rows.Length == 1 && rows[0].Slot == 7 && rows[0].ItemId == 123 && rows[0].Quantity == 1);
        furniture.Slot = 8; items[7] = Create(furniture);
        Throws(() => HousingFcStorageReader.DescribeItems(items, (GameInventoryType)furniture.Container, _ => "Unused"));
        Console.WriteLine("PASS copied item identity keeps exact slots and rejects mismatched slots");
        Console.WriteLine("6 housing/FC reader groups passed.");
    }
    private static GameInventoryItem Create(InventoryItem item) => (GameInventoryItem)Activator.CreateInstance(
        typeof(GameInventoryItem), BindingFlags.Instance | BindingFlags.NonPublic, null, [item], null)!;
    [UnmanagedCallersOnly] private static byte IsEmpty(InventoryItem* item) => item->ItemId == 0 ? (byte)1 : (byte)0;
    [UnmanagedCallersOnly] private static InventoryType ReadContainer(InventoryItem* item) => item->Container;
    [UnmanagedCallersOnly] private static ushort ReadSlot(InventoryItem* item) => (ushort)item->Slot;
    [UnmanagedCallersOnly] private static uint ReadItemId(InventoryItem* item) => item->ItemId;
    [UnmanagedCallersOnly] private static uint ReadQuantity(InventoryItem* item) => (uint)item->Quantity;
    [UnmanagedCallersOnly] private static InventoryItem.ItemFlags ReadFlags(InventoryItem* item) => item->Flags;
}
