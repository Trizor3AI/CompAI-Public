"""Bound model choices to locally validated isolation evidence and runtime discovery."""
import json
import re

from prepare_isolation_catalog import digest
from codex_tool_policy import expected_nested_tools, policy_id, resource_tools

DEFAULT_MODEL = "gpt-6-sol"
DEFAULT_EFFORT = "medium"
EFFORTS = ("none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra")


def validate_selection(model, effort):
    if (not isinstance(model, str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,99}", model)
            or not isinstance(effort, str) or effort not in EFFORTS):
        raise RuntimeError("Invalid model or reasoning choice")
    return model, effort


def validate_model_evidence(row, pin, catalog_efforts, *, catalog_has_clock=None):
    """Check that offered pairs are exactly the passing cases, including on resume."""
    try:
        model, offered = row["id"], row["efforts"]
        resource_policy = bool(resource_tools(pin))
        if resource_policy and type(catalog_has_clock) is not bool:
            raise ValueError()
        passing, excluded = row["cases"], row["excluded_efforts"]
        if (row.get("passed") is not True or not isinstance(offered, list) or not offered
                or len(offered) != len(set(offered)) or not isinstance(passing, list)
                or not isinstance(excluded, list)):
            raise ValueError()
        catalog_efforts = list(catalog_efforts)
        if not catalog_efforts or len(catalog_efforts) != len(set(catalog_efforts)):
            raise ValueError()
        for effort in catalog_efforts:
            validate_selection(model, effort)
        expected = {("enabled-web", effort) for effort in catalog_efforts} | {
            ("enabled", "medium"), ("disabled", "medium")}
        seen, successful, rejected = set(), set(), set()

        def inspect(case):
            identity = (case["case"], case["effort"])
            if (case["model"] != model or identity not in expected or identity in seen
                    or any(case["runtime"].get(key) != pin[key] for key in
                           ("binary_sha256", "catalog_sha256", "runtime_files"))):
                raise ValueError()
            if resource_policy and (case.get("policy_id") != policy_id(pin)
                                    or case.get("clock_tool_expected") is not catalog_has_clock):
                raise ValueError()
            seen.add(identity)
            return identity

        for case in passing:
            mode, effort = inspect(case)
            if (case.get("passed") is not True or case.get("unexpected_wrappers") is not False
                    or any(case.get(key) is not True for key in (
                        "initial_summary_only", "credential_free", "exact_model_and_effort", "provider_fallback_disabled"))
                    or type(case.get("model_requests")) is not int or case["model_requests"] < 1):
                raise ValueError()
            if resource_policy:
                expected_tools = expected_nested_tools(pin,
                    dynamic=("discover_tools", "invoke_tool") if mode != "disabled" else (),
                    web_search=mode == "enabled-web")
                if not catalog_has_clock:
                    expected_tools.remove("clock__curr_time")
                if (case.get("mcp_servers_empty") is not True or case.get("nested_tools") != expected_tools
                        or case.get("shell_and_patch_denied") is not True):
                    raise ValueError()
            if mode == "disabled":
                if (case.get("disabled_tool_absent") is not True or case.get("injected_tool_denied") is not True
                        or type(case.get("disabled_callback_calls")) is not int or case["disabled_callback_calls"] != 0):
                    raise ValueError()
            else:
                tools = case["nested_tools"]
                if not isinstance(tools, list) or len(tools) != len(set(tools)):
                    raise ValueError()
                tool_set = set(tools) - {"clock__curr_time"} - set(resource_tools(pin))
                web_tools = {"web__run"} if mode == "enabled-web" else set()
                legacy_tools = {"query_quests"} | web_tools
                deferred_tools = {"discover_tools", "invoke_tool"} | web_tools
                if (tool_set not in (legacy_tools, deferred_tools)
                        or (tool_set == deferred_tools and case.get("discovery_metadata_intact") is not True)
                        or case.get("lookup_calls") != 3 or case.get("completion_states_checked") != [False, True, None]
                        or case.get("web_completion_simulated") is not (mode == "enabled-web")
                        or any(case.get(key) is not True for key in (
                            "pagination_checked", "unmentioned_id_lookup_checked", "shell_and_patch_denied"))):
                    raise ValueError()
                if mode == "enabled-web":
                    successful.add(effort)
        for exclusion in excluded:
            case = exclusion["case"]
            mode, effort = inspect(case)
            if (mode != "enabled-web" or effort != "ultra" or exclusion["effort"] != effort
                    or case.get("passed") is not False or case.get("error") not in (
                        "Unexpected provider reasoning effort: ['xhigh']",
                        "Unexpected provider reasoning effort: ['max']")):
                raise ValueError()
            rejected.add(effort)
        if (seen != expected or set(offered) != successful or successful & rejected
                or successful | rejected != set(catalog_efforts)):
            raise ValueError()
        return offered
    except (ValueError, KeyError, TypeError, AttributeError, RuntimeError):
        raise RuntimeError("Model selection cases do not establish exactly the offered model/reasoning pairs") from None


