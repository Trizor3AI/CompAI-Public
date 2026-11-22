using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace CompAI;

internal sealed record QuestLookupSummary(string captured_at_utc, bool stale, string scope,
    int catalog_rows, int observed_quest_count, int active_count, int observed_completed_count);

internal sealed record QuestLookupResult(string captured_at_utc, bool stale, string scope,
    int catalog_rows, int observed_quest_count, int active_count, int observed_completed_count,
    int total_matches, int offset, int? next_offset, uint[] missing_ids, QuestChatRow[] rows);

/// <summary>
/// Queries an immutable managed snapshot frozen at Send. This class performs no native reads, I/O,
/// persistence or game actions. The caller owns access revocation and request/session lifetime.
/// </summary>
internal static class QuestLookup
{
    internal const int MaxBytes = 20000;
    internal const string Scope = "ordinary_quests_read_only_lookup";
    private static readonly HashSet<string> ArgumentFields = new(StringComparer.Ordinal)
        { "view", "query", "ids", "offset", "limit" };

    internal static QuestLookupSummary CreateSummary(QuestSnapshot snapshot, bool stale) => new(
        snapshot.CapturedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), stale, Scope,
        snapshot.CatalogRows, snapshot.Quests.Length, snapshot.Quests.Count(q => q.Accepted),
        snapshot.Quests.Count(q => q.Completed is true));

    internal static QuestLookupResult Query(QuestSnapshot snapshot, bool stale, JsonElement arguments)
    {
        var request = Parse(arguments);
        var summary = CreateSummary(snapshot, stale);
        var knownIds = new HashSet<uint>();
        foreach (var quest in snapshot.Quests)
        {
            knownIds.Add(quest.NativeId);
            if (quest.RowId is { } rowId) knownIds.Add(rowId);
        }
        var missing = request.Ids.Where(id => !knownIds.Contains(id)).ToArray();
        var ids = request.Ids.ToHashSet();
        var terms = request.Query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = snapshot.Quests.Where(q => request.View switch
            {
                "active" => q.Accepted,
                "completed" => q.Completed is true,
                _ => true,
            })
            .Where(q => ids.Count == 0 || ids.Contains(q.NativeId) || q.RowId is { } rowId && ids.Contains(rowId))
            .Where(q => terms.Length == 0 || q.Name is { } name &&
                terms.All(term => name.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(q => q.NativeId).ThenBy(q => q.RowId)
            .ToArray();
        var selected = matches.Skip(request.Offset).Take(request.Limit).Select(QuestChatContext.ToRow).ToList();
        QuestLookupResult Build() => new(summary.captured_at_utc, summary.stale, summary.scope,
            summary.catalog_rows, summary.observed_quest_count, summary.active_count, summary.observed_completed_count,
            matches.Length, request.Offset,
            // An offset past the end returns an empty final page. Otherwise progress is exactly the rows returned.
            request.Offset < matches.Length && selected.Count < matches.Length - request.Offset
                ? request.Offset + selected.Count : null,
            missing, selected.ToArray());
        var result = Build();
        if (JsonSerializer.SerializeToUtf8Bytes(result).Length > MaxBytes)
        {
            selected = selected.Select(q => q.journal_text is null ? q :
                q with { journal_text = null, journal_status = "omitted" }).ToList();
            result = Build();
        }
        while (JsonSerializer.SerializeToUtf8Bytes(result).Length > MaxBytes && selected.Count > 0)
        {
            selected.RemoveAt(selected.Count - 1);
            result = Build();
        }
        // Names and paragraph lengths are bounded before serialization, so a single row always fits.
        if (request.Offset < matches.Length && selected.Count == 0)
            throw new InvalidOperationException("A quest lookup row exceeded its response budget.");
        return result;
    }

    private sealed record LookupRequest(string View, string Query, uint[] Ids, int Offset, int Limit);

    private static LookupRequest Parse(JsonElement arguments)
    {
        static void Require(bool condition)
        {
            if (!condition) throw new ArgumentException("Invalid saved quest lookup arguments.", nameof(arguments));
        }
        Require(arguments.ValueKind == JsonValueKind.Object);
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
            Require(ArgumentFields.Contains(property.Name) && fields.Add(property.Name));
        Require(fields.SetEquals(ArgumentFields));

        var viewElement = arguments.GetProperty("view");
        Require(viewElement.ValueKind == JsonValueKind.String);
        var view = viewElement.GetString()!;
        Require(view is "active" or "completed" or "all");
        var queryElement = arguments.GetProperty("query");
        Require(queryElement.ValueKind == JsonValueKind.String);
        var query = queryElement.GetString()!;
        Require(query.Length <= 200 && !query.Contains('\0'));
        var offsetElement = arguments.GetProperty("offset");
        Require(offsetElement.ValueKind == JsonValueKind.Number && offsetElement.TryGetInt32(out _));
        var offset = offsetElement.GetInt32();
        Require(offset >= 0);
        var limitElement = arguments.GetProperty("limit");
        Require(limitElement.ValueKind == JsonValueKind.Number && limitElement.TryGetInt32(out _));
        var limit = limitElement.GetInt32();
        Require(limit is >= 1 and <= 50);
        var idsElement = arguments.GetProperty("ids");
        Require(idsElement.ValueKind == JsonValueKind.Array && idsElement.GetArrayLength() <= 50);
        var ids = new List<uint>();
        var nativeIds = new HashSet<uint>();
        foreach (var value in idsElement.EnumerateArray())
        {
            Require(value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out _));
            var id = value.GetUInt32();
            Require(id is >= 1 and <= 65535 or >= 65537 and <= 131071);
            // A native ID and its catalog-row alias must not request the same quest twice.
            Require(nativeIds.Add(id <= 65535 ? id : id - 65536));
            ids.Add(id);
        }
        Require(ids.Count == 0 || string.IsNullOrWhiteSpace(query));
        return new(view, query, ids.ToArray(), offset, limit);
    }
}
