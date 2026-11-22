"""Offline crafting permissions, query-first protocol, frozen rows and deferred transport."""
from copy import deepcopy
from datetime import datetime, timedelta, timezone
import io
import json
from pathlib import Path
import threading
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import companion
import crafting_log_lookup
import deferred_fixture
from crafting_log_lookup import (SCOPE, SOURCE, TOOL, INSTRUCTIONS, MAX_RESULT_BYTES,
                                 IDENTITY_FIELDS, validate_arguments, validate_result, validate_summary)
from collection_lookup_check import prepared
from companion_architecture_check import wire_request
from companion_request import normalize_request, recheck_request
from companion_tools import build_registry
from quest_lookup import QuestQueryBridge, encoded_size
from quest_lookup_check import completed, tool_message


def request(**changes):
    value = wire_request(28)
    value["crafting_log_lookup"] = {"scope": SCOPE}
    value.update(changes)
    return value


def row(ident, captured, state="completed", **changes):
    return {"id": ident, "name": f"Synthetic Recipe {ident}", "captured_at_utc": captured,
            "state": state, "completed": True if state == "completed" else None,
            "unknown_reason": None if state == "completed" else "Synthetic unverified evidence.", **changes}


def result(**changes):
    queried = datetime.now(timezone.utc)
    captured = (queried - timedelta(seconds=1)).isoformat()
    return {"scope": SCOPE, "queried_at_utc": queried.isoformat(), "available": True,
            "unavailable_reason": None, "source": SOURCE, "readiness": "positive_observations_only",
            "coverage": "Synthetic public notebook definitions; observations cover requested rows only.",
            "catalog_count": 13892, "matched_count": 3,
            "entries": [row(1, captured), row(2, captured, "not_observed_complete"), row(30241, captured, "unknown")],
            "offset": 0, "limit": 50, "next_offset": None, "truncated": False, **changes}


def unavailable(**changes):
    return result(available=False, unavailable_reason="Synthetic unavailable",
                  readiness="unavailable", catalog_count=None, matched_count=0, entries=[], **changes)


def event(ident=50, **arguments):
    return tool_message(ident=ident, tool="query_crafting_log", arguments=arguments or {"query": "Synthetic"})


