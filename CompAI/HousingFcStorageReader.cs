using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dalamud.Game.Inventory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace CompAI;

/// <summary>
/// Framework-thread observations of opened housing and FC storage. No menu actions,
/// remote requests, owner guesses, furnishing moves or native memory offsets.
/// </summary>
internal sealed unsafe class HousingFcStorageReader(IGameInventory inventory, IDataManager data, IGameGui gui)
{
    internal string? HousingUnavailable { get; private set; }
    internal string? FcChestUnavailable { get; private set; }

    internal IReadOnlyList<StorageContainerCapture> Capture(ulong owner, bool housing, bool fcChest)
    {
        var result = new List<StorageContainerCapture>();
        var player = PlayerState.Instance();
        if (!OwnerReady(player, owner)) return result;
        if (housing)
        {
            try { this.CaptureHousing(result); }
            catch (Exception) { this.HousingUnavailable = "Housing data is loading or unavailable; previous observations are retained."; }
        }
        if (fcChest)
        {
            try { this.CaptureFcChest(owner, result); }
            catch (Exception) { this.FcChestUnavailable = "FC chest data is loading or unavailable; previous observations are retained."; }
        }
        return PlayerState.Instance() == player && OwnerReady(player, owner) ? result : [];
    }

    private void CaptureHousing(List<StorageContainerCapture> result)
    {
        this.HousingUnavailable = "Open the furnishing menu and its storeroom to observe loaded housing items.";
        var addon = gui.GetAddonByName<AtkUnitBase>("HousingGoods", 1);
        if (!Ready(addon)) return;
        var manager = HousingManager.Instance();
        if (!TryHouse(manager, out var house, out var indoors)) return;
        var territory = manager->CurrentTerritory;
        var observed = new List<StorageContainerCapture>();
        var key = house.Id.ToString("X16", CultureInfo.InvariantCulture);
        var label = this.HouseLabel(house);
        foreach (var type in HousingTypes(indoors))
        {
            var captured = this.ReadContainer("housing", key, label, type, Capacity(type));
            if (captured is not null) observed.Add(captured);
        }
        if (HousingManager.Instance() != manager || manager->CurrentTerritory != territory ||
            !TryHouse(manager, out var afterHouse, out var afterIndoors) || house != afterHouse || indoors != afterIndoors ||
            gui.GetAddonByName<AtkUnitBase>("HousingGoods", 1) != addon || !Ready(addon)) return;
        result.AddRange(observed);
        if (observed.Count > 0) this.HousingUnavailable = null;
    }

    private void CaptureFcChest(ulong owner, List<StorageContainerCapture> result)
    {
        this.FcChestUnavailable = "Open the FC chest; if identity is unavailable, open your FC Members page first.";
        var addon = gui.GetAddonByName<AtkUnitBase>("FreeCompanyChest", 1);
        var chestAgent = AgentFreeCompanyChest.Instance();
        if (!Ready(addon) || chestAgent == null || !chestAgent->IsAgentActive() || chestAgent->AddonId != addon->Id) return;
        var agent = AgentFreeCompany.Instance();
        if (agent == null) return;
        var company = agent->InfoProxyFreeCompany;
        var members = agent->InfoProxyFreeCompanyMember;
        if (!TryCompany(company, members, owner, out var id, out var label)) return;
        var key = id.ToString("X16", CultureInfo.InvariantCulture);
        var observed = new List<StorageContainerCapture>();
        foreach (var type in FcTypes)
        {
            var captured = this.ReadContainer("fc_chest", key, label, type, Capacity(type));
            if (captured is not null) observed.Add(captured);
        }
        if (AgentFreeCompany.Instance() != agent || agent->InfoProxyFreeCompany != company ||
            agent->InfoProxyFreeCompanyMember != members ||
            !TryCompany(company, members, owner, out var afterId, out var afterLabel) || id != afterId || label != afterLabel ||
            gui.GetAddonByName<AtkUnitBase>("FreeCompanyChest", 1) != addon || !Ready(addon) ||
            AgentFreeCompanyChest.Instance() != chestAgent || !chestAgent->IsAgentActive() || chestAgent->AddonId != addon->Id)
            return;
        result.AddRange(observed);
        if (observed.Count > 0) this.FcChestUnavailable = null;
    }

