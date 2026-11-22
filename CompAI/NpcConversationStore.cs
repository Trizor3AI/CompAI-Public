using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace CompAI;

internal sealed record NpcDialogueLine(string Speaker, string Text, DateTimeOffset ObservedAtUtc,
    bool FromPreviousArea);

/// <summary>A frozen observed Talk log, with no quest association or completeness claim.</summary>
internal sealed class NpcConversationSnapshot
{
    internal NpcConversationSnapshot(DateTimeOffset capturedAtUtc, int droppedLines,
        IEnumerable<NpcDialogueLine> lines)
    {
        this.CapturedAtUtc = capturedAtUtc;
        this.DroppedLines = droppedLines;
        this.Lines = Array.AsReadOnly(lines.ToArray());
    }

    internal DateTimeOffset CapturedAtUtc { get; }
    internal int DroppedLines { get; }
    internal IReadOnlyList<NpcDialogueLine> Lines { get; }
}

/// <summary>
/// Session-only, managed observations. The caller gates reads and clears on permission loss,
/// logout, reload, or a confirmed character change. Null means Talk closed, not read failure.
/// </summary>
internal sealed class NpcConversationStore
{
    internal const int MaxLines = 128;
    internal const int MaxCharacters = 32768;
    internal const int MaxSpeakerCharacters = 128;
    internal const int MaxTextCharacters = 2048;
    internal static readonly TimeSpan Retention = TimeSpan.FromMinutes(30);

    private readonly object gate = new();
    private readonly List<NpcDialogueLine> lines = new();
    private int activeIndex = -1;
    private int characters;
    private int droppedLines;
    private NpcDialogueContext? previousAreaLine;
    private DateTimeOffset? nextExpiry;
    private DateTimeOffset? lastExpiryCheck;
    private long expiryLinesInspected;

    // Bounded scalar instrumentation for offline work-count fixtures.
    internal long ExpiryLinesInspected { get { lock (this.gate) return this.expiryLinesInspected; } }

    internal void Observe(NpcDialogueContext? line, DateTimeOffset now)
    {
        if (line is not null && (line.Speaker is null || line.Text is null ||
            line.Speaker.Length > MaxSpeakerCharacters || line.Text.Length is < 1 or > MaxTextCharacters))
            throw new ArgumentException("NPC dialogue exceeds the observed Talk bounds.", nameof(line));

        lock (this.gate)
        {
            this.ExpireUnderLock(now);
            if (line is null)
            {
                this.activeIndex = -1;
                this.previousAreaLine = null;
                return;
            }

            // A still-visible old page (including its reveal animation) must not become
            // new-area dialogue or refresh a historical line's retention clock.
            if (this.previousAreaLine is { } previous && SameRevealedLine(previous.Speaker,
                    previous.Text, line.Speaker, line.Text))
                return;
            this.previousAreaLine = null;

            if (this.activeIndex >= 0)
            {
                var current = this.lines[this.activeIndex];
                if (SameRevealedLine(current.Speaker, current.Text, line.Speaker, line.Text))
                {
                    // Keep the longest observed prefix; a transient shorter frame must not
                    // erase text. Prefix matching cannot establish a real quest/page boundary.
                    var text = line.Text.Length > current.Text.Length ? line.Text : current.Text;
                    this.characters += text.Length - current.Text.Length;
                    this.lines[this.activeIndex] = current with { Text = text, ObservedAtUtc = now };
                    this.TrimUnderLock();
                    return;
                }
            }

            this.lines.Add(new(line.Speaker, line.Text, now, false));
            var expires = now + Retention;
            if (this.nextExpiry is null || expires < this.nextExpiry) this.nextExpiry = expires;
            this.characters += line.Speaker.Length + line.Text.Length;
            this.activeIndex = this.lines.Count - 1;
            this.TrimUnderLock();
        }
    }

    internal void Expire(DateTimeOffset now)
    {
        lock (this.gate) this.ExpireUnderLock(now);
    }

    internal void OnAreaChanged(DateTimeOffset now)
    {
        lock (this.gate)
        {
            this.ExpireUnderLock(now);
            if (this.activeIndex >= 0)
            {
                var current = this.lines[this.activeIndex];
                this.previousAreaLine = new(current.Speaker, current.Text);
            }
            this.activeIndex = -1;
            for (var index = 0; index < this.lines.Count; index++)
                this.lines[index] = this.lines[index] with { FromPreviousArea = true };
        }
    }

    internal NpcConversationSnapshot CaptureSnapshot(DateTimeOffset now)
    {
        lock (this.gate)
        {
            this.ExpireUnderLock(now);
            return new(now, this.droppedLines, this.lines);
        }
    }

