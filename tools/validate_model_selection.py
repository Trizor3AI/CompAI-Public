"""Validate visible model/effort selections using the pinned runtime and loopback only.

No login, credentials, game data, web lookup or provider request is used. The
selection-evidence guard alone is bootstrapped from the pinned visible catalog;
baseline and hosted-search isolation guards remain enforced throughout.
"""
from datetime import datetime, timezone
import argparse
import json
from pathlib import Path
from unittest.mock import patch

import companion
from model_selection import validate_model_evidence
from prepare_isolation_catalog import digest
from quest_lookup_runtime_check import run_case
from codex_tool_policy import policy_id, resource_tools


ROOT = Path(__file__).resolve().parents[1]
REPORT = ROOT / "bridge/.isolation-probe" / "model-selection-validation.json"


def candidates():
    catalog = json.loads(companion.CATALOG.read_text(encoding="utf-8"))
    selected = {}
    for model in catalog["models"]:
        if model.get("visibility") != "list":
            continue
        ident = model.get("slug")
        efforts = [row["effort"] for row in model.get("supported_reasoning_levels", [])]
        if (not isinstance(ident, str) or ident in selected or not efforts or
                len(efforts) != len(set(efforts)) or "medium" not in efforts or
                model.get("apply_patch_tool_type", "missing") is not None):
            raise RuntimeError("Unexpected visible model metadata; inspect before validating")
        selected[ident] = efforts
    if "gpt-6-sol" not in selected:
        raise RuntimeError("Pinned baseline model missing from visible candidates")
    return selected


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--resume", action="store_true", help="Reuse complete passing models from the failed matrix report")
    args = parser.parse_args()
    if REPORT.exists():
        raise RuntimeError("Preserve existing model-selection evidence before another run")
    executable = companion.find_runtime()
    pin = companion.checked_pin(executable)
    companion.checked_web_pin(pin)
    selected = candidates()
    resource_policy = bool(resource_tools(pin))
    clock_support = ({row["slug"]: "clock" in row.get("experimental_supported_tools", [])
                      for row in json.loads(companion.CATALOG.read_text(encoding="utf-8"))["models"]}
                     if resource_policy else {})
    models = []
    excluded_models = []
    failure = REPORT.with_name("model-selection-validation-failed.json")
    previous_report = json.loads(failure.read_text(encoding="utf-8")) if args.resume else None
    if resource_policy and previous_report is not None and (
            previous_report.get("schema_version") != 2 or previous_report.get("policy_id") != policy_id(pin)
            or any(previous_report.get(key) != pin[key] for key in ("binary_sha256", "catalog_sha256", "runtime_files"))):
        raise RuntimeError("Prior matrix evidence uses a different runtime/tool policy")
    completed = previous_report["models"] if previous_report is not None else []
    for previous in completed:
        ident = previous["id"]
        if ident not in selected or ident in {model["id"] for model in models}:
            raise RuntimeError("Unknown or duplicate model in prior matrix evidence")
        validate_model_evidence(previous, pin, selected[ident], catalog_has_clock=clock_support.get(ident))
        models.append(previous)
    for ident, efforts in selected.items():
        if ident in {model["id"] for model in models}:
            print(f"REUSE {ident}: completed cases unchanged", flush=True)
            continue
        cases = []
        excluded = []
        # Every offered effort is transported through an actual app-server turn,
        # and exercises the real code host's allowlist, denied shell/patch calls,
        # paginated quest lookup, an unmentioned ID lookup, and hosted web tools.
        planned = [("enabled-web", effort) for effort in efforts]
        # Web-off and disabled quest registration are model-dependent too.
        planned += [("enabled", "medium"), ("disabled", "medium")]
        for mode, effort in planned:
            with patch.object(companion, "checked_model_selection", return_value=selected):
                case = run_case(mode, ident, effort)
            if not case["passed"]:
                # These exact failures identify the unsupported direct-tool route
                # before and after deferred discovery. Neither route is selectable.
                if ident == "gpt-5.5" and case.get("error") in (
                        "Unexpected directly registered provider tools: ['query_quests', 'request_user_input', 'web_search']",
                        "Unexpected directly registered provider tools: ['discover_tools', 'invoke_tool', 'request_user_input', 'web_search']"):
                    excluded_models.append({"id": ident, "reason": "No validated code-mode tool isolation path",
                                            "case": case})
                    break
                if effort == "ultra" and case.get("error") in (
                        "Unexpected provider reasoning effort: ['xhigh']",
                        "Unexpected provider reasoning effort: ['max']"):
                    excluded.append({"effort": effort, "reason": case["error"],
                                     "case": case})
                    continue
                failure.write_text(json.dumps({"schema_version": 2 if resource_policy else 1,
                    "policy_id": policy_id(pin),
                    **{key: pin[key] for key in ("binary_sha256", "catalog_sha256", "runtime_files")},
                    "all_passed": False, "models": models,
                    "failed_case": case}, indent=2) + "\n", encoding="utf-8")
                raise RuntimeError(f"{ident}/{effort}/{mode}: {case.get('error', 'failed')}")
            cases.append(case)
        if ident in {model["id"] for model in excluded_models}:
            print(f"EXCLUDE {ident}: no validated code-mode tool isolation path", flush=True)
            continue
        allowed = [effort for effort in efforts if effort not in {row["effort"] for row in excluded}]
        models.append({"id": ident, "efforts": allowed, "passed": True, "cases": cases,
                       "excluded_efforts": excluded})
        print(f"PASS {ident}: {len(allowed)} exact efforts, {len(cases)} runtime cases" +
              ("; Ultra excluded (runtime remaps effort)" if excluded else ""), flush=True)
    if (companion.checked_pin(executable) != pin or
            digest(companion.CATALOG) != pin["catalog_sha256"] or candidates() != selected):
        raise RuntimeError("Runtime/catalog changed during validation; report not published")
    companion.checked_web_pin(pin)
    for model in models:
        validate_model_evidence(model, pin, selected[model["id"]], catalog_has_clock=clock_support.get(model["id"]))
    report = {"schema_version": 2 if resource_policy else 1, "policy_id": policy_id(pin), "all_passed": True,
              "validated_at_utc": datetime.now(timezone.utc).isoformat(),
              "route": "loopback Responses only; no credentials, game data, provider or web requests",
              **{key: pin[key] for key in ("binary_sha256", "catalog_sha256", "runtime_files")},
              "models": models,
              "excluded_models": excluded_models,
              "limitations": ["all_passed covers only models and efforts listed in the allowed models array",
                              "Excluded models and remapped efforts are not selectable",
                              "Provider availability and account entitlement are not tested offline",
                              "No live answer quality or live search quality claim"]}
    REPORT.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(f"Validated {len(models)} visible models; wrote {REPORT}", flush=True)


if __name__ == "__main__":
    main()
