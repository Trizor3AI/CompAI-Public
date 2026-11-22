using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CompAI;

internal sealed record ChatTurn(string Question, string Answer, IReadOnlyList<QuestionSource> Sources,
    string? ContextLabel, bool IncludesInventory = false, string? PersonalityName = null)
{
    // A session-only handle into the bounded local finder. Never saved or sent to AI.
    [JsonIgnore] public Guid? InventoryFindKey { get; init; }
    [JsonIgnore] public Guid? ShopFindKey { get; init; }
}

internal sealed record ChatExchange(
    [property: JsonPropertyName("question")] string Question,
    [property: JsonPropertyName("answer")] string Answer,
    [property: JsonPropertyName("is_excerpt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IsExcerpt = false);

internal sealed record ChatContextStats(int IncludedCount, int TotalCount, int Bytes,
    int OmittedCount, string? OmissionReason, int PartialCount = 0);

// Owned by CompanionConnection under its gate. The chat store persists completed turns only.
internal sealed class ChatHistory
{
    internal const int MaxStoredTurns = 1000;
    internal const int MaxContextBytes = 24000;
    private const int MaxSourceTitleChars = 120;
    private const string ModelSourcePrefix = "Model-provided, unverified: ";
    private readonly List<ChatTurn> turns = new();
    private IReadOnlyList<ChatTurn>? cachedView;
    private ChatExchange[]? cachedContext;
    private ChatContextStats? cachedContextStats;
    internal int Revision { get; private set; }
    internal bool ContainsInventory { get; private set; }

    // Keep every retained turn readable. Context() prefers whole pairs, with a labelled
    // verbatim excerpt when the latest pair alone would otherwise leave no continuity.
    internal IReadOnlyList<ChatTurn> View => this.cachedView ??= Array.AsReadOnly(this.turns.ToArray());

    internal ChatContextStats ContextStats
    {
        get { this.EnsureContext(); return this.cachedContextStats!; }
    }

    internal ChatTurn[] Export() => this.turns.Select(Clone).ToArray();

    internal void Restore(IReadOnlyList<ChatTurn> saved)
    {
        if (saved.Count > MaxStoredTurns)
            throw new InvalidDataException("Chat has too many turns");
        var validated = new List<ChatTurn>(saved.Count);
        foreach (var turn in saved)
        {
            if (turn is null || string.IsNullOrWhiteSpace(turn.Question) || turn.Question.Length > 2000 ||
                string.IsNullOrWhiteSpace(turn.Answer) || turn.Answer.Length > 65536 ||
                turn.ContextLabel is { Length: > 65536 } || turn.Sources is null || turn.Sources.Count > 20 ||
                !ValidPersonalityName(turn.PersonalityName))
                throw new InvalidDataException("Invalid saved chat turn");
            var sources = turn.Sources.ToArray();
            for (var i = 0; i < sources.Length; i++)
            {
                var source = sources[i];
                if (string.IsNullOrWhiteSpace(source.Title) ||
                    string.IsNullOrWhiteSpace(source.Url) || !AnswerLinks.TryCanonicalize(source.Url, out var uri) ||
                    source.IsBundled && (!uri.IsDefaultPort || uri.Host is not
                        ("na.finalfantasyxiv.com" or "ffxiv.consolegameswiki.com")))
                    throw new InvalidDataException("Invalid saved source link");
                if (source.Title.Length > MaxSourceTitleChars)
                {
                    // Earlier versions stored the complete host in this generated label.
                    if (source.IsBundled || source.Title != ModelSourcePrefix + uri.Host)
                        throw new InvalidDataException("Invalid saved source title");
                    sources[i] = source with { Title = ModelSourceTitle(uri) };
                }
            }
            validated.Add(turn with { Sources = sources });
        }
        this.turns.Clear();
        this.turns.AddRange(validated);
        this.cachedView = null;
        this.cachedContext = null;
        this.ContainsInventory = validated.Any(x => x.IncludesInventory);
        ++this.Revision;
    }

    private static ChatTurn Clone(ChatTurn turn) => turn with { Sources = turn.Sources.ToArray() };

    internal static string ModelSourceTitle(Uri uri)
    {
        var title = ModelSourcePrefix + uri.Host;
        return title.Length <= MaxSourceTitleChars ? title : "Model-provided, unverified link";
    }

    private static bool ValidPersonalityName(string? name) => name is null ||
        !string.IsNullOrWhiteSpace(name) && name.Length <= PersonalityPresets.MaxNameChars && !name.Any(char.IsControl);

    internal void Add(string question, string answer, IReadOnlyList<QuestionSource> sources,
        string? contextLabel, bool includesInventory, string? personalityName = null, Guid? inventoryFindKey = null,
        Guid? shopFindKey = null)
    {
        if (!ValidPersonalityName(personalityName)) throw new ArgumentException("Invalid personality name", nameof(personalityName));
        this.turns.Add(new ChatTurn(question, answer, sources.ToArray(), contextLabel, includesInventory, personalityName)
            { InventoryFindKey = inventoryFindKey, ShopFindKey = shopFindKey });
        this.cachedView = null;
        this.cachedContext = null;
        // Sending is stopped at the storage limit by the caller. Never discard an old
        // exchange to make room for an already completed answer awaiting preservation.
        this.ContainsInventory |= includesInventory;
        ++this.Revision;
    }

    internal ChatExchange[] Context()
    {
        this.EnsureContext();
        return this.cachedContext!.ToArray();
    }

    private void EnsureContext()
    {
        if (this.cachedContext is not null) return;
        var result = new List<ChatExchange>();
        var bytes = 2; // JSON array brackets; each additional entry adds its separator.
        for (var i = this.turns.Count - 1; i >= 0; i--)
        {
            var turn = this.turns[i];
            var answer = turn.Answer + (string.IsNullOrEmpty(turn.ContextLabel) ? "" : "\n\n" + turn.ContextLabel);
            // Keep consecutive whole pairs whenever they fit.
            var exchange = new ChatExchange(turn.Question, answer);
            var addedBytes = JsonSerializer.SerializeToUtf8Bytes(exchange).Length + (result.Count == 0 ? 0 : 1);
            if (bytes + addedBytes > MaxContextBytes)
            {
                if (result.Count == 0)
                {
                    exchange = LatestExcerpt(turn);
                    result.Add(exchange);
                    bytes += JsonSerializer.SerializeToUtf8Bytes(exchange).Length;
                }
                break;
            }
            bytes += addedBytes;
            result.Add(exchange);
        }
        result.Reverse();
        this.cachedContext = result.ToArray();
        var omitted = this.turns.Count - result.Count;
        var partial = result.Count(x => x.IsExcerpt);
        this.cachedContextStats = new(result.Count, this.turns.Count, bytes, omitted,
            partial > 0 ? "latest_exchange_excerpt" : omitted == 0 ? null : "history_byte_budget", partial);
    }

    private static ChatExchange LatestExcerpt(ChatTurn turn)
    {
        const string label = "[Partial latest exchange: verbatim head/tail excerpts. Omitted text, including possible qualifiers, is unknown. The full saved exchange requires permitted older-message lookup.]\n\n";
        var context = string.IsNullOrEmpty(turn.ContextLabel) ? "" : "\n\nSaved context:\n";
        var overhead = JsonSerializer.SerializeToUtf8Bytes(new ChatExchange(turn.Question, label + context, true)).Length + 2;
        var available = MaxContextBytes - overhead;
        // The complete question is retained. Reserve up to a quarter of the remaining
        // bytes for historical context qualifiers; spend the rest on the answer itself.
        var contextBudget = context.Length == 0 ? 0 : Math.Min(4000, available / 4);
        var contextText = context.Length == 0 ? "" : FitExcerpt(turn.ContextLabel!, contextBudget);
        var answerBudget = available - EscapedBytes(contextText);
        var answer = FitExcerpt(turn.Answer, answerBudget);
        return new(turn.Question, label + answer + context + contextText, true);
    }

    private static int EscapedBytes(string text) => JsonSerializer.SerializeToUtf8Bytes(text).Length - 2;

    private static string FitExcerpt(string text, int byteBudget)
    {
        if (EscapedBytes(text) <= byteBudget) return text;
        const string omission = "\n[... middle omitted; not a complete statement ...]\n";
        string Slice(int chars)
        {
            var head = (chars + 1) / 2;
            var tail = text.Length - chars / 2;
            if (head > 0 && head < text.Length && char.IsHighSurrogate(text[head - 1]) && char.IsLowSurrogate(text[head])) head--;
            if (tail > 0 && tail < text.Length && char.IsHighSurrogate(text[tail - 1]) && char.IsLowSurrogate(text[tail])) tail++;
            return text[..head] + omission + text[tail..];
        }
        var low = 0;
        var high = text.Length - 1;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            if (EscapedBytes(Slice(middle)) <= byteBudget) low = middle;
            else high = middle - 1;
        }
        return Slice(low);
    }

    internal bool ContainsSource(string url) => this.turns.Any(t => t.Sources.Any(s => s.Url == url) ||
        AnswerLinks.Extract(t.Question).Any(link => link.AbsoluteUri == url));

    internal void Clear()
    {
        this.turns.Clear();
        this.ContainsInventory = false;
        this.cachedView = null;
        this.cachedContext = null;
        ++this.Revision;
    }
}
