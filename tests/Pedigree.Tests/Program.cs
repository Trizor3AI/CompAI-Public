using CompAI;
using System.Text.Json;

var tests = new (string, Action)[]
{
    ("all 54 known item identities and strict range", ItemIdentity),
    ("all 1-4 star pairs decode from foreground colors", AllValues),
    ("paired and split nodes follow coordinates, not node IDs or enumeration", Layouts),
    ("localized labels retain canonical stat order", LocalLabels),
    ("duplicates, missing labels and ambiguous labels fail closed", LabelsFail),
    ("unknown colors, mixed sexes and malformed star groups fail closed", ColorsFail),
    ("unknown, unrelated and inconsistent tooltip text fails closed", TextFails),
    ("nearest-row and horizontal bounds reject ambiguous fragments", CoordinatesFail),
    ("same slot and item never reuse a prior decoding", NoIdentityCache),
    ("captured G6-F diagnostic decodes its shared-origin parentage rows", CapturedDiagnostic),
    ("shared label origin does not permit ambiguous or leftward star fragments", EqualOriginBoundaries),
};
var failed = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
return failed == 0 ? 0 : 1;

static string[] Labels() => ["Maximum Speed", "Acceleration", "Endurance", "Stamina", "Cunning"];

static TooltipRun[] Group(bool male, int stars)
{
    ushort bright = male ? (ushort)529 : (ushort)531;
    ushort dim = male ? (ushort)530 : (ushort)532;
    return [new(male ? "♂" : "♀", 0), new(new string('★', 4 - stars), dim),
        new(new string('★', stars), bright)];
}

static TooltipText Node(uint id, float x, float y, params TooltipRun[] runs) =>
    new(id, x, y, string.Concat(runs.Select(run => run.Text)), [], runs);

static HoveredChocoboTooltip Sample(int father = 2, int mother = 3, int layout = 0,
    string[]? labels = null)
{
    labels ??= Labels();
    var nodes = new List<TooltipText>();
    for (var i = 0; i < 5; i++)
    {
        var y = 100 + i * 22;
        var males = Group(true, father);
        var females = Group(false, mother);
        if (layout == 0)
            nodes.Add(Node(7, 10, y, [new(labels[i] + ": ", 0), ..males, new(" ", 0), ..females]));
        else
        {
            nodes.Add(Node(7, 10, y, new TooltipRun(labels[i], 0)));
            if (layout == 1)
                nodes.Add(Node(7, 130, y + 2, [..males, new(" ", 0), ..females]));
            else
            {
                nodes.Add(Node(7, 130, y + 2, males));
                nodes.Add(Node(7, 200, y - 1, females));
            }
        }
    }
    return new("Inventory2", 3, 9614, 9614, "Covering Permission G9-F", 0,
        DateTimeOffset.Parse("2026-10-02T12:00:00Z"), nodes.ToArray(), labels);
}

static PedigreeReading Decode(HoveredChocoboTooltip sample)
{
    Check(PedigreeDecoder.TryDecode(sample, out var reading, out var reason), "decode failed: " + reason);
    Check(reading is not null, "successful decoding needs a reading");
    return reading!;
}

