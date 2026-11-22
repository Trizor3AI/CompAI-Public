using System.Text.Json;
using System.Text;
using CompAI;

var captured = DateTimeOffset.Parse("2026-10-08T00:00:00Z");
var pedigree = new ObservedPedigree("ItemDetail", "user_verified_tooltip", captured,
    "covering_permission", 6, "female", [1, 2, 3, 4, 1], [4, 3, 2, 1, 4]);
var rows = new[] {
    new InventoryLookupRow("Inventory1", 0, 9611, 9611, "Covering Permission G6-F", 1, false, pedigree),
    new InventoryLookupRow("Inventory1", 1, 9611, 9611, "Covering Permission G6-F", 1, false, null),
    new InventoryLookupRow("Inventory2", 0, 42, 42, "TEST TOKEN", 3, false, null),
    new InventoryLookupRow("Inventory2", 1, 1000042, 42, "TEST TOKEN", 2, true, null),
};
var bags = new[] {
    new InventoryLookupBag("Inventory1", 35, null, 2), new InventoryLookupBag("Inventory2", 35, null, 2),
    new InventoryLookupBag("Inventory3", 35, null, 0), new InventoryLookupBag("Inventory4", null, "Unavailable", 0),
};
var lookup = new InventoryLookup(captured, true, bags, rows);
var summaryJson = JsonSerializer.Serialize(lookup.Summary);
Check(!summaryJson.Contains("TEST TOKEN") && !summaryJson.Contains("father_stars") &&
    lookup.Summary.item_slot_count == 4 && lookup.Summary.verified_pedigree_count == 1, "Summary must omit rows");
var counts = Query(lookup, query: "test token", ids: [42], limit: 1);
Check(counts.total_quantity == 5 && counts.total_matches == 2 && counts.rows.Length == 1 &&
    counts.next_offset == 1 && counts.changed && counts.containers[3].unavailable_reason is not null,
    "Quantity must span pages and preserve incomplete/historical coverage");
Check(Query(lookup, ids: [1000042]).total_quantity == 2 &&
    Query(lookup, bags: ["Inventory1"], query: "token").total_matches == 0, "Raw ID/bag filter");
Console.WriteLine("PASS summary privacy, exact filters and complete count totals");

JsonElement Exact(string locations, string bags = "[]")
{
    using var document = JsonDocument.Parse("{\"view\":\"items\",\"query\":\"\",\"ids\":[],\"bags\":" + bags +
        ",\"offset\":0,\"limit\":50,\"locations\":" + locations + "}");
    return document.RootElement.Clone();
}
var exact = lookup.Query(Exact("[{\"bag\":\"Inventory1\",\"slot\":1}]"));
Check(exact.rows.Length == 1 && exact.rows[0].slot == 1 && exact.rows[0].pedigree is null && exact.total_quantity == 1,
    "exact slot must select the individual same-ID card, preserving unknown pedigree");
Check(lookup.Query(Exact("[{\"bag\":\"Inventory1\",\"slot\":1}]", "[\"Inventory2\"]")).total_matches == 0 &&
    lookup.Query(Exact("[{\"bag\":\"Inventory1\",\"slot\":34}]")).total_matches == 0 &&
    lookup.Query(Exact("[]")).rows.Length == 4, "exact locations combine with filters and absent locations never substitute");
Check(lookup.Query(Exact("[{\"bag\":\"Inventory1\",\"slot\":1},{\"bag\":\"Inventory2\",\"slot\":0}]")).rows
    .Select(row => (row.bag, row.slot)).SequenceEqual(new[] { ("Inventory1", 1u), ("Inventory2", 0u) }),
    "multiple explicit locations retain separate bag identity");
foreach (var badLocations in new[] { "null", "{}", "[{\"bag\":\"Inventory1\",\"slot\":35}]",
    "[{\"bag\":\"Inventory1\",\"slot\":-1}]", "[{\"bag\":\"Inventory1\",\"slot\":true}]",
    "[{\"bag\":\"Retainer\",\"slot\":1}]", "[{\"bag\":\"Inventory1\",\"slot\":1,\"extra\":0}]",
    "[{\"bag\":\"Inventory1\",\"slot\":1},{\"bag\":\"Inventory1\",\"slot\":1}]" })
{
    try { lookup.Query(Exact(badLocations)); throw new Exception("Accepted invalid locations"); }
    catch (ArgumentException) { }
}
Console.WriteLine("PASS optional exact locations, intersecting filters and malformed-location rejection");

pedigree.FatherStars[0] = 4;
rows[0] = rows[0] with { quantity = 999, pedigree = null };
var cards = Query(lookup, view: "pedigrees");
Check(cards.rows.Length == 2 && cards.rows[0].slot == 0 && cards.rows[1].slot == 1 &&
    cards.rows[0].pedigree!.FatherStars[0] == 1 && cards.rows[1].pedigree is null && cards.total_quantity == 2,
    "Same item ID cannot associate pedigrees across slots, and source mutation cannot change frozen data");
