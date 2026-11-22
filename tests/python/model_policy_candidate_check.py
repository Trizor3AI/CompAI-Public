"""Synthetic policy-bound model/web evidence checks; never launch a provider."""
from contextlib import ExitStack, redirect_stdout
import copy
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from types import SimpleNamespace

from codex_tool_policy import expected_nested_tools, known_function_declaration_prefixes, policy_id, resource_tools
from model_selection import checked_model_selection, validate_model_evidence
from quest_lookup_runtime_check import disabled_query_rejected
import validate_web_search


ROOT = Path(__file__).resolve().parents[2]
CATALOG = ROOT / "bridge/.isolation-probe/no-patch-models.json"
RESOURCES = ["list_mcp_resource_templates", "list_mcp_resources", "read_mcp_resource"]


def row(pin, efforts, *, clock=True, legacy=False):
    cases = []
    for mode, effort in [("enabled-web", value) for value in efforts] + [("enabled", "medium"), ("disabled", "medium")]:
        case = {"case": mode, "model": "gpt-6-sol", "effort": effort, "passed": True,
                "runtime": copy.deepcopy(pin), "model_requests": 3, "initial_summary_only": True,
                "credential_free": True, "unexpected_wrappers": False,
                "exact_model_and_effort": True, "provider_fallback_disabled": True}
        tools = ([] if legacy else RESOURCES) + (["clock__curr_time"] if clock else [])
        if mode != "disabled":
            tools += ["discover_tools", "invoke_tool"] + (["web__run"] if mode == "enabled-web" else [])
            case.update(lookup_calls=3, pagination_checked=True, unmentioned_id_lookup_checked=True,
                        completion_states_checked=[False, True, None], discovery_metadata_intact=True,
                        web_completion_simulated=mode == "enabled-web", shell_and_patch_denied=True,
                        nested_tools=sorted(tools))
        else:
            case.update(disabled_tool_absent=True, disabled_callback_calls=0, injected_tool_denied=True)
        if not legacy:
            case.update(policy_id=policy_id(pin), clock_tool_expected=clock, mcp_servers_empty=True,
                        nested_tools=sorted(tools), shell_and_patch_denied=True)
        cases.append(case)
    return {"id": "gpt-6-sol", "efforts": list(efforts), "passed": True, "cases": cases, "excluded_efforts": []}


