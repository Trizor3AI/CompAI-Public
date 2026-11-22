"""Pinned-runtime inventory/quest dispatch proof using synthetic loopback only.

No credentials, real game data, provider calls or public web lookups. The runtime,
tool registration, isolation flags and result validators remain production code.
"""
import copy
from contextlib import ExitStack
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
import threading

import companion
import inventory_lookup as inventory
import quest_lookup as quests
from quest_lookup_runtime_check import (check, check_discovery_transport, check_registration, decoded_objects,
    discovery_script, fixture as quest_fixture, invocation_script, names, strings, temporary_profile)


EVIDENCE = Path(__file__).resolve().parents[2] / "bridge/.isolation-probe" / "inventory-lookup-runtime.json"
AUDIT = '''
let shellDenied=false, patchDenied=false;
try { await tools.exec_command({cmd:"echo SYNTHETIC_INVENTORY_SHOULD_NOT_RUN"}); } catch { shellDenied=true; }
try { await tools.apply_patch("*** Begin Patch\\n*** End Patch"); } catch { patchDenied=true; }
text({tag:"inventory_runtime_audit",tools:Object.keys(tools).sort(),require:typeof require,
      process:typeof process,fetch:typeof fetch,shellDenied,patchDenied});
'''.strip()
CARD_QUERY = {"view": "pedigrees", "query": "", "ids": [], "bags": [], "offset": 0, "limit": 50}
ITEM_QUERY = {"view": "items", "query": "PRIVATE_SYNTHETIC_TOKEN", "ids": [], "bags": [], "offset": 0, "limit": 50}
QUEST_QUERY = {"view": "active", "query": "", "ids": [], "offset": 0, "limit": 50}


def inventory_fixture():
    captured = datetime.now(timezone.utc) - timedelta(seconds=5)
    pedigree = {"source": "ItemDetail", "status": "user_verified_tooltip",
                "observed_at_utc": (captured - timedelta(seconds=1)).isoformat(),
                "kind": "covering_permission", "grade": 6, "sex": "female",
                "father_stars": [4, 3, 2, 1, 4], "mother_stars": [1, 2, 3, 4, 1]}
    rows = []
    for slot in range(3):
        detail = copy.deepcopy(pedigree) if slot < 2 else None
        if slot == 1:
            detail["father_stars"] = [1, 4, 2, 4, 3]
        rows.append({"bag": "Inventory1", "slot": slot, "item_id": 9611, "base_item_id": 9611,
                     "name": "PRIVATE_SYNTHETIC_CARD", "quantity": 1, "is_hq": False, "pedigree": detail})
    for slot, quantity in ((0, 3), (1, 2)):
        rows.append({"bag": "Inventory2", "slot": slot, "item_id": 100, "base_item_id": 100,
                     "name": "PRIVATE_SYNTHETIC_TOKEN", "quantity": quantity, "is_hq": False, "pedigree": None})
    summary = {"captured_at_utc": captured.isoformat(), "changed": False, "scope": inventory.SCOPE,
               "item_slot_count": 5, "verified_pedigree_count": 2,
               "containers": [{"name": f"Inventory{index}", "slot_count": 35,
                               "unavailable_reason": None, "item_count": {1: 3, 2: 2}.get(index, 0)}
                              for index in range(1, 5)]}
    return summary, rows


