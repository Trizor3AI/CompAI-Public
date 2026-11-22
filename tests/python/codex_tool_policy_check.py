"""Synthetic policy/evidence/guard fixtures only; never launch a provider."""
from copy import deepcopy
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import companion
from companion_provider import CodexProvider
from codex_tool_policy import (MCP_RESOURCE_TOOLS, MAX_MCP_STATUS_PAGES,
    resource_tools, policy_id, expected_nested_tools, required_isolation_cases,
    require_no_mcp_servers, declared_function_names, known_function_declaration_prefixes)
from prepare_isolation_catalog import digest


REVIEWED_PIN = {
    "codex_version": "codex-cli 0.167.0", "model": "gpt-6-sol",
    "binary_sha256": "edfdd48f8b458af0906e507b4cb05217b326c3d6e515e9f024e05e3b8d650f70",
    "runtime_files": {
        "codex.exe": "edfdd48f8b458af0906e507b4cb05217b326c3d6e515e9f024e05e3b8d650f70",
        "codex-code-mode-host.exe": "5acd42f62966dd4a498ebfe04c5cae4c4a644a342a65a21bb53f8f36235c3d35"},
    "catalog_sha256": "4c9cb7c9d56372d65fb05740548aab27abc59aec2ceb439afe7bb45aaeabee2e",
}
RESOURCE_CHECKS = {
    name: True for name in (
        "mcp_servers_empty_before_turn", "mcp_servers_empty_after_turn", "mcp_resource_list_empty",
        "mcp_resource_templates_empty", "mcp_unknown_server_read_rejected", "no_sentinel_in_tool_results",
        "outside_fixture_unchanged", "no_node_or_fetch_globals", "code_host_inventory",
        "allowed_snapshot_executed", "one_mcp_resource_diagnostic", "resource_helpers_are_functions")}


