using System;
using System.Collections.Generic;

namespace CompAI;

internal readonly record struct AnswerTextRun(string Text, string? Url, bool Bold = false);

// Only hyperlinks and paired bold markers are interpreted. All other answer
// text, including newlines and unsupported Markdown, remains literal data.
internal static class AnswerLinks
{
    private readonly record struct RawRun(string Text, string? Url, bool AllowBold);
    private readonly record struct BoldToken(string Text, string? Url, bool Marker);

    internal static IReadOnlyList<AnswerTextRun> Parse(string answer)
    {
        var runs = new List<RawRun>();
        var plainStart = 0;
        var i = 0;
        while (i < answer.Length)
        {
            if (answer[i] == '[' && TryMarkdownSpan(answer, i, out var labelEnd, out var spanEnd, out var destination))
            {
                // Image syntax and invalid destinations stay literal, including the URL text.
                if ((i == 0 || answer[i - 1] != '!') && labelEnd > i + 1 &&
                    TryDestination(destination, out var markdownUrl))
                {
                    AppendPlain(runs, answer, plainStart, i);
                    runs.Add(new RawRun(answer.Substring(i + 1, labelEnd - i - 1), markdownUrl, true));
                    plainStart = spanEnd;
                }
                i = spanEnd;
                continue;
            }
            if (StartsWithHttps(answer, i) && IsBareBoundary(answer, i))
            {
                var end = i;
                while (end < answer.Length && !IsUrlTerminator(answer[end])) end++;
                var candidate = TrimTrailingPunctuation(answer.Substring(i, end - i));
                if (TryCanonicalize(candidate, out var bareUri))
                {
                    var autolink = i > 0 && answer[i - 1] == '<' && end < answer.Length && answer[end] == '>' &&
                                   plainStart <= i - 1 && candidate.Length == end - i;
                    AppendPlain(runs, answer, plainStart, autolink ? i - 1 : i);
                    runs.Add(new RawRun(candidate, bareUri.AbsoluteUri, false));
                    i = autolink ? end + 1 : i + candidate.Length;
                    plainStart = i;
                    continue;
                }
                i = end;
                continue;
            }
            i++;
        }
        AppendPlain(runs, answer, plainStart, answer.Length);
        return ApplyBold(runs);
    }

