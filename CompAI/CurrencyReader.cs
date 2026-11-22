using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace CompAI;

/// <summary>
/// Framework-thread only. The caller gates permission and checks the loaded character,
/// session and territory before/after capture. Never opens menus, requests packets or
/// consults CurrencyManager's separate content bucket, whose readiness is unverified.
/// </summary>
internal sealed unsafe class CurrencyReader(IDataManager data)
{
    private readonly CosmicCurrencyReader cosmic = new(data);
    // Item/SpecialItemBucket IDs documented by the installed API 15 CurrencyManager.
    // Deliberately excludes ContentItemBucket and unnamed entries. Presence, never a
    // generic zero-returning count, establishes whether a balance was observed.
    internal static ReadOnlySpan<uint> CommonItemIds => [
        10307, 21072, 21073, 21074, 21075, 21076, 21077, 21078, 21079, 21080, 21081,
        21172, 21935, 22525, 25199, 25200, 26533, 26807, 28063, 28186, 28187, 28188,
        30341, 33913, 33914, 36656, 36657, 37854, 38534, 38952, 39885, 41079,
        41629, 41784, 41785, 44472, 46178, 48084, 49125, 52322];

    internal CurrencySnapshot Capture(uint territory)
    {
        try
        {
            var manager = InventoryManager.Instance();
            if (manager == null) return CurrencySnapshot.Unavailable("Currency inventory manager is unavailable.");
            var container = manager->GetInventoryContainer(InventoryType.Currency);
            if (!Usable(container))
                return CurrencySnapshot.Unavailable("Currency inventory container is not loaded or has an unsupported layout.");
            var capturedAt = DateTimeOffset.UtcNow;
            var count = container->Size;
            var itemAddress = container->Items;
            Span<CurrencySlotValue> copied = stackalloc CurrencySlotValue[count];
            for (var index = 0; index < count; index++) copied[index] = Copy(itemAddress[index]);

            var commonManager = CurrencyManager.Instance();
            Span<CurrencyBalanceValue> common = stackalloc CurrencyBalanceValue[CommonItemIds.Length];
            if (commonManager != null)
                for (var index = 0; index < common.Length; index++)
                    common[index] = CopyCommon(commonManager, CommonItemIds[index]);

            var snapshot = Describe(capturedAt, true, copied, id =>
                data.GetExcelSheet<Item>().TryGetRow(id, out var row) ? row.Name.ExtractText() : null);
            if (commonManager != null)
                snapshot = AddCommon(snapshot, common, id =>
                    data.GetExcelSheet<Item>().TryGetRow(id, out var row) ? row.Name.ExtractText() : null);
            snapshot = AddCosmic(snapshot, this.cosmic.Capture(territory));

            if (InventoryManager.Instance() != manager ||
                manager->GetInventoryContainer(InventoryType.Currency) != container || !Usable(container) ||
                container->Items != itemAddress || container->Size != count)
                return CurrencySnapshot.Unavailable("Currency inventory changed during reading; try again after loading.");
            for (var index = 0; index < count; index++)
                if (copied[index] != Copy(itemAddress[index]))
                    return CurrencySnapshot.Unavailable("Currency balances changed during reading; try again.");
            if (CurrencyManager.Instance() != commonManager)
                return CurrencySnapshot.Unavailable("Common currency state changed during reading; try again.");
            if (commonManager != null)
                for (var index = 0; index < common.Length; index++)
                    if (common[index] != CopyCommon(commonManager, CommonItemIds[index]))
                        return CurrencySnapshot.Unavailable("Common currency balances changed during reading; try again.");
            return snapshot;
        }
        catch (Exception ex)
        {
            // No pointers, character identity, file paths or arbitrary exception text in results.
            return CurrencySnapshot.Unavailable($"Currency read unavailable ({ex.GetType().Name}).");
        }
    }

    private static bool Usable(InventoryContainer* container) => container != null &&
        container->Type == InventoryType.Currency && container->IsLoaded && container->Items != null &&
        container->Size is >= 1 and <= CurrencySnapshot.MaxEntries;

    private static CurrencySlotValue Copy(InventoryItem item) =>
        new(item.ItemId, item.Quantity, item.Slot, item.Container == InventoryType.Currency, item.IsSymbolic);

    private static CurrencyBalanceValue CopyCommon(CurrencyManager* manager, uint itemId)
    {
        var present = manager->HasItem(itemId);
        return new(itemId, present, present ? manager->GetItemCount(itemId) : 0);
    }

