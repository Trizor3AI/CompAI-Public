"""Offline wire32 reader permissions, evidence contracts and C# producer roundtrips."""
from copy import deepcopy
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import unittest
from unittest.mock import patch

import collection_lookup
import companion
import progression_lookup
import deferred_fixture
from collection_lookup_check import prepared, result as collection_result
from progression_lookup_check import result as progression_result
from companion_architecture_check import wire_request
from companion_request import normalize_request, recheck_request
from companion_tools import build_registry
from deferred_tools import DeferredTools
from quest_lookup import encoded_size
from quest_lookup_check import completed, tool_message


CASES = {
    "triple_triad": (collection_lookup, "collection_lookup", "collections", "collection_query", "query_collections"),
    "battle_chocobo": (progression_lookup, "progression_lookup", "progression", "progression_query", "query_progression"),
    "pvp": (progression_lookup, "progression_lookup", "progression", "progression_query", "query_progression"),
}
PRODUCERS = {
    "triple_triad": ("triple-triad-producer.json", "TripleTriad.Tests", 8),
    "battle_chocobo": ("battle-chocobo-results.json", "BattleChocobo.Tests", 7),
    "pvp": ("pvp-csharp-fixtures.json", "Pvp.Tests", 4),
}


def request(category, version=32):
    module, field, *_ = CASES[category]
    value = wire_request(version)
    value[field] = {"scope": module.SCOPE, "categories": [category]}
    return value


def result(category):
    return collection_result(category) if category == "triple_triad" else progression_result(category)


def event(category, ident=50, **changes):
    return tool_message(ident=ident, tool=CASES[category][4], arguments={"category": category, **changes})