def run_case(enabled):
    case = "both-enabled" if enabled else "inventory-disabled-quest-enabled"
    inv_summary, inv_rows = inventory_fixture()
    quest_summary, quest_rows = quest_fixture()
    requests, callbacks, returned, rpc_calls, errors, emissions = [], [], [], [], [], []

    def inventory_query(arguments):
        check(enabled, "Disabled inventory reached the host callback")
        check(arguments in (CARD_QUERY, ITEM_QUERY), "Unexpected inventory query")
        inventory.validate_arguments(arguments)
        callbacks.append(("query_inventory", copy.deepcopy(arguments)))
        selected = inv_rows[:3] if arguments == CARD_QUERY else inv_rows[3:]
        result = {**inv_summary, "total_matches": len(selected),
                  "total_quantity": sum(row["quantity"] for row in selected),
                  "offset": 0, "next_offset": None, "rows": copy.deepcopy(selected)}
        inventory.validate_result(result, inventory.validate_summary(inv_summary), arguments)
        returned.append(result)
        return result

    def quest_query(arguments):
        check(arguments == QUEST_QUERY, "Unexpected quest query")
        callbacks.append(("query_quests", copy.deepcopy(arguments)))
        rows = [copy.deepcopy(row) for row in quest_rows if row["accepted"]]
        result = {**quest_summary, "total_matches": len(rows), "offset": 0,
                  "next_offset": None, "missing_ids": [], "rows": rows}
        quests.validate_result(result, quests.validate_summary(quest_summary), arguments)
        returned.append(result)
        return result

    definitions = [quests.TOOL] + ([inventory.TOOL] if enabled else [])
    scripts = [AUDIT, discovery_script(definitions), invocation_script("query_quests", QUEST_QUERY)]
    if enabled:
        scripts += [invocation_script("query_inventory", query) for query in (CARD_QUERY, ITEM_QUERY)]

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def do_POST(self):
            try:
                check(self.path == "/v1/responses", "Unexpected loopback endpoint")
                check(not self.headers.get("Authorization"), "Unexpected authorization on loopback")
                request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                requests.append(request)
                number = len(requests)
                check(number <= 6, "Unexpected extra model iterations")
                if number <= len(scripts):
                    item = {"type": "custom_tool_call", "id": f"ct_inventory_{number}",
                            "call_id": f"call_inventory_{number}", "name": "exec", "input": scripts[number - 1]}
                elif not enabled and number == len(scripts) + 1:
                    item = {"type": "function_call", "id": "fc_disabled_inventory",
                            "call_id": "call_disabled_inventory", "name": "query_inventory",
                            "arguments": json.dumps(CARD_QUERY)}
                else:
                    item = {"type": "message", "id": f"msg_inventory_{number}", "role": "assistant",
                            "content": [{"type": "output_text", "text": json.dumps({"answer": "Synthetic check complete."})}]}
                response_id = f"resp_inventory_{number}"
                events = [{"type": "response.created", "response": {"id": response_id}},
                          {"type": "response.output_item.done", "output_index": 0, "item": item},
                          {"type": "response.completed", "response": {
                              "id": response_id, "status": "completed", "output": [item],
                              "usage": {"input_tokens": 0, "output_tokens": 0, "total_tokens": 0,
                                        "input_tokens_details": {"cached_tokens": 0}}}}]
                body = "".join("data: " + json.dumps(event) + "\n\n" for event in events).encode("utf-8")
                self.send_response(200)
                self.send_header("Content-Type", "text/event-stream")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
            except Exception as exc:
                errors.append(str(exc))
                self.send_error(500, "Synthetic harness rejected request")

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    original_profile, original_config, original_emit = companion.PROFILE, companion.CONFIG, companion.emit
    provider = ('model_providers.ffxiv_inventory_mock={name="Inventory loopback check",'
                f'base_url="http://127.0.0.1:{server.server_port}/v1",wire_api="responses",'
                'requires_openai_auth=false,supports_websockets=false,'
                'request_max_retries=0,stream_max_retries=0}')

    class LocalCompanion(companion.Companion):
        def preflight(self):
            config = self.call("config/read", {"cwd": str(companion.PROFILE / "work"), "includeLayers": False})["config"]
            for key, value in {"model_provider": "ffxiv_inventory_mock", "model": companion.MODEL,
                               "forced_login_method": "chatgpt", "approval_policy": "never",
                               "sandbox_mode": "read-only", "web_search": "disabled"}.items():
                check(config.get(key) == value, "Unexpected loopback setting: " + key)
            check(self.account()["type"] is None, "Fresh test profile unexpectedly authenticated")
            return {"primary": {"usedPercent": 0, "resetsAt": None}}

        def call(self, method, params=None, **kwargs):
            rpc_calls.append((method, copy.deepcopy(params)))
            if method == "thread/start":
                check(params["modelProvider"] == "openai", "Production provider unexpectedly changed")
                params = {**params, "modelProvider": "ffxiv_inventory_mock"}
            return super().call(method, params, **kwargs)

    result, client = {"case": case, "passed": False}, None
    try:
        with temporary_profile("inventory-runtime-") as temporary, ExitStack() as lifetime:
            companion.PROFILE = Path(temporary)
            companion.CONFIG = [value for value in original_config if not value.startswith(("model_provider=", "cli_auth_credentials_store="))]
            companion.CONFIG += ['model_provider="ffxiv_inventory_mock"', provider, 'cli_auth_credentials_store="ephemeral"']
            companion.emit = emissions.append
            client = LocalCompanion(web_search=False)
            lifetime.callback(client.close)
            result["runtime"] = {key: client.pin[key] for key in
                                 ("codex_version", "binary_sha256", "catalog_sha256", "runtime_files")}
            failure = None
            try:
                client.ask({"version": 8, "question": "What should I do next?", "snapshot": None,
                            "history": [], "web_search": False, "model": companion.MODEL,
                            "reasoning_effort": companion.DEFAULT_EFFORT, "quest_lookup": quest_summary,
                            "inventory_lookup": inv_summary if enabled else None},
                           quest_query=quest_query, inventory_query=inventory_query)
            except Exception as exc:
                failure = str(exc)
            check(not errors, "; ".join(errors))
            check(requests, "No loopback request: " + str(failure))
            starts = [params for method, params in rpc_calls if method == "thread/start"]
            check(len(starts) == 1, "Expected one ephemeral thread")
            expected_tools = check_registration(starts[0], definitions)
            check(all(rule not in starts[0]["developerInstructions"] for rule in (quests.INSTRUCTIONS, inventory.INSTRUCTIONS)),
                  "Eager reader rules bypass discovery")
            check(starts[0]["allowProviderModelFallback"] is False, "Fallback unexpectedly allowed")
            check(all(request.get("model") == companion.MODEL for request in requests), "Unexpected model")
            initial = "\n".join(strings(requests[0]))
            check("PRIVATE_SYNTHETIC_" not in initial and '"rows"' not in initial, "Private rows leaked into initial prompt")
            blocks = [value for value in strings(requests[0]) if value.startswith("Approved context and dated reference")]
            check(len(blocks) == 1, "Missing approved context block")
            context = json.loads(blocks[0].split("\n", 1)[1])
            check(context.get("inventory") is None and context.get("history") == [], "Unexpected full inventory/history")
            check(context.get("quest_lookup") == quests.validate_summary(quest_summary), "Quest summary mismatch")
            check(context.get("inventory_lookup") == (inventory.validate_summary(inv_summary) if enabled else None),
                  "Inventory summary mismatch")
            outputs = [item.get("output") for request in requests for item in request.get("input", [])
                       if item.get("type") in ("custom_tool_call_output", "function_call_output")]
            objects = list(decoded_objects(outputs))
            audits = [value for value in objects if value.get("tag") == "inventory_runtime_audit"]
            check(audits, "No real code-mode audit")
            catalog = json.loads(companion.CATALOG.read_text(encoding="utf-8"))["models"]
            metadata = next(value for value in catalog if value.get("slug") == companion.MODEL)
            nested_tools = sorted(expected_tools + (["clock__curr_time"] if "clock" in metadata.get("experimental_supported_tools", []) else []))
            for audit in audits:
                check(audit["tools"] == nested_tools, "Unexpected nested tools: " + repr(audit["tools"]))
                check(all(audit.get(key) == "undefined" for key in ("require", "process", "fetch")), "External I/O exposed")
                check(audit.get("shellDenied") and audit.get("patchDenied"), "Shell/patch not denied")
            check_discovery_transport(objects, definitions,
                                      [quests.INSTRUCTIONS] + ([inventory.INSTRUCTIONS] if enabled else []))
            check(not names(requests[0].get("tools")), "Unexpected direct provider tools")
            wrappers = set(name for item in requests[0].get("input", []) if item.get("type") == "additional_tools"
                           for name in names(item.get("tools")))
            check(wrappers <= {"functions.exec", "functions.wait", "functions.request_user_input",
                               "functions.request_user_input_async", "clock.sleep"}, "Unexpected wrapper")
            transported = [value for value in objects if value.get("scope") in (quests.SCOPE, inventory.SCOPE) and "rows" in value]
            check(all(value in transported for value in returned), "Private callback results not transported intact")
            check(callbacks.count(("query_quests", QUEST_QUERY)) == 1, "Quest lookup did not coexist")
            if enabled:
                check(failure is None, "Enabled request failed: " + str(failure))
                check(callbacks == [("query_quests", QUEST_QUERY), ("query_inventory", CARD_QUERY), ("query_inventory", ITEM_QUERY)],
                      "Wrong query order or count")
                cards = next(value["rows"] for value in returned if value.get("total_matches") == 3)
                check(cards[0]["item_id"] == cards[1]["item_id"] and
                      cards[0]["pedigree"]["father_stars"] != cards[1]["pedigree"]["father_stars"] and
                      cards[2]["pedigree"] is None, "Card instances or unknown pedigree lost")
                check(returned[-1]["total_quantity"] == 5, "Deterministic stack total changed")
                check(len([event for event in emissions if "question_answer" in event]) == 1, "Final answer missing")
                result.update(bulk_card_identity_and_unknown_checked=True, deterministic_count_checked=True)
            else:
                check(not any(name == "query_inventory" for name, _ in callbacks), "Disabled inventory callback reached")
                output_text = "\n".join(strings(outputs)).lower()
                denied = any(word in output_text for word in ("unsupported", "unknown", "unrecognized", "denied", "not found", "not available"))
                check(denied or failure is not None and "tool" in failure.lower(), "Injected disabled tool not denied")
                result.update(disabled_inventory_absent=True, injected_inventory_tool_denied=True)
            result.update(passed=True, model_requests=len(requests), lookup_calls=len(callbacks),
                          nested_tools=nested_tools, summary_only_initial=True, callback_results_intact=True,
                          shell_and_patch_denied=True, credential_free=True, discovery_metadata_intact=True)
    except Exception as exc:
        result["error"] = str(exc)
    finally:
        companion.PROFILE, companion.CONFIG, companion.emit = original_profile, original_config, original_emit
        server.shutdown()
        server.server_close()
    return result


def main():
    cases = [run_case(enabled) for enabled in (True, False)]
    evidence = {"schema_version": 1, "checked_at": datetime.now(timezone.utc).isoformat(),
                "route": "synthetic loopback Responses only; no credentials, game data or external requests",
                "all_passed": all(case["passed"] for case in cases), "cases": cases}
    EVIDENCE.write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
    for case in cases:
        print(("PASS " if case["passed"] else "FAIL ") + case["case"] +
              ("" if case["passed"] else ": " + case.get("error", "unknown error")))
    print("Evidence: " + str(EVIDENCE))
    return 0 if evidence["all_passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
