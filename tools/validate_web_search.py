"""Validate isolated hosted-search registration against a loopback provider only.

This does not test ChatGPT entitlement, live search results, citations, or any
external destination. It never reads credentials or sends a provider request.
"""
from datetime import datetime, timezone
import json
from pathlib import Path
import subprocess
import sys

from companion import checked_pin, find_runtime
from prepare_isolation_catalog import digest
from codex_tool_policy import (declared_function_names, expected_nested_tools,
    known_function_declaration_prefixes, policy_id, resource_tools)


ROOT = Path(__file__).resolve().parents[1]
EVIDENCE = ROOT / "bridge/.isolation-probe"
CASES = (
    ("text", []),
    ("inspect", ["--code-call", "inspect"]),
    ("deny-command", ["--code-call", "exec_command"]),
    ("deny-patch", ["--code-call", "apply_patch"]),
)


def main():
    executable = find_runtime()
    pin = checked_pin(executable)
    resource_policy = bool(resource_tools(pin))
    catalog = EVIDENCE / "no-patch-models.json"
    run_root = EVIDENCE / "web-runs" / datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ")
    run_root.mkdir(parents=True, exist_ok=False)
    reports = []
    for name, flags in CASES:
        case_root = run_root / name
        case_root.mkdir(exist_ok=False)
        command = [sys.executable, "-I", "-S", "-B", str(ROOT / "tools/run.py"), "isolation_mock_probe",
                   "--strict", "--catalog", str(catalog), "--web-search",
                   "--scratch-root", str(case_root / "scratch"), *flags]
        try:
            completed = subprocess.run(command, cwd=ROOT, capture_output=True,
                                       text=True, encoding="utf-8", timeout=90)
        except subprocess.TimeoutExpired as error:
            for filename, value in (("stdout.txt", error.stdout), ("stderr.txt", error.stderr)):
                (case_root / filename).write_bytes(value if isinstance(value, bytes) else (value or "").encode("utf-8"))
            (case_root / "failure.json").write_text(json.dumps({"case": name, "passed": False,
                "error": "child timeout", "timeout_seconds": 90}, indent=2), encoding="utf-8")
            raise RuntimeError(f"Hosted-search local case timed out: {name}; inspect {case_root}") from error
        (case_root / "stdout.txt").write_text(completed.stdout, encoding="utf-8")
        (case_root / "stderr.txt").write_text(completed.stderr, encoding="utf-8")
        try:
            report = json.loads(completed.stdout)
        except ValueError:
            (case_root / "failure.json").write_text(json.dumps({"case": name, "passed": False,
                "error": "invalid child JSON", "exit_code": completed.returncode}, indent=2), encoding="utf-8")
            raise RuntimeError(f"{name} produced no valid loopback report; inspect {case_root}") from None
        # Preserve the exact returned JSON before any success/policy check.
        (case_root / "report.json").write_text(completed.stdout, encoding="utf-8")
        if completed.returncode or not report.get("passed") or not report.get("runtime_pin_verified"):
            raise RuntimeError(f"Hosted-search local case failed: {name}; inspect {case_root / 'report.json'}")
        declarations = sorted(line for line in report["requests"][0]["prompt_tool_declarations"]
                              if line.startswith("declare const tools: {"))
        expected_names = expected_nested_tools(pin, web_search=True)
        if (declarations != known_function_declaration_prefixes(pin, web_search=True)
                or declared_function_names(declarations) != expected_names):
            raise RuntimeError(f"Hosted-search declarations differ from exact policy: {name}")
        if resource_policy and (report.get("policy_id") != policy_id(pin)
                or report.get("mcp_servers_empty") is not True
                or report["checks"].get("mcp_servers_empty_before_turn") is not True
                or report["checks"].get("mcp_servers_empty_after_turn") is not True):
            raise RuntimeError(f"Hosted-search MCP zero-server evidence missing: {name}")
        if name == "inspect" and report["code_host"] != [{
                "tools": expected_names, "require": "undefined", "process": "undefined", "fetch": "undefined"}]:
            raise RuntimeError("Hosted-search runtime inventory differs from exact policy")
        reports.append({"name": name, "checks": report["checks"],
                        "nested_tool_declarations": declarations,
                        "nested_tool_names": expected_names,
                        "code_host": report["code_host"]})
        print(f"PASS web-{name}", flush=True)
    if digest(executable) != pin["binary_sha256"] or digest(catalog) != pin["catalog_sha256"]:
        raise RuntimeError("Runtime/catalog changed during web validation")
    if checked_pin(executable) != pin:
        raise RuntimeError("Private runtime manifest changed during web validation")
    path = EVIDENCE / "web-search-validation.json"
    if path.exists():
        raise RuntimeError("Web-search evidence already exists; preserve it before another run")
    path.write_text(json.dumps({
        "schema_version": 2 if resource_policy else 1,
        "policy_id": policy_id(pin),
        "validated_at_utc": datetime.now(timezone.utc).isoformat(),
        "route": "loopback custom provider only; no external/provider request",
        "codex_version": pin["codex_version"],
        "binary_sha256": pin["binary_sha256"],
        "runtime_files": pin["runtime_files"],
        "catalog_sha256": pin["catalog_sha256"],
        "model": pin["model"],
        "cases": reports,
        "all_passed": True,
        "limitations": ["Native ChatGPT hosted-search entitlement untested",
                        "Live search destination, result handling and citation quality untested"],
    }, indent=2), encoding="utf-8")
    print(f"Wrote {path}")


if __name__ == "__main__":
    main()