static void Reject(HoveredChocoboTooltip sample, string context)
{
    Check(!PedigreeDecoder.TryDecode(sample, out var result, out var reason), "unexpected acceptance: " + context);
    Check(result is null && !string.IsNullOrWhiteSpace(reason), "failed decoding must have no result and a reason");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static HoveredChocoboTooltip ChangeNode(HoveredChocoboTooltip sample, int index, TooltipText node) =>
    sample with { Nodes = sample.Nodes.Select((old, i) => i == index ? node : old).ToArray() };

static void ItemIdentity()
{
    for (uint id = 9561; id <= 9614; id++)
    {
        Check(PedigreeDecoder.TryClassify(id, out var kind, out var grade, out var sex), "known item rejected");
        Check(grade == (id - 9561) % 9 + 1, "grade mismatch");
        Check(sex == ((id - 9561) % 18 < 9 ? "male" : "female"), "sex mismatch");
        Check(kind == (id < 9579 ? "fledgling" : id < 9597 ? "retired_registration" : "covering_permission"),
            "kind mismatch");
        var read = Decode(Sample() with { ItemId = id, BaseItemId = id });
        Check(read.Kind == kind && read.Grade == grade && read.Sex == sex, "reading identity mismatch");
    }
    foreach (uint id in new uint[] { 0, 9560, 9615, 1009561, uint.MaxValue })
    {
        Check(!PedigreeDecoder.TryClassify(id, out _, out _, out _), "unknown item classified");
        Reject(Sample() with { ItemId = id, BaseItemId = id }, "unknown item");
    }
    Reject(Sample() with { ItemId = 1009614 }, "item/base ID mismatch");
}

static void AllValues()
{
    for (var father = 1; father <= 4; father++)
    for (var mother = 1; mother <= 4; mother++)
    {
        var read = Decode(Sample(father, mother));
        Check(read.FatherStars.SequenceEqual(Enumerable.Repeat(father, 5)), "male bright count mismatch");
        Check(read.MotherStars.SequenceEqual(Enumerable.Repeat(mother, 5)), "female bright count mismatch");
    }
}

static void Layouts()
{
    for (var layout = 0; layout <= 2; layout++)
    {
        var sample = Sample(1, 4, layout);
        sample = sample with { Nodes = sample.Nodes.Reverse().ToArray() };
        var reading = Decode(sample);
        Check(reading.FatherStars.All(value => value == 1) && reading.MotherStars.All(value => value == 4),
            "node enumeration changed the result");
    }
    var reordered = Sample(2, 3, 2);
    // Sex markers identify parents even if the two columns are reversed.
    reordered = reordered with { Nodes = reordered.Nodes.Select((node, i) => node with
        { X = i % 3 == 1 ? 200 : i % 3 == 2 ? 130 : 10 }).ToArray() };
    Check(Decode(reordered).FatherStars.All(value => value == 2), "sex columns cannot determine parent identity");
}

static void LocalLabels()
{
    var labels = new[] { "最高速度", "Beschleunigung", "Endurance", "Enduranță", "Ruse" };
    var sample = Sample(labels: labels);
    sample = sample with { Nodes = sample.Nodes.Select((node, stat) =>
        Node(node.NodeId, node.X, 200 - stat * 22,
            [new(labels[stat] + " ", 0), ..Group(true, stat % 4 + 1), ..Group(false, 4 - stat % 4)])).ToArray() };
    var reading = Decode(sample);
    Check(reading.FatherStars.SequenceEqual(new[] { 1, 2, 3, 4, 1 }), "localized labels/order lost");
}

static void LabelsFail()
{
    var sample = Sample();
    Reject(sample with { Nodes = sample.Nodes.Skip(1).ToArray() }, "missing label");
    Reject(sample with { Nodes = [..sample.Nodes, sample.Nodes[0] with { Y = 300 }] }, "duplicate label");
    Reject(sample with { StatLabels = ["Speed", "Speed", "Endurance", "Stamina", "Cunning"] }, "duplicate label definitions");
    Reject(sample with { StatLabels = ["", "Acceleration", "Endurance", "Stamina", "Cunning"] }, "empty label definition");
    Reject(sample with { StatLabels = Labels().Take(4).ToArray() }, "missing label definition");
    var ambiguous = Sample(labels: ["Speed", "Speed detail", "Endurance", "Stamina", "Cunning"]);
    Reject(ambiguous, "label prefixes overlap");
    Reject(ChangeNode(sample, 0, Node(1, 10, 100, new TooltipRun("Maximum Speed: 100", 0))),
        "own stat value is not pedigree");
}

static void ColorsFail()
{
    var sample = Sample();
    foreach (ushort color in new ushort[] { 0, 1, 530, 531, 532, ushort.MaxValue })
    {
        var runs = sample.Nodes[0].Runs.Select(run => run.Color == 529 ? run with { Color = color } : run).ToArray();
        Reject(ChangeNode(sample, 0, Node(7, 10, 100, runs)), "unknown/mis-sexed or missing bright color " + color);
    }
    Reject(ChangeNode(sample, 0, Node(7, 10, 100,
        [new("Maximum Speed ", 0), ..Group(true, 2), ..Group(true, 3)])), "duplicate male group");
    Reject(ChangeNode(sample, 0, Node(7, 10, 100,
        [new("Maximum Speed ", 0), new("♂", 0), new("★★", 529), new("★★", 530), ..Group(false, 3)])),
        "bright stars followed by dim stars");
    Reject(ChangeNode(sample, 0, Node(7, 10, 100,
        [new("Maximum Speed ", 0), ..Group(true, 0), ..Group(false, 3)])), "zero stars");
    Reject(ChangeNode(sample, 0, Node(7, 10, 100,
        [new("Maximum Speed ", 0), ..Group(true, 2), new("★", 529), ..Group(false, 3)])), "fifth star");
    Reject(ChangeNode(sample, 0, Node(7, 10, 100,
        [new("Maximum Speed ", 0), new("★★★★", 529), new("★★★★", 531)])), "missing sex markers");
    Reject(ChangeNode(sample, 0, Node(7, 10, 100,
        [new("Maximum Speed ", 0), new("♂", 999), new("★★★★", 529), ..Group(false, 3)])), "unknown marker color");
}

static void TextFails()
{
    var sample = Sample();
    Reject(ChangeNode(sample, 0, Node(7, 10, 100, new TooltipRun("Maximum Speed ♂??? ♀???", 0))), "unknown pedigree");
    Reject(sample with { Nodes = [..sample.Nodes, Node(99, 130, 400, new TooltipRun("★", 529))] }, "unassigned star");
    Reject(ChangeNode(sample, 0, sample.Nodes[0] with { Runs = [] }), "missing styles");
    Reject(ChangeNode(sample, 0, sample.Nodes[0] with { Text = sample.Nodes[0].Text + " " }), "text/style mismatch");
    Reject(ChangeNode(sample, 0, Node(7, 10, 100,
        [new("Maximum Speed ", 0), ..Group(true, 2), new(" / ", 0), ..Group(false, 3)])), "unrecognized separator");
}

static void CoordinatesFail()
{
    var sample = Sample(layout: 2);
    Reject(ChangeNode(sample, 1, sample.Nodes[1] with { X = 5 }), "stars left of label");
    Reject(ChangeNode(sample, 2, sample.Nodes[2] with { X = sample.Nodes[1].X }), "overlapping groups");
    Reject(ChangeNode(sample, 1, sample.Nodes[1] with { Y = 111 }), "between stat rows");
    Reject(ChangeNode(sample, 1, sample.Nodes[1] with { Y = 109.1f }), "beyond nearest-row spacing limit");
    Reject(ChangeNode(sample, 0, sample.Nodes[0] with { Y = sample.Nodes[3].Y }), "overlapping labels");
    Reject(ChangeNode(sample, 1, sample.Nodes[1] with { X = float.NaN }), "nonfinite coordinate");
    var sparse = sample with { Nodes = sample.Nodes.Select((node, index) =>
        node with { Y = 100 + index / 3 * 100 + (index % 3 == 0 ? 0 : 11) }).ToArray() };
    Reject(sparse, "10px hard limit even for widely separated rows");
}

static void NoIdentityCache()
{
    var first = Decode(Sample(1, 2));
    var second = Decode(Sample(4, 3));
    Check(first.FatherStars.All(value => value == 1) && second.FatherStars.All(value => value == 4),
        "same slot/item must decode independently each time");
}

static HoveredChocoboTooltip LoadCapturedDiagnostic()
{
    // This regression fixture preserves the supplied diagnostic's layout and color runs;
    // capture time and slot are synthetic. Encoded SeStrings are deliberately omitted.
    var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "covering-permission-g6-f-shared-origin.json");
    using var document = JsonDocument.Parse(File.ReadAllText(path));
    var root = document.RootElement;
    var nodes = root.GetProperty("nodes").EnumerateArray().Select(node => new TooltipText(
        node.GetProperty("NodeId").GetUInt32(), node.GetProperty("X").GetSingle(),
        node.GetProperty("Y").GetSingle(), node.GetProperty("Text").GetString()!, [],
        node.GetProperty("Runs").EnumerateArray().Select(run => new TooltipRun(
            run.GetProperty("Text").GetString()!, run.GetProperty("Color").GetUInt16())).ToArray())).ToArray();
    var itemId = root.GetProperty("ItemId").GetUInt32();
    return new HoveredChocoboTooltip(root.GetProperty("Bag").GetString()!,
        root.GetProperty("Slot").GetUInt32(), itemId, itemId, "Covering Permission G6-F", 1,
        root.GetProperty("CapturedAtUtc").GetDateTimeOffset(), nodes,
        root.GetProperty("StatLabels").EnumerateArray().Select(label => label.GetString()!).ToArray());
}

