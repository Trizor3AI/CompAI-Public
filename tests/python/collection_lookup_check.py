"""Offline collection protocol, category permissions, deferred transport and producer checks."""
from copy import deepcopy
from datetime import datetime, timedelta, timezone
import json
import io
from pathlib import Path
import threading
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import companion
import deferred_fixture
from collection_lookup import (SCOPE, SOURCES, TOOL, INSTRUCTIONS, COUNT_FIELDS, MAX_RESULT_BYTES,
                               LEGACY_CATEGORIES, V24_CATEGORIES, V26_CATEGORIES, V27_CATEGORIES, CATEGORY_INSTRUCTIONS,
                               permitted_categories, permitted_tool, permitted_instructions,
                               validate_arguments, validate_result, validate_summary)
import collection_lookup
from companion_tools import build_registry
from companion_architecture_check import wire_request
from companion_request import normalize_request, recheck_request
import inventory_question_check as question_checks
from quest_lookup import QuestQueryBridge, encoded_size
from quest_lookup_check import completed, tool_message


def request(**changes):
    value = wire_request(23)
    value["collection_lookup"] = {"scope": SCOPE}
    value.update(changes)
    return value


def expanded_request(categories=None, **changes):
    value = request(version=24, collection_lookup={
        "scope": SCOPE, "categories": list(V24_CATEGORIES) if categories is None else categories})
    value.update(changes)
    return value


def modern_request(categories=None, **changes):
    value = wire_request(26)
    value["collection_lookup"] = {"scope": SCOPE, "categories": list(V26_CATEGORIES) if categories is None else categories}
    value.update(changes)
    return value


def latest_request(categories=None, **changes):
    value = wire_request(27)
    value["collection_lookup"] = {"scope": SCOPE, "categories": list(V27_CATEGORIES) if categories is None else categories}
    value.update(changes)
    return value


def result(category="mounts", **changes):
    return {
        "scope": SCOPE, "category": category, "captured_at_utc": datetime.now(timezone.utc).isoformat(),
        "available": True, "unavailable_reason": None, "source": SOURCES[category],
        "readiness": "positive_observations_only", "coverage": "Synthetic named catalog; negatives remain unknown.",
        "catalog_count": 3, "observed_unlocked_count": 1, "not_observed_unlocked_count": 1, "unknown_count": 1,
        "entries": [
            {"id": 1, "name": "Synthetic Cloud", "state": "unlocked", "unlocked": True, "unknown_reason": None},
            {"id": 2, "name": "Synthetic Mist", "state": "not_observed_unlocked", "unlocked": None,
             "unknown_reason": "Negative-state readiness is unverified."},
            {"id": 3, "name": "Synthetic Rain", "state": "unknown", "unlocked": None,
             "unknown_reason": "Unsupported native mapping."},
        ],
        "matched_count": 3, "offset": 0, "limit": 50, "next_offset": None, "truncated": False,
        **changes,
    }


def unavailable(category="mounts"):
    return result(category, available=False, unavailable_reason="Synthetic loading",
                  readiness="unavailable", **dict.fromkeys(COUNT_FIELDS),
                  entries=[], matched_count=0)


def event(ident=50, **arguments):
    return tool_message(ident=ident, tool="query_collections", arguments={"category": "mounts", **arguments})


def prepared(events, *, raw=False):
    client, calls = question_checks.QuestionChecks().client(events) if raw else deferred_fixture.client(events)
    payload = request()
    client.model, client.effort, client.web_search = payload["model"], payload["reasoning_effort"], False
    outputs = []
    client.send = outputs.append
    return client, calls, outputs


