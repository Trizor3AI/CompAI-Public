using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CompAI;

internal sealed record GameChatReplyPart(string Text, MapLocationLink? Map = null);
internal sealed record GameChatReplyLine(IReadOnlyList<GameChatReplyPart> Parts)
{
    internal string Text => string.Concat(Parts.Select(part => part.Text));
}

internal static class GameChatReply
{
    internal const int MaxLineBytes = 800;
    internal const int MaxMapActions = 8;
    // Conservative space for native map/link/color payloads. The emitter also checks actual encoded bytes.
    internal const int LinePayloadReserveBytes = 24;
    internal const int MapPayloadReserveBytes = 96;
    internal const int MaxMapLabelBytes = 512;

    internal static IReadOnlyList<string> Format(string answer, string speaker = "CompAI", bool markdown = true) =>
        FormatParts(answer, speaker, markdown).Select(line => line.Text).ToArray();

    /// <summary>Pure bounded layout. Only the optional trusted resolver can make a map action; labels stay atomic.</summary>
    internal static IReadOnlyList<GameChatReplyLine> FormatParts(string answer, string speaker = "CompAI", bool markdown = true,
        Func<MapLocationLink, string?>? resolveMap = null)
    {
        var name = new StringBuilder();
        foreach (var rune in speaker.EnumerateRunes())
        {
            if (name.Length + rune.Utf16SequenceLength > 40) break;
            if (Rune.IsControl(rune) || rune.Value is '[' or ']' or ':') continue;
            name.Append(Rune.IsWhiteSpace(rune) ? " " : rune.ToString());
        }
        var safeName = name.ToString().Trim();
        var prefix = "[" + (safeName.Length == 0 ? "CompAI" : safeName) + "]: ";
        var prefixBytes = Encoding.UTF8.GetByteCount(prefix) + LinePayloadReserveBytes;
        var lines = new List<GameChatReplyLine>();
        var parts = new List<GameChatReplyPart>();
        var plain = new StringBuilder(prefix);
        var bytes = prefixBytes;
        var hasContent = false;
        var space = false;
        var resolvedMaps = new Dictionary<MapLocationLink, string?>();
        var actions = 0;

        void FlushText()
        {
            if (plain.Length == 0) return;
            parts.Add(new(plain.ToString()));
            plain.Clear();
        }
        void FlushLine()
        {
            if (!hasContent) return;
            FlushText();
            lines.Add(new(parts.ToArray()));
            parts.Clear();
            plain.Append(prefix);
            bytes = prefixBytes;
            hasContent = false;
            space = false;
        }
        void AppendPlain(string text)
        {
            foreach (var rune in text.EnumerateRunes())
            {
                if (Rune.IsWhiteSpace(rune)) { space = hasContent; continue; }
                // Model text never becomes SeString payloads or game commands.
                if (Rune.IsControl(rune)) continue;
                var width = rune.Utf8SequenceLength;
                if (bytes + width + (space ? 1 : 0) > MaxLineBytes) FlushLine();
                if (space) { plain.Append(' '); bytes++; space = false; }
                plain.Append(rune.ToString());
                bytes += width;
                hasContent = true;
            }
        }
        bool AppendMap(MapLocationLink map)
        {
            if (resolveMap is null || actions >= MaxMapActions) return false;
            if (!resolvedMaps.TryGetValue(map, out var label))
            {
                if (resolvedMaps.Count >= MaxMapActions) return false;
                try { label = MapLabel(resolveMap(map)); }
                catch (Exception) { label = null; }
                resolvedMaps.Add(map, label);
            }
            if (label is null) return false;
            var width = Encoding.UTF8.GetByteCount(label) + MapPayloadReserveBytes;
            if (bytes + width + (space ? 1 : 0) > MaxLineBytes) FlushLine();
            if (space) { plain.Append(' '); bytes++; space = false; }
            FlushText();
            parts.Add(new(label, map));
            bytes += width;
            hasContent = true;
            actions++;
            return true;
        }

        if (!markdown) AppendPlain(answer);
        else
        {
            var runs = AnswerLinks.Parse(answer);
            for (var index = 0; index < runs.Count; index++)
            {
                var run = runs[index];
                if (MapLocationLink.TryParse(run.Url, out var map))
                {
                    // Bold fragments inside one map label must produce one atomic link.
                    var originalLabel = new StringBuilder(run.Text);
                    while (index + 1 < runs.Count && runs[index + 1].Url == run.Url)
                        originalLabel.Append(runs[++index].Text);
                    if (!AppendMap(map)) AppendPlain(originalLabel.ToString());
                    continue;
                }
                AppendPlain(run.Text);
                if (run.Url is { } url && run.Text != url) AppendPlain(" (" + url + ")");
            }
        }
        FlushLine();
        return lines;
    }

    private static string? MapLabel(string? text)
    {
        if (text is null || text.Length > MaxMapLabelBytes) return null;
        var label = new StringBuilder();
        var space = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) { space = label.Length > 0; continue; }
            if (Rune.IsControl(rune)) continue;
            if (space) { label.Append(' '); space = false; }
            label.Append(rune.ToString());
        }
        var result = label.ToString();
        return result.Length > 0 && Encoding.UTF8.GetByteCount(result) <= MaxMapLabelBytes ? result : null;
    }
}
