using System;

namespace CompAI;

/// <summary>Managed values only; no ContentId, entity ID, world IDs or native references.</summary>
internal sealed record GameContextReading(DateTimeOffset CapturedAtUtc, GameCharacterContext? Character,
    NpcDialogueContext? Dialogue, string? CharacterUnavailable, string? DialogueUnavailable,
    GameLocationContext? Location = null, string? LocationUnavailable = null);

/// <summary>
/// Level is the actual unsynced class/job level. Experience is progress within that level;
/// NextLevelExperience is the total needed for the next level, not the remaining amount.
/// Null EXP fields mean unavailable, including a next-level requirement at the job's cap.
/// </summary>
internal sealed record GameCharacterContext(string Name, uint ClassJobId, string ClassJobName,
    string Abbreviation, int Level, int? Experience, int? NextLevelExperience);

/// <summary>
/// World names belong to Location access. CurrentWorldName identifies the world being visited;
/// HomeWorldName identifies the home world. Neither is a data-center name, and null means unknown.
/// </summary>
internal sealed record GameLocationContext(uint TerritoryId, string LocationName,
    string? CurrentWorldName = null, string? HomeWorldName = null);

/// <summary>One currently displayed Talk sentence; an empty speaker has no asserted identity.</summary>
internal sealed record NpcDialogueContext(string Speaker, string Text);
