using System;
using System.Collections.Generic;
using System.Text.Json;

namespace CompAI;

internal enum CharacterDetailsSection { Jobs, Equipped, Gearsets, Gearset }

internal readonly record struct CharacterDetailsSelection(CharacterDetailsSection Section, int? GearsetId = null)
{
    internal void Validate()
    {
        if (!Enum.IsDefined(this.Section) || (this.Section == CharacterDetailsSection.Gearset
                ? this.GearsetId is not (>= 0 and < CharacterDetailsReader.MaxGearsets)
                : this.GearsetId is not null))
            throw new ArgumentException("Invalid character detail capture selection.");
    }
}

/// <summary>
/// One question's lazy captures. Paging reuses the selected section's frozen snapshot;
/// another section/set has its own capture timestamp. No cross-question game state is retained.
/// </summary>
internal sealed class CharacterDetailsCaptureCache(Func<CharacterDetailsSelection, CharacterDetailsLookup> capture)
{
    private readonly object gate = new();
    private readonly Dictionary<CharacterDetailsSelection, Lazy<CharacterDetailsLookup>> captures = [];

    internal CharacterDetailsLookup GetLookup(JsonElement arguments)
    {
        var selection = CharacterDetailsLookup.SelectCapture(arguments);
        Lazy<CharacterDetailsLookup> value;
        lock (this.gate)
        {
            if (!this.captures.TryGetValue(selection, out value!))
            {
                value = new(() => capture(selection));
                this.captures.Add(selection, value);
            }
        }
        // Do not hold the cache gate while waiting for a framework-thread capture.
        return value.Value;
    }
}

/// <summary>Managed sheet metadata only; never item ownership, equipment state, or character identity.</summary>
internal sealed class CharacterDetailsMetadata(
    Func<uint, (string Name, int? ItemLevel)> resolveItem,
    Func<uint, int, (uint? ItemId, string? Name)> resolveMateria)
{
    private readonly Dictionary<uint, (string Name, int? ItemLevel)> items = [];
    private readonly Dictionary<(uint Id, int Grade), (uint? ItemId, string? Name)> materia = [];

    internal (string Name, int? ItemLevel) Item(uint baseId)
    {
        if (!this.items.TryGetValue(baseId, out var value))
        {
            value = resolveItem(baseId);
            this.items.Add(baseId, value);
        }
        return value;
    }

    internal (uint? ItemId, string? Name) Materia(uint id, int grade)
    {
        var key = (id, grade);
        if (!this.materia.TryGetValue(key, out var value))
        {
            value = resolveMateria(id, grade);
            this.materia.Add(key, value);
        }
        return value;
    }
}
