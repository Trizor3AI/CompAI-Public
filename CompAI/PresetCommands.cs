using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CompAI;

internal sealed record PresetCommandPlan(string PresetId, string? Command, string? UnavailableReason);

// Pure naming/collision policy. Registration and game access stay in Plugin.
internal static class PresetCommands
{
    internal static string? FromName(string name)
    {
        var text = new StringBuilder();
        try
        {
            foreach (var c in name.Normalize(NormalizationForm.FormD))
            {
                if (char.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                if (c is ' ' or '-') continue;
                var lower = char.ToLowerInvariant(c);
                if (lower is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_')) return null;
                text.Append(lower);
            }
        }
        catch (ArgumentException) { return null; }
        if (text.Length is < 2 or > 32 || text[0] is not (>= 'a' and <= 'z')) return null;
        return "/" + text;
    }

    internal static IReadOnlyList<PresetCommandPlan> Plan(IEnumerable<PersonalityPreset> presets,
        IEnumerable<string> reserved, IEnumerable<string> occupied)
    {
        var candidates = presets.Select(p => new { Preset = p, Command = p.Id == PersonalityPresets.None.Id
            ? null : FromName(p.Name) }).ToArray();
        var duplicates = candidates.Where(p => p.Command is not null).GroupBy(p => p.Command!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var game = new HashSet<string>(reserved, StringComparer.OrdinalIgnoreCase) { "/compai" };
        var plugins = new HashSet<string>(occupied, StringComparer.OrdinalIgnoreCase);
        return candidates.Select(p => new PresetCommandPlan(p.Preset.Id, p.Command,
            p.Preset.Id == PersonalityPresets.None.Id ? "None has no preset shortcut. Use /compai with None as the default." :
            p.Command is null ? "No shortcut: use a name that becomes 2–32 Latin letters, digits or underscores, starting with a letter. Spaces, hyphens and accents are normalized." :
            duplicates.Contains(p.Command) ? p.Command + " is shared by multiple preset names. Rename one before using it." :
            game.Contains(p.Command) ? p.Command + " is reserved by the game or CompAI." :
            plugins.Contains(p.Command) ? p.Command + " is already registered by another plugin." : null)).ToArray();
    }
}
