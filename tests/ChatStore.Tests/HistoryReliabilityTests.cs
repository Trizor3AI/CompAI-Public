using CompAI;
using System.Text;
using System.Text.Json;

internal static class HistoryReliabilityTests
{
    internal static void Run()
    {
        foreach (var (answerBody, contextBody) in new[]
        {
            (new string('a', 50000), "small context"),
            ("short answer", new string('ș', 30000)),
            (string.Concat(Enumerable.Repeat("🧭ș\n", 12000)), new string('ș', 30000)),
        })
        {
            var answer = "Answer opening qualifier. " + answerBody + " Answer final qualification.";
            var contextLabel = "Context opening caveat. " + contextBody + " Context final caveat.";
            var history = new ChatHistory();
            history.Add("Older", "Older complete answer", [], null, false);
            history.Add(new string('ș', 2000), answer, [], contextLabel, false);
            var saved = history.Export();
            var recent = history.Context();
            var stats = history.ContextStats;
            Check(recent.Length == 1 && recent[0].IsExcerpt && recent[0].Question == saved[^1].Question,
                "latest question remains complete and its answer is explicitly partial");
            Check(recent[0].Answer.Contains("Answer opening qualifier.") && recent[0].Answer.Contains("Answer final qualification.") &&
                  recent[0].Answer.Contains("Context opening caveat.") && recent[0].Answer.Contains("Context final caveat.") &&
                  recent[0].Answer.Contains("middle omitted") && recent[0].Answer.Contains("possible qualifiers"),
                "verbatim head and tail retain endpoint qualifiers and label missing text");
            Check(stats.Bytes == JsonSerializer.SerializeToUtf8Bytes(recent).Length && stats.Bytes <= ChatHistory.MaxContextBytes &&
                  stats.PartialCount == 1 && stats.OmittedCount == 1 && stats.TotalCount == 2,
                "actual escaped wire bytes and whole/partial omission counts are exact");
            Check(!recent[0].Answer.EnumerateRunes().Any(rune => rune.Value == 0xfffd) &&
                  history.Export()[^1].Answer == answer && history.Export()[^1].ContextLabel == contextLabel,
                "Unicode and saved complete text remain unchanged");
            var lookupTurns = saved.Take(saved.Length - recent.Count(pair => !pair.IsExcerpt)).ToArray();
            Check(lookupTurns.Length == 2, "partially supplied newest exchange stays in the permitted lookup prefix");
            foreach (var field in new[] { "answer", "context_label" })
            {
                var complete = field == "answer" ? answer : contextLabel;
                var readBack = new StringBuilder();
                var offset = 0;
                do
                {
                    var page = ChatHistoryLookup.Query(lookupTurns, ReadArguments(2, field, offset));
                    var content = page.content!;
                    Check(content.index == 2 && content.field == field && content.total_chars == complete.Length &&
                          page.offset == offset && page.rows.Length == 0 && content.text.Length > 0 &&
                          JsonSerializer.SerializeToUtf8Bytes(page).Length <= ChatHistoryLookup.MaxBytes,
                        "each direct page is scoped, bounded and makes progress");
                    readBack.Append(content.text);
                    if (page.next_offset is not { } next) break;
                    Check(next == readBack.Length, "direct-read cursor has no gaps or duplicated characters");
                    offset = next;
                } while (true);
                Check(readBack.ToString() == complete, "permitted consecutive pages reconstruct the full verbatim saved field");
            }
        }
        var small = new ChatHistory();
        small.Add("Question", "Answer", [], null, false);
        Check(!JsonSerializer.Serialize(small.Context()).Contains("is_excerpt"), "complete-pair wire shape remains unchanged");
        var turns = new[] { new ChatTurn("Q", "🧭done", [], null) };
        var empty = ChatHistoryLookup.Query(turns, ReadArguments(1, "context_label", 0));
        Check(empty.content!.text == "" && empty.content.total_chars == 0 && empty.next_offset is null,
            "missing context reads as an empty, terminal field");
        foreach (var arguments in new[] { ReadArguments(2, "answer", 0), ReadArguments(1, "answer", 1) })
        {
            try { ChatHistoryLookup.Query(turns, arguments); throw new Exception("invalid direct-read cursor accepted"); }
            catch (ArgumentException) { }
        }
    }

    internal static object ContractFixture()
    {
        var history = new ChatHistory();
        history.Add("Synthetic large reply", "Opening qualifier. " + string.Concat(Enumerable.Repeat("🧭ș\n", 12000)) +
            " Final qualification.", [], "Synthetic saved context.", false);
        var turns = history.Export();
        var pages = new List<object>();
        var offset = 0;
        do
        {
            var arguments = ReadArguments(1, "answer", offset);
            var result = ChatHistoryLookup.Query(turns, arguments);
            pages.Add(new { arguments, result });
            if (result.next_offset is not { } next) break;
            offset = next;
        } while (true);
        return new { history = history.Context(), summary = ChatHistoryLookup.CreateSummary(turns), pages };
    }

    private static JsonElement ReadArguments(int index, string field, int offset) =>
        JsonSerializer.SerializeToElement(new { query = "", offset, limit = 1, index, field });

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
