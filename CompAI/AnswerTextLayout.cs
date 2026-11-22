using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CompAI;

internal readonly record struct AnswerTextFragment(
    string Text, string? Url, float X, float Y, float Width, float Height, bool Bold = false);

internal readonly record struct AnswerFragmentRange(int Start, int End)
{
    internal int Count => this.End - this.Start;
}

internal sealed record AnswerTextLayoutResult(
    IReadOnlyList<AnswerTextFragment> Fragments, float Height)
{
    // Build emits fragments in line order with one common line height. Search both
    // bounds so a tall answer costs logarithmic lookup plus its visible fragments.
    internal AnswerFragmentRange VisibleFragments(float top, float bottom)
    {
        if (bottom < top) return new(0, 0);
        var low = 0;
        var high = this.Fragments.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            var fragment = this.Fragments[middle];
            if (fragment.Y + fragment.Height < top) low = middle + 1;
            else high = middle;
        }
        var first = low;
        high = this.Fragments.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (this.Fragments[middle].Y <= bottom) low = middle + 1;
            else high = middle;
        }
        return new(first, low);
    }
}

/// <summary>Pure, measured line layout for text and inline links.</summary>
internal static class AnswerTextLayout
{
    private readonly record struct Element(string Text, string? Url, bool Bold);

    internal static AnswerTextLayoutResult Build(
        IReadOnlyList<AnswerTextRun> runs, float width, float lineHeight, Func<string, float> measure)
        => Build(runs, width, lineHeight, (text, _) => measure(text));

    internal static AnswerTextLayoutResult Build(
        IReadOnlyList<AnswerTextRun> runs, float width, float lineHeight, Func<string, bool, float> measure)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(measure);
        if (!float.IsFinite(width) || width < 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (!float.IsFinite(lineHeight) || lineHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(lineHeight));

        var fragments = new List<AnswerTextFragment>();
        var word = new List<Element>();
        var whitespace = new List<Element>();
        var x = 0f;
        var y = 0f;
        var hasText = false;
        var afterCarriageReturn = false;

        float Measure(string text, bool bold)
        {
            var result = measure(text, bold);
            if (!float.IsFinite(result) || result < 0)
                throw new ArgumentException("Text measurement must be finite and nonnegative", nameof(measure));
            return result;
        }

        void NextLine()
        {
            x = 0;
            y += lineHeight;
        }

        void Emit(string text, string? url, bool bold)
        {
            if (text.Length == 0) return;
            var advance = Measure(text, bold);
            fragments.Add(new AnswerTextFragment(text, url, x, y, advance, lineHeight, bold));
            x += advance;
        }

        // Consecutive elements with the same link target render as one measured fragment.
        static IEnumerable<Element> Groups(List<Element> elements)
        {
            if (elements.Count == 0) yield break;
            var builder = new StringBuilder();
            var url = elements[0].Url;
            var bold = elements[0].Bold;
            foreach (var element in elements)
            {
                if (element.Url != url || element.Bold != bold)
                {
                    yield return new Element(builder.ToString(), url, bold);
                    builder.Clear();
                    url = element.Url;
                    bold = element.Bold;
                }
                builder.Append(element.Text);
            }
            yield return new Element(builder.ToString(), url, bold);
        }

        float GroupWidth(List<Element> elements)
        {
            var total = 0f;
            foreach (var group in Groups(elements)) total += Measure(group.Text, group.Bold);
            return total;
        }

        void EmitGroups(List<Element> elements)
        {
            foreach (var group in Groups(elements)) Emit(group.Text, group.Url, group.Bold);
            elements.Clear();
        }

        void EmitSplitWord()
        {
            var chunk = new StringBuilder();
            string? chunkUrl = null;
            var chunkBold = false;

            void FlushChunk()
            {
                if (chunk.Length == 0) return;
                Emit(chunk.ToString(), chunkUrl, chunkBold);
                chunk.Clear();
            }

            foreach (var element in word)
            {
                if (chunk.Length > 0 && (element.Url != chunkUrl || element.Bold != chunkBold))
                    FlushChunk();
                if (chunk.Length == 0)
                {
                    chunkUrl = element.Url;
                    chunkBold = element.Bold;
                }

                var candidate = chunk.ToString() + element.Text;
                if (x + Measure(candidate, chunkBold) > width && (x > 0 || chunk.Length > 0))
                {
                    FlushChunk();
                    if (x > 0) NextLine();
                    chunkUrl = element.Url;
                    chunkBold = element.Bold;
                }
                // A single text element wider than the available width still occupies one line.
                chunk.Append(element.Text);
            }
            FlushChunk();
            word.Clear();
        }

        void FlushWord()
        {
            if (word.Count == 0) return;
            var wordWidth = GroupWidth(word);
            var spaceWidth = GroupWidth(whitespace);
            if (x > 0 && x + spaceWidth + wordWidth > width)
            {
                // Only whitespace at a soft wrap is discarded.
                whitespace.Clear();
                NextLine();
            }
            else
            {
                EmitGroups(whitespace);
            }

            if (x + wordWidth <= width)
                EmitGroups(word);
            else
                EmitSplitWord();
        }

        foreach (var run in runs)
        {
            var text = run.Text ?? string.Empty;
            var enumerator = StringInfo.GetTextElementEnumerator(text);
            while (enumerator.MoveNext())
            {
                var element = enumerator.GetTextElement();
                if (afterCarriageReturn)
                {
                    afterCarriageReturn = false;
                    if (element == "\n") continue;
                }
                hasText = true;
                if (element is "\r" or "\n" or "\r\n" or "\u2028" or "\u2029")
                {
                    FlushWord();
                    EmitGroups(whitespace);
                    NextLine();
                    afterCarriageReturn = element == "\r";
                }
                else if (char.IsWhiteSpace(element, 0) && element is not ("\u00a0" or "\u2007" or "\u202f"))
                {
                    FlushWord();
                    whitespace.Add(new Element(element, run.Url, run.Bold));
                }
                else
                {
                    word.Add(new Element(element, run.Url, run.Bold));
                }
            }
        }
        FlushWord();
        EmitGroups(whitespace);
        return new AnswerTextLayoutResult(fragments, hasText ? y + lineHeight : 0);
    }
}
