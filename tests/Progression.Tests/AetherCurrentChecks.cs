using CompAI;
using Lumina;
using Lumina.Data;
using Lumina.Excel.Sheets;

internal static class AetherCurrentChecks
{
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-10-25T12:00:00Z");
    private static readonly AetherCurrentObservation Owner = new(1, 42, new string('0', 112), new string('0', 8));
    private static AetherZoneDefinition[] Definitions() => [new(1, "Synthetic North", [2818048, 2818051, 2818058])];

    internal static void Run(System.Action<string, System.Action> test)
    {
        test("aether member references exclude placeholder holes and preserve full EventIds", Membership);
        test("aether positive counts retain unknown negatives and never promote zone completion", PositiveCounts);
        test("aether supported membership rejects missing invalid duplicate and oversized definitions", InvalidDefinitions);
        test("aether capture rechecks owner and catalog before dereferencing retained state", CaptureBoundaries);
        test("aether repeated capture rejects changing flags and accepts matching stable passes", RepeatedCapture);
        test("aether catalog identity includes each referenced sheet game and language", CatalogIdentity);
        test("aether contracts reject unpaired inconsistent unknown and oversized counts", CountContract);
    }

    private static void Membership()
    {
        var definition = ProgressionReader.DefineAetherZone(1, "Synthetic North",
            [new(2818048, true), new(0, false), new(2818051, true), new(0, true), new(2818058, true)]);
        Check(definition.CurrentIds!.SequenceEqual([2818048u, 2818051u, 2818058u]));
        var calls = new List<uint>();
        var result = ProgressionReader.DescribeAetherCurrents(Time, [definition], _ => false,
            id => { calls.Add(id); return id == 2818051; });
        Check(calls.SequenceEqual(definition.CurrentIds!) && result.Entries[0].Progress == 1 && result.Entries[0].Required == 3);
        Check(ProgressionReader.DefineAetherZone(6, "Synthetic Small Zone", [new(2818123, true)]).CurrentIds!.Length == 1);
        foreach (var refs in new AetherCurrentReference[][]
        {
            [], [new(0, true)], [new(2818048, false)], [new(2818048, true), new(2818048, true)],
            Enumerable.Range(0, 16).Select(id => new AetherCurrentReference((uint)(2818048 + id), true)).ToArray(),
        }) Throws<InvalidOperationException>(() => ProgressionReader.DefineAetherZone(1, "Synthetic", refs));
    }

    internal static ProgressionSnapshot Counts(bool complete, Func<uint, bool> observeCurrent) =>
        ProgressionReader.DescribeAetherCurrents(Time, Definitions(), _ => complete, observeCurrent);

    private static void PositiveCounts()
    {
        foreach (var count in new[] { 0, 1, 3 })
        {
            var positives = Definitions()[0].CurrentIds!.Take(count).ToHashSet();
            var result = Counts(false, positives.Contains);
            var row = result.Entries.Single();
            Check(row.Progress == count && row.Required == 3 && row.Completed is null && row.State == "not_observed_complete");
            Check(row.UnknownReason!.Contains("unknown, not missing"));
        }
        var complete = Counts(true, _ => true).Entries.Single();
        Check(complete.Completed == true && complete.State == "completed" && complete.Progress == complete.Required);
        Check(!Counts(true, id => id == 2818048).Available);
    }

    private static void InvalidDefinitions()
    {
        foreach (var ids in new uint[]?[] { null, [], [0], [2818048, 2818048], Enumerable.Range(0, 16).Select(i => (uint)(2818048 + i)).ToArray() })
        {
            var calls = 0;
            var result = ProgressionReader.DescribeAetherCurrents(Time, [new(1, "Synthetic", ids)],
                _ => { calls++; return false; }, _ => { calls++; return false; });
            Check(!result.Available && calls == 0);
        }
        Check(!ProgressionReader.DescribeAetherCurrents(Time, [Definitions()[0], Definitions()[0]], _ => false, _ => false).Available);
        var unknown = ProgressionReader.DescribeAetherCurrents(Time, [new(32, "Future Zone")],
            _ => throw new Exception("Unsupported zone read"), _ => throw new Exception("Unsupported member read")).Entries.Single();
        Check(unknown.State == "unknown" && unknown.Progress is null && unknown.Required is null);
    }

    private static void CaptureBoundaries()
    {
        var calls = 0;
        var catalogCalls = 0;
        var result = ProgressionReader.CaptureAetherCurrents(Time, () => null,
            () => { catalogCalls++; return Definitions(); }, _ => { calls++; return false; }, _ => { calls++; return false; }, () => true);
        Check(!result.Available && catalogCalls == 0 && calls == 0);
        foreach (var replacement in new AetherCurrentObservation?[]
        {
            null, Owner with { Player = 2 }, Owner with { ContentId = 43 },
            Owner with { Currents = new string('1', 112) }, Owner with { Zones = new string('1', 8) },
        })
        {
            var loadedCatalog = false;
            calls = 0;
            result = ProgressionReader.CaptureAetherCurrents(Time, () => loadedCatalog ? replacement : Owner,
                () => { loadedCatalog = true; return Definitions(); }, _ => { calls++; return false; },
                _ => { calls++; return false; }, () => true);
            Check(!result.Available && calls == 0);
        }
        result = ProgressionReader.CaptureAetherCurrents(Time, () => Owner, Definitions,
            _ => { calls++; return false; }, _ => { calls++; return false; }, () => false);
        Check(!result.Available && calls == 0);
    }

