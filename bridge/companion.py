"""Isolated subscription companion proof. No game data or API-key fallback.

status: supported account/quota checks only. login: official browser sign-in.
prove: one synthetic model turn, after runtime/configuration/account/quota checks.
"""
import argparse
import json
from chat_attachments import ATTACHMENT_INSTRUCTIONS
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time
from datetime import datetime, timezone
from urllib.parse import urlparse
import webbrowser

from prepare_isolation_catalog import digest
from runtime_bundle import resolve_runtime, validate_runtime
from codex_tool_policy import (resource_tools, policy_id, expected_nested_tools,
    required_isolation_cases, require_no_mcp_servers, known_function_declaration_prefixes,
    declared_function_names)
from model_selection import (DEFAULT_MODEL, DEFAULT_EFFORT, checked_model_selection,
    validate_selection, require_permitted)
from inventory_question import (DATA_RULE, INSTRUCTIONS, WEB_INSTRUCTIONS, MAX_REQUEST_BYTES,
    source_context, spoiler_instructions)
from personality import DATA_RULE as PERSONALITY_DATA_RULE, INSTRUCTIONS as PERSONALITY_INSTRUCTIONS
from companion_metrics import TurnMetrics, measured, timed
from companion_process import ProcessContainmentError, start_owned_process, close_owned_process
from companion_request import normalize_request, recheck_request
from companion_tools import build_registry, READER_NAMES
from deferred_tools import DeferredTools, DeferredToolsError
from companion_provider import (CodexProvider, QuestionProvider, CompanionFailure,
    provider_failure_kind, quota_windows, ERROR_STAGES, RuntimeInbox, MAX_RUNTIME_FRAME_BYTES)
from quest_context import QUEST_INSTRUCTIONS
from quest_lookup import (MAX_CALLS as MAX_LOOKUP_CALLS, MAX_TOTAL_BYTES as MAX_LOOKUP_TOTAL_BYTES,
    QuestQueryBridge, LookupTransportTimeout, encoded_size as lookup_result_size)
from chat_history_lookup import NOTES_INSTRUCTIONS
from collection_lookup import SOURCES as COLLECTION_SOURCES, permitted_categories as permitted_collection_categories
from progression_lookup import (SOURCES as PROGRESSION_SOURCES, COUNT_FIELDS as PROGRESSION_COUNTS,
                                ROW_FIELDS as PROGRESSION_ROW_FIELDS, permitted_categories as permitted_progression_categories)
from crafting_log_lookup import (SCOPE as CRAFTING_LOG_SCOPE, ROW_FIELDS as CRAFTING_LOG_ROW_FIELDS,
                                 IDENTITY_FIELDS as CRAFTING_LOG_IDENTITY_FIELDS, MAX_CACHED_ROWS)
from activity_lookup import model_result as activity_model_result
from game_data_tools import GameDataTools
from streamed_answer import MAX_ENCODED_CHARS as MAX_ANSWER_ENVELOPE_CHARS

ROOT = Path(__file__).resolve().parent
PROFILE = ROOT / ".companion"
MODEL = DEFAULT_MODEL
CATALOG = ROOT / ".isolation-probe" / "no-patch-models.json"
CONFIG = [
    'model_provider="openai"', 'forced_login_method="chatgpt"', 'model="gpt-6-sol"',
    'approval_policy="never"', 'sandbox_mode="read-only"',
    'cli_auth_credentials_store="keyring"', 'check_for_update_on_startup=false',
    'features.shell_tool=false', 'features.unified_exec=false',
    'features.browser_use=false', 'features.browser_use_external=false',
    'features.browser_use_full_cdp_access=false', 'features.auth_elicitation=false',
    'features.remote_plugin=false', 'features.tool_suggest=false', 'features.shell_snapshot=false',
    'features.computer_use=false', 'features.apps=false', 'features.plugins=false',
    'features.multi_agent=false', 'features.image_generation=false',
    'features.view_image=false', 'web_search="disabled"',
    'features.request_permissions_tool=false', 'features.skill_search=false',
    'features.hooks=false', 'features.goals=false', 'features.memories=false',
    'agents.enabled=false', 'features.multi_agent_v2=false',
    'features.unbounded_connection_retries=false',
    'features.enable_request_compression=false', 'analytics.enabled=false', 'feedback.enabled=false',
]
SNAPSHOT = {"synthetic": True, "items": [{"name": "TEST TOKEN", "quantity": 3},
                                        {"name": "TEST TOKEN", "quantity": 2}]}
AUDIT_SCRIPT = '''text({tag:"ffxiv_companion_audit_v1",tools:Object.keys(tools).sort(),require:typeof require,process:typeof process,fetch:typeof fetch});
text(await tools.get_snapshot({}));'''
ALLOWED_TOOLS = ["clock__curr_time", "get_snapshot"]
QUESTION_TIMEOUT_SECONDS = 300
HIGH_REASONING_TIMEOUT_SECONDS = 600
MIN_RESPONSE_TIMEOUT_SECONDS = 60
MAX_RESPONSE_TIMEOUT_SECONDS = 1800
def resolve_response_timeout_seconds(value, effort):
    if value is None:
        return HIGH_REASONING_TIMEOUT_SECONDS if effort in ("high", "xhigh", "max", "ultra") else QUESTION_TIMEOUT_SECONDS
    if type(value) is not int or not MIN_RESPONSE_TIMEOUT_SECONDS <= value <= MAX_RESPONSE_TIMEOUT_SECONDS:
        raise RuntimeError("Response timeout must be an integer from 60 through 1800 seconds")
    return value


def response_timeout_argument(value):
    try:
        if not value.isascii() or not value.isdecimal():
            raise ValueError()
        return resolve_response_timeout_seconds(int(value), DEFAULT_EFFORT)
    except (ValueError, RuntimeError):
        raise argparse.ArgumentTypeError("response-timeout-seconds must be an integer from 60 through 1800") from None


def failure_message(exc, stage=None):
    result = _failure_message(exc)
    if stage is not None:
        classified = "provider." + exc.kind if isinstance(exc, CompanionFailure) and exc.kind in ("authentication", "quota") else getattr(exc, "stage", None)
        result["error_stage"] = (classified if classified in ERROR_STAGES else
                                 stage if stage in ERROR_STAGES else "provider.request")
    return result


def _failure_message(exc):
    if isinstance(exc, CompanionFailure):
        return {"error": str(exc), "error_kind": exc.kind}
    if isinstance(exc, TimeoutError):
        return {"error": "Companion response timed out; no automatic retry", "error_kind": "provider"}
    if isinstance(exc, RuntimeError):
        return {"error": str(exc), "error_kind": "protocol"}
    return {"error": f"Companion setup or protocol failed ({type(exc).__name__}); inspect local installation",
            "error_kind": "protocol"}


def parse_question_answer(answer):
    """Separate rejected model output from bridge protocol failures."""
    reason = None
    if isinstance(answer, str) and len(answer) > MAX_ANSWER_ENVELOPE_CHARS:
        reason = "answer envelope exceeded size limit"
    elif answer is None or isinstance(answer, str) and not answer.strip():
        reason = "missing response"
    elif not isinstance(answer, str):
        reason = "invalid answer shape"
    else:
        try:
            parsed = json.loads(answer)
        except (ValueError, RecursionError):
            reason = "invalid JSON"
        else:
            if not isinstance(parsed, dict) or set(parsed) != {"answer"} or not isinstance(parsed["answer"], str):
                reason = "invalid answer shape"
            elif not 1 <= len(parsed["answer"]) <= 6000:
                reason = "answer length outside 1-6000 characters"
            else:
                return parsed
    # Keep only a fixed reason; model output and parser exception details are private.
    raise CompanionFailure("provider", f"Question returned an invalid answer ({reason}); no automatic retry") from None


def unavailable_lookup(label, invalid_arguments=False):
    return {"available": False, "error_kind": "invalid_arguments" if invalid_arguments else "reader",
            "message": (label + " lookup arguments are invalid. Check the tool schema and correct them within the remaining lookup budget."
                        if invalid_arguments else label + " lookup is unavailable. Treat its data as unknown; do not guess.")}


