using System.Runtime.Loader;
using System.Text.Json;
using CompAI;

var libraryPath = Environment.GetEnvironmentVariable("DALAMUD_LIB_PATH") ??
                  Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                      "XIVLauncher", "addon", "Hooks", "dev");
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var path = Path.Combine(libraryPath, name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
Tests.Run();
if (args is ["--produce-fixtures", var outputPath]) Tests.WriteProducerFixtures(outputPath);

internal static class Tests
{
    private static int passed;
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-10-12T09:00:00Z");

    internal static void Run()
    {
        Test("unloaded and unsupported layouts stay unavailable", () =>
        {
            foreach (var count in new[] { 0, CurrencySnapshot.MaxEntries + 1 })
                Check(!CurrencyReader.Describe(Time, true, new CurrencySlotValue[count], _ => "Test").Available);
            Check(!CurrencyReader.Describe(Time, false, [new(1, 100, 0)], _ => "Gil").Available);
        });
        Test("empty IDs never invent zeros; observed zero retained", () =>
        {
            var snapshot = CurrencyReader.Describe(Time, true, [new(1, 0, 0), new(0, 42, 1)], _ => "Gil");
            Check(snapshot.Available && snapshot.Entries.Count == 1 && snapshot.Entries[0].Quantity == 0);
            var result = new CurrencyLookup(snapshot).Query(Args("{\"item_ids\":[999]}"));
            Check(result.available && result.entries.Length == 0 && result.matched_count == 0);
            Check(result.coverage.Contains("unknown, not zero") && result.coverage.Contains("Cosmic Exploration"));
        });
        Test("malformed and duplicate observed slots fail closed", () =>
        {
            foreach (var bad in new[] { new CurrencySlotValue(1, -1, 0), new(1, 5, 1),
                         new(1, 5, 0, false), new(1, 5, 0, true, true) })
                Check(!CurrencyReader.Describe(Time, true, [bad], _ => "Gil").Available);
            Check(!CurrencyReader.Describe(Time, true, [new(1, 5, 0), new(1, 6, 1)], _ => "Gil").Available);
        });
        Test("missing or malformed names keep ID with unavailable name", () =>
        {
            foreach (var name in new string?[] { null, "", "\0name", new('x', 129) })
            {
                var snapshot = CurrencyReader.Describe(Time, true, [new(1, 100, 0)], _ => name);
                Check(snapshot.Entries[0].Name == "[currency name unavailable]" && snapshot.Entries[0].ItemId == 1);
            }
        });
        Test("common balances require presence and a named local definition", () =>
        {
            var inventory = new CurrencySnapshot(Time, true, [new(1, "Gil", 100)]);
            var snapshot = CurrencyReader.AddCommon(inventory,
                [new(21072, true, 0), new(26533, false, 999), new(26807, true, 5), new(33913, true, 10)],
                id => id == 33913 ? null : "Synthetic common currency");
            Check(snapshot.Available && snapshot.Entries.Count == 3);
            Check(snapshot.Entries.Single(entry => entry.ItemId == 21072).Quantity == 0);
            Check(snapshot.Entries.All(entry => entry.ItemId != 26533 && entry.ItemId != 33913));
            Check(snapshot.CapturedAtUtc == inventory.CapturedAtUtc);
        });
        Test("common balance conflicts and unsupported IDs fail closed", () =>
        {
            var inventory = new CurrencySnapshot(Time, true, [new(21072, "Venture", 1)]);
            foreach (var balances in new CurrencyBalanceValue[][] {
                [new(21072, true, 2)], [new(21072, true, uint.MaxValue)], [new(37549, true, 1)],
                [new(21072, true, 1), new(21072, true, 1)] })
                Check(!CurrencyReader.AddCommon(inventory, balances, _ => "Synthetic").Available);
            Check(CurrencyReader.AddCommon(inventory, [new(21072, true, 1)], _ => "Venture").Entries.Count == 1);
            Check(!CurrencyReader.AddCommon(new(Time, false, [], "Unloaded"),
                [new(21072, true, 1)], _ => "Venture").Available);
        });
        Test("common allowlist covers requested families and excludes content currencies", () =>
        {
            foreach (var id in new uint[] { 21072, 10307, 33913, 33914, 41784, 41785, 26533, 26807, 36656 })
                Check(CurrencyReader.CommonItemIds.Contains(id));
            foreach (var id in new uint[] { 31135, 37549, 37550, 41668, 21173, 49124 })
                Check(!CurrencyReader.CommonItemIds.Contains(id));
        });
        Test("MGF and MGC labels expand after validating local names in both sources", () =>
        {
            static string? Name(uint id) => id switch { 41629 => "MGF", 52322 => "MGC", 29 => "MGP", 21072 => "Venture", _ => null };
            var inventory = CurrencyReader.Describe(Time, true,
                [new(41629, 12, 0), new(52322, 0, 1), new(29, 99, 2), new(21072, 7, 3)], Name);
            var common = CurrencyReader.AddCommon(new(Time, true, [new(29, "MGP", 99)]),
                [new(41629, true, 12), new(52322, true, 0), new(21072, true, 7)], Name);
            foreach (var snapshot in new[] { inventory, common })
            {
                Check(snapshot.Entries.Single(entry => entry.ItemId == 41629).Name == "MGF (Manderville Gold Saucer Fame)");
                Check(snapshot.Entries.Single(entry => entry.ItemId == 52322).Name == "MGC (Manderville Gold Saucer Credits)");
                Check(snapshot.Entries.Single(entry => entry.ItemId == 29).Name == "MGP");
                Check(snapshot.Entries.Single(entry => entry.ItemId == 21072).Name == "Venture");
                Check(snapshot.Entries.Single(entry => entry.ItemId == 52322).Quantity == 0);
            }
            foreach (var invalid in new string?[] { null, "", "MGF\0", new('x', 129) })
            {
                Check(CurrencyReader.Describe(Time, true, [new(41629, 1, 0)], _ => invalid).Entries[0].Name == "[currency name unavailable]");
                Check(CurrencyReader.AddCommon(new(Time, true, []), [new(41629, true, 1)], _ => invalid).Entries.Count == 0);
            }
            Check(CurrencyReader.Describe(Time, true, [new(41629, 1, 0)], _ => "Changed local name").Entries[0].Name == "Changed local name");
            Check(CurrencyReader.AddCommon(new(Time, true, []), [new(52322, true, 1)], _ => "Changed local name").Entries[0].Name == "Changed local name");
        });
        Test("query ID and name filters intersect", () =>
        {
            var lookup = new CurrencyLookup(new(Time, true,
                [new(1, "Gil", 1000), new(2, "Test Scrip", 50), new(3, "Other Scrip", 5)]));
            var result = lookup.Query(Args("{\"query\":\"test sCrip\",\"item_ids\":[2,3]}"));
            Check(result.entries.Length == 1 && result.entries[0].item_id == 2 && result.entries[0].quantity == 50);
            Check(lookup.Query(Args("{}")).entries.Length == 3);
        });
        Test("Cosmic loaded HUD includes explicit zero and current planet only", () =>
        {
            var sample = Cosmic();
            var rows = CosmicCurrencyReader.CaptureStable(() => sample, CosmicDefinition, 1237);
            Check(rows.Length == 2 && rows[0].ItemId == 45690 && rows[0].Quantity == 0);
            Check(rows[1].ItemId == 45691 && rows[1].Quantity == 42);
            foreach (var (id, icon, name) in new[] { (48146, 65127, "Phaenna Credit"),
                (48147, 65128, "Oizys Credit"), (48148, 65129, "Auxesia Credit") })
            {
                sample = sample with { PlanetItemId = id, PlanetIcon = icon };
                rows = CosmicCurrencyReader.CaptureStable(() => sample, CosmicDefinition, 1237);
                Check(rows.Length == 2 && rows[1].ItemId == id && rows[1].Name == name &&
                    rows[1].Quantity == 42 && rows.All(r => r.ItemId != 45691));
            }
        });
        Test("unloaded wrong-zone and fetching Cosmic data never invent balances", () =>
        {
            foreach (var invalid in new[] { Cosmic() with { Loaded = false }, Cosmic() with { Territory = 999 },
                Cosmic() with { State = 0 }, Cosmic() with { State = 1 }, Cosmic() with { State = 2 },
                Cosmic() with { Manager = 0 }, Cosmic() with { Agent = 0 }, Cosmic() with { Info = 0 } })
                Check(CosmicCurrencyReader.CaptureStable(() => invalid, CosmicDefinition, 1237).Length == 0);
            Check(CosmicCurrencyReader.CaptureStable(() => null, CosmicDefinition, 1237).Length == 0);
            Check(CosmicCurrencyReader.CaptureStable(() => Cosmic(), CosmicDefinition, 0).Length == 0);
        });
        Test("Cosmic malformed counters and unsupported planet identity stay unknown", () =>
        {
            foreach (var invalid in new[] { Cosmic() with { CosmoCount = -1 }, Cosmic() with { CosmoCount = 30001 },
                Cosmic() with { PlanetCount = -1 }, Cosmic() with { PlanetCount = 10001 },
                Cosmic() with { CosmoMax = 0 }, Cosmic() with { PlanetMax = 0 },
                Cosmic() with { PlanetItemId = -1 }, Cosmic() with { PlanetItemId = 1 },
                Cosmic() with { PlanetItemId = 48149 },
                Cosmic() with { CosmoIcon = 1 }, Cosmic() with { PlanetIcon = 65127 } })
                Check(CosmicCurrencyReader.CaptureStable(() => invalid, CosmicDefinition, 1237).Length == 0);
        });
        Test("Cosmic sample changes across metadata work reject all regional rows", () =>
        {
            foreach (var changed in new CosmicCurrencySample?[] { null, Cosmic() with { Info = 4 },
                Cosmic() with { Manager = 4 }, Cosmic() with { Agent = 4 }, Cosmic() with { State = 2 },
                Cosmic() with { Territory = 1 }, Cosmic() with { CosmoCount = 1 },
                Cosmic() with { PlanetCount = 43 }, Cosmic() with { PlanetMax = 5000 } })
            {
                var count = 0;
                Check(CosmicCurrencyReader.CaptureStable(() => ++count == 1 ? Cosmic() : changed,
                    CosmicDefinition, 1237).Length == 0);
            }
        });
        Test("Cosmic metadata must remain named matching and stable", () =>
        {
            foreach (var name in new[] { "", "bad\0", new string('a', 129) })
                Check(CosmicCurrencyReader.CaptureStable(() => Cosmic(),
                    id => CosmicDefinition(id)!.Value with { Name = name }, 1237).Length == 0);
            Check(CosmicCurrencyReader.CaptureStable(() => Cosmic(), _ => null, 1237).Length == 0);
            var reads = 0;
            Check(CosmicCurrencyReader.CaptureStable(() => Cosmic(), id => ++reads < 3 ? CosmicDefinition(id) :
                CosmicDefinition(id)!.Value with { Name = "Changed" }, 1237).Length == 0);
        });
        Test("Cosmic merge preserves ordinary balances and rejects conflicting sources", () =>
        {
            var rows = CosmicCurrencyReader.CaptureStable(() => Cosmic(), CosmicDefinition, 1237);
            var inventory = new CurrencySnapshot(Time, true, [new(1, "Gil", 10)]);
            var combined = CurrencyReader.AddCosmic(inventory, rows);
            Check(combined.Available && combined.Entries.Count == 3 && combined.CapturedAtUtc == Time);
            Check(ReferenceEquals(inventory, CurrencyReader.AddCosmic(inventory, [])));
            Check(!CurrencyReader.AddCosmic(new(Time, true, [new(45690, "Cosmocredit", 1)]), rows).Available);
            Check(!CurrencyReader.AddCosmic(new(Time, false, [], "Unloaded"), rows).Available);
            Check(!CurrencyReader.AddCosmic(inventory, [rows[0]]).Available);
            Check(!CurrencyReader.AddCosmic(inventory, [rows[0], new(1, "Gil", 1)]).Available);
        });
        Test("bad arguments rejected before reading", () =>
        {
            foreach (var input in new[] { "null", "[]", "{\"bad\":0}", "{\"query\":null}",
                         "{\"query\":\"\\n\"}", "{\"item_ids\":[0]}", "{\"item_ids\":[-1]}",
                         "{\"item_ids\":[1,1]}", "{\"item_ids\":[1.1]}", "{\"item_ids\":null}",
                         "{\"query\":\"a\",\"query\":\"b\"}", "{\"query\":\"" + new string('x', 101) + "\"}" })
                Throws<ArgumentException>(() => CurrencyLookup.ValidateArguments(Args(input)));
            Throws<ArgumentException>(() => CurrencyLookup.ValidateArguments(
                JsonSerializer.SerializeToElement(new { item_ids = Enumerable.Range(1, 21).ToArray() })));
        });
        Test("captures and response arrays cannot rewrite later queries", () =>
        {
            var source = new CurrencyEntry[] { new(1, "Gil", 7) };
            var snapshot = new CurrencySnapshot(Time, true, source);
            var lookup = new CurrencyLookup(snapshot);
            source[0] = new(1, "Gil", 999);
            lookup.Query(Args("{}")).entries[0] = new(1, "Gil", 888);
            Check(snapshot.Entries[0].Quantity == 7 && lookup.Query(Args("{}")).entries[0].quantity == 7);
        });
        Test("unavailable results cannot leak old rows", () =>
        {
            Throws<ArgumentException>(() => new CurrencySnapshot(Time, false, [new(1, "Gil", 4)], "Unloaded"));
            var result = new CurrencyLookup(new(Time, false, [], "Unloaded")).Query(Args("{}"));
            Check(!result.available && result.entries.Length == 0 && result.unavailable_reason == "Unloaded");
        });
        Test("capture bounds, identity ambiguity and negative values rejected", () =>
        {
            Throws<ArgumentException>(() => new CurrencySnapshot(Time, true, [new(1, "Gil", -1)]));
            Throws<ArgumentException>(() => new CurrencySnapshot(Time, true, [new(0, "Gil", 1)]));
            Throws<ArgumentException>(() => new CurrencySnapshot(Time, true, [new(1, "Gil", 1), new(1, "Gil", 2)]));
            Throws<ArgumentException>(() => new CurrencySnapshot(Time, true,
                Enumerable.Range(1, CurrencySnapshot.MaxEntries + 1).Select(id => new CurrencyEntry((uint)id, "Test", 1))));
        });
        Test("result count and encoded bytes bounded", () =>
        {
            var snapshot = new CurrencySnapshot(Time, true,
                Enumerable.Range(1, CurrencySnapshot.MaxEntries).Select(id => new CurrencyEntry((uint)id, new('語', 128), 1)));
            var result = new CurrencyLookup(snapshot).Query(Args("{}"));
            Check(result.entries.Length is > 0 and <= CurrencyLookup.MaxResults && result.truncated);
            Check(result.matched_count == CurrencySnapshot.MaxEntries);
            Check(JsonSerializer.SerializeToUtf8Bytes(result).Length <= CurrencyLookup.MaxBytes);
        });
        Console.WriteLine($"{passed} currency fixture groups passed; native capture requires in-game verification.");
    }

    private static JsonElement Args(string value) => JsonDocument.Parse(value).RootElement.Clone();
    internal static CosmicCurrencySample Cosmic() => new(1, 2, 3, 1237, true, 3,
        0, 30000, 65112, 42, 10000, 45691, 65126);
    internal static CosmicCurrencyDefinition? CosmicDefinition(uint id) => id switch
    {
        45690 => new("Cosmocredit", 65112), 45691 => new("Lunar Credit", 65126),
        48146 => new("Phaenna Credit", 65127), 48147 => new("Oizys Credit", 65128),
        48148 => new("Auxesia Credit", 65129), _ => null,
    };
    internal static void WriteProducerFixtures(string outputPath)
    {
        static object Produce(string name, CurrencySnapshot snapshot, JsonElement arguments) =>
            new { name, arguments, result = new CurrencyLookup(snapshot).Query(arguments) };
        var fixtures = new[]
        {
            Produce("unicode_byte_truncation", new(Time, true,
                Enumerable.Range(1, CurrencySnapshot.MaxEntries).Select(id =>
                    new CurrencyEntry((uint)id, new('界', 128), 1))), Args("{}")),
            Produce("unavailable", new(Time, false, [], "Currency container is unavailable."), Args("{}")),
            Produce("explicit_zero_and_absent_id", new(Time, true, [new(1, "Gil", 0)]),
                Args("{\"item_ids\":[1,999]}")),
            Produce("common_present_zero_missing_unknown", CurrencyReader.AddCommon(new(Time, true, [new(1, "Gil", 1)]),
                [new(21072, true, 0), new(26533, false, 0), new(36656, true, 123)],
                id => id == 21072 ? "Venture" : "Trophy Crystal"), Args("{}")),
            Produce("cosmic_current_zone_only", CurrencyReader.AddCosmic(new(Time, true, [new(1, "Gil", 1)]),
                CosmicCurrencyReader.CaptureStable(() => Cosmic(), CosmicDefinition, 1237)),
                Args("{\"item_ids\":[45690,45691,48146]}")),
            Produce("cosmic_absent_outside_zone", CurrencyReader.AddCosmic(new(Time, true, [new(1, "Gil", 1)]),
                CosmicCurrencyReader.CaptureStable(() => null, CosmicDefinition, 999)),
                Args("{\"item_ids\":[45690,45691,48146]}")),
            Produce("cosmic_oizys", CurrencyReader.AddCosmic(new(Time, true, [new(1, "Gil", 1)]),
                CosmicCurrencyReader.CaptureStable(() => Cosmic() with { PlanetItemId = 48147, PlanetIcon = 65128 },
                    CosmicDefinition, 1237)), Args("{\"item_ids\":[45690,48147,48148]}")),
            Produce("cosmic_auxesia", CurrencyReader.AddCosmic(new(Time, true, [new(1, "Gil", 1)]),
                CosmicCurrencyReader.CaptureStable(() => Cosmic() with { PlanetItemId = 48148, PlanetIcon = 65129 },
                    CosmicDefinition, 1237)), Args("{\"item_ids\":[45690,48147,48148]}")),
        };
        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(fixtures, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Wrote synthetic currency producer fixtures.");
    }
    private static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
    private static void Test(string name, Action body) { body(); passed++; Console.WriteLine("PASS " + name); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}
