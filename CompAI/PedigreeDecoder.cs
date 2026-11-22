using System;
using System.Collections.Generic;
using System.Linq;

namespace CompAI;

/// <summary>
/// Decodes the displayed parentage rows, never a chocobo's own racing stars. The game
/// draws four stars per parent: dim stars first, then 1-4 bright stars (Addon 8367).
/// Colors 529/530 belong to the male parent; 531/532 belong to the female parent.
/// A layout we cannot identify unambiguously remains unavailable.
/// </summary>
internal static class PedigreeDecoder
{
    internal static bool TryClassify(uint itemId, out string kind, out int grade, out string sex)
    {
        kind = string.Empty;
        grade = 0;
        sex = string.Empty;
        if (itemId is < 9561 or > 9614)
            return false;

        var offset = (int)itemId - 9561;
        kind = (offset / 18) switch
        {
            0 => "fledgling",
            1 => "retired_registration",
            _ => "covering_permission",
        };
        grade = offset % 9 + 1;
        sex = offset % 18 < 9 ? "male" : "female";
        return true;
    }

    internal static bool TryDecode(HoveredChocoboTooltip sample, out PedigreeReading? reading,
        out string reason)
    {
        reading = null;
        reason = "The tooltip parentage layout is unavailable.";
        if (sample is null || sample.ItemId != sample.BaseItemId ||
            !TryClassify(sample.BaseItemId, out var kind, out var grade, out var sex))
            return Fail("This is not a recognized chocobo card item.", out reason);
        if (sample.Nodes is null || sample.Nodes.Length > 256 || sample.StatLabels is null ||
            sample.StatLabels.Length != 5)
            return Fail("Five local chocobo stat labels are required.", out reason);

        var labels = sample.StatLabels.Select(label => label?.Trim().TrimEnd(':').Trim()).ToArray();
        if (labels.Any(string.IsNullOrWhiteSpace) || labels.Distinct(StringComparer.Ordinal).Count() != 5)
            return Fail("Chocobo stat labels are missing or ambiguous.", out reason);

        var rows = new Row?[5];
        var fragments = new List<Fragment>();
        for (var index = 0; index < sample.Nodes.Length; index++)
        {
            var node = sample.Nodes[index];
            if (node is null || node.Text is null || !float.IsFinite(node.X) || !float.IsFinite(node.Y))
                return Fail("The tooltip contains invalid text coordinates.", out reason);

            var labelIndex = -1;
            var contentStart = 0;
            for (var stat = 0; stat < 5; stat++)
            {
                if (!MatchLabel(node.Text, labels[stat]!, out var afterLabel))
                    continue;
                if (labelIndex >= 0 || rows[stat] is not null)
                    return Fail("A chocobo stat label appears more than once or overlaps another label.", out reason);
                labelIndex = stat;
                contentStart = afterLabel;
            }

            if (labelIndex >= 0)
                rows[labelIndex] = new Row(index, node.X, node.Y);

            var contents = node.Text.AsSpan(contentStart);
            if (contents.Contains("???", StringComparison.Ordinal))
                return Fail("The tooltip contains unknown parentage (???).", out reason);

            var hasParentage = contents.IndexOfAny('♂', '♀', '★') >= 0;
            if (!hasParentage)
            {
                // A recognized label followed by another value is not the expected row layout.
                if (labelIndex >= 0 && !string.IsNullOrWhiteSpace(node.Text[contentStart..]))
                    return Fail("A chocobo stat row has an unrecognized value layout.", out reason);
                continue;
            }

            if (node.Runs is null || node.Runs.Any(run => run is null || run.Text is null) ||
                !string.Equals(string.Concat(node.Runs.Select(run => run.Text)), node.Text, StringComparison.Ordinal))
                return Fail("The tooltip's styled text could not be matched to its visible text.", out reason);

            var characters = new List<ColoredChar>();
            var characterIndex = 0;
            foreach (var run in node.Runs)
            {
                foreach (var character in run.Text)
                {
                    if (characterIndex++ < contentStart || char.IsWhiteSpace(character))
                        continue;
                    if (character is not ('♂' or '♀' or '★'))
                        return Fail("The parentage row contains an unrecognized symbol or layout.", out reason);
                    characters.Add(new ColoredChar(character, run.Color));
                }
            }

            fragments.Add(new Fragment(index, node.X, node.Y, characters));
        }

        if (rows.Any(row => row is null))
            return Fail("The tooltip does not expose all five labeled parentage rows.", out reason);
        var completeRows = rows.Select(row => row!).ToArray();
        var tolerances = new float[5];
        for (var stat = 0; stat < 5; stat++)
        {
            var spacing = completeRows.Where((_, other) => other != stat)
                .Min(other => Math.Abs(other.Y - completeRows[stat].Y));
            if (spacing < 2)
                return Fail("Chocobo stat labels overlap vertically; their rows are ambiguous.", out reason);
            // Stay comfortably below half the nearest row spacing, and never cross 10 pixels.
            tolerances[stat] = Math.Min(10, spacing * 0.4f);
        }

        var assigned = Enumerable.Range(0, 5).Select(_ => new List<Fragment>()).ToArray();
        foreach (var fragment in fragments)
        {
            var nearest = Enumerable.Range(0, 5)
                .OrderBy(stat => Math.Abs(fragment.Y - completeRows[stat].Y)).ToArray();
            var stat = nearest[0];
            var distance = Math.Abs(fragment.Y - completeRows[stat].Y);
            if (distance > tolerances[stat] ||
                Math.Abs(distance - Math.Abs(fragment.Y - completeRows[nearest[1]].Y)) < 0.01f)
                return Fail("A parentage fragment cannot be assigned to one stat row.", out reason);
            // These are text-box origins, not the first glyph positions. ItemDetail can use
            // the same origin for a label and its separately aligned paired-star text box.
            // Row proximity and the complete sex/color groups still identify the contents;
            // independently split groups must retain an unambiguous order below.
            if (fragment.NodeIndex != completeRows[stat].NodeIndex && fragment.X < completeRows[stat].X - 0.01f)
                return Fail("A parentage fragment starts to the left of its stat label.", out reason);
            assigned[stat].Add(fragment);
        }

        var father = new int[5];
        var mother = new int[5];
        for (var stat = 0; stat < 5; stat++)
        {
            var ordered = assigned[stat].OrderBy(fragment => fragment.X).ToArray();
            for (var i = 1; i < ordered.Length; i++)
                if (Math.Abs(ordered[i].X - ordered[i - 1].X) < 0.01f)
                    return Fail("Overlapping parentage fragments have ambiguous reading order.", out reason);
            var characters = ordered.SelectMany(fragment => fragment.Characters).ToArray();
            if (!TryReadRow(characters, out father[stat], out mother[stat]))
                return Fail("A parentage row needs one male and one female group, each with four recognized stars.", out reason);
        }

        reading = new PedigreeReading(kind, grade, sex, father, mother);
        reason = "All five displayed parentage rows decoded; these are inherited parent stars, not own racing stars.";
        return true;
    }

