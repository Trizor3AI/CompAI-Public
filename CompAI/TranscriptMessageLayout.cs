using System;
using System.Numerics;

namespace CompAI;

// All geometry that affects a completed exchange's measured cursor advance.
internal readonly record struct TranscriptLayoutKey(float Width, float LineHeight, int Font,
    Vector2 ItemSpacing, Vector2 FramePadding, Vector2 ItemInnerSpacing, float IndentSpacing);

// Retained turns are immutable. Changing a turn (including its sources or context)
// replaces that object; a retained-history shift therefore invalidates the old index.
internal sealed class TranscriptMessageLayout
{
    private object? content;
    private TranscriptLayoutKey key;
    internal float Height { get; private set; }

    internal bool Matches(object content, TranscriptLayoutKey key) =>
        ReferenceEquals(this.content, content) && this.key == key;

    internal void Remember(object content, TranscriptLayoutKey key, float height)
    {
        this.content = content;
        this.key = key;
        this.Height = Math.Max(0, height);
    }

    internal bool CanSkip(object content, TranscriptLayoutKey key, float start, float top, float bottom) =>
        this.Matches(content, key) && (start + this.Height < top || start > bottom);

    // Dummy adds ItemSpacing.Y itself. Keep its final cursor exactly where drawing
    // the exchange left it, including the trailing separator and its spacing.
    internal float PlaceholderHeight(float itemSpacing) => Math.Max(0, this.Height - itemSpacing);
}
