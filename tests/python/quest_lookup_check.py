"""Synthetic quest lookup and private bridge regressions; never contacts a provider."""
import deferred_fixture
import copy
import json
from pathlib import Path
import threading
import unittest
from unittest.mock import patch

import companion
from inventory_question import validate_request
from inventory_question_check import QuestionChecks, pedigree_fixture
from quest_context_check import fixture as old_fixture
from quest_lookup import (SUMMARY_FIELDS, SCOPE, TOOL, QuestQueryBridge, encoded_size,
                          validate_arguments, validate_result, validate_summary)


def summary():
    old = old_fixture()
    return {**{key: old[key] for key in SUMMARY_FIELDS}, "scope": SCOPE}


def arguments(**changes):
    return {"view": "all", "query": "", "ids": [], "offset": 0, "limit": 50, **changes}


def result(meta, args=None):
    args = args or arguments()
    rows = old_fixture()["rows"]
    return {**meta, "total_matches": 2, "offset": args["offset"], "next_offset": None,
            "missing_ids": [], "rows": rows}


def request(meta=None):
    return {"version": 6, "question": "Which prerequisite am I missing?", "snapshot": None,
            "history": [], "web_search": False, "quest_lookup": meta}


def tool_message(args=None, ident=50, **params):
    return {"id": ident, "method": "item/tool/call", "params": {
        "threadId": "fixture-thread", "turnId": "fixture-turn", "tool": "query_quests",
        "arguments": args or arguments(), **params}}


def completed():
    return [{"method": "item/completed", "params": {"item": {"type": "agentMessage", "text": '{"answer":"Observed answer."}'}}},
            {"method": "turn/completed", "params": {"turn": {"status": "completed"}}}]


