using System;
using System.Collections.Generic;
using System.Linq;

namespace CompAI;

// SourceKey is local identity only. It is never exposed to the model.
internal sealed record StorageItem(int Slot, uint ItemId, uint BaseItemId, string Name,
    int Quantity, bool IsHq, uint? UnitPriceGil = null, StorageMateria[]? Materia = null,
    string? MateriaUnavailable = null);
internal sealed record StorageMateria(int Slot, uint MateriaId, int Grade, uint? ItemId, string? Name, bool IsEmpty);
internal sealed record StorageContainerCapture(string Category, string SourceKey, string SourceLabel,
    string Container, int Capacity, StorageItem[] Items, DateTimeOffset CapturedAtUtc);

internal static class StorageSnapshot
{
    internal const int MaxContainers = 256;
    internal const int MaxRows = 10000;
    internal static readonly string[] Categories = ["crystals", "key_items", "armoury", "retainers", "saddlebag", "housing", "fc_chest"];
    internal static string ItemNamespace(string category) => category == "key_items" ? "event_item" : "item";
    internal static StorageItem[] CloneItems(StorageItem[] items) =>
        items.Select(item => item with { Materia = item.Materia?.ToArray() }).ToArray();
    internal static bool SameItems(StorageItem[] left, StorageItem[] right) => left.Length == right.Length &&
        left.Zip(right).All(pair => pair.First with { Materia = null } == pair.Second with { Materia = null } &&
            (pair.First.Materia is null ? pair.Second.Materia is null :
                pair.Second.Materia is not null && pair.First.Materia.SequenceEqual(pair.Second.Materia)));
    internal static bool Valid(StorageContainerCapture? value)
    {
        static bool Text(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);
        if (value is null || !Categories.Contains(value.Category) || !Text(value.SourceKey, 128) ||
            !Text(value.SourceLabel, 128) || !Text(value.Container, 128) || value.Capacity is < 1 or > 1000 ||
            value.CapturedAtUtc == default || value.CapturedAtUtc > DateTimeOffset.UtcNow.AddMinutes(1) ||
            value.Items is null || value.Items.Length > value.Capacity) return false;
        var slots = new HashSet<int>();
        return value.Items.All(item => item is not null && item.Slot >= 0 && item.Slot < value.Capacity &&
            slots.Add(item.Slot) && item.ItemId > 0 &&
            (value.Category == "key_items" ? item.BaseItemId == item.ItemId && item.BaseItemId is >= 2_000_000 and < 3_000_000 && !item.IsHq
                : item.BaseItemId is > 0 and < 1_000_000 && item.ItemId < 2_000_000) &&
            Text(item.Name, 256) && item.Quantity is > 0 and <= 999999 &&
            (item.UnitPriceGil is null || value.Category == "retainers" && item.UnitPriceGil is > 0 and <= 999999999) &&
            (value.Category == "armoury" ? value.Capacity <= 50 && item.Quantity == 1 &&
                item.ItemId == item.BaseItemId + (item.IsHq ? 1_000_000u : 0u) && ValidMateria(item.Materia) &&
                item.MateriaUnavailable is null : item.Materia is null && item.MateriaUnavailable is null));
    }

    private static bool ValidMateria(StorageMateria[]? materia) => materia is { Length: 5 } &&
        materia.Select((entry, slot) => entry is not null && entry.Slot == slot &&
            entry.MateriaId <= ushort.MaxValue && entry.Grade is >= 0 and <= 20 &&
            (entry.IsEmpty ? entry.MateriaId == 0 && entry.Grade == 0 && entry.ItemId is null && entry.Name is null :
                entry.MateriaId > 0 && (entry.ItemId is null || entry.ItemId is > 0 and < 1_000_000) &&
                (entry.Name is null || !string.IsNullOrWhiteSpace(entry.Name) && entry.Name.Length <= 128 &&
                    !entry.Name.Any(char.IsControl)))).All(valid => valid);
}
