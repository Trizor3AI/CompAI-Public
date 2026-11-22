"""Exercise actual pinned quest tools with synthetic loopback Responses, never a provider.

Only the provider/account gate is substituted. The bundled executable, catalog,
isolation flags, dynamic tool dispatch, and quest validation remain production code.
Saved evidence contains checks/counts, not request bodies, credentials, or game data.
"""
import argparse
import copy
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
import re
import tempfile
import threading

import companion
from codex_tool_policy import expected_nested_tools, policy_id, require_no_mcp_servers, resource_tools
from quest_lookup import INSTRUCTIONS, TOOL, SCOPE, validate_arguments, validate_result, validate_summary


ROOT = Path(__file__).resolve().parents[2]
EVIDENCE = ROOT / "bridge/.isolation-probe" / "quest-lookup-runtime.json"
QUESTION = "What should I do next?"
AUDIT = '''
const toolsSeen = Object.keys(tools).sort();
let shellDenied = false, patchDenied = false;
try { await tools.exec_command({cmd:"echo SYNTHETIC_QUEST_SHELL_SHOULD_NOT_RUN"}); } catch { shellDenied = true; }
try { await tools.apply_patch("*** Begin Patch\\n*** End Patch"); } catch { patchDenied = true; }
text({tag:"quest_runtime_audit",tools:toolsSeen,require:typeof require,process:typeof process,
      fetch:typeof fetch,shellDenied,patchDenied});
'''.strip()
QUERIES = [
    {"view": "active", "query": "", "ids": [], "offset": 0, "limit": 1},
    {"view": "active", "query": "", "ids": [], "offset": 1, "limit": 1},
    {"view": "all", "query": "", "ids": [102, 103, 999], "offset": 0, "limit": 50},
]


def check(condition, message):
    if not condition:
        raise AssertionError(message)


def temporary_profile(prefix):
    root = ROOT / ".test-tmp"
    root.mkdir(exist_ok=True)
    return tempfile.TemporaryDirectory(prefix=prefix, dir=root)


def discovery_script(definitions):
    return "text(await tools.discover_tools(" + json.dumps({"names": [tool["name"] for tool in definitions]}) + "));"


def invocation_script(name, arguments):
    return "text(await tools.invoke_tool(" + json.dumps({"name": name, "arguments": arguments}) + "));"


def check_registration(start, definitions):
    expected = ["discover_tools", "invoke_tool"] if definitions else []
    check(sorted(tool["name"] for tool in start["dynamicTools"]) == expected,
          "Unexpected dynamic tool registration")
    permitted = [tool["name"] for tool in definitions]
    for tool in start["dynamicTools"]:
        properties = tool["inputSchema"]["properties"]
        names_schema = properties["names"]["items"] if tool["name"] == "discover_tools" else properties["name"]
        check(sorted(names_schema["enum"]) == sorted(permitted), "Wrapper catalogue permissions changed")
    instructions = start["developerInstructions"]
    for tool in definitions:
        entry = json.dumps({"name": tool["name"], "description": tool["description"]},
                           ensure_ascii=False, separators=(",", ":"))
        check(entry in instructions, "Permitted capability missing from initial catalogue")
    return expected


def check_discovery_transport(objects, definitions, instructions):
    discoveries = [value for value in objects if set(value) == {"tools", "instructions"}]
    check(discoveries, "No discovery metadata transported")
    check(all(value["tools"] == definitions for value in discoveries), "Discovered reader schemas changed in transit")
    check(all(all(rule in value["instructions"] for rule in instructions) for value in discoveries),
          "Discovered reader usage rules changed in transit")


def strings(value):
    if isinstance(value, str):
        yield value
    elif isinstance(value, dict):
        for child in value.values():
            yield from strings(child)
    elif isinstance(value, list):
        for child in value:
            yield from strings(child)


def disabled_query_rejected(requests, failure):
    outputs = [item.get("output") for request in requests for item in request.get("input", [])
               if item.get("type") in ("function_call_output", "custom_tool_call_output")
               and item.get("call_id") == "call_disabled_quest"]
    text = "\n".join(strings(outputs)).lower()
    denied = any(word in text for word in ("unsupported", "unknown", "unrecognized", "denied", "not found", "not available"))
    return denied or failure is not None and "tool" in failure.lower()


