using System.Collections.Immutable;
using System.Text.Json;
using CompAI;

var tests = new (string Name, Action Run)[]
{
    ("summary contains counts and timestamp only", Summary),
    ("full catalog is reachable by pagination without question matching", FullCatalog),
    ("active and completed views preserve independent flags", Views),
    ("name search supports case-insensitive AND terms", Names),
    ("native and sheet IDs resolve exactly; missing is source-wide", Ids),
    ("unknown and negative completion remain distinct", Unknown),
    ("byte budget preserves pagination and explicit text omissions", Budget),
    ("invalid types, fields, duplicate properties and aliases fail closed", Invalid),
    ("past-end and empty pages are final", Empty),
    ("managed snapshot and historical metadata remain unchanged", Frozen),
};
foreach (var test in tests) { test.Run(); Console.WriteLine("PASS " + test.Name); }
if (args.Length == 2 && args[0] == "--fixture")
{
    var snapshot = Large();
    var wide = Wide();
    File.WriteAllText(args[1], JsonSerializer.Serialize(new {
        summary = QuestLookup.CreateSummary(snapshot, true),
        results = new[] {
            Lookup(snapshot, limit: 50), Lookup(snapshot, view: "completed", ids: new uint[] { 1, 2, 65536 + 4, 9999 }),
            Lookup(snapshot, query: "phantoms fillet"), Lookup(wide, limit: 50),
            Lookup(snapshot, offset: int.MaxValue),
            Lookup(Snapshot(new[] { Quest(6009) with { RowId = null, Completed = null, Accepted = true, Sequence = 2 } })),
        },
    }));
}
Console.WriteLine($"{tests.Length} quest lookup groups passed (managed synthetic snapshots only).");

static void Summary()
{
    var result = QuestLookup.CreateSummary(Large(), true);
    Check(result.catalog_rows == 5533 && result.observed_quest_count == 5533 && result.active_count == 3 &&
        result.observed_completed_count == 2766 && result.stale && result.scope == "ordinary_quests_read_only_lookup", "Summary metadata");
    using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result));
    Check(doc.RootElement.EnumerateObject().Count() == 7 && !doc.RootElement.TryGetProperty("rows", out _), "Summary has no rows");
}

static void FullCatalog()
{
    var snapshot = Large();
    var ids = new List<ushort>();
    var offset = 0;
    do
    {
        var result = Lookup(snapshot, offset: offset, limit: 50);
        Check(result.total_matches == 5533, "Lookup did not prefilter the full snapshot");
        ids.AddRange(result.rows.Select(q => q.native_id));
        if (result.next_offset is not { } next) break;
        Check(next > offset && next == offset + result.rows.Length, "Pagination must make exact progress");
        offset = next;
    } while (true);
    Check(ids.SequenceEqual(Enumerable.Range(1, 5533).Select(i => (ushort)i)), "Pagination loses or duplicates rows");
}

static void Views()
{
    var snapshot = Large();
    var active = Lookup(snapshot, view: "active");
    Check(active.total_matches == 3 && active.rows.All(q => q.accepted), "Active view");
    var completed = Lookup(snapshot, view: "completed");
    Check(completed.total_matches == 2766 && completed.rows.All(q => q.completed is true), "Completed means positive observation only");
    Check(active.rows.Single(q => q.native_id == 2).completed is true, "Accepted repeatable quest can be completed too");
}

static void Names()
{
    var result = Lookup(Large(), query: "  FILLET\tphantoms  ");
    Check(result.total_matches == 1 && result.rows[0].native_id == 100, "AND terms/case/whitespace");
    Check(Lookup(Large(), query: "phantoms missing").rows.Length == 0, "All terms required");
    Check(Lookup(Large(), query: "antoms").rows.Single().native_id == 100, "Substring search");
    var unicode = Snapshot(new[] { Quest(1) with { Name = "Țară Știință" } });
    Check(Lookup(unicode, query: "țară știință").rows.Length == 1, "Romanian case matching");
}

static void Ids()
{
    var result = Lookup(Large(), view: "completed", ids: new uint[] { 1, 2, 65536 + 4, 9999 });
    Check(result.rows.Select(q => q.native_id).SequenceEqual(new ushort[] { 2, 4 }), "Native/row IDs and view filter");
    Check(result.missing_ids.SequenceEqual(new uint[] { 9999 }), "Filtered native ID1 exists; must not be marked absent");
    Check(Lookup(Large(), ids: new uint[] { 65536 + 100 }).rows.Single().native_id == 100, "Exact row ID");
    var unmapped = Snapshot(new[] { Quest(6009) with { RowId = null, Completed = null } });
    Check(Lookup(unmapped, ids: new uint[] { 6009 }).rows.Length == 1, "Unmapped active native ID can resolve");
    Check(Lookup(unmapped, ids: new uint[] { 65536 + 6009 }).missing_ids.Length == 1, "Do not invent row mapping");
}

static void Unknown()
{
    var snapshot = Snapshot(new[] { Quest(1), Quest(2), Quest(6009) with { RowId = null, Completed = null } });
    var result = Lookup(snapshot);
    Check(result.rows[0].completed is false && result.rows[1].completed is true && result.rows[2].completed is null,
        "Tri-state completion preserved");
    Check(Lookup(snapshot, view: "completed").total_matches == 1, "Unknown is not completed");
}

