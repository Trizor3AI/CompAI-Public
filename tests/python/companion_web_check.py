"""Offline web opt-in regressions; fake App Server transport and synthetic data only."""
from collections import deque
import copy
import json
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import patch

import companion
from inventory_question import INSTRUCTIONS, WEB_INSTRUCTIONS, validate_request


ROOT = Path(__file__).resolve().parents[2]
EVIDENCE = json.loads((ROOT / "bridge/.isolation-probe" / "web-search-validation.json").read_text(encoding="utf-8"))
PIN = {key: EVIDENCE[key] for key in ("codex_version", "binary_sha256", "catalog_sha256", "model", "runtime_files")}


def request(web_marker=None, version=3):
    value = {"version": version, "question": "What is the FFXIV Aether Current system?",
             "snapshot": None, "history": []}
    if version != 3:
        value.pop("history")
    if web_marker is not None:
        value["web_search"] = web_marker
    return value


def client(messages, web_search=False):
    instance = companion.Companion.__new__(companion.Companion)
    instance.web_search = web_search
    instance.cancel_event = threading.Event()
    instance.preflight = lambda: {"primary": {"usedPercent": 20}}
    calls, replies = [], []

    def call(method, params=None, **kwargs):
        calls.append((method, params))
        if method == "thread/start":
            return {"thread": {"id": "synthetic-thread"}}
        if method == "turn/start":
            return {"turn": {"id": "synthetic-turn"}}
        return {"data": []}

    events = deque(messages)
    instance.call = call
    instance.receive = lambda **_: events.popleft()
    instance.send = replies.append
    return instance, calls, replies


def answer_event():
    return {"method": "item/completed", "params": {"item": {"type": "agentMessage",
            "text": '{"answer":"Aether Currents unlock flight. [Source](https://example.org/ffxiv/aether)"}'}}}


def web_event():
    return {"method": "item/completed", "params": {"item": {"type": "webSearch",
            "query": "FFXIV Aether Currents", "id": "synthetic-search"}}}


def completed_event():
    return {"method": "turn/completed", "params": {"turn": {"status": "completed"}}}