    internal void Clear()
    {
        lock (this.gate)
        {
            this.lines.Clear();
            this.activeIndex = -1;
            this.characters = 0;
            this.droppedLines = 0;
            this.previousAreaLine = null;
            this.nextExpiry = null;
            this.lastExpiryCheck = null;
            this.expiryLinesInspected = 0;
        }
    }

    private static bool SameRevealedLine(string oldSpeaker, string oldText, string speaker, string text) =>
        string.Equals(oldSpeaker, speaker, StringComparison.Ordinal) &&
        (oldText.StartsWith(text, StringComparison.Ordinal) || text.StartsWith(oldText, StringComparison.Ordinal));

    private void ExpireUnderLock(DateTimeOffset now)
    {
        var backwards = this.lastExpiryCheck is { } previous && now < previous;
        this.lastExpiryCheck = now;
        if (!backwards && (this.nextExpiry is null || now < this.nextExpiry)) return;

        this.nextExpiry = null;
        for (var index = this.lines.Count - 1; index >= 0; index--)
        {
            if (this.expiryLinesInspected < long.MaxValue) this.expiryLinesInspected++;
            var observed = this.lines[index].ObservedAtUtc;
            if (now < observed || now - observed >= Retention) this.DropUnderLock(index);
            else
            {
                var expires = observed + Retention;
                if (this.nextExpiry is null || expires < this.nextExpiry) this.nextExpiry = expires;
            }
        }
    }

    private void TrimUnderLock()
    {
        while (this.lines.Count > MaxLines || this.characters > MaxCharacters) this.DropUnderLock(0);
    }

    private void DropUnderLock(int index)
    {
        this.characters -= this.lines[index].Speaker.Length + this.lines[index].Text.Length;
        this.lines.RemoveAt(index);
        if (this.activeIndex == index) this.activeIndex = -1;
        else if (this.activeIndex > index) this.activeIndex--;
        if (this.droppedLines < int.MaxValue) this.droppedLines++;
    }
}

internal sealed record NpcDialogueLookupSummary(string scope, DateTimeOffset captured_at_utc,
    int total_lines, int dropped_lines);
internal sealed record NpcDialogueLookupLine(string speaker, string text, DateTimeOffset observed_at_utc,
    bool from_previous_area);
internal sealed record NpcDialogueLookupResult(string scope, DateTimeOffset captured_at_utc,
    int total_lines, int dropped_lines, int offset, int? next_offset, NpcDialogueLookupLine[] lines);

/// <summary>Pages only the frozen Talk observations captured for one permitted answer.</summary>
internal sealed class NpcDialogueLookup
{
    internal const string Scope = "observed_npc_dialogue";
    internal const int MaxBytes = 20000;
    private readonly DateTimeOffset capturedAtUtc;
    private readonly int droppedLines;
    private readonly NpcDialogueLine[] lines;

    internal NpcDialogueLookup(NpcConversationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        this.capturedAtUtc = snapshot.CapturedAtUtc;
        this.droppedLines = snapshot.DroppedLines;
        this.lines = snapshot.Lines.ToArray();
    }

    internal NpcDialogueLookupSummary Summary =>
        new(Scope, this.capturedAtUtc, this.lines.Length, this.droppedLines);

    internal NpcDialogueLookupResult Query(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Count() != 2 ||
            !arguments.TryGetProperty("offset", out var offsetValue) ||
            !arguments.TryGetProperty("limit", out var limitValue) ||
            offsetValue.ValueKind != JsonValueKind.Number || !offsetValue.TryGetInt32(out var offset) || offset < 0 ||
            limitValue.ValueKind != JsonValueKind.Number || !limitValue.TryGetInt32(out var limit) || limit is < 1 or > 20)
            throw new InvalidOperationException("Expected an NPC dialogue offset and limit (1 to 20).");

        var count = offset >= this.lines.Length ? 0 : Math.Min(limit, this.lines.Length - offset);
        NpcDialogueLookupResult Build()
        {
            var selected = this.lines.Skip(offset).Take(count)
                .Select(line => new NpcDialogueLookupLine(line.Speaker, line.Text, line.ObservedAtUtc,
                    line.FromPreviousArea)).ToArray();
            return new(Scope, this.capturedAtUtc, this.lines.Length, this.droppedLines, offset,
                offset < this.lines.Length && count < this.lines.Length - offset ? offset + count : null, selected);
        }

        var result = Build();
        while (JsonSerializer.SerializeToUtf8Bytes(result).Length > MaxBytes)
        {
            // A bounded individual line fits even when every UTF-16 character is escaped.
            // Reduce the page, never the captured text, and preserve a progressing cursor.
            if (count <= 1) throw new InvalidOperationException("NPC dialogue exceeds the response budget.");
            count--;
            result = Build();
        }
        return result;
    }
}
