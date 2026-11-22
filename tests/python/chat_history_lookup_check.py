"""Focused local-only memory protocol and dispatch checks; no provider calls."""
import deferred_fixture
import copy
import json
import threading
import unittest
from unittest.mock import patch

import companion
from chat_history_lookup import (SCOPE, TOOL, validate_arguments, validate_result, validate_summary)
from inventory_question import validate_history, validate_request
from inventory_question_check import QuestionChecks
from inventory_lookup_check import (fixture as inventory_fixture, inventory_message)
from quest_lookup import QuestQueryBridge
from quest_lookup_check import (completed, tool_message, summary as quest_summary, result as quest_result)


def arguments(**changes):
    return {"query": "", "offset": 0, "limit": 5, **changes}


def summary(count=1):
    return {"scope": SCOPE, "exchange_count": count}


def result(**changes):
    return {**summary(), "total_matches": 1, "offset": 0, "next_offset": None,
            "rows": [{"index": 1, "question": "PRIVATE OLD QUESTION", "answer": "PRIVATE OLD ANSWER",
                      "context_label": "Historical snapshot, not current holdings.", "truncated": False}], **changes}


def request(**changes):
    return {"version": 9, "question": "What did we decide before?", "snapshot": None,
            "history": [], "chat_notes": "Prefer concise replies.", "history_lookup": summary(),
            "inventory_lookup": None, "quest_lookup": None, "web_search": False,
            "model": companion.DEFAULT_MODEL, "reasoning_effort": companion.DEFAULT_EFFORT, **changes}


def history_message(ident=50, **changes):
    return tool_message(ident=ident, tool="query_chat_history", **{"arguments": arguments(), **changes})


