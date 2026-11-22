using System.Text;
using System.Text.Json;
using CompAI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

internal static class CostTests
{
    private static readonly DateTimeOffset CapturedAt = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static int passed;

    internal static void Run()
    {
        Test("requested sections skip unrelated readers and reject invalid selections before work", () =>
        {
            foreach (var section in Enum.GetValues<CharacterDetailsSection>())
            {
                var jobs = 0;
                var equipped = 0;
                var gearsets = 0;
                var selection = new CharacterDetailsSelection(section, section == CharacterDetailsSection.Gearset ? 73 : null);
                var reading = CharacterDetailsReader.CaptureRequested(selection, CapturedAt,
                    () => { jobs++; return []; }, () => { equipped++; return []; }, () => { gearsets++; return []; });
                Check(jobs + equipped + gearsets == 1, "exactly one requested reader invoked");
                Check((reading.Jobs is not null) == (section == CharacterDetailsSection.Jobs), "jobs isolated");
                Check((reading.Equipped is not null) == (section == CharacterDetailsSection.Equipped), "worn gear isolated");
                Check((reading.Gearsets is not null) == (section is CharacterDetailsSection.Gearsets or CharacterDetailsSection.Gearset),
                    "saved gear isolated");
            }
            var calls = 0;
            foreach (var selection in new[] {
                new CharacterDetailsSelection(CharacterDetailsSection.Gearset),
                new CharacterDetailsSelection(CharacterDetailsSection.Gearset, 100),
                new CharacterDetailsSelection(CharacterDetailsSection.Gearsets, 0),
                new CharacterDetailsSelection((CharacterDetailsSection)100) })
                Throws<ArgumentException>(() => CharacterDetailsReader.CaptureRequested(selection, CapturedAt,
                    () => { calls++; return []; }, () => { calls++; return []; }, () => { calls++; return []; }));
            Check(calls == 0, "invalid selection did no work");
        });
        Test("summary-only saved sets have no item description and cannot claim empty set details", () =>
        {
            var work = new Work();
            var sets = CharacterDetailsReader.DescribeGearsets(NativeSets(), 0, 73, false, work.Metadata(), JobName);
            Check(sets.Length == 100 && sets.All(x => x.Items is null), "only summaries copied");
            Check(sets.Single(x => x.IsCurrent).Id == 73, "current saved index retained");
            Check(work.Items == 0 && work.Materia == 0, "zero metadata reads for summaries");
            var lookup = new CharacterDetailsLookup(new(CapturedAt, null, null, sets, null, null, null));
            Check(lookup.Query(Args("gearsets")).available, "summaries available");
            Check(!lookup.Query(Args("gearset", 73)).available, "uncaptured items explicitly unavailable");
        });
        Test("requested set copies only that set and preserves gaps, HQ, and selected-index meaning", () =>
        {
            var entries = NativeSets();
            entries[73].Items[5].ItemId = 0;
            entries[73].Items[0].Materia[2] = 0;
            entries[0].ClassJob = 0; // Unrequested malformed entry cannot poison the selected set.
            var work = new Work();
            var selected = CharacterDetailsReader.DescribeGearsets(entries.AsSpan(73, 1), 73, 73, true,
                work.Metadata(), JobName).Single();
            Check(selected.Id == 73 && selected.IsCurrent && selected.Items!.Length == 14, "correct set and fixed slots");
            Check(selected.Items![0].ItemId == 1000100 && selected.Items[0].BaseItemId == 100 && selected.Items[0].IsHq,
                "raw HQ identity independent of metadata key");
            Check(selected.Items[5].IsEmpty && selected.Items[0].Materia[2].IsEmpty, "equipment and materia gaps retained");
            entries[73].Items[0].Materia[0] = 99;
            Check(selected.Items[0].Materia[0].MateriaId == 27, "native snapshot values copied");
            Check(work.Items == 13 && work.Materia == 5, "selected occupied slots and unique materia only");
            entries[99].Flags = 0;
            var absent = CharacterDetailsReader.DescribeGearsets(entries.AsSpan(99, 1), 99, 73, true,
                work.Metadata(), JobName);
            Check(absent.Length == 0, "missing entry stays missing");
            Throws<InvalidOperationException>(() => CharacterDetailsReader.DescribeGearsets(entries.AsSpan(73, 2),
                73, 73, true, work.Metadata(), JobName));
            entries[73].Id = 72;
            Throws<InvalidOperationException>(() => CharacterDetailsReader.DescribeGearsets(entries.AsSpan(73, 1),
                73, 73, true, work.Metadata(), JobName));
        });
        Test("request cache freezes pages independently and validates every query before capture", () =>
        {
            var selections = new List<CharacterDetailsSelection>();
            var cache = new CharacterDetailsCaptureCache(selection =>
            {
                selections.Add(selection);
                return new(new(CapturedAt.AddSeconds(selections.Count), [], null, [], null, null, null));
            });
            var jobs = cache.GetLookup(Args("jobs"));
            Check(ReferenceEquals(jobs, cache.GetLookup(Args("jobs", offset: 20))), "pagination reuses frozen capture");
            var summaries = cache.GetLookup(Args("gearsets"));
            var firstSet = cache.GetLookup(Args("gearset", 73));
            Check(ReferenceEquals(firstSet, cache.GetLookup(Args("gearset", 73, 8))), "set pagination reuses capture");
            _ = cache.GetLookup(Args("gearset", 74));
            Check(selections.Count == 4 && selections[2].GearsetId == 73 && selections[3].GearsetId == 74,
                "sections and requested set IDs have separate captures");
            Check(jobs.Query(Args("jobs")).captured_at_utc != summaries.Query(Args("gearsets")).captured_at_utc,
                "separate capture timestamps remain truthful");
            Throws<ArgumentException>(() => cache.GetLookup(Args("jobs", 73)));
            Throws<ArgumentException>(() => cache.GetLookup(Args("gearset", 100)));
            Throws<ArgumentException>(() => cache.GetLookup(Args("jobs", offset: -1)));
            Check(selections.Count == 4, "invalid queries never schedule work");
        });
        Test("request metadata reuses base items and grade-specific materia without reusing game state", () =>
        {
            var work = new Work();
            var metadata = work.Metadata();
            var normal = CharacterDetailsReader.DescribeItem(metadata, 0, 100, 100, false,
                [27, 27, 0, 0, 0], [9, 10, 0, 0, 0]);
            var high = CharacterDetailsReader.DescribeItem(metadata, 1, 1000100, 100, true,
                [27, 0, 0, 0, 0], [9, 0, 0, 0, 0]);
            Check(work.Items == 1 && work.Materia == 2, "one base item, distinct grades cached separately");
            Check(!normal.IsHq && high.IsHq && normal.Slot == 0 && high.Slot == 1, "state stays per item");
            Check(normal.Materia[1].Grade == 10 && high.Materia[1].IsEmpty, "materia state stays per item");
            var unknownCalls = 0;
            var unknown = new CharacterDetailsMetadata(_ => ("[item name unavailable]", null), (_, _) =>
                { unknownCalls++; return (null, null); });
            Check(unknown.Materia(999, 255) == (null, null) && unknown.Materia(999, 255) == (null, null), "unknown preserved");
            Check(unknownCalls == 1, "unknown metadata is request-cached too");
            _ = work.Metadata().Item(100);
            Check(work.Items == 2, "a new request resolves metadata again");
        });
        Test("bounded 100-set workload matches prior results with fewer descriptions and metadata reads", () =>
        {
            var entries = NativeSets();
            var before = new Work();
            CharacterEquipmentItem[] LegacyItems(int set) => Enumerable.Range(0, 14).Select(slot =>
            {
                var item = entries[set].Items[slot];
                // Prior implementation resolved every item and its five distinct materia on every appearance.
                return CharacterDetailsReader.DescribeItem(before.Metadata(), slot, item.ItemId, (uint)(100 + slot),
                    slot == 0 && set % 2 == 1, item.Materia, item.MateriaGrades);
            }).ToArray();
            CharacterJobContext[] Jobs() => [new(1, "Fixture job", "FJ", 100, 0)];
            var baseline = new CharacterDetailsLookup(new(CapturedAt, Jobs(), LegacyItems(0),
                Enumerable.Range(0, 100).Select(i => new CharacterGearsetContext(i, $"Fixture {i}", 1,
                    JobName(1), i == 73, LegacyItems(i))).ToArray(), null, null, null));
            Check(before.Items == 1414 && before.Materia == 7070, "prior all-section work measured");

            foreach (var (view, setId) in new (string, int?)[] { ("jobs", null), ("gearsets", null), ("gearset", 73), ("equipped", null) })
            {
                var after = new Work();
                var metadata = after.Metadata();
                var arguments = Args(view, setId);
                var selection = CharacterDetailsLookup.SelectCapture(arguments);
                var reading = CharacterDetailsReader.CaptureRequested(selection, CapturedAt, Jobs,
                    () => CharacterDetailsReader.DescribeGearsets(entries.AsSpan(0, 1), 0, 73, true, metadata, JobName)[0].Items!,
                    () => CharacterDetailsReader.DescribeGearsets(
                        setId.HasValue ? entries.AsSpan(setId.Value, 1) : entries.AsSpan(), setId ?? 0, 73,
                        setId.HasValue, metadata, JobName));
                var expected = JsonSerializer.Serialize(baseline.Query(arguments));
                var actual = JsonSerializer.Serialize(new CharacterDetailsLookup(reading).Query(arguments));
                Check(expected == actual, "requested wire result unchanged");
                var descriptions = reading.Equipped?.Length ?? reading.Gearsets?.Sum(x => x.Items?.Length ?? 0) ?? 0;
                Check(after.Items == (view is "gearset" or "equipped" ? 14 : 0), "requested item work only");
                Check(after.Materia == (descriptions > 0 ? 5 : 0), "unique materia work only");
                Console.WriteLine($"WORK {view}: item descriptions 1414 -> {descriptions}; item metadata {before.Items} -> {after.Items}; materia metadata {before.Materia} -> {after.Materia}");
            }
            var sharedWork = new Work();
            var sharedMetadata = sharedWork.Metadata();
            foreach (var id in new[] { 73, 74, 0 })
                _ = CharacterDetailsReader.DescribeGearsets(entries.AsSpan(id, 1), id, 73, true, sharedMetadata, JobName);
            Check(sharedWork.Items == 14 && sharedWork.Materia == 5, "metadata shared across requested sets and equipped section");
        });
        Console.WriteLine($"Passed {passed} character-details cost checks.");
    }