    private StorageContainerCapture? ReadContainer(string category, string sourceKey, string sourceLabel,
        InventoryType type, int expectedSize)
    {
        var manager = InventoryManager.Instance();
        if (manager == null) return null;
        var container = manager->GetInventoryContainer(type);
        if (!ContainerReady(container, type, expectedSize)) return null;
        var address = container->Items;
        var copied = inventory.GetInventoryItems((GameInventoryType)type).ToArray();
        if (copied.Length != expectedSize) return null;
        var entries = DescribeItems(copied, (GameInventoryType)type, this.ItemName);
        if (InventoryManager.Instance() != manager || manager->GetInventoryContainer(type) != container ||
            !ContainerReady(container, type, expectedSize) || container->Items != address) return null;
        var after = inventory.GetInventoryItems((GameInventoryType)type);
        if (after.Length != copied.Length) return null;
        for (var slot = 0; slot < copied.Length; slot++)
            if (!copied[slot].Equals(after[slot])) return null;
        return new(category, sourceKey, sourceLabel, ContainerLabel(type), expectedSize, entries, DateTimeOffset.UtcNow);
    }

    internal static bool TryHouse(HousingManager* manager, out HouseId house, out bool indoors)
    {
        house = default;
        indoors = false;
        if (manager == null || manager->CurrentTerritory == null || !manager->CurrentTerritory->IsLoaded()) return false;
        var type = manager->CurrentTerritory->GetTerritoryType();
        if (type == HousingTerritoryType.Indoor)
        {
            if (manager->IndoorTerritory == null ||
                manager->CurrentTerritory != (HousingTerritory*)manager->IndoorTerritory) return false;
            house = manager->IndoorTerritory->HouseId;
            indoors = true;
        }
        else if (type == HousingTerritoryType.Outdoor)
        {
            if (manager->OutdoorTerritory == null ||
                manager->CurrentTerritory != (HousingTerritory*)manager->OutdoorTerritory) return false;
            house = manager->GetCurrentHouseId();
            // A ward contains other people's gardens. Only the selected editing plot qualifies.
            if (house.IsApartment || manager->OutdoorTerritory->EditingFurnishingsOfPlot < 0 ||
                house.PlotIndex != manager->OutdoorTerritory->EditingFurnishingsOfPlot) return false;
        }
        else return false;
        return ValidHouse(house);
    }

    internal static bool ValidHouse(HouseId house) => house.Id != 0 && house.Id != ulong.MaxValue &&
        house.WorldId > 0 && house.TerritoryTypeId > 0 && house.WardIndex < 60 && !house.IsWorkshop &&
        (house.IsApartment ? house.ApartmentDivision < 2 && house.RoomNumber > 0 : house.PlotIndex < 60);

    internal static bool TryCompany(InfoProxyFreeCompany* company, InfoProxyFreeCompanyMember* members,
        ulong owner, out ulong id, out string label)
    {
        id = 0;
        label = "";
        if (owner == 0 || company == null || members == null || company->Id == 0 ||
            company->Id != members->FreeCompanyId || members->CharData == null || members->EntryCount is < 1 or > 512)
            return false;
        var ownMemberSeen = false;
        // Only test local ownership; no other member's identity, name or account ID is copied.
        foreach (ref readonly var member in members->CharDataSpan)
            if (member.ContentId == owner) { ownMemberSeen = true; break; }
        if (!ownMemberSeen) return false;
        var name = company->NameString.Trim();
        if (name.Length is < 1 or > 80 || name.Any(char.IsControl)) return false;
        id = company->Id;
        label = "FC chest — " + name;
        return true;
    }

    internal static StorageItem[] DescribeItems(ReadOnlySpan<GameInventoryItem> slots, GameInventoryType type,
        Func<uint, string> name)
    {
        if (slots.Length != Capacity((InventoryType)type))
            throw new InvalidOperationException("Housing or FC inventory layout is unsupported.");
        var entries = new List<StorageItem>();
        for (var slot = 0; slot < slots.Length; slot++)
        {
            var item = slots[slot];
            if (item.IsEmpty) continue;
            if (item.InventorySlot != slot || item.ContainerType != type || item.BaseItemId == 0 || item.Quantity <= 0)
                throw new InvalidOperationException("Housing or FC inventory slot identity is unavailable.");
            entries.Add(new(slot, item.ItemId, item.BaseItemId, name(item.BaseItemId), item.Quantity, item.IsHq));
        }
        return entries.ToArray();
    }

    internal static ReadOnlySpan<InventoryType> FcTypes =>
    [
        InventoryType.FreeCompanyPage1, InventoryType.FreeCompanyPage2, InventoryType.FreeCompanyPage3,
        InventoryType.FreeCompanyPage4, InventoryType.FreeCompanyPage5, InventoryType.FreeCompanyCrystals,
    ];

