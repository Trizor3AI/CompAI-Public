using CompAI;

static void Check(bool condition, string caseName)
{
    if (!condition) throw new Exception("Failed: " + caseName);
}

static string Display(IReadOnlyList<AnswerTextRun> runs) => string.Concat(runs.Select(run => run.Text));

var labeledAnswer = "Here are the sources:\n" +
    "[Item page](https://na.finalfantasyxiv.com/lodestone/playguide/db/item/47bb4cee92e/)\n" +
    "[Acquisition reference](https://ffxiv.consolegameswiki.com/wiki/Phantom_Dispeller_%CE%B2)\n" +
    "[Quest journal](https://ffxiv.consolegameswiki.com/wiki/Phantoms_to_Fillet) — check the date.";
var labeledRuns = AnswerLinks.Parse(labeledAnswer);
Check(labeledRuns.Count(run => run.Url is not null) == 3, "Screenshot labels become three links");
Check(Display(labeledRuns) == "Here are the sources:\nItem page\nAcquisition reference\nQuest journal — check the date.",
    "Markdown labels display without syntax; prose and newlines preserved");
Check(labeledRuns.Where(run => run.Url is not null).Select(run => run.Text)
    .SequenceEqual(new[] { "Item page", "Acquisition reference", "Quest journal" }), "Label text preserved");

var unicode = AnswerLinks.Parse("Before β — [猫](https://example.org/cat) after\nNext line.");
Check(Display(unicode) == "Before β — 猫 after\nNext line.", "Unicode and plain text preserved");

var boldCount = AnswerLinks.Parse("You have **60 Phantom Dispeller β** across carried bags.");
Check(Display(boldCount) == "You have 60 Phantom Dispeller β across carried bags.",
    "Greek count bold markers removed");
Check(boldCount.Count(run => run.Bold) == 1 && boldCount.Single(run => run.Bold).Text == "60 Phantom Dispeller β",
    "Greek count marked bold");

var boldLinks = AnswerLinks.Parse("**[Item page](https://example.org/item)** and " +
    "[**Quest journal**](https://example.org/quest)");
Check(Display(boldLinks) == "Item page and Quest journal", "Bold link labels display without markers");
Check(boldLinks.Count(run => run.Bold && run.Url is not null) == 2,
    "Both outer and inner bold link labels retain URL and style");

foreach (var literal in new[] { "Unmatched **marker", "Empty ****", "Blank ** **" })
{
    var runs = AnswerLinks.Parse(literal);
    Check(Display(runs) == literal && runs.All(run => !run.Bold), "Unmatched or empty bold markers literal");
}
var urlStars = AnswerLinks.Parse("[link](https://example.org/a**b) and https://example.org/c**d");
Check(Display(urlStars) == "link and https://example.org/c**d",
    "Asterisks inside Markdown destination and bare URL untouched");
Check(urlStars.All(run => !run.Bold) && urlStars.Count(run => run.Url is not null) == 2,
    "Asterisks in URLs keep links valid without bold");

var screenshot = AnswerLinks.Extract(
    "Goldentail Pie: https://ffxiv.consolegameswiki.com/wiki/Goldentail_Pie. " +
    "Teamcraft: https://ffxivteamcraft.com/db/en/item/46254");
Check(screenshot.Count == 2, "Wiki and Teamcraft extracted");
Check(screenshot[0].Host == "ffxiv.consolegameswiki.com", "Wiki host");
Check(screenshot[1].Host == "ffxivteamcraft.com", "Teamcraft host");

var markdown = AnswerLinks.Extract(
    "[Quest](https://ffxiv.consolegameswiki.com/wiki/A_(B)) and " +
    "<https://example.org/guide>, then https://example.org/next.)");
Check(markdown.Count == 3, "Markdown links and punctuation");
Check(markdown[0].AbsoluteUri.EndsWith("/A_(B)"), "Balanced wiki parentheses preserved");
Check(markdown[1].AbsoluteUri == "https://example.org/guide", "Autolink bracket trimmed");
Check(markdown[2].AbsoluteUri == "https://example.org/next", "Sentence punctuation trimmed");
var markdownRuns = AnswerLinks.Parse("[Quest](https://ffxiv.consolegameswiki.com/wiki/A_(B)) and " +
    "<https://example.org/guide>, then https://example.org/next.)");
Check(Display(markdownRuns) == "Quest and https://example.org/guide, then https://example.org/next.)",
    "Balanced Markdown URL and bare-link punctuation display correctly");

var duplicates = AnswerLinks.Extract(
    "https://EXAMPLE.org:443/path https://example.org/path, https://example.org/other");
Check(duplicates.Count == 2, "Canonical URL deduplication");

var unsafeLinks = AnswerLinks.Extract(
    "http://example.org/ javascript:alert(1) file:///c:/x " +
    "https://user:pass@example.org/private https:// https://example.org/%ZZ " +
    "https://safe.example.org/page");
Check(unsafeLinks.Count == 1 && unsafeLinks[0].Host == "safe.example.org",
    "Schemes, credentials and malformed URLs rejected");
var invalidLiteral = "[bad](http://example.org/) [private](https://user:pass@example.org/) " +
    "![image](https://example.org/image) [broken](https://example.org/noend";
var invalidRuns = AnswerLinks.Parse(invalidLiteral);
Check(invalidRuns.All(run => run.Url is null) && Display(invalidRuns) == invalidLiteral,
    "Invalid Markdown destinations and unsupported image syntax remain literal");

var capped = AnswerLinks.Extract(string.Join(" ", Enumerable.Range(0, 25).Select(i =>
    $"https://example.org/{i}")));
Check(capped.Count == 20, "Maximum of twenty links");

var mapText = "Go to [Town (X:12.3, Y:24.5)](compai-map://132/2?x=12.3&y=24.5), " +
    "[same place](compai-map://132/2?x=12.300&y=24.500) and [guide](https://example.org/guide).";
var mapRuns = AnswerLinks.Parse(mapText);
Check(mapRuns.Count(run => run.Url?.StartsWith(MapLocationLink.Scheme) == true) == 2,
    "Explicit bounded map markdown becomes local clickable text");
Check(AnswerLinks.ExtractMaps(mapText).Count == 1 && AnswerLinks.Extract(mapText).Single().Host == "example.org",
    "Canonical map actions deduplicate and never become external web sources");
foreach (var url in new[] { "compai-map://132/2?x=1&y=2&command=teleport", "compai-map://132/2?x=NaN&y=2",
    "compai-map://132/2?x=1&y=2#other", "compai-map://132/2?x=0&y=2", "compai-map://132/2?x=1&y=101" })
{
    var raw = "[bad](" + url + ")";
    Check(Display(AnswerLinks.Parse(raw)) == raw && AnswerLinks.ExtractMaps(raw).Count == 0,
        "Malformed or command-bearing map links stay literal");
}
Check(AnswerLinks.ExtractMaps(string.Join(" ", Enumerable.Range(1, 15).Select(i =>
    $"[map](compai-map://132/2?x={i}&y=2)"))).Count == 8, "Native map actions capped at eight per answer");
Console.WriteLine("AnswerLinks offline regressions passed.");