def require_lookup_permission(reader, arguments, summary):
    # These argument validators also enforce per-category access. Separate that
    # denial before offering an otherwise safe schema correction to the model.
    if reader == "crafting_log" and summary != {"scope": CRAFTING_LOG_SCOPE}:
        raise CompanionFailure("permission", "Crafting log is not permitted; question interrupted")
    if not isinstance(arguments, dict):
        return
    if reader == "game_context":
        view = arguments.get("view")
        if isinstance(view, str) and view in ("character", "location", "dialogue") and summary.get(view + "_allowed") is not True:
            raise CompanionFailure("permission", "Game context category is not permitted; question interrupted")
    if reader == "personality_details":
        section = arguments.get("section")
        if isinstance(section, str) and section in ("preferences", "likes_dislikes", "opinions", "background") and section not in summary["sections"]:
            raise CompanionFailure("permission", "Personality section is not permitted; question interrupted")
    if reader == "valuation":
        categories = arguments.get("categories")
        if isinstance(categories, list) and any(isinstance(c, str) and c not in summary["categories"] for c in categories):
            raise CompanionFailure("permission", "Saved-item pricing category is not permitted; question interrupted")
    if reader == "storage":
        category = arguments.get("category")
        if isinstance(category, str) and category in ("crystals", "key_items", "retainers", "saddlebag", "housing", "fc_chest", "armoury") and category not in summary["categories"]:
            raise CompanionFailure("permission", "Storage category is not permitted; question interrupted")
    if reader == "collections":
        category = arguments.get("category")
        if isinstance(category, str) and category in COLLECTION_SOURCES and category not in permitted_collection_categories(summary):
            raise CompanionFailure("permission", "Collection category is not permitted; question interrupted")
    if reader == "menus":
        category = arguments.get("category")
        if isinstance(category, str) and category not in summary["categories"]:
            raise CompanionFailure("permission", "Menu category is not permitted; question interrupted")
    if reader == "activities":
        category = arguments.get("category")
        if isinstance(category, str) and category not in summary["categories"]:
            raise CompanionFailure("permission", "Activity category is not permitted; question interrupted")
    if reader == "progression":
        category = arguments.get("category")
        if isinstance(category, str) and category in PROGRESSION_SOURCES and category not in permitted_progression_categories(summary):
            raise CompanionFailure("permission", "Progression category is not permitted; question interrupted")


def official_login_url(url):
    """Validate the original value before a URL parser can normalize it."""
    if (not isinstance(url, str) or len(url) > 8192 or
            any(ord(character) <= 32 or ord(character) == 127 for character in url) or
            "\\" in url):
        return False
    try:
        parsed = urlparse(url)
        return (parsed.scheme == "https" and
                parsed.hostname in ("auth.openai.com", "chatgpt.com", "auth.chatgpt.com") and
                parsed.username is None and parsed.password is None and
                parsed.port in (None, 443))
    except ValueError:
        return False


def checked_pin(executable):
    pin = json.loads((CATALOG.parent / "runtime-pin.json").read_text(encoding="utf-8"))
    validation = json.loads((CATALOG.parent / "validation-success.json").read_text(encoding="utf-8"))
    if digest(CATALOG) != pin["catalog_sha256"]:
        raise RuntimeError("Runtime/catalog pin changed; revalidate isolation first")
    # The authoritative runtime check hashes both executables exactly once.
    validate_runtime(executable, pin)
    if validation.get("all_passed") is not True or any(validation.get(key) != pin[key] for key in ("binary_sha256", "catalog_sha256", "model", "runtime_files")):
        raise RuntimeError("No passing isolation evidence for this runtime/catalog")
    resource_policy = bool(resource_tools(pin))
    if resource_policy and (validation.get("schema_version") != 2 or validation.get("policy_id") != policy_id(pin)
                            or validation.get("codex_version") != pin["codex_version"]):
        raise RuntimeError("New runtime requires its MCP resource isolation policy evidence")
    cases = validation.get("cases", [])
    expected_cases = set(required_isolation_cases(pin))
    if (not isinstance(cases, list) or any(not isinstance(case, dict) for case in cases)
            or len(cases) != len(expected_cases) or {case.get("name") for case in cases} != expected_cases):
        raise RuntimeError("Incomplete isolation evidence")
    for case in cases:
        path = (CATALOG.parent / case["file"]).resolve()
        if not path.is_relative_to(CATALOG.parent.resolve()) or case.get("passed") is not True or digest(path) != case["sha256"]:
            raise RuntimeError("Isolation evidence changed; revalidate first")
        if resource_policy:
            report = json.loads(path.read_text(encoding="utf-8"))
            checks = report.get("checks") if isinstance(report, dict) else None
            if (not isinstance(report, dict) or report.get("passed") is not True
                    or report.get("policy_id") != policy_id(pin) or not isinstance(checks, dict)
                    or not checks or any(value is not True for value in checks.values())):
                raise RuntimeError("Invalid MCP resource isolation case evidence")
            if case["name"] == "mcp-inherited-server" and not (
                    report.get("guard_rejected") is True
                    and report.get("rpc_methods") == ["mcpServerStatus/list"]
                    and type(report.get("turn_start_calls")) is int and report["turn_start_calls"] == 0
                    and type(report.get("model_requests")) is int and report["model_requests"] == 0
                    and report.get("scope") == "runtime_disabled_loopback_mcp_server_guard; rejected before turn/start"
                    and isinstance(report.get("mcp_server_inventory_before_turn"), dict)
                    and isinstance(report["mcp_server_inventory_before_turn"].get("data"), list)
                    and any(isinstance(server, dict) and server.get("name") == "compai_inherited_synthetic"
                            for server in report["mcp_server_inventory_before_turn"]["data"])
                    and report.get("mcp_fixture_requests") == []
                    and all(checks.get(key) is True for key in ("guard_rejected", "configured_server_observed",
                        "guard_checked_actual_runtime", "no_turn_start", "no_model_requests", "disabled_server_not_contacted",
                        "outside_fixture_unchanged", "runtime_pin_verified", "no_unexpected_error"))):
                raise RuntimeError("Inherited MCP server rejection evidence is missing")
            if case["name"] == "mcp-resources" and any(checks.get(key) is not True for key in (
                    "mcp_servers_empty_before_turn", "mcp_servers_empty_after_turn",
                    "mcp_resource_list_empty", "mcp_resource_templates_empty", "mcp_unknown_server_read_rejected",
                    "no_sentinel_in_tool_results", "outside_fixture_unchanged", "no_node_or_fetch_globals",
                    "code_host_inventory", "allowed_snapshot_executed", "one_mcp_resource_diagnostic",
                    "resource_helpers_are_functions")):
                raise RuntimeError("Empty-server MCP resource behavior evidence is missing")
    return pin


def checked_web_pin(pin):
    """Search is an explicit per-request opt-in with separate offline evidence."""
    try:
        report = json.loads((CATALOG.parent / "web-search-validation.json").read_text(encoding="utf-8"))
        resource_policy = bool(resource_tools(pin))
        if (report.get("schema_version") != (2 if resource_policy else 1)
                or report.get("all_passed") is not True
                or resource_policy and report.get("policy_id") != policy_id(pin)):
            raise ValueError()
        if any(report.get(key) != pin[key] for key in ("codex_version", "binary_sha256", "catalog_sha256", "model", "runtime_files")):
            raise ValueError()
        cases = report["cases"]
        if len(cases) != 4 or {case["name"] for case in cases} != {"text", "inspect", "deny-command", "deny-patch"}:
            raise ValueError()
        required = {"turn_completed", "known_model", "loopback_response_used", "outside_fixture_unchanged",
                    "no_direct_provider_tools", "only_expected_wrappers", "only_requested_model",
                    "hosted_web_declared", "patch_not_declared", "no_mcp_servers", "shell_not_declared", "no_other_nested_tools"}
        if resource_policy:
            required |= {"mcp_servers_empty_before_turn", "mcp_servers_empty_after_turn"}
        declarations = known_function_declaration_prefixes(pin, web_search=True)
        nested_tools = expected_nested_tools(pin, web_search=True)
        for case in cases:
            checks = required | ({"code_host_inventory", "no_node_or_fetch_globals"} if case["name"] == "inspect" else
                                 {"blocked_in_code_host"} if case["name"].startswith("deny-") else set())
            if any(case["checks"].get(key) is not True for key in checks):
                raise ValueError()
            if case["nested_tool_declarations"] != declarations:
                raise ValueError()
            if (declared_function_names(case["nested_tool_declarations"]) != nested_tools
                    or resource_policy and case.get("nested_tool_names") != nested_tools):
                raise ValueError()
            if case["name"] == "inspect" and case["code_host"] != [{
                    "tools": nested_tools, "require": "undefined",
                    "process": "undefined", "fetch": "undefined"}]:
                raise ValueError()
    except (OSError, ValueError, KeyError, TypeError, AttributeError):
        raise RuntimeError("Web search isolation evidence is missing or changed; revalidate before enabling search") from None


def find_runtime():
    """Use only the plugin-owned runtime; desktop updates and PATH are irrelevant."""
    return resolve_runtime(ROOT)


def audit_from_output(output):
    parts = output if isinstance(output, list) else [{"text": output}]
    for part in parts:
        if not isinstance(part, dict) or not isinstance(part.get("text"), str):
            continue
        for line in part["text"].splitlines():
            try:
                value = json.loads(line)
            except ValueError:
                continue
            if isinstance(value, dict) and value.get("tag") == "ffxiv_companion_audit_v1":
                return value
    return None


def emit(value):
    print(json.dumps(value), flush=True)


