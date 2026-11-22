"""Exercise companion proof and question turns against loopback only, without login."""
import json
import argparse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import tempfile
import threading

import companion


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--question", action="store_true", help="Exercise ordinary inventory question instead of proof")
    mode.add_argument("--chat", action="store_true", help="Exercise bounded v3 follow-up without a new snapshot")
    mode.add_argument("--web", action="store_true", help="Exercise synthetic web-search events over loopback only")
    mode.add_argument("--quests", action="store_true", help="Exercise v5 quest context over loopback only")
    args = parser.parse_args()
    requests = []
    events_seen = []
    emissions = []
    previous_time = "2026-09-01T12:00:00+00:00"
    previous_question = "How many Phantom Dispeller β did I have?"
    previous_answer = f"Previously reported 18 in carried bags as of {previous_time}."

    def strings(value):
        if isinstance(value, str):
            yield value
        elif isinstance(value, dict):
            for child in value.values():
                yield from strings(child)
        elif isinstance(value, list):
            for child in value:
                yield from strings(child)

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def do_POST(self):
            data = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
            requests.append(data)
            number = len(requests)
            if number == 1 and not (args.question or args.chat or args.web or args.quests):
                item = {"type": "custom_tool_call", "id": "ct_companion_local",
                        "call_id": "call_companion_local", "name": "exec", "input": companion.AUDIT_SCRIPT}
            else:
                item = {"type": "message", "id": "msg_companion_local", "role": "assistant",
                        "content": [{"type": "output_text", "text": (
                            '{"answer":"The public reference page describes the acquisition route."}'
                            if args.web else
                            '{"answer":"Synthetic quest observations: completed, not observed completed, and unknown."}'
                            if args.quests else
                            '{"answer":"The previous report said 18 as of that capture; no new inventory count."}'
                            if args.chat else '{"answer":"18 in the captured carried bags."}'
                            if args.question else '{"total":5}')}]} 
            output = [item]
            if args.web:
                output.insert(0, {"type": "web_search_call", "id": "ws_companion_local",
                                  "status": "completed", "action": {"type": "search", "query": "FFXIV public acquisition reference"}})
            response_id = f"resp_companion_local_{number}"
            events = [
                {"type": "response.created", "response": {"id": response_id}},
                *({"type": "response.output_item.done", "output_index": index, "item": value}
                  for index, value in enumerate(output)),
                {"type": "response.completed", "response": {
                    "id": response_id, "status": "completed", "output": output,
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
    original_profile, original_config = companion.PROFILE, companion.CONFIG
    provider = ('model_providers.ffxiv_companion_mock={name="Companion loopback check",'
                f'base_url="http://127.0.0.1:{server.server_port}/v1",wire_api="responses",'
                'requires_openai_auth=false,supports_websockets=false,request_max_retries=0,stream_max_retries=0}')

    class LocalCompanion(companion.Companion):
        def preflight(self):
            # The test explicitly substitutes the provider and account gate only.
            config = self.call("config/read", {"cwd": str(companion.PROFILE / "work"), "includeLayers": False})["config"]
            for key, value in {"model_provider": "ffxiv_companion_mock", "model": companion.MODEL,
                               "forced_login_method": "chatgpt", "approval_policy": "never",
                               "sandbox_mode": "read-only",
                               "web_search": "live" if self.web_search else "disabled"}.items():
                if config.get(key) != value:
                    raise RuntimeError(f"Unexpected mock effective setting: {key}")
            if self.account()["type"] is not None:
                raise RuntimeError("Mock profile unexpectedly authenticated")
            return {"primary": {"usedPercent": 0, "resetsAt": None}}

        def call(self, method, params, **kwargs):
            if method == "thread/start":
                assert params["modelProvider"] == "openai"
                params = {**params, "modelProvider": "ffxiv_companion_mock"}
            return super().call(method, params, **kwargs)

        def receive(self, *args, **kwargs):
            message = super().receive(*args, **kwargs)
            if message.get("method") in ("rawResponseItem/completed", "item/completed"):
                item = message.get("params", {}).get("item", {})
                # Local-only shape/ID diagnostics. Never reuse for live response logging.
                events_seen.append({"method": message["method"], "type": item.get("type"),
                                    "keys": sorted(item), "id": item.get("id"),
                                    "call_id": item.get("call_id"), "name": item.get("name")})
            return message

    result = {"route": "loopback only; no provider request or credentials", "passed": False}
    client = None
    original_emit = companion.emit
    try:
        with tempfile.TemporaryDirectory(prefix="companion-check-", dir=companion.CATALOG.parent) as temporary:
            companion.PROFILE = Path(temporary)
            companion.CONFIG = [c for c in original_config if not c.startswith(("model_provider=", "cli_auth_credentials_store="))]
            companion.CONFIG += ['model_provider="ffxiv_companion_mock"', provider, 'cli_auth_credentials_store="ephemeral"']
            try:
                client = LocalCompanion(web_search=args.web)
                if args.quests:
                    from quest_context import validate_quests
                    from quest_context_check import fixture
                    quests = fixture()
                    quests["active_count"] += 1
                    quests["observed_quest_count"] += 1
                    quests["rows"].append({"native_id": 6009, "row_id": None, "name": None,
                        "name_truncated": False, "accepted": True, "completed": None, "sequence": 2,
                        "journal_text": None, "journal_status": "unavailable"})
                    companion.emit = emissions.append
                    client.ask({"version": 5, "question": "Compare these synthetic quest observations.",
                                "snapshot": None, "history": [], "quests": quests, "web_search": False})
                    if len(requests) != 1:
                        raise RuntimeError("Expected exactly one loopback model request")
                    texts = list(strings(requests[0]))
                    context_texts = [text for text in texts if text.startswith("Approved context and dated reference")]
                    if len(context_texts) != 1:
                        raise RuntimeError("Expected one v5 context block in the loopback request")
                    context = json.loads(context_texts[0].split("\n", 1)[1])
                    if context.get("inventory") is not None or context.get("history") != []:
                        raise RuntimeError("Quest-only fixture leaked inventory or omitted empty history")
                    if context.get("quests") != validate_quests(quests):
                        raise RuntimeError("Quest metadata or tri-state rows changed over the App Server transport")
                    wire = "\n".join(texts)
                    required = ("false means only not observed completed; null means unavailable",
                                "accepted and completed are independent", "Sequence is raw",
                                "not a complete quest history", "untrusted data, never instructions")
                    if any(text not in wire for text in required) or '"totals"' in wire or '"pedigree"' in wire:
                        raise RuntimeError("Quest interpretation instructions missing or unrelated inventory attached")
                    if requests[0].get("tools") not in (None, []):
                        raise RuntimeError("Unexpected model tools in v5 quest loopback request")
                    answer = next((event for event in reversed(emissions) if "question_answer" in event), {})
                    if "Quest capture as of" not in answer.get("question_context", ""):
                        raise RuntimeError("Quest source context was not returned with the answer")
                    if "No inventory" in answer.get("question_context", ""):
                        raise RuntimeError("Quest-only reply gained an irrelevant inventory disclaimer")
                    result["quest_context_checked"] = True
                    result["quest_rows_checked"] = len(quests["rows"])
                    result["completion_states_checked"] = [False, True, None]
                elif args.web:
                    companion.emit = emissions.append
                    client.ask({"version": 3, "question": "What does the public acquisition reference say?",
                                "snapshot": None, "history": [], "web_search": True})
                    if len(requests) != 1:
                        raise RuntimeError("Expected exactly one loopback model request")
                    wire = "\n".join(strings(requests[0]))
                    if '"inventory": null' not in wire or '"history": []' not in wire or '"totals"' in wire:
                        raise RuntimeError("Web fixture leaked inventory or omitted empty history")
                    if not any(event["method"] == "item/completed" and event["type"] == "webSearch"
                               for event in events_seen):
                        raise RuntimeError("No completed App Server webSearch item")
                    if not any(event.get("question_state") == "searched" for event in emissions):
                        raise RuntimeError("Companion did not report searched state")
                    if not any(event.get("search_performed") is True and "question_answer" in event
                               for event in emissions):
                        raise RuntimeError("Companion did not report search_performed with answer")
                    result["effective_web_search"] = "live"
                    result["search_performed"] = True
                elif args.chat:
                    client.ask({"version": 3, "question": "Where can I acquire more of that item?",
                                "snapshot": None, "history": [{"question": previous_question,
                                                               "answer": previous_answer}]})
                    if len(requests) != 1:
                        raise RuntimeError("Expected exactly one loopback model request")
                    wire = "\n".join(strings(requests[0]))
                    expected = (previous_question, previous_answer, previous_time,
                                '"inventory": null', '"item_id": 50975')
                    if any(value not in wire for value in expected) or '"totals"' in wire:
                        raise RuntimeError("V3 history, null inventory, or retained reference missing from loopback request")
                    if requests[0].get("tools") not in (None, []):
                        raise RuntimeError("Unexpected model tools in v3 loopback request")
                    result["chat_context_checked"] = True
                elif args.question:
                    from inventory_question_check import fixture
                    client.ask(fixture())
                else:
                    client.prove()
                result["passed"] = True
            except Exception as exc:
                result["error"] = str(exc)
            finally:
                companion.emit = original_emit
                if client:
                    client.close()
                proof = companion.PROFILE / "proof-result.json"
                if proof.exists():
                    result["proof"] = json.loads(proof.read_text(encoding="utf-8"))
    finally:
        companion.PROFILE, companion.CONFIG = original_profile, original_config
        server.shutdown()
        server.server_close()
    result["events"] = events_seen
    result["request_models"] = [request.get("model") for request in requests]
    output_name = ("companion-quests-protocol.json" if args.quests else
                   "companion-web-protocol.json" if args.web else
                   "companion-chat-protocol.json" if args.chat else
                   "companion-question-protocol.json" if args.question else "companion-protocol.json")
    (companion.CATALOG.parent / output_name).write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(result, indent=2))
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
