using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CompAI;

// Wire names deliberately match the strict Python validator. No character ID or raw variables leave the plugin.
internal sealed record QuestChatAttachment(string captured_at_utc, bool stale, string scope,
    int catalog_rows, int observed_quest_count, int active_count, int observed_completed_count,
    int matched_count, int active_omitted, int matched_omitted, QuestChatRow[] rows);

internal sealed record QuestChatRow(ushort native_id, uint? row_id, string? name, bool name_truncated,
    bool accepted, bool? completed, byte? sequence, string? journal_text, string journal_status);

internal static class QuestChatContext
{
    internal const int MaxBytes = 20000;
    internal const string Scope = "ordinary_quests_active_and_question_matches";
    private static readonly Regex Tokens = new(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "after", "also", "been", "before", "complete", "completed", "done", "from", "have",
        "help", "into", "just", "know", "more", "need", "quest", "quests", "show", "stage", "tell",
        "that", "them", "there", "these", "they", "this", "what", "when", "where", "which", "with", "would",
    };

    internal static QuestChatAttachment Create(QuestSnapshot snapshot, bool stale, string question)
    {
        var all = snapshot.Quests;
        var terms = Tokens.Matches(question).Select(match => match.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var active = all.Where(q => q.Accepted).OrderBy(q => q.NativeId).Take(30).Select(ToRow).ToList();
        var matches = all.Where(q => !q.Accepted).Select(q => (Quest: q, Score: MatchScore(q, question, terms)))
            .Where(pair => pair.Score > 0).OrderByDescending(pair => pair.Score).ThenBy(pair => pair.Quest.NativeId).ToArray();
        var selected = active.Concat(matches.Take(20).Select(pair => ToRow(pair.Quest))).ToList();
        var activeCount = all.Count(q => q.Accepted);
        var completedCount = all.Count(q => q.Completed is true);
        QuestChatAttachment Build() => new(snapshot.CapturedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            stale, Scope, snapshot.CatalogRows, all.Length, activeCount, completedCount,
            matches.Length, activeCount - selected.Count(q => q.accepted),
            matches.Length - selected.Count(q => !q.accepted), selected.ToArray());
        var result = Build();
        if (JsonSerializer.SerializeToUtf8Bytes(result).Length > MaxBytes)
        {
            // Journal paragraphs are optional; preserve quest identities/status before extra prose.
            selected = selected.Select(q => q.journal_text is null ? q :
                q with { journal_text = null, journal_status = "omitted" }).ToList();
            result = Build();
        }
        while (JsonSerializer.SerializeToUtf8Bytes(result).Length > MaxBytes && selected.Count > 0)
        {
            selected.RemoveAt(selected.Count - 1);
            result = Build();
        }
        return result;
    }

    private static int MatchScore(QuestObservation quest, string question, HashSet<string> terms)
    {
        if (terms.Contains(quest.NativeId.ToString(CultureInfo.InvariantCulture)) ||
            quest.RowId is { } rowId && terms.Contains(rowId.ToString(CultureInfo.InvariantCulture)))
            return 100;
        if (string.IsNullOrWhiteSpace(quest.Name)) return 0;
        if (question.Contains(quest.Name, StringComparison.OrdinalIgnoreCase)) return 100;
        return Tokens.Matches(quest.Name).Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase)
            .Count(term => term.Length >= 4 && !StopWords.Contains(term) && terms.Contains(term));
    }

    internal static QuestChatRow ToRow(QuestObservation quest)
    {
        var name = Clean(quest.Name);
        var truncated = name?.Length > 200;
        if (truncated)
        {
            var length = char.IsHighSurrogate(name![199]) ? 199 : 200;
            name = name[..length];
        }
        var journal = quest.Accepted ? Clean(quest.JournalText) : null;
        var journalStatus = journal is null ? "unavailable" : journal.Length > 512 ? "omitted" : "included";
        return new(quest.NativeId, quest.RowId, name, truncated, quest.Accepted, quest.Completed,
            quest.Sequence, journalStatus == "included" ? journal : null, journalStatus);
    }

    private static string? Clean(string? text)
    {
        var clean = text?.Replace("\0", "").Trim();
        return string.IsNullOrWhiteSpace(clean) ? null : clean;
    }
}
