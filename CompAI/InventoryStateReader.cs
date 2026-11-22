using System;
using Dalamud.Game.Inventory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace CompAI;

/// <summary>
/// Bounded local comparison of carried Inventory1-4. Read only on the framework thread.
/// The caller supplies a loaded, nonzero character identity and must recheck the session
/// before publishing a capture. No pointer, span, name, or pedigree leaves this reader.
/// </summary>
internal sealed unsafe class InventoryStateReader(IGameInventory inventory)
{
    internal const int ExpectedBagCount = 4;
    internal const int ExpectedSlotsPerBag = 35;
    internal const int ExpectedSlotCount = ExpectedBagCount * ExpectedSlotsPerBag;

    internal InventoryStateStamp Read(ulong contentId)
    {
        RequireIdentity(contentId);
        var manager = InventoryManager.Instance();
        if (manager == null)
            throw new InvalidOperationException("Inventory manager unavailable.");

        ReadOnlySpan<GameInventoryType> types =
        [
            GameInventoryType.Inventory1, GameInventoryType.Inventory2,
            GameInventoryType.Inventory3, GameInventoryType.Inventory4,
        ];
        Span<nint> containers = stackalloc nint[ExpectedBagCount];
        Span<nint> itemPointers = stackalloc nint[ExpectedBagCount];
        Span<InventorySlotState> copied = stackalloc InventorySlotState[ExpectedSlotCount];
        for (var bag = 0; bag < ExpectedBagCount; bag++)
        {
            var type = types[bag];
            var container = manager->GetInventoryContainer((InventoryType)type);
            // GetInventoryItems checks allocation, but does not guarantee IsLoaded.
            if (container == null || container->Items == null || container->Type != (InventoryType)type)
                throw new InvalidOperationException($"{type} unavailable.");
            RequireLoadedBag(container->IsLoaded, container->Size);
            containers[bag] = (nint)container;
            itemPointers[bag] = (nint)container->Items;

            var slots = inventory.GetInventoryItems(type);
            RequireLoadedBag(container->IsLoaded, slots.Length);
            for (var slot = 0; slot < ExpectedSlotsPerBag; slot++)
            {
                var offset = bag * ExpectedSlotsPerBag + slot;
                var item = slots[slot];
                if (item.IsEmpty)
                {
                    copied[offset] = InventorySlotState.Empty;
                    continue;
                }
                if (item.InventorySlot != (uint)slot || item.ContainerType != type)
                    throw new InvalidOperationException("Inventory slot layout changed; state unavailable.");
                copied[offset] = new InventorySlotState(item.ItemId, item.BaseItemId,
                    item.Quantity, item.IsHq);
            }
        }

        if (InventoryManager.Instance() != manager)
            throw new InvalidOperationException("Inventory manager changed during polling; retry after loading.");
        for (var bag = 0; bag < ExpectedBagCount; bag++)
        {
            var container = manager->GetInventoryContainer((InventoryType)types[bag]);
            if (container == null || (nint)container != containers[bag] ||
                (nint)container->Items != itemPointers[bag] || container->Type != (InventoryType)types[bag])
                throw new InvalidOperationException("Inventory allocation changed during polling; retry after loading.");
            RequireLoadedBag(container->IsLoaded, container->Size);
        }
        return new InventoryStateStamp(contentId, copied);
    }

    // Managed-only seam: bag order is Inventory1, Inventory2, Inventory3, Inventory4;
    // slot position is its zero-based index. Empty slots have a single canonical value.
    internal static InventoryStateStamp CopyState(ulong contentId, ReadOnlySpan<InventoryBagState> bags)
    {
        RequireIdentity(contentId);
        if (bags.Length != ExpectedBagCount)
            throw new InvalidOperationException("Carried inventory bag count differs from the inspected build; state unavailable.");
        Span<InventorySlotState> copied = stackalloc InventorySlotState[ExpectedSlotCount];
        for (var bag = 0; bag < ExpectedBagCount; bag++)
        {
            var source = bags[bag];
            RequireLoadedBag(source.IsLoaded, source.Slots.Length);
            for (var slot = 0; slot < ExpectedSlotsPerBag; slot++)
            {
                var item = source.Slots.Span[slot];
                copied[bag * ExpectedSlotsPerBag + slot] = item.IsEmpty ? InventorySlotState.Empty : item;
            }
        }
        return new InventoryStateStamp(contentId, copied);
    }

    private static void RequireIdentity(ulong contentId)
    {
        if (contentId == 0)
            throw new InvalidOperationException("Character identity unavailable for inventory state.");
    }

    private static void RequireLoadedBag(bool isLoaded, int slotCount)
    {
        if (!isLoaded || slotCount != ExpectedSlotsPerBag)
            throw new InvalidOperationException("Carried inventory bag is not loaded or does not contain 35 slots; state unavailable.");
    }
}

internal readonly record struct InventoryBagState(bool IsLoaded, ReadOnlyMemory<InventorySlotState> Slots);

internal readonly record struct InventorySlotState(uint ItemId, uint BaseItemId, int Quantity, bool IsHq, bool IsEmpty = false)
{
    internal static InventorySlotState Empty => new(0, 0, 0, false, true);
}

/// <summary>Exact immutable local polling state; never included in a UI snapshot or export.</summary>
internal sealed class InventoryStateStamp : IEquatable<InventoryStateStamp>
{
    private readonly InventorySlotState[] slots;

    internal InventoryStateStamp(ulong contentId, ReadOnlySpan<InventorySlotState> slots)
    {
        if (contentId == 0 || slots.Length != InventoryStateReader.ExpectedSlotCount)
            throw new InvalidOperationException("Inventory identity or slot count is unavailable.");
        this.ContentId = contentId;
        this.slots = slots.ToArray();
    }

    internal ulong ContentId { get; }

    public bool Equals(InventoryStateStamp? other) =>
        ReferenceEquals(this, other) ||
        (other is not null && this.ContentId == other.ContentId && this.slots.AsSpan().SequenceEqual(other.slots));

    public override bool Equals(object? obj) => obj is InventoryStateStamp other && this.Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(this.ContentId);
        foreach (var slot in this.slots)
            hash.Add(slot);
        return hash.ToHashCode();
    }

    public static bool operator ==(InventoryStateStamp? left, InventoryStateStamp? right) =>
        ReferenceEquals(left, right) || (left is not null && left.Equals(right));

    public static bool operator !=(InventoryStateStamp? left, InventoryStateStamp? right) => !(left == right);
}
