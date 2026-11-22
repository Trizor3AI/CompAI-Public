using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Inventory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace CompAI;

/// <summary>Framework-thread copies of owned Armoury slots. Never reads equipped items or saved gearset references.</summary>
internal sealed unsafe class ArmouryReader(IGameInventory inventory, IDataManager data)
{
    // Names and values were inspected in the installed FFXIVClientStructs assembly.
    // The retired waist container is intentionally absent.
    internal static readonly InventoryType[] Types = [
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead,
        InventoryType.ArmoryBody, InventoryType.ArmoryHands, InventoryType.ArmoryLegs,
        InventoryType.ArmoryFeets, InventoryType.ArmoryEar, InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist, InventoryType.ArmoryRings, InventoryType.ArmorySoulCrystal,
    ];
    internal const int MaximumSlots = 50;
    private readonly Dictionary<uint, string> names = new();
    private readonly Dictionary<(uint Id, int Grade), (uint? ItemId, string? Name)> materiaNames = new();

    internal IReadOnlyList<StorageContainerCapture> Capture(ulong owner, bool permitted)
    {
        if (!permitted) return [];
        try { return this.CaptureReady(owner); }
        catch (Exception) { return []; } // An unsupported/loading Armoury must not discard other storage observations.
    }

    private IReadOnlyList<StorageContainerCapture> CaptureReady(ulong owner)
    {
        var player = PlayerState.Instance();
        if (!OwnerReady(player, owner)) return [];
        var manager = InventoryManager.Instance();
        if (manager == null) return [];
        var copies = new List<(InventoryType Type, nint Container, nint Items, GameInventoryItem[] Slots)>();
        foreach (var type in Types)
        {
            var container = manager->GetInventoryContainer(type);
            // Missing/unloaded remains unknown and retains the previous observation.
            if (!ContainerReady(container, type)) continue;
            var slots = inventory.GetInventoryItems((GameInventoryType)type).ToArray();
            if (slots.Length != container->Size) return [];
            copies.Add((type, (nint)container, (nint)container->Items, slots));
        }
        var now = DateTimeOffset.UtcNow;
        var captures = copies.Select(copy => new StorageContainerCapture("armoury", "personal", "Armoury Chest",
            copy.Type.ToString(), copy.Slots.Length, DescribeItems(copy.Slots.Select(item => item.IsEmpty
                ? new ArmouryObservedItem(true, 0, 0, 0, 0, 0, false, [], [])
                : new ArmouryObservedItem(false, item.ContainerType, (int)item.InventorySlot, item.ItemId,
                    item.BaseItemId, item.Quantity, item.IsHq, item.Materia.ToArray(), item.MateriaGrade.ToArray())).ToArray(),
                copy.Type, this.Name, this.Materia), now)).ToArray();
        // Validate the entire copied set after description, not each page in isolation:
        // an item moving between pages must not become two simultaneous owned rows.
        if (InventoryManager.Instance() != manager || PlayerState.Instance() != player || !OwnerReady(player, owner)) return [];
        foreach (var copy in copies)
        {
            var container = manager->GetInventoryContainer(copy.Type);
            if ((nint)container != copy.Container || !ContainerReady(container, copy.Type) ||
                (nint)container->Items != copy.Items || container->Size != copy.Slots.Length) return [];
            var after = inventory.GetInventoryItems((GameInventoryType)copy.Type);
            if (after.Length != copy.Slots.Length) return [];
            for (var slot = 0; slot < after.Length; slot++)
                if (!copy.Slots[slot].Equals(after[slot])) return [];
        }
        return OwnerReady(player, owner) ? captures : [];
    }

    internal static bool ContainerReady(InventoryContainer* container, InventoryType type) => Types.Contains(type) &&
        container != null && container->Items != null && container->IsLoaded && container->Type == type &&
        container->Size is > 0 and <= MaximumSlots;

    internal static bool OwnerReady(PlayerState* player, ulong owner) =>
        owner != 0 && player != null && player->IsLoaded && player->ContentId == owner;

    internal static StorageItem[] DescribeItems(ReadOnlySpan<ArmouryObservedItem> slots, InventoryType type,
        Func<uint, string> name, Func<uint, int, (uint? ItemId, string? Name)> materia)
    {
        if (!Types.Contains(type) || slots.Length is < 1 or > MaximumSlots)
            throw new InvalidOperationException("Armoury layout is unavailable.");
        var result = new List<StorageItem>();
        for (var slot = 0; slot < slots.Length; slot++)
        {
            var item = slots[slot];
            if (item.IsEmpty) continue;
            if (item.ContainerType != (GameInventoryType)type || item.Slot != slot || item.Quantity != 1 ||
                item.BaseItemId is 0 or >= 1_000_000 ||
                item.ItemId != item.BaseItemId + (item.IsHq ? 1_000_000u : 0u))
                throw new InvalidOperationException("Armoury slot identity is unavailable.");
            result.Add(new(slot, item.ItemId, item.BaseItemId, name(item.BaseItemId), 1, item.IsHq,
                Materia: CopyMateria(item.Materia, item.MateriaGrades, materia)));
        }
        return result.ToArray();
    }

    internal static StorageMateria[] CopyMateria(ReadOnlySpan<ushort> ids, ReadOnlySpan<byte> grades,
        Func<uint, int, (uint? ItemId, string? Name)> resolve)
    {
        if (ids.Length != 5 || grades.Length != 5)
            throw new InvalidOperationException("Armoury materia layout is unavailable.");
        var result = new StorageMateria[5];
        for (var slot = 0; slot < result.Length; slot++)
        {
            var id = ids[slot];
            var grade = id == 0 ? 0 : grades[slot];
            if (grade > 20) throw new InvalidOperationException("Armoury materia grade is unsupported.");
            var label = id == 0 ? (null, null) : resolve(id, grade);
            result[slot] = new(slot, id, grade, label.Item1, label.Item2, id == 0);
        }
        return result;
    }

    private string Name(uint id)
    {
        if (this.names.TryGetValue(id, out var cached)) return cached;
        try
        {
            if (data.GetExcelSheet<Item>().TryGetRow(id, out var row) &&
                row.Name.ExtractText().Trim() is { Length: > 0 and <= 256 } text && !text.Any(char.IsControl))
            {
                if (this.names.Count >= 1024) this.names.Clear();
                this.names[id] = text;
                return text;
            }
        }
        catch { /* Preserve the copied owned item when sheet metadata is unavailable. */ }
        return $"Item #{id}";
    }

    private (uint? ItemId, string? Name) Materia(uint id, int grade)
    {
        if (this.materiaNames.TryGetValue((id, grade), out var cached)) return cached;
        try
        {
            if (!data.GetExcelSheet<Lumina.Excel.Sheets.Materia>().TryGetRow(id, out var row) || grade >= row.Item.Count)
                return (null, null);
            var item = row.Item[grade];
            if (item.RowId is 0 or >= 1_000_000) return (null, null);
            var text = item.IsValid ? item.Value.Name.ExtractText().Trim() : null;
            if (text is null || text.Length is < 1 or > 128 || text.Any(char.IsControl)) return (item.RowId, null);
            if (this.materiaNames.Count >= 512) this.materiaNames.Clear();
            return this.materiaNames[(id, grade)] = (item.RowId, text);
        }
        catch { return (null, null); }
    }
}

internal readonly record struct ArmouryObservedItem(bool IsEmpty, GameInventoryType ContainerType, int Slot,
    uint ItemId, uint BaseItemId, int Quantity, bool IsHq, ushort[] Materia, byte[] MateriaGrades);
