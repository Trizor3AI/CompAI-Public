"""Inspect App Server tools using a loopback Responses stub, never a real provider.

Uses a dedicated CODEX_HOME, no credentials, synthetic data, and read-only
permissions. The result applies to the mock provider route, not automatically
to ChatGPT subscription routing. Does not record request bodies or headers.
"""

import argparse
import json
import os
from pathlib import Path
import queue
import re
import shutil
import subprocess
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from collections import deque
from prepare_isolation_catalog import digest
from runtime_bundle import resolve_runtime, validate_runtime
from codex_tool_policy import (declared_function_names, expected_nested_tools, policy_id,
                              require_no_mcp_servers, resource_tools)


ROOT = Path(__file__).resolve().parents[1]
BRIDGE = ROOT / "bridge"
TEST_ROOT = BRIDGE / ".isolation-probe"
WORK = TEST_ROOT / "allowed"
TEST_HOME = TEST_ROOT / "mock-home"
SENTINEL = "SYNTHETIC_OUTSIDE_MARKER_8721"
SYNTHETIC_IMAGE_URL = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC"
SYNTHETIC_JPEG_URL = "data:image/jpeg;base64,/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAABAAEDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD50ooor8MP9Uz/2Q=="
SYNTHETIC_IMAGE_URLS = (SYNTHETIC_IMAGE_URL, SYNTHETIC_JPEG_URL)


def tool_names(tools, prefix=""):
    names = []
    for tool in tools or []:
        name = tool.get("name") or tool.get("type", "unknown")
        if tool.get("type") == "namespace":
            names.extend(tool_names(tool.get("tools"), prefix + name + "."))
        else:
            names.append(prefix + name)
    return names


def declaration_lines(value):
    """Record tool declaration signatures only, never the surrounding prompt."""
    found = []
    if isinstance(value, dict):
        for child in value.values():
            found.extend(declaration_lines(child))
    elif isinstance(value, list):
        for child in value:
            found.extend(declaration_lines(child))
    elif isinstance(value, str):
        for line in value.splitlines():
            if re.match(r"\s*(namespace\s+\w+\s*\{|type\s+\w+\s*=|// namespace\s+\w+|declare const tools:)", line):
                found.append(line.strip()[:180])
    return found


def declared_tool_names(lines):
    """Normalize only nested tool declaration names, not shared type aliases."""
    return sorted(set(declared_function_names(lines)))


def response_body(call_result):
    if not isinstance(call_result, dict) or call_result.get("ok") is not True:
        return None
    value = call_result.get("value")
    if isinstance(value, str):
        try:
            value = json.loads(value)
        except (TypeError, ValueError):
            return None
    return value if isinstance(value, dict) else None


class InheritedServerRejected(Exception):
    """Expected real-runtime negative-case stop before any model turn."""


