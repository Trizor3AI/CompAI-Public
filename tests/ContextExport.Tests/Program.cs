using CompAI;
using System.Globalization;
using System.Text;
using System.Text.Json;

var tests = new (string, Action)[]
{
    ("same item IDs retain separate slots, names, quantities and HQ", DistinctSlots),
    ("historical snapshot remains separate from automatically decoded current scan", SeparateObservations),
    ("automatic and legacy pedigree provenance remain distinct in JSON and text", PedigreeProvenance),
    ("unavailable bags differ from known empty bags", BagAvailability),
    ("scan-only export preserves null inventory and unavailable reading", ScanOnly),
    ("star order and unknown attributes remain explicit", StarOrderAndUnknowns),
    ("Unicode and all UTC timestamps survive UTF-8 serialization", UnicodeAndUtc),
    ("export contains only the explicit data contract", DataScope),
    ("extra inventory exports preserve EventItem namespace and crystal quantities", ExtraInventory),
    ("file pair is UTF-8 and repeated exports never overwrite", FileRoundTrip),
};
var failed = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
return failed == 0 ? 0 : 1;

static DateTimeOffset Epoch() => DateTimeOffset.Parse("2026-10-02T12:00:00Z", CultureInfo.InvariantCulture);
static ExportPedigree Reading(int firstStar = 1, DateTimeOffset? observed = null) =>
    new(observed ?? Epoch().AddMinutes(-2), "Covering Permission", 4, "Female",
        ExportStarRow.From([firstStar, 2, 3, 4, 1]), ExportStarRow.From([4, 3, 2, 1, 4]), "decoded_tooltip");
static ExportInventoryItem Item(uint slot = 0, ExportPedigree? reading = null) =>
    new(slot, 9614, 9614, "Covering Permission G4-F", 1, false, reading);
static ExportPedigreeScanCard Card(ExportPedigreeCheckStatus status = ExportPedigreeCheckStatus.Decoded,
    ExportPedigree? reading = null) => new("Inventory1", 0, 9614, 9614,
        "Covering Permission G4-F", Epoch().AddMinutes(1), status,
        status == ExportPedigreeCheckStatus.Decoded ? "Complete automatic tooltip reading" : null,
        reading);
static ContextExportData Data() => new(Epoch().AddMinutes(2),
    new(Epoch(), [new("Inventory1", 35, null, [Item()])]), false, false, null,
    new(false, 0, "Stopped", []));
static JsonElement Root(ContextExportData data) => JsonDocument.Parse(Export.CreateJson(data)).RootElement.Clone();
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void DistinctSlots()
{
    var data = Data() with
    {
        SavedInventory = new(Epoch(), [new("Inventory1", 35, null,
            [Item(reading: Reading()), Item(8, Reading(4)) with { Name = "Second card", Quantity = 2, IsHq = true }])]),
    };
    var items = Root(data).GetProperty("saved_inventory").GetProperty("bags")[0].GetProperty("items");
    Check(items.GetArrayLength() == 2, "matching item IDs must not collapse distinct slots");
    Check(items[0].GetProperty("slot_zero_based").GetUInt32() == 0 &&
        items[0].GetProperty("slot_display").GetUInt64() == 1 &&
        items[1].GetProperty("slot_zero_based").GetUInt32() == 8 &&
        items[1].GetProperty("slot_display").GetUInt64() == 9, "slot numbering must be unambiguous");
    Check(items[0].GetProperty("raw_item_id").GetUInt32() == items[1].GetProperty("raw_item_id").GetUInt32(),
        "fixture must contain matching item IDs");
    Check(items[1].GetProperty("quantity").GetInt32() == 2 && items[1].GetProperty("is_hq").GetBoolean(),
        "quantity and HQ must survive independently");
    var text = Export.CreateText(data);
    Check(text.Contains("Slot 1 (raw zero-based slot 0)") && text.Contains("Slot 9 (raw zero-based slot 8): Second card"),
        "text must distinguish both cards");
    Check(text.Contains("quantity 2 | HQ yes | raw ID 9614 | base ID 9614"), "compact item row must retain all metadata");
}