def checked_model_selection(pin, catalog, report_path=None):
    """The report is offline tool-isolation evidence, not account entitlement evidence."""
    try:
        report = json.loads((report_path or catalog.parent / "model-selection-validation.json").read_text(encoding="utf-8"))
        resource_policy = bool(resource_tools(pin))
        if (report.get("schema_version") != (2 if resource_policy else 1)
                or report.get("all_passed") is not True
                or (resource_policy and report.get("policy_id") != policy_id(pin))
                or (not resource_policy and report.get("policy_id", policy_id(pin)) != policy_id(pin))):
            raise ValueError()
        if any(report.get(key) != pin[key] for key in ("binary_sha256", "catalog_sha256", "runtime_files")):
            raise ValueError()
        if digest(catalog) != pin["catalog_sha256"]:
            raise ValueError()
        catalog_rows = json.loads(catalog.read_text(encoding="utf-8"))["models"]
        known = {}
        for row in catalog_rows:
            slug = row["slug"]
            if slug in known:
                raise ValueError()
            known[slug] = row
        rows = report["models"]
        if not isinstance(rows, list) or not 1 <= len(rows) <= 100:
            raise ValueError()
        permitted = {}
        for row in rows:
            slug, efforts = row["id"], row["efforts"]
            entry = known[slug]
            if (slug in permitted or row.get("passed") is not True or entry.get("visibility") != "list"
                    or "apply_patch_tool_type" not in entry or entry["apply_patch_tool_type"] is not None
                    or not isinstance(efforts, list) or not efforts or len(efforts) != len(set(efforts))):
                raise ValueError()
            known_efforts = {value["effort"] for value in entry["supported_reasoning_levels"]}
            for effort in efforts:
                validate_selection(slug, effort)
                if effort not in known_efforts:
                    raise ValueError()
            validate_model_evidence(row, pin, known_efforts,
                catalog_has_clock="clock" in entry.get("experimental_supported_tools", []))
            permitted[slug] = efforts
        if DEFAULT_EFFORT not in permitted.get(DEFAULT_MODEL, []):
            raise ValueError()
        return permitted
    except (OSError, ValueError, KeyError, TypeError, AttributeError, RuntimeError):
        raise RuntimeError("Model selection isolation evidence is missing or changed; revalidate before choosing models") from None


def require_permitted(model, effort, permitted):
    validate_selection(model, effort)
    if effort not in permitted.get(model, []):
        raise RuntimeError("Selected model/reasoning has no validated isolation evidence; no fallback")


def discover_models(call, permitted):
    """Intersect visible runtime models with locally validated model/effort pairs."""
    cursor, cursors, found = None, set(), {}
    for _ in range(10):
        params = {"includeHidden": False, "limit": 100}
        if cursor is not None:
            params["cursor"] = cursor
        page = call("model/list", params)
        if not isinstance(page, dict) or not isinstance(page.get("data"), list) or len(page["data"]) > 100:
            raise RuntimeError("Invalid model discovery response")
        for row in page["data"]:
            if not isinstance(row, dict):
                raise RuntimeError("Invalid model discovery row")
            model = row.get("model")
            if not isinstance(model, str) or model not in permitted or row.get("hidden") is True:
                continue
            if model in found:
                raise RuntimeError("Duplicate model discovery row")
            supported = row.get("supportedReasoningEfforts")
            if not isinstance(supported, list):
                continue
            advertised = {choice.get("reasoningEffort") for choice in supported
                          if isinstance(choice, dict) and isinstance(choice.get("reasoningEffort"), str)}
            efforts = [effort for effort in EFFORTS if effort in permitted[model] and effort in advertised]
            if not efforts:
                continue
            name = row.get("displayName")
            if not isinstance(name, str) or not name.strip() or any(ord(c) < 32 for c in name):
                name = model
            default = row.get("defaultReasoningEffort")
            if default not in efforts:
                default = "medium" if "medium" in efforts else efforts[0]
            found[model] = {"id": model, "name": name[:100], "efforts": efforts, "default_effort": default}
            # Missing capability is unknown, never permission to silently drop images.
            modalities = row.get("inputModalities")
            if isinstance(modalities, list) and "image" in modalities:
                found[model]["supports_images"] = True
        cursor = page.get("nextCursor")
        if cursor is None or cursor == "":
            return list(found.values())
        if not isinstance(cursor, str) or len(cursor) > 1024 or cursor in cursors:
            raise RuntimeError("Invalid model discovery cursor")
        cursors.add(cursor)
    raise RuntimeError("Model discovery exceeded the page limit")


def require_available(model, effort, models):
    if not any(row["id"] == model and effort in row["efforts"] for row in models):
        raise RuntimeError("Selected model/reasoning is unavailable in this runtime; no fallback")
