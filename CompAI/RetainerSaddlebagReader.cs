using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dalamud.Game.Inventory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace CompAI;

/// <summary>Framework-thread observations of already opened storage. No menu actions or packet hooks.</summary>
internal sealed unsafe class RetainerSaddlebagReader(IGameInventory inventory, IDataManager data, IGameGui gui)
{
    internal IReadOnlyList<StorageContainerCapture> Capture(ulong owner, bool retainers, bool saddlebag, bool personal = false)
    {
        var result = new List<StorageContainerCapture>();
        var player = PlayerState.Instance();
        if (!OwnerReady(player, owner)) return result;
        if (personal)
        {
            // These personal containers need no native UI action. Unloaded is unknown;
            // the previous observation is retained until a complete loaded copy exists.
            foreach (var (type, category, min, max) in new[] {
                (InventoryType.Crystals, "crystals", 18, 18), (InventoryType.KeyItems, "key_items", 1, 200) })
            {
                try
                {
                    var capture = ReadContainer(category, "personal", "Carried inventory", type, min, max, type.ToString(), null);
                    if (capture is not null) result.Add(capture);
                }
                catch (Exception) { /* A loading or unsupported personal container is not an empty one. */ }
            }
        }
        // One unavailable storage source must not discard independently observed storage.
        if (saddlebag)
        {
            try { CaptureSaddlebag(result); }
            catch (Exception) { /* A missing/loading menu leaves the previous observation intact. */ }
        }
        if (retainers)
        {
            try { CaptureRetainer(result); }
            catch (Exception) { /* The next framework observation can retry after loading. */ }
        }
        return PlayerState.Instance() == player && OwnerReady(player, owner) ? result : [];
    }

    private void CaptureSaddlebag(List<StorageContainerCapture> result)
    {
        var regular = gui.GetAddonByName<AtkUnitBase>("InventoryBuddy", 1);
        var expanded = gui.GetAddonByName<AtkUnitBase>("InventoryBuddy2", 1);
        var isExpanded = Ready(expanded);
        if (!isExpanded && !Ready(regular)) return;
        var selectedTab = isExpanded ? -1 : ((AddonInventoryBuddy*)regular)->TabIndex;
        var types = SaddlebagTypes(isExpanded, selectedTab);
        if (types.Length == 0) return;
        var observed = new List<StorageContainerCapture>();
        foreach (var type in types)
        {
            var captured = ReadContainer("saddlebag", "chocobo", "Chocobo saddlebag", type,
                35, 35, type.ToString(), null);
            if (captured is not null) observed.Add(captured);
        }
        if (isExpanded)
        {
            if (gui.GetAddonByName<AtkUnitBase>("InventoryBuddy2", 1) != expanded || !Ready(expanded)) return;
        }
        else if (gui.GetAddonByName<AtkUnitBase>("InventoryBuddy", 1) != regular || !Ready(regular) ||
                 ((AddonInventoryBuddy*)regular)->TabIndex != selectedTab) return;
        result.AddRange(observed);
    }