static void CapturedDiagnostic()
{
    var sample = LoadCapturedDiagnostic();
    var reading = Decode(sample);
    Check(reading.Kind == "covering_permission" && reading.Grade == 6 && reading.Sex == "female",
        "captured item classification");
    Check(reading.FatherStars.SequenceEqual(new[] { 4, 2, 2, 1, 1 }) &&
        reading.MotherStars.SequenceEqual(new[] { 1, 4, 4, 4, 2 }), "captured parentage differs from its colored rows");
    var reversed = Decode(sample with { Nodes = sample.Nodes.Reverse().ToArray() });
    Check(reversed.FatherStars.SequenceEqual(reading.FatherStars) &&
        reversed.MotherStars.SequenceEqual(reading.MotherStars), "capture enumeration must not determine stat order");
}

static void EqualOriginBoundaries()
{
    var sample = LoadCapturedDiagnostic();
    var pairedIndex = Array.FindIndex(sample.Nodes, node => node.Text.StartsWith('♂'));
    var paired = sample.Nodes[pairedIndex];
    var labelIndex = Array.FindIndex(sample.Nodes, node => node.Text == "Cunning");
    Check(paired.X == sample.Nodes[labelIndex].X, "fixture must exercise equal text-box origins");
    Reject(ChangeNode(sample, pairedIndex, paired with { X = paired.X - 0.25f }), "actually leftward star fragment");
    Reject(sample with { Nodes = [..sample.Nodes, paired] }, "duplicate shared-origin pair");
    Reject(sample with { Nodes = [..sample.Nodes, paired with { X = paired.X + 2 }] }, "duplicate pair with distinct origin");
    Reject(sample with { Nodes = [..sample.Nodes, sample.Nodes[labelIndex]] }, "duplicate shared-origin label");
    var unknownColors = paired.Runs.Select(run => run.Color == 529 ? run with { Color = 999 } : run).ToArray();
    Reject(ChangeNode(sample, pairedIndex, paired with { Runs = unknownColors }), "unknown colors at allowed origin");

    var split = Sample(2, 3, layout: 2);
    split = split with { Nodes = split.Nodes.Select(node => node with { X = 10 }).ToArray() };
    Reject(split, "two separate sex groups sharing an origin have no known reading order");
    var pairedSynthetic = Sample(2, 3, layout: 1);
    pairedSynthetic = pairedSynthetic with { Nodes = pairedSynthetic.Nodes.Select(node => node with { X = 10 }).ToArray() };
    Check(Decode(pairedSynthetic).FatherStars.All(value => value == 2),
        "one paired-star text node may share its label origin");
}