static void SeparateObservations()
{
    var data = Data() with
    {
        InventoryChanged = true,
        SavedInventory = new(Epoch(), [new("Inventory1", 35, null, [Item(reading: Reading(1)), Item(1)])]),
        CurrentPedigreeScan = new(true, 8, "Scanning", [Card(reading: Reading(4, Epoch().AddMinutes(1))),
            Card(ExportPedigreeCheckStatus.Decoded, Reading(3)) with { SlotZeroBased = 1 }]),
    };
    var json = Root(data);
    var savedItems = json.GetProperty("saved_inventory").GetProperty("bags")[0].GetProperty("items");
    var scanCards = json.GetProperty("current_pedigree_scan").GetProperty("cards");
    Check(savedItems[0].GetProperty("pedigree").GetProperty("father_stars").GetProperty("speed").GetInt32() == 1,
        "current scan must not overwrite historical stars for matching slot and ID");
    Check(savedItems[1].GetProperty("pedigree").ValueKind == JsonValueKind.Null,
        "current scan cannot fill missing historical pedigree by matching slot and ID");
    Check(scanCards[0].GetProperty("status").GetString() == "decoded" &&
        scanCards[0].GetProperty("reading").GetProperty("father_stars").GetProperty("speed").GetInt32() == 4,
        "decoded scan must retain its own status and reading");
    Check(scanCards[1].GetProperty("status").GetString() == "decoded", "decoded current cards must remain explicit");
    Check(json.GetProperty("inventory_changed").GetBoolean(), "stale historical snapshot must be marked changed");
    var text = Export.CreateText(data);
    Check(text.Contains("CURRENT PEDIGREE SCAN — separate from saved inventory") && text.Contains("Status: decoded") &&
        text.Contains("Scan is in progress") && text.Contains("only cards collected so far"), "text must label scan separation and partial coverage");
}

static void BagAvailability()
{
    var data = Data() with
    {
        InventoryUnavailableReason = "Some bags could not be read",
        SavedInventory = new(Epoch(), [new("Inventory1", 35, null, []),
            new("Inventory2", null, "No slots returned; loading or availability unknown", [])]),
    };
    var bags = Root(data).GetProperty("saved_inventory").GetProperty("bags");
    Check(bags[0].GetProperty("slot_count").GetInt32() == 35 &&
        bags[0].GetProperty("unavailable_reason").ValueKind == JsonValueKind.Null, "known empty bag must remain available");
    Check(bags[1].GetProperty("slot_count").ValueKind == JsonValueKind.Null &&
        bags[1].GetProperty("unavailable_reason").GetString()!.Contains("unknown"), "unavailable slot count must remain unknown");
    var text = Export.CreateText(data);
    Check(text.Contains("No occupied item records.") && text.Contains("No item records; unavailable does not mean empty."),
        "empty and unavailable bags need distinct explanations");
    Check(text.Contains("Slot count: unknown"), "null slot count must never become zero");
}

static void PedigreeProvenance()
{
    var data = Data() with { SavedInventory = new(Epoch(), [new("Inventory1", 35, null,
        [Item(reading: Reading()), Item(1, Reading() with { Status = "user_verified_tooltip" })])]) };
    var json = Root(data);
    var items = json.GetProperty("saved_inventory").GetProperty("bags")[0].GetProperty("items");
    Check(items[0].GetProperty("pedigree").GetProperty("status").GetString() == "decoded_tooltip" &&
        items[1].GetProperty("pedigree").GetProperty("status").GetString() == "user_verified_tooltip",
        "export must preserve new and historical provenance explicitly");
    Check(!Export.CreateJson(data).Contains("verified_pedigree"), "automatic data must not use a falsely verified property name");
    var text = Export.CreateText(data);
    Check(text.Contains("provenance decoded_tooltip") && text.Contains("provenance user_verified_tooltip") &&
        !text.Contains("Saved pedigree: checked"), "text must not claim all attached readings were checked");
}