cards.rows[0].pedigree!.FatherStars[0] = 4;
Check(Query(lookup, view: "pedigrees").rows[0].pedigree!.FatherStars[0] == 1,
    "Mutating returned rows cannot change frozen data");
Console.WriteLine("PASS verified slot identity and nested immutable copies");

var autoFledgling = AutoFledglingCapture();
Console.WriteLine("PASS automatically decoded fledgling pending/failed refresh, captured query and historical isolation");

var wideRows = Enumerable.Range(0, 35).Select(slot => new InventoryLookupRow("Inventory1", (uint)slot,
    9611, 9611, new string('\u0219', 200), 1, false, pedigree)).ToArray();
var wide = new InventoryLookup(captured, false, [new("Inventory1", 35, null, 35)], wideRows);
var first = Query(wide, limit: 50);
Check(JsonSerializer.SerializeToUtf8Bytes(first).Length <= InventoryLookup.MaxBytes &&
    first.rows.Length > 0 && first.rows.Length < 35 && first.next_offset == first.rows.Length,
    "Byte budget must advance pages without dropping pedigree details");
var allSlots = new List<uint>();
var offset = 0;
do {
    var page = Query(wide, offset: offset, limit: 50);
    allSlots.AddRange(page.rows.Select(row => row.slot));
    if (page.next_offset is not { } next) break;
    offset = next;
} while (true);
Check(allSlots.SequenceEqual(Enumerable.Range(0, 35).Select(i => (uint)i)), "Pagination gap");
foreach (var bad in new[] {
    "{\"view\":\"all\",\"query\":\"\",\"ids\":[],\"bags\":[\"Retainer\"],\"offset\":0,\"limit\":10}",
    "{\"view\":\"all\",\"query\":\"\",\"ids\":[42,42],\"bags\":[],\"offset\":0,\"limit\":10}",
    "{\"view\":\"all\",\"query\":\"\",\"ids\":[],\"bags\":[],\"offset\":0,\"limit\":51}",
    "{\"view\":\"all\",\"query\":\"\",\"ids\":[],\"bags\":[],\"offset\":0,\"limit\":10,\"extra\":true}",
}) {
    using var invalid = JsonDocument.Parse(bad);
    try { lookup.Query(invalid.RootElement); throw new Exception("Accepted invalid tool arguments"); }
    catch (ArgumentException) { }
}
Check(Query(lookup, offset: int.MaxValue).next_offset is null, "Past-end page");
Console.WriteLine("PASS byte-bounded pagination and invalid arguments");
if (args.Length == 2 && args[0] == "--fixture")
    File.WriteAllText(args[1], JsonSerializer.Serialize(new { summary = lookup.Summary,
        results = new[] { Query(lookup), Query(lookup, view: "pedigrees"), counts, Query(lookup, offset: int.MaxValue) },
        auto_fledgling = new { summary = autoFledgling.Summary, result = Query(autoFledgling, view: "pedigrees"),
            filtered_result = Query(autoFledgling, view: "pedigrees", query: "PRIVATE_SYNTHETIC_BIRD") } }));

static InventoryLookupResult Query(InventoryLookup lookup, string view = "all", string query = "",
    uint[]? ids = null, string[]? bags = null, int offset = 0, int limit = 50) =>
    lookup.Query(JsonSerializer.SerializeToElement(new { view, query, ids = ids ?? [], bags = bags ?? [], offset, limit }));
static void Check(bool value, string message) { if (!value) throw new Exception(message); }