class ModelPolicyChecks(unittest.TestCase):
    def setUp(self):
        self.pin = json.loads((CATALOG.parent / "runtime-pin.json").read_text(encoding="utf-8"))
        self.assertEqual(resource_tools(self.pin), frozenset(RESOURCES))

    def test_exact_new_surface_with_catalog_clock_on_or_off(self):
        for clock in (True, False):
            self.assertEqual(validate_model_evidence(row(self.pin, ["medium"], clock=clock),
                self.pin, ["medium"], catalog_has_clock=clock), ["medium"])

    def test_disabled_quest_denial_ignores_audit_and_other_calls(self):
        audit = {"type": "custom_tool_call_output", "call_id": "call_disabled_audit",
                 "output": json.dumps({"shellDenied": True, "patchDenied": True})}
        requests = [{"input": [audit]}]
        self.assertFalse(disabled_query_rejected(requests, None))
        requests[0]["input"].append({"type": "function_call_output", "call_id": "other_call",
                                      "output": "Unknown tool denied"})
        self.assertFalse(disabled_query_rejected(requests, None))
        target = {"type": "function_call_output", "call_id": "call_disabled_quest", "output": "ignored"}
        requests[0]["input"].append(target)
        self.assertFalse(disabled_query_rejected(requests, None))
        target["output"] = "Unsupported tool: query_quests"
        self.assertTrue(disabled_query_rejected(requests, None))
        self.assertTrue(disabled_query_rejected([], "Unknown tool query_quests"))
        self.assertFalse(disabled_query_rejected([], "Connection closed"))

    def test_new_surface_rejects_extras_omissions_and_missing_mcp_guard(self):
        for change in ("extra", "missing-resource", "missing-clock", "duplicate", "policy", "mcp", "disabled-extra"):
            value = row(self.pin, ["medium"])
            case = value["cases"][-1 if change == "disabled-extra" else 0]
            if change in ("extra", "disabled-extra"):
                case["nested_tools"].append("exec_command")
            elif change == "missing-resource":
                case["nested_tools"].remove("read_mcp_resource")
            elif change == "missing-clock":
                case["nested_tools"].remove("clock__curr_time")
            elif change == "duplicate":
                case["nested_tools"].append("read_mcp_resource")
            elif change == "policy":
                case["policy_id"] = "no_mcp_resources_v1"
            else:
                case["mcp_servers_empty"] = False
            with self.subTest(change=change), self.assertRaises(RuntimeError):
                validate_model_evidence(value, self.pin, ["medium"], catalog_has_clock=True)

    def test_old_evidence_cannot_authorize_new_policy_and_legacy_rejects_resources(self):
        old_pin = {**self.pin, "binary_sha256": "legacy-fixture"}
        old_row = row(old_pin, ["medium"], legacy=True)
        self.assertEqual(validate_model_evidence(old_row, old_pin, ["medium"]), ["medium"])
        old_row["cases"][0]["nested_tools"].append("read_mcp_resource")
        with self.assertRaises(RuntimeError):
            validate_model_evidence(old_row, old_pin, ["medium"])
        efforts = next(entry["supported_reasoning_levels"] for entry in
            json.loads(CATALOG.read_text(encoding="utf-8"))["models"] if entry["slug"] == "gpt-6-sol")
        report = {"schema_version": 2, "policy_id": policy_id(self.pin), "all_passed": True,
                  **{key: self.pin[key] for key in ("binary_sha256", "catalog_sha256", "runtime_files")},
                  "models": [row(self.pin, [entry["effort"] for entry in efforts])]}
        scratch = ROOT / ".test-tmp"
        scratch.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="model-policy-fixture-", dir=scratch) as temporary:
            path = Path(temporary) / "evidence.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            self.assertIn("gpt-6-sol", checked_model_selection(self.pin, CATALOG, path))
            report["schema_version"] = 1
            path.write_text(json.dumps(report), encoding="utf-8")
            with self.assertRaises(RuntimeError):
                checked_model_selection(self.pin, CATALOG, path)

    def test_web_projection_ignores_type_aliases_but_rejects_extra_functions_and_missing_guard(self):
        scratch = ROOT / ".test-tmp"
        scratch.mkdir(exist_ok=True)
        for change in (None, "extra-function", "duplicate-function", "missing-guard"):
            declarations = ["type Role = \"user\" | \"assistant\";", "type ContentBlock ="]
            declarations += known_function_declaration_prefixes(self.pin, web_search=True)
            if change == "extra-function":
                declarations.append("declare const tools: { exec_command(args: {")
            elif change == "duplicate-function":
                declarations.append(declarations[-1])
            probe = {"passed": True, "runtime_pin_verified": True,
                     "policy_id": policy_id(self.pin), "mcp_servers_empty": change != "missing-guard",
                     "checks": {"mcp_servers_empty_before_turn": True, "mcp_servers_empty_after_turn": True},
                     "requests": [{"prompt_tool_declarations": declarations}],
                     "code_host": [{"tools": expected_nested_tools(self.pin, web_search=True),
                                    "require": "undefined", "process": "undefined", "fetch": "undefined"}]}
            with self.subTest(change=change), tempfile.TemporaryDirectory(prefix="web-policy-fixture-", dir=scratch) as temporary:
                evidence = Path(temporary)
                executable = evidence / "synthetic.exe"
                with ExitStack() as stack:
                    stack.enter_context(redirect_stdout(io.StringIO()))
                    stack.enter_context(patch.object(validate_web_search, "EVIDENCE", evidence))
                    stack.enter_context(patch.object(validate_web_search, "find_runtime", return_value=executable))
                    stack.enter_context(patch.object(validate_web_search, "checked_pin", return_value=self.pin))
                    stack.enter_context(patch.object(validate_web_search, "digest", side_effect=lambda path:
                        self.pin["binary_sha256"] if path == executable else self.pin["catalog_sha256"]))
                    stack.enter_context(patch.object(validate_web_search.subprocess, "run", return_value=
                        SimpleNamespace(returncode=0, stdout=json.dumps(probe), stderr="")))
                    if change is None:
                        validate_web_search.main()
                        saved = json.loads((evidence / "web-search-validation.json").read_text())
                        self.assertEqual(saved["schema_version"], 2)
                        self.assertEqual(saved["policy_id"], policy_id(self.pin))
                        self.assertEqual(saved["cases"][0]["nested_tool_declarations"],
                                         known_function_declaration_prefixes(self.pin, web_search=True))
                    else:
                        with self.assertRaises(RuntimeError):
                            validate_web_search.main()
                        self.assertFalse((evidence / "web-search-validation.json").exists())


if __name__ == "__main__":
    unittest.main()