static void Budget()
{
    var snapshot = Wide();
    var first = Lookup(snapshot, limit: 50);
    Check(JsonSerializer.SerializeToUtf8Bytes(first).Length <= QuestLookup.MaxBytes, "Escaped response budget");
    Check(first.rows.Length > 0 && first.rows.Length < 50 && first.next_offset == first.rows.Length, "Budget shrinks page but preserves continuation");
    Check(first.rows.All(q => q.journal_text is null && q.name_truncated), "Oversized names explicitly truncated");
    Check(first.rows.Where(q => q.accepted).All(q => q.journal_status == "omitted"), "Budget-dropped paragraphs labeled omitted");
    var second = Lookup(snapshot, offset: first.next_offset!.Value, limit: 50);
    Check(second.rows[0].native_id == first.rows[^1].native_id + 1, "No row skipped at budget boundary");
    Check(JsonSerializer.SerializeToUtf8Bytes(second).Length <= QuestLookup.MaxBytes, "Later pages bounded too");
}

static void Invalid()
{
    var valid = "{\"view\":\"all\",\"query\":\"\",\"ids\":[],\"offset\":0,\"limit\":20}";
    var invalid = new[] {
        "null", "[]", "{}", valid.Replace("\"all\"", "\"other\""), valid.Replace("\"view\"", "\"View\""),
        valid.Replace("\"query\":\"\"", "\"query\":null"), valid.Replace("\"query\":\"\"", "\"query\":123"),
        valid.Replace("\"query\":\"\"", "\"query\":\"" + new string('x', 201) + "\""),
        valid.Replace("\"query\":\"\"", "\"query\":\"\\u0000\""),
        valid.Replace("\"offset\":0", "\"offset\":-1"), valid.Replace("\"offset\":0", "\"offset\":1.0"),
        valid.Replace("\"offset\":0", "\"offset\":true"), valid.Replace("\"offset\":0", "\"offset\":2147483648"),
        valid.Replace("\"limit\":20", "\"limit\":0"), valid.Replace("\"limit\":20", "\"limit\":51"),
        valid.Replace("\"limit\":20", "\"limit\":\"20\""), valid.Replace("[]", "null"), valid.Replace("[]", "[1,1]"),
        valid.Replace("[]", "[1,65537]"), valid.Replace("[]", "[0]"), valid.Replace("[]", "[65536]"),
        valid.Replace("[]", "[131072]"), valid.Replace("[]", "[true]"), valid.Replace("[]", "[1.0]"),
        valid.Replace("[]", "[" + string.Join(',', Enumerable.Range(1, 51)) + "]"),
        valid.Replace("[]", "[1]").Replace("\"query\":\"\"", "\"query\":\"name\""),
        valid.Replace("}", ",\"extra\":true}"), valid.Replace("}", ",\"limit\":20}"),
        valid.Replace("\"query\":\"\",", ""),
    };
    foreach (var json in invalid)
    {
        using var document = JsonDocument.Parse(json);
        try { QuestLookup.Query(Large(), false, document.RootElement); }
        catch (ArgumentException) { continue; }
        throw new Exception("Invalid request was accepted: " + json);
    }
}

static void Empty()
{
    var empty = Lookup(Snapshot(Array.Empty<QuestObservation>()));
    Check(empty.total_matches == 0 && empty.rows.Length == 0 && empty.next_offset is null, "Empty snapshot");
    var past = Lookup(Large(), offset: int.MaxValue);
    Check(past.rows.Length == 0 && past.offset == int.MaxValue && past.next_offset is null, "Past end without overflow");
}

static void Frozen()
{
    var snapshot = Large();
    var original = JsonSerializer.Serialize(snapshot);
    var first = Lookup(snapshot, ids: new uint[] { 2 });
    var changed = snapshot with { Quests = ImmutableArray<QuestObservation>.Empty };
    var second = Lookup(snapshot, ids: new uint[] { 2 });
    Check(first.captured_at_utc == second.captured_at_utc && first.rows.SequenceEqual(second.rows) && first.stale,
        "Original frozen snapshot remains stable");
    Check(JsonSerializer.Serialize(snapshot) == original && changed.Quests.Length == 0, "Query must not mutate snapshots");
    var text = JsonSerializer.Serialize(first);
    Check(!text.Contains("ContentId") && !text.Contains("Variables") && !text.Contains("Flags"), "No identity/raw variables transmitted");
}

static QuestSnapshot Large() => Snapshot(Enumerable.Range(1, 5533).Select(i => Quest(i) with {
    Name = i == 100 ? "Phantoms to Fillet" : "Synthetic Quest " + i,
    Accepted = i <= 3, Sequence = i <= 3 ? (byte)3 : null,
}));
static QuestSnapshot Wide() => Snapshot(Enumerable.Range(1, 80).Select(i => Quest(i) with {
    Name = new string('界', 300) + i, Accepted = i <= 30, Sequence = i <= 30 ? (byte)3 : null,
    JournalText = new string('界', 500),
}));
static QuestObservation Quest(int id) => new(65536u + (uint)id, (ushort)id, "Synthetic " + id,
    false, false, id % 2 == 0, null, null, null, ImmutableArray<byte>.Empty, null);
static QuestSnapshot Snapshot(IEnumerable<QuestObservation> rows)
{
    var quests = rows.ToImmutableArray();
    return new(DateTimeOffset.UtcNow.AddMinutes(-5), 0, "synthetic", "limited", "limited", null,
        quests.Length, quests.Length, 30, quests);
}
static QuestLookupResult Lookup(QuestSnapshot snapshot, string view = "all", string query = "", uint[]? ids = null,
    int offset = 0, int limit = 20)
{
    using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { view, query, ids = ids ?? Array.Empty<uint>(), offset, limit }));
    return QuestLookup.Query(snapshot, true, doc.RootElement);
}
static void Check(bool condition, string note) { if (!condition) throw new Exception(note); }
