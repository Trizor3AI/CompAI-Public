using CompAI;
using System.Text;
using System.Text.Json;

var tests = new (string, Action)[]
{
    ("stable copies require at least 200ms and handle backwards time", StableInterval),
    ("switching same-ID cards resets settling and preserves distinct slots", SlotSwitch),
    ("changed readings immediately revoke old parentage and wait for stable replacement", ChangedReadings),
    ("attachment requires exact bag, slot, item, quantity and quality", AttachmentIdentity),
    ("clear and reset remove pending observations", ClearAndReset),
    ("unknown layouts and unsupported items cannot attach", UnknownLayouts),
    ("attachment arrays are defensive copies", DefensiveAttachments),
    ("scan views and accepted input do not expose mutable store arrays", DefensiveViewsAndInput),
    ("wire serialization describes automatic decode without claiming user verification", WireShape),
    ("only decoded evidence changes invalidate an inventory capture", AttachmentRevisions),
};
var failed = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
return failed == 0 ? 0 : 1;

static DateTimeOffset Epoch() => DateTimeOffset.Parse("2026-10-02T12:00:00Z");

static HoveredChocoboTooltip Sample(int father = 2, int mother = 3, uint slot = 3, int ms = 0)
{
    string[] labels = ["Maximum Speed", "Acceleration", "Endurance", "Stamina", "Cunning"];
    var nodes = labels.Select((label, stat) =>
    {
        TooltipRun[] runs = [new(label + " ♂", 0), new(new string('★', 4 - father), 530),
            new(new string('★', father), 529), new(" ♀", 0), new(new string('★', 4 - mother), 532),
            new(new string('★', mother), 531)];
        var text = string.Concat(runs.Select(run => run.Text));
        return new TooltipText((uint)stat + 1, 10, 100 + stat * 22, text, Encoding.UTF8.GetBytes(text), runs);
    }).ToArray();
    return new("Inventory2", slot, 9614, 9614, "Covering Permission G9-F", 0,
        Epoch().AddMilliseconds(ms), nodes, labels);
}

static PedigreeCard Save(PedigreeScanStore store, HoveredChocoboTooltip sample)
{
    store.Observe(sample);
    store.Observe(sample with { CapturedAtUtc = sample.CapturedAtUtc.AddMilliseconds(200) });
    return store.Cards.Single(card => card.Sample.Bag == sample.Bag && card.Sample.Slot == sample.Slot);
}

static ObservedPedigree? Attach(PedigreeScanStore store, uint slot = 3) =>
    store.Attachment("Inventory2", slot, 9614, 9614, 1, false);

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void StableInterval()
{
    var store = new PedigreeScanStore();
    store.Observe(Sample());
    Check(store.Cards.Length == 0, "first frame must not save a card");
    store.Observe(Sample(ms: 199));
    Check(store.Cards.Length == 0, "199ms is too short");
    store.Observe(Sample(ms: 200));
    Check(store.Cards.Length == 1 && store.Cards[0].Reading is not null, "200ms saves a complete decoded card");
    Check(Attach(store)?.Status == "decoded_tooltip", "complete stars attach automatically without a user checkbox");

    store.Clear();
    store.Observe(Sample(ms: 500));
    store.Observe(Sample(ms: 200));
    store.Observe(Sample(ms: 399));
    Check(store.Cards.Length == 0, "backwards time restarts stability interval");
    store.Observe(Sample(ms: 400));
    Check(store.Cards.Length == 1, "card settles after restarted interval");

    store.Clear();
    store.Observe(Sample());
    var moved = Sample(ms: 200);
    moved = moved with { Nodes = moved.Nodes.Select(node => node with { X = node.X + 100, Y = node.Y + 40 }).ToArray() };
    store.Observe(moved);
    Check(store.Cards.Length == 1, "moving the whole tooltip must not reset content stability");
}

