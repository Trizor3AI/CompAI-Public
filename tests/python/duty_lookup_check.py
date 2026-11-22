"""Offline duty permission, producer, validation and provider-transport checks."""
import deferred_fixture
from copy import deepcopy
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import threading
import unittest
from unittest.mock import patch

import companion
from companion_architecture_check import wire_request
from companion_request import normalize_request, recheck_request
from duty_lookup import SCOPE, TOOL, INSTRUCTIONS, validate_arguments, validate_result, validate_summary
import inventory_question_check as question_checks
from quest_lookup import QuestQueryBridge
from quest_lookup_check import completed, tool_message


def request(**changes):
    value = wire_request(16)
    value["duty_lookup"] = {"scope": SCOPE}
    value.update(changes)
    return value


def result(**changes):
    return {"scope": SCOPE, "captured_at_utc": datetime.now(timezone.utc).isoformat(),
            "available": True, "unavailable_reason": None, "coverage": "Supported local instances; negatives remain unknown.",
            "entries": [{"duty_id": 10, "instance_content_id": 100, "name": "Synthetic Cavern",
                         "unlocked": True, "completed": None, "unknown_reason": "Completion readiness is unverified."}],
            "matched_count": 1, "truncated": False, **changes}


def prepared(events):
    client, calls = deferred_fixture.client(events)
    payload = request()
    client.model, client.effort, client.web_search = payload["model"], payload["reasoning_effort"], False
    outputs = []
    client.send = outputs.append
    return client, calls, outputs


def event(ident=50, **arguments):
    return tool_message(ident=ident, tool="query_duties", arguments=arguments)


