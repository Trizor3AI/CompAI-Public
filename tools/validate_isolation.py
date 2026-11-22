"""Validate an already prepared runtime/catalog pin using local synthetic probes only.

Run prepare_isolation_catalog.py first, after preserving the previous evidence.
Never signs in or invokes a real model provider. Writes validation-success.json
only after every case required by the exact runtime policy passes.
"""
from datetime import datetime, timezone
import json
from pathlib import Path
import subprocess
import sys

from prepare_isolation_catalog import digest
from runtime_bundle import resolve_runtime, validate_runtime
from codex_tool_policy import policy_id, required_isolation_cases, resource_tools

ROOT = Path(__file__).resolve().parents[1]
BRIDGE = ROOT / "bridge"
OUTPUT = BRIDGE / ".isolation-probe"
CASES = (
    ("shell-control", ["--strict", "--control-shell"]),
    ("text", []),
    ("snapshot", ["--dynamic-snapshot", "--code-call", "get_snapshot"]),
    ("deny-command", ["--dynamic-snapshot", "--code-call", "exec_command"]),
    ("deny-patch", ["--dynamic-snapshot", "--code-call", "apply_patch"]),
    ("inspect", ["--dynamic-snapshot", "--code-call", "inspect"]),
    ("mcp-resources", ["--dynamic-snapshot", "--code-call", "mcp_resources"]),
    ("mcp-inherited-server", ["--inherited-server"]),
)


def main():
    executable = resolve_runtime(BRIDGE)
    catalog = OUTPUT / "no-patch-models.json"
    pin_path = OUTPUT / "runtime-pin.json"
    pin = json.loads(pin_path.read_text(encoding="utf-8"))
    validate_runtime(executable, pin)
    if digest(executable) != pin["binary_sha256"] or digest(catalog) != pin["catalog_sha256"]:
        raise RuntimeError("Prepare the catalog for the installed runtime before validating")
    success_path = OUTPUT / "validation-success.json"
    if success_path.exists():
        raise RuntimeError("Preserve the previous candidate validation-success.json before a new validation")
    selected_policy = policy_id(pin)
    required = required_isolation_cases(pin)
    flags_by_name = dict(CASES)
    if len(set(required)) != len(required) or not set(required).issubset(flags_by_name):
        raise RuntimeError("Unexpected isolation case policy")
    run_root = OUTPUT / "runs" / datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ")
    run_root.mkdir(parents=True, exist_ok=False)
    evidence = []
    for name in required:
        flags = flags_by_name[name]
        arguments = flags if name == "shell-control" else ["--strict", "--catalog", str(catalog), *flags]
        arguments = [*arguments, "--scratch-root", str(run_root / name)]
        result = subprocess.run([sys.executable, "-I", "-S", "-B", str(ROOT / "tools/run.py"), "isolation_mock_probe", *arguments],
                                cwd=ROOT, capture_output=True, text=True, encoding="utf-8", timeout=90)
        try:
            report = json.loads(result.stdout)
        except ValueError:
            raise RuntimeError(f"{name} did not produce a valid local report: {result.stderr[-2000:]}") from None
        if (result.returncode or report.get("passed") is not True or report.get("policy_id") != selected_policy
                or not report.get("checks") or not all(value is True for value in report["checks"].values())):
            (OUTPUT / f"failed-{name}.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
            raise RuntimeError(f"Local isolation case failed: {name}; inspect failed-{name}.json")
        evidence.append((name, report))
        print(f"PASS {name}", flush=True)
    if (digest(executable) != pin["binary_sha256"] or digest(catalog) != pin["catalog_sha256"] or
            json.loads(pin_path.read_text(encoding="utf-8")) != pin):
        raise RuntimeError("Runtime/catalog changed during validation; do not publish this evidence")
    validate_runtime(executable, pin)
    cases = []
    for name, report in evidence:
        path = OUTPUT / f"verified-{name}.json"
        path.write_text(json.dumps(report, indent=2), encoding="utf-8")
        cases.append({"name": name, "file": path.name, "sha256": digest(path), "passed": True})
    validation = {"schema_version": 1, "validated_at_utc": datetime.now(timezone.utc).isoformat(),
                  "route": "loopback mock only; no provider or subscription request",
                  **{key: pin[key] for key in ("codex_version", "binary_sha256", "catalog_sha256", "model")},
                  "runtime_files": pin["runtime_files"],
                  "cases": cases, "all_passed": True}
    if resource_tools(pin):
        validation.update(schema_version=2, policy_id=selected_policy)
    with success_path.open("x", encoding="utf-8") as stream:
        json.dump(validation, stream, indent=2)
        stream.write("\n")
    print(f"Validated {len(cases)} local cases for {pin['codex_version']}; subscription route not retested.")


if __name__ == "__main__":
    main()
