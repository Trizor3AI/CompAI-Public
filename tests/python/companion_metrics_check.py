"""Offline numeric telemetry and owned-process fixtures. No account or model calls."""
import ctypes
from ctypes import wintypes
import json
import math
import os
import subprocess
import sys
import unittest
from unittest.mock import patch

import companion
import deferred_fixture
import companion_metrics as metrics
from companion_provider import CodexTurn, ProviderEvent
from collections import deque
from types import SimpleNamespace
import companion_process as processes
import inventory_question_check as questions
import quest_lookup_check as quests
import context_expansion_check as context
import personality_details_check as personalities
from datetime import datetime, timedelta, timezone


def usage_event(total=None, **changes):
    return {"method": "thread/tokenUsage/updated", "params": {
        "threadId": "fixture-thread", "turnId": "fixture-turn", "tokenUsage": {
            "total": total, "last": {"inputTokens": 1, "cachedInputTokens": 0,
                                      "outputTokens": 2, "reasoningOutputTokens": 0}}, **changes}}


def rows(emitted):
    return [call.args[0]["performance"] for call in emitted.call_args_list
            if "performance" in call.args[0]]


class MetricsChecks(unittest.TestCase):
    def test_timing_allowlist_finite_and_exception_path(self):
        output = []
        clock = iter([1., 1.25]).__next__
        with self.assertRaises(ValueError), metrics.measured("tool.inventory", output.append, clock=clock):
            raise ValueError("synthetic private error")
        self.assertEqual(output, [{"performance": {"stage": "tool.inventory", "elapsed_ms": 250.}}])
        for stage, end in (("private prompt", 2), ("provider.turn", float("nan")),
                           ("provider.turn", float("inf")), ("provider.turn", -1)):
            metrics.emit_elapsed(output.append, stage, 0, clock=lambda: end)
        self.assertEqual(len(output), 1)

    def test_latest_total_replaces_repeated_events_and_never_uses_last(self):
        output = []
        turn = metrics.TurnMetrics(output.append, clock=lambda: 1.)
        first = {"inputTokens": 100, "cachedInputTokens": 20, "outputTokens": 10, "reasoningOutputTokens": 4}
        latest = {**first, "inputTokens": 150, "outputTokens": 25}
        for value in (first, first, latest, latest):
            event = usage_event(value)
            turn.observe(event["method"], event["params"], "fixture-thread", "fixture-turn")
        turn.first_answer()
        turn.first_answer()
        turn.finish()
        turn.finish()
        self.assertEqual(len(output), 3)
        self.assertEqual(output[-1]["performance"], {"stage": "provider.tokens", "input_tokens": 150,
            "cached_input_tokens": 20, "output_tokens": 25, "reasoning_tokens": 4})

    def test_missing_malformed_and_unrelated_usage_stays_unknown(self):
        turn = metrics.TurnMetrics(lambda _: None)
        for value in (None, {}, [], "private", {"inputTokens": True, "cachedInputTokens": -1,
                "outputTokens": 1.5, "reasoningOutputTokens": metrics.MAX_COUNTER + 1}):
            event = usage_event(value)
            turn.observe(event["method"], event["params"], "fixture-thread", "fixture-turn")
            self.assertEqual(turn.counters, dict.fromkeys(metrics.TOKEN_FIELDS))
        event = usage_event({"inputTokens": 9}, turnId="another-turn")
        turn.observe(event["method"], event["params"], "fixture-thread", "fixture-turn")
        self.assertEqual(turn.counters, dict.fromkeys(metrics.TOKEN_FIELDS))
        event = usage_event({"inputTokens": 0})
        turn.observe(event["method"], event["params"], "fixture-thread", "fixture-turn")
        self.assertEqual(turn.counters["input_tokens"], 0)
        self.assertIsNone(turn.counters["output_tokens"])
        turn.observe(event["method"], usage_event(None)["params"], "fixture-thread", "fixture-turn")
        self.assertIsNone(turn.counters["input_tokens"])

    def test_real_dispatch_emits_only_safe_numeric_measurements_before_answer(self):
        total = {"inputTokens": 80, "cachedInputTokens": 50, "outputTokens": 15, "reasoningOutputTokens": 7}
        meta = quests.summary()
        client, _ = deferred_fixture.client([
            quests.tool_message(), usage_event(total), usage_event(total), *quests.completed()])
        with patch.object(companion, "emit") as emitted:
            client.ask(quests.request(meta), quest_query=lambda args: quests.result(meta, args))
        performance = [row for row in rows(emitted) if row["stage"] not in ("provider.activity", "request.activity")]
        self.assertEqual([row["stage"] for row in performance],
            ["tool.quests", "provider.first_answer", "provider.turn", "provider.tokens"])
        self.assertEqual(performance[-1]["input_tokens"], 80)
        self.assertIn("question_answer", emitted.call_args.args[0])
        for row in performance[:-1]:
            self.assertEqual(set(row), {"stage", "elapsed_ms"})
            self.assertTrue(math.isfinite(row["elapsed_ms"]) and row["elapsed_ms"] >= 0)
        self.assertNotIn("fixture-thread", json.dumps(performance))
        self.assertNotIn("prerequisite", json.dumps(performance))

    def test_visible_preview_marks_first_answer_once_and_failure_finishes(self):
        common = {"threadId": "fixture-thread", "turnId": "fixture-turn"}
        client, _ = questions.QuestionChecks().client([
            {"method": "item/started", "params": {**common, "item": {
                "type": "agentMessage", "id": "answer-item", "phase": "final_answer", "text": '{"answer":"Visible'}}},
            {"method": "turn/completed", "params": {"turn": {"status": "failed"}}}])
        with patch.object(companion, "emit") as emitted, self.assertRaises(companion.CompanionFailure):
            client.ask({"version": 2, "question": "A synthetic question", "snapshot": None})
        performance = [row for row in rows(emitted) if row["stage"] not in ("provider.activity", "request.activity")]
        self.assertEqual([row["stage"] for row in performance],
                         ["provider.first_answer", "provider.turn", "provider.tokens"])
        self.assertEqual(performance[-1], {"stage": "provider.tokens", **dict.fromkeys(metrics.TOKEN_FIELDS)})
        events = [call.args[0] for call in emitted.call_args_list]
        self.assertTrue(any(event.get("question_partial") == "Visible" for event in events))

    def test_observed_items_dedupe_strictly_and_usage_is_not_model_calls(self):
        output = []
        turn = metrics.TurnMetrics(output.append)
        for _ in range(3):
            turn.observe_event(ProviderEvent(kind="reasoning", observation_id="private-item", strictly_correlated=True))
            turn.observe_event(ProviderEvent(kind="search_started", observation_id="private-search", strictly_correlated=True))
            turn.observe_event(ProviderEvent(kind="search", observation_id="private-search", strictly_correlated=True))
            turn.observe_event(ProviderEvent(kind="usage", usage={"input_tokens": 99}, strictly_correlated=True))
            turn.tool("private-call")
        for event in (ProviderEvent(kind="reasoning", observation_id="other", correlated=False, strictly_correlated=True),
                      ProviderEvent(kind="reasoning", observation_id="other"),
                      ProviderEvent(kind="search", observation_id=None, strictly_correlated=True)):
            turn.observe_event(event)
        counts = [row["performance"] for row in output if row["performance"]["stage"] == "provider.activity"]
        self.assertEqual(len(counts), 3)
        self.assertEqual(counts[-1], {"stage": "provider.activity", "tool_calls": 1, "web_searches": 1, "reasoning_items": 1})
        self.assertNotIn("private", json.dumps(output))
        self.assertNotIn("other", json.dumps(output))
        for index in range(1100):
            turn.tool(str(index))
        self.assertEqual(len(turn._observed["tool_calls"]), 1024)

    def test_codex_item_ids_are_correlated_without_reasoning_content(self):
        common = {"threadId": "fixture-thread", "turnId": "fixture-turn"}
        messages = deque([
            {"method": method, "params": {**common, "item": {"type": kind, "id": identifier,
                "text": "private reasoning should never be retained", "query": "private query"}}}
            for kind, identifier in (("reasoning", "r1"), ("webSearch", "w1"))
            for method in ("item/started", "item/completed")])
        messages.append({"method": "item/completed", "params": {**common, "turnId": "unrelated",
            "item": {"type": "reasoning", "id": "r2"}}})
        adapter = CodexTurn(SimpleNamespace(receive=lambda **_: messages.popleft()),
                            "fixture-thread", "fixture-turn", lambda _: None)
        observed, output = [], []
        turn = metrics.TurnMetrics(output.append)
        while messages:
            event = adapter.read(1)
            observed.append(event)
            turn.observe_event(event)
        self.assertEqual([event.kind for event in observed], ["reasoning", "reasoning", "search_started", "search", "other"])
        self.assertTrue(all(event.text is None for event in observed))
        self.assertEqual(len(turn._observed["reasoning_items"]), 1)
        self.assertEqual(len(turn._observed["web_searches"]), 1)
        self.assertNotIn("private", json.dumps(output))

    def test_startup_and_preflight_failure_are_measured_without_private_error(self):
        with patch.object(companion, "find_runtime", side_effect=OSError("private runtime path")), \
                patch.object(companion, "emit") as emitted, self.assertRaises(OSError):
            companion.Companion()
        self.assertEqual(rows(emitted)[0]["stage"], "runtime.startup")
        client = object.__new__(companion.Companion)
        client.account = lambda: {"type": None}
        with patch.object(companion, "emit") as emitted, self.assertRaises(companion.CompanionFailure):
            client.preflight()
        self.assertEqual(rows(emitted)[0]["stage"], "runtime.preflight")
        self.assertNotIn("private", json.dumps(rows(emitted)))

    def test_instruction_prefix_is_stable_across_questions(self):
        instructions, turns = [], []
        for question in ("A synthetic question", "Another synthetic question"):
            client, calls = questions.QuestionChecks().client(quests.completed())
            payload = personalities.request(question=question, personality_details_lookup=None)
            with patch.object(companion, "emit"):
                client.ask(payload)
            instructions.append(next(params["developerInstructions"] for method, params in calls if method == "thread/start"))
            turns.append(next(params["input"] for method, params in calls if method == "turn/start"))
        self.assertEqual(instructions[0], instructions[1])
        self.assertNotEqual(turns[0][0], turns[1][0])
        for turn in turns:
            self.assertEqual(json.loads(turn[1]["text"].split("\n", 1)[1])["personality"], payload["personality"])

    def test_character_sections_have_independent_stamps_but_paging_stays_frozen(self):
        selections = [context.detail_args("jobs"), context.detail_args("equipped"),
                      context.detail_args("gearsets"), context.detail_args("gearset", gearset_id=0),
                      context.detail_args("gearset", gearset_id=1)]
        captured = datetime.now(timezone.utc) - timedelta(seconds=10)
        snapshots = { (args["view"], args["gearset_id"]): context.detail_result(args,
            captured_at_utc=(captured + timedelta(seconds=index)).isoformat())
            for index, args in enumerate(selections)}
        # Query a later page after other sections; it must retain the original jobs timestamp.
        selections.append(context.detail_args("jobs", offset=1))
        messages = [context.message("query_character_details", ident=i, args=args)
                    for i, args in enumerate(selections)]
        payload, client, _ = context.prepared([*messages, *quests.completed()],
            character_details_lookup={"scope": context.details.SCOPE})
        def query(args):
            result = snapshots[(args["view"], args["gearset_id"])]
            return result if args["offset"] == 0 else {**result, "offset": args["offset"], "entries": []}
        with patch.object(companion, "emit") as emitted:
            client.ask(payload, character_details_query=query)
        footer = emitted.call_args.args[0]["question_context"]
        self.assertIn("separate sections may reflect different times", footer)
        self.assertEqual(sum(row["stage"] == "tool.character_details" for row in rows(emitted)), 6)
        for snapshot in snapshots.values():
            self.assertIn(snapshot["captured_at_utc"], footer)
        # A different stamp on another page of the same selector is still rejected.
        payload, client, _ = context.prepared(messages, character_details_lookup={"scope": context.details.SCOPE})
        def changed_page(args):
            result = query(args)
            return result if args["offset"] == 0 else {**result, "captured_at_utc": datetime.now(timezone.utc).isoformat()}
        with patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure):
            client.ask(payload, character_details_query=changed_page)


