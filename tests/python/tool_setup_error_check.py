"""Local tool-catalogue setup failures preserve account recovery semantics. No live calls."""
import unittest
from unittest.mock import patch

import companion
from deferred_tools import DeferredToolsError
import inventory_question_check as questions


class ToolSetupErrorChecks(unittest.TestCase):
    def test_local_constructor_failures_are_recoverable_and_start_no_turn(self):
        for message in ("CompAI tool catalogue is invalid.", "CompAI tool catalogue exceeds its bound."):
            with self.subTest(message=message):
                client, calls = questions.QuestionChecks().client([])
                with patch.object(companion, "DeferredTools", side_effect=DeferredToolsError("protocol", message)) as setup, \
                        patch.object(companion, "emit") as emitted, \
                        self.assertRaises(companion.CompanionFailure) as caught:
                    client.ask({"version": 2, "question": "Synthetic question", "snapshot": None})
                failure = companion.failure_message(caught.exception, "provider.request")
                self.assertEqual(failure["error_kind"], "provider")
                self.assertEqual(failure["error_stage"], "provider.request")
                self.assertIn(message, failure["error"])
                self.assertIn("Reload/update CompAI", failure["error"])
                self.assertIn("refreshing account status will not fix", failure["error"])
                self.assertIn("No automatic retry", failure["error"])
                setup.assert_called_once()
                self.assertEqual(calls, [], "No runtime thread/model turn starts for an invalid local catalogue")
                self.assertFalse(any("question_answer" in call.args[0] for call in emitted.call_args_list))

    def test_authentication_failure_keeps_its_original_category(self):
        client, calls = questions.QuestionChecks().client([])
        with patch.object(client, "preflight", side_effect=companion.CompanionFailure("authentication", "Synthetic sign-in required")), \
                patch.object(companion, "DeferredTools", wraps=companion.DeferredTools) as setup, patch.object(companion, "emit"), \
                self.assertRaises(companion.CompanionFailure) as caught:
            client.ask({"version": 2, "question": "Synthetic question", "snapshot": None})
        self.assertEqual(companion.failure_message(caught.exception, "provider.request"), {
            "error": "Synthetic sign-in required", "error_kind": "authentication",
            "error_stage": "provider.authentication"})
        setup.assert_called_once()
        self.assertEqual(calls, [])

    def test_other_runtime_errors_are_not_reclassified_by_local_constructor_catch(self):
        client, calls = questions.QuestionChecks().client([])
        with patch.object(companion, "DeferredTools", side_effect=RuntimeError("Synthetic protocol failure")), \
                patch.object(companion, "emit"), self.assertRaises(RuntimeError) as caught:
            client.ask({"version": 2, "question": "Synthetic question", "snapshot": None})
        self.assertNotIsInstance(caught.exception, companion.CompanionFailure)
        self.assertEqual(companion.failure_message(caught.exception, "provider.request")["error_kind"], "protocol")
        self.assertEqual(calls, [])


if __name__ == "__main__":
    unittest.main()