    internal static IReadOnlyList<Uri> Extract(string answer, int maximum = 20)
    {
        var urls = new List<Uri>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var run in Parse(answer))
        {
            if (urls.Count >= maximum) break;
            if (run.Url is null || !TryCanonicalize(run.Url, out var uri) || !seen.Add(uri.AbsoluteUri)) continue;
            urls.Add(uri);
        }
        return urls;
    }

    // Local map actions are never web sources and never reach Process.Start.
    private static bool TryDestination(string value, out string canonical)
    {
        if (MapLocationLink.TryParse(value, out var map)) { canonical = map.ToUrl(); return true; }
        if (TryCanonicalize(value, out var uri)) { canonical = uri.AbsoluteUri; return true; }
        canonical = "";
        return false;
    }

    internal static IReadOnlyList<MapLocationLink> ExtractMaps(string answer)
    {
        var maps = new List<MapLocationLink>();
        foreach (var run in Parse(answer))
        {
            if (MapLocationLink.TryParse(run.Url, out var map) && !maps.Contains(map)) maps.Add(map);
            if (maps.Count >= 8) break;
        }
        return maps;
    }

    private static void AppendPlain(List<RawRun> runs, string answer, int start, int end)
    {
        if (end > start) runs.Add(new RawRun(answer.Substring(start, end - start), null, true));
    }

    private static IReadOnlyList<AnswerTextRun> ApplyBold(List<RawRun> rawRuns)
    {
        var tokens = new List<BoldToken>();
        foreach (var run in rawRuns)
        {
            if (!run.AllowBold)
            {
                tokens.Add(new BoldToken(run.Text, run.Url, false));
                continue;
            }
            var start = 0;
            while (start < run.Text.Length)
            {
                var marker = run.Text.IndexOf("**", start, StringComparison.Ordinal);
                if (marker < 0) break;
                if (marker > start)
                    tokens.Add(new BoldToken(run.Text.Substring(start, marker - start), run.Url, false));
                tokens.Add(new BoldToken("**", run.Url, true));
                start = marker + 2;
            }
            if (start < run.Text.Length)
                tokens.Add(new BoldToken(run.Text[start..], run.Url, false));
        }

        var paired = new bool[tokens.Count];
        var open = -1;
        var hasContent = false;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!tokens[i].Marker)
            {
                if (!string.IsNullOrWhiteSpace(tokens[i].Text)) hasContent = true;
                continue;
            }
            if (open < 0)
            {
                open = i;
                hasContent = false;
            }
            else if (hasContent)
            {
                paired[open] = paired[i] = true;
                open = -1;
                hasContent = false;
            }
            else
            {
                // Adjacent or whitespace-only marker pairs stay literal.
                open = -1;
            }
        }

        var output = new List<AnswerTextRun>();
        var bold = false;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (paired[i])
            {
                bold = !bold;
                continue;
            }
            if (output.Count > 0 && output[^1].Url == token.Url && output[^1].Bold == bold)
            {
                var previous = output[^1];
                output[^1] = previous with { Text = previous.Text + token.Text };
            }
            else
                output.Add(new AnswerTextRun(token.Text, token.Url, bold));
        }
        return output;
    }

    private static bool StartsWithHttps(string value, int index) =>
        value.AsSpan(index).StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static bool IsBareBoundary(string value, int index) =>
        index == 0 || !char.IsLetterOrDigit(value[index - 1]);

    private static bool IsUrlTerminator(char value) =>
        char.IsWhiteSpace(value) || value is '<' or '>' or '"' or '\'' or '`';

    private static bool TryMarkdownSpan(string answer, int start, out int labelEnd, out int spanEnd,
        out string destination)
    {
        labelEnd = answer.IndexOf(']', start + 1);
        spanEnd = start + 1;
        destination = "";
        if (labelEnd < 0 || labelEnd + 1 >= answer.Length || answer[labelEnd + 1] != '(') return false;
        var depth = 1;
        for (var i = labelEnd + 2; i < answer.Length; i++)
        {
            if (answer[i] == '(') depth++;
            else if (answer[i] == ')' && --depth == 0)
            {
                destination = answer.Substring(labelEnd + 2, i - labelEnd - 2);
                spanEnd = i + 1;
                return true;
            }
        }
        // A broken Markdown construct must not turn its destination into a bare link.
        spanEnd = answer.Length;
        return true;
    }

    internal static bool TryCanonicalize(string url, out Uri uri)
    {
        if (url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps || parsed.UserInfo.Length != 0 ||
            string.IsNullOrWhiteSpace(parsed.Host) || !parsed.IsWellFormedOriginalString())
        {
            uri = null!;
            return false;
        }
        uri = parsed;
        return true;
    }

    private static string TrimTrailingPunctuation(string value)
    {
        while (value.Length > 0)
        {
            var last = value[^1];
            if (last is '.' or ',' or ';' or ':' or '!' or '?' ||
                (last == ')' && IsUnmatchedClosing(value, '(', ')')) ||
                (last == ']' && IsUnmatchedClosing(value, '[', ']')) ||
                (last == '}' && IsUnmatchedClosing(value, '{', '}')))
                value = value[..^1];
            else
                break;
        }
        return value;
    }

    private static bool IsUnmatchedClosing(string value, char opening, char closing)
    {
        var balance = 0;
        foreach (var character in value)
        {
            if (character == opening) balance++;
            if (character == closing) balance--;
        }
        return balance < 0;
    }
}
