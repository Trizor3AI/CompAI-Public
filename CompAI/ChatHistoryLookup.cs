using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CompAI;

internal sealed record ChatHistoryLookupSummary(string scope, int exchange_count);
internal sealed record ChatHistoryLookupRow(int index, string question, string answer,
    string? context_label, bool truncated);
internal sealed record ChatHistoryLookupResult(string scope, int exchange_count, int total_matches,
    int offset, int? next_offset, ChatHistoryLookupRow[] rows,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ChatHistoryLookupContent? content = null);
internal sealed record ChatHistoryLookupContent(int index, string field, int total_chars, string text);

/// <summary>
/// Reads only the frozen omitted prefix (including a partially supplied latest exchange). It never
/// reads other chats or files. These are historical messages, not verified current game facts.
/// </summary>
internal static class ChatHistoryLookup
{
    internal const string Scope = "current_chat_history";
    internal const int MaxBytes = 20000;
    private static readonly HashSet<string> ArgumentFields = new(StringComparer.Ordinal)
        { "query", "offset", "limit" };
    private static readonly HashSet<string> ReadArgumentFields = new(StringComparer.Ordinal)
        { "query", "offset", "limit", "index", "field" };

    internal static ChatHistoryLookupSummary CreateSummary(IReadOnlyList<ChatTurn> olderTurns)
    {
        ArgumentNullException.ThrowIfNull(olderTurns);
        if (olderTurns.Count > ChatHistory.MaxStoredTurns)
            throw new ArgumentException("Too many saved exchanges.", nameof(olderTurns));
        return new(Scope, olderTurns.Count);
    }

    internal static ChatHistoryLookupResult Query(IReadOnlyList<ChatTurn> olderTurns, JsonElement arguments)
    {
        var (query, offset, limit, index, field) = Parse(arguments);
        var summary = CreateSummary(olderTurns);
        if (index is not null)
            return Read(olderTurns, summary, index.Value, field!, offset);
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = olderTurns.Select((turn, index) => (Turn: turn, Index: index + 1))
            .Where(x => terms.All(term => Contains(x.Turn.Question, term) || Contains(x.Turn.Answer, term) ||
                Contains(x.Turn.ContextLabel, term))).ToArray();
        var selected = matches.Skip(offset).Take(limit).ToArray();
        var count = selected.Length;
        var answerLimit = 6000;
        var contextLimit = 1000;

        ChatHistoryLookupResult Build()
        {
            var rows = selected.Take(count).Select(x => new ChatHistoryLookupRow(x.Index,
                Excerpt(x.Turn.Question, 2000, terms), Excerpt(x.Turn.Answer, answerLimit, terms),
                x.Turn.ContextLabel is null ? null : Excerpt(x.Turn.ContextLabel, contextLimit, terms),
                x.Turn.Question.Length > 2000 || x.Turn.Answer.Length > answerLimit ||
                x.Turn.ContextLabel?.Length > contextLimit)).ToArray();
            return new(summary.scope, summary.exchange_count, matches.Length, offset,
                offset < matches.Length && count < matches.Length - offset ? offset + count : null, rows);
        }

        var result = Build();
        // Keep a real page boundary. If even the first escaped row is large, shorten its labelled
        // excerpts around the match, rather than returning an empty page that cannot advance.
        while (JsonSerializer.SerializeToUtf8Bytes(result).Length > MaxBytes)
        {
            if (count > 1) count--;
            else if (answerLimit > 256) answerLimit = Math.Max(256, answerLimit / 2);
            else if (contextLimit > 128) contextLimit = Math.Max(128, contextLimit / 2);
            else throw new InvalidOperationException("A chat history lookup row exceeded its response budget.");
            result = Build();
        }
        return result;
    }