class ChatHistoryLookupChecks(unittest.TestCase):
    def test_partial_latest_history_is_labelled_and_permission_independent(self):
        pair = {"question": "Synthetic question", "answer": "[Partial latest exchange: verbatim head/tail excerpts.]\nOpening\n[... middle omitted ...]\nFinal qualifier.",
                "is_excerpt": True}
        validate_request(request(history=[pair], history_lookup=None))
        for history in ([{**pair, "is_excerpt": False}], [{**pair, "is_excerpt": 1}],
                        [{**pair, "answer": "Unlabelled"}], [pair, {"question": "Q", "answer": "A"}]):
            with self.assertRaises(RuntimeError):
                validate_history(history, flexible=True)
        with self.assertRaises(RuntimeError):
            validate_history([pair], flexible=False)
        client, calls = deferred_fixture.client(completed())
        with patch.object(companion, "emit") as emit:
            client.ask(request(history=[pair], history_lookup=None),
                       history_query=lambda _: self.fail("Excerpt cannot enable lookup"))
        self.assertEqual(next(p for m, p in calls if m == "thread/start")["dynamicTools"], [])
        self.assertIn("latest exchange is a partial excerpt", emit.call_args.args[0]["question_context"])

    def test_direct_read_pages_validate_utf16_cursors_and_remain_permission_gated(self):
        args = arguments(query="", offset=0, limit=1, index=1, field="answer")
        page = {**summary(), "total_matches": 1, "offset": 0, "next_offset": 3, "rows": [],
                "content": {"index": 1, "field": "answer", "total_chars": 10, "text": "🧭ș"}}
        validate_arguments(args)
        self.assertEqual(validate_result(page, summary(), args), page)
        for mutation in (lambda r: r.update(next_offset=2), lambda r: r.update(next_offset=None),
                         lambda r: r["content"].update(text=""), lambda r: r["content"].update(index=2),
                         lambda r: r["content"].update(field="context_label"),
                         lambda r: r["content"].update(text="\ud800"),
                         lambda r: r["content"].update(total_chars=2)):
            bad = copy.deepcopy(page)
            mutation(bad)
            with self.assertRaises(RuntimeError):
                validate_result(bad, summary(), args)
        for bad in (arguments(index=1), arguments(field="answer"), {**args, "limit": 2},
                    {**args, "query": "search"}, {**args, "index": True}, {**args, "offset": 65537},
                    {**args, "field": "path"}):
            with self.assertRaises(RuntimeError):
                validate_arguments(bad)
        client, _ = deferred_fixture.client([history_message(arguments=args)])
        with patch.object(companion, "emit"), self.assertRaises(RuntimeError):
            client.ask(request(history_lookup=None), history_query=lambda _: self.fail("Disabled direct read"))
        client, calls = deferred_fixture.client([history_message(arguments=args), *completed()])
        sent = []
        client.send = sent.append
        with patch.object(companion, "emit"):
            client.ask(request(), history_query=lambda value: page if value == args else self.fail("Changed arguments"))
        self.assertEqual(json.loads(sent[0]["result"]["contentItems"][0]["text"]), page)
        deferred_fixture.assert_exposed(self, client, calls, [TOOL])

    def test_flexible_history_bounded_without_four_pair_or_8000_character_cliff(self):
        pairs = [{"question": "Q", "answer": "A"} for _ in range(7)]
        pairs[-1]["answer"] = "A" * 9000
        payload = request(history=pairs)
        self.assertEqual(validate_request(payload), (payload["question"], None))
        for history in ([{"question": "Q", "answer": "A" * 24000}], pairs * 1001):
            with self.assertRaises(RuntimeError):
                validate_request(request(history=history))
        legacy = {key: value for key, value in payload.items() if key not in ("chat_notes", "history_lookup")}
        legacy["version"] = 8
        with self.assertRaises(RuntimeError):
            validate_request(legacy)

    def test_notes_summary_and_request_contract(self):
        validate_request(request(chat_notes="", history_lookup=None))
        for changes in ({"chat_notes": "x" * 2001}, {"chat_notes": "bad\x00"}, {"chat_notes": None},
                        {"history_lookup": summary(True)}, {"history_lookup": summary(1001)},
                        {"history_lookup": {**summary(), "scope": "other_chat"}}, {"extra": True}):
            with self.assertRaises(RuntimeError):
                validate_request(request(**changes))

    def test_lazy_history_and_exact_context_annotations(self):
        payload = request(history=[{"question": "Recent", "answer": "Recent answer"}] * 5)
        client, calls = deferred_fixture.client([history_message(), *completed()])
        sent = []
        client.send = sent.append
        with patch.object(companion, "emit") as emit:
            client.ask(payload, history_query=lambda _: result())
        thread = next(p for m, p in calls if m == "thread/start")
        deferred_fixture.assert_exposed(self, client, calls, [TOOL])
        self.assertIn("user-provided goals", thread["developerInstructions"])
        self.assertIn("'you' addresses the companion", thread["developerInstructions"])
        self.assertIn("inventing a user profile", thread["developerInstructions"])
        self.assertIn("notes do not select or replace that personality", thread["developerInstructions"])
        self.assertNotIn("Prefer concise replies", thread["developerInstructions"])
        initial = next(p for m, p in calls if m == "turn/start")["input"][1]["text"]
        self.assertNotIn("PRIVATE OLD", initial)
        context = json.loads(initial.split("\n", 1)[1])
        self.assertEqual(context["chat_notes"], payload["chat_notes"])
        self.assertEqual(context["history_lookup"], summary())
        self.assertEqual(json.loads(sent[0]["result"]["contentItems"][0]["text"]), result())
        self.assertEqual(emit.call_args.args[0]["question_context"],
                         "Recent context: 5 exchanges; chat notes supplied; 1 older chat lookups.")

    def test_disabled_absent_unrelated_and_bad_correlation(self):
        client, calls = deferred_fixture.client(completed())
        with patch.object(companion, "emit") as emit:
            client.ask(request(chat_notes="", history_lookup=None), history_query=lambda _: self.fail("Disabled"))
        self.assertEqual(next(p for m, p in calls if m == "thread/start")["dynamicTools"], [])
        self.assertEqual(emit.call_args.args[0]["question_context"], "Recent context: 0 exchanges; 0 older chat lookups.")
        for payload, message in ((request(history_lookup=None), history_message()),
                                 (request(), history_message(threadId="other")),
                                 (request(), history_message(turnId="other"))):
            client, _ = deferred_fixture.client([message])
            with patch.object(companion, "emit"), self.assertRaises(RuntimeError):
                client.ask(payload, history_query=lambda _: self.fail("Denied before callback"))
        client, _ = deferred_fixture.client(completed())
        with patch.object(companion, "emit"):
            client.ask(request(), history_query=lambda _: self.fail("Do not automatically fetch older history"))

    def test_shared_call_budget_and_ids_across_all_three_tools(self):
        inv, inv_result = inventory_fixture()
        quests = quest_summary()
        payload = request(inventory_lookup=inv, quest_lookup=quests)
        messages = [history_message(i) if i % 3 == 0 else inventory_message(i) if i % 3 == 1
                    else tool_message(ident=i) for i in range(13)]
        client, _ = deferred_fixture.client(messages)
        called = []
        with patch.object(companion, "emit"), self.assertRaises(RuntimeError):
            client.ask(payload, history_query=lambda _: (called.append("history"), result())[1],
                       inventory_query=lambda _: (called.append("inventory"), inv_result)[1],
                       quest_query=lambda args: (called.append("quest"), quest_result(quests, args))[1])
        self.assertEqual(called, ["history", "inventory", "quest"] * 4)
        client, _ = deferred_fixture.client([inventory_message(5), history_message(5)])
        with patch.object(companion, "emit"), self.assertRaises(RuntimeError):
            client.ask(payload, history_query=lambda _: self.fail("Duplicate ID"),
                       inventory_query=lambda _: inv_result, quest_query=lambda _: self.fail("Unused"))

    def test_invalid_pages_indices_and_limits(self):
        self.assertEqual(validate_result(result(), validate_summary(summary()), arguments()), result())
        for args in (arguments(limit=6), arguments(offset=True), arguments(query="x" * 201), arguments(extra=1)):
            with self.assertRaises(RuntimeError):
                validate_arguments(args)
        for mutation in (lambda r: r.update(total_matches=2), lambda r: r.update(next_offset=1),
                         lambda r: r["rows"][0].update(index=0), lambda r: r["rows"][0].update(truncated="yes"),
                         lambda r: r["rows"][0].update(answer="x" * 6001),
                         lambda r: r["rows"][0].update(context_label="x" * 1001)):
            bad = copy.deepcopy(result())
            mutation(bad)
            with self.assertRaises(RuntimeError):
                validate_result(bad, summary(), arguments())

    def test_notes_cannot_change_during_preflight(self):
        payload = request()
        client, calls = deferred_fixture.client([])
        client.preflight = lambda: payload.update(chat_notes="Changed")
        with patch.object(companion, "emit"), self.assertRaises(RuntimeError):
            client.ask(payload, history_query=lambda _: self.fail("No turn started"))
        self.assertNotIn("turn/start", [m for m, _ in calls])

    def test_history_private_pipe_correlation(self):
        cancellation, ready = threading.Event(), threading.Event()
        sent, received = [], []
        bridge = QuestQueryBridge(cancellation, lambda value: (sent.append(value), ready.set()), timeout=1)
        worker = threading.Thread(target=lambda: received.append(bridge.query(arguments(), "history")))
        worker.start()
        self.assertTrue(ready.wait(1))
        self.assertEqual(sent[0], {"history_query": {"id": 1, "arguments": arguments()}})
        with self.assertRaises(RuntimeError):
            bridge.accept({"quest_result": {"id": 1, "result": result()}})
        bridge.accept({"history_result": {"id": 1, "result": result()}})
        worker.join(1)
        self.assertFalse(worker.is_alive())
        self.assertEqual(received, [result()])
        with self.assertRaises(RuntimeError):
            bridge.accept({"history_result": {"id": 1, "result": result()}})

    def test_quota_duration_is_server_supplied_and_optional(self):
        for duration in (300, 10080, None, True, -1, "300", 999999999):
            window = {"usedPercent": 20, "resetsAt": 123, "windowDurationMins": duration}
            quota = companion.quota_windows({"rateLimits": {"primary": window}})["primary"]
            if type(duration) is int and 0 < duration <= 525600:
                self.assertEqual(quota["windowDurationMins"], duration)
            else:
                self.assertNotIn("windowDurationMins", quota)


if __name__ == "__main__":
    unittest.main()
