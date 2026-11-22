using System.Globalization;
using CompAI;

MacroCopyChecks.Run();
if (args.Contains("--macro")) return;
ChatInputChecks.Run();
if (args.Contains("--input")) return;
ChatScrollChecks.Run();
if (args.Contains("--scroll")) return;
TranscriptRenderChecks.Run();

static float Measure(string text) => new StringInfo(text).LengthInTextElements;

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("Failed: " + name);
}

static string Visible(AnswerTextLayoutResult layout) =>
    string.Concat(layout.Fragments.Select(fragment => fragment.Text));

const string link = "https://example.org/a";
var inline = AnswerTextLayout.Build([
    new("See ", null), new(link, link), new(" for details.", null),
], 100, 10, Measure);
Check(Visible(inline) == "See " + link + " for details.", "inline content preserved");
var linked = inline.Fragments.Where(fragment => fragment.Url is not null).ToArray();
Check(linked.Length == 1 && linked[0].Text == link && linked[0].Url == link, "link target retained");
var afterLink = inline.Fragments.First(fragment => fragment.Text == "for");
Check(afterLink.Y == linked[0].Y && afterLink.X > linked[0].X + linked[0].Width,
    "following text continues after inline link");

var wrapped = AnswerTextLayout.Build([new("Alpha beta", null)], 6, 10, Measure);
Check(wrapped.Fragments.Any(fragment => fragment.Text == "Alpha" && fragment.Y == 0), "first word on first line");
Check(wrapped.Fragments.Any(fragment => fragment.Text == "beta" && fragment.X == 0 && fragment.Y == 10),
    "word wraps without leading space");
Check(Visible(wrapped) == "Alphabeta", "only soft-wrap whitespace discarded");

var longLink = AnswerTextLayout.Build([new(link, link)], 5, 8, Measure);
Check(Visible(longLink) == link && longLink.Fragments.Count > 1, "long URL preserved and split");
Check(longLink.Fragments.All(fragment => fragment.Url == link && fragment.Width <= 5 && fragment.Text.Length > 0),
    "split URL remains clickable and within width");
Check(longLink.Height > 8, "long URL advances lines");

var blank = AnswerTextLayout.Build([new("A\n\nB\n", null)], 20, 10, Measure);
Check(blank.Fragments.Single(fragment => fragment.Text == "B").Y == 20, "explicit blank line preserved");
Check(blank.Height == 40, "trailing explicit newline consumes a line");
var crlf = AnswerTextLayout.Build([new("A\r", null), new("\nB", null)], 20, 10, Measure);
Check(crlf.Fragments.Single(fragment => fragment.Text == "B").Y == 10, "cross-run CRLF is one newline");

var unicode = AnswerTextLayout.Build([new("A😀e\u0301中", null)], 2, 10, Measure);
Check(Visible(unicode) == "A😀e\u0301中", "Unicode content preserved");
Check(unicode.Fragments.All(fragment =>
    !char.IsLowSurrogate(fragment.Text[0]) && !char.IsHighSurrogate(fragment.Text[^1]) &&
    fragment.Text[0] != '\u0301'), "Unicode text elements are not split");
Check(unicode.Fragments.Any(fragment => fragment.Text.Contains("e\u0301")), "combining sequence stays together");

var empty = AnswerTextLayout.Build([new("", null)], 10, 10, Measure);
Check(empty.Fragments.Count == 0 && empty.Height == 0, "empty text has no layout height");

var tiny = AnswerTextLayout.Build([new("ABC", null)], 0, 7, Measure);
Check(Visible(tiny) == "ABC" && tiny.Fragments.Count == 3 && tiny.Height == 21,
    "zero width makes progress one glyph per line");

var linkLabel = AnswerTextLayout.Build([new("Inventory Guide", link)], 9, 10, Measure);
Check(linkLabel.Fragments.Any(fragment => fragment.Text == "Guide" && fragment.Y == 10),
    "link label wraps at word boundary");
Check(linkLabel.Fragments.All(fragment => fragment.Url == link), "wrapped label keeps target");

var screenshotAnswer = "Phantom β: [Phantom Dispeller β](https://example.org/item) is listed.";
var screenshotRuns = AnswerLinks.Parse(screenshotAnswer);
var screenshotLayout = AnswerTextLayout.Build(screenshotRuns, 22, 10, Measure);
var screenshotLinks = screenshotLayout.Fragments.Where(fragment => fragment.Url is not null).ToArray();
Check(screenshotLinks.Length >= 2 && screenshotLinks.All(fragment => fragment.Url == "https://example.org/item"),
    "Markdown label stays clickable after wrapping");
Check(screenshotLinks.Any(fragment => fragment.Text == "β"), "Greek beta remains in linked label");
Check(screenshotLayout.Fragments.All(fragment => !fragment.Text.Contains("](https://")),
    "Markdown syntax is not rendered as answer prose");
var beta = screenshotLinks.Single(fragment => fragment.Text == "β");
var trailing = screenshotLayout.Fragments.First(fragment => fragment.Text == "is");
Check(trailing.Y == beta.Y && trailing.X > beta.X + beta.Width,
    "text after wrapped Markdown link continues inline");

var boldText = AnswerTextLayout.Build([new("AB", null), new("CDEF", link, true), new("G", null)],
    5, 10, (text, bold) => Measure(text) + (bold ? 1 : 0));
Check(Visible(boldText) == "ABCDEFG", "bold boundaries preserve text");
Check(boldText.Fragments.Where(fragment => fragment.Bold).All(fragment => fragment.Url == link && fragment.Width <= 5),
    "bold wrapped fragments preserve links and measured stroke width");
Check(boldText.Fragments.Any(fragment => fragment.Bold) && boldText.Fragments.Any(fragment => !fragment.Bold),
    "bold style stays within its span");

Console.WriteLine("Answer text layout offline regressions passed.");