    internal static InventoryType[] HousingTypes(bool indoors) => indoors
        ? [InventoryType.HousingInteriorPlacedItems1, InventoryType.HousingInteriorPlacedItems2,
            InventoryType.HousingInteriorPlacedItems3, InventoryType.HousingInteriorPlacedItems4,
            InventoryType.HousingInteriorPlacedItems5, InventoryType.HousingInteriorPlacedItems6,
            InventoryType.HousingInteriorPlacedItems7, InventoryType.HousingInteriorPlacedItems8,
            InventoryType.HousingInteriorPlacedItems9, InventoryType.HousingInteriorPlacedItems10,
            InventoryType.HousingInteriorPlacedItems11, InventoryType.HousingInteriorPlacedItems12,
            InventoryType.HousingInteriorStoreroom1, InventoryType.HousingInteriorStoreroom2,
            InventoryType.HousingInteriorStoreroom3, InventoryType.HousingInteriorStoreroom4,
            InventoryType.HousingInteriorStoreroom5, InventoryType.HousingInteriorStoreroom6,
            InventoryType.HousingInteriorStoreroom7, InventoryType.HousingInteriorStoreroom8,
            InventoryType.HousingInteriorStoreroom9, InventoryType.HousingInteriorStoreroom10,
            InventoryType.HousingInteriorStoreroom11]
        : [InventoryType.HousingExteriorPlacedItems, InventoryType.HousingExteriorPlacedItems2,
            InventoryType.HousingExteriorStoreroom, InventoryType.HousingExteriorStoreroom2];

    internal static int Capacity(InventoryType type) => type switch
    {
        >= InventoryType.FreeCompanyPage1 and <= InventoryType.FreeCompanyPage5 => 50,
        InventoryType.FreeCompanyCrystals => 18,
        >= InventoryType.HousingInteriorPlacedItems1 and <= InventoryType.HousingInteriorPlacedItems12 => 50,
        >= InventoryType.HousingInteriorStoreroom1 and <= InventoryType.HousingInteriorStoreroom11 => 50,
        InventoryType.HousingExteriorPlacedItems or InventoryType.HousingExteriorPlacedItems2 or
            InventoryType.HousingExteriorStoreroom or InventoryType.HousingExteriorStoreroom2 => 40,
        _ => throw new InvalidOperationException("Storage type is outside the supported allowlist."),
    };

    internal static string ContainerLabel(InventoryType type) => type switch
    {
        >= InventoryType.FreeCompanyPage1 and <= InventoryType.FreeCompanyPage5 => $"Chest page {type - InventoryType.FreeCompanyPage1 + 1}",
        InventoryType.FreeCompanyCrystals => "Chest crystals",
        >= InventoryType.HousingInteriorPlacedItems1 and <= InventoryType.HousingInteriorPlacedItems12 => $"Indoor placed furnishings {type - InventoryType.HousingInteriorPlacedItems1 + 1}",
        >= InventoryType.HousingInteriorStoreroom1 and <= InventoryType.HousingInteriorStoreroom11 => $"Indoor storeroom {type - InventoryType.HousingInteriorStoreroom1 + 1}",
        InventoryType.HousingExteriorPlacedItems => "Outdoor placed furnishings 1",
        InventoryType.HousingExteriorPlacedItems2 => "Outdoor placed furnishings 2",
        InventoryType.HousingExteriorStoreroom => "Outdoor storeroom 1",
        InventoryType.HousingExteriorStoreroom2 => "Outdoor storeroom 2",
        _ => throw new InvalidOperationException("Storage type is outside the supported allowlist."),
    };

    private string ItemName(uint id) => data.GetExcelSheet<Item>().TryGetRow(id, out var item)
        ? item.Name.ExtractText().Trim() : $"Item #{id}";

    private string HouseLabel(HouseId house)
    {
        var world = data.GetExcelSheet<World>().TryGetRow(house.WorldId, out var worldRow)
            ? worldRow.Name.ExtractText() : $"World {house.WorldId}";
        var district = data.GetExcelSheet<TerritoryType>().TryGetRow(house.TerritoryTypeId, out var territory)
            ? territory.PlaceName.Value.Name.ExtractText() : $"District {house.TerritoryTypeId}";
        var address = house.IsApartment ? $"Apartment {house.RoomNumber}, division {house.ApartmentDivision + 1}"
            : $"Plot {house.PlotIndex + 1}" + (house.RoomNumber > 0 ? $", room {house.RoomNumber}" : "");
        var label = $"{world} · {district} · Ward {house.WardIndex + 1} · {address}";
        return label.Length <= 128 ? label : label[..128];
    }

    internal static bool ContainerReady(InventoryContainer* container, InventoryType type, int expectedSize) =>
        container != null && container->Items != null && container->IsLoaded && container->Type == type &&
        container->Size == expectedSize;

    private static bool OwnerReady(PlayerState* player, ulong owner) =>
        owner != 0 && player != null && player->IsLoaded && player->ContentId == owner;
    private static bool Ready(AtkUnitBase* addon) => addon != null && addon->IsVisible && addon->IsReady;
}
