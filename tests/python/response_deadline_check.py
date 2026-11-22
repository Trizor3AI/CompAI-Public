"""Response-limit regressions with a fake clock/provider; no waits or live requests."""
import io
import json
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import companion
from companion_provider import ProviderEvent
from deferred_runtime_check import Harness


class DeadlineHarness(Harness):
    def __init__(self, *, effort="medium", completed_at=None, cancelled_at=None):
        super().__init__([], reasoning_effort=effort, web_search=True)
        self.now = 1000.0
        self.started_at = self.now
        self.completed_at = None if completed_at is None else self.now + completed_at
        self.cancelled_at = None if cancelled_at is None else self.now + cancelled_at
        self.answer_sent = False
        self.waits = []

    def read(self, *, timeout):
        self.waits.append(timeout)
        if self.cancelled_at is not None and self.now + timeout >= self.cancelled_at:
            self.now = self.cancelled_at
            self.runner.cancel_event.set()
            raise RuntimeError("Question cancelled")
        if self.completed_at is not None and self.now + timeout >= self.completed_at:
            self.now = self.completed_at
            if not self.answer_sent:
                self.answer_sent = True
                return ProviderEvent(kind="answer", text='{"answer":"Synthetic delayed response."}')
            return ProviderEvent(kind="completed", status="completed")
        self.now += timeout
        raise TimeoutError()

    def run(self, timeout=None):
        with patch.object(companion.time, "monotonic", side_effect=lambda: self.now), \
                patch.object(companion, "emit", side_effect=self.emitted.append), \
                patch.object(companion, "GameDataTools", return_value=self.public):
            self.runner.ask(self.wire, personality_details_query=self.reader,
                            response_timeout_seconds=timeout)


class ResponseDeadlineChecks(unittest.TestCase):
    def assert_limit(self, harness, expected):
        self.assertIn(f"{expected} seconds to finish", harness.provider.open.call_args.args[0])
        self.assertEqual(harness.public.question_deadline, harness.started_at + expected)
        harness.provider.start.assert_called_once()
        harness.turn.finish.assert_called_once()
        self.assertTrue(harness.waits)
        self.assertTrue(all(0 < wait <= 10 for wait in harness.waits))

    def test_normal_response_can_complete_after_old_limit(self):
        harness = DeadlineHarness(completed_at=240)
        harness.run()
        self.assert_limit(harness, 300)
        harness.turn.cancel.assert_not_called()
        self.assertEqual(next(row["question_answer"] for row in harness.emitted if "question_answer" in row),
                         "Synthetic delayed response.")

    def test_high_and_above_automatically_allow_ten_minutes(self):
        for effort in ("high", "xhigh", "max", "ultra"):
            with self.subTest(effort=effort):
                harness = DeadlineHarness(effort=effort, completed_at=540)
                harness.run()
                self.assert_limit(harness, 600)
                harness.turn.cancel.assert_not_called()

    def test_defaults_and_explicit_overrides_expire_once_without_retry(self):
        for effort, override, expected in (("none", None, 300), ("minimal", None, 300),
                ("low", None, 300), ("medium", None, 300), ("high", None, 600),
                ("high", 60, 60), ("medium", 421, 421), ("medium", 1800, 1800)):
            with self.subTest(effort=effort, override=override):
                harness = DeadlineHarness(effort=effort)
                with self.assertRaises(companion.CompanionFailure) as failed:
                    harness.run(override)
                self.assert_limit(harness, expected)
                harness.turn.cancel.assert_called_once()
                self.assertEqual(harness.now - harness.started_at, expected)
                error = companion.failure_message(failed.exception)
                self.assertIn(f"{expected}-second response limit", error["error"])
                self.assertIn("no automatic retry", error["error"])
                self.assertEqual(error["error_kind"], "provider")
                self.assertFalse(any("question_answer" in row for row in harness.emitted))

    def test_cancellation_stops_extended_wait_without_retry(self):
        harness = DeadlineHarness(effort="high", cancelled_at=200)
        with self.assertRaisesRegex(RuntimeError, "cancelled"):
            harness.run()
        self.assert_limit(harness, 600)
        harness.turn.cancel.assert_called_once()
        self.assertEqual(harness.now - harness.started_at, 200)
        self.assertFalse(any("question_answer" in row for row in harness.emitted))

    def test_resolved_limit_is_frozen_before_provider_start(self):
        harness = DeadlineHarness(effort="medium")
        harness.runner.preflight.side_effect = lambda: setattr(harness.runner, "effort", "high") or {}
        with self.assertRaises(companion.CompanionFailure) as failed:
            harness.run()
        self.assert_limit(harness, 300)
        self.assertIn("300-second response limit", str(failed.exception))
        harness.turn.cancel.assert_called_once()

    def test_invalid_direct_overrides_fail_before_provider_work(self):
        for value in (0, 59, 1801, True, False, 300.0, "300", [], {}):
            with self.subTest(value=value):
                harness = DeadlineHarness()
                with self.assertRaisesRegex(RuntimeError, "integer from 60 through 1800"):
                    harness.run(value)
                harness.runner.preflight.assert_not_called()
                harness.provider.open.assert_not_called()
                harness.provider.start.assert_not_called()

    def test_cli_rejects_invalid_values_and_non_question_actions_before_runtime(self):
        invalid = [["--response-timeout-seconds", value, "bridge", "ask"]
                   for value in ("59", "1801", "300.0", "true", "3e2", "+300", "3_00", "٣٠٠")]
        invalid += [["--response-timeout-seconds", "300", *action] for action in (
            [], ["status"], ["login"], ["prove"], ["status", "ask"], ["bridge"],
            ["bridge", "status"], ["bridge", "login"], ["bridge", "logout"], ["bridge", "test"])]
        for arguments in invalid:
            with self.subTest(arguments=arguments), patch.object(companion.sys, "argv", ["companion.py", *arguments]), \
                    patch.object(companion.sys, "stderr", io.StringIO()), patch.object(companion, "Companion") as factory, \
                    self.assertRaises(SystemExit) as failed:
                companion.main()
            self.assertEqual(failed.exception.code, 2)
            factory.assert_not_called()

    def test_cli_passes_valid_or_omitted_override_only_to_ask(self):
        for value in (None, 60, 421, 1800):
            arguments = [] if value is None else ["--response-timeout-seconds", str(value)]
            wire = {"version": 2, "question": "Synthetic question", "snapshot": None}
            stream = io.StringIO(json.dumps({"request": wire}) + "\n")
            stream.reconfigure = lambda **_: None
            with self.subTest(value=value), patch.object(companion.sys, "argv", ["companion.py", *arguments, "bridge", "ask"]), \
                    patch.object(companion.sys, "stdin", stream), patch.object(companion, "Companion") as factory, \
                    patch.object(companion.threading, "Thread", side_effect=lambda target, **_: SimpleNamespace(start=target)):
                self.assertEqual(companion.main(), 0)
            factory.return_value.ask.assert_called_once()
            self.assertEqual(factory.return_value.ask.call_args.args, (wire,))
            self.assertEqual(factory.return_value.ask.call_args.kwargs["response_timeout_seconds"], value)
            factory.return_value.prove.assert_not_called()
            factory.return_value.close.assert_called_once()


if __name__ == "__main__":
    unittest.main()