static void SlotSwitch()
{
    var store = new PedigreeScanStore();
    store.Observe(Sample(slot: 2));
    store.Observe(Sample(slot: 3, ms: 250));
    Check(store.Cards.Length == 0, "same item ID in another slot restarts settling");
    store.Observe(Sample(slot: 2, ms: 450));
    Check(store.Cards.Length == 0, "returning to previous slot still restarts settling");
    store.Observe(Sample(slot: 2, ms: 650));
    var second = Save(store, Sample(4, 1, slot: 3, ms: 700));
    Check(store.Cards.Length == 2 && second.Reading is not null, "same-ID cards are retained independently");
    Check(Attach(store, 2)?.FatherStars.All(value => value == 2) == true && Attach(store, 3)?.FatherStars.All(value => value == 4) == true,
        "each same-ID card must attach only its own slot-bound reading");
    Check(Attach(store, 3)?.FatherStars.All(value => value == 4) == true, "second card attaches its own reading");
    Check(store.Cards.Select(card => card.Sample.Slot).SequenceEqual(new uint[] { 2, 3 }), "slot ordering");
}

static void ChangedReadings()
{
    var store = new PedigreeScanStore();
    Save(store, Sample());
    store.Observe(Sample(ms: 400));
    Check(Attach(store) is not null, "identical repeat retains decoded evidence");
    store.Observe(Sample(father: 4, ms: 500));
    Check(Attach(store) is null, "changed content must revoke old parentage immediately while settling");
    store.Observe(Sample(father: 4, ms: 700));
    var second = store.Cards.Single();
    Check(second.Reading!.FatherStars.All(value => value == 4), "stable changed reading is decoded independently");
    Check(Attach(store)?.FatherStars.All(value => value == 4) == true, "stable replacement attaches automatically");
}

static void AttachmentIdentity()
{
    var store = new PedigreeScanStore();
    Save(store, Sample());
    Check(Attach(store) is not null, "exact identity attaches");
    foreach (var wrong in new[]
    {
        store.Attachment("Inventory1", 3, 9614, 9614, 1, false),
        store.Attachment("Inventory2", 4, 9614, 9614, 1, false),
        store.Attachment("Inventory2", 3, 9605, 9614, 1, false),
        store.Attachment("Inventory2", 3, 9614, 9605, 1, false),
        store.Attachment("Inventory2", 3, 9614, 9614, 0, false),
        store.Attachment("Inventory2", 3, 9614, 9614, 2, false),
        store.Attachment("Inventory2", 3, 9614, 9614, 1, true),
    }) Check(wrong is null, "mismatched inventory identity attached");
}

static void ClearAndReset()
{
    var store = new PedigreeScanStore();
    Save(store, Sample());
    store.Observe(Sample(slot: 4, ms: 400));
    store.Clear();
    Check(store.Cards.Length == 0 && Attach(store) is null, "clear drops all saved readings");
    store.Observe(Sample(slot: 4, ms: 600));
    Check(store.Cards.Length == 0, "clear also drops the prior pending frame");
    store.ResetPending();
    store.Observe(Sample(slot: 4, ms: 800));
    Check(store.Cards.Length == 0, "reset alone drops a pending frame");
    store.Observe(Sample(slot: 4, ms: 1000));
    Check(store.Cards.Length == 1, "capture resumes after a fresh stable pair");
}

static void UnknownLayouts()
{
    var store = new PedigreeScanStore();
    var sample = Sample();
    var unknown = sample with { Nodes = sample.Nodes.Skip(1).ToArray() };
    var card = Save(store, unknown);
    Check(card.Reading is null && !string.IsNullOrWhiteSpace(card.Reason), "unknown layout saved for diagnosis only");
    Check(Attach(store) is null, "unknown layout never attaches");
    store.Clear();
    Save(store, Sample());
    store.Observe(unknown with { CapturedAtUtc = Epoch().AddMilliseconds(400) });
    Check(Attach(store) is null, "unrecognized replacement immediately revokes a formerly decoded card");
    store.Clear();
    foreach (var unsupported in new[]
    {
        Sample() with { Bag = "Retainer1" }, Sample() with { Slot = 35 },
        Sample() with { ItemId = 1009614 }, Sample() with { ItemId = 1, BaseItemId = 1 },
    })
    {
        store.Observe(unsupported);
        store.Observe(unsupported with { CapturedAtUtc = Epoch().AddMilliseconds(300) });
        Check(store.Cards.Length == 0, "unsupported item entered capture store");
    }
}

