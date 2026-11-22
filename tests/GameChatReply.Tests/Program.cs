using System.Runtime.Loader;
using System.Text;
using CompAI;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;

AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "XIVLauncher", "addon", "Hooks", "dev", name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
var passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Test(string name, Action action) { action(); ++passed; Console.WriteLine("PASS " + name); }
const string MapText = "[Town (X:12.3, Y:24.5)](compai-map://132/2?x=12.3&y=24.5)";
const string VerifiedLabel = "Synthetic Town / Upper (X: 12.3, Y: 24.5)";
string Link(uint id) => $"[Place {id}](compai-map://132/{id}?x=12.3&y=24.5)";
void Bound(IReadOnlyList<GameChatReplyLine> lines)
{
    foreach (var line in lines)
    {
        var bytes = GameChatReply.LinePayloadReserveBytes + line.Parts.Sum(part => Encoding.UTF8.GetByteCount(part.Text) +
            (part.Map.HasValue ? GameChatReply.MapPayloadReserveBytes : 0));
        Check(bytes <= GameChatReply.MaxLineBytes, "Line including payload budget exceeded 800 bytes.");
        Check(!line.Text.Any(char.IsControl) && !line.Text.Contains('\uFFFD'), "Control or broken Unicode reached output.");
    }
}

