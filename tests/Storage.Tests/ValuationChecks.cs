using System.Text.Json;
using CompAI;

internal static class ValuationChecks
{
    internal static void Run(string[] args)
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
        JsonElement Query(ValuationLookup lookup, string view, string[] categories, int[]? handles = null) =>
            JsonSerializer.SerializeToElement(lookup.Query(JsonSerializer.SerializeToElement(new { view, categories, handles = handles ?? [] })));
        ValuationContainer Container(string category, string key, string name, string page, StorageItem[] rows) =>
            new(category, key, name, page, now, true, rows);
        var captures = new List<ValuationContainer>();
        for (var retainer = 0; retainer < 10; retainer++)
            for (var page = 0; page < 7; page++)
                captures.Add(Container("retainers", $"PRIVATE-{retainer}", $"Retainer {retainer + 1}", $"RetainerPage{page + 1}",
                    Enumerable.Range(0, 25).Select(slot =>
                    {
                        var id = (uint)(1000 + retainer * 175 + page * 25 + slot);
                        return new StorageItem(slot, id, id, $"Item {id}", 99, false);
                    }).ToArray()));
        captures.Add(Container("inventory", "PRIVATE-CARRIED", "Carried bags", "Inventory1", [new(0, 1000, 1000, "Item 1000", 3, false), new(1, 1001000, 1000, "Item 1000", 2, true)]));
        captures.Add(Container("inventory", "PRIVATE-CARRIED", "Carried bags", "Inventory2", []) with { Available = false });
        captures.Add(Container("retainers", "PRIVATE-0", "Retainer 1", "Current market listings", [new(0, 1000, 1000, "Item 1000", 20, false, 999999)]));
        captures.Add(Container("housing", "PRIVATE-HOUSE", "Estate", "Indoor placed furnishings 1", [new(0, 9000, 9000, "Placed chair", 1, false)]));
        captures.Add(Container("housing", "PRIVATE-HOUSE", "Estate", "Indoor storeroom 1", [new(0, 9001, 9001, "Stored chair", 1, false)]));
        captures.Add(Container("fc_chest", "PRIVATE-FC", "Company", "Chest page 1", [new(0, 9002, 9002, "Shared ore", 3, false)]));
        var lookup = new ValuationLookup(captures, ["inventory", "retainers", "housing", "fc_chest"], carriedChanged: true);
        var personal = new[] { "inventory", "retainers" };
        var candidates = Query(lookup, "candidates", personal);
        var rows = candidates.GetProperty("rows").EnumerateArray().ToArray();
        Check(rows.Length == 1751 && candidates.GetProperty("observed_slots").GetInt32() == 1752,
            "All ten full retainers plus carried rows are included in one candidate response.");
        Check(candidates.GetProperty("complete").GetBoolean() && candidates.GetProperty("carried_changed").GetBoolean() &&
            candidates.GetProperty("unavailable_carried_containers").GetInt32() == 1,
            "Coverage and changed/unavailable carried containers stay explicit.");
        Check(candidates.GetProperty("excluded_rows").GetProperty("market_listings").GetInt32() == 1 &&
            !candidates.GetRawText().Contains("PRIVATE") && !candidates.GetRawText().Contains("Retainer 1"),
            "Private identities/names never enter candidate tuples and listed inventory is excluded.");
        var nq = rows.Single(row => row[1].GetInt32() == 1000 && row[2].GetInt32() == 0);
        var hq = rows.Single(row => row[1].GetInt32() == 1000 && row[2].GetInt32() == 1);
        Check(nq[3].GetInt64() == 102 && hq[3].GetInt64() == 2, "HQ grouping and cross-source quantities are exact.");
        var details = Query(lookup, "details", personal, [nq[0].GetInt32(), hq[0].GetInt32()]);
        Check(details.GetProperty("rows")[0].GetProperty("total_locations").GetInt32() == 2 &&
            details.GetProperty("snapshot_id").GetString() == candidates.GetProperty("snapshot_id").GetString() &&
            !details.GetRawText().Contains("PRIVATE"), "Winners retain exact labels/slots without local source keys.");
        var frozen = candidates.GetRawText();
        captures[0].Items[0] = captures[0].Items[0] with { Quantity = 1 };
        Check(Query(lookup, "candidates", personal).GetRawText() == frozen, "Live cache mutation cannot rewrite a frozen valuation.");
        var housing = Query(lookup, "candidates", ["housing"]);
        Check(housing.GetProperty("rows").GetArrayLength() == 1 &&
            housing.GetProperty("excluded_rows").GetProperty("placed_furnishings").GetInt32() == 1,
            "Explicit housing scope includes storeroom only; placed furnishings remain excluded.");
        foreach (var invalid in new[] {
            JsonSerializer.SerializeToElement(new {view="candidates", categories=new[]{"saddlebag"}, handles=Array.Empty<int>()}),
            JsonSerializer.SerializeToElement(new {view="candidates", categories=personal, handles=new[]{1}}),
            JsonSerializer.SerializeToElement(new {view="details", categories=new[]{"housing"}, handles=new[]{nq[0].GetInt32()}}) })
        {
            try { lookup.Query(invalid); throw new Exception("Invalid valuation scope accepted."); }
            catch (ArgumentException) { }
        }
        var many = Enumerable.Range(0, 42).Select(page => Container("retainers", "PRIVATE-MANY", "Synthetic", $"Page{page}",
            Enumerable.Range(0, 100).Select(slot => new StorageItem(slot, (uint)(page * 100 + slot + 1),
                (uint)(page * 100 + slot + 1), "Bound fixture", 1, false)).ToArray())).ToArray();
        var oversize = new ValuationLookup(many, ["retainers"]);
        try { Query(oversize, "candidates", ["retainers"]); throw new Exception("Oversized scope was silently truncated."); }
        catch (IOException) { }
        var repeated = new ValuationLookup(Enumerable.Range(0, 40).Select(page => Container("retainers", "PRIVATE-R", "Repeat", $"Page{page}",
            [new(0, 5, 5, "Ore", 99, false)])), ["retainers"]);
        var capped = Query(repeated, "details", ["retainers"], [1]).GetProperty("rows")[0];
        Check(capped.GetProperty("total_locations").GetInt32() == 40 && capped.GetProperty("locations_truncated").GetBoolean() &&
            capped.GetProperty("locations").GetArrayLength() == 20, "Location cap does not truncate candidate quantities or hide omitted locations.");
        var crystalLookup = new ValuationLookup([Container("crystals", "personal", "Carried inventory", "Crystals",
            [new(0, 1, 1, "Fire Shard", 9999, false)])], ["crystals"]);
        Check(Query(crystalLookup, "candidates", ["crystals"]).GetProperty("rows")[0][3].GetInt32() == 9999,
            "Personal crystals participate in batch pricing.");
        try { _ = new ValuationLookup([], ["key_items"]); throw new Exception("KeyItems valuation accepted."); }
        catch (ArgumentException) { }
        var bindingRows = new[] { Container("retainers", "PRIVATE-RESUME", "Retainer", "Page1",
            [new(0, 5, 5, "Ore", 9, false)]) };
        string Binding(ValuationContainer[] sources, string scope = "session:character:load:access",
            string[]? permitted = null, bool changed = false) =>
            Query(new ValuationLookup(sources, permitted ?? ["retainers"], changed, scope),
                "candidates", ["retainers"]).GetProperty("resume_identity").GetString()!;
        var binding = Binding(bindingRows);
        Check(binding.Length == 64 && binding == Binding(bindingRows.Select(c => c with {
            CapturedAtUtc = now.AddMinutes(1) }).ToArray()),
            "Unchanged observed contents keep their resume identity after timestamp-only refresh.");
        Check(binding != Binding(bindingRows, "session:other-character:load:access") &&
            binding != Binding(bindingRows, "session:character:load:other-access") &&
            binding != Binding(bindingRows, permitted: ["retainers", "saddlebag"]) &&
            binding != Binding(bindingRows.Select(c => c with { SourceKey = "PRIVATE-OTHER" }).ToArray()) &&
            binding != Binding(bindingRows.Select(c => c with { Items = [c.Items[0] with { Quantity = 8 }] }).ToArray()),
            "Resume identity binds character, permission epoch, permitted sources and exact saved contents.");
        if (args.Length == 2 && args[0] == "--write-valuation-fixture")
            File.WriteAllText(args[1], JsonSerializer.Serialize(new { summary = lookup.Summary,
                arguments = new {view="candidates", categories=personal, handles=Array.Empty<int>()}, candidates, details }));
        Console.WriteLine("PASS Valuation: ten full retainers, quantities, frozen identities, scoped storage, hard bounds and detail locations");
    }
}
