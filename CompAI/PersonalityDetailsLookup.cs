using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace CompAI;

// Each request captures an immutable copy from its chat, not a live preset-library reference.
internal sealed class PersonalityDetailsLookup
{
    private readonly Dictionary<string, string> sections;

    internal PersonalityDetailsLookup(PersonalityDetails details)
    {
        if (!PersonalityPresets.ValidDetails(details)) throw new ArgumentException("Invalid personality details.", nameof(details));
        this.sections = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["preferences"] = details.Preferences,
            ["likes_dislikes"] = details.LikesDislikes,
            ["opinions"] = details.Opinions,
            ["background"] = details.Background,
        }.Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    internal bool HasSections => this.sections.Count != 0;
    internal object Summary => new { sections = this.sections.Keys.ToArray() };

    internal object Query(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Invalid personality lookup arguments.");
        string? section = null;
        var count = 0;
        foreach (var property in arguments.EnumerateObject())
        {
            count++;
            if (property.Name != "section" || property.Value.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("Invalid personality lookup arguments.");
            section = property.Value.GetString();
        }
        if (count != 1 || section is null || !this.sections.TryGetValue(section, out var text))
            throw new InvalidOperationException("Personality section is unavailable.");
        return new { section, text };
    }
}