class DutyChecks(unittest.TestCase):
    def test_v16_permission_summary_and_exact_legacy_versions(self):
        wire = request()
        approved = normalize_request(wire)
        self.assertEqual(approved.duty_lookup, {"scope": SCOPE})
        self.assertEqual(approved.context({})["duty_lookup"], {"scope": SCOPE})
        self.assertIsNone(approved.currency_lookup)
        for version in range(1, 16):
            legacy = wire_request(version)
            self.assertIsNone(normalize_request(legacy).duty_lookup)
            legacy["duty_lookup"] = None
            with self.subTest(version=version), self.assertRaises(RuntimeError):
                normalize_request(legacy)
        for summary in ({}, {"scope": "all_duties"}, {"scope": SCOPE, "entries": []}):
            with self.assertRaises(RuntimeError):
                validate_summary(summary)
        for mutation in (lambda value: value.pop("duty_lookup"), lambda value: value.update(duty_lookup={"scope": "wrong"}),
                         lambda value: value.update(version=17)):
            value = request()
            mutation(value)
            with self.assertRaises(RuntimeError):
                normalize_request(value)
        wire["duty_lookup"] = None
        with self.assertRaises(RuntimeError):
            recheck_request(wire, approved)

    def test_strict_optional_filters_and_bounds(self):
        self.assertEqual(validate_arguments({}), {"query": "", "duty_ids": []})
        self.assertEqual(validate_arguments({"duty_ids": [4294967295]}), {"query": "", "duty_ids": [4294967295]})
        for args in ({"query": "x" * 101}, {"query": "a\n"}, {"query": "a\u0085"}, {"query": None},
                     {"duty_ids": [True]}, {"duty_ids": [0]}, {"duty_ids": [4294967296]}, {"duty_ids": [1, 1]},
                     {"duty_ids": list(range(1, 22))}, {"item_ids": [1]}, []):
            with self.subTest(args=args), self.assertRaises(RuntimeError):
                validate_arguments(args)

    def test_results_keep_positive_unknown_and_independent_states(self):
        meta = {"scope": SCOPE}
        value = result()
        self.assertIsNone(validate_result(value, meta, {"query": "SYNTHETIC cavern", "duty_ids": [10]})["entries"][0]["completed"])
        entry = value["entries"][0]
        independent = {**entry, "unlocked": None, "completed": True}
        self.assertTrue(validate_result(result(entries=[independent]), meta, {})["entries"][0]["completed"])
        unsupported = {**entry, "instance_content_id": None, "unlocked": None, "completed": None}
        validate_result(result(entries=[unsupported]), meta, {})
        for invalid in ({**entry, "unlocked": False}, {**entry, "completed": False}, {**entry, "unknown_reason": None},
                        {**entry, "instance_content_id": None}, {**entry, "unlocked": 1}, {**entry, "duty_id": True},
                        {**entry, "completed": True, "unknown_reason": "Unexpected"}, {**entry, "name": "bad\nname"}):
            with self.subTest(entry=invalid), self.assertRaises(RuntimeError):
                validate_result(result(entries=[invalid]), meta, {})
        self.assertIn("Null is unknown", INSTRUCTIONS)
        self.assertIn("Do not infer one flag from the other", INSTRUCTIONS)
        self.assertIn("Never send private duty state", INSTRUCTIONS)

    def test_result_envelope_filters_and_truncation_are_strict(self):
        meta = {"scope": SCOPE}
        for changes, args in (({"scope": "all"}, {}), ({"matched_count": True}, {}), ({"matched_count": 2}, {}),
                              ({"truncated": True}, {}), ({"available": False}, {}), ({"coverage": ""}, {}),
                              ({"unavailable_reason": "unexpected"}, {}), ({}, {"query": "other"}), ({}, {"duty_ids": [99]}),
                              ({"entries": result()["entries"] * 2, "matched_count": 2}, {}),
                              ({"captured_at_utc": (datetime.now(timezone.utc) + timedelta(days=1)).isoformat()}, {})):
            with self.subTest(changes=changes, args=args), self.assertRaises(RuntimeError):
                validate_result(result(**changes), meta, args)
        unavailable = result(available=False, unavailable_reason="Loading", entries=[], matched_count=0)
        self.assertFalse(validate_result(unavailable, meta, {})["available"])
        validate_result(result(entries=[], matched_count=0), meta, {})
        validate_result(result(matched_count=2, truncated=True), meta, {})

    def test_on_demand_dispatch_has_summary_only_and_audits_use(self):
        client, calls, outputs = prepared([event(query="cavern"), *completed()])
        reads = []
        def query(args):
            self.assertIn("turn/start", [method for method, _ in calls])
            reads.append(args)
            return result()
        with patch.object(companion, "emit") as emit:
            client.ask(request(), duty_query=query)
        self.assertEqual(reads, [{"query": "cavern", "duty_ids": []}])
        thread = next(params for method, params in calls if method == "thread/start")
        deferred_fixture.assert_exposed(self, client, calls, [TOOL])
        self.assertIn(INSTRUCTIONS, deferred_fixture.instructions(client, calls))
        turn = next(params for method, params in calls if method == "turn/start")
        self.assertNotIn('"unlocked"', json.dumps(turn))
        self.assertTrue(outputs[0]["result"]["success"])
        self.assertIn("Duty state observed", emit.call_args.args[0]["question_context"])
        self.assertTrue(any(call.args[0].get("performance", {}).get("stage") == "tool.duties" for call in emit.call_args_list))

    def test_permission_absence_and_unrequested_reader_never_capture(self):
        client, calls, _ = prepared(completed())
        with patch.object(companion, "emit") as emit:
            client.ask(request(duty_lookup=None), duty_query=lambda _: self.fail("No permission"))
        self.assertEqual(next(params for method, params in calls if method == "thread/start")["dynamicTools"], [])
        self.assertNotIn("Duty state observed", emit.call_args.args[0]["question_context"])
        client, _, _ = prepared(completed())
        with patch.object(companion, "emit"):
            client.ask(request(), duty_query=lambda _: self.fail("No lookup requested"))
        client, _, outputs = prepared([event()])
        with patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure) as failure:
            client.ask(request(duty_lookup=None), duty_query=lambda _: self.fail("Disabled"))
        self.assertEqual(failure.exception.kind, "permission")
        self.assertFalse(outputs[0]["result"]["success"])
        client, calls, _ = prepared(completed())
        with self.assertRaises(RuntimeError):
            client.ask(request())
        self.assertFalse(calls)

    def test_capture_identity_and_access_revocation_abort_answer(self):
        first = result()
        for key, replacement in (("captured_at_utc", (datetime.fromisoformat(first["captured_at_utc"]) - timedelta(seconds=1)).isoformat()),
                                 ("coverage", "Changed capture coverage")):
            client, _, outputs = prepared([event(), event(ident=51)])
            values = iter([first, {**first, key: replacement}])
            with patch.object(companion, "emit"), self.assertRaisesRegex(RuntimeError, "Duties lookup failed"):
                client.ask(request(), duty_query=lambda _: next(values))
            self.assertTrue(outputs[0]["result"]["success"])
            self.assertFalse(outputs[1]["result"]["success"])
        client, _, outputs = prepared([event()])
        def denied(_):
            raise PermissionError("synthetic revoked access")
        with patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure) as failure:
            client.ask(request(), duty_query=denied)
        self.assertEqual(failure.exception.kind, "permission")
        self.assertFalse(outputs[0]["result"]["success"])

    def test_shared_call_budget_and_correctable_arguments(self):
        value = result()
        client, _, outputs = prepared([event(ident=50 + index) for index in range(13)])
        calls = []
        with patch.object(companion, "emit"), self.assertRaises(RuntimeError):
            client.ask(request(), duty_query=lambda args: calls.append(args) or value)
        self.assertEqual(len(calls), 12)
        self.assertFalse(outputs[-1]["result"]["success"])
        client, _, outputs = prepared([event(duty_ids=[0]), event(ident=51), *completed()])
        calls = []
        with patch.object(companion, "emit"):
            client.ask(request(), duty_query=lambda args: calls.append(args) or value)
        self.assertEqual(len(calls), 1)
        self.assertFalse(outputs[0]["result"]["success"])
        self.assertTrue(outputs[1]["result"]["success"])

    def test_pipe_correlates_duty_result_kind_id_and_single_reply(self):
        messages, responses = [], []
        ready = threading.Event()
        def emit(value):
            messages.append(value)
            ready.set()
        bridge = QuestQueryBridge(threading.Event(), emit, timeout=2)
        worker = threading.Thread(target=lambda: responses.append(bridge.query({}, "duty")))
        worker.start()
        self.assertTrue(ready.wait(2))
        with bridge.lock:
            ident = bridge.pending
        for invalid in ({"currency_result": {"id": ident, "result": {}}}, {"duty_result": {"id": ident + 1, "result": {}}}):
            with self.assertRaises(RuntimeError):
                bridge.accept(invalid)
        bridge.accept({"duty_result": {"id": ident, "result": {"synthetic": True}}})
        worker.join(2)
        self.assertFalse(worker.is_alive())
        self.assertEqual(messages[0]["duty_query"]["arguments"], {"query": "", "duty_ids": []})
        self.assertEqual(responses, [{"synthetic": True}])
        with self.assertRaises(RuntimeError):
            bridge.accept({"duty_result": {"id": ident, "result": {}}})

    def test_csharp_producer_fixture_survives_real_transport(self):
        path = Path(__file__).resolve().parents[2] / ".test-tmp" / "duty-producer.json"
        self.assertTrue(path.exists(), "Produce required fixtures with tests/Duty.Tests --produce-fixtures .test-tmp/duty-producer.json")
        fixtures = json.loads(path.read_text(encoding="utf-8"))
        self.assertEqual({value["name"] for value in fixtures},
                         {"true_and_unknown", "independent_completed", "unavailable", "unicode_truncation"})
        for fixture in fixtures:
            with self.subTest(name=fixture["name"]):
                validate_result(fixture["result"], {"scope": SCOPE}, fixture["arguments"])
                client, _, outputs = prepared([event(**fixture["arguments"]), *completed()])
                with patch.object(companion, "emit"):
                    client.ask(request(), duty_query=lambda _, value=fixture["result"]: deepcopy(value))
                transported = json.loads(outputs[0]["result"]["contentItems"][0]["text"])
                self.assertEqual(transported, fixture["result"])


if __name__ == "__main__":
    unittest.main()
