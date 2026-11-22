"""Two pinned-runtime memory-tool cases, synthetic loopback only and no credentials."""
import copy
from contextlib import ExitStack
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
import threading

import companion
import chat_history_lookup as history
from chat_history_lookup_check import arguments, request, result, summary
from quest_lookup_runtime_check import (check, check_discovery_transport, check_registration, decoded_objects,
    discovery_script, invocation_script, names, strings, temporary_profile)

EVIDENCE = Path(__file__).resolve().parents[2] / "bridge/.isolation-probe" / "chat-history-runtime.json"
AUDIT = '''
let shellDenied=false, patchDenied=false;
try { await tools.exec_command({cmd:"echo SYNTHETIC_HISTORY_SHOULD_NOT_RUN"}); } catch { shellDenied=true; }
try { await tools.apply_patch("*** Begin Patch\\n*** End Patch"); } catch { patchDenied=true; }
text({tag:"chat_history_runtime_audit",tools:Object.keys(tools).sort(),require:typeof require,
      process:typeof process,fetch:typeof fetch,shellDenied,patchDenied});
'''.strip()


def run_case(enabled):
    requests, callbacks, rpc_calls, errors, emissions = [], [], [], [], []
    payload = request(chat_notes="Synthetic preference only.", history_lookup=summary() if enabled else None)

    def query(args):
        check(enabled and args == arguments(), "Disabled or unexpected history callback")
        callbacks.append(copy.deepcopy(args))
        return result()

    scripts = [AUDIT] + ([discovery_script([history.TOOL]), invocation_script("query_chat_history", arguments())]
                         if enabled else [])

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def do_POST(self):
            try:
                check(self.path == "/v1/responses" and not self.headers.get("Authorization"),
                      "Unexpected endpoint or authorization")
                requests.append(json.loads(self.rfile.read(int(self.headers["Content-Length"]))))
                number = len(requests)
                check(number <= 4, "Unexpected extra model iterations")
                if number <= len(scripts):
                    script = scripts[number - 1]
                    item = {"type": "custom_tool_call", "id": f"ct_memory_{number}",
                            "call_id": f"call_memory_{number}", "name": "exec", "input": script}
                elif number == 2:
                    item = {"type": "function_call", "id": "fc_disabled_history", "call_id": "call_disabled_history",
                            "name": "query_chat_history", "arguments": json.dumps(arguments())}
                else:
                    item = {"type": "message", "id": "msg_memory", "role": "assistant", "content": [
                        {"type": "output_text", "text": json.dumps({"answer": "Synthetic check complete."})}]}
                ident = f"resp_memory_{number}"
                events = [{"type": "response.created", "response": {"id": ident}},
                          {"type": "response.output_item.done", "output_index": 0, "item": item},
                          {"type": "response.completed", "response": {"id": ident, "status": "completed", "output": [item],
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
    provider = ('model_providers.ffxiv_memory_mock={name="Memory loopback check",'
                f'base_url="http://127.0.0.1:{server.server_port}/v1",wire_api="responses",'
                'requires_openai_auth=false,supports_websockets=false,request_max_retries=0,stream_max_retries=0}')

    class LocalCompanion(companion.Companion):
        def preflight(self):
            config = self.call("config/read", {"cwd": str(companion.PROFILE / "work"), "includeLayers": False})["config"]
            for key, value in {"model_provider": "ffxiv_memory_mock", "model": companion.MODEL,
                               "forced_login_method": "chatgpt", "approval_policy": "never",
                               "sandbox_mode": "read-only", "web_search": "disabled"}.items():
                check(config.get(key) == value, "Unexpected loopback setting: " + key)
            check(self.account()["type"] is None, "Test profile unexpectedly authenticated")
            return {"primary": {"usedPercent": 0, "resetsAt": None}}

        def call(self, method, params=None, **kwargs):
            rpc_calls.append((method, copy.deepcopy(params)))
            if method == "thread/start":
                check(params["modelProvider"] == "openai", "Production provider changed")
                params = {**params, "modelProvider": "ffxiv_memory_mock"}
            return super().call(method, params, **kwargs)

    evidence = {"case": "history-enabled" if enabled else "history-disabled", "passed": False}
    try:
        with temporary_profile("history-runtime-") as temporary, ExitStack() as lifetime:
            companion.PROFILE = Path(temporary)
            companion.CONFIG = [value for value in original_config if not value.startswith(("model_provider=", "cli_auth_credentials_store="))]
            companion.CONFIG += ['model_provider="ffxiv_memory_mock"', provider, 'cli_auth_credentials_store="ephemeral"']
            companion.emit = emissions.append
            client = LocalCompanion(web_search=False)
            lifetime.callback(client.close)
            evidence["runtime"] = {key: client.pin[key] for key in ("codex_version", "binary_sha256", "catalog_sha256", "runtime_files")}
            failure = None
            try:
                client.ask(payload, history_query=query)
            except Exception as exc:
                failure = str(exc)
            check(not errors, "; ".join(errors))
            check(requests, "No loopback request: " + str(failure))
            starts = [params for method, params in rpc_calls if method == "thread/start"]
            check(len(starts) == 1, "Expected one ephemeral thread")
            exposed = check_registration(starts[0], [history.TOOL] if enabled else [])
            check(history.INSTRUCTIONS not in starts[0]["developerInstructions"], "Eager history rules bypass discovery")
            check(starts[0]["allowProviderModelFallback"] is False, "Provider fallback permitted")
            check(all(item.get("model") == companion.MODEL for item in requests), "Unexpected model")
            initial = "\n".join(strings(requests[0]))
            check("PRIVATE OLD" not in initial, "Older messages leaked into initial request")
            blocks = [value for value in strings(requests[0]) if value.startswith("Approved context and dated reference")]
            check(len(blocks) == 1, "Missing context block")
            context = json.loads(blocks[0].split("\n", 1)[1])
            check(context.get("history_lookup") == payload["history_lookup"], "Summary mismatch")
            check(context.get("chat_notes") == payload["chat_notes"], "Notes lost")
            check(not names(requests[0].get("tools")), "Unexpected direct provider tools")
            outputs = [item.get("output") for response in requests for item in response.get("input", [])
                       if item.get("type") in ("custom_tool_call_output", "function_call_output")]
            objects = list(decoded_objects(outputs))
            audits = [value for value in objects if value.get("tag") == "chat_history_runtime_audit"]
            check(audits, "Missing code-mode audit")
            catalog = json.loads(companion.CATALOG.read_text(encoding="utf-8"))["models"]
            model = next(value for value in catalog if value.get("slug") == companion.MODEL)
            expected = sorted(exposed +
                              (["clock__curr_time"] if "clock" in model.get("experimental_supported_tools", []) else []))
            for audit in audits:
                check(audit["tools"] == expected, "Unexpected nested tools")
                check(all(audit.get(key) == "undefined" for key in ("require", "process", "fetch")), "External I/O exposed")
                check(audit.get("shellDenied") and audit.get("patchDenied"), "Shell or patch exposed")
            if enabled:
                check(failure is None and callbacks == [arguments()], "Enabled lookup failed: " + str(failure))
                check_discovery_transport(objects, [history.TOOL], [history.INSTRUCTIONS])
                check(result() in objects, "History result changed in transit")
                answers = [item for item in emissions if "question_answer" in item]
                check(len(answers) == 1 and "1 older chat lookups" in answers[0]["question_context"], "Reply metadata missing")
                evidence["discovery_metadata_intact"] = True
            else:
                check(not callbacks, "Disabled callback invoked")
                text = "\n".join(strings(outputs)).lower()
                denied = any(word in text for word in ("unsupported", "unknown", "unrecognized", "denied", "not found", "not available"))
                check(denied or failure is not None and "tool" in failure.lower(), "Injected disabled lookup not denied")
            evidence.update(passed=True, model_requests=len(requests), lookup_calls=len(callbacks),
                            summary_only_initial=True, nested_tools=expected, shell_and_patch_denied=True,
                            credential_free=True, disabled_injection_denied=not enabled)
    except Exception as exc:
        evidence["error"] = str(exc)
    finally:
        companion.PROFILE, companion.CONFIG, companion.emit = original_profile, original_config, original_emit
        server.shutdown()
        server.server_close()
    return evidence


def main():
    cases = [run_case(enabled) for enabled in (True, False)]
    evidence = {"schema_version": 1, "checked_at": datetime.now(timezone.utc).isoformat(),
                "route": "synthetic loopback Responses only; no credentials, game data or external requests",
                "all_passed": all(case["passed"] for case in cases), "cases": cases}
    EVIDENCE.write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
    for case in cases:
        print(("PASS " if case["passed"] else "FAIL ") + case["case"] +
              ("" if case["passed"] else ": " + case.get("error", "unknown error")))
    return 0 if evidence["all_passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