    private void CaptureRetainer(List<StorageContainerCapture> result)
    {
        var bag = gui.GetAddonByName<AtkUnitBase>("InventoryRetainer", 1);
        var large = gui.GetAddonByName<AtkUnitBase>("InventoryRetainerLarge", 1);
        var sale = gui.GetAddonByName<AtkUnitBase>("RetainerSellList", 1);
        var bagsVisible = Ready(bag) || Ready(large);
        if (!bagsVisible && !Ready(sale)) return;
        var manager = RetainerManager.Instance();
        if (!TryRetainer(manager, out var retainerId, out var label)) return;
        var sourceKey = retainerId.ToString("X16", CultureInfo.InvariantCulture);
        var observed = new List<StorageContainerCapture>();
        if (bagsVisible)
        {
            for (var page = 0; page < 7; page++)
            {
                var type = (InventoryType)((int)InventoryType.RetainerPage1 + page);
                // Native physical containers need not match the sorted screen's page layout.
                var captured = ReadContainer("retainers", sourceKey, label, type, 1, 35,
                    type.ToString(), null);
                if (captured is not null) observed.Add(captured);
            }
            var crystals = ReadContainer("retainers", sourceKey, label, InventoryType.RetainerCrystals,
                18, 18, "RetainerCrystals", null);
            if (crystals is not null) observed.Add(crystals);
        }
        var agent = AgentRetainer.Instance();
        if (Ready(sale) && agent != null && agent->IsAgentActive() &&
            agent->RetainerSellListAddonId == sale->Id)
        {
            var saleRows = CopySaleRows(agent);
            var market = ReadContainer("retainers", sourceKey, label, InventoryType.RetainerMarket,
                20, 20, "Current market listings", saleRows);
            if (market is not null && AgentRetainer.Instance() == agent && agent->IsAgentActive() &&
                agent->RetainerSellListAddonId == sale->Id && saleRows.SequenceEqual(CopySaleRows(agent)) &&
                gui.GetAddonByName<AtkUnitBase>("RetainerSellList", 1) == sale && Ready(sale))
                observed.Add(market);
        }
        // LastSelectedRetainerId survives closing the retainer. It is not enough by itself.
        if (RetainerManager.Instance() != manager || !TryRetainer(manager, out var afterId, out var afterLabel) ||
            retainerId != afterId || label != afterLabel) return;
        if (bagsVisible && !((gui.GetAddonByName<AtkUnitBase>("InventoryRetainer", 1) == bag && Ready(bag)) ||
                            (gui.GetAddonByName<AtkUnitBase>("InventoryRetainerLarge", 1) == large && Ready(large))))
            observed.RemoveAll(x => x.Container != "Current market listings");
        result.AddRange(observed);
    }

    private StorageContainerCapture? ReadContainer(string category, string sourceKey, string label,
        InventoryType type, int minSize, int maxSize, string containerLabel, RetainerSaleRow[]? saleRows)
    {
        var manager = InventoryManager.Instance();
        if (manager == null) return null;
        var container = manager->GetInventoryContainer(type);
        if (!ContainerReady(container, type, minSize, maxSize)) return null;
        var address = container->Items;
        var size = container->Size;
        var copied = inventory.GetInventoryItems((GameInventoryType)type).ToArray();
        if (copied.Length != size) return null;
        var prices = saleRows is null ? null : manager->RetainerMarketPrices.ToArray();
        var slots = copied.Select(item => item.IsEmpty ? new StorageObservedItem(true, 0, 0, 0, 0, 0, false) :
            new StorageObservedItem(false, item.ContainerType, (int)item.InventorySlot, item.ItemId,
                type == InventoryType.KeyItems ? item.ItemId : item.BaseItemId, item.Quantity, item.IsHq)).ToArray();
        var entries = DescribeItems(slots, (GameInventoryType)type, type == InventoryType.KeyItems ? EventName : Name, saleRows, prices);
        if (InventoryManager.Instance() != manager || manager->GetInventoryContainer(type) != container ||
            !ContainerReady(container, type, minSize, maxSize) || container->Items != address || container->Size != size)
            return null;
        var after = inventory.GetInventoryItems((GameInventoryType)type);
        if (after.Length != copied.Length) return null;
        for (var slot = 0; slot < copied.Length; slot++)
            if (!copied[slot].Equals(after[slot])) return null;
        if (prices is not null && !prices.AsSpan().SequenceEqual(manager->RetainerMarketPrices)) return null;
        return new(category, sourceKey, label, containerLabel, size, entries, DateTimeOffset.UtcNow);
    }

    private string Name(uint id)
    {
        var sheet = data.GetExcelSheet<Item>();
        return sheet.TryGetRow(id, out var row) ? row.Name.ExtractText().Trim() : $"Item #{id}";
    }

    private string EventName(uint id)
    {
        var sheet = data.GetExcelSheet<EventItem>();
        var name = sheet.TryGetRow(id, out var row) ? row.Name.ExtractText().Trim() : null;
        return string.IsNullOrWhiteSpace(name) ? $"Key item #{id}" : name;
    }

