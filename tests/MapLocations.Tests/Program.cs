using System.Globalization;
using System.Text.Json;
using CompAI;

if (args is ["--inspect-maps", var sqpack])
{
    OfflineMapProbe.Run(sqpack);
    return;
}

var passed = 0;
void Check(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
void Test(string name, Action action) { action(); ++passed; Console.WriteLine("PASS " + name); }
MapLocationDefinition Row(uint id, uint territory = 100, string name = "Synthetic City / First Floor") =>
    new(id, territory, name, "Synthetic City", "Synthetic Region", "First Floor", 200);

Test("display coordinates roundtrip with invariant decimal dots", () =>
{
    var before = CultureInfo.CurrentCulture;
    try
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ro-RO");
        var expected = new MapLocationLink(129, 128, 12.3m, 45.678m);
        Check(expected.ToUrl() == "compai-map://129/128?x=12.3&y=45.678");
        Check(MapLocationLink.TryParse(expected.ToUrl(), out var actual) && expected == actual);
        Check(actual.CoordinateLabel == "X: 12.3, Y: 45.678");
        Check(MapLocationLink.TryParse("compai-map://129/128?x=12.300&y=1.000", out var normalized));
        Check(normalized.ToUrl() == "compai-map://129/128?x=12.3&y=1");
        Check(MapLocationLink.TryParse("compai-map://65535/65535?x=1&y=100", out _));
    }
    finally { CultureInfo.CurrentCulture = before; }
});

Test("untrusted map links reject alternate syntax, overflow, and injected actions", () =>
{
    foreach (var url in new string?[]
    {
        null, "", "https://129/128?x=12&y=13", "COMPAI-MAP://129/128?x=12&y=13",
        " compai-map://129/128?x=12&y=13", "compai-map://129/128?x=12&y=13 ",
        "compai-map://0/128?x=12&y=13", "compai-map://129/0?x=12&y=13",
        "compai-map://65536/128?x=12&y=13", "compai-map://129/4294967296?x=12&y=13",
        "compai-map://0129/128?x=12&y=13", "compai-map://129/0128?x=12&y=13",
        "compai-map://129@evil/128?x=12&y=13", "compai-map://129:80/128?x=12&y=13",
        "compai-map://129/128/?x=12&y=13", "compai-map://129/../128?x=12&y=13",
        "compai-map://129/128?y=13&x=12", "compai-map://129/128?x=12&y=13&x=14",
        "compai-map://129/128?x=12&y=13#anything", "compai-map://129/128?x=12&y=13;command=/teleport",
        "compai-map://129/128?x=12&y=13\n/teleport", "compai-map://129/128?x=12&y=13%0a",
        "compai-map://129/128?x=%31%32&y=13", "compai-map://129/128?x=12,3&y=13",
        "compai-map://129/128?x=NaN&y=13", "compai-map://129/128?x=Infinity&y=13",
        "compai-map://129/128?x=1e1&y=13", "compai-map://129/128?x=+12&y=13",
        "compai-map://129/128?x=-12&y=13", "compai-map://129/128?x=0&y=13",
        "compai-map://129/128?x=0.999&y=13", "compai-map://129/128?x=100.001&y=13",
        "compai-map://129/128?x=12&y=101", "compai-map://129/128?x=012&y=13",
        "compai-map://129/128?x=.12&y=13", "compai-map://129/128?x=12.&y=13",
        "compai-map://129/128?x=12.0001&y=13", "compai-map://129/128?x=１２&y=13",
        "compai-map://129/128?x=12&Y=13", new string('x', 1000),
    }) Check(!MapLocationLink.TryParse(url, out _));
});

Test("manual link records cannot bypass bounds or precision", () =>
{
    foreach (var link in new[] { new MapLocationLink(0, 1, 12, 13), new MapLocationLink(1, 0, 12, 13),
        new MapLocationLink(65536, 1, 12, 13), new MapLocationLink(1, 1, 0, 13),
        new MapLocationLink(1, 1, 12, 101), new MapLocationLink(1, 1, 12.0001m, 13) })
    {
        Check(!link.IsValid);
        var rejected = false;
        try { _ = link.ToUrl(); } catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected);
    }
});

