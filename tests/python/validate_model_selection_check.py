"""Synthetic validator exclusion checks; runtime/evidence boundaries are mocked."""

from contextlib import ExitStack, redirect_stdout
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

import validate_model_selection as validator


ROOT = Path(__file__).resolve().parents[2]
DIRECT_TOOL_ERRORS = (
    "Unexpected directly registered provider tools: ['query_quests', 'request_user_input', 'web_search']",
    "Unexpected directly registered provider tools: ['discover_tools', 'invoke_tool', 'request_user_input', 'web_search']",
)


class ValidatorExclusionChecks(unittest.TestCase):
    def run_matrix(self, error, target="gpt-5.5"):
        scratch = ROOT / ".test-tmp"
        scratch.mkdir(exist_ok=True)
        temporary = tempfile.TemporaryDirectory(prefix="model-validator-fixture-", dir=scratch)
        self.addCleanup(temporary.cleanup)
        report = Path(temporary.name) / "model-selection-validation.json"
        failure = report.with_name("model-selection-validation-failed.json")
        selected = {"gpt-6-sol": ["medium"], target: ["low", "medium"]}
        pin = {"binary_sha256": "synthetic-binary", "catalog_sha256": "synthetic-catalog", "runtime_files": {}}

        def run_case(mode, ident, effort):
            case = {"case": mode, "model": ident, "effort": effort, "passed": ident != target}
            if ident == target:
                case["error"] = error
            return case

        caught = None
        with ExitStack() as stack:
            stack.enter_context(redirect_stdout(io.StringIO()))
            stack.enter_context(patch.object(sys, "argv", ["validate_model_selection.py"]))
            stack.enter_context(patch.object(validator, "REPORT", report))
            stack.enter_context(patch.object(validator, "candidates", return_value=selected))
            stack.enter_context(patch.object(validator.companion, "find_runtime", return_value=Path("synthetic.exe")))
            stack.enter_context(patch.object(validator.companion, "checked_pin", return_value=pin))
            stack.enter_context(patch.object(validator.companion, "checked_web_pin"))
            stack.enter_context(patch.object(validator, "digest", return_value=pin["catalog_sha256"]))
            check_evidence = stack.enter_context(patch.object(validator, "validate_model_evidence"))
            runtime = stack.enter_context(patch.object(validator, "run_case", side_effect=run_case))
            try:
                validator.main()
            except RuntimeError as exception:
                caught = exception
        return (caught, json.loads(report.read_text()) if report.exists() else None,
                json.loads(failure.read_text()) if failure.exists() else None, runtime, check_evidence)

    def test_only_exact_legacy_and_deferred_errors_exclude_gpt55(self):
        for error in DIRECT_TOOL_ERRORS:
            with self.subTest(error=error):
                caught, report, failure, runtime, evidence = self.run_matrix(error)
                self.assertIsNone(caught)
                self.assertIsNone(failure)
                self.assertTrue(report["all_passed"])
                self.assertEqual([row["id"] for row in report["models"]], ["gpt-6-sol"])
                self.assertEqual(len(report["excluded_models"]), 1)
                excluded = report["excluded_models"][0]
                self.assertEqual(excluded["id"], "gpt-5.5")
                self.assertEqual(excluded["case"]["error"], error)
                self.assertFalse(excluded["case"]["passed"])
                self.assertEqual([call.args for call in runtime.call_args_list if call.args[1] == "gpt-5.5"],
                                 [("enabled-web", "gpt-5.5", "low")])
                self.assertEqual([call.args[0]["id"] for call in evidence.call_args_list], ["gpt-6-sol"])

    def test_other_models_with_same_errors_fail_and_preserve_failure(self):
        for target in ("gpt-6-luna", "gpt-5.5-preview"):
            for error in DIRECT_TOOL_ERRORS:
                with self.subTest(model=target, error=error):
                    caught, report, failure, _, _ = self.run_matrix(error, target)
                    self.assertIsInstance(caught, RuntimeError)
                    self.assertIsNone(report)
                    self.assertFalse(failure["all_passed"])
                    self.assertEqual(failure["failed_case"]["model"], target)
                    self.assertEqual(failure["failed_case"]["error"], error)
                    self.assertEqual([row["id"] for row in failure["models"]], ["gpt-6-sol"])

    def test_different_tools_and_unrelated_errors_remain_failures(self):
        for error in (
            "Unexpected directly registered provider tools: ['discover_tools', 'exec_command', 'invoke_tool', 'request_user_input', 'web_search']",
            "Unexpected directly registered provider tools: ['discover_tools', 'invoke_tool', 'web_search']",
            "Unexpected directly registered provider tools: ['invoke_tool', 'discover_tools', 'request_user_input', 'web_search']",
            "Unexpected directly registered provider tools: ['query_quests', 'request_user_input', 'web_search', 'apply_patch']",
            DIRECT_TOOL_ERRORS[1] + " (additional failure)",
            "Unexpected provider reasoning effort: ['xhigh']",  # Low effort is not the separate Ultra exception.
            "Runtime pin mismatch",
            "Unknown provider failure",
            None,
        ):
            with self.subTest(error=error):
                caught, report, failure, _, _ = self.run_matrix(error)
                self.assertIsInstance(caught, RuntimeError)
                self.assertIsNone(report)
                self.assertEqual(failure["failed_case"]["error"], error)
                self.assertEqual(failure["failed_case"]["model"], "gpt-5.5")


if __name__ == "__main__":
    unittest.main()