    private static ChatHistoryLookupResult Read(IReadOnlyList<ChatTurn> turns,
        ChatHistoryLookupSummary summary, int index, string field, int offset)
    {
        if (index > turns.Count) throw new ArgumentException("Saved exchange index is unavailable.");
        var turn = turns[index - 1];
        var text = field == "answer" ? turn.Answer : turn.ContextLabel ?? "";
        if (offset < text.Length && offset > 0 && char.IsLowSurrogate(text[offset]) && char.IsHighSurrogate(text[offset - 1]))
            throw new ArgumentException("Saved exchange offset splits a Unicode character.");
        var count = Math.Min(6000, Math.Max(0, text.Length - offset));
        ChatHistoryLookupResult Build()
        {
            var end = Math.Min(text.Length, offset + count);
            if (end > offset && end < text.Length && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end])) end--;
            var excerpt = offset < text.Length ? text[offset..end] : "";
            return new(summary.scope, summary.exchange_count, 1, offset,
                end < text.Length ? end : null, [], new(index, field, text.Length, excerpt));
        }
        var result = Build();
        while (JsonSerializer.SerializeToUtf8Bytes(result).Length > MaxBytes)
        {
            count /= 2;
            if (count == 0) throw new InvalidOperationException("A chat history content page exceeded its response budget.");
            result = Build();
        }
        return result;
    }

    private static bool Contains(string? text, string term) =>
        text?.Contains(term, StringComparison.OrdinalIgnoreCase) is true;

    private static string Excerpt(string text, int maxChars, string[] terms)
    {
        if (text.Length <= maxChars) return text;
        const string marker = " [excerpt] ";
        var available = maxChars - marker.Length * 2;
        var match = terms.Select(term => (Index: text.IndexOf(term, StringComparison.OrdinalIgnoreCase), Length: term.Length))
            .Where(value => value.Index >= 0).OrderBy(value => value.Index).ThenByDescending(value => value.Length)
            .FirstOrDefault();
        // Leave enough space after the match starts for even a maximum-length search term.
        var beforeMatch = Math.Min(available / 3, Math.Max(0, available - match.Length));
        var start = Math.Clamp(match.Index - beforeMatch, 0, text.Length - available);
        if (start > 0 && char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1])) start++;
        var end = Math.Min(text.Length, start + available);
        if (end < text.Length && end > start && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end])) end--;
        return (start > 0 ? marker : "") + text[start..end] + (end < text.Length ? marker : "");
    }

    private static (string Query, int Offset, int Limit, int? Index, string? Field) Parse(JsonElement arguments)
    {
        static void Require(bool condition)
        {
            if (!condition) throw new ArgumentException("Invalid saved chat lookup arguments.", nameof(arguments));
        }
        Require(arguments.ValueKind == JsonValueKind.Object);
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
            Require(ReadArgumentFields.Contains(property.Name) && fields.Add(property.Name));
        Require(fields.SetEquals(ArgumentFields) || fields.SetEquals(ReadArgumentFields));
        var queryValue = arguments.GetProperty("query");
        Require(queryValue.ValueKind == JsonValueKind.String);
        var query = queryValue.GetString()!;
        Require(query.Length <= 200 && !query.Contains('\0'));
        var offsetValue = arguments.GetProperty("offset");
        Require(offsetValue.ValueKind == JsonValueKind.Number && offsetValue.TryGetInt32(out _));
        var offset = offsetValue.GetInt32();
        Require(offset >= 0);
        var limitValue = arguments.GetProperty("limit");
        Require(limitValue.ValueKind == JsonValueKind.Number && limitValue.TryGetInt32(out _));
        var limit = limitValue.GetInt32();
        Require(limit is >= 1 and <= 5);
        if (fields.SetEquals(ArgumentFields)) return (query, offset, limit, null, null);
        var indexValue = arguments.GetProperty("index");
        Require(indexValue.ValueKind == JsonValueKind.Number && indexValue.TryGetInt32(out _));
        var index = indexValue.GetInt32();
        var fieldValue = arguments.GetProperty("field");
        Require(fieldValue.ValueKind == JsonValueKind.String);
        var field = fieldValue.GetString();
        Require(index is >= 1 and <= ChatHistory.MaxStoredTurns && field is "answer" or "context_label" &&
            query.Length == 0 && limit == 1 && offset <= 65536);
        return (query, offset, limit, index, field);
    }
}