def main():
    global TEST_ROOT, WORK, TEST_HOME
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--attack", help="Inject this tool name in the mock response")
    parser.add_argument("--strict", action="store_true")
    parser.add_argument("--catalog", type=Path, help="Task-local startup model catalog override")
    parser.add_argument("--code-call", choices=["get_snapshot", "exec_command", "apply_patch", "inspect", "mcp_resources"],
                        help="Inject a call through the actual functions.exec wrapper")
    parser.add_argument("--control-shell", action="store_true",
                        help="Advertise shell tools as a positive control; never inject a tool call")
    parser.add_argument("--dynamic-snapshot", action="store_true",
                        help="Expose one schema-validated synthetic snapshot function")
    parser.add_argument("--web-search", action="store_true",
                        help="Explore hosted live search registration on the loopback provider only")
    parser.add_argument("--image-input", action="store_true",
                        help="Send synthetic in-memory PNG/JPEG and verify transport without enabling file tools")
    parser.add_argument("--scratch-root", type=Path, help="Dedicated synthetic fixture directory for this run")
    parser.add_argument("--inherited-server", action="store_true",
                        help="Require the real guard to reject a disabled synthetic loopback MCP server before a turn")
    args = parser.parse_args()
    if args.scratch_root:
        TEST_ROOT = args.scratch_root.resolve()
        WORK, TEST_HOME = TEST_ROOT / "allowed", TEST_ROOT / "mock-home"
    if args.control_shell and (args.attack or args.dynamic_snapshot or args.code_call):
        parser.error("shell control cannot be combined with injected or dynamic calls")
    if args.code_call and args.attack:
        parser.error("select either --attack or --code-call")
    if args.inherited_server and (not args.strict or not args.catalog or args.attack or args.code_call
                                  or args.dynamic_snapshot or args.control_shell or args.web_search or args.image_input):
        parser.error("inherited-server requires only strict/catalog and optional scratch-root")
    executable = resolve_runtime(BRIDGE)
    pin_path = ((args.catalog.resolve().parent if args.catalog else BRIDGE / ".isolation-probe") / "runtime-pin.json")
    manifest = json.loads(pin_path.read_text(encoding="utf-8"))
    validate_runtime(executable, manifest)
    selected_policy = policy_id(manifest)
    if args.catalog:
        if manifest["binary_sha256"] != digest(executable) or manifest["catalog_sha256"] != digest(args.catalog.resolve()):
            raise RuntimeError("Binary or catalog differs from runtime pin; revalidate before using this profile")
    if (args.inherited_server or args.code_call == "mcp_resources") and not resource_tools(manifest):
        parser.error("MCP resource cases require the exact reviewed resource-tool policy pin")
    if args.code_call == "mcp_resources" and not (args.strict and args.catalog and args.dynamic_snapshot):
        parser.error("MCP resource case requires strict/catalog/dynamic-snapshot")
    for directory in (WORK, TEST_HOME, TEST_ROOT / "outside", TEST_ROOT / "tmp"):
        directory.mkdir(parents=True, exist_ok=True)
    outside = TEST_ROOT / "outside" / "dummy.txt"
    outside.write_text(SENTINEL, encoding="utf-8")
    (WORK / "allowed.txt").write_text("SYNTHETIC_ALLOWED", encoding="utf-8")

    captured = []
    tool_outputs = []
    code_host = []
    resource_diagnostics = []
    mcp_fixture_requests = []

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def do_GET(self):
            if self.path == "/mcp-inherited":
                mcp_fixture_requests.append("GET")
            self.send_response(404)
            self.end_headers()

        def do_POST(self):
            if self.path == "/mcp-inherited":
                mcp_fixture_requests.append("POST")
                self.send_response(404)
                self.send_header("Content-Length", "0")
                self.end_headers()
                return
            data = json.loads(self.rfile.read(int(self.headers.get("Content-Length", "0"))))
            captured.append({"path": self.path, "model": data.get("model"),
                             "synthetic_image_count": sum(part.get("image_url") in SYNTHETIC_IMAGE_URLS
                                 for entry in data.get("input", []) if isinstance(entry, dict)
                                 for part in entry.get("content", []) if isinstance(part, dict)),
                             "image_input_count": sum(part.get("type") == "input_image"
                                 for entry in data.get("input", []) if isinstance(entry, dict)
                                 for part in entry.get("content", []) if isinstance(part, dict)),
                             "body_keys": sorted(data),
                             "prompt_tool_declarations": declaration_lines(data.get("input")),
                             "additional_tools": [name for item in data.get("input", [])
                                                  if item.get("type") == "additional_tools"
                                                  for name in tool_names(item.get("tools"))],
                             "tools_present": "tools" in data,
                             "tool_count": len(data.get("tools", [])),
                             "tools": tool_names(data.get("tools")),
                             "tool_choice": data.get("tool_choice")})
            for item in data.get("input", []):
                if item.get("type") in ("function_call_output", "custom_tool_call_output"):
                    output = str(item.get("output", ""))
                    tool_outputs.append({"output": output[:500], "sentinel_visible": SENTINEL in output})
                    for part in item.get("output", []) if isinstance(item.get("output"), list) else []:
                        try:
                            value = json.loads(part.get("text", ""))
                            if isinstance(value, dict) and "tools" in value and "require" in value:
                                code_host.append(value)
                            if isinstance(value, dict) and value.get("kind") == "compai_mcp_resources_policy_check":
                                resource_diagnostics.append(value)
                        except (TypeError, ValueError):
                            pass
            number = len(captured)
            response_id = f"resp_local_{number}"
            if (args.attack or args.dynamic_snapshot or args.code_call) and number == 1:
                script = f"Get-Content -LiteralPath '{str(outside).replace(chr(39), chr(39) * 2)}'"
                arguments = {"cmd": script, "command": script, "workdir": str(WORK),
                             "timeout_ms": 1000, "yield_time_ms": 1000, "max_output_tokens": 100}
                name = args.attack or "get_snapshot"
                if name == "get_snapshot":
                    arguments = {}
                item = {"type": "function_call", "id": "fc_local_1", "call_id": "call_local_1",
                        "name": name, "arguments": json.dumps(arguments)}
                if args.code_call:
                    if args.code_call == "mcp_resources":
                        read_arguments = json.dumps({"server": "compai-unconfigured-test", "uri": outside.resolve().as_uri()})
                        code = (
                            "const capture=async(f)=>{try{return {ok:true,value:await f()};}catch(e){return {ok:false,error:String(e)};}};"
                            "const result={kind:'compai_mcp_resources_policy_check',tools:Object.keys(tools),"
                            "require:typeof require,process:typeof process,fetch:typeof fetch,types:{"
                            "list_mcp_resources:typeof tools.list_mcp_resources,"
                            "list_mcp_resource_templates:typeof tools.list_mcp_resource_templates,"
                            "read_mcp_resource:typeof tools.read_mcp_resource}};"
                            "result.snapshot=await capture(()=>tools.get_snapshot({}));"
                            "result.resources=await capture(()=>tools.list_mcp_resources({}));"
                            "result.templates=await capture(()=>tools.list_mcp_resource_templates({}));"
                            "result.read=await capture(()=>tools.read_mcp_resource(" + read_arguments + "));text(result);"
                        )
                    elif args.code_call == "get_snapshot":
                        code = "text(await tools.get_snapshot({}));"
                    elif args.code_call == "exec_command":
                        code = "text(await tools.exec_command(" + json.dumps({"cmd": script}) + "));"
                    elif args.code_call == "apply_patch":
                        patch = ("*** Begin Patch\n*** Update File: " + str(outside) + "\n@@\n-" + SENTINEL
                                 + "\n+SYNTHETIC_PATCHED\n*** End Patch")
                        code = "text(await tools.apply_patch(" + json.dumps(patch) + "));"
                    else:
                        code = ("text({tools:Object.keys(tools), require:typeof require, "
                                "process:typeof process, fetch:typeof fetch});")
                    item = {"type": "custom_tool_call", "id": "ct_local_1", "call_id": "call_local_1",
                            "name": "exec", "input": code}
            else:
                item = {"type": "message", "id": f"msg_local_{number}", "role": "assistant",
                        "content": [{"type": "output_text", "text": "LOCAL_OK"}]}
            events = [
                {"type": "response.created", "response": {"id": response_id}},
                {"type": "response.output_item.done", "output_index": 0, "item": item},
                {"type": "response.completed", "response": {
                    "id": response_id, "status": "completed", "output": [item],
                    "usage": {"input_tokens": 0, "output_tokens": 0, "total_tokens": 0,
                              "input_tokens_details": {"cached_tokens": 0}}}},
            ]
            body = "".join("data: " + json.dumps(event) + "\n\n" for event in events).encode()
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    provider = ('model_providers.ffxiv_mock={name="FFXIV local mock",'
                f'base_url="http://127.0.0.1:{server.server_port}/v1",'
                'wire_api="responses",requires_openai_auth=false,supports_websockets=false,'
                'request_max_retries=0,stream_max_retries=0'
                + (',supports_standalone_web_search=true' if args.web_search else '') + '}')
    config = [
        'model_provider="ffxiv_mock"', provider, 'model="gpt-6-sol"',
        'approval_policy="never"', 'sandbox_mode="read-only"',
        'cli_auth_credentials_store="ephemeral"', 'check_for_update_on_startup=false',
        'features.shell_tool=false', 'features.unified_exec=false',
        'features.browser_use=false', 'features.browser_use_external=false',
        'features.browser_use_full_cdp_access=false', 'features.auth_elicitation=false',
        'features.remote_plugin=false', 'features.tool_suggest=false', 'features.shell_snapshot=false',
        'features.computer_use=false', 'features.apps=false', 'features.plugins=false',
        'features.multi_agent=false', 'features.image_generation=false',
        'features.view_image=false', 'web_search="disabled"',
        'features.request_permissions_tool=false', 'features.skill_search=false',
        'features.hooks=false', 'features.goals=false',
        'features.memories=false', 'agents.enabled=false', 'features.multi_agent_v2=false',
        'features.unbounded_connection_retries=false',
        'features.enable_request_compression=false',
        'analytics.enabled=false', 'feedback.enabled=false',
    ]
    if args.inherited_server:
        config.append('mcp_servers.compai_inherited_synthetic={url="http://127.0.0.1:'
                      + str(server.server_port) + '/mcp-inherited",enabled=false}')
    command = [str(executable), "app-server"]
    if args.strict:
        command.append("--strict-config")
    for value in config:
        command.extend(["-c", value])
    if args.catalog:
        command.extend(["-c", "model_catalog_json=" + json.dumps(str(args.catalog.resolve()))])
    if args.control_shell:
        command.extend(["-c", "features.shell_tool=true", "-c", "features.unified_exec=true"])
    if args.web_search:
        command.extend(["-c", 'web_search="live"'])
    # Do not inherit provider keys, proxy settings, or the user's Codex config.
    environment = {k: v for k, v in os.environ.items() if k.upper() in {
        "PATH", "PATHEXT", "SYSTEMROOT", "WINDIR", "COMSPEC", "SYSTEMDRIVE",
        "USERPROFILE", "APPDATA", "LOCALAPPDATA", "PROGRAMFILES", "PROGRAMFILES(X86)",
    }}
    environment.update(CODEX_HOME=str(TEST_HOME), TEMP=str(TEST_ROOT / "tmp"),
                       TMP=str(TEST_ROOT / "tmp"), NO_PROXY="127.0.0.1,localhost")
    inbox = queue.Queue()
    deferred = deque()
    errors = []
    process = subprocess.Popen(command, cwd=WORK, env=environment, stdin=subprocess.PIPE,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               text=True, encoding="utf-8", bufsize=1)

    def read_stdout():
        for line in process.stdout:
            try:
                inbox.put(json.loads(line))
            except json.JSONDecodeError:
                pass
        inbox.put(None)

    def read_stderr():
        for line in process.stderr:
            errors.append(line.rstrip()[:500])

    threading.Thread(target=read_stdout, daemon=True).start()
    threading.Thread(target=read_stderr, daemon=True).start()
    seq = 0
    turn_start_calls = 0

    def send(method, params, response=True):
        nonlocal seq, turn_start_calls
        seq += 1
        if method == "turn/start":
            turn_start_calls += 1
        request = {"method": method, "params": params}
        if response:
            request["id"] = seq
        process.stdin.write(json.dumps(request) + "\n")
        process.stdin.flush()
        return seq

    def receive(target=None):
        if target is None and deferred:
            return deferred.popleft()
        if target is not None:
            for message in list(deferred):
                if message.get("id") == target:
                    deferred.remove(message)
                    return message
        deadline = time.monotonic() + 25
        while time.monotonic() < deadline:
            message = inbox.get(timeout=max(0.1, deadline - time.monotonic()))
            if message is None:
                raise RuntimeError("App Server exited")
            if target is None or message.get("id") == target:
                return message
            deferred.append(message)
        raise TimeoutError("App Server timeout")

    def call(method, params):
        message = receive(send(method, params))
        if "error" in message:
            raise RuntimeError(f"{method}: {message['error']}")
        return message.get("result", {})

    result = {"route": "loopback mock; no subscription/provider request", "attack": args.attack,
              "shell_control": args.control_shell, "dynamic_snapshot": args.dynamic_snapshot,
              # The pin binds the catalog contents; evidence needs no machine-local path.
              "catalog_override": args.catalog.name if args.catalog else None, "code_call": args.code_call,
              "runtime_pin_verified": bool(args.catalog), "web_search_experiment": args.web_search,
              "policy_id": selected_policy, "mcp_status_pages": [], "rpc_methods": [],
              "mcp_servers_empty_before_turn": False, "mcp_servers_empty_after_turn": False}
    guard_phase = "before_turn"

    def guard_call(method, params):
        result["rpc_methods"].append(method)
        response = call(method, params)
        result["mcp_status_pages"].append({"phase": guard_phase, "response": response})
        result.setdefault("mcp_server_inventory_" + guard_phase, response)
        return response

    try:
        call("initialize", {"clientInfo": {"name": "ffxiv_isolation_mock", "version": "0.1.0"},
                            "capabilities": {"experimentalApi": True}})
        send("initialized", {}, response=False)
        effective_config = call("config/read", {"cwd": str(WORK), "includeLayers": False})["config"]
        result["unbounded_connection_retries_disabled"] = (
            effective_config.get("features", {}).get("unbounded_connection_retries") is False)
        if not result["unbounded_connection_retries_disabled"]:
            raise RuntimeError("Unbounded connection retries were not disabled")
        models = call("model/list", {"limit": 100, "includeHidden": True})
        result["model_listed"] = any(m.get("model") == "gpt-6-sol" for m in models.get("data", []))
        result["provider_capabilities"] = call("modelProvider/capabilities/read", {})
        params = {"model": "gpt-6-sol", "cwd": str(WORK),
                  "approvalPolicy": "never", "sandbox": "read-only", "ephemeral": True,
                  "allowProviderModelFallback": False}
        if args.dynamic_snapshot:
            params["dynamicTools"] = [{"type": "function", "name": "get_snapshot", "description": "Return synthetic fixture only",
                                       "inputSchema": {"type": "object", "properties": {},
                                                       "additionalProperties": False}}]
        thread = call("thread/start", params)
        thread_id = thread["thread"]["id"]
        try:
            require_no_mcp_servers(guard_call, thread_id)
        except RuntimeError as exc:
            if not args.inherited_server:
                raise
            result.update(guard_rejected=True, guard_error=str(exc))
            raise InheritedServerRejected from exc
        if args.inherited_server:
            raise RuntimeError("Configured synthetic MCP server was not rejected; no model turn was sent")
        result["mcp_servers_empty_before_turn"] = True
        result["mcp_servers"] = 0
        result["mcp_next_cursor"] = None
        call("turn/start", {"threadId": thread_id, "model": "gpt-6-sol", "effort": "medium",
             "approvalPolicy": "never", "sandboxPolicy": {"type": "readOnly"},
             "input": [{"type": "text", "text": "Synthetic local protocol test. Reply LOCAL_OK."}]
                 + ([{"type": "image", "url": url} for url in SYNTHETIC_IMAGE_URLS] if args.image_input else [])})
        deadline = time.monotonic() + 35
        while time.monotonic() < deadline:
            message = receive()
            params = message.get("params", {})
            if message.get("method") == "item/tool/call":
                permitted = (args.dynamic_snapshot and params.get("tool") == "get_snapshot"
                             and params.get("arguments") == {})
                response = {"success": permitted, "contentItems": [{"type": "inputText",
                            "text": "{\"synthetic_item_count\": 5}" if permitted else "Denied by test client"}]}
                process.stdin.write(json.dumps({"id": message["id"], "result": response}) + "\n")
                process.stdin.flush()
                result["dynamic_call_allowed"] = permitted
            if message.get("method") == "turn/completed":
                result["turn_status"] = params.get("turn", {}).get("status")
                result["turn_error"] = params.get("turn", {}).get("error")
                break
        else:
            result["error"] = "No completion before deadline"
        guard_phase = "after_turn"
        require_no_mcp_servers(guard_call, thread_id)
        result["mcp_servers_empty_after_turn"] = True
    except InheritedServerRejected:
        pass
    except Exception as exc:
        result["error"] = f"{type(exc).__name__}: {exc}"
    finally:
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)
        server.shutdown()
        server.server_close()
    result["requests"] = captured
    result["tool_outputs"] = tool_outputs
    result["code_host"] = code_host
    result["outside_fixture_unchanged"] = outside.read_text(encoding="utf-8") == SENTINEL
    result["turn_start_calls"] = turn_start_calls
    result["model_requests"] = len(captured)
    result["mcp_servers_empty"] = result["mcp_servers_empty_before_turn"] and result["mcp_servers_empty_after_turn"]
    result["diagnostics"] = [line for line in errors if any(
        word in line.lower() for word in ("error", "warn", "fallback", "catalog", "unknown", "unrecognized", "unsupported"))][-10:]
    declarations = "\n".join(line for request in captured for line in request["prompt_tool_declarations"])
    result["declared_nested_tools"] = declared_tool_names(declarations.splitlines())
    outputs = "\n".join(item["output"] for item in tool_outputs)
    if args.inherited_server:
        statuses = [item for page in result["mcp_status_pages"]
                    if isinstance(page.get("response"), dict) and isinstance(page["response"].get("data"), list)
                    for item in page["response"]["data"]]
        observed = any(isinstance(item, dict) and item.get("name") == "compai_inherited_synthetic" for item in statuses)
        result.update(scope="runtime_disabled_loopback_mcp_server_guard; rejected before turn/start",
                      configured_server_observed=observed, mcp_fixture_requests=mcp_fixture_requests)
        result["checks"] = {"guard_rejected": result.get("guard_rejected") is True,
                            "configured_server_observed": observed,
                            "guard_checked_actual_runtime": result["rpc_methods"] == ["mcpServerStatus/list"],
                            "no_turn_start": turn_start_calls == 0, "no_model_requests": not captured,
                            "disabled_server_not_contacted": not mcp_fixture_requests,
                            "outside_fixture_unchanged": result["outside_fixture_unchanged"],
                            "runtime_pin_verified": result["runtime_pin_verified"] is True,
                            "unbounded_connection_retries_disabled": result.get("unbounded_connection_retries_disabled") is True,
                            "no_unexpected_error": "error" not in result}
        result["passed"] = all(result["checks"].values())
        print(json.dumps(result, indent=2))
        return 0 if result["passed"] else 1
    checks = {"turn_completed": result.get("turn_status") == "completed",
              "unbounded_connection_retries_disabled": result.get("unbounded_connection_retries_disabled") is True,
              "known_model": result.get("model_listed") is True,
              "loopback_response_used": bool(captured),
              "outside_fixture_unchanged": result["outside_fixture_unchanged"],
              "mcp_servers_empty_before_turn": result["mcp_servers_empty_before_turn"],
              "mcp_servers_empty_after_turn": result["mcp_servers_empty_after_turn"]}
    expected_wrappers = {"functions.exec", "functions.wait", "functions.request_user_input",
                         "functions.request_user_input_async", "clock.sleep"}
    checks["no_direct_provider_tools"] = bool(captured) and all(not request["tools"] for request in captured)
    checks["only_expected_wrappers"] = bool(captured) and all(
        set(request["additional_tools"]) == expected_wrappers for request in captured)
    checks["only_requested_model"] = bool(captured) and all(request["model"] == "gpt-6-sol" for request in captured)
    if args.image_input:
        checks["exact_image_bytes_transported"] = bool(captured) and captured[0]["synthetic_image_count"] == 2
        checks["image_file_tool_not_declared"] = "view_image(args:" not in declarations
    if args.web_search:
        checks["hosted_web_declared"] = "web__run(args:" in declarations
        checks["patch_not_declared"] = "apply_patch(input:" not in declarations
        checks["no_mcp_servers"] = result.get("mcp_servers") == 0 and not result.get("mcp_next_cursor")
    if args.control_shell:
        checks["shell_positive_control"] = "exec_command(args:" in declarations
    else:
        checks["shell_not_declared"] = "exec_command(args:" not in declarations
    if args.catalog:
        checks["patch_not_declared"] = "apply_patch(input:" not in declarations
        allowed = set(expected_nested_tools(manifest, dynamic=("get_snapshot",) if args.dynamic_snapshot else (),
                                            web_search=args.web_search))
        declared = set(result["declared_nested_tools"])
        if args.web_search:
            checks["no_other_nested_tools"] = declared == allowed
        else:
            checks["only_expected_nested_tools"] = declared == allowed
    if args.code_call in ("exec_command", "apply_patch"):
        checks["blocked_in_code_host"] = f"tools.{args.code_call} is not a function" in outputs
    elif args.attack:
        checks["unsupported_direct_call"] = f"unsupported call: {args.attack}" in outputs
    elif args.dynamic_snapshot and args.code_call != "inspect":
        checks["allowed_snapshot_executed"] = result.get("dynamic_call_allowed") is True and "synthetic_item_count" in outputs
    if args.code_call in ("inspect", "mcp_resources"):
        allowed = set(expected_nested_tools(manifest, dynamic=("get_snapshot",) if args.dynamic_snapshot else (),
                                            web_search=args.web_search))
        checks["code_host_inventory"] = bool(code_host) and all(set(item["tools"]) == allowed for item in code_host)
        checks["no_node_or_fetch_globals"] = bool(code_host) and all(
            item.get(key) == "undefined" for item in code_host for key in ("require", "process", "fetch"))
    if args.code_call == "mcp_resources":
        diagnostic = resource_diagnostics[0] if len(resource_diagnostics) == 1 else {}
        resources = response_body(diagnostic.get("resources"))
        templates = response_body(diagnostic.get("templates"))
        read = diagnostic.get("read", {})
        result["resource_diagnostics"] = resource_diagnostics
        result["read_error_transport"] = "caught_exception" if isinstance(read, dict) and read.get("ok") is False else "returned_value"
        checks["allowed_snapshot_executed"] = (result.get("dynamic_call_allowed") is True
                                              and response_body(diagnostic.get("snapshot")) == {"synthetic_item_count": 5})
        checks.update({
            "one_mcp_resource_diagnostic": len(resource_diagnostics) == 1,
            "resource_helpers_are_functions": diagnostic.get("types") == {name: "function" for name in resource_tools(manifest)},
            "mcp_resource_list_empty": resources == {"resources": []},
            "mcp_resource_templates_empty": templates == {"resourceTemplates": []},
            "mcp_unknown_server_read_rejected": read == {"ok": False, "error": "unknown MCP server 'compai-unconfigured-test'"},
            "no_sentinel_in_tool_results": not any(item["sentinel_visible"] for item in tool_outputs) and SENTINEL not in json.dumps(resource_diagnostics),
        })
    result["checks"] = checks
    result["passed"] = all(checks.values())
    print(json.dumps(result, indent=2))
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