class QuestionRunner:
    def open_question(self, instructions, tools):
        quota = self.preflight()
        provider = self.question_provider()
        return provider, provider.open(instructions, tools), quota

    def ask(self, request, quest_query=None, inventory_query=None, history_query=None, game_context_query=None,
            npc_dialogue_query=None, character_details_query=None, personality_details_query=None, currency_query=None, duty_query=None,
            game_definitions_query=None, gc_supply_query=None, npc_target_query=None, storage_query=None, valuation_query=None,
            collection_query=None, progression_query=None, crafting_log_query=None, menu_query=None, activity_query=None,
            response_timeout_seconds=None, *, prepared=None):
        if prepared is None:
            prepared = normalize_request(request)
        response_timeout_seconds = resolve_response_timeout_seconds(response_timeout_seconds, prepared.effort)
        if prepared.provider != self.provider_id:
            raise RuntimeError("Request provider does not match the selected runtime; no fallback")
        question, snapshot, history = prepared.question, prepared.snapshot, prepared.history
        selected = (prepared.model, prepared.effort)
        if selected != (self.model, self.effort):
            raise RuntimeError("Model/reasoning request does not match the isolated process selection")
        search_requested = prepared.web_search
        if search_requested != getattr(self, "web_search", False):
            raise RuntimeError("Web search request does not match the isolated process mode")
        quests, quest_lookup = prepared.quests, prepared.quest_lookup
        inventory_lookup, history_lookup = prepared.inventory_lookup, prepared.history_lookup
        chat_notes = prepared.chat_notes
        game_context_lookup, npc_dialogue_lookup = prepared.game_context_lookup, prepared.npc_dialogue_lookup
        character_details_lookup, spoilers_allowed = prepared.character_details_lookup, prepared.spoilers_allowed
        public_data = GameDataTools(online_allowed=search_requested, cache_dir=PROFILE / "public-data-cache",
                                   cancelled=self.cancel_event.is_set,
                                   progress=lambda message: emit({"question_state": message})) if prepared.public_data_allowed else None
        if public_data is not None:
            public_data.resume_pricing_allowed = question.strip().casefold().rstrip(".!?").strip() in (
                "continue pricing", "resume pricing")
        lookups = build_registry(prepared, {"quests": quest_query, "inventory": inventory_query,
            "history": history_query, "game_context": game_context_query, "npc_dialogue": npc_dialogue_query,
            "character_details": character_details_query, "personality_details": personality_details_query,
            "currencies": currency_query, "duties": duty_query, "collections": collection_query, "progression": progression_query,
            "crafting_log": crafting_log_query, "menus": menu_query, "activities": activity_query, "game_definitions": game_definitions_query,
            "gc_supply": gc_supply_query, "npc_target": npc_target_query, "storage": storage_query, "valuation": valuation_query},
            public_data, CompanionFailure)
        hosted_search = search_requested and self.provider_id == "codex"
        instructions = WEB_INSTRUCTIONS if hosted_search else INSTRUCTIONS
        if prepared.includes_personality:
            instructions = instructions.replace(DATA_RULE, PERSONALITY_DATA_RULE) + PERSONALITY_INSTRUCTIONS
        if prepared.optional_web_search:
            instructions = instructions.replace(
                "The user selected Search web: perform a hosted web lookup for this question before answering.",
                "The user permits hosted web lookup when public facts need verification; it is optional for questions answered by local evidence.")
            if quest_lookup is not None:
                instructions = instructions.replace("Do not infer quest unlocks.",
                    "Do not infer quest unlocks from inventory alone; combine recorded quest status with verified public requirements, and distinguish unknown states.")
            if lookups:
                tool_names = "discover_tools and invoke_tool"
                instructions = instructions.replace(
                    "Do not claim to have searched the web. Do not follow links or use tools; all allowed context is already supplied.",
                    "Do not claim to have searched the web or follow links. Only the supplied context and registered read-only tools " + tool_names + " are available.")
                instructions = instructions.replace("Only hosted web search is authorized.",
                    "Only hosted web search and the registered read-only tools " + tool_names + " are authorized.")
        if inventory_lookup is not None:
            instructions = instructions.replace("If inventory is null, never invent current owned items, quantities, or absence. Mention that no current inventory\n"
                "snapshot is available only when the user asks about ownership, counts, or their inventory.",
                "Inventory rows use the query_inventory capability; discover and invoke it when needed. Never invent holdings or absence.")
            instructions = instructions.replace("For supplied inventory counts, use only its deterministic totals. Use slot_items for individual item locations:",
                "For inventory counts, use the tool's deterministic total_quantity for the requested filter. Use rows for individual item locations:")
            instructions = instructions.replace("Use chocobo_details.coverage to explain missing candidate cards only when breeding or pedigree data is relevant.",
                "Use inventory_lookup.containers and all relevant pedigree result pages to establish candidate coverage; null pedigrees remain unknown.")
            instructions = instructions.replace("Each pedigree's father_stars and mother_stars are separate five-star rows, in parent_star_order.",
                "Each pedigree's father_stars and mother_stars contain five values ordered maximum_speed, acceleration, endurance, stamina, cunning.")
        instructions += QUEST_INSTRUCTIONS if quests is not None else ""
        if search_requested and not hosted_search:
            instructions += "\nThis provider has no hosted web-search tool. Permitted public game-data APIs may be used through registered tools. Never claim a web search occurred.\n"
        try:
            deferred = DeferredTools(lookups)
        except DeferredToolsError as exc:
            # This validates application-owned definitions before a model turn.
            # It is a local setup failure, not evidence of expired account access.
            # Later model/tool protocol and permission failures keep their own codes.
            raise CompanionFailure("provider", str(exc) +
                " Reload/update CompAI or report this error; refreshing account status will not fix local tool definitions."
                " No automatic retry.", stage="provider.request") from None
        instructions += deferred.instructions
        if lookups:
            instructions += (
                f"\nThis question has a shared allowance of {MAX_LOOKUP_CALLS} tool lookups, "
                f"{MAX_LOOKUP_TOTAL_BYTES} bytes of tool results, and {response_timeout_seconds} seconds to finish. "
                "These limits are shared across all tools and result pages; combined searches may consume more than one lookup. "
                "Plan a small set of relevant queries and leave time to answer. If coverage is too large, "
                "explain the limit and ask to narrow the scope instead of scanning every page.\n"
            )
        if public_data is not None and "price_saved_items" not in lookups and any(tool.reader in ("inventory", "storage") for tool in lookups.values()):
            instructions += (
                "\nFor a most-expensive-item or whole-inventory valuation, first establish the requested world/DC, "
                "candidate coverage and whether the user means per-item or total-stack value. The market tool prices "
                "one item at a time. If all relevant candidates cannot be checked within the remaining budget, "
                "propose a bounded shortlist or storage scope. Never call a sampled winner the most valuable item "
                "across all inventories; identify exactly which candidates were compared. Report asking prices as "
                "estimates, never guaranteed sale proceeds.\n"
            )
        instructions = instructions.replace("This chat supports text input only; do not suggest uploading screenshots or claim you can inspect images here.", "")
        instructions += ATTACHMENT_INSTRUCTIONS if prepared.attachments else (
            "\nNo files are attached to this question. Past attachment labels do not include their contents; "
            "ask the user to select a file again if needed.\n")
        instructions += NOTES_INSTRUCTIONS if chat_notes.strip() else ""
        instructions += spoiler_instructions(spoilers_allowed) if spoilers_allowed is not None else ""
        instructions += (
            "\nCurrent request configuration supplied by the companion (trusted application metadata): "
            + json.dumps({"provider": self.provider_id, "model": self.model,
                          "reasoning_effort": "provider default" if self.provider_id != "codex" and self.effort == "none" else self.effort}, ensure_ascii=True)
            + ". If asked which model or reasoning level is selected, report these exact configured values. "
            "They describe this request and supersede model/reasoning labels in previous chat messages. "
            "Do not claim to independently inspect the provider's underlying model implementation or private reasoning. "
            "Do not mention this configuration unless the user asks.\n"
        )
        provider, tid, quota = self.open_question(instructions, deferred.definitions)
        # Revalidate expiry/permissions and compare copied approved data before send.
        recheck_request(request, prepared)
        references = source_context("\n".join([question] + [pair["question"] for pair in history]))
        turn, answer = None, None
        search_performed = False
        lookup_calls, lookup_bytes = 0, 0
        lookup_call_ids = set()
        reader_calls = dict.fromkeys(READER_NAMES, 0)
        returned_pedigree_slots = set()
        character_details_stamps = {}
        currency_identity = None
        duty_identity = None
        collection_identities = {}
        collection_rows = {}
        progression_identities = {}
        progression_rows = {}
        achievement_progress_id = None
        crafting_log_identity = None
        crafting_log_rows = {}
        menu_identities = {}
        menu_rows = {}
        activity_identities = {}
        activity_rows = {}
        market_notice = None
        metrics = TurnMetrics(emit)
        try:
            metrics.activity("starting")
            turn = provider.start(tid, question, prepared.context(references),
                                  prepared.includes_personality, metrics.preview,
                                  **({"attachments": prepared.attachments} if prepared.attachments else {}))
            emit({"question_state": "started"})
            metrics.activity("working")
            deadline = time.monotonic() + response_timeout_seconds
            if public_data is not None:
                public_data.question_deadline = deadline
            while time.monotonic() < deadline:
                try:
                    event = turn.read(timeout=min(10, max(.1, deadline - time.monotonic())))
                except TimeoutError:
                    continue
                metrics.observe_event(event)
                if event.kind == "tool" and event.tool == "discover_tools":
                    try:
                        if (not event.is_request or not event.strictly_correlated or
                                type(event.request_id) not in (int, str) or event.request_id in lookup_call_ids):
                            raise CompanionFailure("protocol", "Uncorrelated or duplicate tool discovery")
                        lookup_call_ids.add(event.request_id)
                        metrics.tool(event.request_id)
                        if self.cancel_event.is_set():
                            raise CompanionFailure("reader", "Question cancelled")
                        result = deferred.discover(event.arguments)
                        turn.reply_tool(event, True, result)
                    except Exception as exc:
                        turn.reject_tool(event, "Tool discovery")
                        kind = exc.kind if isinstance(exc, (CompanionFailure, DeferredToolsError)) else "protocol"
                        raise CompanionFailure(kind, "Tool discovery failed or reached its limit; question interrupted") from None
                    emit({"question_state": "loaded tool details"})
                    metrics.activity("working")
                    continue
                if event.kind == "tool" and event.tool == "invoke_tool":
                    lookup_calls += 1  # Invalid attempts consume the same bounded allowance.
                    valid_call_id = type(event.request_id) in (int, str) and event.request_id not in lookup_call_ids
                    label = "Data"
                    budget_failure = None  # Fixed local diagnostic only; never expose callback exception text.
                    try:
                        if not event.is_request or not event.strictly_correlated or not valid_call_id:
                            raise CompanionFailure("protocol", "Unexpected data lookup")
                        lookup_call_ids.add(event.request_id)
                        metrics.tool(event.request_id)
                        tool_name, tool_arguments = deferred.resolve(event.arguments)
                        registration = lookups[tool_name]
                        label = registration.label
                        meta, query, reader = registration.summary, registration.query, registration.reader
                        validate_args, validate_data = registration.validate_arguments, registration.validate_result
                        require_lookup_permission(reader, tool_arguments, meta)
                        success = True
                        try:
                            arguments = validate_args(tool_arguments)
                        except (RuntimeError, ValueError, TypeError, KeyError):
                            # Bad model arguments consume one call; a correction is
                            # another call in this same turn, never a new question.
                            result, success = unavailable_lookup(label, invalid_arguments=True), False
                        if self.cancel_event.is_set():
                            raise RuntimeError("Question cancelled")
                        if lookup_calls > MAX_LOOKUP_CALLS:
                            budget_failure = f"reached the shared {MAX_LOOKUP_CALLS}-call budget"
                            raise CompanionFailure("reader", "Data lookup call budget exhausted")
                        if success:
                            if registration.reader_counts is not None:
                                # A federated search still pays for each underlying reader.
                                lookup_calls += sum(registration.reader_counts(arguments).values()) - 1
                                if lookup_calls > MAX_LOOKUP_CALLS:
                                    budget_failure = f"reached the shared {MAX_LOOKUP_CALLS}-call budget"
                                    raise CompanionFailure("reader", "Data lookup call budget exhausted")
                            try:
                                stage = "tool.game_data" if reader == "public_data" else "tool." + reader
                                with measured(stage, emit):
                                    raw = query(arguments)
                            except PermissionError:
                                raise CompanionFailure("permission", "Reader access denied; question interrupted") from None
                            except LookupTransportTimeout:
                                # The local pipe stopped answering. End this question,
                                # but do not invalidate a verified provider account.
                                raise CompanionFailure("reader", "Local data lookup timed out; question interrupted") from None
                            except (OSError, TimeoutError):
                                raw = {"reader_unavailable": True}
                            if isinstance(raw, dict) and set(raw) == {"reader_unavailable"} and raw["reader_unavailable"] is True:
                                result, success = unavailable_lookup(label), False
                            else:
                                # Malformed/private results and changed snapshot
                                # identity stay fatal; never relabel them unavailable.
                                result = validate_data(raw, meta, arguments)
                        if success and reader == "character_details":
                            selection = (arguments["view"], arguments["gearset_id"])
                            stamp = datetime.fromisoformat(result["captured_at_utc"].replace("Z", "+00:00")).astimezone(timezone.utc).isoformat()
                            if selection in character_details_stamps and stamp != character_details_stamps[selection]:
                                raise RuntimeError("Character details section capture changed during the question")
                            character_details_stamps[selection] = stamp
                        if success and reader == "currencies":
                            identity = (datetime.fromisoformat(result["captured_at_utc"].replace("Z", "+00:00")).isoformat(),
                                        result["available"], result["unavailable_reason"], result["coverage"])
                            if currency_identity is not None and identity != currency_identity:
                                raise RuntimeError("Currency snapshot changed during the question")
                            currency_identity = identity
                        if success and reader == "duties":
                            identity = (datetime.fromisoformat(result["captured_at_utc"].replace("Z", "+00:00")).astimezone(timezone.utc).isoformat(),
                                        result["available"], result["unavailable_reason"], result["coverage"])
                            if duty_identity is not None and identity != duty_identity:
                                raise RuntimeError("Duty snapshot changed during the question")
                            duty_identity = identity
                        if success and reader == "collections":
                            category = arguments["category"]
                            identity = (datetime.fromisoformat(result["captured_at_utc"].replace("Z", "+00:00")).astimezone(timezone.utc).isoformat(),
                                        *(result[key] for key in ("available", "unavailable_reason", "coverage", "source",
                                            "readiness", "catalog_count", "observed_unlocked_count",
                                            "not_observed_unlocked_count", "unknown_count")))
                            if category in collection_identities and identity != collection_identities[category]:
                                raise RuntimeError("Collection category snapshot changed during the question")
                            collection_identities[category] = identity
                            for entry in result["entries"]:
                                key = (category, entry["id"])
                                observed = (entry["name"], entry["state"], entry["unlocked"], entry["unknown_reason"])
                                if key in collection_rows and collection_rows[key] != observed:
                                    raise RuntimeError("Collection entry changed during the question")
                                collection_rows[key] = observed
                        if success and reader == "progression":
                            category = arguments["category"]
                            identity = (datetime.fromisoformat(result["captured_at_utc"].replace("Z", "+00:00")).astimezone(timezone.utc).isoformat(),
                                        *(result[key] for key in ("available", "unavailable_reason", "coverage", "source",
                                            "readiness", *PROGRESSION_COUNTS)))
                            if category in progression_identities and identity != progression_identities[category]:
                                raise RuntimeError("Progression category snapshot changed during the question")
                            progression_identities[category] = identity
                            for entry in result["entries"]:
                                if category == "achievement_progress":
                                    if achievement_progress_id is not None and entry["id"] != achievement_progress_id:
                                        raise RuntimeError("Loaded achievement progress record changed during the question")
                                    achievement_progress_id = entry["id"]
                                key = (category, entry["id"])
                                observed = tuple(entry[field] for field in PROGRESSION_ROW_FIELDS) + (entry.get('observed_at_utc'), entry.get('observed_page'))
                                if key in progression_rows and progression_rows[key] != observed:
                                    raise RuntimeError("Progression entry changed during the question")
                                progression_rows[key] = observed
                        if success and reader == "menus":
                            category = arguments["category"]
                            # Search results and spelling suggestions can change between queries on the same frozen menu.
                            identity = tuple(result[key] for key in ("scope", "category", "captured_at_utc", "available",
                                "unavailable_reason", "coverage", "source", "readiness", "catalog_count"))
                            if category in menu_identities and identity != menu_identities[category]:
                                raise RuntimeError("Menu observation changed during the question")
                            menu_identities[category] = identity
                            for entry in result["entries"]:
                                key = (category, entry["id"])
                                if key in menu_rows and entry != menu_rows[key]:
                                    raise RuntimeError("Menu entry changed during the question")
                                menu_rows[key] = entry
                        if success and reader == "activities":
                            category = arguments["category"]
                            identity = tuple((key, result[key]) for key in sorted(result) if key not in
                                ("entries", "matched_count", "offset", "limit", "next_offset", "truncated"))
                            if category in activity_identities and identity != activity_identities[category]:
                                raise RuntimeError("Activity observation changed during the question")
                            activity_identities[category] = identity
                            for entry in result["entries"]:
                                key = (category, entry["id"])
                                observed = tuple((field, entry[field]) for field in sorted(entry))
                                if key in activity_rows and observed != activity_rows[key]:
                                    raise RuntimeError("Activity entry changed during the question")
                                activity_rows[key] = observed
                        if success and reader == "crafting_log":
                            identity = tuple(result[key] for key in CRAFTING_LOG_IDENTITY_FIELDS)
                            if crafting_log_identity is not None and identity != crafting_log_identity:
                                raise RuntimeError("Crafting log catalog or availability changed during the question")
                            crafting_log_identity = identity
                            for entry in result["entries"]:
                                key = entry["id"]
                                observed = tuple(entry[field] for field in CRAFTING_LOG_ROW_FIELDS)
                                if key in crafting_log_rows and crafting_log_rows[key] != observed:
                                    raise RuntimeError("Crafting log row changed during the question")
                                crafting_log_rows[key] = observed
                            if len(crafting_log_rows) > MAX_CACHED_ROWS:
                                raise RuntimeError("Crafting log row cache exceeded its bound")
                        if self.cancel_event.is_set():
                            raise RuntimeError("Question cancelled")
                        lookup_bytes += lookup_result_size(result)
                        if lookup_bytes > MAX_LOOKUP_TOTAL_BYTES:
                            budget_failure = f"exceeded the shared {MAX_LOOKUP_TOTAL_BYTES}-byte result budget"
                            raise CompanionFailure("reader", "Data lookup response budget exhausted")
                    except Exception as exc:
                        turn.reject_tool(event, label)
                        kind = exc.kind if isinstance(exc, (CompanionFailure, DeferredToolsError)) else "protocol"
                        message = (label + " lookup " + budget_failure + "; question interrupted. "
                                   "Narrow the question and send again." if budget_failure else
                                   label + " lookup failed or was rejected; question interrupted")
                        raise CompanionFailure(kind, message) from None
                    # Keep full reader data for validation, frozen-row checks and original-byte
                    # accounting. Only the model-facing activity reply uses shared null defaults.
                    reply_result = activity_model_result(result) if success and reader == "activities" else result
                    turn.reply_tool(event, success, reply_result)
                    if success:
                        if reader == "valuation" and result.get("ok") is True:
                            market_notice = result["pricing_notice"]
                        if registration.reader_counts is not None:
                            for part, key in (("inventory", "carried"), ("storage", "storage")):
                                if isinstance(result[key], dict) and "rows" in result[key]:
                                    reader_calls[part] += 1
                            inventory_result = result["carried"]
                        else:
                            reader_calls[reader] += 1
                            inventory_result = result if reader == "inventory" else None
                        if isinstance(inventory_result, dict) and "rows" in inventory_result:
                            returned_pedigree_slots.update((row["bag"], row["slot"]) for row in inventory_result["rows"]
                                                           if row["pedigree"] is not None)
                    emit({"question_state": ("checked " if success else "unavailable ") + reader})
                    metrics.activity("working")
                    continue
                if not event.correlated:
                    if event.is_request:
                        turn.reject_request(event)
                        raise RuntimeError("Unexpected server request; question interrupted")
                    continue
                if event.is_request:
                    turn.reject_request(event)
                    raise RuntimeError("Unexpected tool or approval request; question interrupted")
                if event.usage is not None:
                    metrics.counters = event.usage
                if event.kind == "search":
                    if not hosted_search:
                        raise RuntimeError("Unexpected web search in an ordinary question")
                    search_performed = True
                    emit({"question_state": "searched"})
                elif event.kind == "answer":
                    answer = event.text
                elif event.kind == "completed":
                    if event.status != "completed":
                        raise CompanionFailure(event.failure_kind, "Question stopped or failed; no automatic retry")
                    break
            else:
                raise CompanionFailure("provider",
                    f"Question reached the {response_timeout_seconds}-second response limit. "
                    "Increase Response limit in Settings > Account or try a narrower question; no automatic retry.", stage="provider.request")
            if hosted_search and not search_performed and not prepared.optional_web_search:
                raise RuntimeError("Web search could not be verified for this request; no answer saved and no automatic retry")
            parsed = parse_question_answer(answer)
            turn.finish()
            metrics.first_answer()
            metrics.activity("finishing")
            metrics.finish()
            sources = []
            if references["items"]:
                for item in references["items"]:
                    for source in item["sources"]:
                        sources.append({"title": source["title"], "url": source["url"]})
            if prepared.includes_history:
                if snapshot is None:
                    context = ""
                else:
                    context = ("Historical carried-bag capture as of " + snapshot["captured_at_utc"] + ". "
                               + snapshot["scope"] + ". Current holdings are unverified. "
                               + ("Changes were observed before sending." if snapshot["changed_since_capture"]
                                  else "No changes were observed before sending; later changes are unknown."))
                if quests is not None:
                    context += ((" " if context else "") + "Quest capture as of " + quests["captured_at_utc"]
                                + "; " + str(len(quests["rows"])) + " selected ordinary quest rows. "
                                + ("Last-known state; changes may be missing." if quests["stale"]
                                   else "Observed before sending; later changes are unknown."))
                if inventory_lookup is not None and reader_calls["inventory"]:
                    context += ((" " if context else "") + "Inventory snapshot as of " + inventory_lookup["captured_at_utc"]
                                + "; " + str(reader_calls["inventory"]) + " local inventory lookups. Carried bags 1-4 only. "
                                + ("Changes were observed before sending." if inventory_lookup["changed"]
                                   else "Observed before sending; later changes are unknown."))
                if inventory_lookup is not None:
                    context += ((" " if context else "") + "Pedigrees: "
                                + str(inventory_lookup["verified_pedigree_count"]) + " available in sent snapshot; "
                                + str(len(returned_pedigree_slots)) + " returned by inventory lookups.")
                if quest_lookup is not None and reader_calls["quests"]:
                    context += ((" " if context else "") + "Quest snapshot as of " + quest_lookup["captured_at_utc"]
                                + "; " + str(reader_calls["quests"]) + " local quest lookups. "
                                + ("Last-known state; changes may be missing." if quest_lookup["stale"]
                                   else "Observed before sending; later changes are unknown."))
                if reader_calls["currencies"]:
                    context += ((" " if context else "") + "Currency inventory observed at " + currency_identity[0]
                                + "; " + str(reader_calls["currencies"]) + " local currency lookups. "
                                + "Partial local observations; absent currencies and server freshness are unknown.")
                if reader_calls["duties"]:
                    context += ((" " if context else "") + "Duty state observed at " + duty_identity[0]
                                + "; " + str(reader_calls["duties"]) + " local duty lookups. "
                                + "Observed positive unlock/completion only; unsupported and negative states remain unknown.")
                if reader_calls["collections"]:
                    context += ((" " if context else "") + str(reader_calls["collections"])
                                + " local collection lookups. Each requested category is frozen after its first lookup; "
                                + "positive unlocks are observed, other states remain unknown. Unlocks do not prove item ownership.")
                if reader_calls["progression"]:
                    context += ((" " if context else "") + str(reader_calls["progression"])
                                + " local progression lookups. Requested categories are frozen independently; "
                                + "achievement completion uses the loaded bitmap; any achievement progress is a loaded cached record that may be a previous selection. Aether negatives remain unknown, "
                                + "gathering/fishing-log negatives remain unknown, and Shared FATE/Trust show only observed visible menu groups.")
                if reader_calls["menus"]:
                    context += ((" " if context else "") + str(reader_calls["menus"])
                                + " shop/glamour lookups; loaded observations only, with plate references distinct from stored appearances and owned quantities.")
                if reader_calls["activities"]:
                    context += ((" " if context else "") + str(reader_calls["activities"])
                                + " local activity lookups; observed menu data with category-specific limits. "
                                + "Configured references and public reward previews do not establish ownership or claims.")
                if reader_calls["crafting_log"]:
                    context += ((" " if context else "") + str(reader_calls["crafting_log"])
                                + " local crafting-log lookups. Public recipe filters precede bounded observations; "
                                + "rows retain their first observation times, native negatives remain unknown, and counts describe definitions only.")
                if reader_calls["game_definitions"]:
                    context += ((" " if context else "") + str(reader_calls["game_definitions"]) + " local game-definition lookups; public items/recipes/actions/quests/maps, not character unlock or completion state.")
                if reader_calls["gc_supply"]:
                    context += ((" " if context else "") + str(reader_calls["gc_supply"]) + " Grand Company delivery-menu lookups; observed requests and eligibility, not completion history.")
                if reader_calls["storage"]:
                    context += ((" " if context else "") + str(reader_calls["storage"]) + " storage-cache lookups; last-observed permitted containers only, not live storage or historical sales.")
                if reader_calls["valuation"]:
                    context += ((" " if context else "") + "Saved items batch-priced using Universalis; saved permitted containers only. Ranked by recent-sale estimates capped at asking prices; insufficient sale evidence and missing quotes are excluded, not valued at zero. Prices are estimates, not guaranteed proceeds.")
                if reader_calls["npc_target"]:
                    context += ((" " if context else "") + str(reader_calls["npc_target"]) + " selected-target lookups; visible identification only, no inferred services or player profiles.")
                if game_context_lookup is not None and reader_calls["game_context"]:
                    context += ((" " if context else "") + "Game context as of " + game_context_lookup["captured_at_utc"]
                                + "; " + str(reader_calls["game_context"]) + " local character/location/dialogue lookups.")
                if npc_dialogue_lookup is not None and reader_calls["npc_dialogue"]:
                    context += ((" " if context else "") + "Observed NPC dialogue as of " + npc_dialogue_lookup["captured_at_utc"]
                                + "; " + str(reader_calls["npc_dialogue"]) + " local dialogue lookups; "
                                + str(npc_dialogue_lookup["total_lines"]) + " retained lines; "
                                + str(npc_dialogue_lookup["dropped_lines"]) + " dropped lines. Observed Talk only; incomplete history.")
                if character_details_lookup is not None and reader_calls["character_details"]:
                    context += ((" " if context else "") + "Character detail section/set captures at "
                                + ", ".join(dict.fromkeys(character_details_stamps.values()))
                                + "; " + str(reader_calls["character_details"]) + " local detail lookups. "
                                + "Each requested section/set is frozen after its first lookup; separate sections may reflect different times. "
                                + "Equipped gear is capture-time state; saved gearsets are potentially outdated references.")
                if reader_calls["personality_details"]:
                    context += ((" " if context else "") + str(reader_calls["personality_details"]) + " optional personality detail lookups.")
                if reader_calls["public_data"]:
                    context += ((" " if context else "") + str(reader_calls["public_data"]) + " public game-data lookups (XIVAPI/Universalis/xivgear; may use cached results).")
                if references["items"]:
                    context += (" " if context else "") + "Bundled acquisition reference catalog checked " + references["checked_at"] + "."
                if search_performed:
                    context += (" " if context else "") + "Web lookup performed for this reply."
                if prepared.includes_notes:
                    context += ((" " if context else "") + "Recent context: " + str(len(history)) + " exchanges; "
                                + ("latest exchange is a partial excerpt; " if any(pair.get("is_excerpt") for pair in history) else "")
                                + ("chat notes supplied; " if chat_notes.strip() else "")
                                + str(reader_calls["history"]) + " older chat lookups.")
                if market_notice:
                    context += "\n" + market_notice
                emit({"question_answer": parsed["answer"], "question_context": context,
                      "question_model": self.model, "question_effort": "provider default" if self.provider_id != "codex" and self.effort == "none" else self.effort,
                      "question_sources": sources, "search_performed": search_performed, "quota_before": quota})
            else:
                if snapshot is None:
                    footer = "\n\nNo inventory snapshot supplied; owned items and quantities are unavailable."
                else:
                    footer = ("\n\nInventory as of capture " + snapshot["captured_at_utc"] + ". "
                              + snapshot["scope"] + ". Current holdings are unverified.")
                    if snapshot.get("changed_since_capture") is True:
                        footer += " Inventory changed since capture."
                if references["items"]:
                    footer += "\nAcquisition reference checked " + references["checked_at"] + " (bundled; no live search):"
                emit({"question_answer": parsed["answer"] + footer,
                      "question_sources": sources, "quota_before": quota})
        except Exception:
            if turn is not None:
                turn.finish()
                try:
                    turn.cancel()
                except Exception:
                    pass
            raise
        finally:
            metrics.finish()



