"""Offline currency boundaries and real bridge dispatch with synthetic balances."""
import deferred_fixture
from copy import deepcopy
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import threading
import unittest
from unittest.mock import patch

import companion
from companion_request import normalize_request, recheck_request
from currency_lookup import SCOPE, TOOL, INSTRUCTIONS, validate_arguments, validate_result, validate_summary
from companion_architecture_check import wire_request
import inventory_question_check as question_checks
from quest_lookup import QuestQueryBridge
from quest_lookup_check import completed, tool_message


def request(**changes):
    value = wire_request(14)
    value.update(version=15, currency_lookup={"scope": SCOPE})
    value.update(changes)
    return value


def result(**changes):
    return {"scope": SCOPE, "captured_at_utc": datetime.now(timezone.utc).isoformat(),
            "available": True, "unavailable_reason": None, "coverage": "Observed container only; missing is unknown.",
            "entries": [{"item_id": 1, "name": "Gil", "quantity": 0}],
            "matched_count": 1, "truncated": False, **changes}


def prepared(events):
    client, calls = deferred_fixture.client(events)
    payload = request()
    client.model, client.effort, client.web_search = payload["model"], payload["reasoning_effort"], False
    outputs = []
    client.send = outputs.append
    return client, calls, outputs


def event(ident=50, **arguments):
    return tool_message(ident=ident, tool="query_currencies", arguments=arguments)


