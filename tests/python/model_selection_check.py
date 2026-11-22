"""Offline model-picker checks: no account, game data, or provider requests."""
from collections import deque
import copy
import json
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import patch

import companion
from inventory_question import validate_request
from model_selection import (DEFAULT_MODEL, DEFAULT_EFFORT, checked_model_selection,
    discover_models, require_available, require_permitted, validate_selection, validate_model_evidence)
from prepare_isolation_catalog import digest


ALLOWED = {DEFAULT_MODEL: ["low", "medium", "high"], "gpt-6-luna": ["low", "medium", "high"]}


def request():
    return {"version": 7, "question": "Explain aether currents.", "snapshot": None,
            "history": [], "quest_lookup": None, "web_search": False,
            "model": "gpt-6-luna", "reasoning_effort": "high"}


def model_row(model, efforts=("low", "medium", "high"), **kwargs):
    return {"model": model, "displayName": model.upper(), "defaultReasoningEffort": "medium",
            "supportedReasoningEfforts": [{"reasoningEffort": effort} for effort in efforts], **kwargs}


def evidence_row(model, efforts, pin, *, deferred=False, clock=False):
    cases = []
    for mode, effort in [("enabled-web", value) for value in efforts] + [("enabled", "medium"), ("disabled", "medium")]:
        case = {"case": mode, "model": model, "effort": effort, "passed": True, "runtime": copy.deepcopy(pin),
                "model_requests": 2 if mode == "disabled" else 5, "initial_summary_only": True,
                "credential_free": True, "unexpected_wrappers": False,
                "exact_model_and_effort": True, "provider_fallback_disabled": True}
        if mode == "disabled":
            case.update(disabled_tool_absent=True, disabled_callback_calls=0, injected_tool_denied=True)
        else:
            case.update(nested_tools=(["discover_tools", "invoke_tool"] if deferred else ["query_quests"])
                        + (["web__run"] if mode == "enabled-web" else []) + (["clock__curr_time"] if clock else []),
                        lookup_calls=3, pagination_checked=True, unmentioned_id_lookup_checked=True,
                        completion_states_checked=[False, True, None], shell_and_patch_denied=True,
                        web_completion_simulated=mode == "enabled-web")
            if deferred:
                case["discovery_metadata_intact"] = True
        cases.append(case)
    return {"id": model, "efforts": list(efforts), "passed": True, "cases": cases, "excluded_efforts": []}


def fake_client():
    client = companion.Companion.__new__(companion.Companion)
    client.model, client.effort, client.web_search = "gpt-6-luna", "high", False
    client.model_allowlist = ALLOWED
    client.cancel_event = threading.Event()
    client.account = lambda refresh=False: {"type": "chatgpt", "planType": "plus"}
    calls = []
    messages = deque([
        {"method": "item/completed", "params": {"item": {"type": "agentMessage", "text": '{"answer":"Synthetic answer."}'}}},
        {"method": "turn/completed", "params": {"turn": {"status": "completed"}}},
    ])

    def call(method, params=None, **kwargs):
        calls.append((method, params))
        if method == "config/read":
            return {"config": {"model_provider": "openai", "model": client.model,
                "forced_login_method": "chatgpt", "approval_policy": "never", "sandbox_mode": "read-only",
                "web_search": "live" if client.web_search else "disabled"}}
        if method == "model/list":
            return {"data": [model_row(model) for model in ALLOWED]}
        if method == "account/rateLimits/read":
            return {"rateLimits": {"primary": {"usedPercent": 5}}}
        if method == "thread/start":
            return {"thread": {"id": "synthetic-thread"}}
        if method == "turn/start":
            return {"turn": {"id": "synthetic-turn"}}
        return {"data": []}

    client.call = call
    client.receive = lambda **_: messages.popleft()
    client.send = lambda _: None
    return client, calls