class ActivityReaderChecks(unittest.TestCase):
    def test_wire32_is_required_and_all_previous_category_sets_stay_frozen(self):
        for category, (module, field, *_rest) in CASES.items():
            summary = request(category)[field]
            self.assertEqual(module.validate_summary(summary, version=32), summary)
            self.assertEqual(module.validate_summary(summary, version=36), summary)
            self.assertEqual(getattr(normalize_request(request(category)), field), summary)
            for version in range(1, 32):
                with self.subTest(category=category, version=version), self.assertRaises(RuntimeError):
                    normalize_request(request(category, version))
            with self.assertRaises(RuntimeError):
                module.validate_summary(summary, version=37)
        for version in range(27, 32):
            summary = {"scope": collection_lookup.SCOPE, "categories": list(collection_lookup.V27_CATEGORIES)}
            self.assertEqual(collection_lookup.validate_summary(summary, version=version), summary)
        summary = {"scope": progression_lookup.SCOPE, "categories": list(progression_lookup.V31_CATEGORIES)}
        self.assertEqual(progression_lookup.validate_summary(summary, version=31), summary)
        # Every optional older field still normalizes on the newest envelope, including a real game-context summary.
        wire = request("triple_triad", version=36)
        wire["game_context_lookup"] = {
            "scope": "local_character_and_npc_dialogue", "captured_at_utc": datetime.now(timezone.utc).isoformat(),
            "character_allowed": True, "character_available": False, "location_allowed": False,
            "location_available": False, "dialogue_allowed": False, "dialogue_available": False,
        }
        self.assertIsNotNone(normalize_request(wire).game_context_lookup)

    def test_each_grant_scopes_discovery_schema_instructions_and_permission_only_context(self):
        for category, (module, field, reader, callback, tool_name) in CASES.items():
            with self.subTest(category=category):
                wire = request(category)
                approved = normalize_request(wire)
                registry = build_registry(approved, {reader: lambda _: self.fail("Discovery read state")}, None,
                                          companion.CompanionFailure)
                self.assertEqual(set(registry), {tool_name})
                definition = registry[tool_name].definition
                self.assertEqual(definition["inputSchema"]["properties"]["category"]["enum"], [category])
                self.assertIn(category, definition["description"])
                self.assertLessEqual(len(definition["description"]), 512)
                self.assertIn(module.CATEGORY_INSTRUCTIONS[category], registry[tool_name].instructions)
                for unrelated in set(module.CATEGORY_INSTRUCTIONS) - {category}:
                    self.assertNotIn(module.CATEGORY_INSTRUCTIONS[unrelated], registry[tool_name].instructions)
                discovery = DeferredTools(registry)
                self.assertIn(category, discovery.instructions)
                self.assertEqual(discovery.discover({"names": [tool_name]})["tools"], [definition])
                self.assertEqual(getattr(approved, field), wire[field])
                self.assertNotIn("entries", json.dumps(approved.context({})))
                wire[field] = None
                with self.assertRaises(RuntimeError):
                    recheck_request(wire, approved)
                self.assertEqual(build_registry(normalize_request(wire), {}, None, companion.CompanionFailure), {})
        # All grants together remain inside the existing discovery description bounds.
        for module in (collection_lookup, progression_lookup):
            self.assertLessEqual(len(module.permitted_tool({"scope": module.SCOPE, "categories": list(module.SOURCES)})["description"]), 512)

    def test_unauthorized_category_is_fatal_even_if_tool_schema_and_argument_validator_are_bypassed(self):
        for category, (module, field, _reader, callback, _tool_name) in CASES.items():
            wire = request(category)
            wire[field]["categories"] = ["mounts" if category == "triple_triad" else "achievements"]
            client, _, outputs = prepared([event(category)])
            with self.subTest(category=category), patch.object(module, "permitted_tool", return_value=deepcopy(module.TOOL)), \
                    patch.object(module, "validate_arguments", side_effect=lambda args, *_: args), \
                    patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure) as failure:
                client.ask(wire, **{callback: lambda _: self.fail("Unauthorized reader executed")})
            self.assertEqual(failure.exception.kind, "permission")
            self.assertFalse(outputs[-1]["result"]["success"])

    def test_revoked_and_cancelled_reads_never_deliver_data(self):
        for category, (_module, _field, _reader, callback, _tool_name) in CASES.items():
            client, _, outputs = prepared([event(category)])
            def denied(_):
                raise PermissionError("Synthetic permission revoked")
            with self.subTest(category=category), patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure) as failure:
                client.ask(request(category), **{callback: denied})
            self.assertEqual(failure.exception.kind, "permission")
            self.assertFalse(outputs[-1]["result"]["success"])
            client, _, outputs = prepared([event(category)])
            def cancelled(_):
                client.cancel_event.set()
                return result(category)
            with patch.object(companion, "emit"), self.assertRaises(RuntimeError):
                client.ask(request(category), **{callback: cancelled})
            self.assertFalse(outputs[-1]["result"]["success"])

    def test_three_categories_transport_only_after_discovery_and_freeze_returned_rows(self):
        for category, (module, field, _reader, callback, _tool_name) in CASES.items():
            first = result(category)
            client, calls, outputs = prepared([event(category), *completed()])
            with self.subTest(category=category), patch.object(companion, "emit"):
                client.ask(request(category), **{callback: lambda _, data=first: deepcopy(data)})
            self.assertEqual(json.loads(outputs[-1]["result"]["contentItems"][0]["text"]), first)
            deferred_fixture.assert_exposed(self, client, calls, [module.permitted_tool(request(category)[field])])
            changed = deepcopy(first)
            changed["entries"][0]["name"] += " changed"
            client, _, outputs = prepared([event(category), event(category, ident=51)])
            replies = iter((first, changed))
            with patch.object(companion, "emit"), self.assertRaises(RuntimeError):
                client.ask(request(category), **{callback: lambda _: next(replies)})
            self.assertTrue(outputs[0]["result"]["success"])
            self.assertFalse(outputs[-1]["result"]["success"])

    def test_triple_triad_keeps_positive_only_evidence_and_rejects_retained_menu_readiness(self):
        meta, args = request("triple_triad")["collection_lookup"], {"category": "triple_triad"}
        value = result("triple_triad")
        collection_lookup.validate_result(value, meta, args)
        self.assertIsNone(value["entries"][1]["unlocked"])
        for fields in ({"unlocked": False}, {"state": "locked"}, {"deck": 1}, {"quantity": 1}):
            invalid = deepcopy(value)
            invalid["entries"][1].update(fields)
            with self.subTest(fields=fields), self.assertRaises(RuntimeError):
                collection_lookup.validate_result(invalid, meta, args)
        with self.assertRaises(RuntimeError):
            collection_lookup.validate_result({**value, "readiness": "last_observed_positive_observations"}, meta, args)
        invalid = deepcopy(value)
        invalid["entries"][0]["id"] = 476
        with self.assertRaises(RuntimeError):
            collection_lookup.validate_result(invalid, meta, args)

    def test_battle_chocobo_field_ids_counter_bounds_and_rank20_target_are_exact(self):
        meta, args = request("battle_chocobo")["progression_lookup"], {"category": "battle_chocobo"}
        value = result("battle_chocobo")
        progression_lookup.validate_result(value, meta, args)
        capped = deepcopy(value)
        capped["entries"][0].update(rank=20, experience=4294967295, experience_required=None)
        progression_lookup.validate_result(capped, meta, args)
        for index, fields in ((0, {"rank": 0}), (0, {"rank": 21}), (0, {"rank": True}),
                              (0, {"experience": 101}), (0, {"experience_required": None}),
                              (0, {"experience_required": 0}), (0, {"experience": -1}),
                              (0, {"rank": 20}), (0, {"completed": False}), (0, {"level": 10}),
                              (1, {"rank": 11}), (1, {"experience": 0}), (1, {"progress": 0}),
                              (4, {"progress": 256}), (4, {"rank": 0}), (4, {"id": 6})):
            invalid = deepcopy(value)
            invalid["entries"][index].update(fields)
            with self.subTest(index=index, fields=fields), self.assertRaises(RuntimeError):
                progression_lookup.validate_result(invalid, meta, args)
        with self.assertRaises(RuntimeError):
            progression_lookup.validate_arguments({"category": "battle_chocobo", "state": "completed"}, meta)

    def test_pvp_preserves_two_experience_domains_without_invented_targets(self):
        meta, args = request("pvp")["progression_lookup"], {"category": "pvp"}
        value = result("pvp")
        value["entries"][0].update(rank=255, experience=4294967295)
        value["entries"][1].update(rank=0, experience=65535)
        progression_lookup.validate_result(value, meta, args)
        for index, fields in ((0, {"experience": 4294967296}), (1, {"experience": 65536}),
                              (0, {"rank": 256}), (1, {"rank": -1}), (0, {"rank": True}),
                              (0, {"id": 3}), (0, {"experience_required": 100}), (0, {"completed": False}),
                              (1, {"progress": 0}), (1, {"required": 100}), (1, {"level": 1})):
            invalid = deepcopy(value)
            invalid["entries"][index].update(fields)
            with self.subTest(index=index, fields=fields), self.assertRaises(RuntimeError):
                progression_lookup.validate_result(invalid, meta, args)

    def test_pvp_retention_is_category_specific_and_expires_after_fifteen_minutes(self):
        meta, args = request("pvp")["progression_lookup"], {"category": "pvp"}
        now = datetime.now(timezone.utc)
        value = result("pvp")
        value.update(readiness="last_observed_profile", captured_at_utc=(now - timedelta(minutes=14)).isoformat())
        progression_lookup.validate_result(value, meta, args)
        for age in (15, 30):
            with self.subTest(age=age), self.assertRaises(RuntimeError):
                progression_lookup.validate_result({**value, "captured_at_utc": (now - timedelta(minutes=age)).isoformat()}, meta, args)
        for category in ("battle_chocobo", "trust", "achievements"):
            with self.subTest(category=category), self.assertRaises(RuntimeError):
                progression_lookup.validate_result({**progression_result(category), "readiness": "last_observed_profile"},
                                                   {"scope": progression_lookup.SCOPE, "categories": [category]}, {"category": category})
        for category in ("battle_chocobo", "pvp"):
            value = result(category)
            value.update(readiness="saved_menu_observations", source="Saved local " + progression_lookup.SOURCES[category])
            with self.assertRaises(RuntimeError):
                progression_lookup.validate_result(value, request(category)["progression_lookup"], {"category": category})

    def test_complete_field_catalog_counts_are_not_zero_or_partial_when_available(self):
        for category in ("battle_chocobo", "pvp"):
            value = result(category)
            meta, args = request(category)["progression_lookup"], {"category": category}
            for count in (0, len(value["entries"]) - 1, len(value["entries"]) + 1):
                invalid = deepcopy(value)
                invalid.update(catalog_count=count, observed_count=count, matched_count=0, entries=[])
                with self.subTest(category=category, count=count), self.assertRaises(RuntimeError):
                    progression_lookup.validate_result(invalid, meta, {**args, "ids": [99]})

    def test_verified_venture_bothers_counter_12_of_15_survives_wire31_and32(self):
        category = "challenge_log"
        meta, args = {"scope": progression_lookup.SCOPE, "categories": [category]}, {"category": category}
        now = datetime.now(timezone.utc)
        value = progression_result(category)
        observed = {**value["entries"][1], "id": 34, "name": "Synthetic Venture Bothers", "progress": 12, "required": 15}
        value.update(entries=[observed], captured_at_utc=now.isoformat(), catalog_count=1, completed_count=0,
                     not_observed_complete_count=1, unknown_count=0, matched_count=1)
        saved = deepcopy(value)
        saved.update(readiness="saved_menu_observations", source="Saved local " + progression_lookup.SOURCES[category])
        saved["entries"][0].update(observed_at_utc=now.isoformat(), observed_page=0)
        for version in (31, 32):
            wire = wire_request(version)
            wire["progression_lookup"] = meta
            for data in (value, saved):
                with self.subTest(version=version, readiness=data["readiness"]):
                    progression_lookup.validate_result(data, meta, args)
                    message = tool_message(ident=50, tool="query_progression", arguments=args)
                    client, _, outputs = prepared([message, *completed()])
                    with patch.object(companion, "emit"):
                        client.ask(wire, progression_query=lambda _, data=data: deepcopy(data))
                    delivered = json.loads(outputs[-1]["result"]["contentItems"][0]["text"])
                    self.assertEqual((delivered["entries"][0]["progress"], delivered["entries"][0]["required"]), (12, 15))
        invalid = deepcopy(value)
        invalid["entries"][0]["id"] = 35
        with self.assertRaises(RuntimeError):
            progression_lookup.validate_result(invalid, meta, args)

    def test_required_csharp_producers_survive_real_deferred_transport(self):
        for category, (filename, project, minimum) in PRODUCERS.items():
            path = Path(__file__).resolve().parents[2] / ".test-tmp" / filename
            self.assertTrue(path.exists(), f"Produce required synthetic fixtures: dotnet run --project tests/{project} -- --produce-fixtures .test-tmp/{filename}")
            fixtures = json.loads(path.read_text(encoding="utf-8"))
            self.assertGreaterEqual(len(fixtures), minimum)
            module, field, _reader, callback, _tool = CASES[category]
            unavailable_seen = page_seen = False
            for fixture in fixtures:
                with self.subTest(category=category, name=fixture["name"]):
                    value, arguments = fixture["result"], fixture["arguments"]
                    self.assertEqual(arguments["category"], category)
                    fixed = datetime.fromisoformat(value["captured_at_utc"])
                    class FixtureDatetime(datetime):
                        @classmethod
                        def now(cls, tz=None):
                            return fixed
                    # Freeze synthetic producer validation at capture; retention expiry is tested separately.
                    with patch.object(module, "datetime", FixtureDatetime):
                        module.validate_result(value, request(category)[field], arguments)
                    self.assertLessEqual(encoded_size(value), module.MAX_RESULT_BYTES)
                    unavailable_seen |= not value["available"]
                    page_seen |= value["truncated"]
                    client, _, outputs = prepared([event(category, **{key: val for key, val in arguments.items() if key != "category"}), *completed()])
                    with patch.object(companion, "emit"), patch.object(module, "datetime", FixtureDatetime):
                        client.ask(request(category), **{callback: lambda _, data=value: deepcopy(data)})
                    self.assertEqual(json.loads(outputs[-1]["result"]["contentItems"][0]["text"]), value)
            self.assertTrue(unavailable_seen)
            self.assertTrue(page_seen)


if __name__ == "__main__":
    unittest.main()