    private static RaptureGearsetModule.GearsetEntry[] NativeSets()
    {
        var entries = new RaptureGearsetModule.GearsetEntry[100];
        for (var index = 0; index < entries.Length; index++)
        {
            ref var entry = ref entries[index];
            entry.Id = (byte)index;
            entry.ClassJob = 1;
            entry.Flags = RaptureGearsetModule.GearsetFlag.Exists;
            Encoding.UTF8.GetBytes($"Fixture {index}").CopyTo(entry.Name);
            for (var slot = 0; slot < 14; slot++)
            {
                ref var item = ref entry.Items[slot];
                item.ItemId = (uint)(100 + slot + (slot == 0 && index % 2 == 1 ? 1000000 : 0));
                for (var materiaSlot = 0; materiaSlot < 5; materiaSlot++)
                {
                    item.Materia[materiaSlot] = (ushort)(27 + materiaSlot);
                    item.MateriaGrades[materiaSlot] = 9;
                }
            }
        }
        return entries;
    }

    private sealed class Work
    {
        internal int Items;
        internal int Materia;
        internal CharacterDetailsMetadata Metadata() => new(id => { this.Items++; return ($"Item {id}", 700); },
            (id, grade) => { this.Materia++; return (id + 100, $"Materia {id}/{grade}"); });
    }

    private static string JobName(uint id) => $"Job {id}";
    private static JsonElement Args(string view, int? gearsetId = null, int offset = 0) =>
        JsonSerializer.SerializeToElement(new { view, gearset_id = gearsetId, offset, limit = 20 });
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