Test("inline native map part stays between prose without a second Map line", () =>
{
    var lines = GameChatReply.FormatParts("Go to " + MapText + ", then wait.", "Armo", resolveMap: _ => VerifiedLabel);
    Check(lines.Count == 1 && lines[0].Parts.Count == 3, "Expected prose, map, prose in one line.");
    Check(lines[0].Text == "[Armo]: Go to " + VerifiedLabel + ", then wait.", "Inline sequence changed.");
    Check(lines[0].Parts[1].Map == new MapLocationLink(132, 2, 12.3m, 24.5m), "Map destination was lost.");
    Bound(lines);
});
Test("bold fragments produce one map action and no duplicated label", () =>
{
    var calls = 0;
    var lines = GameChatReply.FormatParts("Go [**Town** coordinates](compai-map://132/2?x=12.3&y=24.5).",
        resolveMap: _ => { calls++; return VerifiedLabel; });
    Check(calls == 1 && lines.SelectMany(line => line.Parts).Count(part => part.Map.HasValue) == 1, "One label became multiple map actions.");
    Check(lines.Single().Text == "[CompAI]: Go " + VerifiedLabel + ".", "Bold map label duplicated.");
});
Test("atomic Unicode map labels move intact across message boundaries", () =>
{
    var label = new string('界', 128) + " (X: 12.3, Y: 24.5)";
    var lines = GameChatReply.FormatParts(new string('x', 650) + " " + MapText + " after 😀",
        new string('界', 40), resolveMap: _ => label);
    Check(lines.Count >= 2, "Long prose did not wrap.");
    var mapParts = lines.SelectMany(line => line.Parts).Where(part => part.Map.HasValue).ToArray();
    Check(mapParts.Length == 1 && mapParts[0].Text == label, "Map label was split or truncated.");
    Check(lines[^1].Text.EndsWith(" after 😀", StringComparison.Ordinal), "Following Unicode prose was lost.");
    Bound(lines);
});
Test("map resolver attempts and emitted actions are each capped at eight", () =>
{
    var calls = 0;
    var many = string.Join(" then ", Enumerable.Range(1, 20).Select(id => Link((uint)id)));
    var lines = GameChatReply.FormatParts(many, resolveMap: map => { calls++; return "Verified " + map.MapId; });
    Check(calls == 8 && lines.SelectMany(line => line.Parts).Count(part => part.Map.HasValue) == 8, "Unique map/action cap failed.");
    Check(string.Concat(lines.Select(line => line.Text)).Contains("Place 20"), "Unlinked remaining text was dropped.");
    calls = 0;
    lines = GameChatReply.FormatParts(string.Join(" then ", Enumerable.Repeat(MapText, 20)),
        resolveMap: _ => { calls++; return VerifiedLabel; });
    Check(calls == 1 && lines.SelectMany(line => line.Parts).Count(part => part.Map.HasValue) == 8, "Repeated map lookup/action cap failed.");
    calls = 0;
    lines = GameChatReply.FormatParts(many, resolveMap: _ => { calls++; return null; });
    Check(calls == 8 && lines.All(line => line.Parts.All(part => part.Map is null)), "Invalid maps caused unbounded lookups.");
    Bound(lines);
});
Test("unavailable or oversized labels leave readable original text without actions", () =>
{
    foreach (var resolver in new Func<MapLocationLink, string?>[] { _ => null, _ => throw new InvalidOperationException(),
        _ => "\0\u0002", _ => new string('界', 200), _ => new string('x', 513) })
    {
        var lines = GameChatReply.FormatParts("Go " + MapText + ".", resolveMap: resolver);
        Check(lines.Single().Text == "[CompAI]: Go Town (X:12.3, Y:24.5).", "Invalid resolver lost original label.");
        Check(lines.Single().Parts.All(part => part.Map is null), "Invalid resolver created an action.");
    }
});
Test("plain compatibility retains source URLs, speaker colors' text, and sanitized Unicode", () =>
{
    var source = "**Salut șțâîă β** [guide](https://example.com/test)\n\0\u0002\u0003" +
        string.Concat(Enumerable.Repeat(" 😀", 900));
    var lines = GameChatReply.FormatParts(source, "[\u0002Armo\n]:");
    Check(lines.Count > 1 && lines.All(line => line.Text.StartsWith("[Armo]: ", StringComparison.Ordinal)), "Speaker prefix changed.");
    var joined = string.Concat(lines.Select(line => line.Text));
    Check(joined.Contains("Salut șțâîă β") && joined.Contains("guide (https://example.com/test)") && !joined.Contains("**"), "Plain text or HTTPS source lost.");
    Check(GameChatReply.Format("\n\0\u0002").Count == 0, "Control-only output became a message.");
    Check(GameChatReply.Format("**literal** [input]", "Ștefan", false).Single() == "[Ștefan]: **literal** [input]", "Input echo parsed Markdown.");
    var calls = 0;
    var echo = GameChatReply.FormatParts(MapText, markdown: false, resolveMap: _ => { calls++; return VerifiedLabel; });
    Check(calls == 0 && echo.Single().Text.Contains("compai-map://"), "User echo created a map action.");
    Bound(lines);
});
Test("native encoding preserves bounded inline payloads, surrounding text and color", () =>
{
    var map = new MapLocationLink(132, 2, 12.3m, 24.5m);
    // Raw-position constructor avoids all game-sheet access; this checks native encoding only.
    var payload = new MapLinkPayload(map.TerritoryId, map.MapId, 123000, 245000);
    var payloads = new Dictionary<MapLocationLink, MapLinkPayload> { [map] = payload };
    var lines = GameChatReply.FormatParts("Before " + MapText + " after 😀.", "Armo", resolveMap: _ => VerifiedLabel);
    foreach (var color in new uint?[] { null, 0x31AABB })
    {
        var output = GameChatReplyRendering.Build(lines.Single(), payloads, color);
        Check(output.TextValue == lines.Single().Text, "Native encoding altered prose.");
        var encoded = output.Encode();
        Check(encoded.Length <= 800, "Native line exceeded byte bound.");
        var parsed = SeString.Parse(encoded);
        var maps = parsed.Payloads.OfType<MapLinkPayload>().ToArray();
        Check(maps.Length == 1 && maps[0].RawX == 123000 && maps[0].RawY == 245000, "Native map payload was split or lost.");
        Check(parsed.Payloads.OfType<TextPayload>().Any(part => part.Text?.Contains(" after 😀.") == true), "Trailing text is absent.");
        Check(encoded.AsSpan().IndexOf(RawPayload.LinkTerminator.Encode()) >= 0, "Map link terminator is missing.");
        if (color.HasValue) Check(parsed.Payloads.Count > lines.Single().Parts.Count + 4, "Speaker color payloads are missing.");
    }
    var many = GameChatReply.FormatParts(string.Join(" then ", Enumerable.Repeat(MapText, 8)),
        new string('界', 40), resolveMap: _ => new string('界', 128) + " (X: 12.3, Y: 24.5)");
    Check(many.Sum(line => GameChatReplyRendering.Build(line, payloads, 0x31AABB).Payloads.OfType<MapLinkPayload>().Count()) == 8,
        "Bounded long replies lost map links during encoding.");
    Check(many.All(line => GameChatReplyRendering.Build(line, payloads, 0x31AABB).Encode().Length <= 800), "Encoded long reply exceeded byte bound.");
    var rejected = false;
    try { GameChatReplyRendering.Build(new([new(new string('x', 801))]), payloads, 0x31AABB); }
    catch (InvalidOperationException) { rejected = true; }
    Check(rejected, "Encoder accepted an oversized caller-supplied line.");
});
Console.WriteLine($"{passed} inline game-chat map fixture groups passed; no game/account/network calls.");
