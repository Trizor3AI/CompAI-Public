using System.Numerics;
using CompAI;

internal static class TranscriptRenderChecks
{
    internal static void Run()
    {
        static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("Failed: " + name);
        }

        var key = new TranscriptLayoutKey(560, 17, 1, new(8, 7), new(9, 4), new(4, 4), 21);
        var message = new object();
        var measured = new TranscriptMessageLayout();
        Check(!measured.CanSkip(message, key, 1000, 0, 400), "unmeasured messages must draw");
        measured.Remember(message, key, 250);
        Check(measured.CanSkip(message, key, -300, 0, 400), "cached message above viewport skips");
        Check(measured.CanSkip(message, key, 401, 0, 400), "cached message below viewport skips");
        Check(!measured.CanSkip(message, key, -250, 0, 400), "viewport edge remains submitted");
        Check(!measured.CanSkip(message, key, -200, 0, 400), "partially visible message draws");
        Check(measured.PlaceholderHeight(key.ItemSpacing.Y) + key.ItemSpacing.Y == measured.Height,
            "placeholder has identical total cursor advance");
        Check(!measured.CanSkip(new object(), key, 1000, 0, 400), "replacement and shifted messages invalidate height");
        foreach (var changed in new[]
        {
            key with { Width = 400 }, key with { LineHeight = 20 }, key with { Font = 2 },
            key with { ItemSpacing = new(8, 9) }, key with { FramePadding = new(10, 6) },
            key with { ItemInnerSpacing = new(5, 4) }, key with { IndentSpacing = 24 },
        })
            Check(!measured.CanSkip(message, changed, 1000, 0, 400), "geometry change invalidates height");
        measured.Remember(message, key, 500);
        Check(!measured.CanSkip(message, key, -300, 0, 400), "expanded context changes visible message range");

        var answer = string.Join("\n", Enumerable.Range(0, 90).Select(i =>
            $"Line {i}: **Keep this qualifier**; consult the [reference guide](https://example.org/guide) before proceeding."));
        var layout = AnswerTextLayout.Build(AnswerLinks.Parse(answer), key.Width, key.LineHeight,
            (text, bold) => text.Length * 8f + (bold ? 0.75f : 0));
        foreach (var bounds in new[] { (-500f, -10f), (0f, 400f), (500f, 900f), (layout.Height, layout.Height + 400), (20f, 20f) })
        {
            var range = layout.VisibleFragments(bounds.Item1, bounds.Item2);
            var expected = layout.Fragments.Where(x => x.Y + x.Height >= bounds.Item1 && x.Y <= bounds.Item2).ToArray();
            Check(layout.Fragments.Skip(range.Start).Take(range.Count).SequenceEqual(expected),
                "fragment search preserves exact visible text, links, bold and order");
        }
        Check(layout.VisibleFragments(10, 9).Count == 0, "inverted viewport draws no fragments");
        Check(AnswerTextLayout.Build([], 100, 17, text => text.Length).VisibleFragments(0, 400).Count == 0,
            "empty answer range is empty");
        var resized = AnswerTextLayout.Build(AnswerLinks.Parse(answer), 280, 17, text => text.Length * 8f);
        Check(resized.Height > layout.Height, "width changes rebuild a taller wrapped answer");
        var streamed = AnswerTextLayout.Build(AnswerLinks.Parse(answer + "\nNew final qualifier."),
            key.Width, key.LineHeight, text => text.Length * 8f);
        Check(streamed.Height > layout.Height, "growing answer has a new full height");

        // Reproducible offline submission counts, not GPU timings. This fixture is
        // 600 completed turns: ordinary linked answers, then one unusually tall
        // final answer. The viewport is at the tail, as it is after completion.
        const int turns = 600;
        const float viewport = 520;
        const float headerAndControls = 105;
        var ordinary = AnswerTextLayout.Build(AnswerLinks.Parse(string.Join("\n", answer.Split('\n').Take(12))),
            key.Width, key.LineHeight, (text, bold) => text.Length * 8f + (bold ? 0.75f : 0));
        var totalHeight = (turns - 1) * (headerAndControls + ordinary.Height) + headerAndControls + layout.Height;
        var top = totalHeight - viewport;
        var drawnMessages = 0;
        var drawnFragments = 0;
        var cursor = 0f;
        for (var i = 0; i < turns; ++i)
        {
            var current = i == turns - 1 ? layout : ordinary;
            var rowHeight = headerAndControls + current.Height;
            var item = new object();
            var cached = new TranscriptMessageLayout();
            cached.Remember(item, key, rowHeight);
            if (cached.CanSkip(item, key, cursor, top, totalHeight))
                cursor += cached.PlaceholderHeight(key.ItemSpacing.Y) + key.ItemSpacing.Y;
            else
            {
                ++drawnMessages;
                var answerStart = cursor + headerAndControls;
                drawnFragments += current.VisibleFragments(top - answerStart, totalHeight - answerStart).Count;
                cursor += rowHeight;
            }
        }
        Check(cursor == totalHeight, "long-chat skip preserves content height and scrollbar extent");
        Check(drawnMessages == 1 && drawnFragments < layout.Fragments.Count / 3,
            "only the tail and visible answer lines submit drawing work");
        Console.WriteLine($"PASS long-chat cached submission fixture: {turns} -> {drawnMessages} messages; " +
            $"{(turns - 1) * ordinary.Fragments.Count + layout.Fragments.Count:N0} -> {drawnFragments:N0} answer fragments; same {totalHeight:N0}px extent.");
        Console.WriteLine("PASS transcript height invalidation, expanded context, fragment boundaries, links, resize and answer growth.");
    }
}