class CurrencyChecks(unittest.TestCase):
    def test_cosmic_csharp_producer_preserves_zero_and_unknown_through_deferred_tool(self):
        path = Path(__file__).resolve().parents[2] / ".test-tmp" / "currency-producer.json"
        fixtures = {row["name"]: row for row in json.loads(path.read_text(encoding="utf-8"))}
        for key in ("cosmic_current_zone_only", "cosmic_absent_outside_zone", "cosmic_oizys", "cosmic_auxesia"):
            fixture = fixtures[key]
            actual = validate_result(fixture["result"], {"scope": SCOPE}, fixture["arguments"])
            client, _, outputs = prepared([event(**fixture["arguments"]), *completed()])
            with patch.object(companion, "emit"):
                client.ask(request(), currency_query=lambda _: actual)
            self.assertTrue(outputs[0]["result"]["success"])
        present = fixtures["cosmic_current_zone_only"]["result"]["entries"]
        self.assertEqual([(row["item_id"], row["quantity"]) for row in present], [(45690, 0), (45691, 42)])
        self.assertEqual(fixtures["cosmic_absent_outside_zone"]["result"]["entries"], [])
        for key, ident, name in (("cosmic_oizys", 48147, "Oizys Credit"), ("cosmic_auxesia", 48148, "Auxesia Credit")):
            self.assertEqual([(row["item_id"], row["name"], row["quantity"])
                              for row in fixtures[key]["result"]["entries"]],
                             [(45690, "Cosmocredit", 0), (ident, name, 42)])

    def test_v15_permission_only_and_legacy_versions_remain_exact(self):
        value = request()
        prepared = normalize_request(value)
        self.assertEqual(prepared.currency_lookup, {"scope": SCOPE})
        self.assertEqual(prepared.context({})["currency_lookup"], {"scope": SCOPE})
        for version in range(1, 15):
            old = wire_request(version)
            self.assertIsNone(normalize_request(old).currency_lookup)
            old["currency_lookup"] = None
            with self.subTest(version=version), self.assertRaises(RuntimeError):
                normalize_request(old)
        for value in ({"scope": SCOPE, "entries": []}, {}, {"scope": "all_currencies"}):
            with self.assertRaises(RuntimeError):
                validate_summary(value)
        value = request()
        approved = normalize_request(value)
        value["currency_lookup"] = None
        with self.assertRaises(RuntimeError):
            recheck_request(value, approved)

    def test_strict_optional_filters_and_uint_bounds(self):
        self.assertEqual(validate_arguments({}), {"query": "", "item_ids": []})
        for args in ({"query": "x" * 101}, {"query": "a\0"}, {"query": "a\n"}, {"query": "a\u0085"}, {"query": None}, {"item_ids": [True]},
                     {"item_ids": [0]}, {"item_ids": [4294967296]}, {"item_ids": [1, 1]},
                     {"item_ids": list(range(1, 22))}, {"view": "all"}, []):
            with self.subTest(args=args), self.assertRaises(RuntimeError):
                validate_arguments(args)

    def test_result_scope_missing_zero_unavailable_and_filters(self):
        summary = {"scope": SCOPE}
        self.assertEqual(validate_result(result(), summary, {})["entries"][0]["quantity"], 0)
        self.assertEqual(validate_result(result(entries=[], matched_count=0), summary, {})["entries"], [])
        unavailable = result(available=False, unavailable_reason="Menu data unavailable", entries=[], matched_count=0)
        self.assertFalse(validate_result(unavailable, summary, {})["available"])
        validate_result(result(), summary, {"query": "GIL", "item_ids": [1]})
        for changes, args in (({"scope": "all_currencies"}, {}), ({"matched_count": True}, {}),
                              ({"matched_count": 2}, {}), ({"truncated": True}, {}),
                              ({"available": False}, {}), ({"unavailable_reason": "bad"}, {}),
                              ({"coverage": ""}, {}), ({}, {"query": "other"}), ({}, {"item_ids": [2]}),
                              ({"captured_at_utc": (datetime.now(timezone.utc)+timedelta(days=1)).isoformat()}, {})):
            with self.subTest(changes=changes, args=args), self.assertRaises(RuntimeError):
                validate_result(result(**changes), summary, args)
        self.assertIn("unknown, never zero", INSTRUCTIONS)

    def test_duplicate_overflow_and_truncated_results(self):
        entries = [{"item_id": i + 1, "name": "Synthetic", "quantity": 2147483647} for i in range(50)]
        validate_result(result(entries=entries, matched_count=51, truncated=True), {"scope": SCOPE}, {})
        # Native JSON byte budget can truncate before the 50-row limit for localized names.
        entries = [{"item_id": i + 1, "name": "界" * 128, "quantity": 1} for i in range(20)]
        validate_result(result(entries=entries, matched_count=50, truncated=True), {"scope": SCOPE}, {})
        with self.assertRaises(RuntimeError):
            validate_result(result(entries=entries, matched_count=50, truncated=False), {"scope": SCOPE}, {})
        for entries in ([{"item_id": 1, "name": "Gil", "quantity": -1}],
                        [{"item_id": 1, "name": "Gil", "quantity": True}],
                        [{"item_id": 1, "name": "Gil", "quantity": 4294967296}],
                        result()["entries"] * 2):
            with self.assertRaises(RuntimeError):
                validate_result(result(entries=entries, matched_count=len(entries)), {"scope": SCOPE}, {})

    def test_summary_only_then_tool_dispatch_independent_of_inventory(self):
        client, calls, outputs = prepared([event(query="gil"), *completed()])
        reads = []
        def query(args):
            self.assertIn("turn/start", [method for method, _ in calls])
            reads.append(args)
            return result()
        with patch.object(companion, "emit") as emit:
            client.ask(request(), currency_query=query)
        self.assertEqual(reads, [{"query": "gil", "item_ids": []}])
        thread = next(params for method, params in calls if method == "thread/start")
        deferred_fixture.assert_exposed(self, client, calls, [TOOL])
        turn = next(params for method, params in calls if method == "turn/start")
        self.assertNotIn('"quantity"', json.dumps(turn))
        self.assertTrue(outputs[0]["result"]["success"])
        self.assertIn("Currency inventory observed", emit.call_args.args[0]["question_context"])

    def test_no_permission_no_reader_and_no_unrequested_reads(self):
        client, calls, _ = prepared(completed())
        with patch.object(companion, "emit"):
            client.ask(request(), currency_query=lambda _: self.fail("Not requested"))
        client, calls, outputs = prepared([event()])
        with patch.object(companion, "emit"), self.assertRaises(RuntimeError):
            client.ask(request(currency_lookup=None), currency_query=lambda _: self.fail("Disabled"))
        self.assertFalse(outputs[0]["result"]["success"])
        client, calls, _ = prepared(completed())
        with self.assertRaises(RuntimeError):
            client.ask(request())
        self.assertFalse(calls)

    def test_same_answer_snapshot_identity_is_frozen(self):
        client, _, outputs = prepared([event(), event(ident=51)])
        first = result()
        changed = deepcopy(first)
        changed["captured_at_utc"] = (datetime.fromisoformat(first["captured_at_utc"]) - timedelta(seconds=1)).isoformat()
        values = iter([first, changed])
        with patch.object(companion, "emit"), self.assertRaisesRegex(RuntimeError, "Currencies lookup failed"):
            client.ask(request(), currency_query=lambda _: next(values))
        self.assertTrue(outputs[0]["result"]["success"])
        self.assertFalse(outputs[1]["result"]["success"])

    def test_currency_pipe_correlates_response_kind_and_id(self):
        messages, responses = [], []
        ready = threading.Event()
        def emit(value):
            messages.append(value)
            ready.set()
        bridge = QuestQueryBridge(threading.Event(), emit, timeout=2)
        worker = threading.Thread(target=lambda: responses.append(bridge.query({}, "currency")))
        worker.start()
        self.assertTrue(ready.wait(2))
        # emit executes under the bridge lock, so acquiring it guarantees pending setup is finished.
        with bridge.lock:
            ident = bridge.pending
        with self.assertRaises(RuntimeError):
            bridge.accept({"inventory_result": {"id": ident, "result": {}}})
        bridge.accept({"currency_result": {"id": ident, "result": {"synthetic": True}}})
        worker.join(2)
        self.assertFalse(worker.is_alive())
        self.assertEqual(messages[0]["currency_query"]["arguments"], {"query": "", "item_ids": []})
        self.assertEqual(responses, [{"synthetic": True}])
        with self.assertRaises(RuntimeError):
            bridge.accept({"currency_result": {"id": ident, "result": {}}})


if __name__ == "__main__":
    unittest.main()