    internal static CurrencySnapshot AddCosmic(CurrencySnapshot inventory, CurrencyEntry[] entries)
    {
        if (!inventory.Available || entries.Length == 0) return inventory;
        if (entries.Length != 2 || entries[0].ItemId != CosmicCurrencyReader.CosmocreditId ||
            !CosmicCurrencyReader.SupportedPlanetCredit(entries[1].ItemId))
            return CurrencySnapshot.Unavailable("Cosmic currency entries are inconsistent; balance is unavailable.");
        var combined = inventory.Entries.ToDictionary(entry => entry.ItemId);
        foreach (var entry in entries)
        {
            if (combined.TryGetValue(entry.ItemId, out var existing) && existing.Quantity != entry.Quantity)
                return CurrencySnapshot.Unavailable("Currency sources disagree; balance is unavailable.");
            combined[entry.ItemId] = entry;
        }
        if (combined.Count > CurrencySnapshot.MaxEntries)
            return CurrencySnapshot.Unavailable("Currency entry count exceeds the supported bound.");
        return new(inventory.CapturedAtUtc, true, combined.Values.OrderBy(entry => entry.ItemId));
    }

    internal static CurrencySnapshot AddCommon(CurrencySnapshot inventory,
        ReadOnlySpan<CurrencyBalanceValue> common, Func<uint, string?> itemName)
    {
        if (!inventory.Available) return inventory;
        var entries = inventory.Entries.ToDictionary(entry => entry.ItemId);
        var seen = new HashSet<uint>();
        foreach (var balance in common)
        {
            if (!CommonItemIds.Contains(balance.ItemId) || !seen.Add(balance.ItemId) ||
                (balance.Present && balance.Quantity > int.MaxValue))
                return CurrencySnapshot.Unavailable("Common currency entries are inconsistent; balance is unavailable.");
            if (!balance.Present) continue;
            // The allowlist is not sufficient by itself: the installed Item sheet must
            // identify the entry too. An absent/invalid definition remains unknown.
            var name = itemName(balance.ItemId)?.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length > CurrencySnapshot.MaxNameLength || name.Any(char.IsControl))
                continue;
            if (entries.TryGetValue(balance.ItemId, out var existing) && existing.Quantity != balance.Quantity)
                return CurrencySnapshot.Unavailable("Currency sources disagree; balance is unavailable.");
            entries[balance.ItemId] = new(balance.ItemId, DisplayName(balance.ItemId, name), (int)balance.Quantity);
        }
        if (entries.Count > CurrencySnapshot.MaxEntries)
            return CurrencySnapshot.Unavailable("Currency entry count exceeds the supported bound.");
        return new(inventory.CapturedAtUtc, true, entries.Values.OrderBy(entry => entry.ItemId));
    }

    /// <summary>Managed seam: empty IDs provide no balance, even in a loaded container.</summary>
    internal static CurrencySnapshot Describe(DateTimeOffset capturedAt, bool loaded,
        ReadOnlySpan<CurrencySlotValue> slots, Func<uint, string?> itemName)
    {
        if (!loaded || slots.Length is < 1 or > CurrencySnapshot.MaxEntries)
            return new(capturedAt, false, Array.Empty<CurrencyEntry>(), "Currency inventory container is not loaded or has an unsupported layout.");
        var entries = new List<CurrencyEntry>();
        var seen = new HashSet<uint>();
        for (var index = 0; index < slots.Length; index++)
        {
            var item = slots[index];
            if (item.ItemId == 0) continue;
            if (item.Quantity < 0 || item.Slot != index || !item.IsCurrencyContainer || item.IsSymbolic || !seen.Add(item.ItemId))
                return new(capturedAt, false, Array.Empty<CurrencyEntry>(), "Currency inventory entries are inconsistent; balance is unavailable.");
            var name = itemName(item.ItemId)?.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length > CurrencySnapshot.MaxNameLength || name.Any(char.IsControl))
                name = "[currency name unavailable]";
            else
                name = DisplayName(item.ItemId, name);
            entries.Add(new(item.ItemId, name, item.Quantity));
        }
        return new(capturedAt, true, entries);
    }

    // Expand only verified acronym/ID pairs after validating the local definition.
    // A missing, changed or localized name must not be replaced by assumed metadata.
    private static string DisplayName(uint itemId, string name) => (itemId, name) switch
    {
        (41629, "MGF") => "MGF (Manderville Gold Saucer Fame)",
        (52322, "MGC") => "MGC (Manderville Gold Saucer Credits)",
        _ => name,
    };
}

internal readonly record struct CurrencySlotValue(uint ItemId, int Quantity, int Slot,
    bool IsCurrencyContainer = true, bool IsSymbolic = false);

internal readonly record struct CurrencyBalanceValue(uint ItemId, bool Present, uint Quantity);
