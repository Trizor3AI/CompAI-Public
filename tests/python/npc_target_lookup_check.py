"""Synthetic selected-target grants, schemas, freshness and C# producer checks."""
from copy import deepcopy
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import unittest

from game_context_lookup import timestamp
from npc_target_lookup import (LEGACY_SCOPE, NPC_KINDS, OTHER_KINDS, SCOPE, lodestone_search,
                               permitted_instructions, permitted_tool, validate_arguments,
                               validate_summary, validate_result)


def summary(npcs=True, players=False):
    return {"scope": SCOPE, "allow_npcs": npcs, "allow_players_and_objects": players}


def result(**changes):
    return {"scope": SCOPE, "captured_at_utc": datetime.now(timezone.utc).isoformat(), "available": True,
            "unavailable_reason": None, "coverage": "One selected permitted target; no inferred services or actions.",
            "target": {"name": "Synthetic Merchant", "data_id": 123, "kind": "event_npc", "player": None}, **changes}


def player_target(name="Synthetic Player", world="Example World"):
    return {"name": name, "data_id": None, "kind": "player", "player": {
        "home_world_id": 23 if world else None, "home_world_name": world,
        "class_job_id": 19, "class_job_name": "Paladin", "level": 90,
        "lodestone_search_url": lodestone_search(name, world)}}