def decoded_objects(value):
    """Unwrap code-mode text and dynamic-tool JSON without depending on presentation."""
    if isinstance(value, dict):
        yield value
        for child in value.values():
            yield from decoded_objects(child)
    elif isinstance(value, list):
        for child in value:
            yield from decoded_objects(child)
    elif isinstance(value, str):
        for line in value.splitlines():
            try:
                decoded = json.loads(line)
            except (ValueError, TypeError):
                continue
            if isinstance(decoded, (dict, list)):
                yield from decoded_objects(decoded)


def names(tools, prefix=""):
    result = []
    for tool in tools or []:
        name = tool.get("name") or tool.get("type", "unknown")
        if tool.get("type") == "namespace":
            result.extend(names(tool.get("tools"), prefix + name + "."))
        else:
            result.append(prefix + name)
    return result


def fixture():
    summary = {"captured_at_utc": datetime.now(timezone.utc).isoformat(), "stale": False,
               "scope": SCOPE, "catalog_rows": 4, "observed_quest_count": 4,
               "active_count": 2, "observed_completed_count": 1}
    rows = []
    for ident, name, accepted, completed in (
        (100, "PRIVATE_SYNTHETIC_ACTIVE_ALPHA", True, False),
        (101, "PRIVATE_SYNTHETIC_ACTIVE_BETA", True, False),
        (102, "PRIVATE_SYNTHETIC_PREREQUISITE", False, True),
        (103, "PRIVATE_SYNTHETIC_UNKNOWN", False, None),
    ):
        rows.append({"native_id": ident, "row_id": 65536 + ident, "name": name,
                     "name_truncated": False, "accepted": accepted, "completed": completed,
                     "sequence": 3 if accepted else None,
                     "journal_text": "PRIVATE_SYNTHETIC_JOURNAL" if accepted else None,
                     "journal_status": "included" if accepted else "unavailable"})
    return summary, rows