class CraftingLogChecks(unittest.TestCase):
    def test_wire28_is_nullable_exact_and_older_versions_cannot_opt_in(self):
        value = request()
        approved = normalize_request(value)
        self.assertEqual(approved.crafting_log_lookup, {"scope": SCOPE})
        self.assertEqual(approved.context({})["crafting_log_lookup"], {"scope": SCOPE})
        self.assertIsNone(approved.progression_lookup)
        self.assertIsNone(approved.game_definitions_lookup)
        self.assertIsNone(normalize_request(request(crafting_log_lookup=None)).crafting_log_lookup)
        value["crafting_log_lookup"]["scope"] = "all_recipes"
        self.assertEqual(approved.crafting_log_lookup, {"scope": SCOPE})
        with self.assertRaises(RuntimeError):
            recheck_request(value, approved)
        for version in range(1, 28):
            wire = wire_request(version)
            self.assertIsNone(normalize_request(wire).crafting_log_lookup)
            for summary in (None, {"scope": SCOPE}):
                with self.subTest(version=version, summary=summary), self.assertRaises(RuntimeError):
                    normalize_request({**wire, "crafting_log_lookup": summary})
        value = request()
        value.pop("crafting_log_lookup")
        with self.assertRaises(RuntimeError):
            normalize_request(value)
        with self.assertRaises(RuntimeError):
            normalize_request(request(version=29))

    def test_summary_grants_only_one_independent_reader(self):
        for summary in ({}, {"scope": "observed_progression"}, {"scope": SCOPE, "entries": []},
                        {"scope": SCOPE, "categories": ["crafting_log"]}, True):
            with self.subTest(summary=summary), self.assertRaises(RuntimeError):
                validate_summary(summary)
        registry = build_registry(normalize_request(request()), {"crafting_log": lambda _: None},
                                  None, companion.CompanionFailure)
        self.assertEqual(set(registry), {"query_crafting_log"})
        self.assertEqual(registry["query_crafting_log"].definition, TOOL)
        for summary in (None, {}, {"scope": "local_game_definitions_v2"}):
            with self.assertRaises(companion.CompanionFailure) as failure:
                companion.require_lookup_permission("crafting_log", {}, summary)
            self.assertEqual(failure.exception.kind, "permission")

    def test_arguments_require_public_filters_and_never_accept_state_scans(self):
        self.assertEqual(validate_arguments({"ids": [30241]}),
                         {"query": "", "ids": [30241], "offset": 0, "limit": 50})
        self.assertEqual(validate_arguments({"query": "  Synthetic  ", "ids": [30241], "limit": 1})["query"],
                         "Synthetic")
        validate_arguments({"query": "🐟", "ids": [4294967295], "offset": 65535, "limit": 50})
        for arguments in ({}, {"query": ""}, {"query": "   "}, {"ids": []},
                          {"query": "x"}, {"query": "x", "ids": [1]}, {"query": "x" * 101},
                          {"query": "🐟" * 51}, {"query": "\ud800"}, {"query": "bad\nname"},
                          {"query": "\u0085x"}, {"query": None}, {"ids": [True]}, {"ids": [0]},
                          {"ids": [4294967296]}, {"ids": [1, 1]}, {"ids": list(range(1, 22))},
                          {"ids": "1"}, {"query": "valid", "offset": True},
                          {"query": "valid", "offset": 65536}, {"query": "valid", "limit": 51},
                          {"query": "valid", "limit": 0}, {"query": "valid", "limit": 1.0},
                          {"query": "valid", "state": "completed"}, {"category": "crafting_log"}):
            with self.subTest(arguments=arguments), self.assertRaises(RuntimeError):
                validate_arguments(arguments)
        self.assertNotIn("state", TOOL["inputSchema"]["properties"])

    def test_only_positive_observations_are_true_and_counts_are_public_definitions(self):
        meta, args, value = {"scope": SCOPE}, {"query": "Synthetic"}, result()
        validate_result(value, meta, args)
        self.assertEqual(value["catalog_count"], 13892)
        self.assertIs(value["entries"][0]["completed"], True)
        self.assertTrue(all(entry["completed"] is None for entry in value["entries"][1:]))
        for fields in ({"completed": False}, {"completed": 0}, {"completed": True},
                       {"state": "not_completed"}, {"state": "unknown", "unknown_reason": ""},
                       {"unknown_reason": None}, {"item_id": 21}):
            bad = deepcopy(value)
            bad["entries"][1].update(fields)
            with self.subTest(fields=fields), self.assertRaises(RuntimeError):
                validate_result(bad, meta, args)
        validate_result(unavailable(), meta, args)
        self.assertIn("PUBLIC DEFINITIONS only", INSTRUCTIONS)
        self.assertIn("BEFORE any character", INSTRUCTIONS)
        self.assertIn("no state filter or whole-log scan", INSTRUCTIONS)
        self.assertIn("Never send private crafting state", INSTRUCTIONS)

    def test_definition_filter_pages_misses_and_high_recipe_ids_are_valid(self):
        meta, base = {"scope": SCOPE}, result()
        validate_result(result(entries=base["entries"][:1], limit=1, next_offset=1, truncated=True),
                        meta, {"query": "Synthetic", "limit": 1})
        validate_result(result(entries=base["entries"][1:], offset=1),
                        meta, {"query": "Synthetic", "offset": 1})
        validate_result(result(entries=[], offset=65535), meta, {"query": "Synthetic", "offset": 65535})
        validate_result(result(entries=[], matched_count=0), meta, {"ids": [4294967295]})
        high = base["entries"][2]
        validate_result(result(entries=[high], matched_count=1), meta, {"ids": [30241], "query": "Synthetic"})
        self.assertGreater(high["id"], 6407)
        # Recipe names follow invariant character folding, not expanding Python casefold.
        special = {**base["entries"][0], "name": "Straße"}
        validate_result(result(entries=[special], matched_count=1), meta, {"query": "straße"})
        with self.assertRaises(RuntimeError):
            validate_result(result(entries=[special], matched_count=1), meta, {"query": "strasse"})
        for fields, args in (({"next_offset": True}, {"query": "Synthetic"}),
                             ({"truncated": True}, {"query": "Synthetic"}),
                             ({"catalog_count": 2}, {"query": "Synthetic"}),
                             ({"catalog_count": 65537}, {"query": "Synthetic"}),
                             ({"matched_count": 0}, {"query": "Synthetic"}),
                             ({}, {"ids": [1]}), ({}, {"query": "absent"}),
                             ({"entries": list(reversed(base["entries"]))}, {"query": "Synthetic"}),
                             ({"entries": [base["entries"][0]] * 3}, {"query": "Synthetic"})):
            with self.subTest(fields=fields, args=args), self.assertRaises(RuntimeError):
                validate_result(result(**fields), meta, args)

    def test_exact_provenance_time_text_and_size_bounds(self):
        meta, args = {"scope": SCOPE}, {"query": "Synthetic"}
        for fields in ({"source": "arbitrary"}, {"scope": "observed_progression"},
                       {"readiness": "loaded_bitmap"}, {"available": 1}, {"unavailable_reason": "Unexpected"},
                       {"catalog_count": True}, {"matched_count": True}, {"coverage": ""},
                       {"coverage": "x" * 1001}, {"queried_at_utc": "bad"}, {"extra": 0},
                       {"queried_at_utc": (datetime.now(timezone.utc) + timedelta(days=1)).isoformat()}):
            with self.subTest(fields=fields), self.assertRaises(RuntimeError):
                validate_result(result(**fields), meta, args)
        for fields in ({"name": "bad\nname"}, {"name": "🐟" * 65}, {"name": "\ud800"},
                       {"captured_at_utc": (datetime.now(timezone.utc) + timedelta(days=1)).isoformat()},
                       {"unknown_reason": "x" * 257}, {"unknown_reason": "\u0085"}):
            bad = result()
            bad["entries"][2].update(fields)
            with self.subTest(fields=fields), self.assertRaises(RuntimeError):
                validate_result(bad, meta, args)
        for fields in ({"catalog_count": 0}, {"matched_count": 1}, {"readiness": "positive_observations_only"},
                       {"unavailable_reason": None}):
            with self.subTest(fields=fields), self.assertRaises(RuntimeError):
                validate_result({**unavailable(), **fields}, meta, args)
        value = result()
        value.update(entries=[row(ident, value["queried_at_utc"], "unknown", name="🐟" * 64,
                                  unknown_reason="x" * 256) for ident in range(1, 51)], matched_count=50)
        self.assertGreater(encoded_size(value), MAX_RESULT_BYTES)
        with self.assertRaises(RuntimeError):
            validate_result(value, meta, {"query": "🐟"})

    def test_deferred_execution_is_on_demand_and_context_contains_no_rows(self):
        client, calls, outputs = prepared([event(), *completed()])
        reads = []
        def query(arguments):
            self.assertIn("turn/start", [method for method, _ in calls])
            reads.append(arguments)
            return result()
        with patch.object(companion, "emit") as emit:
            client.ask(request(), crafting_log_query=query)
        self.assertEqual(reads, [validate_arguments({"query": "Synthetic"})])
        deferred_fixture.assert_exposed(self, client, calls, [TOOL])
        self.assertIn(INSTRUCTIONS, deferred_fixture.instructions(client, calls))
        turn = next(parameters for method, parameters in calls if method == "turn/start")
        self.assertNotIn('"entries"', json.dumps(turn))
        self.assertNotIn("Synthetic Recipe", json.dumps(turn))
        self.assertTrue(outputs[0]["result"]["success"])
        self.assertIn("local crafting-log lookups", emit.call_args.args[0]["question_context"])
        self.assertTrue(any(call.args[0].get("performance", {}).get("stage") == "tool.crafting_log"
                            for call in emit.call_args_list))

    def test_disabled_unused_missing_and_revoked_readers_never_leak_rows(self):
        for wire in (request(crafting_log_lookup=None), wire_request(27)):
            client, calls, _ = prepared(completed())
            with patch.object(companion, "emit"):
                client.ask(wire, crafting_log_query=lambda _: self.fail("Not permitted"))
            self.assertEqual(next(params for method, params in calls if method == "thread/start")["dynamicTools"], [])
            client, _, outputs = prepared([event()])
            with patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure) as failure:
                client.ask(wire, crafting_log_query=lambda _: self.fail("Disabled"))
            self.assertEqual(failure.exception.kind, "permission")
            self.assertFalse(outputs[0]["result"]["success"])
        client, _, _ = prepared(completed())
        with patch.object(companion, "emit"):
            client.ask(request(), crafting_log_query=lambda _: self.fail("Unused"))
        client, calls, _ = prepared(completed())
        with self.assertRaises(RuntimeError):
            client.ask(request())
        self.assertFalse(calls)
        client, _, outputs = prepared([event()])
        with patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure) as failure:
            client.ask(request(), crafting_log_query=lambda _: (_ for _ in ()).throw(PermissionError("Revoked")))
        self.assertEqual(failure.exception.kind, "permission")
        self.assertFalse(outputs[0]["result"]["success"])

    def test_direct_and_undiscovered_calls_are_rejected(self):
        for tool, args in (("query_crafting_log", {"query": "Synthetic"}),
                           ("invoke_tool", {"name": "query_crafting_log", "arguments": {"query": "Synthetic"}})):
            client, _, _ = prepared([tool_message(ident=50, tool=tool, arguments=args)], raw=True)
            with patch.object(companion, "emit"), self.assertRaises(RuntimeError):
                client.ask(request(), crafting_log_query=lambda _: self.fail("Undiscovered"))

    def test_answer_freezes_catalog_and_every_row_but_allows_later_query_times(self):
        first = result()
        for fields in ({"coverage": "Changed"}, {"catalog_count": 13893}):
            client, _, outputs = prepared([event(), event(ident=51)])
            values = iter((first, {**first, **fields}))
            with patch.object(companion, "emit"), self.assertRaises(RuntimeError):
                client.ask(request(), crafting_log_query=lambda _: next(values))
            self.assertTrue(outputs[0]["result"]["success"])
            self.assertFalse(outputs[1]["result"]["success"])
        for fields in ({"name": "Synthetic Renamed Recipe"}, {"state": "unknown", "completed": None,
                                                            "unknown_reason": "Changed"},
                       {"captured_at_utc": (datetime.fromisoformat(first["queried_at_utc"]) - timedelta(seconds=5)).isoformat()}):
            changed = deepcopy(first)
            changed["entries"][0].update(fields)
            client, _, outputs = prepared([event(), event(ident=51)])
            values = iter((first, changed))
            with patch.object(companion, "emit"), self.assertRaises(RuntimeError):
                client.ask(request(), crafting_log_query=lambda _: next(values))
            self.assertFalse(outputs[1]["result"]["success"])
        changed_time = {**first, "queried_at_utc": datetime.now(timezone.utc).isoformat()}
        client, _, outputs = prepared([event(), event(ident=51), *completed()])
        values = iter((first, changed_time))
        with patch.object(companion, "emit"):
            client.ask(request(), crafting_log_query=lambda _: next(values))
        self.assertTrue(all(output["result"]["success"] for output in outputs))
        self.assertEqual(set(IDENTITY_FIELDS), {"available", "unavailable_reason", "source", "coverage",
                                              "readiness", "catalog_count"})

    def test_availability_stays_frozen_for_answer_and_cancelled_data_is_not_delivered(self):
        for pair in ((unavailable(), result()), (result(), unavailable())):
            client, _, outputs = prepared([event(), event(ident=51)])
            values = iter(pair)
            with patch.object(companion, "emit"), self.assertRaises(RuntimeError):
                client.ask(request(), crafting_log_query=lambda _: next(values))
            self.assertFalse(outputs[1]["result"]["success"])
        client, _, outputs = prepared([event()])
        def cancel(_):
            client.cancel_event.set()
            return result()
        with patch.object(companion, "emit"), self.assertRaises(RuntimeError):
            client.ask(request(), crafting_log_query=cancel)
        self.assertFalse(outputs[0]["result"]["success"])

    def test_shared_budgets_count_repeats_and_invalid_attempts(self):
        value, reads = result(), []
        client, _, outputs = prepared([event(ident=50 + index) for index in range(13)])
        with patch.object(companion, "emit"), self.assertRaisesRegex(RuntimeError, "12-call budget"):
            client.ask(request(), crafting_log_query=lambda args: reads.append(args) or value)
        self.assertEqual(len(reads), 12)
        self.assertFalse(outputs[-1]["result"]["success"])
        client, _, outputs = prepared([event(query=""), event(ident=51), *completed()])
        with patch.object(companion, "emit"):
            client.ask(request(), crafting_log_query=lambda _: value)
        self.assertFalse(outputs[0]["result"]["success"])
        self.assertTrue(outputs[1]["result"]["success"])
        client, _, outputs = prepared([event(), event(ident=51)])
        with patch.object(companion, "emit"), patch.object(companion, "MAX_LOOKUP_TOTAL_BYTES", encoded_size(value)), \
                self.assertRaisesRegex(RuntimeError, "byte result budget"):
            client.ask(request(), crafting_log_query=lambda _: value)
        self.assertFalse(outputs[1]["result"]["success"])

    def test_pipe_correlates_kind_id_and_normalizes_filters(self):
        messages, responses, ready = [], [], threading.Event()
        def emit(value):
            messages.append(value)
            ready.set()
        bridge = QuestQueryBridge(threading.Event(), emit, timeout=2)
        worker = threading.Thread(target=lambda: responses.append(bridge.query({"ids": [30241]}, "crafting_log")))
        worker.start()
        self.assertTrue(ready.wait(2))
        ident = messages[0]["crafting_log_query"]["id"]
        for invalid in ({"progression_result": {"id": ident, "result": {}}},
                        {"crafting_log_result": {"id": ident + 1, "result": {}}},
                        {"crafting_log_result": {"id": True, "result": {}}}):
            with self.assertRaises(RuntimeError):
                bridge.accept(invalid)
        bridge.accept({"crafting_log_result": {"id": ident, "result": {"synthetic": True}}})
        worker.join(2)
        self.assertFalse(worker.is_alive())
        self.assertEqual(messages[0]["crafting_log_query"]["arguments"], validate_arguments({"ids": [30241]}))
        self.assertEqual(responses, [{"synthetic": True}])
        with self.assertRaises(RuntimeError):
            bridge.accept({"crafting_log_result": {"id": ident, "result": {}}})

    def test_cli_wires_v28_callback_and_result_without_runtime(self):
        payload = request()
        reply = {"crafting_log_result": {"id": 1, "result": {"synthetic": True}}}
        stream = io.StringIO(json.dumps({"request": payload}) + "\n" + json.dumps(reply) + "\n")
        stream.reconfigure = lambda **_: None
        with patch.object(companion.sys, "argv", ["companion.py", "bridge", "ask"]), \
                patch.object(companion.sys, "stdin", stream), \
                patch.object(companion.threading, "Thread", side_effect=lambda target, **_: SimpleNamespace(start=target)), \
                patch.object(companion, "Companion") as constructor, patch.object(companion, "emit"), \
                patch.object(companion.QuestQueryBridge, "accept") as accept, \
                patch.object(companion.QuestQueryBridge, "query", return_value="fixture") as query:
            self.assertEqual(companion.main(), 0)
            callback = constructor.return_value.ask.call_args.kwargs["crafting_log_query"]
            self.assertEqual(callback({"ids": [30241]}), "fixture")
        accept.assert_called_once_with(reply)
        query.assert_called_once_with({"ids": [30241]}, "crafting_log")
        self.assertEqual(constructor.return_value.ask.call_args.args, (payload,))

    def test_csharp_producer_survives_deferred_transport(self):
        path = Path(__file__).resolve().parents[2] / ".test-tmp" / "crafting-log-producer.json"
        self.assertTrue(path.exists(), "Produce required .test-tmp/crafting-log-producer.json with CraftingLog.Tests")
        fixtures = json.loads(path.read_text(encoding="utf-8"))
        self.assertGreaterEqual(len(fixtures), 6)
        states, unavailable_seen, page_seen, high_id_seen = set(), False, False, False
        for fixture in fixtures:
            with self.subTest(name=fixture["name"]):
                value, arguments = fixture["result"], fixture["arguments"]
                validate_result(value, {"scope": SCOPE}, arguments)
                states.update(entry["state"] for entry in value["entries"])
                unavailable_seen |= not value["available"]
                page_seen |= value["truncated"]
                high_id_seen |= any(entry["id"] > 6407 for entry in value["entries"])
                client, _, outputs = prepared([event(**arguments), *completed()])
                with patch.object(companion, "emit"):
                    client.ask(request(), crafting_log_query=lambda _, data=value: deepcopy(data))
                self.assertEqual(json.loads(outputs[0]["result"]["contentItems"][0]["text"]), value)
        self.assertEqual(states, {"completed", "not_observed_complete", "unknown"})
        self.assertTrue(unavailable_seen and page_seen and high_id_seen)


if __name__ == "__main__":
    unittest.main()