static void ScanOnly()
{
    var data = Data() with
    {
        SavedInventory = null, CapturePending = true, InventoryUnavailableReason = "No saved capture",
        CurrentPedigreeScan = new(false, 0, "Stopped", [Card(ExportPedigreeCheckStatus.Unavailable) with
            { Reason = "Unsupported tooltip layout" }]),
    };
    var json = Root(data);
    Check(json.GetProperty("saved_inventory").ValueKind == JsonValueKind.Null && json.GetProperty("capture_pending").GetBoolean(),
        "scan-only export must retain unavailable inventory and pending capture");
    var card = json.GetProperty("current_pedigree_scan").GetProperty("cards")[0];
    Check(card.GetProperty("reading").ValueKind == JsonValueKind.Null && card.GetProperty("status").GetString() == "unavailable",
        "undecodable card must not imply a reading");
    var text = Export.CreateText(data);
    Check(text.Contains("Saved capture: unavailable") && text.Contains("Status: unavailable") &&
        text.Contains("Reason: Unsupported tooltip layout") && text.Contains("both star rows are unknown"), "scan-only text must remain useful and explicit");
}

static void StarOrderAndUnknowns()
{
    var reading = Reading() with { Kind = null, Grade = null, Sex = null,
        FatherStars = ExportStarRow.From([4, 0, 2]), MotherStars = ExportStarRow.From(null) };
    var data = Data() with { SavedInventory = new(Epoch(), [new("Inventory1", 35, null, [Item(reading: reading)])]) };
    var json = Root(data);
    Check(json.GetProperty("pedigree_stat_order").EnumerateArray().Select(v => v.GetString()).SequenceEqual(
        ["Speed", "Acceleration", "Endurance", "Stamina", "Cunning"]), "stat order must be canonical");
    var pedigree = json.GetProperty("saved_inventory").GetProperty("bags")[0].GetProperty("items")[0].GetProperty("pedigree");
    Check(pedigree.GetProperty("father_stars").EnumerateObject().Select(p => p.Name).SequenceEqual(
        ["speed", "acceleration", "endurance", "stamina", "cunning"]), "named row order must match canonical order");
    Check(pedigree.GetProperty("father_stars").GetProperty("acceleration").ValueKind == JsonValueKind.Null &&
        pedigree.GetProperty("father_stars").GetProperty("cunning").ValueKind == JsonValueKind.Null &&
        pedigree.GetProperty("mother_stars").EnumerateObject().All(p => p.Value.ValueKind == JsonValueKind.Null),
        "missing or invalid stars must remain null, not zero");
    var text = Export.CreateText(data);
    Check(text.Contains("Kind: unknown; grade: unknown; sex: unknown") && text.Contains("4 | unknown | 2 | unknown | unknown"),
        "unknown values must be readable in text");
    Check(text.Contains("Ability, colour, breeding capacity and own racing stats are unavailable"), "unsupported attributes need a visible limitation");
}

static void UnicodeAndUtc()
{
    var offsetTime = DateTimeOffset.Parse("2026-10-02T15:00:00+03:00", CultureInfo.InvariantCulture);
    const string name = "チョコボ — Étoile ♀";
    var reading = Reading(observed: offsetTime);
    var data = Data() with
    {
        ExportedAtUtc = offsetTime, SavedInventory = new(offsetTime, [new("Inventory1", 35, null,
            [Item(reading: reading) with { Name = name }])]),
        CurrentPedigreeScan = new(false, 0, "Oprit — finalizat", [Card(reading: reading) with
            { Name = name, ObservedAtUtc = offsetTime }]),
    };
    var encoding = new UTF8Encoding(false, true);
    var jsonText = Export.CreateJson(data);
    var text = Export.CreateText(data);
    Check(jsonText.Contains(name) && text.Contains(name), "Unicode names should be directly readable");
    Check(encoding.GetString(encoding.GetBytes(jsonText)) == jsonText && encoding.GetString(encoding.GetBytes(text)) == text,
        "UTF-8 roundtrip must preserve content");
    using var parsed = JsonDocument.Parse(encoding.GetBytes(jsonText));
    var json = parsed.RootElement;
    var saved = json.GetProperty("saved_inventory");
    var card = json.GetProperty("current_pedigree_scan").GetProperty("cards")[0];
    foreach (var date in new[] { json.GetProperty("exported_at_utc"), saved.GetProperty("captured_at_utc"),
        saved.GetProperty("bags")[0].GetProperty("items")[0].GetProperty("pedigree").GetProperty("observed_at_utc"),
        card.GetProperty("observed_at_utc"), card.GetProperty("reading").GetProperty("observed_at_utc") })
        Check(date.GetString() == "2026-10-02T12:00:00.0000000Z", "UTC-labelled timestamp must normalize its actual offset");
    Check(text.Contains("Exported UTC: 2026-10-02T12:00:00.0000000Z"), "text timestamp must normalize to UTC too");
}