static void DefensiveAttachments()
{
    var store = new PedigreeScanStore();
    Save(store, Sample());
    var first = Attach(store)!;
    first.FatherStars[0] = 99;
    first.MotherStars[0] = 98;
    var second = Attach(store)!;
    Check(second.FatherStars.SequenceEqual(new[] { 2, 2, 2, 2, 2 }) &&
          second.MotherStars.SequenceEqual(new[] { 3, 3, 3, 3, 3 }), "attachment mutation reached store");
}

static void DefensiveViewsAndInput()
{
    var store = new PedigreeScanStore();
    var source = Sample();
    Save(store, source);
    var exposed = store.Cards.Single();
    exposed.Reading!.FatherStars[0] = 99;
    exposed.Reading.MotherStars[0] = 98;
    exposed.Sample.StatLabels[0] = "mutated label";
    exposed.Sample.Nodes[0].Encoded[0] = 0;
    exposed.Sample.Nodes[0].Runs[0] = new("mutated run", 0);
    source.StatLabels[1] = "mutated source label";
    source.Nodes[1].Encoded[0] = 0;
    var reading = Attach(store)!;
    Check(reading.FatherStars[0] == 2 && reading.MotherStars[0] == 3, "view mutation altered decoded stars");
    var after = store.Cards.Single();
    Check(after.Sample.StatLabels[0] == "Maximum Speed" && after.Sample.StatLabels[1] == "Acceleration",
        "view/input label mutation reached stored sample");
    Check(after.Sample.Nodes[0].Encoded[0] != 0 && after.Sample.Nodes[1].Encoded[0] != 0 &&
        after.Sample.Nodes[0].Runs[0].Text.StartsWith("Maximum Speed", StringComparison.Ordinal),
        "view/input nested node mutation reached stored sample");
}

static void WireShape()
{
    var store = new PedigreeScanStore();
    Save(store, Sample());
    using var document = JsonDocument.Parse(JsonSerializer.Serialize(Attach(store)));
    var root = document.RootElement;
    string[] names = ["source", "status", "observed_at_utc", "kind", "grade", "sex", "father_stars", "mother_stars"];
    Check(root.EnumerateObject().Select(property => property.Name).Order().SequenceEqual(names.Order()), "unexpected wire fields");
    Check(root.GetProperty("source").GetString() == "ItemDetail" &&
        root.GetProperty("status").GetString() == "decoded_tooltip" &&
        root.GetProperty("kind").GetString() == "covering_permission" &&
        root.GetProperty("grade").GetInt32() == 9 && root.GetProperty("sex").GetString() == "female",
        "wire pedigree identity");
    Check(root.GetProperty("observed_at_utc").GetDateTimeOffset() == Epoch().AddMilliseconds(200), "wire timestamp must be the observed stable sample");
    Check(root.GetProperty("father_stars").EnumerateArray().Select(value => value.GetInt32()).SequenceEqual(new[] { 2, 2, 2, 2, 2 }) &&
        root.GetProperty("mother_stars").GetArrayLength() == 5, "five canonical star values per parent");
    Check(!document.RootElement.GetRawText().Contains("Nodes", StringComparison.Ordinal) &&
        !document.RootElement.GetRawText().Contains("Encoded", StringComparison.Ordinal), "raw tooltip must stay local");
}

static void AttachmentRevisions()
{
    var store = new PedigreeScanStore();
    var initial = store.AttachmentRevision;
    store.Observe(Sample());
    Check(store.AttachmentRevision == initial && !store.HasReadings, "unsettled scan cannot add attachment evidence");
    store.Observe(Sample(ms: 200));
    var decoded = store.AttachmentRevision;
    Check(decoded != initial && store.HasReadings, "complete new decode must trigger an inventory refresh");
    store.ResetPending(); // Starting/stopping the scan resets collection, not decoded evidence.
    store.Observe(Sample(ms: 500));
    store.Observe(Sample(ms: 700));
    Check(store.AttachmentRevision == decoded && Attach(store) is not null,
        "stopping/restarting collection or identical observations must not strip an in-flight capture");
    store.Observe(Sample(father: 4, ms: 900));
    Check(store.AttachmentRevision != decoded && !store.HasReadings && Attach(store) is null,
        "contradictory evidence must invalidate a capture immediately, before settling");
    Save(store, Sample(father: 4, ms: 1100));
    var replaced = store.AttachmentRevision;
    store.Clear();
    Check(store.AttachmentRevision != replaced && !store.HasReadings, "inventory invalidation must revoke capture evidence");
}