Test("map pairing refuses invented maps, other territories, and unusable size factors", () =>
{
    var link = new MapLocationLink(100, 20, 12.3m, 15.4m);
    Check(MapLocationsReader.IsVerifiedPair(link, 20, 100, 200, 100));
    Check(!MapLocationsReader.IsVerifiedPair(link, 21, 100, 200, 100));
    Check(!MapLocationsReader.IsVerifiedPair(link, 20, 101, 200, 100));
    Check(!MapLocationsReader.IsVerifiedPair(link, 20, 100, 0, 100));
    Check(!MapLocationsReader.IsVerifiedPair(link, 20, 100, 200, 0));
    Check(!MapLocationsReader.IsVerifiedPair(link with { X = 101 }, 20, 100, 200, 100));
});

Test("name lookup retains every distinct floor and bounded deterministic pages", () =>
{
    var rows = Enumerable.Range(1, 500).Select(id => Row((uint)id, id % 2 == 0 ? 100u : 101u));
    var page = MapLocationsReader.SelectPage(rows, new(null, 100, "synthetic FLOOR", 20, 3));
    Check(page.Count == 250 && page.Rows.Select(row => row.map_id).SequenceEqual(new uint[] { 42, 44, 46 }));
    Check(MapLocationsReader.SelectPage(rows, new(null, 100, "city", 400, 3)).Rows.Length == 0);
    Check(MapLocationsReader.SelectPage(rows, new(42, 100, "", 0, 3)).Rows.Single().map_id == 42);
    Check(MapLocationsReader.SelectPage(rows, new(42, 101, "", 0, 3)).Count == 0);
    var floors = new[] { Row(1) with { floor_name = "Upper", name = "Synthetic City / Upper" },
        Row(2) with { floor_name = "Lower", name = "Synthetic City / Lower" } };
    Check(MapLocationsReader.SelectPage(floors, new(null, null, "city", 0, 20)).Rows.Length == 2);
    Check(MapLocationsReader.SelectPage(floors, new(null, null, "lower", 0, 20)).Rows.Single().map_id == 2);
    Check(MapLocationsReader.SelectPage([Row(1, name: "Éther Straße")], new(null, null, "éther", 0, 20)).Count == 1);
    Check(MapLocationsReader.SelectPage([Row(1, name: "Éther Straße")], new(null, null, "strasse", 0, 20)).Count == 0);
});

Test("map search refuses unbounded selectors and runaway catalogs", () =>
{
    foreach (var input in new[] { new MapLookupArguments(null, null, "", 0, 20), new(null, null, "x", 0, 20),
        new(null, null, "city", -1, 20), new(null, null, "city", 65536, 20), new(null, null, "city", 0, 21),
        new(null, null, "city", 0, 0), new(0, null, "", 0, 20), new(65536, null, "", 0, 20),
        new(1, null, "city", 0, 20), new(1, null, "", 1, 20), new(null, 0, "", 0, 20),
        new(null, null, "city\n", 0, 20), new(null, null, new string('x', 101), 0, 20) })
    {
        var rejected = false;
        try { MapLocationsReader.SelectPage([], input); } catch (ArgumentException) { rejected = true; }
        Check(rejected);
    }
    var bounded = false;
    try { MapLocationsReader.SelectPage(Enumerable.Repeat(Row(1), 65537), new(null, null, "city", 0, 20)); }
    catch (InvalidOperationException) { bounded = true; }
    Check(bounded);
});

if (args is ["--write-fixture", var output])
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
    File.WriteAllText(output, JsonSerializer.Serialize(new
    {
        url = new MapLocationLink(100, 1, 12.3m, 15.4m).ToUrl(),
        rows = new[] { Row(1), Row(2) with { floor_name = "Second Floor", name = "Synthetic City / Second Floor" } },
    }));
}
Console.WriteLine($"{passed} map location checks passed.");