static void DataScope()
{
    var json = Root(Data());
    Check(json.GetProperty("schema_version").GetInt32() == 3, "extra inventory export uses schema 3");
    Check(json.EnumerateObject().Select(p => p.Name).SequenceEqual([
        "schema_version", "scope", "exported_at_utc", "pedigree_stat_order", "notes", "capture_pending", "inventory_changed",
        "inventory_unavailable_reason", "saved_inventory", "extra_inventory", "current_pedigree_scan"]), "export must have a bounded top-level contract");
    var item = json.GetProperty("saved_inventory").GetProperty("bags")[0].GetProperty("items")[0];
    Check(item.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] {
        "slot_zero_based", "raw_item_id", "base_item_id", "name", "quantity", "is_hq", "pedigree", "slot_display" }.Order()),
        "item contract must exclude raw tooltip or unrelated data");
    Check(Export.CreateText(Data()).Contains("personal crystals/key items are covered; other storage is excluded"),
        "storage coverage limit must be explicit");
}

static void ExtraInventory()
{
    var data = Data() with { ExtraInventory = [new("crystals", "Crystals", "item", Epoch(), 18,
        [new(0, 1, 1, "Fire Shard", 9999, false, null)]), new("key_items", "KeyItems", "event_item", Epoch(), 105,
        [new(3, 2000001, 2000001, "Quest letter", 1, false, null)])] };
    var extras = Root(data).GetProperty("extra_inventory");
    Check(extras[0].GetProperty("items")[0].GetProperty("quantity").GetInt32() == 9999 &&
        extras[1].GetProperty("item_namespace").GetString() == "event_item" &&
        extras[1].GetProperty("items")[0].GetProperty("base_item_id").GetUInt32() == 2000001, "Extra inventory must not normalize EventItem IDs.");
    Check(Export.CreateText(data).Contains("definition namespace event_item") && Export.CreateText(data).Contains("Quest letter"), "Text extra inventory is readable.");
}

static void FileRoundTrip()
{
    var data = Data() with { SavedInventory = new(Epoch(), [new("Inventory1", 35, null,
        [Item() with { Name = "チョコボ — Étoile ♀" }])]) };
    var json = Export.CreateJson(data);
    var text = Export.CreateText(data);
    var root = Path.Combine(Path.GetTempPath(), "ffxiv-context-export-tests-" + Guid.NewGuid().ToString("N"));
    var outputs = new List<ContextExportPaths>();
    try
    {
        outputs.Add(ContextExportFiles.Save(root, json, text, data.ExportedAtUtc));
        outputs.Add(ContextExportFiles.Save(root, json, text, data.ExportedAtUtc));
        Check(outputs[0].Directory != outputs[1].Directory, "same timestamp must not overwrite an earlier export");
        var utf8 = new UTF8Encoding(false, true);
        foreach (var output in outputs)
        {
            Check(Path.GetDirectoryName(output.Directory) == root, "export folder must stay in the supplied root");
            foreach (var (path, content) in new[] { (output.JsonFile, json), (output.TextFile, text) })
            {
                var bytes = File.ReadAllBytes(path);
                Check(!(bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf), "UTF-8 export must omit BOM");
                Check(utf8.GetString(bytes) == content, "file must contain the complete UTF-8 document");
            }
        }
        Check(Directory.GetDirectories(root).Length == 2 && !Directory.EnumerateDirectories(root).Any(p => p.EndsWith(".pending")),
            "only completed export pairs should remain");
    }
    finally
    {
        // Delete only the explicitly returned files and their empty directories.
        foreach (var output in outputs)
        {
            File.Delete(output.JsonFile);
            File.Delete(output.TextFile);
            Directory.Delete(output.Directory, false);
        }
        if (Directory.Exists(root)) Directory.Delete(root, false);
    }
}