class SelectionTests(unittest.TestCase):
    def test_request_v7_and_legacy_contracts(self):
        self.assertEqual(validate_request(request()), (request()["question"], None))
        for version in (3, 4, 5, 6):
            value = {"version": version, "question": "Test question", "snapshot": None, "history": []}
            if version == 5:
                value["quests"] = None
            if version == 6:
                value["quest_lookup"] = None
            validate_request(value)
            value["model"] = DEFAULT_MODEL
            with self.assertRaises(RuntimeError):
                validate_request(value)
        for field in ("model", "reasoning_effort", "quest_lookup"):
            value = request()
            value.pop(field)
            with self.assertRaises(RuntimeError):
                validate_request(value)

    def test_malformed_choices_and_unvalidated_pairs_rejected(self):
        for model, effort in ((None, "medium"), (True, "medium"), (" x", "medium"),
                              ('x"\nmodel_provider="evil', "medium"), ("x", "Medium"),
                              ("x", None), ("x", []), ("x", True)):
            with self.subTest(model=model, effort=effort), self.assertRaises(RuntimeError):
                validate_selection(model, effort)
        for model, effort in (("unknown", "medium"), (DEFAULT_MODEL, "ultra")):
            with self.assertRaisesRegex(RuntimeError, "no fallback"):
                require_permitted(model, effort, ALLOWED)

    def test_v7_preserves_historical_inventory_and_pedigree_coverage(self):
        from inventory_question_check import pedigree_fixture
        value = pedigree_fixture()
        value.update(version=6, quest_lookup=None)
        value["snapshot"]["changed"] = True
        value["snapshot"]["containers"][1]["items"][0]["pedigree"] = None
        _, previous = validate_request(value)
        value.update(version=7, model="gpt-6-luna", reasoning_effort="high")
        _, current = validate_request(value)
        self.assertEqual(previous, current)
        self.assertTrue(current["changed_since_capture"])
        self.assertEqual(current["snapshot_status"], "historical_as_of_capture")
        self.assertEqual(current["chocobo_details"]["status"], "partial")
        self.assertEqual(current["chocobo_details"]["coverage"]["verified_card_slots"], 1)
        client, calls = fake_client()
        with patch.object(companion, "emit") as output:
            client.ask(value)
        context = output.call_args.args[0]["question_context"]
        self.assertIn("Changes were observed", context)
        turn = next(params for method, params in calls if method == "turn/start")
        context = json.loads(turn["input"][1]["text"].split("\n", 1)[1])
        self.assertEqual(context["inventory"]["chocobo_details"]["coverage"]["verified_card_slots"], 1)

    def test_report_is_bound_to_runtime_catalog_and_only_safe_rows(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            catalog = root / "models.json"
            rows = [{"slug": model, "visibility": "list", "apply_patch_tool_type": None,
                     "supported_reasoning_levels": [{"effort": effort} for effort in efforts]}
                    for model, efforts in ALLOWED.items()]
            catalog.write_text(json.dumps({"models": rows}), encoding="utf-8")
            pin = {"binary_sha256": "binary", "catalog_sha256": digest(catalog),
                   "runtime_files": {"codex.exe": "binary", "codex-code-mode-host.exe": "host"}}
            report = {"schema_version": 1, "all_passed": True, **pin,
                      "models": [evidence_row(model, efforts, pin) for model, efforts in ALLOWED.items()]}
            target = root / "model-selection-validation.json"

            def save(value):
                target.write_text(json.dumps(value), encoding="utf-8")

            save(report)
            self.assertEqual(checked_model_selection(pin, catalog), ALLOWED)
            for mutate in (lambda value: value.update(binary_sha256="changed"),
                           lambda value: value.update(runtime_files={}),
                           lambda value: value.update(all_passed=False),
                           lambda value: value["models"][0].update(passed=False),
                           lambda value: value["models"][0].update(efforts=["ultra"]),
                           lambda value: value["models"].append(value["models"][0]),
                           lambda value: value["models"][0].update(id="unknown")):
                broken = copy.deepcopy(report)
                mutate(broken)
                save(broken)
                with self.assertRaises(RuntimeError):
                    checked_model_selection(pin, catalog)
            save(report)
            catalog.write_text("changed", encoding="utf-8")
            with self.assertRaises(RuntimeError):
                checked_model_selection(pin, catalog)
            for field, value in (("visibility", "hide"), ("apply_patch_tool_type", "freeform")):
                changed = copy.deepcopy(rows)
                changed[0][field] = value
                catalog.write_text(json.dumps({"models": changed}), encoding="utf-8")
                new_pin = {**pin, "catalog_sha256": digest(catalog)}
                save({**report, **new_pin})
                with self.assertRaises(RuntimeError):
                    checked_model_selection(new_pin, catalog)

    def test_evidence_rejects_remapped_effort_wrong_model_and_incomplete_tool_checks(self):
        pin = {"binary_sha256": "binary", "catalog_sha256": "catalog", "runtime_files": {"codex.exe": "binary"}}
        row = evidence_row(DEFAULT_MODEL, ["medium", "high"], pin)
        row["excluded_efforts"] = [{"effort": "ultra", "reason": "Runtime remaps Ultra", "case": {
            "model": DEFAULT_MODEL, "effort": "ultra", "case": "enabled-web", "runtime": copy.deepcopy(pin),
            "passed": False, "error": "Unexpected provider reasoning effort: ['xhigh']"}}]
        efforts = ["medium", "high", "ultra"]
        self.assertEqual(validate_model_evidence(row, pin, efforts), ["medium", "high"])
        for mutate in (lambda value: value["efforts"].append("ultra"),
                       lambda value: value["cases"][0].update(model="gpt-6-luna"),
                       lambda value: value["cases"][0].update(exact_model_and_effort=False),
                       lambda value: value["cases"][0].update(shell_and_patch_denied=False),
                       lambda value: value["cases"][0].update(nested_tools=["query_quests", "web__run", "exec_command"]),
                       lambda value: value["cases"].pop(),
                       lambda value: value["cases"][0]["runtime"].update(catalog_sha256="stale"),
                       lambda value: value["excluded_efforts"][0]["case"].update(model="gpt-6-luna"),
                       lambda value: value["excluded_efforts"][0]["case"].update(error="Some unrelated failure")):
            broken = copy.deepcopy(row)
            mutate(broken)
            with self.subTest(mutation=mutate), self.assertRaises(RuntimeError):
                validate_model_evidence(broken, pin, efforts)

    def test_evidence_accepts_exact_legacy_or_deferred_tools_with_optional_clock(self):
        pin = {"binary_sha256": "binary", "catalog_sha256": "catalog", "runtime_files": {"codex.exe": "binary"}}
        efforts = ["medium", "high"]
        for deferred in (False, True):
            for clock in (False, True):
                with self.subTest(deferred=deferred, clock=clock):
                    row = evidence_row(DEFAULT_MODEL, efforts, pin, deferred=deferred, clock=clock)
                    self.assertEqual(validate_model_evidence(row, pin, efforts), efforts)

    def test_deferred_evidence_rejects_incomplete_discovery_and_changed_tool_contract(self):
        pin = {"binary_sha256": "binary", "catalog_sha256": "catalog", "runtime_files": {"codex.exe": "binary"}}
        efforts = ["medium", "high"]
        row = evidence_row(DEFAULT_MODEL, efforts, pin, deferred=True)
        mutations = (
            lambda case: case.pop("discovery_metadata_intact"),
            lambda case: case.update(discovery_metadata_intact=False),
            lambda case: case.update(discovery_metadata_intact=None),
            lambda case: case.update(discovery_metadata_intact=1),
            lambda case: case["nested_tools"].append("query_quests"),
            lambda case: case["nested_tools"].append("exec_command"),
            lambda case: case["nested_tools"].append("apply_patch"),
            lambda case: case["nested_tools"].append("request_user_input"),
            lambda case: case["nested_tools"].append("web_search"),
            lambda case: case["nested_tools"].append("discover_tools"),
            lambda case: case["nested_tools"].extend(["clock__curr_time", "clock__curr_time"]),
            lambda case: case["nested_tools"].remove("discover_tools"),
            lambda case: case["nested_tools"].remove("invoke_tool"),
            lambda case: case.update(nested_tools="discover_tools,invoke_tool"),
            lambda case: case.update(nested_tools=[{}]),
            lambda case: case.update(lookup_calls=2),
            lambda case: case.update(completion_states_checked=[False, True]),
            lambda case: case.update(pagination_checked=False),
            lambda case: case.update(unmentioned_id_lookup_checked=False),
            lambda case: case.update(shell_and_patch_denied=False),
            lambda case: case.update(exact_model_and_effort=False),
            lambda case: case.update(credential_free=False),
            lambda case: case.update(initial_summary_only=False),
            lambda case: case.update(provider_fallback_disabled=False),
            lambda case: case.update(unexpected_wrappers=True),
            lambda case: case.update(model_requests=True),
            lambda case: case.update(model="gpt-6-luna"),
            lambda case: case["runtime"].update(catalog_sha256="stale"),
            lambda case: case.update(web_completion_simulated=not case["web_completion_simulated"]),
            lambda case: case["nested_tools"].remove("web__run") if case["case"] == "enabled-web"
                         else case["nested_tools"].append("web__run"),
        )
        for index in (0, 2):  # Web-enabled and web-disabled permitted lookup paths.
            for mutate in mutations:
                broken = copy.deepcopy(row)
                mutate(broken["cases"][index])
                with self.subTest(mode=row["cases"][index]["case"], mutation=mutate), self.assertRaises(RuntimeError):
                    validate_model_evidence(broken, pin, efforts)
        for key, value in (("disabled_tool_absent", False), ("injected_tool_denied", False),
                           ("disabled_callback_calls", 1)):
            broken = copy.deepcopy(row)
            broken["cases"][-1][key] = value
            with self.subTest(disabled_guard=key), self.assertRaises(RuntimeError):
                validate_model_evidence(broken, pin, efforts)

    def test_discovery_paginates_and_intersects_validated_efforts(self):
        calls = []
        pages = iter([
            {"data": [model_row(DEFAULT_MODEL), model_row("unknown")], "nextCursor": "next"},
            {"data": [model_row("gpt-6-luna", ("low", "medium", "ultra"))]},
        ])
        models = discover_models(lambda method, params: calls.append((method, params)) or next(pages), ALLOWED)
        self.assertEqual([row["id"] for row in models], list(ALLOWED))
        self.assertEqual(models[1]["efforts"], ["low", "medium"])
        self.assertEqual(calls[1][1]["cursor"], "next")
        self.assertTrue(all(params["includeHidden"] is False for _, params in calls))
        require_available("gpt-6-luna", "medium", models)
        with self.assertRaisesRegex(RuntimeError, "no fallback"):
            require_available("gpt-6-luna", "high", models)

    def test_discovery_rejects_cycles_duplicates_and_malformed_pages(self):
        for page in ({"data": [], "nextCursor": "repeat"}, {"data": None}, {"data": [None]},
                     {"data": [], "nextCursor": 2}, {"data": [model_row(DEFAULT_MODEL)] * 2}):
            with self.subTest(page=page), self.assertRaises(RuntimeError):
                discover_models(lambda *_: page, ALLOWED)
        self.assertEqual(discover_models(lambda *_: {"data": [model_row(DEFAULT_MODEL, hidden=True)]}, ALLOWED), [])

    def test_selected_model_and_effort_reach_thread_turn_and_answer_metadata(self):
        client, calls = fake_client()
        payload = request()
        payload["history"] = [{"question": "An earlier question", "answer": "An earlier answer"}]
        with patch.object(companion, "emit") as output:
            client.ask(payload)
        thread = next(params for method, params in calls if method == "thread/start")
        turn = next(params for method, params in calls if method == "turn/start")
        self.assertEqual(thread["model"], "gpt-6-luna")
        self.assertIs(thread["allowProviderModelFallback"], False)
        self.assertIn('"model": "gpt-6-luna", "reasoning_effort": "high"', thread["developerInstructions"])
        self.assertIn("supersede model/reasoning labels in previous chat messages", thread["developerInstructions"])
        self.assertIn("Do not claim to independently inspect", thread["developerInstructions"])
        self.assertEqual((turn["model"], turn["effort"]), ("gpt-6-luna", "high"))
        self.assertIn("An earlier question", turn["input"][1]["text"])
        result = output.call_args.args[0]
        self.assertEqual((result["question_model"], result["question_effort"]), ("gpt-6-luna", "high"))

    def test_preflight_unavailable_effort_never_starts_thread(self):
        client, calls = fake_client()
        original = client.call
        client.call = lambda method, params=None, **kwargs: ({"data": [model_row("gpt-6-luna", ("medium",))]}
            if method == "model/list" else original(method, params, **kwargs))
        with self.assertRaisesRegex(RuntimeError, "unavailable.*no fallback"):
            client.ask(request())
        self.assertNotIn("thread/start", [method for method, _ in calls])

    def test_request_cannot_switch_selection_after_process_or_during_preflight(self):
        client, calls = fake_client()
        value = request()
        value["model"] = DEFAULT_MODEL
        with self.assertRaisesRegex(RuntimeError, "does not match"):
            client.ask(value)
        self.assertEqual(calls, [])
        value = request()

        def mutate():
            value["reasoning_effort"] = "medium"
            return {}

        client.preflight = mutate
        with self.assertRaisesRegex(RuntimeError, "changed during"):
            client.ask(value)
        self.assertNotIn("turn/start", [method for method, _ in calls])

    def test_status_discovery_failure_keeps_authenticated_account_and_quota(self):
        client, _ = fake_client()
        client.available_models = lambda: (_ for _ in ()).throw(RuntimeError("private error body"))
        with patch.object(companion, "emit"):
            value = client.status()
        self.assertEqual(value["account"], {"type": "chatgpt", "planType": "plus"})
        self.assertEqual(value["quota_guard"], "available")
        self.assertIn("models_error", value)
        self.assertNotIn("private error", value["models_error"])

    def test_constructor_rejects_selection_before_process_start(self):
        with patch.object(companion, "find_runtime", return_value=Path("fixture.exe")), \
                patch.object(companion, "checked_pin", return_value={}), \
                patch.object(companion, "checked_model_selection", return_value=ALLOWED), \
                patch.object(companion.subprocess, "Popen") as spawn:
            for model, effort in (("unknown", "medium"), (DEFAULT_MODEL, "ultra")):
                with self.assertRaises(RuntimeError):
                    companion.Companion(model=model, effort=effort)
            spawn.assert_not_called()

    def test_proof_remains_baseline_only(self):
        client, calls = fake_client()
        with self.assertRaisesRegex(RuntimeError, "baseline"):
            client.prove()
        self.assertEqual(calls, [])


if __name__ == "__main__":
    unittest.main()