class PolicyChecks(unittest.TestCase):
    def test_resource_exception_requires_the_exact_reviewed_identity(self):
        self.assertEqual(resource_tools(REVIEWED_PIN), MCP_RESOURCE_TOOLS)
        changes = [None, {}, {**REVIEWED_PIN, "codex_version": "codex-cli 0.168.0"},
                   {**REVIEWED_PIN, "catalog_sha256": "0" * 64},
                   {**REVIEWED_PIN, "binary_sha256": "0" * 64},
                   {**REVIEWED_PIN, "model": "other-model"}]
        for name in ("codex.exe", "codex-code-mode-host.exe", "unexpected.exe"):
            changed = deepcopy(REVIEWED_PIN)
            changed["runtime_files"][name] = "0" * 64
            changes.append(changed)
        changed = deepcopy(REVIEWED_PIN)
        del changed["runtime_files"]["codex-code-mode-host.exe"]
        changes.append(changed)
        for pin in changes:
            with self.subTest(pin=pin):
                self.assertEqual(resource_tools(pin), frozenset())
                self.assertEqual(policy_id(pin), "no_mcp_resources_v1")
                self.assertEqual(len(required_isolation_cases(pin)), 6)
                self.assertEqual(expected_nested_tools(pin), ["clock__curr_time"])
        self.assertEqual(policy_id(REVIEWED_PIN), "empty_mcp_resources_v1")
        self.assertEqual(required_isolation_cases(REVIEWED_PIN)[-2:], ("mcp-resources", "mcp-inherited-server"))

    def test_function_projection_ignores_shared_types_but_preserves_unknown_functions(self):
        declarations = known_function_declaration_prefixes(REVIEWED_PIN, web_search=True)
        self.assertEqual(declared_function_names(['type ContentBlock =', *declarations]),
                         expected_nested_tools(REVIEWED_PIN, web_search=True))
        self.assertIn("unexpected", declared_function_names(["declare const tools: { unexpected(args: {"]))
        with self.assertRaises(ValueError):
            declared_function_names(["declare const tools: { malformed"])
        self.assertEqual(expected_nested_tools(REVIEWED_PIN, dynamic=("get_snapshot",)),
                         sorted(["clock__curr_time", "get_snapshot", *MCP_RESOURCE_TOOLS]))

    def test_empty_status_pages_are_bounded_and_all_pages_are_checked(self):
        pages = iter([{"data": [], "nextCursor": "second"}, {"data": [], "nextCursor": None}])
        calls = []
        def call(method, params):
            calls.append((method, params))
            return next(pages)
        require_no_mcp_servers(call, "thread-fixture")
        self.assertEqual([params["cursor"] for _, params in calls], [None, "second"])
        calls.clear()
        def endless(method, params):
            calls.append(method)
            return {"data": [], "nextCursor": str(len(calls))}
        with self.assertRaisesRegex(RuntimeError, "page limit"):
            require_no_mcp_servers(endless, "thread-fixture")
        self.assertEqual(len(calls), MAX_MCP_STATUS_PAGES)
        calls.clear()
        def loop(method, params):
            calls.append(method)
            return {"data": [], "nextCursor": "same"}
        with self.assertRaisesRegex(RuntimeError, "cursor"):
            require_no_mcp_servers(loop, "thread-fixture")
        self.assertEqual(len(calls), 2)

    def test_malformed_or_inherited_servers_reject_before_question_turn(self):
        bad = [None, [], {}, {"data": None}, {"data": {}}, {"data": ""},
               {"data": [{"name": "disabled-or-unready-server"}]}, {"data": [None]}]
        bad.extend({"data": [], "nextCursor": value} for value in (False, 0, [], "", " ", "x" * 257))
        for page in bad:
            with self.subTest(page=page):
                calls = []
                def call(method, params, **_):
                    calls.append(method)
                    return {"thread": {"id": "fixture"}} if method == "thread/start" else page
                provider = CodexProvider(SimpleNamespace(call=call, model="gpt-6-sol", effort="medium"), Path("synthetic"))
                with self.assertRaises(RuntimeError):
                    thread = provider.open("synthetic", [])
                    provider.start(thread, "never sent", {}, False, None)
                self.assertEqual(calls, ["thread/start", "mcpServerStatus/list"])

    def test_synthetic_prove_uses_the_same_guard_before_turn(self):
        client = companion.Companion.__new__(companion.Companion)
        client.pin = deepcopy(REVIEWED_PIN)
        client.preflight = lambda: {}
        calls = []
        def call(method, params=None, **_):
            calls.append(method)
            return {"thread": {"id": "fixture"}} if method == "thread/start" else {"data": None}
        client.call = call
        with tempfile.TemporaryDirectory() as directory, patch.object(companion, "PROFILE", Path(directory)):
            with self.assertRaisesRegex(RuntimeError, "Malformed MCP"):
                client.prove(force=True, save=False)
        self.assertEqual(calls, ["thread/start", "mcpServerStatus/list"])

    def test_reusing_a_thread_rechecks_status_at_each_turn_boundary(self):
        calls = []
        inventories = iter([{"data": []}, {"data": []}, {"data": [{"name": "new-server"}]}])
        def call(method, params, **_):
            calls.append(method)
            if method == "thread/start":
                return {"thread": {"id": "fixture"}}
            if method == "mcpServerStatus/list":
                return next(inventories)
            if method == "turn/start":
                return {"turn": {"id": "fixture-turn"}}
            self.fail("Unexpected RPC")
        provider = CodexProvider(SimpleNamespace(call=call, model="gpt-6-sol", effort="medium"), Path("synthetic"))
        thread = provider.open("synthetic", [])
        provider.start(thread, "synthetic first turn", {}, False, None)
        with self.assertRaisesRegex(RuntimeError, "inherited MCP"):
            provider.start(thread, "second turn must not be sent", {}, False, None)
        self.assertEqual(calls, ["thread/start", "mcpServerStatus/list", "mcpServerStatus/list",
                                 "turn/start", "mcpServerStatus/list"])

    def make_evidence(self, root, *, legacy=False):
        pin = deepcopy(REVIEWED_PIN)
        if legacy:
            pin.update(codex_version="codex-cli 0.160.0", binary_sha256="legacy-main", catalog_sha256="legacy-catalog",
                       runtime_files={"codex.exe": "legacy-main", "codex-code-mode-host.exe": "legacy-host"})
        catalog = root / "catalog.json"
        catalog.write_text("{}", encoding="utf-8")
        (root / "runtime-pin.json").write_text(json.dumps(pin), encoding="utf-8")
        cases = []
        for name in required_isolation_cases(pin):
            report = {"passed": True, "policy_id": policy_id(pin), "checks": {"fixture_completed": True}}
            if name == "mcp-resources":
                report["checks"] = dict(RESOURCE_CHECKS)
            if name == "mcp-inherited-server":
                report.update(guard_rejected=True, rpc_methods=["mcpServerStatus/list"], turn_start_calls=0,
                    model_requests=0, scope="runtime_disabled_loopback_mcp_server_guard; rejected before turn/start",
                    mcp_fixture_requests=[],
                    mcp_server_inventory_before_turn={"data": [{"name": "compai_inherited_synthetic"}], "nextCursor": None})
                report["checks"] = {name: True for name in ("guard_rejected", "configured_server_observed",
                    "guard_checked_actual_runtime", "no_turn_start", "no_model_requests", "disabled_server_not_contacted",
                    "outside_fixture_unchanged", "runtime_pin_verified", "no_unexpected_error")}
            path = root / f"verified-{name}.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            cases.append({"name": name, "file": path.name, "sha256": digest(path), "passed": True})
        validation = {**pin, "schema_version": 1 if legacy else 2, "policy_id": policy_id(pin),
                      "all_passed": True, "cases": cases}
        (root / "validation-success.json").write_text(json.dumps(validation), encoding="utf-8")
        return pin, catalog, validation

    def check_fixture(self, root, pin, catalog):
        # Synthetic evidence tests only: no executable bytes or provider process.
        with patch.object(companion, "CATALOG", catalog), patch.object(companion, "validate_runtime"), \
                patch.object(companion, "digest", side_effect=lambda path: pin["catalog_sha256"] if path == catalog else digest(path)):
            return companion.checked_pin(root / "codex.exe")

    def test_legacy_six_case_evidence_remains_accepted_and_new_policy_needs_eight(self):
        for legacy in (True, False):
            with self.subTest(legacy=legacy), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                pin, catalog, validation = self.make_evidence(root, legacy=legacy)
                self.assertEqual(self.check_fixture(root, pin, catalog), pin)
                if not legacy:
                    validation["schema_version"] = 1
                    validation["cases"] = validation["cases"][:6]
                    (root / "validation-success.json").write_text(json.dumps(validation), encoding="utf-8")
                    with self.assertRaisesRegex(RuntimeError, "requires.*policy evidence"):
                        self.check_fixture(root, pin, catalog)

    def test_new_negative_evidence_cannot_be_omitted_or_only_claimed_passed(self):
        mutations = [("mcp-inherited-server", "guard_rejected", False),
                     ("mcp-inherited-server", "model_requests", 1),
                     ("mcp-inherited-server", "turn_start_calls", False),
                     ("mcp-inherited-server", "mcp_server_inventory_before_turn", {"data": []}),
                     ("mcp-inherited-server", "scope", "deterministic fixture"),
                     ("mcp-resources", "checks", {"generic_pass": True})]
        for name, key, value in mutations:
            with self.subTest(name=name, key=key), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                pin, catalog, validation = self.make_evidence(root)
                case = next(case for case in validation["cases"] if case["name"] == name)
                path = root / case["file"]
                report = json.loads(path.read_text())
                report[key] = value
                path.write_text(json.dumps(report), encoding="utf-8")
                case["sha256"] = digest(path)
                (root / "validation-success.json").write_text(json.dumps(validation), encoding="utf-8")
                with self.assertRaises(RuntimeError):
                    self.check_fixture(root, pin, catalog)

    def test_web_evidence_is_exact_and_old_schema_cannot_enable_new_policy(self):
        required = {name: True for name in ("turn_completed", "known_model", "loopback_response_used",
            "outside_fixture_unchanged", "no_direct_provider_tools", "only_expected_wrappers", "only_requested_model",
            "hosted_web_declared", "patch_not_declared", "no_mcp_servers", "shell_not_declared", "no_other_nested_tools",
            "mcp_servers_empty_before_turn", "mcp_servers_empty_after_turn", "code_host_inventory",
            "no_node_or_fetch_globals", "blocked_in_code_host")}
        for legacy in (True, False):
            with self.subTest(legacy=legacy), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                pin, catalog, _ = self.make_evidence(root, legacy=legacy)
                names = expected_nested_tools(pin, web_search=True)
                report = {**pin, "schema_version": 1 if legacy else 2, "policy_id": policy_id(pin),
                          "all_passed": True, "cases": []}
                for name in ("text", "inspect", "deny-command", "deny-patch"):
                    report["cases"].append({"name": name, "checks": dict(required),
                        "nested_tool_declarations": known_function_declaration_prefixes(pin, web_search=True),
                        "nested_tool_names": names,
                        "code_host": [{"tools": names, "require": "undefined", "process": "undefined", "fetch": "undefined"}]
                                     if name == "inspect" else []})
                path = root / "web-search-validation.json"
                path.write_text(json.dumps(report), encoding="utf-8")
                with patch.object(companion, "CATALOG", catalog):
                    companion.checked_web_pin(pin)
                    changes = []
                    changed = deepcopy(report)
                    changed["cases"][0]["nested_tool_declarations"].append("declare const tools: { unexpected(args: {")
                    changes.append(changed)
                    if not legacy:
                        changed = deepcopy(report)
                        changed["schema_version"] = 1
                        changes.append(changed)
                        changed = deepcopy(report)
                        del changed["cases"][0]["checks"]["mcp_servers_empty_after_turn"]
                        changes.append(changed)
                        changed = deepcopy(report)
                        changed["cases"][1]["code_host"][0]["tools"] = ["clock__curr_time", "web__run"]
                        changes.append(changed)
                    for changed in changes:
                        path.write_text(json.dumps(changed), encoding="utf-8")
                        with self.assertRaises(RuntimeError):
                            companion.checked_web_pin(pin)


if __name__ == "__main__":
    unittest.main()