    private static void RepeatedCapture()
    {
        var zoneCalls = 0;
        var currentCalls = new List<uint>();
        var result = ProgressionReader.CaptureAetherCurrents(Time, () => Owner, Definitions,
            _ => { zoneCalls++; return false; }, id => { currentCalls.Add(id); return id == 2818051; }, () => true);
        Check(result.Available && zoneCalls == 2 && currentCalls.SequenceEqual(Definitions()[0].CurrentIds!.Concat(Definitions()[0].CurrentIds!)));
        Check(result.Entries.Single().Progress == 1);
        zoneCalls = 0;
        result = ProgressionReader.CaptureAetherCurrents(Time, () => Owner, Definitions,
            _ => { zoneCalls++; return false; }, _ => zoneCalls == 1, () => true);
        Check(!result.Available && zoneCalls == 2);
        zoneCalls = 0;
        result = ProgressionReader.CaptureAetherCurrents(Time, () => zoneCalls == 0 ? Owner : Owner with { ContentId = 99 }, Definitions,
            _ => { zoneCalls++; return false; }, _ => true, () => true);
        Check(!result.Available && zoneCalls == 1);
        zoneCalls = 0;
        result = ProgressionReader.CaptureAetherCurrents(Time, () => Owner, Definitions,
            _ => { zoneCalls++; return false; }, _ => true, () => zoneCalls == 0);
        Check(!result.Available && zoneCalls == 1);
    }

    private static void CatalogIdentity()
    {
        var identities = Enumerable.Range(0, 5).Select(_ => new object()).ToArray();
        var catalog = new AetherCurrentCatalog(identities[0], identities[1], identities[2], identities[3], identities[4], "English", Definitions());
        Check(catalog.Matches(identities[0], identities[1], identities[2], identities[3], identities[4], "English"));
        for (var index = 0; index < identities.Length; index++)
        {
            var changed = identities.ToArray();
            changed[index] = new object();
            Check(!catalog.Matches(changed[0], changed[1], changed[2], changed[3], changed[4], "English"));
        }
        Check(!catalog.Matches(identities[0], identities[1], identities[2], identities[3], identities[4], "French"));
    }

    private static void CountContract()
    {
        foreach (var row in new ProgressionEntry[]
        {
            new(1, "Synthetic", "completed", Completed: true, Progress: 2, Required: 3),
            new(1, "Synthetic", "completed", Completed: true, Progress: 3),
            new(1, "Synthetic", "completed", Completed: true, Required: 3),
            new(1, "Synthetic", "unknown", Progress: 0, Required: 3, UnknownReason: "Unknown"),
            new(1, "Synthetic", "not_observed_complete", Progress: -1, Required: 3, UnknownReason: "Unknown"),
            new(1, "Synthetic", "not_observed_complete", Progress: 4, Required: 3, UnknownReason: "Unknown"),
            new(1, "Synthetic", "not_observed_complete", Progress: 0, Required: 0, UnknownReason: "Unknown"),
            new(1, "Synthetic", "not_observed_complete", Progress: 0, Required: 16, UnknownReason: "Unknown"),
        }) Throws<ArgumentException>(() => new ProgressionSnapshot("aether_currents", Time, true, "positive_observations_only", [row]));
        Check(new ProgressionSnapshot("aether_currents", Time, true, "positive_observations_only",
            [new(1, "Legacy Synthetic", "completed", Completed: true)]).Entries[0].Progress is null);
    }

    internal static void VerifyPublicCatalog(string sqpack)
    {
        using var game = new GameData(sqpack);
        var definitions = ProgressionReader.BuildAetherCatalog(game.Excel.GetSheet<AetherCurrentCompFlgSet>(Language.English),
            game.Excel.GetSheet<AetherCurrent>(Language.English), game.Excel.GetSheet<TerritoryType>(Language.English),
            game.Excel.GetSheet<PlaceName>(Language.English));
        Check(definitions.Length > 0 && definitions.Select(row => row.Id).Distinct().Count() == definitions.Length);
        Check(definitions.All(row => row.CurrentIds is { Length: > 0 and <= 15 }));
        var result = ProgressionReader.DescribeAetherCurrents(Time, definitions, _ => false, _ => false);
        Check(result.Available && result.Entries.Where(row => row.State != "unknown").All(row => row.Progress == 0 && row.Required is >= 1 and <= 15));
        Console.WriteLine($"PASS installed public aether catalog: {definitions.Length} zones; {definitions.Sum(row => row.CurrentIds!.Length)} member references; " +
            $"member counts {string.Join(',', definitions.Select(row => row.CurrentIds!.Length).Distinct().Order())}. No native state called.");
    }

    private static void Check(bool condition) { if (!condition) throw new Exception("Aether-current assertion failed."); }
    private static void Throws<T>(System.Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}
