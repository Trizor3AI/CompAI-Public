using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CompAI;

internal sealed record MacroCopyPart(string Label, string Text, int LineCount);

/// <summary>Extracts copyable text only; it neither validates nor executes game commands.</summary>
internal static class MacroCopy
{
    private const int LinesPerPart = 15;
    private static readonly Regex PartHeading = new(
        @"^(?:Macro\s+(?:Part|P)|Part)\s*([1-9][0-9]*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private sealed record Block(string[] Lines, int? ExplicitPart);

    internal static IReadOnlyList<MacroCopyPart> Extract(string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var lines = answer.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var blocks = new List<Block>();
        string? adjacentHeading = null;
        for (var index = 0; index < lines.Length; index++)
        {
            if (!TryOpenFence(lines[index], out var marker, out var length, out var acceptedTag))
            {
                if (!string.IsNullOrWhiteSpace(lines[index])) adjacentHeading = lines[index];
                continue;
            }

            var start = index + 1;
            while (++index < lines.Length && !IsClosingFence(lines[index], marker, length)) { }
            if (index == lines.Length) break; // An unfinished streamed block is never copyable.

            if (acceptedTag && IsMacro(lines, start, index))
                blocks.Add(new Block(lines[start..index], ReadPartHeading(adjacentHeading)));
            adjacentHeading = null;
        }

        var result = new List<MacroCopyPart>();
        for (var blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
        {
            var block = blocks[blockIndex];
            var label = block.ExplicitPart is { } number ? $"Macro P{number}"
                : blocks.Count == 1 ? "Macro" : $"Macro {blockIndex + 1}";
            var partCount = (block.Lines.Length + LinesPerPart - 1) / LinesPerPart;
            for (var part = 0; part < partCount; part++)
            {
                var start = part * LinesPerPart;
                var count = Math.Min(LinesPerPart, block.Lines.Length - start);
                var partLabel = partCount == 1 ? label
                    : block.ExplicitPart.HasValue ? $"{label} ({part + 1}/{partCount})"
                    : $"{label} P{part + 1}";
                result.Add(new(partLabel, string.Join("\n", block.Lines, start, count), count));
            }
        }
        return result;
    }

    private static bool TryOpenFence(string line, out char marker, out int length, out bool acceptedTag)
    {
        marker = '\0';
        length = 0;
        acceptedTag = false;
        var start = FenceStart(line);
        if (start < 0 || start == line.Length || line[start] is not ('`' or '~')) return false;
        marker = line[start];
        while (start + length < line.Length && line[start + length] == marker) length++;
        if (length < 3) return false;
        var tag = line[(start + length)..].Trim();
        if (marker == '`' && tag.Contains('`')) return false;
        acceptedTag = tag.Length == 0 || tag.Equals("text", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("plaintext", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("macro", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("ffxiv", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private static bool IsClosingFence(string line, char marker, int length)
    {
        var start = FenceStart(line);
        if (start < 0) return false;
        var end = start;
        while (end < line.Length && line[end] == marker) end++;
        return end - start >= length && string.IsNullOrWhiteSpace(line[end..]);
    }

    private static int FenceStart(string line)
    {
        var start = 0;
        while (start < line.Length && line[start] == ' ') start++;
        return start <= 3 ? start : -1;
    }

    private static bool IsMacro(string[] lines, int start, int end)
    {
        var hasCommand = false;
        for (var index = start; index < end; index++)
        {
            var line = lines[index].AsSpan().Trim();
            if (line.IsEmpty) continue;
            if (line.Length < 2 || line[0] != '/' || !char.IsLetter(line[1])) return false;
            // Require a command token, excluding paths, URLs, comments and slash-prefixed code.
            var tokenEnd = 2;
            while (tokenEnd < line.Length && !char.IsWhiteSpace(line[tokenEnd]))
            {
                if (!char.IsLetterOrDigit(line[tokenEnd])) return false;
                tokenEnd++;
            }
            hasCommand = true;
        }
        return hasCommand;
    }

    private static int? ReadPartHeading(string? heading)
    {
        if (heading is null) return null;
        var value = heading.Trim().TrimStart('#').Trim().TrimEnd(':').TrimEnd();
        if (value.Length >= 4 && ((value.StartsWith("**", StringComparison.Ordinal) && value.EndsWith("**", StringComparison.Ordinal))
            || (value.StartsWith("__", StringComparison.Ordinal) && value.EndsWith("__", StringComparison.Ordinal))))
            value = value[2..^2].Trim().TrimEnd(':').TrimEnd();
        var match = PartHeading.Match(value);
        return match.Success && int.TryParse(match.Groups[1].Value, out var number) ? number : null;
    }
}
