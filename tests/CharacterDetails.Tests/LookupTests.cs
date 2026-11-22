using System.Text.Json;
using CompAI;

internal static class LookupTests
{
    private static readonly DateTimeOffset CapturedAt = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private static int passed;

    internal static void Run()
    {
        Test("summary advertises exactly the character-details scope", () =>
        {
            var json = JsonSerializer.SerializeToElement(new CharacterDetailsLookup(Reading()).Summary);
            Check(json.EnumerateObject().Count() == 1 &&
                  json.GetProperty("scope").GetString() == "local_character_details", "exact scope-only summary");
        });
        Test("argument validation rejects extra, missing, duplicated, and mistyped fields", () =>
        {
            string[] invalid =
            [
                "[]", "{}", "{\"view\":\"jobs\",\"offset\":0,\"limit\":20}",
                "{\"view\":\"jobs\",\"gearset_id\":null,\"offset\":0,\"limit\":20,\"extra\":false}",
                "{\"view\":\"jobs\",\"view\":\"jobs\",\"gearset_id\":null,\"offset\":0,\"limit\":20}",
                "{\"view\":\"all\",\"gearset_id\":null,\"offset\":0,\"limit\":20}",
                "{\"view\":\"gearset\",\"gearset_id\":null,\"offset\":0,\"limit\":20}",
                "{\"view\":\"gearsets\",\"gearset_id\":1,\"offset\":0,\"limit\":20}",
                "{\"view\":\"jobs\",\"gearset_id\":null,\"offset\":false,\"limit\":20}",
                "{\"view\":\"jobs\",\"gearset_id\":null,\"offset\":0.5,\"limit\":20}",
                "{\"view\":\"jobs\",\"gearset_id\":null,\"offset\":0,\"limit\":\"20\"}",
            ];
            foreach (var input in invalid)
                Throws<ArgumentException>(() => CharacterDetailsLookup.ValidateArguments(Json(input)));
            foreach (var offset in new[] { -1, 101 })
                Throws<ArgumentException>(() => CharacterDetailsLookup.ValidateArguments(Args("jobs", offset: offset)));
            foreach (var limit in new[] { 0, 21 })
                Throws<ArgumentException>(() => CharacterDetailsLookup.ValidateArguments(Args("jobs", limit: limit)));
            foreach (var id in new[] { -1, 100 })
                Throws<ArgumentException>(() => CharacterDetailsLookup.ValidateArguments(Args("gearset", id)));
            CharacterDetailsLookup.ValidateArguments(Args("gearset", 99, 100, 1));
        });
        Test("jobs paging advances by returned entries and ends cleanly", () =>
        {
            var reading = Reading() with { Jobs = Enumerable.Range(1, 25).Select(i =>
                new CharacterJobContext((uint)i, $"Job {i}", $"J{i}", i, i * 10)).ToArray() };
            var lookup = new CharacterDetailsLookup(reading);
            var first = lookup.Query(Args("jobs"));
            Check(first.available && first.total == 25 && first.entries.Length == 20 && first.next_offset == 20, "first page");
            var last = lookup.Query(Args("jobs", offset: 20));
            Check(last.entries.Length == 5 && last.next_offset is null && last.unavailable_reason is null, "last page");
            var beyond = lookup.Query(Args("jobs", offset: 100));
            Check(beyond.available && beyond.total == 25 && beyond.entries.Length == 0 && beyond.next_offset is null, "past end");
            var row = JsonSerializer.SerializeToElement(first.entries[0]);
            Check(row.EnumerateObject().Select(x => x.Name).SequenceEqual(
                new[] { "class_job_id", "name", "abbreviation", "level", "experience" }), "exact job fields");
        });
        Test("saved-set summaries exclude items and selected set exposes equipment", () =>
        {
            var lookup = new CharacterDetailsLookup(Reading());
            var summaries = lookup.Query(Args("gearsets"));
            var summary = JsonSerializer.SerializeToElement(summaries.entries.Single());
            Check(summary.EnumerateObject().Select(x => x.Name).SequenceEqual(
                new[] { "id", "name", "class_job_id", "class_job_name", "is_current" }), "metadata only");
            var selected = lookup.Query(Args("gearset", 7));
            Check(selected.gearset_id == 7 && selected.available && selected.total == 14 && selected.entries.Length == 14,
                "selected set items");
            var item = (CharacterEquipmentLookupRow)selected.entries[0];
            Check(item.materia.Length == 5 && item.materia[0].grade == 9, "materia preserved");
        });
        Test("missing requested gearset is explicitly unavailable", () =>
        {
            var missing = new CharacterDetailsLookup(Reading()).Query(Args("gearset", 99, offset: 3));
            Check(!missing.available && missing.total == 0 && missing.entries.Length == 0 && missing.next_offset is null,
                "not a successful empty set");
            Check(missing.offset == 3 && missing.gearset_id == 99 && !string.IsNullOrWhiteSpace(missing.unavailable_reason),
                "request context and reason preserved");
        });
        Test("unavailable sections provide bounded nonempty reasons", () =>
        {
            var reading = new CharacterDetailsReading(CapturedAt, null, null, null, " ", "Equipment loading.", new string('X', 1000));
            var lookup = new CharacterDetailsLookup(reading);
            foreach (var view in new[] { "jobs", "equipped", "gearsets", "gearset" })
            {
                var result = lookup.Query(Args(view, view == "gearset" ? 0 : null));
                Check(!result.available && result.total == 0 && result.entries.Length == 0 && result.next_offset is null,
                    "unavailable envelope");
                Check(!string.IsNullOrWhiteSpace(result.unavailable_reason) && result.unavailable_reason.Length <= 256,
                    "bounded reason");
            }
        });
        Test("constructor and returned pages isolate nested equipment and materia arrays", () =>
        {
            var input = Reading();
            var lookup = new CharacterDetailsLookup(input);
            input.Jobs![0] = new(99, "Mutated job", "MUT", 1, 0);
            input.Equipped![0].Materia[0] = new(0, 999, 0, 999, "Mutated materia", false);
            input.Gearsets![0].Items![0] = CharacterDetailsReader.EmptyEquipment(0);
            var first = lookup.Query(Args("equipped"));
            var item = (CharacterEquipmentLookupRow)first.entries[0];
            Check(item.materia[0].materia_id == 27, "input materia isolated");
            item.materia[0] = new(0, 999, 0, 999, "Returned-page mutation", false);
            first.entries[0] = "Replaced row";
            var again = (CharacterEquipmentLookupRow)lookup.Query(Args("equipped")).entries[0];
            Check(again.materia[0].materia_id == 27, "returned materia isolated");
            Check(((CharacterJobLookupRow)lookup.Query(Args("jobs")).entries[0]).class_job_id == 1, "job array isolated");
            Check(!((CharacterEquipmentLookupRow)lookup.Query(Args("gearset", 7)).entries[0]).is_empty, "saved item array isolated");
        });
        Test("escaped equipment pages fit 20KB and make exact forward progress", () =>
        {
            var items = Enumerable.Range(0, 14).Select(slot => new CharacterEquipmentItem(slot,
                new string('\u03B1', 128), 100, 100, new string('\u03B2', 128), false, 700, false,
                Enumerable.Range(0, 5).Select(i => new CharacterMateriaContext(i, 27, 9, 100,
                    new string('\u03B3', 128), false)).ToArray(), null)).ToArray();
            var lookup = new CharacterDetailsLookup(Reading() with { Equipped = items });
            var seen = new List<int>();
            var offset = 0;
            do
            {
                var result = lookup.Query(Args("equipped", offset: offset));
                Check(JsonSerializer.SerializeToUtf8Bytes(result).Length <= 20000, "default escaped byte cap");
                Check(result.entries.Length > 0 && result.entries.Length < 14, "budget trims page");
                seen.AddRange(result.entries.Cast<CharacterEquipmentLookupRow>().Select(x => x.slot));
                if (result.next_offset is not { } next) break;
                Check(next == offset + result.entries.Length && next > offset, "exact positive progress");
                offset = next;
            } while (true);
            Check(seen.SequenceEqual(Enumerable.Range(0, 14)), "no skipped or repeated equipment");
        });
        Test("an individually oversized entry fails instead of returning a stuck page", () =>
        {
            var reading = Reading() with { Jobs = [new(1, new string('X', 21000), "A", 1, 0)] };
            Throws<InvalidOperationException>(() => new CharacterDetailsLookup(reading).Query(Args("jobs")));
        });
        Test("oversized collections and ambiguous saved IDs are rejected", () =>
        {
            Throws<ArgumentException>(() => new CharacterDetailsLookup(Reading() with
            { Jobs = Enumerable.Repeat(new CharacterJobContext(1, "A", "A", 1, 0), 101).ToArray() }));
            var reading = Reading();
            Throws<ArgumentException>(() => new CharacterDetailsLookup(reading with
            { Gearsets = [reading.Gearsets![0], reading.Gearsets[0]] }));
        });
        Console.WriteLine($"Passed {passed} character-details lookup checks.");
    }

    private static CharacterDetailsReading Reading()
    {
        CharacterEquipmentItem[] Items() => Enumerable.Range(0, 14).Select(slot => slot == 0
            ? new CharacterEquipmentItem(0, "Main hand", 100, 100, "Fixture weapon", false, 700, false,
                [new(0, 27, 9, 123, "Fixture materia", false), new(1, 0, 0, null, null, true),
                 new(2, 0, 0, null, null, true), new(3, 0, 0, null, null, true), new(4, 0, 0, null, null, true)], null)
            : CharacterDetailsReader.EmptyEquipment(slot)).ToArray();
        return new(CapturedAt, [new(1, "Fixture job", "FJ", 100, 0)], Items(),
            [new(7, "Fixture set", 1, "Fixture job", false, Items())], null, null, null);
    }

    private static JsonElement Args(string view, int? gearsetId = null, int offset = 0, int limit = 20) =>
        JsonSerializer.SerializeToElement(new { view, gearset_id = gearsetId, offset, limit });
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
    private static void Test(string name, Action action) { action(); passed++; Console.WriteLine($"PASS {name}"); }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
