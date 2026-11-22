using System.Text.Json;
using CompAI;
using Lumina.Text.ReadOnly;

if (args is ["--inspect-item-schema"])
{
    OfflineItemProbe.DescribeSchema();
    return;
}
if (args is ["--inspect-items", var itemSqpack])
{
    OfflineItemProbe.Run(itemSqpack);
    return;
}
if (args is ["--inspect-stats", var statsSqpack])
{
    OfflineItemProbe.InspectStats(statsSqpack);
    return;
}

if (args is ["--inspect-recipes", var sqpack])
{
    OfflineRecipeProbe.Run(sqpack);
    return;
}

var passed = 0;
void Check(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
void Test(string name, System.Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
void Reject(string json)
{
    try { GameDefinitionsLookup.ValidateArguments(JsonDocument.Parse(json).RootElement); }
    catch (ArgumentException) { return; }
    throw new Exception("Expected argument rejection: " + json);
}
GameDefinitionsArguments Args(string json) => GameDefinitionsLookup.Parse(JsonDocument.Parse(json).RootElement);
GameDefinitionEntry Recipe(uint id = 100, string name = "Synthetic Bronze Ingot") => new(id, name,
    recipe: new(200, 2, [new(300, "Synthetic Ore", 3), new(2, "Synthetic Shard", 1)], 0, "Carpenter", 10, 20, 30, 65537, 5, false));
GameDefinitionEntry ActionRow(uint id = 400, string name = "Synthetic Cure", string? description = "Restores target HP.") => new(id, name,
    action: new(description, description is null ? "Dynamic description unavailable." : null, 24, "White Mage", 7, "Healers", 30, 15, 25, false, true));
GameDefinitionEntry Quest(uint id = 65537, string name = "Synthetic First Quest") => new(id, name,
    quest: new([10], 24, "White Mage", [new(7, "Healers")], [65538, 65539], 2, false));
GameDefinitionEntry ItemRow(uint id = 500, string name = "Synthetic Test Ore", string? description = "A synthetic crafting material.", bool equipment = false) => new(id, name,
    item: new(description, description is null ? "Static description unavailable." : null, 7, "Synthetic Materials", 999,
        true, false, false, equipment ? 1u : null, equipment ? 10 : null, equipment ? 12u : null,
        equipment ? 7u : null, equipment ? "Synthetic Jobs" : null));
GameDefinitionEntry MapRow(uint id = 800, string floor = "First Floor") => new(id, "Synthetic City / " + floor,
    map: new(100, "Synthetic City", "Synthetic Region", floor, 200));
GameDefinitionEntry StatsRow(uint id = 500, bool materia = false)
{
    var entry = ItemRow(id, equipment: !materia);
    var equipment = new GameDefinitionEquipmentStats([
        new(1, "Strength", 580, 645, 645, null), new(27, "Critical Hit", 251, 279, 398, null),
        new(44, "Determination", 358, 398, 398, null), new(45, "Skill Speed", 0, 0, 398, null),
        new(999, "Unsupported stat", 1, 1, null, "Unsupported cap factors.")],
        new(132, 147), new(67, 74), new(0, 0), new(0, 0), 2800, 2, true);
    var stats = new GameDefinitionItemStats(materia ? null : equipment, materia ? "Not equipment." : null,
        materia ? new(12, 11, 27, "Critical Hit", 54, 690) : null, materia ? null : "No matching Materia definition.");
    return entry with { item = entry.item! with { stats = stats } };
}
GameDefinitionsLookupResult Build(string json, int count, GameDefinitionEntry[] entries, string? unavailable = null) =>
    GameDefinitionsLookup.Build(Args(json), "English", "Synthetic local sheet fixtures", count, entries,
        DateTimeOffset.Parse("2026-01-01T00:00:00Z"), unavailable);

Test("narrow selectors, types and duplicate properties are enforced", () =>
{
    foreach (var json in new[] { "{}", "null", "[]", "{\"kind\":\"unknown\",\"query\":\"aa\"}",
        "{\"kind\":\"recipes\"}", "{\"kind\":\"recipes\",\"query\":\"a\"}",
        "{\"kind\":\"recipes\",\"query\":\"  \"}", "{\"kind\":\"recipes\",\"query\":\"aa\\n\"}",
        "{\"kind\":\"recipes\",\"query\":\"aa\",\"row_id\":1}", "{\"kind\":\"recipes\",\"row_id\":0}",
        "{\"kind\":\"recipes\",\"row_id\":true}", "{\"kind\":\"recipes\",\"row_id\":4294967296}",
        "{\"kind\":\"recipes\",\"row_id\":1,\"offset\":1}", "{\"kind\":\"recipes\",\"query\":\"aa\",\"limit\":21}",
        "{\"kind\":\"recipes\",\"query\":\"aa\",\"offset\":65536}", "{\"kind\":\"recipes\",\"query\":\"aa\",\"extra\":1}",
        "{\"kind\":\"recipes\",\"query\":\"aa\",\"query\":\"bb\"}",
        "{\"kind\":\"maps\",\"row_id\":65536}", "{\"kind\":\"maps\",\"row_id\":0}",
        "{\"kind\":\"maps\",\"row_id\":800,\"territory_id\":100}" }) Reject(json);
    Check(Args("{\"kind\":\"quests\",\"row_id\":65537}").RowId == 65537);
    Check(Args("{\"kind\":\"items\",\"row_id\":500}").Kind == "items");
    Check(Args("{\"kind\":\"maps\",\"row_id\":65535}").Kind == "maps");
});
Test("batch IDs and stats are exact item selectors with bounded continuation", () =>
{
    foreach(var json in new[] { "{\"kind\":\"items\",\"row_ids\":[]}", "{\"kind\":\"items\",\"row_ids\":[1,1]}",
        "{\"kind\":\"items\",\"row_ids\":[true]}", "{\"kind\":\"items\",\"row_ids\":[0]}",
        "{\"kind\":\"items\",\"row_ids\":[1],\"row_id\":1}", "{\"kind\":\"items\",\"row_ids\":[1],\"query\":\"aa\"}",
        "{\"kind\":\"recipes\",\"row_ids\":[1]}", "{\"kind\":\"items\",\"row_ids\":[1],\"offset\":1}",
        "{\"kind\":\"items\",\"query\":\"aa\",\"detail\":\"stats\"}", "{\"kind\":\"actions\",\"row_id\":1,\"detail\":\"stats\"}",
        "{\"kind\":\"items\",\"row_id\":1,\"detail\":true}" }) Reject(json);
    Reject(JsonSerializer.Serialize(new {kind="items", row_ids=Enumerable.Range(1,21).ToArray()}));
    var args = Args("{\"kind\":\"items\",\"row_ids\":[502,500,501],\"detail\":\"stats\",\"limit\":1}");
    Check(args.RowIds!.SequenceEqual(new uint[]{500,501,502}) && args.Stats);
    var batch = GameDefinitionsLookup.Build(args, "English", "Synthetic", 2, [StatsRow()]);
    Check(batch.next_offset == 1 && batch.matched_count == 2);
    Check(!JsonSerializer.Serialize(ItemRow()).Contains("\"stats\""));
});
Test("quality bonuses and caps apply role-first floors before final half-up rounding", () =>
{
    Check(LocalItemStatsReader.QualityValue(132,15,true).hq_value == 147);
    Check(LocalItemStatsReader.QualityValue(132,15,false).hq_value is null);
    Check(LocalItemStatsReader.CalculateCap(2840,140,100,398) == 398);
    Check(LocalItemStatsReader.CalculateCap(230,150,100,0) == 35); // 34.5 rounds up.
    Check(LocalItemStatsReader.CalculateCap(4610,140,100,645) == 645);
    Check(LocalItemStatsReader.CalculateCap(275,100,90,0) == 25);
    Check(LocalItemStatsReader.CalculateCap(262,100,90,24) == 24); // Role-first: 235 -> 23.5; slot-first rounding incorrectly gives 23.
    Check(LocalItemStatsReader.CalculateCap(262,100,90,25) is null);
    Check(LocalItemStatsReader.CalculateCap(118,67,70,0) == 5); // The first floor matters: 82 -> 54 -> 5; a combined product gives 6.
    foreach(var factors in new (int?,int?,int?,int)[]{(null,100,100,0),(100,null,100,0),(100,100,null,0),
        (-1,100,100,0),(65536,100,100,0),(100,1001,100,0),(100,100,101,0),(100,100,0,0),(2840,140,100,399)})
        Check(LocalItemStatsReader.CalculateCap(factors.Item1,factors.Item2,factors.Item3,factors.Item4) is null);
    foreach(var slots in new (uint,int)[][]{[(0,1)],[(1,-1)],[(1,32768)],[(1,1),(1,2)],Enumerable.Repeat((1u,1),17).ToArray()})
    {
        try { LocalItemStatsReader.StatValues(slots); throw new Exception("Expected invalid stat rejection"); }
        catch(InvalidOperationException) { }
    }
    Check(LocalItemStatsReader.StatValues([(0,0),(27,42)])[27] == 42);
});
Test("item definitions retain static metadata and explicit unknown equipment and availability", () =>
{
    var material = Build("{\"kind\":\"items\",\"row_id\":500}", 1, [ItemRow()]);
    var item = material.entries[0].item!;
    Check(material.scope == "local_game_definitions_v3" && item.stack_size == 999 && item.can_be_hq);
    Check(item.equip_level is null && item.item_level is null && item.class_job_category_id is null && item.character_availability == "unknown");
    var gear = Build("{\"kind\":\"items\",\"row_id\":500}", 1, [ItemRow(equipment: true)]).entries[0].item!;
    Check(gear.equip_level == 10 && gear.item_level == 12 && gear.class_job_category_id == 7);
    Check(Build("{\"kind\":\"items\",\"row_id\":500}", 1, [ItemRow(description: null)]).entries[0].item!.description_unknown_reason is not null);
    Check(!JsonSerializer.Serialize(material).Contains("quantity_owned"));
});
Test("map definitions preserve verified territory and distinct floor metadata without invented destinations", () =>
{
    var map = Build("{\"kind\":\"maps\",\"row_id\":800}", 1, [MapRow()]);
    Check(map.entries[0].map!.territory_id == 100 && map.entries[0].map!.size_factor == 200);
    Check(map.entries[0].map!.floor_name == "First Floor" && map.entries[0].map!.region_name == "Synthetic Region");
    Check(map.entries[0].item is null && map.entries[0].recipe is null && map.entries[0].action is null && map.entries[0].quest is null);
    var floors = Build("{\"kind\":\"maps\",\"query\":\"synthetic city\"}", 2, [MapRow(), MapRow(801, "Second Floor")]);
    Check(floors.entries.Length == 2 && floors.entries[1].map!.floor_name == "Second Floor");
    Check(!JsonSerializer.Serialize(map.entries[0]).Contains("coordinates"));
    Check(!JsonSerializer.Serialize(map.entries[0]).Contains("unlocked"));
});
Test("map results reject malformed identities, mixed payloads, and unbounded fields", () =>
{
    var valid = MapRow();
    foreach (var entry in new[] { new GameDefinitionEntry(800, "Synthetic Missing"), valid with { item = ItemRow().item },
        valid with { map = valid.map! with { territory_id = 0 } }, valid with { map = valid.map! with { territory_id = 65536 } },
        valid with { row_id = 65536 }, valid with { map = valid.map! with { size_factor = 0 } },
        valid with { map = valid.map! with { size_factor = 65536 } }, valid with { map = valid.map! with { territory_name = "" } },
        valid with { map = valid.map! with { floor_name = "bad\nfloor" } },
        valid with { map = valid.map! with { region_name = new string('x', 129) } } })
    {
        var rejected = false;
        try { Build("{\"kind\":\"maps\",\"query\":\"synthetic\"}", 1, [entry]); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected);
    }
    var metadata = Build("{\"kind\":\"maps\",\"row_id\":800}", 1,
        [valid with { map = valid.map! with { size_factor = 65535, floor_name = null, region_name = null } }]);
    Check(metadata.entries[0].map!.size_factor == 65535 && metadata.entries[0].map!.floor_name is null);
});
Test("name search counts every match while retaining only the requested bounded page", () =>
{
    var names = Enumerable.Range(1, 500).Select(id => new GameDefinitionName((uint)id, id % 2 == 0 ? "Synthetic Material" : "Other Row"));
    var page = GameDefinitionsReader.SelectNamePage(names, Args("{\"kind\":\"items\",\"query\":\"synthetic\",\"offset\":20,\"limit\":3}"));
    Check(page.Count == 250 && page.Rows.Select(row => row.RowId).SequenceEqual(new uint[] { 42, 44, 46 }));
    var exhausted = GameDefinitionsReader.SelectNamePage(names, Args("{\"kind\":\"items\",\"query\":\"synthetic\",\"offset\":400}"));
    Check(exhausted.Count == 250 && exhausted.Rows.Length == 0);
    try
    {
        GameDefinitionsReader.SelectNamePage(Enumerable.Repeat(new GameDefinitionName(1, "Synthetic Material"), 65537),
            Args("{\"kind\":\"items\",\"query\":\"synthetic\"}"));
    }
    catch (InvalidOperationException) { return; }
    throw new Exception("Unbounded name index accepted");
});
Test("item kinds cannot carry other definition payloads", () =>
{
    foreach (var entry in new[] { Recipe(500), ItemRow() with { action = ActionRow().action }, new GameDefinitionEntry(500, "Synthetic Empty") })
    {
        var rejected = false;
        try { Build("{\"kind\":\"items\",\"row_id\":500}", 1, [entry]); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected);
    }
});
Test("localized names use bounded term matching without sharp-s expansion", () =>
{
    Check(GameDefinitionsLookup.Matches("Éther Synthetic Bronze", "éther bronze"));
    Check(!GameDefinitionsLookup.Matches("Straße", "strasse"));
    Check(GameDefinitionsLookup.Matches("Synthetic 界界", "界界"));
});
Test("recipe quantities, raw quest prerequisites and base action timing survive serialization", () =>
{
    var recipe = Build("{\"kind\":\"recipes\",\"row_id\":100}", 1, [Recipe()]);
    Check(recipe.entries[0].recipe!.output_quantity == 2 && recipe.entries[0].recipe!.ingredients[0].quantity == 3);
    Check(recipe.entries[0].recipe!.craft_type_id == 0 && recipe.entries[0].recipe!.character_availability == "unknown");
    var action = Build("{\"kind\":\"actions\",\"row_id\":400}", 1, [ActionRow()]);
    Check(action.entries[0].action!.base_recast_100ms == 25);
    var quest = Build("{\"kind\":\"quests\",\"row_id\":65537}", 1, [Quest()]);
    Check(quest.entries[0].quest!.previous_quest_ids.Length == 2 && quest.entries[0].quest!.prerequisite_logic == "uninterpreted");
    Check(!JsonSerializer.Serialize(quest.entries[0]).Contains("journal"));
});
Test("recipe empty signed-minus-one slot is skipped without an item lookup", () =>
{
    var lookedUp = new List<uint>();
    var ingredients = GameDefinitionsReader.DescribeRecipeIngredients(
        [(5106, 2), (5107, 1), (0, 0), (0, 0), (0, 0), (0, 0), (2, 1), (uint.MaxValue, 0)],
        id => { lookedUp.Add(id); return "Synthetic ingredient " + id; });
    Check(lookedUp.SequenceEqual(new uint[] { 5106, 5107, 2 }));
    Check(ingredients.Select(row => row.item_id).SequenceEqual(lookedUp));
    Check(ingredients.Select(row => row.quantity).SequenceEqual(new[] { 2, 1, 1 }));
});
Test("recipe sentinels with quantities and inconsistent ingredient slots stay rejected", () =>
{
    foreach (var slots in new (uint, int)[][] { [(uint.MaxValue, 1)], [(0, 1)], [(500, 0)], [(500, -1)], [(500, 256)],
        Enumerable.Repeat((500u, 1), 17).ToArray() })
    {
        var rejected = false;
        try { GameDefinitionsReader.DescribeRecipeIngredients(slots, _ => throw new Exception("Invalid slot resolved an item")); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected);
    }
});
Test("pagination counts matches and returns a progressing offset", () =>
{
    var page = Build("{\"kind\":\"recipes\",\"query\":\"synthetic\",\"offset\":20,\"limit\":2}", 25, [Recipe(121), Recipe(122)]);
    Check(page.offset == 20 && page.next_offset == 22 && page.matched_count == 25);
    var end = Build("{\"kind\":\"recipes\",\"query\":\"synthetic\",\"offset\":30}", 25, []);
    Check(end.next_offset is null && end.entries.Length == 0);
    var missing = Build("{\"kind\":\"quests\",\"row_id\":999}", 0, []);
    Check(missing.available && missing.entries.Length == 0);
});
Test("byte budget shortens a page without dropping the continuation", () =>
{
    var entries = Enumerable.Range(1, 20).Select(id => ActionRow((uint)id, "Synthetic " + new string('界', 100), new string('界', 1600))).ToArray();
    var page = Build("{\"kind\":\"actions\",\"query\":\"synthetic\"}", 65536, entries);
    Check(page.entries.Length > 0 && page.entries.Length < 20 && page.next_offset == page.entries.Length);
    Check(JsonSerializer.SerializeToUtf8Bytes(page).Length <= GameDefinitionsLookup.MaxBytes);
});
Test("unavailable results cannot carry definitions", () =>
{
    var page = Build("{\"kind\":\"quests\",\"query\":\"synthetic\"}", 0, [], "Synthetic sheet missing.");
    Check(!page.available && page.next_offset is null && page.entries.Length == 0);
    try { Build("{\"kind\":\"recipes\",\"row_id\":100}", 1, [Recipe()], "Missing"); }
    catch (ArgumentException) { return; }
    throw new Exception("Unavailable data leaked");
});
Test("description conversion only admits static text and harmless formatting", () =>
{
    Check(GameDefinitionsReader.StaticDescription(new ReadOnlySeString("Synthetic description")) == "Synthetic description");
    Check(GameDefinitionsReader.StaticDescription(new ReadOnlySeString(new byte[] { 65, 2, 16, 1, 3, 66 })) == "A B");
    Check(GameDefinitionsReader.StaticDescription(new ReadOnlySeString(new byte[] { 65, 2, 8, 1, 3, 66 })) is null);
    Check(GameDefinitionsReader.StaticDescription(new ReadOnlySeString(new string('x', 1601))) is null);
    Check(GameDefinitionsReader.StaticDescription(new ReadOnlySeString("")) is null);
});

if (args is ["--write-fixture", var path])
{
    object Fixture(string name, string json, int count, GameDefinitionEntry[] rows, string? unavailable = null) =>
        new { name, arguments = JsonDocument.Parse(json).RootElement.Clone(), result = Build(json, count, rows, unavailable) };
    var fixtures = new[] {
        Fixture("recipe", "{\"kind\":\"recipes\",\"row_id\":100}", 1, [Recipe()]),
        Fixture("action", "{\"kind\":\"actions\",\"row_id\":400}", 1, [ActionRow()]),
        Fixture("unknown_description", "{\"kind\":\"actions\",\"row_id\":400}", 1, [ActionRow(description: null)]),
        Fixture("quest", "{\"kind\":\"quests\",\"row_id\":65537}", 1, [Quest()]),
        Fixture("item", "{\"kind\":\"items\",\"row_id\":500}", 1, [ItemRow()]),
        Fixture("equipment", "{\"kind\":\"items\",\"row_id\":500}", 1, [ItemRow(equipment: true)]),
        Fixture("equipment_stats", "{\"kind\":\"items\",\"row_id\":500,\"detail\":\"stats\"}", 1, [StatsRow()]),
        Fixture("materia_stats", "{\"kind\":\"items\",\"row_id\":501,\"detail\":\"stats\"}", 1, [StatsRow(501, materia:true)]),
        Fixture("stats_batch", "{\"kind\":\"items\",\"row_ids\":[501,500,999],\"detail\":\"stats\",\"limit\":1}", 2, [StatsRow()]),
        Fixture("stats_batch_next", "{\"kind\":\"items\",\"row_ids\":[501,500,999],\"detail\":\"stats\",\"offset\":1}", 2, [StatsRow(501, materia:true)]),
        Fixture("stats_batch_budget", JsonSerializer.Serialize(new {kind="items",row_ids=Enumerable.Range(500,20).ToArray(),detail="stats"}),
            20, Enumerable.Range(500,20).Select(id => StatsRow((uint)id)).ToArray()),
        Fixture("item_unknown_description", "{\"kind\":\"items\",\"row_id\":500}", 1, [ItemRow(description: null)]),
        Fixture("item_pagination", "{\"kind\":\"items\",\"query\":\"synthetic\",\"offset\":20,\"limit\":2}", 25, [ItemRow(521), ItemRow(522)]),
        Fixture("item_missing", "{\"kind\":\"items\",\"row_id\":999}", 0, []),
        Fixture("item_unavailable", "{\"kind\":\"items\",\"query\":\"synthetic\"}", 0, [], "Synthetic Item sheet missing."),
        Fixture("map", "{\"kind\":\"maps\",\"row_id\":800}", 1, [MapRow()]),
        Fixture("map_floors", "{\"kind\":\"maps\",\"query\":\"synthetic city\"}", 2, [MapRow(), MapRow(801, "Second Floor")]),
        Fixture("map_pagination", "{\"kind\":\"maps\",\"query\":\"synthetic\",\"offset\":20,\"limit\":2}", 25, [MapRow(821), MapRow(822)]),
        Fixture("map_missing", "{\"kind\":\"maps\",\"row_id\":999}", 0, []),
        Fixture("map_unavailable", "{\"kind\":\"maps\",\"query\":\"synthetic\"}", 0, [], "Synthetic Map sheet missing."),
        Fixture("localized", "{\"kind\":\"recipes\",\"query\":\"éther\"}", 1, [Recipe(name: "Éther Synthetic")]),
        Fixture("pagination", "{\"kind\":\"recipes\",\"query\":\"synthetic\",\"offset\":20,\"limit\":2}", 25, [Recipe(121), Recipe(122)]),
        Fixture("end", "{\"kind\":\"recipes\",\"query\":\"synthetic\",\"offset\":30}", 25, []),
        Fixture("missing", "{\"kind\":\"quests\",\"row_id\":999}", 0, []),
        Fixture("unavailable", "{\"kind\":\"quests\",\"query\":\"synthetic\"}", 0, [], "Synthetic sheet missing."),
        Fixture("unicode_budget", "{\"kind\":\"actions\",\"query\":\"synthetic\"}", 65536,
            Enumerable.Range(1, 20).Select(id => ActionRow((uint)id, "Synthetic " + new string('界', 100), new string('界', 1600))).ToArray()) };
    var full = Path.GetFullPath(path);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    File.WriteAllText(full, JsonSerializer.Serialize(fixtures, new JsonSerializerOptions { WriteIndented = true }));
}
Console.WriteLine($"{passed} local game definition fixture groups passed; no live game or AI reads.");