class NpcTargetChecks(unittest.TestCase):
    def test_exact_summary_empty_arguments_and_scope_tools(self):
        self.assertIsNone(validate_summary(None))
        for grants in ((True, False), (False, True), (True, True)):
            value = summary(*grants)
            self.assertEqual(validate_summary(value), value)
            self.assertEqual(permitted_tool(value)["name"], "query_selected_target")
            self.assertIn("NPC targets are permitted." if grants[0] else "NPC targets are not permitted.",
                          permitted_instructions(value))
        self.assertEqual(validate_arguments({}), {})
        for value in ({}, {"scope": SCOPE}, {"scope": "actors"}, summary(False, False), summary(1, False),
                      summary(True, 1), {**summary(), "target": 1}):
            with self.subTest(value=value), self.assertRaises(RuntimeError):
                validate_summary(value)
        for value in ([], None, {"npc_id": 1}, {"view": "location"}):
            with self.assertRaises(RuntimeError):
                validate_arguments(value)

    def test_each_kind_obeys_its_own_grant(self):
        for kind in NPC_KINDS | OTHER_KINDS:
            target = player_target() if kind == "player" else {
                "name": "Synthetic", "data_id": 123, "kind": kind, "player": None}
            for npcs, players in ((True, False), (False, True), (True, True)):
                with self.subTest(kind=kind, npcs=npcs, players=players):
                    allowed = npcs if kind in NPC_KINDS else players
                    if allowed:
                        validate_result(result(target=target), summary(npcs, players), {})
                    else:
                        with self.assertRaises(RuntimeError):
                            validate_result(result(target=target), summary(npcs, players), {})
        for kind in ("aetheryte", "pc", "none", "unknown", "future", [], None):
            with self.subTest(kind=kind), self.assertRaises(RuntimeError):
                validate_result(result(target={**result()["target"], "kind": kind}), summary(True, True), {})

    def test_only_exact_basic_target_fields_are_accepted(self):
        for changes in ({"data_id": True}, {"data_id": 0}, {"data_id": 4294967296}, {"name": "bad\nname"},
                        {"name": "\ud800"}, {"name": "x" * 129}, {"name": " "}, {"player": {}},
                        {"position": [1, 2, 3]}, {"content_id": 999}, {"entity_id": 999}, {"owner_id": 999},
                        {"shop_inventory": []}, {"quests": []}, {"statuses": []}, {"equipment": []}):
            value = result()
            value["target"].update(changes)
            with self.subTest(changes=changes), self.assertRaises(RuntimeError):
                validate_result(value, summary(), {})
        validate_result(result(target={**result()["target"], "data_id": None}), summary(), {})

    def test_player_details_are_bounded_nullable_and_not_inspection(self):
        validate_result(result(target=player_target()), summary(False, True), {})
        target = player_target(world=None)
        target["player"].update(class_job_id=None, class_job_name=None, level=None)
        validate_result(result(target=target), summary(False, True), {})
        for changes in ({"home_world_id": 0}, {"home_world_id": True}, {"home_world_id": None},
                        {"class_job_id": 0}, {"class_job_id": None}, {"home_world_name": "bad\nworld"},
                        {"class_job_name": "x" * 129}, {"level": 0}, {"level": 256}, {"level": True},
                        {"current_world": "Other World"}, {"content_id": 999}, {"equipment": []}):
            target = player_target()
            target["player"].update(changes)
            with self.subTest(changes=changes), self.assertRaises(RuntimeError):
                validate_result(result(target=target), summary(False, True), {})
        for changes in ({"data_id": 123}, {"player": None}, {"player": []}):
            with self.subTest(changes=changes), self.assertRaises(RuntimeError):
                validate_result(result(target={**player_target(), **changes}), summary(False, True), {})

    def test_lodestone_link_is_an_exact_escaped_search_not_a_profile(self):
        target = player_target("Synthetic O'Name &?", "Example World")
        expected = ("https://na.finalfantasyxiv.com/lodestone/character/"
                    "?q=Synthetic%20O%27Name%20%26%3F&worldname=Example%20World")
        self.assertEqual(target["player"]["lodestone_search_url"], expected)
        validate_result(result(target=target), summary(False, True), {})
        for link in (None, "https://na.finalfantasyxiv.com/lodestone/character/12345/", expected + "&extra=1",
                     expected.replace("https:", "http:"), expected.replace("Synthetic", "SomeoneElse")):
            target["player"]["lodestone_search_url"] = link
            with self.subTest(link=link), self.assertRaises(RuntimeError):
                validate_result(result(target=target), summary(False, True), {})
        missing = player_target(world=None)
        missing["player"]["lodestone_search_url"] = expected
        with self.assertRaises(RuntimeError):
            validate_result(result(target=missing), summary(False, True), {})

    def test_stale_unavailable_and_changed_envelopes_fail_closed(self):
        now = datetime.now(timezone.utc)
        for changes in ({"captured_at_utc": (now - timedelta(seconds=31)).isoformat()},
                        {"captured_at_utc": (now + timedelta(seconds=3)).isoformat()}, {"available": 1},
                        {"available": False}, {"target": None}, {"unavailable_reason": "Unexpected"},
                        {"coverage": ""}, {"scope": LEGACY_SCOPE}, {"npc": {}}, {"owner_id": 999}):
            with self.subTest(changes=changes), self.assertRaises(RuntimeError):
                validate_result(result(**changes), summary(), {}, now=now)
        unavailable = result(available=False, unavailable_reason="No supported permitted target", target=None)
        validate_result(unavailable, summary(), {})
        unavailable["captured_at_utc"] = (now - timedelta(days=1)).isoformat()
        validate_result(unavailable, summary(), {}, now=now)

    def test_legacy_schema_remains_npc_only(self):
        legacy_summary = {"scope": LEGACY_SCOPE}
        self.assertEqual(validate_summary(legacy_summary), legacy_summary)
        self.assertEqual(permitted_tool(legacy_summary)["name"], "query_npc_target")
        value = result(scope=LEGACY_SCOPE)
        target = value.pop("target")
        value["npc"] = {key: target[key] for key in ("name", "data_id", "kind")}
        validate_result(value, legacy_summary, {})
        for changes in ({"kind": "player"}, {"kind": "event_object"}, {"kind": "aetheryte"},
                        {"data_id": None}, {"player": None}):
            invalid = deepcopy(value)
            invalid["npc"].update(changes)
            with self.subTest(changes=changes), self.assertRaises(RuntimeError):
                validate_result(invalid, legacy_summary, {})
        with self.assertRaises(RuntimeError):
            validate_result(value, summary(), {})
        with self.assertRaises(RuntimeError):
            validate_result(result(), legacy_summary, {})
        with self.assertRaises(RuntimeError):
            validate_summary({**legacy_summary, "allow_players_and_objects": True})

    def test_csharp_producer_results(self):
        path = Path(__file__).resolve().parents[2] / ".test-tmp" / "npc-target-result.json"
        self.assertTrue(path.exists(), "Run NpcTarget.Tests --produce-fixtures .test-tmp/npc-target-result.json")
        fixtures = json.loads(path.read_text(encoding="utf-8"))
        self.assertEqual({fixture["name"] for fixture in fixtures},
                         {"event_npc", "battle_npc", "player", "event_object", "unavailable", "stale"})
        for fixture in fixtures:
            value = deepcopy(fixture["result"])
            with self.subTest(fixture=fixture["name"]):
                validate_result(value, fixture["summary"], fixture["arguments"], now=timestamp(value["captured_at_utc"]))


if __name__ == "__main__":
    unittest.main()