    private static bool MatchLabel(string text, string label, out int contentStart)
    {
        contentStart = 0;
        while (contentStart < text.Length && char.IsWhiteSpace(text[contentStart]))
            contentStart++;
        if (!text.AsSpan(contentStart).StartsWith(label, StringComparison.Ordinal))
            return false;
        contentStart += label.Length;
        if (contentStart < text.Length && !char.IsWhiteSpace(text[contentStart]) &&
            text[contentStart] is not (':' or '♂' or '♀' or '★'))
            return false;
        while (contentStart < text.Length && char.IsWhiteSpace(text[contentStart]))
            contentStart++;
        if (contentStart < text.Length && text[contentStart] == ':')
            contentStart++;
        while (contentStart < text.Length && char.IsWhiteSpace(text[contentStart]))
            contentStart++;
        return true;
    }

    private static bool TryReadRow(ColoredChar[] characters, out int father, out int mother)
    {
        father = mother = 0;
        // Each group is exactly a sex symbol and four stars. Colors, not glyph counts alone,
        // distinguish 1-4 parentage stars from the dim filler rendered by the game.
        if (characters.Length != 10)
            return false;
        for (var group = 0; group < 2; group++)
        {
            var offset = group * 5;
            var marker = characters[offset];
            if (marker.Character is not ('♂' or '♀'))
                return false;
            var male = marker.Character == '♂';
            ushort bright = male ? (ushort)529 : (ushort)531;
            ushort dim = male ? (ushort)530 : (ushort)532;
            if (marker.Color != 0 && marker.Color != bright && marker.Color != dim)
                return false;
            var value = 0;
            for (var i = 1; i <= 4; i++)
            {
                var star = characters[offset + i];
                if (star.Character != '★')
                    return false;
                if (star.Color == bright)
                    value++;
                else if (star.Color != dim || value != 0)
                    return false;
            }
            if (value == 0 || (male ? father : mother) != 0)
                return false;
            if (male) father = value;
            else mother = value;
        }
        return father is >= 1 and <= 4 && mother is >= 1 and <= 4;
    }

    private static bool Fail(string message, out string reason)
    {
        reason = message;
        return false;
    }

    private sealed record Row(int NodeIndex, float X, float Y);
    private sealed record Fragment(int NodeIndex, float X, float Y, List<ColoredChar> Characters);
    private readonly record struct ColoredChar(char Character, ushort Color);
}
