"""Synthetic local definition producer/validator checks; no game, HTTP or AI calls."""
from copy import deepcopy
import json
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "bridge"))
from game_definitions_lookup import (SCOPE, V2_SCOPE, LEGACY_SCOPE, TOOL, INSTRUCTIONS, validate_summary, validate_arguments,
                                    validate_result, permitted_tool, permitted_instructions)


class GameDefinitionsChecks(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        path = ROOT / ".test-tmp" / "game-definitions-results.json"
        if not path.is_file():
            raise RuntimeError("Run GameDefinitions.Tests with --write-fixture .test-tmp/game-definitions-results.json first")
        cls.fixtures = json.loads(path.read_text(encoding="utf-8"))

    def fixture(self, name="recipe"):
        return deepcopy(next(value for value in self.fixtures if value["name"] == name))

    def validate(self, value):
        return validate_result(value["result"], {"scope": SCOPE}, value["arguments"])

    def test_all_csharp_producer_fixtures(self):
        self.assertEqual(len(self.fixtures), 26)
        for fixture in self.fixtures:
            with self.subTest(fixture=fixture["name"]):
                self.validate(fixture)

    def test_selectors_unknown_keys_and_type_confusion(self):
        valid = {"kind": "recipes", "query": "synthetic"}
        cases = [None, [], {}, {"kind": "recipes"}, {"kind": "unknown", "query": "aa"},
                 {"kind": "actions", "query": "x"}, {"kind": "actions", "query": "aa\n"},
                 {"kind": "actions", "query": "\u0085aa"}, {"kind": "actions", "query": "😀" * 51},
                 {"kind": "actions", "row_id": 0}, {"kind": "actions", "row_id": True},
                 {"kind": "actions", "row_id": 4294967296}, {"kind": "quests", "row_id": 65537, "offset": 1},
                 {"kind": "maps", "row_id": 65536}, {"kind": "maps", "row_id": 0},
                 {"kind": "maps", "row_id": 800, "territory_id": 100},
                 {**valid, "row_id": 1}, {**valid, "extra": True}, {**valid, "offset": True},
                 {**valid, "offset": 65536}, {**valid, "limit": 0}, {**valid, "limit": 21}, {**valid, "limit": 1.0}]
        for value in cases:
            with self.subTest(value=value), self.assertRaises(RuntimeError):
                validate_arguments(value)
        self.assertEqual(validate_arguments(valid)["limit"], 20)
        self.assertIsNone(validate_summary(None))
        for value in ({"scope": "other"}, {"scope": SCOPE, "extra": 1}):
            with self.assertRaises(RuntimeError):
                validate_summary(value)

    def test_item_fields_remain_static_typed_and_bounded(self):
        mutations = [lambda row: row.update(stack_size=True), lambda row: row.update(stack_size=-1),
                     lambda row: row.update(stack_size=4294967296), lambda row: row.update(can_be_hq=1),
                     lambda row: row.update(is_unique="false"), lambda row: row.update(is_untradable=None),
                     lambda row: row.update(quantity_owned=5), lambda row: row.update(marketable=True),
                     lambda row: row.update(vendor_location="Synthetic town"), lambda row: row.update(character_availability="available"),
                     lambda row: row.update(description_unknown_reason="Unexpected"), lambda row: row.update(description="x" * 1601),
                     lambda row: row.update(ui_category_id=None, ui_category_name="Unexpected category"),
                     lambda row: row.update(equip_level=10), lambda row: row.update(item_level=10),
                     lambda row: row.update(class_job_category_id=7)]
        for mutate in mutations:
            fixture = self.fixture("item")
            mutate(fixture["result"]["entries"][0]["item"])
            with self.subTest(mutation=mutate), self.assertRaises(RuntimeError):
                self.validate(fixture)
        fixture = self.fixture("item_unknown_description")
        fixture["result"]["entries"][0]["item"]["description_unknown_reason"] = None
        with self.assertRaises(RuntimeError):
            self.validate(fixture)
        fixture = self.fixture("item")
        fixture["result"]["entries"][0]["item"]["stack_size"] = 0
        self.validate(fixture)  # Raw zero is not inferred usable capacity.

    def test_stats_selectors_and_batch_bounds(self):
        valid = {"kind": "items", "row_ids": [501, 500], "detail": "stats"}
        self.assertEqual(validate_arguments(valid)["row_ids"], [500, 501])
        bad = [{**valid, "row_ids": []}, {**valid, "row_ids": [500, 500]}, {**valid, "row_ids": [True]},
               {**valid, "row_ids": list(range(1, 22))}, {**valid, "row_ids": [0]}, {**valid, "query": "aa"},
               {**valid, "row_id": 500}, {**valid, "kind": "recipes"}, {**valid, "detail": "all"},
               {**valid, "offset": 2}, {"kind": "items", "query": "aa", "detail": "stats"}]
        for args in bad:
            with self.subTest(args=args), self.assertRaises(RuntimeError):
                validate_arguments(args)
        for scope in (LEGACY_SCOPE, V2_SCOPE):
            with self.assertRaises(RuntimeError):
                validate_arguments(valid, {"scope": scope})
            self.assertNotIn("row_ids", permitted_tool({"scope": scope})["inputSchema"]["properties"])
        budget = self.fixture("stats_batch_budget")
        result = self.validate(budget)
        self.assertGreater(len(result["entries"]), 0)
        self.assertLess(len(result["entries"]), 20)
        self.assertEqual(result["next_offset"], len(result["entries"]))
        result["entries"][0]["row_id"] = 1
        with self.assertRaises(RuntimeError):
            self.validate(budget)

    def test_stats_fail_closed_on_hq_caps_slots_materia_and_unrequested_data(self):
        for mutate in (lambda row: row.update(materia_slots=6), lambda row: row.update(advanced_melding_permitted=1),
                       lambda row: row["physical_damage"].update(hq_value=1), lambda row: row.update(delay_ms=True),
                       lambda row: row["attributes"][0].update(cap=1), lambda row: row["attributes"][0].update(cap=None),
                       lambda row: row["attributes"][0].update(cap_unknown_reason="Unexpected"),
                       lambda row: row["attributes"][0].update(base_value=-1),
                       lambda row: row["attributes"].append(deepcopy(row["attributes"][0]))):
            fixture = self.fixture("equipment_stats")
            mutate(fixture["result"]["entries"][0]["item"]["stats"]["equipment"])
            with self.assertRaises(RuntimeError):
                self.validate(fixture)
        for field, value in (("grade_index", 16), ("value", 0), ("value", True), ("minimum_item_level", 0), ("base_param_id", 0)):
            fixture = self.fixture("materia_stats")
            fixture["result"]["entries"][0]["item"]["stats"]["materia"][field] = value
            with self.assertRaises(RuntimeError):
                self.validate(fixture)
        fixture = self.fixture("equipment_stats")
        del fixture["arguments"]["detail"]
        with self.assertRaises(RuntimeError):
            self.validate(fixture)

    def test_equipment_metadata_requires_equipment_and_valid_references(self):
        for mutate in (lambda row: row.update(equip_slot_category_id=None), lambda row: row.update(equip_slot_category_id=0),
                       lambda row: row.update(equip_level=0), lambda row: row.update(equip_level=256),
                       lambda row: row.update(item_level=True), lambda row: row.update(item_level=0),
                       lambda row: row.update(class_job_category_id=None), lambda row: row.update(equippable=True)):
            fixture = self.fixture("equipment")
            mutate(fixture["result"]["entries"][0]["item"])
            with self.assertRaises(RuntimeError):
                self.validate(fixture)

    def test_legacy_scope_keeps_old_tool_and_result_contract(self):
        legacy = {"scope": LEGACY_SCOPE}
        v2 = {"scope": V2_SCOPE}
        current = {"scope": SCOPE}
        self.assertEqual(validate_summary(legacy), legacy)
        self.assertEqual(set(permitted_tool(legacy)["inputSchema"]["properties"]["kind"]["enum"]), {"recipes", "actions", "quests"})
        self.assertEqual(set(permitted_tool(v2)["inputSchema"]["properties"]["kind"]["enum"]), {"items", "recipes", "actions", "quests"})
        self.assertEqual(set(permitted_tool(current)["inputSchema"]["properties"]["kind"]["enum"]), {"maps", "items", "recipes", "actions", "quests"})
        self.assertNotIn("items uses base Item row IDs", permitted_instructions(legacy))
        self.assertIn("items uses base Item row IDs", permitted_instructions(v2))
        self.assertIn("items uses base Item row IDs", permitted_instructions(current))
        self.assertNotIn("compai-map://", permitted_instructions(legacy))
        self.assertNotIn("compai-map://", permitted_instructions(v2))
        self.assertIn("compai-map://", permitted_instructions(current))
        item_args = {"kind": "items", "row_id": 500}
        self.assertEqual(validate_arguments(item_args, current)["kind"], "items")
        with self.assertRaises(RuntimeError):
            validate_arguments(item_args, legacy)
        fixture = self.fixture()
        fixture["result"]["scope"] = LEGACY_SCOPE
        fixture["result"]["entries"][0].pop("item")
        fixture["result"]["entries"][0].pop("map")
        validate_result(fixture["result"], legacy, fixture["arguments"])
        with self.assertRaises(RuntimeError):
            validate_result(fixture["result"], current, fixture["arguments"])
        fixture["result"]["entries"][0]["item"] = None
        with self.assertRaises(RuntimeError):
            validate_result(fixture["result"], legacy, fixture["arguments"])
        fixture = self.fixture("item")
        fixture["result"]["scope"] = LEGACY_SCOPE
        with self.assertRaises(RuntimeError):
            validate_result(fixture["result"], legacy, fixture["arguments"])
        fixture = self.fixture("item")
        fixture["result"]["scope"] = V2_SCOPE
        fixture["result"]["entries"][0].pop("map")
        validate_result(fixture["result"], v2, fixture["arguments"])
        fixture["result"]["entries"][0]["map"] = None
        with self.assertRaises(RuntimeError):
            validate_result(fixture["result"], v2, fixture["arguments"])
        for summary in (legacy, v2):
            with self.assertRaises(RuntimeError):
                validate_arguments({"kind": "maps", "row_id": 800}, summary)
            fixture = self.fixture("map")
            fixture["result"]["scope"] = summary["scope"]
            with self.assertRaises(RuntimeError):
                validate_result(fixture["result"], summary, fixture["arguments"])
        independent = permitted_tool(current)
        independent["inputSchema"]["properties"]["kind"]["enum"].clear()
        self.assertEqual(len(permitted_tool(current)["inputSchema"]["properties"]["kind"]["enum"]), 5)

    def test_map_fields_are_public_bounded_metadata_without_inferred_coordinates(self):
        for mutate in (lambda row: row.update(territory_id=0), lambda row: row.update(territory_id=65536),
                       lambda row: row.update(territory_id=True), lambda row: row.update(territory_id=100.0),
                       lambda row: row.update(size_factor=0), lambda row: row.update(size_factor=65536),
                       lambda row: row.update(size_factor=True), lambda row: row.update(size_factor=200.0),
                       lambda row: row.update(territory_name=None), lambda row: row.update(territory_name="bad\nname"),
                       lambda row: row.update(region_name="x" * 129), lambda row: row.update(floor_name=2),
                       lambda row: row.update(floor_name="bad\u0085floor"), lambda row: row.update(x=12.3, y=15.4),
                       lambda row: row.update(unlocked=True), lambda row: row.update(visited=True),
                       lambda row: row.update(map_id=800), lambda row: row.update(url="compai-map://100/800?x=12&y=13")):
            fixture = self.fixture("map")
            mutate(fixture["result"]["entries"][0]["map"])
            with self.subTest(mutation=mutate), self.assertRaises(RuntimeError):
                self.validate(fixture)
        fixture = self.fixture("map")
        fixture["result"]["entries"][0]["map"].update(region_name=None, floor_name=None, size_factor=65535)
        self.validate(fixture)
        self.assertEqual(validate_arguments({"kind": "maps", "row_id": 65535})["row_id"], 65535)
        floors = self.fixture("map_floors")
        self.validate(floors)
        self.assertEqual([row["map"]["floor_name"] for row in floors["result"]["entries"]], ["First Floor", "Second Floor"])

    def test_map_entries_require_exclusive_payload_and_bounded_map_identity(self):
        for mutate in (lambda row: row.update(map=None), lambda row: row.pop("map"),
                       lambda row: row.update(row_id=65536), lambda row: row.update(row_id=True),
                       lambda row: row.update(item=self.fixture("item")["result"]["entries"][0]["item"]),
                       lambda row: row.update(name="Other zone")):
            fixture = self.fixture("map_floors")
            mutate(fixture["result"]["entries"][0])
            with self.assertRaises(RuntimeError):
                self.validate(fixture)
        fixture = self.fixture("recipe")
        fixture["result"]["entries"][0]["map"] = self.fixture("map")["result"]["entries"][0]["map"]
        with self.assertRaises(RuntimeError):
            self.validate(fixture)

    def test_result_kind_filter_ids_and_private_state_injection_rejected(self):
        mutations = [lambda row: row.update(kind="quests"), lambda row: row.update(extra=True),
                     lambda row: row.update(available=True, unavailable_reason="bad"),
                     lambda row: row["entries"][0].update(row_id=999),
                     lambda row: row["entries"][0].update(unlocked=True),
                     lambda row: row["entries"][0]["recipe"].update(character_availability="available"),
                     lambda row: row["entries"][0]["recipe"].update(output_quantity=True),
                     lambda row: row["entries"][0]["recipe"]["ingredients"][0].update(quantity=0),
                     lambda row: row["entries"][0]["recipe"].update(crafting_level=0),
                     lambda row: row["entries"][0].update(name="bad\nname")]
        for mutation in mutations:
            fixture = self.fixture()
            mutation(fixture["result"])
            with self.assertRaises(RuntimeError):
                self.validate(fixture)
        fixture = self.fixture("localized")
        fixture["arguments"]["query"] = "strasse"
        with self.assertRaises(RuntimeError):
            self.validate(fixture)

    def test_pagination_rejects_nonprogressing_forged_or_empty_pages(self):
        for mutate in (lambda row: row.update(next_offset=20), lambda row: row.update(next_offset=True),
                       lambda row: row.update(offset=0), lambda row: row.update(matched_count=21),
                       lambda row: row.update(entries=[]), lambda row: row.update(matched_count=65537),
                       lambda row: row["entries"].reverse()):
            fixture = self.fixture("pagination")
            mutate(fixture["result"])
            with self.assertRaises(RuntimeError):
                self.validate(fixture)
        fixture = self.fixture("unavailable")
        fixture["result"].update(entries=self.fixture()["result"]["entries"], matched_count=1)
        with self.assertRaises(RuntimeError):
            self.validate(fixture)

    def test_action_unknown_description_and_static_times_are_strict(self):
        for mutate in (lambda row: row.update(description_unknown_reason="Unexpected"),
                       lambda row: row.update(base_recast_100ms=2.5), lambda row: row.update(is_pvp=1),
                       lambda row: row.update(class_job_id=None, class_job_name="White Mage"),
                       lambda row: row.update(description="x" * 1601), lambda row: row.update(character_availability=True)):
            fixture = self.fixture("action")
            mutate(fixture["result"]["entries"][0]["action"])
            with self.assertRaises(RuntimeError):
                self.validate(fixture)
        fixture = self.fixture("unknown_description")
        fixture["result"]["entries"][0]["action"]["description_unknown_reason"] = None
        with self.assertRaises(RuntimeError):
            self.validate(fixture)

    def test_quest_metadata_never_accepts_inferred_prerequisites_or_story(self):
        for mutate in (lambda row: row.update(prerequisite_logic="all"), lambda row: row.update(journal="Story"),
                       lambda row: row.update(previous_quest_ids=[1, 1]), lambda row: row.update(level_values=[1, 2, 3]),
                       lambda row: row.update(previous_quest_join=True), lambda row: row.update(repeatable="false")):
            fixture = self.fixture("quest")
            mutate(fixture["result"]["entries"][0]["quest"])
            with self.assertRaises(RuntimeError):
                self.validate(fixture)

    def test_wire_budget_and_instructions(self):
        fixture = self.fixture("unicode_budget")
        fixture["result"]["entries"] *= 20
        with self.assertRaises(RuntimeError):
            self.validate(fixture)
        self.assertEqual(TOOL["name"], "query_game_definitions")
        self.assertIn("uninterpreted", INSTRUCTIONS)
        self.assertIn("100ms", INSTRUCTIONS)
        self.assertIn("base Item row IDs", INSTRUCTIONS)
        self.assertIn("not owned quantity", INSTRUCTIONS)
        self.assertIn("Do not invent IDs, coordinates or floors", INSTRUCTIONS)
        self.assertIn("generating a link never opens menus", INSTRUCTIONS)


if __name__ == "__main__":
    unittest.main()