def run_case(case, model=None, effort="medium"):
    enabled, web = case != "disabled", case == "enabled-web"
    selected_model = model or companion.MODEL
    selected_metadata = next(value for value in json.loads(companion.CATALOG.read_text(encoding="utf-8"))["models"]
                             if value.get("slug") == selected_model)
    has_clock = "clock" in selected_metadata.get("experimental_supported_tools", [])
    summary, rows = fixture()
    requests, callback_calls, callback_results, emissions, rpc_calls = [], [], [], [], []
    server_errors = []
    audit_disabled = False

    def quest_query(arguments):
        check(enabled, "Disabled lookup invoked the plugin callback")
        check(arguments == QUERIES[len(callback_calls)], "Runtime changed or reordered query arguments")
        validate_arguments(arguments)
        callback_calls.append(copy.deepcopy(arguments))
        selected = [row for row in rows if
                    (arguments["view"] != "active" or row["accepted"]) and
                    (arguments["view"] != "completed" or row["completed"] is True) and
                    (not arguments["ids"] or row["native_id"] in arguments["ids"])]
        offset, limit = arguments["offset"], arguments["limit"]
        page = selected[offset:offset + limit]
        result = {**summary, "total_matches": len(selected), "offset": offset,
                  "next_offset": offset + len(page) if offset + len(page) < len(selected) else None,
                  "missing_ids": [ident for ident in arguments["ids"] if ident not in {row["native_id"] for row in rows}],
                  "rows": copy.deepcopy(page)}
        validate_result(result, validate_summary(summary), arguments)
        callback_results.append(result)
        return result

    scripts = [AUDIT, discovery_script([TOOL])] + [invocation_script("query_quests", query) for query in QUERIES]

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def do_POST(self):
            try:
                check(self.path == "/v1/responses", "Unexpected loopback endpoint")
                check(not self.headers.get("Authorization"), "Loopback request unexpectedly carried authorization")
                request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                requests.append(request)
                number = len(requests)
                check(number <= 6, "Unexpected extra model iterations")
                if enabled and number <= len(scripts):
                    item = {"type": "custom_tool_call", "id": f"ct_quest_{number}",
                            "call_id": f"call_quest_{number}", "name": "exec", "input": scripts[number - 1]}
                elif not enabled and audit_disabled and number == 1:
                    item = {"type": "custom_tool_call", "id": "ct_disabled_audit",
                            "call_id": "call_disabled_audit", "name": "exec", "input": AUDIT}
                elif not enabled and number == (2 if audit_disabled else 1):
                    item = {"type": "function_call", "id": "fc_disabled_quest",
                            "call_id": "call_disabled_quest", "name": "query_quests",
                            "arguments": json.dumps(QUERIES[0])}
                else:
                    item = {"type": "message", "id": f"msg_quest_{number}", "role": "assistant",
                            "content": [{"type": "output_text", "text": json.dumps({"answer": "Synthetic lookup check completed."})}]}
                output = [item]
                if web and number == len(scripts) + 1:
                    # This simulates a hosted search completion, not a real web lookup.
                    output.insert(0, {"type": "web_search_call", "id": "ws_quest_synthetic", "status": "completed",
                                      "action": {"type": "search", "query": "FFXIV public prerequisite rules"}})
                response_id = f"resp_quest_{case}_{number}"
                events = [{"type": "response.created", "response": {"id": response_id}},
                          *({"type": "response.output_item.done", "output_index": index, "item": value}
                            for index, value in enumerate(output)),
                          {"type": "response.completed", "response": {
                              "id": response_id, "status": "completed", "output": output,
                              "usage": {"input_tokens": 0, "output_tokens": 0, "total_tokens": 0,
                                        "input_tokens_details": {"cached_tokens": 0}}}}]
                body = "".join("data: " + json.dumps(event) + "\n\n" for event in events).encode("utf-8")
                self.send_response(200)
                self.send_header("Content-Type", "text/event-stream")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
            except Exception as exc:
                server_errors.append(str(exc))
                self.send_error(500, "Synthetic harness rejected request")

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    original_profile, original_config, original_emit = companion.PROFILE, companion.CONFIG, companion.emit
    provider = ('model_providers.ffxiv_quest_mock={name="Quest loopback check",'
                f'base_url="http://127.0.0.1:{server.server_port}/v1",wire_api="responses",'
                'requires_openai_auth=false,supports_websockets=false,supports_standalone_web_search=true,'
                'request_max_retries=0,stream_max_retries=0}')

    class LocalCompanion(companion.Companion):
        def preflight(self):
            config = self.call("config/read", {"cwd": str(companion.PROFILE / "work"), "includeLayers": False})["config"]
            expected = {"model_provider": "ffxiv_quest_mock", "model": selected_model,
                        "forced_login_method": "chatgpt", "approval_policy": "never",
                        "sandbox_mode": "read-only", "web_search": "live" if web else "disabled"}
            for key, value in expected.items():
                check(config.get(key) == value, "Unexpected effective loopback setting: " + key)
            check(config.get("features", {}).get("unbounded_connection_retries") is False,
                  "Unbounded connection retries were not disabled")
            listed = self.call("model/list", {"includeHidden": False, "limit": 100}).get("data", [])
            selected = next((value for value in listed if value.get("model") == selected_model), None)
            check(selected is not None and effort in
                  [value.get("reasoningEffort") for value in selected.get("supportedReasoningEfforts", [])],
                  "Selected model/effort missing from the actual runtime catalog")
            check(self.account()["type"] is None, "Fresh test profile unexpectedly authenticated")
            return {"primary": {"usedPercent": 0, "resetsAt": None}}

        def call(self, method, params=None, **kwargs):
            rpc_calls.append((method, copy.deepcopy(params)))
            if method == "thread/start":
                check(params["modelProvider"] == "openai", "Production thread provider unexpectedly changed")
                params = {**params, "modelProvider": "ffxiv_quest_mock"}
            if method == "turn/start" and resource_tools(self.pin):
                require_no_mcp_servers(self.call, params["threadId"])
                self.mcp_servers_empty_before_turn = True
            reply = super().call(method, params, **kwargs)
            if method == "thread/start":
                self.guard_thread_id = reply["thread"]["id"]
            return reply

    result = {"case": case, "model": selected_model, "effort": effort, "passed": False}
    client = None
    try:
        with temporary_profile("quest-runtime-") as temporary:
            companion.PROFILE = Path(temporary)
            companion.CONFIG = [value for value in original_config if not value.startswith(("model_provider=", "cli_auth_credentials_store="))]
            companion.CONFIG += ['model_provider="ffxiv_quest_mock"', provider, 'cli_auth_credentials_store="ephemeral"']
            companion.emit = emissions.append
            try:
                selection = {"model": model, "effort": effort} if model is not None else {}
                client = LocalCompanion(web_search=web, **selection)
                audit_disabled = bool(resource_tools(client.pin))
                result["runtime"] = {key: client.pin[key] for key in
                                     ("codex_version", "binary_sha256", "catalog_sha256", "runtime_files")}
                result.update(policy_id=policy_id(client.pin), clock_tool_expected=has_clock)
                failure = None
                try:
                    client.ask({"version": 7 if model is not None else 6,
                                "question": QUESTION, "snapshot": None, "history": [],
                                "web_search": web, "quest_lookup": summary if enabled else None,
                                **({"model": selected_model, "reasoning_effort": effort} if model is not None else {})},
                               quest_query=quest_query)
                except Exception as exc:
                    failure = str(exc)
                if audit_disabled:
                    require_no_mcp_servers(client.call, client.guard_thread_id)
                    check(getattr(client, "mcp_servers_empty_before_turn", False), "MCP zero-server guard did not precede the turn")
                    result["mcp_servers_empty"] = True
                check(not server_errors, "; ".join(server_errors))
                check(requests, "No request reached the loopback provider: " + str(failure))
                check(all(request.get("model") == selected_model for request in requests), "Unexpected model fallback")
                check(all(request.get("reasoning", {}).get("effort") == effort for request in requests),
                      "Unexpected provider reasoning effort: " + repr(sorted({
                          str(request.get("reasoning", {}).get("effort")) for request in requests})))
                starts = [params for method, params in rpc_calls if method == "thread/start"]
                check(len(starts) == 1, "Expected one ephemeral thread")
                check(starts[0]["model"] == selected_model and starts[0]["allowProviderModelFallback"] is False,
                      "Thread selection or no-fallback policy changed")
                turns = [params for method, params in rpc_calls if method == "turn/start"]
                check(len(turns) == 1 and turns[0]["model"] == selected_model and turns[0]["effort"] == effort,
                      "Turn selection changed")
                exposed = check_registration(starts[0], [TOOL] if enabled else [])
                check(INSTRUCTIONS not in starts[0]["developerInstructions"], "Eager quest rules bypass discovery")
                initial = "\n".join(strings(requests[0]))
                check("PRIVATE_SYNTHETIC_" not in initial and '\"rows\"' not in initial,
                      "Full quest data leaked into the initial prompt")
                context_blocks = [value for value in strings(requests[0]) if value.startswith("Approved context and dated reference")]
                check(len(context_blocks) == 1, "Missing approved context block")
                context = json.loads(context_blocks[0].split("\n", 1)[1])
                check(context.get("inventory") is None and context.get("history") == [], "Unrelated inventory/history attached")
                check(context.get("quest_lookup") == (validate_summary(summary) if enabled else None),
                      "Initial lookup summary changed or was omitted")
                tool_outputs = [item.get("output") for request in requests for item in request.get("input", [])
                                if item.get("type") in ("custom_tool_call_output", "function_call_output")]
                objects = list(decoded_objects(tool_outputs))
                direct_tools = set(names(requests[0].get("tools")))
                additional = set(name for item in requests[0].get("input", []) if item.get("type") == "additional_tools"
                                 for name in names(item.get("tools")))
                check(not direct_tools, "Unexpected directly registered provider tools: " + repr(sorted(direct_tools)))
                allowed_wrappers = {"functions.exec", "functions.wait", "functions.request_user_input",
                                    "functions.request_user_input_async", "clock.sleep"}
                check(additional <= allowed_wrappers, "Unexpected executable wrapper tool")
                if enabled or audit_disabled:
                    audits = [value for value in objects if value.get("tag") == "quest_runtime_audit"]
                    check(audits, "No audit executed in the real code-mode host")
                    expected_tools = expected_nested_tools(client.pin, dynamic=exposed, web_search=web)
                    if not has_clock:
                        expected_tools.remove("clock__curr_time")
                    for audit in audits:
                        check(audit["tools"] == expected_tools, "Unexpected nested runtime tools: " + repr(audit["tools"]))
                        check(all(audit.get(key) == "undefined" for key in ("require", "process", "fetch")),
                              "Code host exposed external I/O globals")
                        check(audit.get("shellDenied") is True and audit.get("patchDenied") is True,
                              "Shell or patch call was not denied")
                    result.update(nested_tools=expected_tools, shell_and_patch_denied=True)
                if enabled:
                    check(failure is None, "Enabled lookup failed: " + str(failure))
                    check(len(requests) == len(scripts) + 1, "Did not execute every simulated tool turn")
                    check(callback_calls == QUERIES, "Expected pagination and follow-up ID lookup")
                    check_discovery_transport(objects, [TOOL], [INSTRUCTIONS])
                    transported = [value for value in objects if value.get("scope") == SCOPE and "rows" in value]
                    check(all(expected in transported for expected in callback_results), "Quest results did not reach the next model iteration intact")
                    states = [row["completed"] for expected in callback_results for row in expected["rows"]]
                    check(False in states and True in states and None in states, "Tri-state completion was not transported")
                    check(callback_results[0]["next_offset"] == 1 and callback_results[1]["next_offset"] is None,
                          "Pagination boundary was not exercised")
                    check(callback_results[-1]["missing_ids"] == [999] and "102" not in QUESTION,
                          "Unmentioned prerequisite/missing-ID lookup not exercised")
                    answers = [event for event in emissions if "question_answer" in event]
                    check(len(answers) == 1 and answers[0].get("search_performed") is web, "Missing final answer/search verification")
                    if web:
                        check(any(event.get("question_state") == "searched" for event in emissions), "Simulated web completion not recognized")
                    result.update(nested_tools=expected_tools, lookup_calls=len(callback_calls),
                                  pagination_checked=True, unmentioned_id_lookup_checked=True,
                                  completion_states_checked=[False, True, None], shell_and_patch_denied=True,
                                  web_completion_simulated=web, discovery_metadata_intact=True)
                else:
                    check(not callback_calls, "Disabled query reached the plugin callback")
                    declaration = re.compile(r"declare const tools:\s*\{\s*query_quests\(")
                    check(not declaration.search(initial), "Disabled quest tool was declared")
                    check(disabled_query_rejected(requests, failure), "No evidence that the injected disabled tool was denied")
                    result.update(disabled_tool_absent=True, disabled_callback_calls=0, injected_tool_denied=True)
                result.update(passed=True, model_requests=len(requests), initial_summary_only=True,
                              unbounded_connection_retries_disabled=True,
                              credential_free=True, unexpected_wrappers=False,
                              exact_model_and_effort=True, provider_fallback_disabled=True)
            finally:
                if client is not None:
                    client.close()
    except Exception as exc:
        result["error"] = str(exc)
    finally:
        companion.PROFILE, companion.CONFIG, companion.emit = original_profile, original_config, original_emit
        server.shutdown()
        server.server_close()
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--case", choices=("enabled", "enabled-web", "disabled"), action="append")
    parser.add_argument("--model", help="Explicit validated model; never falls back")
    parser.add_argument("--effort", default="medium")
    args = parser.parse_args()
    cases = [run_case(case, args.model, args.effort) for case in args.case or ("enabled", "enabled-web", "disabled")]
    policies = {case.get("policy_id") for case in cases}
    evidence = {"schema_version": 2 if policies == {"empty_mcp_resources_v1"} else 1,
                "policy_id": next(iter(policies)) if len(policies) == 1 else None,
                "checked_at": datetime.now(timezone.utc).isoformat(),
                "route": "loopback Responses only; no credentials, game data, provider or web requests",
                "all_passed": all(case["passed"] for case in cases), "cases": cases}
    EVIDENCE.write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
    for case in cases:
        print(("PASS " if case["passed"] else "FAIL ") + case["case"] +
              ("" if case["passed"] else ": " + case.get("error", "unknown error")))
    print("Evidence: " + str(EVIDENCE))
    return 0 if evidence["all_passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
