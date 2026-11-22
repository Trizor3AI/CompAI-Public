using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Game.Inventory;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;

namespace CompAI;

/// <summary>
/// On-demand framework-thread reader. The caller must authorize character access
/// before calling and recheck its capture/session epoch before publishing. It never
/// equips items, switches jobs, opens UI, searches other inventories, or sends data.
/// Native pointers and character identifiers stay inside this reader.
/// </summary>
internal sealed unsafe class CharacterDetailsReader(IClientState clientState,
    IObjectTable objects, IDataManager data, IPlayerState playerState, IGameInventory inventory)
{
    internal const int MaxJobs = 100;
    internal const int MaxGearsets = 100;
    internal const int EquipmentSlotCount = 14;
    internal const int MateriaSlotCount = 5;
    internal const string JobsSource = "Dalamud IPlayerState; local ClassJob sheet.";
    internal const string JobsCoverage =
        "Actual unsynced class/job levels and current-level EXP; shared class/job EXP indices are preserved. " +
        "Levels do not establish job unlock status.";
    internal const string EquippedSource =
        "Loaded EquippedItems container; Dalamud IGameInventory; local Item and Materia sheets.";
    internal const string EquippedCoverage =
        "Currently worn equipment, including explicit empty slots and the unused belt slot. " +
        "Materia slots and grades are zero-based; no effective stat bonuses are inferred.";
    internal const string GearsetsSource = "Local RaptureGearsetModule saved entries; local Item and Materia sheets.";
    internal const string GearsetsCoverage =
        "Saved gearset plans, not verified item possession or current equipment. Saved items and materia may be " +
        "stale or missing; a job need not have a saved gearset. IsCurrent means the selected saved-set index only. " +
        "Gearset IDs are zero-based; the displayed gearset number is ID + 1.";

    private static readonly string[] SlotNames =
    [
        "Main hand", "Off hand", "Head", "Body", "Hands", "Belt (unused)", "Legs", "Feet",
        "Earrings", "Necklace", "Bracelets", "Right ring", "Left ring", "Soul crystal",
    ];

    internal CharacterDetailsMetadata CreateMetadataCache() => new(this.ResolveItem, this.ResolveMateria);

    internal CharacterDetailsReading Capture(CharacterDetailsSelection selection, CharacterDetailsMetadata metadata)
    {
        selection.Validate();
        var capturedAt = DateTimeOffset.UtcNow;
        try
        {
            var identity = this.ReadIdentity();
            var reading = CaptureRequested(selection, capturedAt, this.ReadJobs,
                () => this.ReadEquipped(metadata), () => this.ReadGearsets(identity.ContentId, selection, metadata));
            if (this.ReadIdentity() != identity)
                return UnavailableReading(capturedAt, "Character changed during reading; waiting for it to settle.");
            return reading;
        }
        catch (Exception ex)
        {
            return UnavailableReading(capturedAt, Unavailable("Character details", ex));
        }
    }

    internal static CharacterDetailsReading CaptureRequested(CharacterDetailsSelection selection,
        DateTimeOffset capturedAt, Func<CharacterJobContext[]> readJobs,
        Func<CharacterEquipmentItem[]> readEquipped, Func<CharacterGearsetContext[]> readGearsets)
    {
        selection.Validate();
        var reading = UnavailableReading(capturedAt, "This section was not requested in this capture.");
        return selection.Section switch
        {
            CharacterDetailsSection.Jobs => reading with { Jobs = readJobs(), JobsUnavailable = null },
            CharacterDetailsSection.Equipped => reading with { Equipped = readEquipped(), EquippedUnavailable = null },
            _ => reading with { Gearsets = readGearsets(), GearsetsUnavailable = null },
        };
    }

    private (ulong ContentId, uint EntityId, nint Address, uint ClassJobId) ReadIdentity()
    {
        if (!clientState.IsLoggedIn || !playerState.IsLoaded)
            throw new InvalidOperationException("Character is not loaded.");
        var local = objects.LocalPlayer;
        var contentId = playerState.ContentId;
        var entityId = playerState.EntityId;
        var classJobId = playerState.ClassJob.RowId;
        if (local is null || local.Address == nint.Zero || contentId == 0 || entityId == 0 ||
            classJobId == 0 || local.EntityId != entityId || local.ClassJob.RowId != classJobId)
            throw new InvalidOperationException("Character identity is not ready.");
        return (contentId, entityId, local.Address, classJobId);
    }

    private CharacterJobContext[] ReadJobs()
    {
        var jobs = new List<CharacterJobContext>();
        var rowsInspected = 0;
        foreach (var row in data.GetExcelSheet<ClassJob>())
        {
            // Include row zero in the scan bound, but not the adventurer placeholder.
            if (++rowsInspected > MaxJobs + 1)
                throw new InvalidOperationException("Class/job sheet exceeds the inspected bounds.");
            if (row.RowId == 0) continue;
            int? level = null;
            int? experience = null;
            if (row.ExpArrayIndex >= 0)
            {
                try
                {
                    var value = (int)playerState.GetClassJobLevel(row);
                    if (value is >= 0 and <= 200) level = value;
                }
                catch { /* Preserve the class/job entry with an unavailable level. */ }
                try
                {
                    var value = playerState.GetClassJobExperience(row);
                    if (value >= 0) experience = value;
                }
                catch { /* EXP failure must not erase the level or other jobs. */ }
            }
            jobs.Add(new(row.RowId, Label(row.Name.ExtractText(), "[class/job name unavailable]"),
                Label(row.Abbreviation.ExtractText(), "[abbreviation unavailable]", 32), level, experience));
        }
        return jobs.ToArray();
    }

    private CharacterEquipmentItem[] ReadEquipped(CharacterDetailsMetadata metadata)
    {
        var manager = InventoryManager.Instance();
        if (manager == null) throw new InvalidOperationException("Inventory manager is unavailable.");
        var container = manager->GetInventoryContainer(InventoryType.EquippedItems);
        if (container == null || container->Items == null || !container->IsLoaded ||
            container->Type != InventoryType.EquippedItems || container->Size != EquipmentSlotCount)
            throw new InvalidOperationException("Equipment container is not loaded or its layout changed.");
        var itemAddress = container->Items;
        var source = inventory.GetInventoryItems(GameInventoryType.EquippedItems);
        if (source.Length != EquipmentSlotCount)
            throw new InvalidOperationException("Equipment slot count differs from the inspected build.");
        var copied = source.ToArray();
        var result = new CharacterEquipmentItem[EquipmentSlotCount];
        for (var slot = 0; slot < result.Length; slot++)
        {
            var item = copied[slot];
            if (item.IsEmpty) { result[slot] = EmptyEquipment(slot); continue; }
            if (item.ContainerType != GameInventoryType.EquippedItems || item.InventorySlot != slot)
                throw new InvalidOperationException("Equipment slot layout changed.");
            result[slot] = DescribeItem(metadata, slot, item.ItemId, item.BaseItemId, item.IsHq,
                item.Materia, item.MateriaGrade);
        }
        if (InventoryManager.Instance() != manager ||
            manager->GetInventoryContainer(InventoryType.EquippedItems) != container ||
            container->Items != itemAddress || !container->IsLoaded || container->Size != EquipmentSlotCount ||
            container->Type != InventoryType.EquippedItems)
            throw new InvalidOperationException("Equipment allocation changed during reading.");
        var after = inventory.GetInventoryItems(GameInventoryType.EquippedItems);
        if (after.Length != copied.Length)
            throw new InvalidOperationException("Equipment changed during reading.");
        for (var slot = 0; slot < copied.Length; slot++)
            if (!copied[slot].Equals(after[slot]))
                throw new InvalidOperationException("Equipment changed during reading.");
        return result;
    }

    private CharacterGearsetContext[] ReadGearsets(ulong contentId, CharacterDetailsSelection selection,
        CharacterDetailsMetadata metadata)
    {
        var module = RaptureGearsetModule.Instance();
        if (module == null || module->UIModulePtr == null || module->CharacterContentId != contentId ||
            module->Entries.Length != MaxGearsets || module->NumGearsets > MaxGearsets)
            throw new InvalidOperationException("Saved gearsets are not loaded for this character.");
        var currentIndex = module->CurrentGearsetIndex;
        var gearsetCount = module->NumGearsets;
        var firstIndex = selection.GearsetId ?? 0;
        var entryCount = selection.GearsetId.HasValue ? 1 : MaxGearsets;
        var entries = module->Entries.Slice(firstIndex, entryCount).ToArray();
        var classJobs = data.GetExcelSheet<ClassJob>();
        var result = DescribeGearsets(entries, firstIndex, currentIndex,
            selection.Section == CharacterDetailsSection.Gearset, metadata, id =>
                classJobs.TryGetRow(id, out var row)
                    ? Label(row.Name.ExtractText(), "[class/job name unavailable]") : "[class/job name unavailable]");
        if (RaptureGearsetModule.Instance() != module || module->CharacterContentId != contentId ||
            module->UIModulePtr == null || module->CurrentGearsetIndex != currentIndex ||
            module->NumGearsets != gearsetCount || module->Entries.Length != MaxGearsets ||
            !MemoryMarshal.AsBytes(entries.AsSpan()).SequenceEqual(
                MemoryMarshal.AsBytes(module->Entries.Slice(firstIndex, entryCount))))
            throw new InvalidOperationException("Saved gearsets changed during reading.");
        return result;
    }

    internal static CharacterGearsetContext[] DescribeGearsets(
        ReadOnlySpan<RaptureGearsetModule.GearsetEntry> entries, int firstIndex, int currentIndex,
        bool includeItems, CharacterDetailsMetadata metadata, Func<uint, string> classJobName)
    {
        if (firstIndex < 0 || entries.Length > MaxGearsets - firstIndex || firstIndex >= MaxGearsets ||
            (includeItems && entries.Length != 1))
            throw new InvalidOperationException("Saved gearset selection exceeds the inspected bounds.");
        var result = new List<CharacterGearsetContext>();
        for (var index = 0; index < entries.Length; index++)
        {
            ref readonly var entry = ref entries[index];
            if ((entry.Flags & RaptureGearsetModule.GearsetFlag.Exists) == 0) continue;
            if (entry.Id != firstIndex + index || entry.Items.Length != EquipmentSlotCount || entry.ClassJob == 0)
                throw new InvalidOperationException("Saved gearset layout changed.");
            var name = ReadGearsetName(entry.Name);
            CharacterEquipmentItem[]? items = includeItems ? new CharacterEquipmentItem[EquipmentSlotCount] : null;
            for (var slot = 0; slot < (items?.Length ?? 0); slot++)
            {
                var item = entry.Items[slot];
                if (item.ItemId == 0) { items![slot] = EmptyEquipment(slot); continue; }
                var (baseId, _) = ItemUtil.GetBaseId(item.ItemId);
                items![slot] = DescribeItem(metadata, slot, item.ItemId, baseId,
                    ItemUtil.IsHighQuality(item.ItemId), item.Materia, item.MateriaGrades);
            }
            result.Add(new(entry.Id, name, entry.ClassJob, classJobName(entry.ClassJob),
                firstIndex + index == currentIndex, items));
        }
        return result.ToArray();
    }

    internal static CharacterEquipmentItem DescribeItem(CharacterDetailsMetadata metadata,
        int slot, uint itemId, uint baseId, bool isHq,
        ReadOnlySpan<ushort> materiaIds, ReadOnlySpan<byte> materiaGrades)
    {
        if ((uint)slot >= EquipmentSlotCount) throw new ArgumentOutOfRangeException(nameof(slot));
        var (name, itemLevel) = metadata.Item(baseId);
        CharacterMateriaContext[] materia;
        string? materiaUnavailable = null;
        try { materia = CopyMateria(materiaIds, materiaGrades, metadata.Materia); }
        catch (Exception ex)
        {
            materia = [];
            materiaUnavailable = Unavailable("Materia", ex);
        }
        return new(slot, SlotNames[slot], itemId, baseId, name, isHq, itemLevel, false,
            materia, materiaUnavailable);
    }

    private (string Name, int? ItemLevel) ResolveItem(uint baseId)
    {
        var name = "[item name unavailable]";
        int? itemLevel = null;
        try
        {
            if (data.GetExcelSheet<Item>().TryGetRow(baseId, out var row))
            {
                name = Label(row.Name.ExtractText(), name);
                if (row.LevelItem.RowId is > 0 and <= int.MaxValue)
                    itemLevel = (int)row.LevelItem.RowId;
            }
        }
        catch { /* Keep raw item identity when optional sheet metadata is unavailable. */ }
        return (name, itemLevel);
    }

    private (uint? ItemId, string? Name) ResolveMateria(uint materiaId, int grade)
    {
        try
        {
            if (!data.GetExcelSheet<Materia>().TryGetRow(materiaId, out var row) ||
                grade < 0 || grade >= row.Item.Count)
                return (null, null);
            var item = row.Item[grade];
            if (item.RowId == 0) return (null, null);
            return (item.RowId, item.IsValid ? Label(item.Value.Name.ExtractText(), "[materia name unavailable]") : null);
        }
        catch { return (null, null); }
    }

    /// <summary>Managed seam; copies fixed slots and keeps unknown IDs/grades without claiming a stat bonus.</summary>
    internal static CharacterMateriaContext[] CopyMateria(ReadOnlySpan<ushort> ids, ReadOnlySpan<byte> grades,
        Func<uint, int, (uint? ItemId, string? Name)> resolve)
    {
        if (ids.Length != MateriaSlotCount || grades.Length != MateriaSlotCount)
            throw new InvalidOperationException("Materia slot count differs from the inspected build.");
        var result = new CharacterMateriaContext[MateriaSlotCount];
        for (var slot = 0; slot < result.Length; slot++)
        {
            var id = ids[slot];
            var grade = grades[slot];
            var label = id == 0 ? (null, null) : resolve(id, grade);
            result[slot] = new(slot, id, id == 0 ? 0 : grade, label.Item1, label.Item2, id == 0);
        }
        return result;
    }

    internal static string ReadGearsetName(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 48) throw new InvalidOperationException("Gearset name layout changed.");
        var end = bytes.IndexOf((byte)0);
        if (end < 0) return "[gearset name unavailable]";
        return Label(Encoding.UTF8.GetString(bytes[..end]), "[gearset name unavailable]", 48);
    }

    internal static CharacterEquipmentItem EmptyEquipment(int slot)
    {
        if ((uint)slot >= EquipmentSlotCount) throw new ArgumentOutOfRangeException(nameof(slot));
        return new(slot, SlotNames[slot], 0, 0, "[empty]", false, null, true,
            CopyMateria(new ushort[MateriaSlotCount], new byte[MateriaSlotCount], (_, _) => (null, null)), null);
    }

    private static string Label(string value, string fallback, int maximum = 128) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximum ? fallback : value.Trim();

    private static string Unavailable(string section, Exception ex) =>
        $"{section} read unavailable ({ex.GetType().Name}).";

    private static CharacterDetailsReading UnavailableReading(DateTimeOffset capturedAt, string reason) =>
        new(capturedAt, null, null, null, reason, reason, reason);
}