class CollectionChecks(unittest.TestCase):
    def test_recent_bestiary_readiness_is_category_scoped_and_expires(self):
        now = datetime.now(timezone.utc)
        meta = {"scope": SCOPE, "categories": ["beastmaster_bestiary"]}
        args = {"category": "beastmaster_bestiary"}
        value = result("beastmaster_bestiary", readiness="last_observed_positive_observations",
                       captured_at_utc=(now - timedelta(minutes=14)).isoformat())
        validate_result(value, meta, args)
        with self.assertRaises(RuntimeError):
            validate_result({**value, "captured_at_utc": (now - timedelta(minutes=15)).isoformat()}, meta, args)
        with self.assertRaises(RuntimeError):
            validate_result(result("mounts", readiness="last_observed_positive_observations"),
                            {"scope": SCOPE, "categories": ["mounts"]}, {"category": "mounts"})

    def test_guidance_contains_only_granted_category_paragraphs_and_one_shared_block(self):
        self.assertEqual(set(CATEGORY_INSTRUCTIONS), set(SOURCES))
        for category in SOURCES:
            guidance = permitted_instructions({"scope": SCOPE, "categories": [category]})
            self.assertEqual(guidance.count(INSTRUCTIONS), 1)
            self.assertEqual(guidance.count("Never send private collection state"), 1)
            self.assertEqual(guidance.count("12 calls and 120KB"), 1)
            self.assertIn(CATEGORY_INSTRUCTIONS[category], guidance)
            for unrelated in SOURCES.keys() - {category}:
                self.assertNotIn(CATEGORY_INSTRUCTIONS[unrelated], guidance)
        all_guidance = permitted_instructions({"scope": SCOPE, "categories": list(SOURCES)})
        self.assertEqual(all_guidance.count(INSTRUCTIONS), 1)
        self.assertTrue(all(all_guidance.count(paragraph) == 1 for paragraph in CATEGORY_INSTRUCTIONS.values()))

    def test_bestiary_requires_v27_and_preserves_every_older_category_set(self):
        for version in (24, 25, 26):
            wire = wire_request(version)
            wire["collection_lookup"] = {"scope": SCOPE, "categories": ["beastmaster_bestiary"]}
            with self.subTest(version=version), self.assertRaises(RuntimeError):
                normalize_request(wire)
            with self.assertRaises(RuntimeError):
                validate_summary(wire["collection_lookup"], version=version)
            wire["collection_lookup"]["categories"] = list(V26_CATEGORIES if version == 26 else V24_CATEGORIES)
            self.assertEqual(normalize_request(wire).collection_lookup, wire["collection_lookup"])
        approved = normalize_request(latest_request(["beastmaster_bestiary"]))
        registry = build_registry(approved, {"collections": lambda _: None}, None, companion.CompanionFailure)
        self.assertEqual(set(registry), {"query_collections"})
        self.assertEqual(registry["query_collections"].definition["inputSchema"]["properties"]["category"]["enum"],
                         ["beastmaster_bestiary"])
        self.assertIsNone(approved.progression_lookup)

    def test_bestiary_denial_is_fatal_despite_bypassed_schema_and_validator(self):
        for wire in (request(), expanded_request(), modern_request(), latest_request(["blue_mage_spells"])):
            client, _, outputs = prepared([event(category="beastmaster_bestiary")])
            with patch.object(collection_lookup, "permitted_tool", return_value=deepcopy(TOOL)), \
                    patch.object(collection_lookup, "validate_arguments", side_effect=lambda args, *_: args), \
                    patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure) as failure:
                client.ask(wire, collection_query=lambda _: self.fail("Unpermitted Bestiary callback"))
            self.assertEqual(failure.exception.kind, "permission")
            self.assertFalse(outputs[0]["result"]["success"])

    def test_bestiary_observation_is_positive_only_with_precise_id_guidance(self):
        meta, arguments = {"scope": SCOPE, "categories": ["beastmaster_bestiary"]}, {"category": "beastmaster_bestiary"}
        value = result("beastmaster_bestiary")
        validate_result(value, meta, arguments)
        validate_result(unavailable("beastmaster_bestiary"), meta, arguments)
        for fields in ({"unlocked": False}, {"state": "uncaptured"}, {"pet_id": 47}, {"quantity": 1}):
            invalid = deepcopy(value)
            invalid["entries"][1].update(fields)
            with self.subTest(fields=fields), self.assertRaises(RuntimeError):
                validate_result(invalid, meta, arguments)
        client, calls, outputs = prepared([event(category="beastmaster_bestiary"), *completed()])
        with patch.object(companion, "emit"):
            client.ask(latest_request(["beastmaster_bestiary"]), collection_query=lambda _: value)
        self.assertTrue(outputs[0]["result"]["success"])
        deferred_fixture.assert_exposed(self, client, calls, [permitted_tool(meta)])
        self.assertIn("XBMPet book RowIds, not linked Pet RowIds", permitted_instructions(meta))
        self.assertIn("content-specific notebook data is excluded", permitted_instructions(meta))
        self.assertIn("no independent", permitted_instructions(meta))

    def test_bestiary_csharp_producer_survives_deferred_transport(self):
        path = Path(__file__).resolve().parents[2] / ".test-tmp" / "bestiary-producer.json"
        self.assertTrue(path.exists(), "Produce required .test-tmp/bestiary-producer.json with Bestiary.Tests")
        fixtures = json.loads(path.read_text(encoding="utf-8"))
        self.assertGreaterEqual(len(fixtures), 4)
        meta = {"scope": SCOPE, "categories": ["beastmaster_bestiary"]}
        states, unavailable_seen, page_seen = set(), False, False
        for fixture in fixtures:
            with self.subTest(name=fixture["name"]):
                value, arguments = fixture["result"], fixture["arguments"]
                self.assertEqual(arguments["category"], "beastmaster_bestiary")
                validate_result(value, meta, arguments)
                states.update(entry["state"] for entry in value["entries"])
                unavailable_seen |= not value["available"]
                page_seen |= value["truncated"]
                client, _, outputs = prepared([event(**arguments), *completed()])
                with patch.object(companion, "emit"):
                    client.ask(latest_request(["beastmaster_bestiary"]), collection_query=lambda _, data=value: deepcopy(data))
                self.assertEqual(json.loads(outputs[0]["result"]["contentItems"][0]["text"]), value)
        self.assertEqual(states, {"unlocked", "not_observed_unlocked", "unknown"})
        self.assertTrue(unavailable_seen)
        self.assertTrue(page_seen)

    def test_blue_mage_requires_v26_and_explicit_independent_permission(self):
        for version in (24, 25):
            wire = wire_request(version)
            wire["collection_lookup"] = {"scope": SCOPE, "categories": ["blue_mage_spells"]}
            with self.subTest(version=version), self.assertRaises(RuntimeError):
                normalize_request(wire)
            wire["collection_lookup"]["categories"] = list(V24_CATEGORIES)
            self.assertEqual(normalize_request(wire).collection_lookup, wire["collection_lookup"])
        for version in (23, 24, 25):
            with self.subTest(version=version), self.assertRaises(RuntimeError):
                validate_summary({"scope": SCOPE, "categories": ["blue_mage_spells"]}, version=version)
        wire = modern_request(["blue_mage_spells"])
        approved = normalize_request(wire)
        self.assertEqual(approved.collection_lookup, wire["collection_lookup"])
        self.assertEqual(permitted_categories({"scope": SCOPE}), LEGACY_CATEGORIES)
        registry = build_registry(approved, {"collections": lambda _: None}, None, companion.CompanionFailure)
        self.assertEqual(set(registry), {"query_collections"})
        self.assertEqual(registry["query_collections"].definition["inputSchema"]["properties"]["category"]["enum"],
                         ["blue_mage_spells"])
        wire["collection_lookup"]["categories"].clear()
        self.assertEqual(approved.collection_lookup["categories"], ["blue_mage_spells"])
        with self.assertRaises(RuntimeError):
            recheck_request(wire, approved)
        self.assertIsNone(normalize_request(modern_request(collection_lookup=None)).collection_lookup)
        with self.assertRaises(RuntimeError):
            normalize_request(modern_request(version=29))

    def test_blue_mage_denial_is_fatal_even_with_bypassed_metadata_and_validator(self):
        for wire in (request(), expanded_request(), modern_request(["mounts"])):
            client, _, outputs = prepared([event(category="blue_mage_spells")])
            with patch.object(collection_lookup, "permitted_tool", return_value=deepcopy(TOOL)), \
                    patch.object(collection_lookup, "validate_arguments", side_effect=lambda args, *_: args), \
                    patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure) as failure:
                client.ask(wire, collection_query=lambda _: self.fail("Unpermitted Blue Mage callback"))
            self.assertEqual(failure.exception.kind, "permission")
            self.assertFalse(outputs[-1]["result"]["success"])

    def test_blue_mage_positive_only_evidence_and_existing_transport(self):
        meta, arguments = {"scope": SCOPE, "categories": ["blue_mage_spells"]}, {"category": "blue_mage_spells"}
        value = result("blue_mage_spells")
        validate_result(value, meta, arguments)
        self.assertIs(value["entries"][0]["unlocked"], True)
        self.assertIsNone(value["entries"][1]["unlocked"])
        for change in ({"unlocked": False}, {"state": "learned"}, {"action_id": 11385}):
            invalid = deepcopy(value)
            invalid["entries"][1].update(change)
            with self.subTest(change=change), self.assertRaises(RuntimeError):
                validate_result(invalid, meta, arguments)
        client, calls, outputs = prepared([event(category="blue_mage_spells"), *completed()])
        with patch.object(companion, "emit"):
            client.ask(modern_request(["blue_mage_spells"]), collection_query=lambda _: value)
        self.assertTrue(outputs[0]["result"]["success"])
        deferred_fixture.assert_exposed(self, client, calls, [permitted_tool(meta)])
        self.assertIn("displayed spellbook number", permitted_instructions(meta))
        self.assertIn("does not establish equipped actions", permitted_instructions(meta))

    def test_blue_mage_snapshot_identity_still_freezes_rows(self):
        first = result("blue_mage_spells")
        changed = deepcopy(first)
        changed["entries"][0]["name"] = "Changed synthetic spell"
        client, _, outputs = prepared([event(category="blue_mage_spells"), event(ident=51, category="blue_mage_spells")])
        values = iter((first, changed))
        with patch.object(companion, "emit"), self.assertRaises(RuntimeError):
            client.ask(modern_request(["blue_mage_spells"]), collection_query=lambda _: next(values))
        self.assertTrue(outputs[0]["result"]["success"])
        self.assertFalse(outputs[1]["result"]["success"])

    def test_v23_summary_only_and_exact_legacy_shapes(self):
        wire = request()
        approved = normalize_request(wire)
        self.assertEqual(approved.collection_lookup, {"scope": SCOPE})
        self.assertEqual(approved.context({})["collection_lookup"], {"scope": SCOPE})
        self.assertEqual(normalize_request(request(collection_lookup=None)).collection_lookup, None)
        for version in range(1, 23):
            legacy = wire_request(version)
            self.assertIsNone(normalize_request(legacy).collection_lookup)
            legacy["collection_lookup"] = None
            with self.subTest(version=version), self.assertRaises(RuntimeError):
                normalize_request(legacy)
        for summary in ({}, {"scope": "all_collections"}, {"scope": SCOPE, "entries": []},
                        {"scope": SCOPE, "catalog_count": 3}):
            with self.subTest(summary=summary), self.assertRaises(RuntimeError):
                validate_summary(summary)
        for mutation in (lambda value: value.pop("collection_lookup"),
                         lambda value: value.update(version=24),
                         lambda value: value.update(collection_lookup={"scope": "wrong"})):
            value = request()
            mutation(value)
            with self.assertRaises(RuntimeError):
                normalize_request(value)
        wire["collection_lookup"] = None
        with self.assertRaises(RuntimeError):
            recheck_request(wire, approved)

    def test_v24_explicit_categories_are_strict_and_do_not_upgrade_v23(self):
        wire = expanded_request(["facewear", "emotes"])
        approved = normalize_request(wire)
        self.assertEqual(approved.collection_lookup, {"scope": SCOPE, "categories": ["facewear", "emotes"]})
        self.assertEqual(approved.context({})["collection_lookup"], approved.collection_lookup)
        wire["collection_lookup"]["categories"].append("orchestrion")
        self.assertEqual(approved.collection_lookup["categories"], ["facewear", "emotes"])
        with self.assertRaises(RuntimeError):
            recheck_request(wire, approved)
        self.assertIsNone(normalize_request(request(version=24, collection_lookup=None)).collection_lookup)
        self.assertEqual(permitted_categories({"scope": SCOPE}), LEGACY_CATEGORIES)
        for categories in ([], ["mounts", "mounts"], ["achievements"], ["facewear", None],
                           "facewear", None, [True], [["facewear"]], list(SOURCES) + ["emotes"]):
            with self.subTest(categories=categories), self.assertRaises(RuntimeError):
                normalize_request(expanded_request(categories) if categories is not None else
                                  request(version=24, collection_lookup={"scope": SCOPE, "categories": None}))
        for value in ({"scope": SCOPE}, {"scope": SCOPE, "categories": ["facewear"], "entries": []},
                      {"scope": "all_collections", "categories": ["facewear"]}):
            with self.subTest(summary=value), self.assertRaises(RuntimeError):
                normalize_request(request(version=24, collection_lookup=value))
        for value in ({"scope": SCOPE, "categories": list(LEGACY_CATEGORIES)},
                      {"scope": SCOPE, "categories": ["facewear"]}):
            with self.subTest(summary=value), self.assertRaises(RuntimeError):
                normalize_request(request(collection_lookup=value))
        with self.assertRaises(RuntimeError):
            normalize_request(expanded_request(version=25))
        for category in SOURCES:
            summary = {"scope": SCOPE, "categories": [category]}
            validate_result(result(category), summary, {"category": category})
            validate_result(unavailable(category), summary, {"category": category})
            for disallowed in SOURCES.keys() - {category}:
                with self.subTest(category=category, disallowed=disallowed), self.assertRaises(RuntimeError):
                    validate_arguments({"category": disallowed}, summary)
                with self.assertRaises(RuntimeError):
                    validate_result(result(disallowed), summary, {"category": disallowed})
        for category in SOURCES.keys() - set(LEGACY_CATEGORIES):
            with self.subTest(category=category), self.assertRaises(RuntimeError):
                validate_result(result(category), {"scope": SCOPE}, {"category": category})

    def test_permitted_schema_is_a_copy_and_validators_enforce_category_access(self):
        wire = expanded_request(["fashion_accessories", "facewear"])
        approved = normalize_request(wire)
        registry = build_registry(approved, {"collections": lambda _: self.fail("No callback needed")},
                                  None, companion.CompanionFailure)
        tool = registry["query_collections"]
        self.assertEqual(tool.definition["inputSchema"]["properties"]["category"]["enum"],
                         ["fashion_accessories", "facewear"])
        self.assertEqual(TOOL["inputSchema"]["properties"]["category"]["enum"], list(SOURCES))
        tool.definition["inputSchema"]["properties"]["category"]["enum"].append("emotes")
        self.assertEqual(approved.collection_lookup["categories"], ["fashion_accessories", "facewear"])
        self.assertEqual(TOOL["inputSchema"]["properties"]["category"]["enum"], list(SOURCES))
        with self.assertRaises(RuntimeError):
            tool.validate_arguments({"category": "emotes"})
        self.assertIn("fashion_accessories, facewear", tool.instructions)
        for wire, forbidden in ((request(), "facewear"), (expanded_request(["facewear"]), "emotes"),
                                (expanded_request(["orchestrion"]), "mounts")):
            client, _, outputs = prepared([event(category=forbidden)])
            # Schema metadata is not an authority source: deliberately advertise all categories.
            with self.subTest(forbidden=forbidden), patch.object(companion, "emit"), \
                    patch.object(collection_lookup, "permitted_tool", return_value=deepcopy(TOOL)), \
                    self.assertRaises(companion.CompanionFailure) as failure:
                client.ask(wire, collection_query=lambda _: self.fail("Disallowed category reached native callback"))
            self.assertEqual(failure.exception.kind, "permission")
            self.assertFalse(outputs[-1]["result"]["success"])

    def test_all_expanded_categories_use_deferred_dispatch_without_initial_state(self):
        events = [event(ident=50 + index, category=category) for index, category in enumerate(V24_CATEGORIES)]
        client, calls, outputs = prepared([*events, *completed()])
        reads = []
        with patch.object(companion, "emit"):
            client.ask(expanded_request(), collection_query=lambda args: reads.append(args["category"]) or result(args["category"]))
        self.assertEqual(reads, list(V24_CATEGORIES))
        self.assertTrue(all(output["result"]["success"] for output in outputs))
        deferred_fixture.assert_exposed(self, client, calls, [permitted_tool(expanded_request()["collection_lookup"])])
        turn = next(params for method, params in calls if method == "turn/start")
        self.assertNotIn('"entries"', json.dumps(turn))
        self.assertNotIn('"observed_unlocked_count"', json.dumps(turn))
        self.assertNotIn("Synthetic Cloud", json.dumps(turn))
        self.assertIn("Never treat null as false", deferred_fixture.instructions(client, calls))
        self.assertIn("colour variants are not counted as separate unlocks", permitted_instructions(expanded_request()["collection_lookup"]))

    def test_strict_category_search_state_and_page_bounds(self):
        self.assertEqual(validate_arguments({"category": "mounts"}), {
            "category": "mounts", "query": "", "ids": [], "state": "all", "offset": 0, "limit": 50})
        for arguments in ({}, {"category": "achievements"}, {"category": ["mounts"]}, {"category": None},
                          {"query": "x" * 101}, {"query": "🐣" * 51}, {"query": "\ud800"},
                          {"query": "bad\n"}, {"query": "\u0085"}, {"query": None},
                          {"ids": [True]}, {"ids": [0]}, {"ids": [4294967296]}, {"ids": [1, 1]},
                          {"ids": list(range(1, 22))}, {"ids": "1"}, {"state": "locked"},
                          {"state": "owned"}, {"state": False}, {"offset": True}, {"offset": -1},
                          {"offset": 4097}, {"limit": 0}, {"limit": 51}, {"limit": 1.0},
                          {"url": "https://example.invalid"}, []):
            invalid = {"category": "mounts", **arguments} if isinstance(arguments, dict) and arguments else arguments
            with self.subTest(arguments=invalid), self.assertRaises(RuntimeError):
                validate_arguments(invalid)
        for category in SOURCES:
            validate_arguments({"category": category, "ids": [4294967295], "offset": 4096, "limit": 1})
        validate_arguments({"category": "minions", "query": "🐣" * 50})

    def test_positive_negative_and_unsupported_observations_remain_distinct(self):
        meta, args = {"scope": SCOPE}, {"category": "mounts"}
        value = result()
        self.assertIs(validate_result(value, meta, args)["entries"][0]["unlocked"], True)
        self.assertIsNone(value["entries"][1]["unlocked"])
        self.assertIsNone(value["entries"][2]["unlocked"])
        for index, changes in ((0, {"unlocked": 1}), (0, {"unlocked": False}),
                               (0, {"unknown_reason": "Unexpected"}), (1, {"unlocked": False}),
                               (1, {"unknown_reason": None}), (2, {"state": "locked"}),
                               (2, {"state": "owned"}), (2, {"unlocked": True}),
                               (2, {"name": "Bad\nname"}), (2, {"id": True}),
                               (2, {"item_id": 17}), (2, {"id": 1})):
            bad = deepcopy(value)
            bad["entries"][index].update(changes)
            with self.subTest(index=index, changes=changes), self.assertRaises(RuntimeError):
                validate_result(bad, meta, args)
        self.assertIn("Never treat null as false", INSTRUCTIONS)
        self.assertIn("never proves a whistle", INSTRUCTIONS)
        self.assertIn("Never send private collection state", INSTRUCTIONS)

    def test_strict_counts_provenance_filters_pages_and_unavailable(self):
        meta, args = {"scope": SCOPE}, {"category": "mounts"}
        first = result()
        validate_result(unavailable(), meta, args)
        validate_result(result(entries=[], matched_count=0, **dict.fromkeys(COUNT_FIELDS, 0)), meta, args)
        validate_result(result(entries=first["entries"][:1], limit=1, next_offset=1, truncated=True),
                        meta, {**args, "limit": 1})
        validate_result(result(entries=first["entries"][1:], offset=1), meta, {**args, "offset": 1})
        validate_result(result(entries=[], offset=4096), meta, {**args, "offset": 4096})
        validate_result(result(entries=first["entries"][:1], next_offset=1, truncated=True), meta, args)
        for changes, arguments in (({"category": "minions"}, args), ({"source": "arbitrary"}, args),
                                   ({"readiness": "complete"}, args), ({"catalog_count": 4}, args),
                                   ({"unknown_count": True}, args), ({"unavailable_reason": "unexpected"}, args),
                                   ({"matched_count": 2}, args), ({"truncated": True}, args),
                                   ({"next_offset": True}, args), ({"offset": 1}, args),
                                   ({"available": False}, args), ({"coverage": ""}, args),
                                   ({"captured_at_utc": (datetime.now(timezone.utc) + timedelta(days=1)).isoformat()}, args),
                                   ({}, {**args, "query": "cloud"}), ({}, {**args, "ids": [1]}),
                                   ({}, {**args, "state": "unlocked"})):
            with self.subTest(changes=changes, arguments=arguments), self.assertRaises(RuntimeError):
                validate_result(result(**changes), meta, arguments)
        for key in COUNT_FIELDS:
            bad = unavailable()
            bad[key] = 0
            with self.subTest(key=key), self.assertRaises(RuntimeError):
                validate_result(bad, meta, args)
        for state, entry in zip(("unlocked", "not_observed_unlocked", "unknown"), first["entries"]):
            validate_result(result(entries=[entry], matched_count=1), meta, {**args, "state": state})
        validate_result(result(entries=first["entries"][:1], matched_count=1), meta,
                        {**args, "query": "SYNTHETIC cloud", "ids": [1]})

    def test_on_demand_deferred_dispatch_has_permission_only_initial_context(self):
        client, calls, outputs = prepared([event(), *completed()])
        reads = []
        def query(arguments):
            self.assertIn("turn/start", [method for method, _ in calls])
            reads.append(arguments)
            return result()
        with patch.object(companion, "emit") as emit:
            client.ask(request(), collection_query=query)
        self.assertEqual(reads, [validate_arguments({"category": "mounts"})])
        deferred_fixture.assert_exposed(self, client, calls, [permitted_tool({"scope": SCOPE})])
        self.assertIn(INSTRUCTIONS, deferred_fixture.instructions(client, calls))
        turn = next(params for method, params in calls if method == "turn/start")
        self.assertNotIn('"entries"', json.dumps(turn))
        self.assertNotIn('"observed_unlocked_count"', json.dumps(turn))
        self.assertNotIn("Synthetic Cloud", json.dumps(turn))
        self.assertTrue(outputs[0]["result"]["success"])
        self.assertIn("local collection lookups", emit.call_args.args[0]["question_context"])
        self.assertTrue(any(call.args[0].get("performance", {}).get("stage") == "tool.collections"
                            for call in emit.call_args_list))

    def test_absent_permission_and_unused_reader_never_capture(self):
        client, calls, _ = prepared(completed())
        with patch.object(companion, "emit"):
            client.ask(request(collection_lookup=None), collection_query=lambda _: self.fail("No permission"))
        self.assertEqual(next(params for method, params in calls if method == "thread/start")["dynamicTools"], [])
        client, _, _ = prepared(completed())
        with patch.object(companion, "emit"):
            client.ask(request(), collection_query=lambda _: self.fail("No requested lookup"))
        client, _, outputs = prepared([event()])
        with patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure) as failure:
            client.ask(request(collection_lookup=None), collection_query=lambda _: self.fail("Disabled"))
        self.assertEqual(failure.exception.kind, "permission")
        self.assertFalse(outputs[0]["result"]["success"])
        client, calls, _ = prepared(completed())
        with self.assertRaises(RuntimeError):
            client.ask(request())
        self.assertFalse(calls)

    def test_direct_and_undiscovered_capability_calls_are_rejected(self):
        for tool, arguments in (("query_collections", {"category": "mounts"}),
                                ("invoke_tool", {"name": "query_collections", "arguments": {"category": "mounts"}})):
            client, _, _ = prepared([tool_message(ident=50, tool=tool, arguments=arguments)], raw=True)
            with self.subTest(tool=tool), patch.object(companion, "emit"), self.assertRaises(RuntimeError):
                client.ask(request(), collection_query=lambda _: self.fail("Undiscovered tool"))

    def test_per_category_capture_identity_is_frozen_and_rows_cannot_change(self):
        first = result()
        for changes in ({"captured_at_utc": (datetime.fromisoformat(first["captured_at_utc"]) - timedelta(seconds=1)).isoformat()},
                        {"coverage": "Different coverage"},
                        {"observed_unlocked_count": 2, "not_observed_unlocked_count": 0,
                         "entries": [first["entries"][0], {**first["entries"][1], "state": "unlocked",
                                                          "unlocked": True, "unknown_reason": None}, first["entries"][2]]},
                        {"entries": [first["entries"][0], {**first["entries"][1], "name": "Changed name"}, first["entries"][2]]}):
            client, _, outputs = prepared([event(), event(ident=51)])
            values = iter((first, {**first, **changes}))
            with self.subTest(changes=changes), patch.object(companion, "emit"), self.assertRaisesRegex(RuntimeError, "Collections lookup failed"):
                client.ask(request(), collection_query=lambda _: next(values))
            self.assertTrue(outputs[0]["result"]["success"])
            self.assertFalse(outputs[1]["result"]["success"])
        client, _, outputs = prepared([event(), event(ident=51, category="minions"), *completed()])
        with patch.object(companion, "emit"):
            client.ask(request(), collection_query=lambda args: result(args["category"]))
        self.assertTrue(all(output["result"]["success"] for output in outputs))

    def test_access_revocation_and_cancellation_abort(self):
        client, _, outputs = prepared([event()])
        def denied(_):
            raise PermissionError("Synthetic revoked")
        with patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure) as failure:
            client.ask(request(), collection_query=denied)
        self.assertEqual(failure.exception.kind, "permission")
        self.assertFalse(outputs[0]["result"]["success"])
        client, _, outputs = prepared([event()])
        def cancelled(_):
            client.cancel_event.set()
            return result()
        with patch.object(companion, "emit"), self.assertRaises(RuntimeError):
            client.ask(request(), collection_query=cancelled)
        self.assertFalse(outputs[0]["result"]["success"])

    def test_shared_call_and_byte_budgets_and_bounded_argument_correction(self):
        value = result()
        client, _, outputs = prepared([event(ident=50 + index) for index in range(13)])
        reads = []
        with patch.object(companion, "emit"), self.assertRaisesRegex(RuntimeError, "12-call budget"):
            client.ask(request(), collection_query=lambda args: reads.append(args) or value)
        self.assertEqual(len(reads), 12)
        self.assertFalse(outputs[-1]["result"]["success"])
        client, _, outputs = prepared([event(ids=[0]), event(ident=51), *completed()])
        reads = []
        with patch.object(companion, "emit"):
            client.ask(request(), collection_query=lambda args: reads.append(args) or value)
        self.assertEqual(len(reads), 1)
        self.assertFalse(outputs[0]["result"]["success"])
        self.assertTrue(outputs[1]["result"]["success"])
        client, _, outputs = prepared([event(), event(ident=51)])
        with patch.object(companion, "emit"), patch.object(companion, "MAX_LOOKUP_TOTAL_BYTES", encoded_size(value)), \
                self.assertRaisesRegex(RuntimeError, "byte result budget"):
            client.ask(request(), collection_query=lambda _: value)
        self.assertTrue(outputs[0]["result"]["success"])
        self.assertFalse(outputs[1]["result"]["success"])

    def test_pipe_correlates_kind_id_single_reply_and_normalizes_arguments(self):
        messages, responses = [], []
        ready = threading.Event()
        def emit(value):
            messages.append(value)
            ready.set()
        bridge = QuestQueryBridge(threading.Event(), emit, timeout=2)
        worker = threading.Thread(target=lambda: responses.append(bridge.query({"category": "minions"}, "collection")))
        worker.start()
        self.assertTrue(ready.wait(2))
        ident = messages[0]["collection_query"]["id"]
        for invalid in ({"duty_result": {"id": ident, "result": {}}},
                        {"collection_result": {"id": ident + 1, "result": {}}},
                        {"collection_result": {"id": True, "result": {}}}):
            with self.assertRaises(RuntimeError):
                bridge.accept(invalid)
        bridge.accept({"collection_result": {"id": ident, "result": {"synthetic": True}}})
        worker.join(2)
        self.assertFalse(worker.is_alive())
        self.assertEqual(messages[0]["collection_query"]["arguments"], validate_arguments({"category": "minions"}))
        self.assertEqual(responses, [{"synthetic": True}])
        with self.assertRaises(RuntimeError):
            bridge.accept({"collection_result": {"id": ident, "result": {}}})

    def test_cli_wires_v23_collection_callback_and_result_input_without_runtime(self):
        payload = request()
        reply = {"collection_result": {"id": 1, "result": {"synthetic": True}}}
        stream = io.StringIO(json.dumps({"request": payload}) + "\n" + json.dumps(reply) + "\n")
        stream.reconfigure = lambda **_: None
        with patch.object(companion.sys, "argv", ["companion.py", "bridge", "ask"]), \
                patch.object(companion.sys, "stdin", stream), \
                patch.object(companion.threading, "Thread", side_effect=lambda target, **_: SimpleNamespace(start=target)), \
                patch.object(companion, "Companion") as constructor, patch.object(companion, "emit"), \
                patch.object(companion.QuestQueryBridge, "accept") as accept, \
                patch.object(companion.QuestQueryBridge, "query", return_value="fixture") as query:
            self.assertEqual(companion.main(), 0)
            callback = constructor.return_value.ask.call_args.kwargs["collection_query"]
            self.assertEqual(callback({"category": "mounts"}), "fixture")
        accept.assert_called_once_with(reply)
        query.assert_called_once_with({"category": "mounts"}, "collection")
        self.assertEqual(constructor.return_value.ask.call_args.args, (payload,))

    def test_csharp_producer_results_survive_real_deferred_transport(self):
        path = Path(__file__).resolve().parents[2] / ".test-tmp" / "collection-producer.json"
        self.assertTrue(path.exists(), "Produce required fixtures with tests/Collections.Tests --produce-fixtures .test-tmp/collection-producer.json")
        fixtures = json.loads(path.read_text(encoding="utf-8"))
        self.assertGreaterEqual(len(fixtures), 10)
        categories, states = set(), set()
        unavailable_seen = page_seen = False
        for fixture in fixtures:
            with self.subTest(name=fixture["name"]):
                value, arguments = fixture["result"], fixture["arguments"]
                self.assertLessEqual(encoded_size(value), MAX_RESULT_BYTES)
                summary = {"scope": SCOPE, "categories": [arguments["category"]]}
                validate_result(value, summary, arguments)
                categories.add(value["category"])
                states.update(entry["state"] for entry in value["entries"])
                unavailable_seen |= not value["available"]
                page_seen |= value["truncated"]
                client, _, outputs = prepared([event(**arguments), *completed()])
                with patch.object(companion, "emit"):
                    payload = expanded_request([arguments["category"]])
                    if arguments["category"] == "triple_triad":
                        payload = wire_request(32)
                        payload["collection_lookup"] = summary
                    client.ask(payload, collection_query=lambda _, data=value: deepcopy(data))
                transported = json.loads(outputs[0]["result"]["contentItems"][0]["text"])
                self.assertEqual(transported, value)
        self.assertEqual(categories, {*V24_CATEGORIES, "triple_triad"})
        self.assertEqual(states, {"unlocked", "not_observed_unlocked", "unknown"})
        self.assertTrue(unavailable_seen)
        self.assertTrue(page_seen)

    def test_blue_mage_csharp_producer_survives_deferred_transport(self):
        path = Path(__file__).resolve().parents[2] / ".test-tmp" / "blue-mage-producer.json"
        self.assertTrue(path.exists(), "Produce required .test-tmp/blue-mage-producer.json with BlueMage.Tests")
        fixtures = json.loads(path.read_text(encoding="utf-8"))
        self.assertGreaterEqual(len(fixtures), 4)
        states, unavailable_seen, page_seen = set(), False, False
        meta = {"scope": SCOPE, "categories": ["blue_mage_spells"]}
        for fixture in fixtures:
            with self.subTest(name=fixture["name"]):
                value, arguments = fixture["result"], fixture["arguments"]
                self.assertEqual(arguments["category"], "blue_mage_spells")
                validate_result(value, meta, arguments)
                states.update(entry["state"] for entry in value["entries"])
                unavailable_seen |= not value["available"]
                page_seen |= value["truncated"]
                client, _, outputs = prepared([event(**arguments), *completed()])
                with patch.object(companion, "emit"):
                    client.ask(modern_request(["blue_mage_spells"]), collection_query=lambda _, data=value: deepcopy(data))
                self.assertEqual(json.loads(outputs[0]["result"]["contentItems"][0]["text"]), value)
        self.assertEqual(states, {"unlocked", "not_observed_unlocked", "unknown"})
        self.assertTrue(unavailable_seen)
        self.assertTrue(page_seen)


if __name__ == "__main__":
    unittest.main()