class LookupChecks(unittest.TestCase):
    def test_summary_only_initial_context_and_query_after_start(self):
        meta = summary()
        client, calls = deferred_fixture.client([tool_message(), *completed()])
        outputs, lookups = [], []
        client.send = outputs.append
        def query(args):
            self.assertIn("turn/start", [m for m, _ in calls])
            lookups.append(args)
            return result(meta, args)
        with patch.object(companion, "emit"):
            client.ask(request(meta), quest_query=query)
        thread = next(params for method, params in calls if method == "thread/start")
        deferred_fixture.assert_exposed(self, client, calls, [TOOL])
        self.assertNotIn("all allowed context is already supplied", thread["developerInstructions"])
        turn = next(params for method, params in calls if method == "turn/start")
        context = json.loads(turn["input"][1]["text"].split("\n", 1)[1])
        self.assertEqual(set(context["quest_lookup"]), SUMMARY_FIELDS)
        self.assertNotIn("Synthetic active quest", json.dumps(turn))
        self.assertEqual(len(lookups), 1)
        self.assertTrue(outputs[0]["result"]["success"])
        states = [row["completed"] for row in json.loads(outputs[0]["result"]["contentItems"][0]["text"])["rows"]]
        self.assertEqual(states, [False, True])

    def test_permission_off_has_no_tool_or_summary_and_rejects_injected_call(self):
        client, calls = deferred_fixture.client(completed())
        with patch.object(companion, "emit"):
            client.ask(request(), quest_query=lambda _: self.fail("No permission"))
        self.assertEqual(next(params for method, params in calls if method == "thread/start")["dynamicTools"], [])
        client, calls = deferred_fixture.client([tool_message()])
        with patch.object(companion, "emit"), self.assertRaises(RuntimeError):
            client.ask(request(), quest_query=lambda _: self.fail("No permission"))
        self.assertIn("turn/interrupt", [method for method, _ in calls])

    def test_web_permission_does_not_force_search_for_local_quest_answer(self):
        meta = summary()
        client, calls = deferred_fixture.client([tool_message(), *completed()])
        client.web_search = True
        payload = request(meta)
        payload["web_search"] = True
        with patch.object(companion, "emit") as emit:
            client.ask(payload, quest_query=lambda args: result(meta, args))
        self.assertFalse(emit.call_args.args[0]["search_performed"])
        instructions = next(params for method, params in calls if method == "thread/start")["developerInstructions"]
        self.assertIn("optional for questions answered by local evidence", instructions)
        self.assertNotIn("Only hosted web search is authorized.", instructions)

    def test_wrong_thread_turn_tool_or_duplicate_request_id_rejected(self):
        for changes in ({"threadId": "other"}, {"turnId": None}, {"tool": "exec_command"}):
            client, _ = deferred_fixture.client([tool_message(**changes)])
            with self.subTest(changes=changes), patch.object(companion, "emit"), self.assertRaises(RuntimeError):
                client.ask(request(summary()), quest_query=lambda _: self.fail("Rejected before lookup"))
        meta = summary()
        client, _ = deferred_fixture.client([tool_message(), tool_message()])
        calls = []
        with patch.object(companion, "emit"), self.assertRaises(RuntimeError):
            client.ask(request(meta), quest_query=lambda args: (calls.append(args), result(meta, args))[1])
        self.assertEqual(len(calls), 1)

    def test_unknown_interactive_request_cannot_bypass_admission(self):
        client, calls = deferred_fixture.client([{"id": 8, "method": "item/commandExecution/requestApproval",
                                                "params": {"threadId": "other"}}])
        with patch.object(companion, "emit"), self.assertRaises(RuntimeError):
            client.ask(request(summary()), quest_query=lambda _: self.fail("No lookup"))
        self.assertIn("turn/interrupt", [method for method, _ in calls])

    def test_call_limit_and_result_budget(self):
        meta = summary()
        client, _ = deferred_fixture.client([tool_message(ident=i) for i in range(13)])
        calls = []
        with patch.object(companion, "emit"), self.assertRaises(RuntimeError):
            client.ask(request(meta), quest_query=lambda args: (calls.append(args), result(meta, args))[1])
        self.assertEqual(len(calls), 12)
        large = result(meta)
        large["rows"] = [copy.deepcopy(large["rows"][1]) for _ in range(50)]
        for i, row in enumerate(large["rows"]):
            row.update(native_id=i + 1, row_id=65537 + i, name="x" * 90)
        large["total_matches"] = 50
        self.assertLess(encoded_size(large), 20000)
        client, _ = deferred_fixture.client([tool_message(ident=i) for i in range(12)])
        with patch.object(companion, "emit"), self.assertRaisesRegex(RuntimeError, "shared 120000-byte result budget"):
            client.ask(request(meta), quest_query=lambda _: large)

    def test_bad_arguments_fail_before_callback(self):
        bad = [arguments(view="dialogue"), arguments(query="x" * 201), arguments(ids=[1, 65537]),
               arguments(ids=[65536]), arguments(ids=[True]), arguments(ids=[1], query="name"),
               arguments(offset=True), arguments(offset=-1), arguments(limit=51), arguments(limit=0),
               arguments(extra="bad"), arguments(ids=list(range(1, 52))), arguments(query="nul\x00")]
        for value in bad:
            with self.subTest(value=value), self.assertRaises(RuntimeError):
                validate_arguments(value)
        client, _ = deferred_fixture.client([tool_message(arguments(ids=[1, 65537])), *completed()])
        responses = []
        client.send = responses.append
        with patch.object(companion, "emit"):
            client.ask(request(summary()), quest_query=lambda _: self.fail("Bad arguments"))
        self.assertFalse(responses[0]["result"]["success"])

    def test_result_shape_identity_paging_types_and_size(self):
        meta = validate_summary(summary())
        good = result(meta)
        mutations = [lambda x: x.update(stale=True), lambda x: x.update(next_offset=2),
                     lambda x: x.update(offset=1), lambda x: x.update(total_matches=1),
                     lambda x: x.update(missing_ids=[99]), lambda x: x.update(extra=1),
                     lambda x: x["rows"][0].update(native_id=True), lambda x: x["rows"][0].update(row_id=5),
                     lambda x: x["rows"][0].update(sequence=True), lambda x: x["rows"][0].update(completed=1),
                     lambda x: x["rows"][0].update(name="界" * 4000),
                     lambda x: x["rows"].reverse(), lambda x: x["rows"][0].update(journal_text="x" * 513)]
        for mutate in mutations:
            bad = copy.deepcopy(good)
            mutate(bad)
            with self.subTest(mutation=mutate), self.assertRaises(RuntimeError):
                validate_result(bad, meta, arguments())
        for view in ("active", "completed"):
            with self.assertRaises(RuntimeError):
                validate_result(good, meta, arguments(view=view))
        good["rows"][0]["completed"] = None
        self.assertIsNone(validate_result(good, meta, arguments())["rows"][0]["completed"])

    def test_summary_validation_and_legacy_no_lookup(self):
        for change in ({"stale": 1}, {"active_count": 31}, {"catalog_rows": True}, {"captured_at_utc": "2026-01-01"},
                       {"observed_completed_count": 99999}, {"scope": "complete_history"}, {"extra": 1}):
            with self.subTest(change=change), self.assertRaises(RuntimeError):
                validate_request(request({**summary(), **change}))
        for version in range(1, 6):
            payload = request(summary())
            payload["version"] = version
            with self.assertRaises(RuntimeError):
                validate_request(payload)
        payload = pedigree_fixture()
        payload.update(version=6, quest_lookup=summary())
        self.assertEqual(len(validate_request(payload)[1]["slot_items"]), 2)

    def test_missing_callback_or_bad_response_cannot_publish_answer(self):
        client, calls = deferred_fixture.client(completed())
        with self.assertRaises(RuntimeError):
            client.ask(request(summary()))
        self.assertFalse(calls)
        client, calls = deferred_fixture.client([tool_message(), *completed()])
        with patch.object(companion, "emit") as emit, self.assertRaises(RuntimeError):
            client.ask(request(summary()), quest_query=lambda _: {"bad": "result"})
        self.assertTrue(all("question_answer" not in call.args[0] for call in emit.call_args_list))

    def test_cancel_after_callback_does_not_forward_game_data(self):
        meta = summary()
        client, _ = deferred_fixture.client([tool_message(), *completed()])
        outputs = []
        client.send = outputs.append
        def query(args):
            client.cancel_event.set()
            return result(meta, args)
        with patch.object(companion, "emit"), self.assertRaises(RuntimeError):
            client.ask(request(meta), quest_query=query)
        self.assertFalse(outputs[0]["result"]["success"])
        self.assertNotIn("Synthetic", json.dumps(outputs))

    def test_bridge_correlates_one_response_and_rejects_unknown_duplicate(self):
        sent = []
        bridge = QuestQueryBridge(threading.Event(), sent.append)
        out = []
        thread = threading.Thread(target=lambda: out.append(bridge.query(arguments())))
        thread.start()
        for _ in range(100):
            if sent:
                break
            threading.Event().wait(.001)
        self.assertEqual(sent[0]["quest_query"]["id"], 1)
        with self.assertRaises(RuntimeError):
            bridge.accept({"quest_result": {"id": 2, "result": {}}})
        bridge.accept({"quest_result": {"id": 1, "result": {"local": True}}})
        thread.join(1)
        self.assertFalse(thread.is_alive())
        self.assertEqual(out, [{"local": True}])
        with self.assertRaises(RuntimeError):
            bridge.accept({"quest_result": {"id": 1, "result": {}}})

    def test_bridge_timeout_cancellation_and_early_response(self):
        bridge = QuestQueryBridge(threading.Event(), lambda _: None, timeout=.01)
        with self.assertRaises(RuntimeError):
            bridge.accept({"quest_result": {"id": 1, "result": {}}})
        with self.assertRaisesRegex(TimeoutError, "timed out"):
            bridge.query(arguments())
        bridge.cancel_event.set()
        with self.assertRaises(RuntimeError):
            bridge.query(arguments())

    def test_csharp_producer_fixtures(self):
        path = Path(__file__).resolve().parents[1] / "QuestLookup.Tests" / "producer-fixtures.json"
        if not path.exists():
            self.skipTest("Run C# QuestLookup fixture producer first")
        fixtures = json.loads(path.read_text(encoding="utf-8"))
        args = [arguments(), arguments(view="completed", ids=[1, 2, 65540, 9999]),
                arguments(query="phantoms fillet"), arguments(), arguments(offset=2147483647), arguments()]
        self.assertEqual(len(fixtures["results"]), len(args))
        for value, query in zip(fixtures["results"], args):
            meta = validate_summary({key: value[key] for key in SUMMARY_FIELDS})
            validate_result(value, meta, query)


if __name__ == "__main__":
    unittest.main(defaultTest="LookupChecks")
