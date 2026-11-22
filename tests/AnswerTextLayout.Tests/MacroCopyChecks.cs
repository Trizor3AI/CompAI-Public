using CompAI;

internal static class MacroCopyChecks
{
    internal static void Run()
    {
        static string Fence(string text, string tag = "") => "```" + tag + "\n" + text + "\n```";
        static string Commands(int count) => string.Join('\n', Enumerable.Range(1, count).Select(i => $"/echo {i}"));

        var fifteen = MacroCopy.Extract(Fence(Commands(15)));
        Check(fifteen.Count == 1 && fifteen[0] == new MacroCopyPart("Macro", Commands(15), 15),
            "15 commands fit one macro without rewriting");
        var sixteen = MacroCopy.Extract(Fence(Commands(16)));
        Check(sixteen.Count == 2 && sixteen[0] == new MacroCopyPart("Macro P1", Commands(15), 15)
            && sixteen[1] == new MacroCopyPart("Macro P2", "/echo 16", 1), "16 commands split at 15");
        var thirtyOne = MacroCopy.Extract(Fence(Commands(31)));
        Check(thirtyOne.Count == 3 && thirtyOne.Select(part => part.LineCount).SequenceEqual([15, 15, 1])
            && string.Join('\n', thirtyOne.Select(part => part.Text)) == Commands(31),
            "all split parts reconstruct the original line order");

        const string exact = " /ac \"Basic Synthesis\" <wait.3>  \n\n/micon \"Șț 中 😀 e\u0301\"\n/echo <t> <me> <se.1>";
        var preserved = MacroCopy.Extract("Explanation stays outside.\r\n" + Fence(exact, "text").Replace("\n", "\r\n")
            + "\r\nAfterwards, review it.");
        Check(preserved.Count == 1 && preserved[0].Text == exact && preserved[0].LineCount == 4,
            "quotes, spaces, blanks, waits, placeholders and Unicode are exact after CRLF normalization");
        var blankBoundary = MacroCopy.Extract(Fence(Commands(14) + "\n\n/echo last"));
        Check(blankBoundary.Count == 2 && blankBoundary[0].LineCount == 15
            && blankBoundary[0].Text.EndsWith('\n') && blankBoundary[1].Text == "/echo last",
            "blank lines count toward the game limit and remain in the copied text");
        Check(MacroCopy.Extract(Fence("\n/echo one\n"))[0].Text == "\n/echo one\n",
            "leading and trailing blank content lines remain intact");

        var explicitParts = MacroCopy.Extract("### Macro P1\n\n" + Fence("/echo first")
            + "\n**Part 2:**\n" + Fence("/echo second", "ffxiv"));
        Check(explicitParts.Select(part => part.Label).SequenceEqual(["Macro P1", "Macro P2"]),
            "adjacent numbered Markdown headings identify explicit parts");
        Check(MacroCopy.Extract("Part1:\n" + Fence("/echo first"))[0].Label == "Macro P1",
            "compact Part1 heading recognized");
        var alternatives = MacroCopy.Extract(Fence("/echo first") + "\nAlternative:\n" + Fence("/echo second"));
        Check(alternatives.Select(part => part.Label).SequenceEqual(["Macro 1", "Macro 2"]),
            "unlabeled independent blocks do not claim sequential parts");
        var staleHeading = MacroCopy.Extract("Macro P9\nSome explanation intervenes.\n" + Fence("/echo one"));
        Check(staleHeading[0].Label == "Macro", "nonadjacent headings do not label a block");
        Check(MacroCopy.Extract("**\n" + Fence("/echo one"))[0].Label == "Macro",
            "malformed heading markup is ordinary surrounding text");
        var splitNamed = MacroCopy.Extract("Macro P2\n" + Fence(Commands(16)) + "\nPart 3\n" + Fence("/echo end"));
        Check(splitNamed.Select(part => part.Label).SequenceEqual(["Macro P2 (1/2)", "Macro P2 (2/2)", "Macro P3"]),
            "splitting an explicitly numbered part does not steal another part number");
        var splitAlternative = MacroCopy.Extract(Fence(Commands(16)) + "\n" + Fence("/echo alternative"));
        Check(splitAlternative.Select(part => part.Label).SequenceEqual(["Macro 1 P1", "Macro 1 P2", "Macro 2"]),
            "split alternative keeps its block identity");

        foreach (var rejected in new[]
        {
            "", "/ac \"Basic Synthesis\" <wait.3>", "Ordinary prose mentioning /echo.",
            "```\n/echo unfinished", Fence(""), Fence("\n \n"),
            Fence("/echo first\nThen do this\n/echo last"), Fence("var value = 1;", "csharp"),
            Fence("/echo command", "unknown-language"), Fence("/echo command", "bash"),
            Fence("// comment"), Fence("/usr/bin/example"), Fence("/echo();"), Fence("https://example.org"),
            "```\n/echo one\n~~~", "````\n/echo one\n```", "```\n/echo one\n``` trailing prose",
            "    ```\n/echo indented fence\n    ```", "An inline ```\n/echo one\n```",
        })
            Check(MacroCopy.Extract(rejected).Count == 0, "reject prose, code or incomplete fences: " + rejected);

        Check(MacroCopy.Extract("~~~macro\n/echo one\n~~~~\n").Single().Text == "/echo one",
            "tilde fences and longer closing fences work");
        Check(MacroCopy.Extract("   ```plaintext\n/echo one\n   ```").Single().Text == "/echo one",
            "up to three spaces before a Markdown fence are supported");
        Check(MacroCopy.Extract(Fence("/echo completed") + "\n```\n/echo unfinished").Single().Text == "/echo completed",
            "unfinished later blocks do not hide earlier complete macros");
        Check(MacroCopy.Extract("````csharp\n```\n/echo inside code\n```\n````").Count == 0,
            "macro-shaped fences inside an unrelated code block remain code");
        Check(MacroCopy.Extract(Fence("/自分 対象", "TEXT")).Single().Text == "/自分 対象",
            "localized slash-command tokens remain intact");
        Console.WriteLine("PASS macro-copy extraction, exact text, 15-line parts, labels and rejected non-macros");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("Failed: " + name);
    }
}