class WebChecks(unittest.TestCase):
    def test_v3_optional_boolean_only_and_legacy_exact_fields(self):
        self.assertEqual(validate_request(request())[1], None)
        for flag in (True, False):
            with self.subTest(flag=flag):
                self.assertEqual(validate_request(request(flag))[1], None)
        for flag in (None, 0, 1, "true", [], {}):
            payload = request()
            payload["web_search"] = flag
            with self.subTest(bad_flag=flag), self.assertRaises(RuntimeError):
                validate_request(payload)
        for version in (1, 2):
            payload = request(True, version)
            if version == 1:
                payload["snapshot"] = {}
            with self.subTest(version=version), self.assertRaisesRegex(RuntimeError, "fields"):
                validate_request(payload)

    def test_web_prompt_replaces_offline_acquisition_limit(self):
        self.assertIn("use ONLY the bundled reference", INSTRUCTIONS)
        self.assertNotIn("use ONLY the bundled reference", WEB_INSTRUCTIONS)
        self.assertIn("direct HTTPS page URL", WEB_INSTRUCTIONS)
        self.assertIn("Never put inventory counts", WEB_INSTRUCTIONS)

    def test_missing_mismatched_and_incomplete_web_evidence_fail_closed(self):
        with tempfile.TemporaryDirectory() as directory:
            catalog = Path(directory) / "no-patch-models.json"
            with patch.object(companion, "CATALOG", catalog):
                with self.assertRaisesRegex(RuntimeError, "evidence"):
                    companion.checked_web_pin(PIN)
                for mutation in (
                    lambda report: report.update(binary_sha256="wrong"),
                    lambda report: report.update(runtime_files={}),
                    lambda report: report["cases"].pop(),
                    lambda report: report["cases"][0]["checks"].update(no_mcp_servers=False),
                    lambda report: report["cases"][1].update(code_host=[]),
                    lambda report: report["cases"][0].update(nested_tool_declarations=[]),
                ):
                    report = copy.deepcopy(EVIDENCE)
                    mutation(report)
                    (catalog.parent / "web-search-validation.json").write_text(json.dumps(report), encoding="utf-8")
                    with self.subTest(mutation=mutation), self.assertRaisesRegex(RuntimeError, "evidence"):
                        companion.checked_web_pin(PIN)
                (catalog.parent / "web-search-validation.json").write_text(json.dumps(EVIDENCE), encoding="utf-8")
                companion.checked_web_pin(PIN)

    def test_missing_evidence_stops_before_process_start(self):
        with tempfile.TemporaryDirectory() as directory, \
             patch.object(companion, "CATALOG", Path(directory) / "no-patch-models.json"), \
             patch.object(companion, "find_runtime", return_value=Path(directory) / "codex.exe"), \
             patch.object(companion, "checked_pin", return_value=PIN), \
             patch.object(companion.subprocess, "Popen") as popen:
            with self.assertRaisesRegex(RuntimeError, "evidence"):
                companion.Companion(web_search=True)
            popen.assert_not_called()

    def test_request_and_process_modes_must_match_before_thread(self):
        for process_mode, request_mode in ((False, True), (True, False)):
            instance, calls, _ = client([], process_mode)
            with self.subTest(process_mode=process_mode, request_mode=request_mode), \
                 self.assertRaisesRegex(RuntimeError, "does not match"):
                instance.ask(request(request_mode))
            self.assertEqual(calls, [])

    def test_preflight_requires_matching_effective_web_mode(self):
        for wanted in (False, True):
            for actual in ("disabled", "live"):
                instance = companion.Companion.__new__(companion.Companion)
                instance.web_search = wanted
                instance.model_allowlist = {companion.MODEL: ["medium"]}
                instance.account = lambda: {"type": "chatgpt"}
                config = {"model_provider": "openai", "model": companion.MODEL,
                          "forced_login_method": "chatgpt", "approval_policy": "never",
                          "sandbox_mode": "read-only", "web_search": actual}
                def call(method, params=None):
                    if method == "config/read":
                        return {"config": config}
                    if method == "model/list":
                        return {"data": [{"model": companion.MODEL,
                                 "supportedReasoningEfforts": [{"reasoningEffort": "medium"}]}]}
                    return {"rateLimits": {"primary": {"usedPercent": 20}}}
                instance.call = call
                with self.subTest(wanted=wanted, actual=actual):
                    if actual == ("live" if wanted else "disabled"):
                        self.assertEqual(instance.preflight()["primary"]["usedPercent"], 20)
                    else:
                        with self.assertRaisesRegex(RuntimeError, "web_search"):
                            instance.preflight()

    def test_observed_web_event_reports_search_without_inventory_footer(self):
        instance, calls, _ = client([web_event(), answer_event(), completed_event()], True)
        with patch.object(companion, "emit") as emitted:
            instance.ask(request(True))
        result = emitted.call_args.args[0]
        self.assertTrue(result["search_performed"])
        self.assertEqual(result["question_context"], "Web lookup performed for this reply.")
        self.assertNotIn("inventory snapshot", result["question_answer"].lower())
        self.assertIn("https://example.org", result["question_answer"])
        thread = next(p for m, p in calls if m == "thread/start")
        self.assertIn("hosted web lookup", thread["developerInstructions"])
        self.assertEqual(thread["dynamicTools"], [])

    def test_default_v3_answer_has_no_search_or_inventory_footer(self):
        instance, calls, _ = client([answer_event(), completed_event()], False)
        with patch.object(companion, "emit") as emitted:
            instance.ask(request())
        result = emitted.call_args.args[0]
        self.assertFalse(result["search_performed"])
        self.assertEqual(result["question_context"], "")
        self.assertNotIn("inventory snapshot", result["question_answer"].lower())
        instructions = next(p for m, p in calls if m == "thread/start")["developerInstructions"]
        self.assertTrue(instructions.startswith(INSTRUCTIONS))
        self.assertIn('"model": "gpt-6-sol", "reasoning_effort": "medium"', instructions)

    def test_no_web_observation_fails_without_emitting_answer(self):
        instance, calls, _ = client([answer_event(), completed_event()], True)
        with patch.object(companion, "emit") as emitted, \
             self.assertRaisesRegex(RuntimeError, "could not be verified"):
            instance.ask(request(True))
        self.assertFalse(any("question_answer" in event.args[0] for event in emitted.call_args_list))
        self.assertIn("turn/interrupt", [method for method, _ in calls])

    def test_unexpected_interactive_call_is_rejected_in_web_mode(self):
        message = {"id": 88, "method": "item/tool/call", "params": {"tool": "exec_command"}}
        instance, calls, replies = client([message], True)
        with patch.object(companion, "emit"), self.assertRaisesRegex(RuntimeError, "Unexpected tool"):
            instance.ask(request(True))
        self.assertEqual(replies[0]["id"], 88)
        self.assertIn("error", replies[0])
        self.assertIn("turn/interrupt", [method for method, _ in calls])

    def test_unexpected_web_event_is_rejected_when_search_off(self):
        instance, calls, _ = client([web_event()], False)
        with patch.object(companion, "emit"), self.assertRaisesRegex(RuntimeError, "Unexpected web search"):
            instance.ask(request())
        self.assertIn("turn/interrupt", [method for method, _ in calls])


if __name__ == "__main__":
    unittest.main()
