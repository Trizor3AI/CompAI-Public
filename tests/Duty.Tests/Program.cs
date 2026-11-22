using System.Runtime.Loader;
using System.Text.Json;
using CompAI;

var libraryPath = Environment.GetEnvironmentVariable("DALAMUD_LIB_PATH") ??
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "addon", "Hooks", "dev");
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var path = Path.Combine(libraryPath, name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
Tests.Run();
if (args is ["--produce-fixtures", var path]) Tests.WriteProducerFixtures(path);

internal static class Tests
{
    private static int passed;
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-10-15T09:00:00Z");
    internal static void Run()
    {
        Test("supported IDs only reach native observation", () =>
        {
            var ids = new List<uint>();
            var snapshot = DutyReader.Describe(Time, [new(10, 100, "First duty"), new(20, null, "Unsupported duty")], id =>
            { ids.Add(id); return new(true, true); });
            Check(ids.SequenceEqual([100u]));
            Check(snapshot.Entries[0].Unlocked == true && snapshot.Entries[0].Completed == true);
            Check(snapshot.Entries[1].Unlocked is null && snapshot.Entries[1].Completed is null && snapshot.Entries[1].UnknownReason is not null);
        });
        Test("unlock and completion stay independent and negatives stay unknown", () =>
        {
            foreach (var native in new[] { new DutyObservedState(false, false), new(true, false), new(false, true), new(true, true) })
            {
                var entry = DutyReader.Describe(Time, [new(10, 100, "Test duty")], _ => native).Entries[0];
                Check(entry.Unlocked == (native.Unlocked ? true : null));
                Check(entry.Completed == (native.Completed ? true : null));
                Check((entry.UnknownReason is null) == (native.Unlocked && native.Completed));
            }
        });
        Test("empty duplicate or invalid catalogs do not call native methods", () =>
        {
            foreach (var catalog in new DutyDefinition[][] { [], [new(0, 100, "A")], [new(1, 0, "A")],
                         [new(1, 100, "A"), new(1, 200, "B")] })
                Check(!DutyReader.Describe(Time, catalog, _ => throw new Exception("Unexpected native call")).Available);
        });
        Test("name and duty ID filters intersect", () =>
        {
            var lookup = new DutyLookup(new(Time, true, [new(1, 100, "Test Cavern", true, null, "Unknown completion"),
                new(2, 200, "Other Cavern", true, true, null)]));
            var result = lookup.Query(Args("{\"query\":\"test cavern\",\"duty_ids\":[1,2]}"));
            Check(result.entries.Length == 1 && result.entries[0].duty_id == 1 && result.entries[0].instance_content_id == 100);
            Check(lookup.Query(Args("{\"duty_ids\":[999]}" )).entries.Length == 0);
        });
        Test("invalid argument types extra fields and duplicate IDs rejected", () =>
        {
            foreach (var value in new[] { "null", "[]", "{\"query\":null}", "{\"query\":\"\\n\"}",
                         "{\"item_ids\":[1]}", "{\"duty_ids\":[0]}", "{\"duty_ids\":[1,1]}", "{\"duty_ids\":[true]}",
                         "{\"duty_ids\":[4294967296]}", "{\"query\":\"a\",\"query\":\"b\"}" })
                Throws<ArgumentException>(() => DutyLookup.ValidateArguments(Args(value)));
            Throws<ArgumentException>(() => DutyLookup.ValidateArguments(JsonSerializer.SerializeToElement(new { duty_ids = Enumerable.Range(1, 21) })));
        });
        Test("false flags missing reasons and unsupported positives rejected", () =>
        {
            foreach (var entry in new DutyEntry[] { new(1, 100, "A", false, null, "Unknown"),
                         new(1, 100, "A", true, null, null), new(1, null, "A", true, null, "Unsupported"),
                         new(1, 100, "A", true, true, "Unexpected reason") })
                Throws<ArgumentException>(() => new DutySnapshot(Time, true, [entry]));
        });
        Test("unavailable never carries prior personal state", () =>
        {
            Throws<ArgumentException>(() => new DutySnapshot(Time, false, [new(1, 100, "A", true, true, null)], "Unloaded"));
            var result = new DutyLookup(new(Time, false, [], "Unloaded")).Query(Args("{}"));
            Check(!result.available && result.entries.Length == 0 && result.unavailable_reason == "Unloaded");
        });
        Test("capture and query arrays cannot mutate the frozen answer", () =>
        {
            var source = new DutyEntry[] { new(1, 100, "A", true, null, "Unknown") };
            var lookup = new DutyLookup(new(Time, true, source));
            source[0] = new(2, 200, "B", true, true, null);
            lookup.Query(Args("{}")).entries[0] = new(9, 900, "Changed", true, true, null);
            Check(lookup.Query(Args("{}")).entries[0].duty_id == 1);
        });
        Test("localized names and large catalogs stay inside row and byte bounds", () =>
        {
            var result = new DutyLookup(Large()).Query(Args("{}"));
            Check(result.entries.Length is > 0 and <= 50 && result.truncated && result.matched_count == 4096);
            Check(JsonSerializer.SerializeToUtf8Bytes(result).Length <= DutyLookup.MaxBytes);
        });
        Console.WriteLine($"{passed} duty fixture groups passed; native observations require in-game verification.");
    }
    private static DutySnapshot Large() => new(Time, true, Enumerable.Range(1, DutySnapshot.MaxEntries)
        .Select(id => new DutyEntry((uint)id, (uint)(id + 5000), new('界', 128), true, null, "Negative readiness unverified")));
    internal static void WriteProducerFixtures(string output)
    {
        static object Fixture(string name, DutySnapshot snapshot, string args) { var arguments = Args(args); return new { name, arguments, result = new DutyLookup(snapshot).Query(arguments) }; }
        var fixtures = new[] {
            Fixture("true_and_unknown", DutyReader.Describe(Time, [new(1, 100, "Synthetic Duty"), new(2, null, "Unsupported Duty")], _ => new(true, false)), "{}"),
            Fixture("independent_completed", DutyReader.Describe(Time, [new(3, 300, "Synthetic Completed")], _ => new(false, true)), "{\"duty_ids\":[3]}"),
            Fixture("unavailable", new(Time, false, [], "Unloaded"), "{}"), Fixture("unicode_truncation", Large(), "{}") };
        var full = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, JsonSerializer.Serialize(fixtures, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static JsonElement Args(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
    private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
}
