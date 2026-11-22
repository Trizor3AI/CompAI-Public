using System;

namespace CompAI;

/// <summary>
/// One on-demand, managed-only character snapshot. Null sections mean unavailable
/// or not requested; an empty jobs/gearsets array is a successfully read empty collection. Arrays are
/// newly allocated for this capture and must not be mutated after publication.
/// </summary>
internal sealed record CharacterDetailsReading(DateTimeOffset CapturedAtUtc,
    CharacterJobContext[]? Jobs, CharacterEquipmentItem[]? Equipped,
    CharacterGearsetContext[]? Gearsets, string? JobsUnavailable,
    string? EquippedUnavailable, string? GearsetsUnavailable);

/// <summary>
/// Unsynced level and current-level EXP from the class/job's shared experience index.
/// A positive level does not establish that the job is unlocked; class/job pairs can
/// share an index. Null means the field could not be read, and zero is preserved.
/// </summary>
internal sealed record CharacterJobContext(uint ClassJobId, string Name,
    string Abbreviation, int? Level, int? Experience);

/// <summary>
/// Slot is the game's zero-based equipment slot, including the unused belt slot.
/// Saved-set items describe the saved plan, not possession or currently worn gear.
/// ItemId preserves the source ID; BaseItemId identifies the local Item sheet row.
/// </summary>
internal sealed record CharacterEquipmentItem(int Slot, string SlotName, uint ItemId,
    uint BaseItemId, string Name, bool IsHq, int? ItemLevel, bool IsEmpty,
    CharacterMateriaContext[] Materia, string? MateriaUnavailable);

/// <summary>
/// Slot and Grade are zero-based native indices. MateriaId identifies the Materia
/// sheet row, while ItemId identifies its grade-specific Item row. Unknown labels
/// and IDs remain null; no stat bonus or effective contribution is inferred.
/// Empty materia positions are included explicitly whenever materia is available.
/// </summary>
internal sealed record CharacterMateriaContext(int Slot, uint MateriaId, int Grade,
    uint? ItemId, string? Name, bool IsEmpty);

/// <summary>
/// Id is zero-based (the game's displayed gearset number is Id + 1). IsCurrent is
/// the selected saved-set index, not proof that its items still match worn gear.
/// Entries may be stale or refer to missing gear. A job need not have a saved set.
/// Items is null for a summary-only capture; this must not be treated as an empty set.
/// </summary>
internal sealed record CharacterGearsetContext(int Id, string Name, uint ClassJobId,
    string ClassJobName, bool IsCurrent, CharacterEquipmentItem[]? Items);