    internal static StorageItem[] DescribeItems(ReadOnlySpan<StorageObservedItem> slots, GameInventoryType type,
        Func<uint, string> name, RetainerSaleRow[]? saleRows = null, ulong[]? prices = null)
    {
        var nativeType = (InventoryType)type;
        var keyItems = nativeType == InventoryType.KeyItems;
        var crystals = nativeType is InventoryType.Crystals or InventoryType.RetainerCrystals;
        if (slots.Length < 1 || slots.Length > (keyItems ? 200 : 35) || crystals && slots.Length != 18 ||
            (saleRows is not null && (slots.Length != 20 || prices?.Length != 20)))
            throw new InvalidOperationException("Storage layout is unavailable.");
        var rows = new List<StorageItem>();
        for (var slot = 0; slot < slots.Length; slot++)
        {
            var item = slots[slot];
            if (item.IsEmpty) continue;
            if (item.ContainerType != type || item.Slot != slot || item.Quantity < 0 || item.BaseItemId == 0 ||
                keyItems && (item.ItemId is < 2_000_000 or >= 3_000_000 || item.BaseItemId != item.ItemId || item.IsHq) ||
                crystals && item.IsHq)
                throw new InvalidOperationException("Storage slot identity is unavailable.");
            if (crystals && item.Quantity == 0) continue;
            if (item.Quantity == 0) throw new InvalidOperationException("Storage item quantity is unavailable.");
            uint? price = null;
            if (saleRows is not null)
            {
                var matches = saleRows.Where(x => x.Slot == slot && x.Quantity == item.Quantity &&
                    (x.ItemId == item.ItemId || x.ItemId == item.BaseItemId)).ToArray();
                if (matches.Length != 1) throw new InvalidOperationException("Sale listing identity is unavailable.");
                price = UnitPrice(prices![slot]);
            }
            rows.Add(new(slot, item.ItemId, item.BaseItemId, name(item.BaseItemId), item.Quantity, item.IsHq, price));
        }
        if (saleRows is not null && rows.Count != saleRows.Length)
            throw new InvalidOperationException("Sale list changed during reading.");
        return rows.ToArray();
    }

    internal static uint? UnitPrice(ulong value) => value is > 0 and <= 999999999 ? (uint)value : null;

    internal static InventoryType[] SaddlebagTypes(bool expanded, int selectedTab) => expanded
        ? [InventoryType.SaddleBag1, InventoryType.SaddleBag2, InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2]
        : selectedTab switch
        {
            0 => [InventoryType.SaddleBag1, InventoryType.SaddleBag2],
            1 => [InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2],
            _ => [],
        };

    internal static RetainerSaleRow[] CopySaleRows(AgentRetainer* agent)
    {
        if (agent == null || agent->SellListEntryCount is < 0 or > 20)
            throw new InvalidOperationException("Sale list is unavailable.");
        var result = new RetainerSaleRow[agent->SellListEntryCount];
        var slots = new HashSet<ushort>();
        for (var index = 0; index < result.Length; index++)
        {
            var entry = agent->SellListEntries[index];
            if (entry.ItemId == 0 || entry.Quantity <= 0 || entry.InventorySlot >= 20 || !slots.Add(entry.InventorySlot))
                throw new InvalidOperationException("Sale list is incomplete.");
            result[index] = new(entry.InventorySlot, entry.ItemId, entry.Quantity);
        }
        return result;
    }

    internal static bool TryRetainer(RetainerManager* manager, out ulong id, out string label)
    {
        id = 0;
        label = "";
        if (manager == null || !manager->IsReady || manager->LastSelectedRetainerId == 0) return false;
        var selected = manager->LastSelectedRetainerId;
        foreach (ref var retainer in manager->Retainers)
        {
            if (retainer.RetainerId != selected) continue;
            if (id != 0) return false;
            id = selected;
            label = retainer.NameString.Trim();
        }
        return id != 0 && label.Length is > 0 and <= 128 && !label.Any(char.IsControl);
    }

    internal static bool ContainerReady(InventoryContainer* container, InventoryType type, int minSize, int maxSize) =>
        container != null && container->Items != null && container->IsLoaded && container->Type == type &&
        container->Size >= minSize && container->Size <= maxSize;

    private static bool OwnerReady(PlayerState* player, ulong owner) =>
        owner != 0 && player != null && player->IsLoaded && player->ContentId == owner;

    private static bool Ready(AtkUnitBase* addon) => addon != null && addon->IsVisible && addon->IsReady;
}

internal readonly record struct RetainerSaleRow(int Slot, uint ItemId, int Quantity);
internal readonly record struct StorageObservedItem(bool IsEmpty, GameInventoryType ContainerType, int Slot,
    uint ItemId, uint BaseItemId, int Quantity, bool IsHq);