static InventoryLookup AutoFledglingCapture()
{
    var stamp = DateTimeOffset.Parse("2026-10-08T01:00:00Z");
    int[] father = [1, 2, 3, 4, 1], mother = [4, 3, 2, 1, 4];
    string[] labels = ["Maximum Speed", "Acceleration", "Endurance", "Stamina", "Cunning"];
    var nodes = labels.Select((label, index) =>
    {
        TooltipRun[] runs = [new(label + " ♂", 0), new(new string('★', 4 - father[index]), 530),
            new(new string('★', father[index]), 529), new(" ♀", 0), new(new string('★', 4 - mother[index]), 532),
            new(new string('★', mother[index]), 531)];
        var text = string.Concat(runs.Select(run => run.Text));
        return new TooltipText((uint)index + 1, 10, 100 + index * 22, text, Encoding.UTF8.GetBytes(text), runs);
    }).ToArray();
    var sample = new HoveredChocoboTooltip("Inventory1", 3, 9567, 9567, "Fledgling Chocobo G7-M",
        0, stamp, nodes, labels);
    var store = new PedigreeScanStore();
    store.Observe(sample);
    store.Observe(sample with { CapturedAtUtc = stamp.AddMilliseconds(250) });
    var card = store.Cards.Single();
    var oldRows = new[] { new InventoryLookupRow("Inventory1", 3, 9567, 9567, sample.ItemName, 1, false, null) };
    var bags = new[] { new InventoryLookupBag("Inventory1", 35, null, 1), new("Inventory2", 35, null, 0),
        new("Inventory3", 35, null, 0), new("Inventory4", 35, null, 0) };
    Check(PedigreeAttachmentStatus.Create([], oldRows, true, null).BlockReason is null,
        "ordinary pending inventory capture without decoded evidence must not block chat");
    var pending = PedigreeAttachmentStatus.Create(store.Cards, oldRows, true, null);
    Check(pending.DecodedCount == 1 && pending.SavedCount == 0 && pending.BlockReason is not null &&
        pending.RefreshFailure is null, "decoded scan must not masquerade as a completed inventory attachment");
    Check(PedigreeAttachmentStatus.Create(store.Cards, [], true, null).MissingCount == 1,
        "first inventory capture must also wait for decoded genes");
    var failed = PedigreeAttachmentStatus.Create(store.Cards, oldRows, false, "Loading; showing saved inventory.");
    Check(failed.BlockReason is not null && failed.RefreshFailure!.Contains("Loading"),
        "failed refresh must stay blocked with its reason while retaining prior data");
    var historical = new InventoryLookup(stamp, true, bags, oldRows);
    Check(Query(historical, view: "pedigrees").rows.Single().pedigree is null,
        "a new decode must not mutate an already frozen older inventory request");

    // The production capture uses this exact identity-bound attachment operation,
    // then constructs inventory lookup from those saved rows at Send.
    var capturedRows = oldRows.Select(row => row with { pedigree = store.Attachment(row.bag, row.slot,
        row.item_id, row.base_item_id, row.quantity, row.is_hq) }).ToArray();
    Check(PedigreeAttachmentStatus.Create(store.Cards, capturedRows, false, null).BlockReason is null,
        "successful automatic capture must release the pending attachment gate");
    Check(PedigreeAttachmentStatus.Create(store.Cards, capturedRows, true, null).BlockReason is null,
        "an unrelated refresh must not block already attached decoded readings");
    var sent = new InventoryLookup(stamp.AddSeconds(1), false, bags, capturedRows);
    var result = Query(sent, view: "pedigrees");
    var supplied = result.rows.Single().pedigree!;
    Check(result.verified_pedigree_count == 1 && supplied.Status == "decoded_tooltip" && supplied.Kind == "fledgling" && supplied.Grade == 7 &&
        supplied.Sex == "male" && supplied.FatherStars.SequenceEqual(father) && supplied.MotherStars.SequenceEqual(mother),
        "actual frozen pedigree query lost an automatically decoded G7 male's identity or either parent row");
    var personalNameFilter = Query(sent, view: "pedigrees", query: "PRIVATE_SYNTHETIC_BIRD");
    Check(personalNameFilter.total_matches == 0 && personalNameFilter.rows.Length == 0 &&
        personalNameFilter.verified_pedigree_count == 1,
        "a bird's personal name is not an item-name match and must not alter the available pedigree count");
    using var wire = JsonDocument.Parse(JsonSerializer.Serialize(result));
    Check(wire.RootElement.GetProperty("rows")[0].GetProperty("pedigree").GetProperty("father_stars").GetArrayLength() == 5,
        "automatically decoded pedigree must be present on the tool wire result");
    var differentReading = capturedRows.Select(row => row with { pedigree = row.pedigree! with {
        FatherStars = [4, 4, 4, 4, 4] } }).ToArray();
    Check(PedigreeAttachmentStatus.Create(store.Cards, differentReading, false, null).BlockReason is not null,
        "same slot and item ID must not accept different parentage");
    var oldObservation = capturedRows.Select(row => row with { pedigree = row.pedigree! with {
        ObservedAtUtc = stamp.AddDays(-1) } }).ToArray();
    Check(PedigreeAttachmentStatus.Create(store.Cards, oldObservation, false, null).BlockReason is not null,
        "an old same-ID pedigree cannot satisfy a newly decoded observation");
    store.Clear(); // Inventory change/logout still revokes current slot-bound readings.
    Check(store.Attachment("Inventory1", 3, 9567, 9567, 1, false) is null &&
        PedigreeAttachmentStatus.Create(store.Cards, capturedRows, false, null).BlockReason is null,
        "revoked current readings must not strand chat behind the attachment gate");
    Check(Query(sent, view: "pedigrees").rows.Single().pedigree!.FatherStars.SequenceEqual(father),
        "a request already sent retains its explicitly historical frozen copy");
    return sent;
}