class Companion(QuestionRunner):
    provider_id = "codex"
    model = DEFAULT_MODEL
    effort = DEFAULT_EFFORT

    @timed("runtime.startup", lambda value: emit(value))
    def __init__(self, cancel_event=None, web_search=False, model=DEFAULT_MODEL, effort=DEFAULT_EFFORT):
        if type(web_search) is not bool:
            raise RuntimeError("Invalid web search choice")
        self.model, self.effort = validate_selection(model, effort)
        self.web_search = web_search
        self.cancel_event = cancel_event or threading.Event()
        executable = find_runtime()
        self.pin = checked_pin(executable)
        self.model_allowlist = checked_model_selection(self.pin, CATALOG)
        require_permitted(self.model, self.effort, self.model_allowlist)
        if self.web_search:
            checked_web_pin(self.pin)
        for name in ("home", "work", "tmp"):
            (PROFILE / name).mkdir(parents=True, exist_ok=True)
        if (PROFILE / "home" / "config.toml").exists():
            raise RuntimeError("Dedicated profile has an unexpected config.toml; inspect before continuing")
        env = {k: v for k, v in os.environ.items() if k.upper() in {
            "PATH", "PATHEXT", "SYSTEMROOT", "WINDIR", "COMSPEC", "SYSTEMDRIVE",
            "USERPROFILE", "APPDATA", "LOCALAPPDATA", "PROGRAMFILES", "PROGRAMFILES(X86)",
        }}
        env.update(CODEX_HOME=str(PROFILE / "home"), TEMP=str(PROFILE / "tmp"),
                   TMP=str(PROFILE / "tmp"), NO_PROXY="127.0.0.1,localhost")
        command = [str(executable), "app-server", "--strict-config"]
        config = [value for value in CONFIG if not value.startswith(("web_search=", "model=", "model_reasoning_effort="))]
        config.append('web_search="live"' if self.web_search else 'web_search="disabled"')
        config.extend(["model=" + json.dumps(self.model), "model_reasoning_effort=" + json.dumps(self.effort)])
        for value in config + ["model_catalog_json=" + json.dumps(str(CATALOG))]:
            command.extend(["-c", value])
        try:
            self.process = start_owned_process(command, cwd=PROFILE / "work", env=env,
                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                bufsize=-1)
        except ProcessContainmentError:
            raise CompanionFailure("provider", "Required Windows process containment could not be established; no runtime was left running",
                                   stage="runtime.containment") from None
        except OSError as exc:
            raise CompanionFailure("provider", "Validated Codex runtime could not start on this machine",
                                   stage="provider.start") from exc
        self.inbox = RuntimeInbox()
        self.seq = 0
        threading.Thread(target=self._read, daemon=True).start()
        try:
            self.call("initialize", {"clientInfo": {"name": "ffxiv_companion", "version": "0.1.0"},
                                    "capabilities": {"experimentalApi": True}})
            self.send({"method": "initialized", "params": {}})
        except Exception as exc:
            self.close()
            if isinstance(exc, CompanionFailure):
                exc.stage = "provider.start"
                raise
            raise CompanionFailure("protocol", "Validated Codex runtime initialization failed; inspect local installation",
                                   stage="provider.start") from None

    def _read(self):
        try:
            while True:
                frame = self.process.stdout.readline(MAX_RUNTIME_FRAME_BYTES + 1)
                if not frame or not self.inbox.feed(frame):
                    return
        except (OSError, ValueError):
            self.inbox.finish("App Server pipe could not be read; no automatic retry")
        finally:
            self.inbox.finish()

    def send(self, message):
        self.inbox.require_writable()
        self.process.stdin.write((json.dumps(message) + "\n").encode("utf-8"))
        self.process.stdin.flush()

    def receive(self, target=None, timeout=30, *, ignore_cancel=False, cancel_grace=0):
        if self.cancel_event.is_set() and not ignore_cancel and cancel_grace <= 0:
            raise RuntimeError("Operation cancelled")
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if self.cancel_event.is_set() and not ignore_cancel:
                if cancel_grace <= 0:
                    raise RuntimeError("Operation cancelled")
                # A dispatched turn/start needs its acknowledgement to obtain the
                # turn ID for interruption. Never extend this short grace period.
                deadline = min(deadline, time.monotonic() + cancel_grace)
            try:
                message = self.inbox.get(target, timeout=min(.25, max(.01, deadline - time.monotonic())))
            except queue.Empty:
                continue
            if self.cancel_event.is_set() and not ignore_cancel and cancel_grace <= 0:
                raise RuntimeError("Operation cancelled")
            return message
        raise TimeoutError("App Server response timed out")

    def call(self, method, params=None, *, ignore_cancel=False, timeout=30, cancel_grace=0):
        if self.cancel_event.is_set() and not ignore_cancel:
            raise RuntimeError("Operation cancelled")
        self.seq += 1
        request = {"id": self.seq, "method": method}
        if params is not None:
            request["params"] = params
        self.send(request)
        message = self.receive(self.seq, timeout=timeout, ignore_cancel=ignore_cancel, cancel_grace=cancel_grace)
        if "error" in message:
            # Never print arbitrary authentication errors/response bodies.
            error = message["error"]
            if not isinstance(error, dict) or type(error.get("code")) is not int:
                raise CompanionFailure("protocol", "Invalid App Server error response")
            kind = provider_failure_kind(error)
            if method == "account/rateLimits/read" and kind == "provider":
                kind = "quota"
            raise CompanionFailure(kind, f"{method} failed (RPC {error['code']})")
        return message.get("result", {})

    def account(self, refresh=False):
        account = self.call("account/read", {"refreshToken": refresh}).get("account") or {}
        return {"type": account.get("type"), "planType": account.get("planType")}

    def login(self, open_browser=True):
        # A cached account can outlive its usable session. Validate it before claiming
        # already connected, otherwise Sign in can loop between that label and signed out.
        if self.account(refresh=True)["type"] == "chatgpt":
            emit({"login": "already connected"})
            return
        response = self.call("account/login/start", {"type": "chatgpt"})
        url = response.get("authUrl", "")
        if not official_login_url(url):
            self.call("account/login/cancel", {"loginId": response["loginId"]})
            raise RuntimeError("Unexpected login URL; not opened")
        emit({"login": "waiting for user", "authUrl": url})
        if open_browser:
            try:
                opened = webbrowser.open(url)
                if not opened:
                    emit({"browser": "Browser did not open; use the official URL shown above"})
            except Exception:
                emit({"browser": "open the displayed official URL manually"})
        deadline = time.monotonic() + 300
        try:
            while time.monotonic() < deadline:
                try:
                    message = self.receive(timeout=10)
                except TimeoutError:
                    # Browser login can complete without its notification reaching this
                    # process. Confirm the account through the official App Server RPC.
                    try:
                        account = self.call("account/read", {"refreshToken": True}, timeout=2).get("account") or {}
                    except TimeoutError:
                        continue
                    if account.get("type") == "chatgpt":
                        if self.cancel_event.is_set():
                            raise RuntimeError("Operation cancelled")
                        emit({"login": "completed", "account": {
                            "type": "chatgpt", "planType": account.get("planType")}})
                        return
                    continue
                params = message.get("params", {})
                if message.get("method") == "account/login/completed" and params.get("loginId") == response["loginId"]:
                    if not params.get("success"):
                        raise CompanionFailure("authentication", "Official login did not complete successfully")
                    emit({"login": "completed", "account": self.account()})
                    return
            raise TimeoutError("Login timed out; run login again when ready")
        finally:
            try:
                account = self.call("account/read", {"refreshToken": False}, ignore_cancel=True, timeout=1).get("account") or {}
                if account.get("type") != "chatgpt":
                    self.call("account/login/cancel", {"loginId": response["loginId"]}, ignore_cancel=True, timeout=1)
            except Exception:
                pass  # Best-effort cleanup; preserve the original failure/cancellation.

    def status(self, *, refresh=True):
        account = self.account(refresh=refresh)
        result = {"runtime_pin": "verified", "profile": "dedicated", "account": account}
        if account["type"] == "chatgpt":
            try:
                response = self.call("account/rateLimits/read", {})
                result["quota"] = quota_windows(response, enforce=False)
                # Use the same guard as request preflight; the UI must not apply
                # a stricter hidden cutoff to otherwise available allowance.
                quota_windows(response)
                result["quota_guard"] = "available"
            except (RuntimeError, TimeoutError) as exc:
                if isinstance(exc, CompanionFailure) and exc.kind == "authentication":
                    raise
                reason = str(exc) if isinstance(exc, RuntimeError) else "Allowance check timed out; refresh account status."
                if "quota" not in result:
                    result["quota_error"] = reason
                result["quota_block_reason"] = reason
                result["quota_guard"] = "blocked"
            try:
                result["models"] = self.available_models()
                if not result["models"]:
                    result["models_error"] = "No compatible validated models were returned; refresh account status later"
            except Exception as exc:
                # Ordinary discovery failures do not invalidate sign-in. Only a
                # structured authentication rejection is evidence to do so.
                # Raw server errors can contain sensitive data and are never emitted.
                if isinstance(exc, CompanionFailure) and exc.kind == "authentication":
                    raise
                result["models_error"] = "Model list unavailable; refresh account status to retry"
        emit(result)
        return result

    def available_models(self):
        return self.question_provider().models()

    def question_provider(self) -> QuestionProvider:
        return CodexProvider(self, PROFILE / "work")

    def logout(self):
        account_type = self.account()["type"]
        if account_type is None:
            emit({"logout": "already signed out"})
            return
        if account_type != "chatgpt":
            raise RuntimeError("Unexpected account type; companion logout refused")
        self.call("account/logout")
        if self.account()["type"] is not None:
            raise RuntimeError("Companion account remained signed in after logout")
        emit({"logout": "completed"})

    @timed("runtime.preflight", lambda value: emit(value))
    def preflight(self):
        return self.question_provider().readiness()

    def prove(self, force=False, save=True):
        if (self.model, self.effort) != (DEFAULT_MODEL, DEFAULT_EFFORT):
            raise RuntimeError("Synthetic proof uses only the baseline model/reasoning")
        quota = self.preflight()
        result_path = PROFILE / "proof-result.json"
        if not force and result_path.exists():
            previous = json.loads(result_path.read_text(encoding="utf-8"))
            if previous.get("passed") and (not resource_tools(self.pin) or previous.get("policy_id") == policy_id(self.pin)) and all(previous.get(key) == self.pin[key]
                    for key in ("binary_sha256", "catalog_sha256", "runtime_files")):
                emit({"proof": "already passed for this pin", "file": str(result_path)})
                return
        result = {"checked_at": datetime.now(timezone.utc).isoformat(), "synthetic_only": True,
                  "binary_sha256": self.pin["binary_sha256"], "catalog_sha256": self.pin["catalog_sha256"],
                  "runtime_files": self.pin["runtime_files"], "policy_id": policy_id(self.pin),
                  "model": MODEL, "quota_before": quota, "passed": False, "snapshot_calls": 0}
        thread = self.call("thread/start", {
            "model": MODEL, "modelProvider": "openai", "cwd": str(PROFILE / "work"),
            "approvalPolicy": "never", "sandbox": "read-only", "ephemeral": True,
            "allowProviderModelFallback": False, "experimentalRawEvents": True,
            "dynamicTools": [{"type": "function", "name": "get_snapshot",
                "description": "Return the user-approved synthetic test snapshot; takes no arguments.",
                "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False}}],
        })
        tid = thread["thread"]["id"]
        require_no_mcp_servers(self.call, tid)
        result["inherited_mcp_count"] = 0
        prompt = ("This is a synthetic integration test. Call functions.exec exactly once with the following "
                  "JavaScript, unchanged. Do not add a pragma or any other code/tool calls. Then use the returned "
                  "snapshot to total the TEST TOKEN quantities and answer only JSON {\"total\": integer}.\n\n" + AUDIT_SCRIPT)
        turn_id = None
        raw_calls = {}
        raw_outputs = {}
        final_answer = None
        observed_types = set()
        deadline = time.monotonic() + 150
        try:
            started = self.call("turn/start", {"threadId": tid, "model": MODEL, "effort": "medium",
                "approvalPolicy": "never", "sandboxPolicy": {"type": "readOnly"},
                "input": [{"type": "text", "text": prompt}],
                "outputSchema": {"type": "object", "properties": {"total": {"type": "integer"}},
                                 "required": ["total"], "additionalProperties": False}}, cancel_grace=1)
            turn_id = started["turn"]["id"]
            emit({"proof": "one synthetic turn started"})
            while time.monotonic() < deadline:
                try:
                    message = self.receive(timeout=min(15, max(1, deadline - time.monotonic())))
                except TimeoutError:
                    continue
                method = message.get("method", "")
                params = message.get("params", {})
                if params.get("threadId") not in (None, tid) or params.get("turnId") not in (None, turn_id):
                    continue
                if method == "item/tool/call":
                    permitted = params.get("tool") == "get_snapshot" and params.get("arguments") == {} and result["snapshot_calls"] == 0
                    self.send({"id": message["id"], "result": {"success": permitted, "contentItems": [
                        {"type": "inputText", "text": json.dumps(SNAPSHOT) if permitted else "Denied: unexpected tool or arguments"}]}})
                    if not permitted:
                        raise RuntimeError("Unexpected dynamic tool call")
                    result["snapshot_calls"] += 1
                elif "id" in message and method:
                    self.send({"id": message["id"], "error": {"code": -32601, "message": "Unsupported companion request"}})
                    raise RuntimeError("Unexpected server request; proof interrupted")
                elif method == "rawResponseItem/completed":
                    item = params.get("item", {})
                    if item.get("type") in ("custom_tool_call", "function_call"):
                        call_id = item.get("call_id")
                        if not isinstance(call_id, str) or not call_id or call_id in raw_calls:
                            raise RuntimeError("Missing or duplicate raw tool call ID")
                        raw_calls[call_id] = item
                    elif item.get("type") in ("custom_tool_call_output", "function_call_output"):
                        call_id = item.get("call_id")
                        if not isinstance(call_id, str) or not call_id or call_id in raw_outputs:
                            raise RuntimeError("Missing or duplicate raw tool output ID")
                        raw_outputs[call_id] = item.get("output")
                elif method == "item/completed":
                    item = params.get("item", {})
                    observed_types.add(item.get("type", "unknown"))
                    if item.get("type") == "agentMessage":
                        final_answer = item.get("text")
                elif method == "turn/completed":
                    result["turn_status"] = params.get("turn", {}).get("status")
                    if result["turn_status"] != "completed":
                        raise CompanionFailure(provider_failure_kind(params.get("turn", {}).get("error")),
                                               "Synthetic turn stopped or failed; no automatic retry")
                    break
            else:
                raise TimeoutError("Synthetic proof timed out")
            audit = None
            exact = False
            if len(raw_calls) == 1:
                call_id, call = next(iter(raw_calls.items()))
                exact = call.get("type") == "custom_tool_call" and call.get("name") in ("exec", "functions.exec") and call.get("input", "").strip() == AUDIT_SCRIPT
                audit = audit_from_output(raw_outputs.get(call_id)) if exact else None
            result["exact_audit_script_observed"] = exact
            result["raw_call_count"] = len(raw_calls)
            result["runtime_audit"] = audit
            result["observed_item_types"] = sorted(observed_types)
            try:
                if not isinstance(final_answer, str) or len(final_answer) > MAX_ANSWER_ENVELOPE_CHARS:
                    raise ValueError()
                answer = json.loads(final_answer)
            except (ValueError, RecursionError):
                answer = None
            result["answer"] = answer
            result["passed"] = (result.get("turn_status") == "completed" and exact and audit is not None
                and audit.get("tools") == expected_nested_tools(self.pin, dynamic=("get_snapshot",))
                and all(audit.get(k) == "undefined" for k in ("require", "process", "fetch"))
                and result["snapshot_calls"] == 1 and answer == {"total": 5})
            if not result["passed"]:
                raise RuntimeError("Synthetic turn did not establish all required proof evidence; no automatic retry")
        except Exception:
            try:
                if turn_id is not None:
                    self.call("turn/interrupt", {"threadId": tid, "turnId": turn_id},
                              ignore_cancel=True, timeout=1)
            except Exception:
                pass
            raise
        finally:
            if save:
                result_path.write_text(json.dumps(result, indent=2), encoding="utf-8")
            emit(result)

    def close(self):
        self.inbox.finish("App Server closed; no automatic retry")
        close_owned_process(self.process)


class AccountCompanion(QuestionRunner):
    """Account-backed runtimes reuse the same approved context and tool dispatcher."""
    def __init__(self, provider, cancel_event, model="auto", effort="none", web_search=False):
        self.provider_id, self.cancel_event = provider, cancel_event
        self.model, self.effort, self.web_search = model, effort, web_search
        if provider == "copilot":
            raise CompanionFailure("authentication", "This provider is disabled pending authenticated free-account validation; no login or request was started")
        if provider == "grok":
            from grok_provider import GrokRuntime
            self.runtime = GrokRuntime(ROOT / "runtime" / "grok-win-x64" / "grok.exe",
                PROFILE / "providers" / "grok", cancel_event=cancel_event,
                model=model, effort=effort, web_search=web_search, emit=emit)
            return
        raise RuntimeError("Unsupported account provider; no fallback")

    def question_provider(self):
        return self.runtime.question_provider()

    def open_question(self, instructions, tools):
        provider = self.question_provider()
        tid, quota = provider.open_with_quota(instructions, tools)
        return provider, tid, quota

    def status(self, *, refresh=True):
        result = self.runtime.status()
        if not isinstance(result, dict) or result.get("provider") != self.provider_id:
            raise RuntimeError("Unexpected account provider status; no fallback")
        account = result.get("account")
        guard = result.get("quota_guard")
        if (result.get("runtime_pin") != "verified" or not isinstance(account, dict)
                or account.get("type") not in (None, self.provider_id)
                or guard not in ("available", "blocked")
                or (guard == "available" and account.get("type") != self.provider_id)
                or ("models" in result and not isinstance(result["models"], list))):
            raise RuntimeError("Invalid account provider readiness; requests remain blocked")
        emit(result)

    def login(self, open_browser=True):
        self.runtime.login(open_browser=open_browser)

    def logout(self):
        self.runtime.logout()

    def prove(self, force=False, save=True):
        raise CompanionFailure("provider", "The Codex developer proof is unavailable for this account provider")

    def close(self):
        self.runtime.close()


def main():
    global PROFILE
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--provider", choices=("codex", "copilot", "grok"), default="codex")
    parser.add_argument("--profile-dir", type=Path, help="Stable companion state directory supplied by the plugin")
    parser.add_argument("--response-timeout-seconds", type=response_timeout_argument, default=None,
                        help="Response limit for bridge ask only (60-1800 seconds); default is 300, or 600 for High and above")
    parser.add_argument("mode", choices=["status", "login", "prove", "bridge"], default="status", nargs="?")
    parser.add_argument("bridge_action", nargs="?", choices=["status", "login", "logout", "test", "ask"])
    args = parser.parse_args()
    if args.response_timeout_seconds is not None and (args.mode != "bridge" or args.bridge_action != "ask"):
        parser.error("response-timeout-seconds is only valid for bridge ask")
    if args.profile_dir is not None:
        if not args.profile_dir.is_absolute():
            parser.error("profile-dir must be an absolute path")
        PROFILE = args.profile_dir.resolve()
    mode = args.mode
    bridge_action = args.bridge_action
    if mode == "bridge" and bridge_action is None:
        parser.error("bridge requires status, login, logout, test, or ask")
    cancel_event = threading.Event()
    requests = queue.Queue(maxsize=1)
    quest_bridge = QuestQueryBridge(cancel_event, emit)
    if mode == "bridge":
        sys.stdin.reconfigure(encoding="utf-8")
        def read_cancel():
            request_seen = False
            while True:
                try:
                    line = sys.stdin.readline(MAX_REQUEST_BYTES + 2)
                    if not line:
                        return
                    if len(line.encode("utf-8")) > MAX_REQUEST_BYTES + 1 or not line.endswith("\n"):
                        raise ValueError()
                    value = json.loads(line)
                    if value == {"cancel": True}:
                        cancel_event.set()
                        return
                    if bridge_action == "ask" and not request_seen and isinstance(value, dict) and set(value) == {"request"}:
                        requests.put_nowait(value["request"])
                        request_seen = True
                    elif bridge_action == "ask" and request_seen and isinstance(value, dict) and set(value) in ({"quest_result"}, {"inventory_result"}, {"history_result"}, {"game_context_result"}, {"npc_dialogue_result"}, {"character_details_result"}, {"personality_details_result"}, {"currency_result"}, {"duty_result"}, {"game_definitions_result"}, {"gc_supply_result"}, {"npc_target_result"}, {"storage_result"}, {"valuation_result"}, {"collection_result"}, {"progression_result"}, {"crafting_log_result"}, {"menus_result"}, {"activities_result"}):
                        quest_bridge.accept(value)
                    else:
                        raise ValueError()
                except (ValueError, UnicodeError, queue.Full, RuntimeError):
                    cancel_event.set()
                    return
        threading.Thread(target=read_cancel, daemon=True).start()
    client = None
    failure_stage = "provider.request"
    try:
        if mode == "bridge" and bridge_action == "logout" and args.provider == "grok":
            # This explicit button removes only CompAI's local encrypted vault.
            # Recovery must not require a healthy provider binary or network call.
            from credential_store import CredentialStore, CredentialStoreError
            failure_stage = "provider.authentication"
            if cancel_event.is_set():
                raise InterruptedError("Local sign-out cancelled")
            try:
                CredentialStore(PROFILE / "providers" / "grok", "grok").delete()
            except CredentialStoreError as exc:
                raise CompanionFailure("authentication", str(exc)) from None
            emit({"provider": "grok", "local_logout": True})
            return 0
        request = None
        if bridge_action == "ask":
            deadline = time.monotonic() + 10
            while time.monotonic() < deadline:
                if cancel_event.is_set():
                    raise RuntimeError("Question cancelled or invalid bridge input")
                try:
                    request = requests.get(timeout=.1)
                    break
                except queue.Empty:
                    continue
            else:
                raise RuntimeError("No approved inventory question received")
            prepared = normalize_request(request)
        failure_stage = "runtime.integrity"
        if request is not None and prepared.provider != args.provider:
            raise RuntimeError("Request provider differs from the explicit process selection; no fallback")
        if args.provider == "codex":
            client = Companion(cancel_event, web_search=prepared.web_search,
                               model=prepared.model, effort=prepared.effort) if request is not None else Companion(cancel_event)
        else:
            client = AccountCompanion(args.provider, cancel_event,
                model=prepared.model if request is not None else "auto",
                effort=prepared.effort if request is not None else "none",
                web_search=prepared.web_search if request is not None else False)
        failure_stage = "provider.request"
        if bridge_action == "ask":
            client.ask(request, quest_query=quest_bridge.query,
                       inventory_query=lambda arguments: quest_bridge.query(arguments, "inventory"),
                       history_query=lambda arguments: quest_bridge.query(arguments, "history"),
                       game_context_query=lambda arguments: quest_bridge.query(arguments, "game_context"),
                       npc_dialogue_query=lambda arguments: quest_bridge.query(arguments, "npc_dialogue"),
                       character_details_query=lambda arguments: quest_bridge.query(arguments, "character_details"),
                       personality_details_query=lambda arguments: quest_bridge.query(arguments, "personality_details"),
                       currency_query=lambda arguments: quest_bridge.query(arguments, "currency"),
                       duty_query=lambda arguments: quest_bridge.query(arguments, "duty"),
                       game_definitions_query=lambda arguments: quest_bridge.query(arguments, "game_definitions"),
                       gc_supply_query=lambda arguments: quest_bridge.query(arguments, "gc_supply"),
                       npc_target_query=lambda arguments: quest_bridge.query(arguments, "npc_target"),
                       storage_query=lambda arguments: quest_bridge.query(arguments, "storage"),
                       valuation_query=lambda arguments: quest_bridge.query(arguments, "valuation"),
                       collection_query=lambda arguments: quest_bridge.query(arguments, "collection"),
                       progression_query=lambda arguments: quest_bridge.query(arguments, "progression"),
                       crafting_log_query=lambda arguments: quest_bridge.query(arguments, "crafting_log"),
                       menu_query=lambda arguments: quest_bridge.query(arguments, "menus"),
                       activity_query=lambda arguments: quest_bridge.query(arguments, "activities"),
                       response_timeout_seconds=args.response_timeout_seconds, prepared=prepared)
        elif mode == "prove" or bridge_action == "test":
            client.prove(force=bridge_action == "test", save=bridge_action != "test")
        else:
            if mode == "login" or bridge_action == "login":
                # Codex opens directly outside bridge mode. Grok's flag instead
                # requests the plugin's browser handler via its login event;
                # suppressing it here would leave the device page unopened.
                client.login(open_browser=mode != "bridge" or args.provider == "grok")
                # Login has just validated or replaced the credentials. Do not immediately
                # rotate them again; still read account, quota and model readiness normally.
                client.status(refresh=False)
            elif bridge_action == "logout":
                client.logout()
                client.status()
            else:
                client.status()
    except Exception as exc:
        failure = failure_message(exc, failure_stage)
        emit(failure)
        # An allowance error must carry the server's reset deadline back to the UI.
        # This account read performs no inference and never retries the question.
        if (bridge_action == "ask" and client is not None and
                failure["error_kind"] == "quota"):
            try:
                client.status()
            except CompanionFailure as refresh_failure:
                if refresh_failure.kind == "authentication":
                    emit(failure_message(refresh_failure, "provider.request"))
            except Exception:
                pass
        return 1
    finally:
        if client:
            client.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