@unittest.skipUnless(os.name == "nt", "Windows-owned runtime fixture")
class ProcessChecks(unittest.TestCase):
    def setUp(self):
        self.api = ctypes.WinDLL("kernel32", use_last_error=True)
        self.api.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        self.api.OpenProcess.restype = wintypes.HANDLE
        self.api.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        self.api.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]
        self.api.CloseHandle.argtypes = [wintypes.HANDLE]

    def test_owned_descendants_end_even_when_parent_already_exited(self):
        unrelated = subprocess.Popen([sys.executable, "-B", "-c", "import time; time.sleep(30)"],
                                     creationflags=subprocess.CREATE_NO_WINDOW, stderr=subprocess.DEVNULL)
        try:
            for parent_exits in (False, True):
                code = ("import subprocess,sys,time; child=subprocess.Popen([sys.executable,'-B','-c',"
                        "'import time; time.sleep(30)'],creationflags=0x08000000,"
                        "stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL); "
                        "print(child.pid,flush=True); " + ("sys.stdin.readline()" if parent_exits else "time.sleep(30)"))
                parent = processes.start_owned_process([sys.executable, "-B", "-c", code],
                    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True)
                child = None
                try:
                    child_id = int(parent.stdout.readline())
                    child = self.api.OpenProcess(0x100001, False, child_id)  # Own fixture child only.
                    self.assertTrue(child)
                    if parent_exits:
                        parent.stdin.write("exit\n")
                        parent.stdin.flush()
                        parent.wait(timeout=5)
                    processes.close_owned_process(parent)
                    self.assertEqual(self.api.WaitForSingleObject(child, 5000), 0)
                    self.assertIsNotNone(parent.poll())
                    self.assertIsNone(unrelated.poll())
                    processes.close_owned_process(parent)  # Idempotent.
                finally:
                    processes.close_owned_process(parent)
                    if child:
                        if self.api.WaitForSingleObject(child, 0) == 258:
                            self.api.TerminateProcess(child, 1)
                            self.api.WaitForSingleObject(child, 5000)
                        self.api.CloseHandle(child)
        finally:
            unrelated.kill()
            unrelated.wait(timeout=5)

    def test_resume_failure_reaps_suspended_process(self):
        created = []
        original = processes.subprocess.Popen
        def capture(*args, **kwargs):
            process = original(*args, **kwargs)
            created.append(process)
            return process
        with patch.object(processes.subprocess, "Popen", side_effect=capture), \
                patch.object(processes, "_resume_process", side_effect=OSError("fixture")), self.assertRaises(OSError):
            processes.start_owned_process([sys.executable, "-B", "-c", "import time; time.sleep(30)"],
                                          stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
        self.assertEqual(len(created), 1)
        self.assertIsNotNone(created[0].poll())
        self.assertTrue(created[0].stdout.closed)


if __name__ == "__main__":
    unittest.main(verbosity=2)
