"""Offline normalization, registry and provider contract checks; no network."""
from collections import deque
from copy import deepcopy
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import sys
import tempfile
import threading
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

import companion
import companion_process
from companion_provider import CodexProvider, CodexTurn, ProviderEvent, CompanionFailure
from companion_request import normalize_request, recheck_request
from companion_tools import build_registry, registry_instructions, TOOL_LABELS
from deferred_tools import WRAPPER_TOOLS
import inventory_question_check as questions
from model_selection import DEFAULT_MODEL, DEFAULT_EFFORT


def wire_request(version):
    request = questions.fixture() if version == 1 else {"version": version, "question": "Synthetic question", "snapshot": None}
    if version >= 3:
        request["history"] = []
    if version == 5:
        request["quests"] = None
    if version >= 6:
        request["quest_lookup"] = None
    if version >= 7:
        request.update(model=DEFAULT_MODEL, reasoning_effort=DEFAULT_EFFORT)
    if version >= 8:
        request["inventory_lookup"] = None
    if version >= 9:
        request.update(chat_notes="", history_lookup=None)
    if version >= 10:
        request["personality"] = None
    if version >= 11:
        request["game_context_lookup"] = None
    if version >= 13:
        request.update(spoilers_allowed=False, npc_dialogue_lookup=None, character_details_lookup=None)
    if version >= 14:
        request["personality_details_lookup"] = None
    if version >= 15:
        request["currency_lookup"] = None
    if version >= 16:
        request["duty_lookup"] = None
    if version >= 17:
        request.update(game_definitions_lookup=None, gc_supply_lookup=None, npc_target_lookup=None, attachments=[])
    if version >= 20:
        request["storage_lookup"] = None
    if version >= 21:
        request["provider"] = "codex"
    if version >= 22:
        request["valuation_lookup"] = None
    if version >= 23:
        request["collection_lookup"] = None
    if version >= 25:
        request["progression_lookup"] = None
    if version >= 28:
        request["crafting_log_lookup"] = None
    if version >= 31:
        request["menu_lookup"] = None
    if version >= 33:
        request["activity_lookup"] = None
    return request


class ArchitectureChecks(unittest.TestCase):
    def test_menu_permission_is_local_explicit_and_scoped_to_wire31(self):
        wire = wire_request(31)
        wire["menu_lookup"] = {"scope": "observed_menus", "categories": ["shops"]}
        wire["web_search"] = False
        approved = normalize_request(wire)
        registry = build_registry(approved, {"menus": lambda args: {}}, None, CompanionFailure)
        self.assertEqual(set(registry), {"query_menus"})
        self.assertFalse(approved.public_data_allowed)
        tool = registry["query_menus"]
        self.assertEqual(tool.validate_arguments({"category": "shops"})["category"], "shops")
        with self.assertRaises(RuntimeError): tool.validate_arguments({"category": "armoire"})
        with self.assertRaises(CompanionFailure):
            companion.require_lookup_permission("menus", {"category": "armoire"}, approved.menu_lookup)
        with self.assertRaises(CompanionFailure): build_registry(approved, {}, None, CompanionFailure)
        older = wire_request(30)
        older["menu_lookup"] = wire["menu_lookup"]
        with self.assertRaises(RuntimeError): normalize_request(older)
        wire["menu_lookup"] = None
        with self.assertRaises(RuntimeError): recheck_request(wire, approved)


    @staticmethod
    def account_status(provider):
        return {"provider": provider, "runtime_pin": "verified", "account": {"type": provider, "planType": "free"},
                "quota_guard": "available", "quota": {"primary": {"usedPercent": 0}},
                "models": [{"id": "auto", "name": "Automatic", "efforts": ["none"], "default_effort": "none", "supports_images": False}]}

    @staticmethod
    def fake_account_wrapper(provider, runtime, web_search=False):
        # Exercise shared contracts without launching an account runtime.
        client = companion.AccountCompanion.__new__(companion.AccountCompanion)
        if hasattr(runtime, 'question_provider'):
            adapter = runtime.question_provider()
            def open_with_quota(instructions, tools):
                quota = runtime.readiness()
                return adapter.open(instructions, tools), quota
            adapter.open_with_quota = open_with_quota
        client.provider_id, client.runtime = provider, runtime
        client.cancel_event = threading.Event()
        client.model, client.effort, client.web_search = "auto", "none", web_search
        return client

    def test_blocked_account_cli_never_starts_runtime_or_falls_back(self):
        for provider, class_name in (("copilot", "CopilotRuntime"),):
            for mode in ("status", "login"):
                with self.subTest(provider=provider, mode=mode):
                    factory = Mock(side_effect=AssertionError("Disabled runtime must never start"))
                    emitted = []
                    with patch.dict(sys.modules, {provider + "_provider": SimpleNamespace(**{class_name: factory})}), \
                            patch.object(companion, "emit", side_effect=emitted.append), \
                            patch.object(companion, "Companion", side_effect=AssertionError("Unexpected Codex fallback")), \
                            patch.object(sys, "argv", ["companion.py", "--provider", provider, mode]):
                        self.assertEqual(companion.main(), 1)
                    factory.assert_not_called()
                    self.assertEqual(len(emitted), 1)
                    self.assertEqual(emitted[0]["error_kind"], "authentication")
                    self.assertIn("provider is disabled", emitted[0]["error"])

    def test_account_wrapper_rejects_status_shapes_that_cannot_authorize_csharp_ui(self):
        valid = self.account_status("grok")
        invalid = [None, {}, {**valid, "provider": "copilot"}, {**valid, "runtime_pin": "unchecked"},
                   {**valid, "account": None}, {**valid, "account": {"type": "chatgpt"}},
                   {**valid, "account": {"type": None}}, {**valid, "quota_guard": "ok"},
                   {**valid, "models": "auto"}]
        for status in invalid:
            runtime = SimpleNamespace(status=lambda: status)
            with self.subTest(status=status), patch.object(companion, "emit") as emit:
                client = self.fake_account_wrapper("grok", runtime)
                with self.assertRaises(RuntimeError):
                    client.status()
                emit.assert_not_called()
        blocked = {**valid, "account": {"type": None}, "quota_guard": "blocked", "models": []}
        with patch.object(companion, "emit") as emit:
            self.fake_account_wrapper("grok", SimpleNamespace(status=lambda: blocked)).status()
            emit.assert_called_once_with(blocked)

    def test_account_providers_are_blocked_before_runtime_creation_without_fallback(self):
        for provider, class_name in (("copilot", "CopilotRuntime"),):
            factory = Mock(side_effect=AssertionError("Disabled runtime must never start"))
            with self.subTest(provider=provider), \
                    patch.dict(sys.modules, {provider + "_provider": SimpleNamespace(**{class_name: factory})}), \
                    patch.object(companion, "Companion", side_effect=AssertionError("Unexpected Codex fallback")), \
                    patch.object(companion, "emit") as emit:
                with self.assertRaisesRegex(CompanionFailure, "provider is disabled"):
                    companion.AccountCompanion(provider, threading.Event())
                factory.assert_not_called()
                emit.assert_not_called()

    def test_grok_routes_to_its_isolated_profile_without_codex_fallback(self):
        runtime = SimpleNamespace(status=lambda: self.account_status("grok"))
        factory = Mock(return_value=runtime)
        cancelled = threading.Event()
        with patch.dict(sys.modules, {"grok_provider": SimpleNamespace(GrokRuntime=factory)}), \
                patch.object(companion, "Companion", side_effect=AssertionError("Unexpected Codex fallback")):
            client = companion.AccountCompanion("grok", cancelled, model="auto", effort="none")
        self.assertIs(client.runtime, runtime)
        args, options = factory.call_args
        self.assertEqual(args, (companion.ROOT / "runtime/grok-win-x64/grok.exe",
                                companion.PROFILE / "providers/grok"))
        self.assertIs(options["cancel_event"], cancelled)
        self.assertEqual((options["model"], options["effort"]), ("auto", "none"))

    def test_copilot_status_matches_shared_account_wire_contract(self):
        from copilot_provider import CopilotRuntime
        runtime = object.__new__(CopilotRuntime)
        runtime.auth_snapshot = lambda: ({"isAuthenticated": True, "copilotPlan": "free"},
            {"quotaSnapshots": {"chat": {"remainingPercentage": 100, "overageAllowedWithExhaustedQuota": False,
                                         "overage": 0, "isUnlimitedEntitlement": False}}})
        status = runtime.status()
        self.assertEqual((status["provider"], status["runtime_pin"], status["account"]["type"], status["quota_guard"]),
                         ("copilot", "verified", "copilot", "available"))
        self.assertEqual([(row["id"], row["default_effort"]) for row in status["models"]], [("auto", "none")])

    def test_account_wrapper_uses_shared_permission_tools_and_rejects_provider_changes(self):
        import shared_tools_integration_check as shared
        for provider in ("copilot", "grok"):
            for online in (False, True):
                wire = wire_request(21)
                wire.update(shared.request(web_search=online))
                wire.update(version=21, provider=provider, model="auto", reasoning_effort="none")
                selected = ["query_personality_details"] + (["get_item"] if online else [])
                events = deque([
                    ProviderEvent(kind="tool", request_id="discover", is_request=True, strictly_correlated=True,
                        tool="discover_tools", arguments={"names": selected}),
                    ProviderEvent(kind="tool", request_id="local", is_request=True, strictly_correlated=True,
                        tool="invoke_tool", arguments={"name": "query_personality_details", "arguments": {"section": "opinions"}})])
                if online:
                    events.append(ProviderEvent(kind="tool", request_id="public", is_request=True, strictly_correlated=True,
                        tool="invoke_tool", arguments={"name": "get_item", "arguments": {"item_id": 5121}}))
                events.extend([ProviderEvent(kind="answer", text='{"answer":"Synthetic account answer."}'),
                               ProviderEvent(kind="completed", status="completed")])
                turn = SimpleNamespace(read=lambda **_: events.popleft(), reply_tool=Mock(), finish=Mock(),
                                       cancel=Mock(), reject_tool=Mock(), reject_request=Mock())
                question_provider = SimpleNamespace(open=Mock(return_value="isolated-session"), start=Mock(return_value=turn))
                runtime = SimpleNamespace(question_provider=lambda: question_provider, readiness=Mock(return_value={"primary": {"usedPercent": 0}}))
                public = shared.FakeGameDataTools()
                emitted, reads = [], []
                with self.subTest(provider=provider, online=online), \
                        patch.object(companion, "emit", side_effect=emitted.append), \
                        patch.object(companion, "GameDataTools", return_value=public) as public_factory:
                    client = self.fake_account_wrapper(provider, runtime, web_search=online)
                    wrong = {**wire, "provider": "grok" if provider == "copilot" else "copilot"}
                    with self.assertRaisesRegex(RuntimeError, "provider does not match"):
                        client.ask(wrong)
                    runtime.readiness.assert_not_called()
                    client.ask(wire, personality_details_query=lambda args: reads.append(args) or {**args, "text": "Fixture opinion"})
                self.assertEqual(len(reads), 1)
                self.assertEqual(public_factory.call_count, int(online))
                self.assertEqual(len(public.calls), int(online))
                self.assertEqual(turn.reply_tool.call_count, 2 + int(online))
                question_provider.start.assert_called_once()
                instructions, tools = question_provider.open.call_args.args
                self.assertIn(f"shared allowance of {companion.MAX_LOOKUP_CALLS} tool lookups", instructions)
                self.assertIn(f"{companion.QUESTION_TIMEOUT_SECONDS} seconds to finish", instructions)
                self.assertNotIn("Never call a sampled winner the most valuable item", instructions)
                self.assertIn('"provider": "' + provider + '"', instructions)
                self.assertIn('"reasoning_effort": "provider default"', instructions)
                self.assertEqual({row["name"] for row in tools}, WRAPPER_TOOLS)
                discovery = turn.reply_tool.call_args_list[0].args[2]
                self.assertEqual([row["name"] for row in discovery["tools"]], selected)
                self.assertIn("inputSchema", discovery["tools"][0])
                from personality_details_lookup import INSTRUCTIONS as persona_instructions
                self.assertNotIn(persona_instructions, instructions)
                self.assertIn(persona_instructions, discovery["instructions"])
                if online:
                    self.assertIn("no hosted web-search tool", instructions)
                self.assertEqual(next(row["question_answer"] for row in emitted if "question_answer" in row), "Synthetic account answer.")
                turn.cancel.assert_not_called()

    def test_valuation_guidance_requires_both_storage_and_market_tools(self):
        import shared_tools_integration_check as shared
        import storage_lookup
        for online in (False, True):
            wire = wire_request(21)
            wire.update(provider="grok", model="auto", reasoning_effort="none", web_search=online,
                        storage_lookup={"scope": storage_lookup.SCOPE, "categories": ["retainers"]})
            events = deque([ProviderEvent(kind="answer", text='{"answer":"Choose a smaller scope."}'),
                            ProviderEvent(kind="completed", status="completed")])
            turn = SimpleNamespace(read=lambda **_: events.popleft(), finish=Mock(), cancel=Mock())
            provider = SimpleNamespace(open=Mock(return_value="isolated-session"), start=Mock(return_value=turn))
            runtime = SimpleNamespace(question_provider=lambda: provider, readiness=Mock(return_value={}))
            query = Mock(side_effect=AssertionError("Instruction setup must not fetch inventory"))
            with self.subTest(online=online), patch.object(companion, "emit"), \
                    patch.object(companion, "GameDataTools", return_value=shared.FakeGameDataTools()):
                self.fake_account_wrapper("grok", runtime, web_search=online).ask(wire, storage_query=query)
            instructions = provider.open.call_args.args[0]
            self.assertEqual("Never call a sampled winner the most valuable item" in instructions, online)
            query.assert_not_called()
            turn.cancel.assert_not_called()

    def test_question_deadline_explains_limit_and_cancels_without_retry(self):
        wire = wire_request(21)
        wire.update(provider="grok", model="auto", reasoning_effort="none")
        turn = SimpleNamespace(read=Mock(side_effect=TimeoutError), finish=Mock(), cancel=Mock())
        provider = SimpleNamespace(open=Mock(return_value="isolated-session"), start=Mock(return_value=turn))
        runtime = SimpleNamespace(question_provider=lambda: provider, readiness=Mock(return_value={}))
        emitted = []
        with patch.object(companion, "emit", side_effect=emitted.append), \
                patch.object(companion.time, "monotonic", side_effect=[0, 0, 0, companion.QUESTION_TIMEOUT_SECONDS + 1]):
            with self.assertRaises(CompanionFailure) as failed:
                self.fake_account_wrapper("grok", runtime).ask(wire)
        error = companion.failure_message(failed.exception, "provider.request")
        self.assertIn(f"{companion.QUESTION_TIMEOUT_SECONDS}-second response limit", error["error"])
        self.assertIn("Response limit in Settings > Account", error["error"])
        self.assertIn("no automatic retry", error["error"])
        self.assertEqual((error["error_kind"], error["error_stage"]), ("provider", "provider.request"))
        provider.start.assert_called_once()
        turn.read.assert_called_once()
        turn.finish.assert_called_once()
        turn.cancel.assert_called_once()
        self.assertFalse(any("question_answer" in row for row in emitted))

    def test_all_legacy_versions_normalize_and_keep_omitted_context_contracts(self):
        for version in range(1, 44):
            with self.subTest(version=version):
                wire = wire_request(version)
                original = deepcopy(wire)
                request = normalize_request(wire)
                self.assertEqual(wire, original)
                self.assertEqual((request.model, request.effort), (DEFAULT_MODEL, DEFAULT_EFFORT))
                self.assertFalse(request.web_search)
                self.assertFalse(request.public_data_allowed)
                self.assertEqual(request.optional_web_search, version >= 6)
                context = request.context({"fixture": True})
                fields = {"inventory", "reference"}
                if version >= 3:
                    fields.add("history")
                if version >= 9:
                    fields.add("chat_notes")
                if version >= 10:
                    fields.add("personality")
                if version >= 13:
                    fields.add("spoilers_allowed")
                self.assertEqual(set(context), fields)
                self.assertEqual(build_registry(request, {}, None, CompanionFailure), {})
                recheck_request(wire, request)

    def test_explicit_provider_is_bound_without_legacy_fallback(self):
        self.assertEqual(normalize_request(wire_request(20)).provider, "codex")
        for provider in ("codex", "copilot", "grok"):
            wire = wire_request(21)
            wire["provider"] = provider
            approved = normalize_request(wire)
            self.assertEqual(approved.provider, provider)
            wire["provider"] = "grok" if provider != "grok" else "copilot"
            with self.assertRaises(RuntimeError): recheck_request(wire, approved)
        wire = wire_request(21)
        for invalid in (None, "", "openai-api", True, [], {}):
            wire["provider"] = invalid
            with self.assertRaises(RuntimeError): normalize_request(wire)
        wire = wire_request(20)
        wire["provider"] = "copilot"
        with self.assertRaises(RuntimeError): normalize_request(wire)

    def test_target_summary_never_upgrades_legacy_npc_permission_to_player_access(self):
        old_summary = {"scope": "observed_target_npc"}
        new_summary = {"scope": "observed_selected_target", "allow_npcs": True, "allow_players_and_objects": True}
        for version, summary in ((17, old_summary), (18, new_summary)):
            wire = wire_request(version)
            wire["npc_target_lookup"] = deepcopy(summary)
            approved = normalize_request(wire)
            self.assertEqual(approved.npc_target_lookup, summary)
            self.assertEqual(approved.context({})["npc_target_lookup"], summary)
            wire["npc_target_lookup"] = deepcopy(new_summary if version == 17 else old_summary)
            with self.subTest(version=version), self.assertRaises(RuntimeError): normalize_request(wire)
        for allow_npcs, allow_players in ((True, False), (False, True)):
            wire = wire_request(18)
            wire["npc_target_lookup"] = {"scope": "observed_selected_target", "allow_npcs": allow_npcs,
                                         "allow_players_and_objects": allow_players}
            approved = normalize_request(wire)
            self.assertEqual(approved.npc_target_lookup, wire["npc_target_lookup"])
            wire["npc_target_lookup"] = None
            with self.assertRaises(RuntimeError): recheck_request(wire, approved)
        for invalid in ({"scope": "observed_selected_target"}, {**new_summary, "allow_npcs": 1},
                        {**new_summary, "allow_players_and_objects": "true"}, {**new_summary, "players": True},
                        {**new_summary, "allow_npcs": False, "allow_players_and_objects": False}):
            wire = wire_request(18)
            wire["npc_target_lookup"] = invalid
            with self.subTest(summary=invalid), self.assertRaises(RuntimeError): normalize_request(wire)

    def test_item_and_map_definitions_require_matching_wire_scope_and_stay_local_without_web(self):
        old = {"scope": "local_game_definitions"}
        v2 = {"scope": "local_game_definitions_v2"}
        current = {"scope": "local_game_definitions_v3"}
        for version, summary in ((17, old), (18, old), (19, v2), (28, v2), (29, current), (30, current), (31, current)):
            wire = wire_request(version)
            wire["game_definitions_lookup"] = deepcopy(summary)
            wire["web_search"] = False
            approved = normalize_request(wire)
            self.assertFalse(approved.public_data_allowed)
            registry = build_registry(approved, {"game_definitions": lambda args: {}}, None, CompanionFailure)
            self.assertEqual(set(registry), {"query_game_definitions"})
            tool = registry["query_game_definitions"]
            item = {"kind": "items", "row_id": 5121}
            if version >= 19:
                self.assertEqual(tool.validate_arguments(item)["row_id"], 5121)
            else:
                with self.assertRaises(RuntimeError): tool.validate_arguments(item)
            maps = {"kind": "maps", "row_id": 800}
            if version >= 29:
                self.assertEqual(tool.validate_arguments(maps)["row_id"], 800)
                self.assertIn("compai-map://", registry_instructions(registry))
            else:
                with self.assertRaises(RuntimeError): tool.validate_arguments(maps)
                self.assertNotIn("compai-map://", registry_instructions(registry))
            for wrong in (old, v2, current):
                if wrong == summary:
                    continue
                wire["game_definitions_lookup"] = deepcopy(wrong)
                with self.subTest(version=version, scope=wrong["scope"]), self.assertRaises(RuntimeError):
                    normalize_request(wire)
        for field in ("game_definitions_lookup", "web_search"):
            wire = wire_request(31)
            wire["game_definitions_lookup"] = current
            wire["web_search"] = False
            approved = normalize_request(wire)
            wire[field] = None if field == "game_definitions_lookup" else True
            with self.assertRaises(RuntimeError): recheck_request(wire, approved)

    def test_boundary_never_upgrades_unsupported_fields_or_public_permissions(self):
        for version in range(1, 14):
            wire = wire_request(version)
            wire["personality_details_lookup"] = None
            with self.subTest(version=version), self.assertRaises(RuntimeError):
                normalize_request(wire)
        for version in (3, 5, 6, 7, 13, 14):
            wire = wire_request(version)
            wire["web_search"] = True
            request = normalize_request(wire)
            self.assertEqual(request.public_data_allowed, version == 14)
            if version != 14:
                with self.assertRaises(CompanionFailure) as failure:
                    build_registry(request, {}, object(), CompanionFailure)
                self.assertEqual(failure.exception.kind, "permission")

    def test_preparation_has_no_nested_alias_and_rejects_valid_approved_data_changes(self):
        for field in ("question", "history", "chat_notes", "web_search", "spoilers_allowed"):
            wire = wire_request(14)
            wire["history"] = [{"question": "Previous", "answer": "Original"}]
            request = normalize_request(wire)
            if field == "history":
                wire[field][0]["answer"] = "Changed"
                self.assertEqual(request.history[0]["answer"], "Original")
            elif field in ("web_search", "spoilers_allowed"):
                wire[field] = True
            else:
                wire[field] = "Changed"
            with self.subTest(field=field), self.assertRaisesRegex(RuntimeError, "changed during"):
                recheck_request(wire, request)
        wire = questions.fixture()
        request = normalize_request(wire)
        wire["snapshot"]["captured_at_utc"] = (datetime.now(timezone.utc) - timedelta(seconds=31)).isoformat()
        with self.assertRaisesRegex(RuntimeError, "stale"):
            recheck_request(wire, request)

    def test_registry_definitions_instructions_and_validators_share_one_source(self):
        import shared_tools_integration_check as shared
        from personality_details_lookup import TOOL, INSTRUCTIONS
        request = normalize_request(shared.request(web_search=True))
        queries = []
        public = shared.FakeGameDataTools()
        registry = build_registry(request, {"personality_details": queries.append}, public, CompanionFailure)
        self.assertEqual(list(registry), ["query_personality_details", *shared.PUBLIC_ARGS])
        self.assertTrue(set(registry) <= TOOL_LABELS.keys())
        registered = registry["query_personality_details"]
        self.assertIs(registered.definition, TOOL)
        self.assertEqual(registered.validate_arguments({"section": "opinions"}), {"section": "opinions"})
        self.assertIn(INSTRUCTIONS, registry_instructions(registry))
        with self.assertRaises(RuntimeError):
            registered.validate_arguments({"section": "background"})
        with self.assertRaises(CompanionFailure):
            build_registry(request, {}, public, CompanionFailure)

    def test_application_accepts_provider_contract_without_app_server_events(self):
        import shared_tools_integration_check as shared
        wire = shared.request()
        client, calls = questions.QuestionChecks().client([])
        operations, replies, events = [], [], deque([
            ProviderEvent(kind="tool", request_id="discover", is_request=True, strictly_correlated=True,
                          tool="discover_tools", arguments={"names": ["query_personality_details"]}),
            ProviderEvent(kind="tool", request_id="one", is_request=True, strictly_correlated=True,
                          tool="invoke_tool", arguments={"name": "query_personality_details", "arguments": {"section": "opinions"}}),
            ProviderEvent(kind="usage", usage={"input_tokens": 31, "cached_input_tokens": None,
                                               "output_tokens": 4, "reasoning_tokens": 0}),
            ProviderEvent(kind="answer", text='{"answer":"Fixture answer."}'),
            ProviderEvent(kind="completed", status="completed"),
        ])
        turn = SimpleNamespace(read=lambda **_: events.popleft(),
            reply_tool=lambda event, success, result: replies.append((event, success, result)),
            finish=lambda: operations.append("finish"), cancel=lambda: self.fail("Successful turn cancelled"))
        provider = SimpleNamespace(models=lambda: [{"fixture": True}], readiness=lambda: {"fixture": True},
            open=lambda instructions, tools: operations.append(("open", tools)) or "independent-provider",
            start=lambda *args: operations.append(("start", args[1:4])) or turn)
        client.question_provider = lambda: provider
        # Exercise the normal readiness wrapper too, rather than the legacy fixture stub.
        client.preflight = lambda: companion.Companion.preflight(client)
        self.assertEqual(client.available_models(), [{"fixture": True}])
        output = []
        with patch.object(companion, "emit", side_effect=output.append):
            client.ask(wire, personality_details_query=lambda args: {**args, "text": "Synthetic opinion"})
        self.assertEqual(calls, [])
        self.assertEqual(len(replies), 2)
        self.assertTrue(all(reply[1] for reply in replies))
        self.assertEqual(replies[0][2]["tools"][0]["name"], "query_personality_details")
        self.assertEqual(replies[1][2], {"section": "opinions", "text": "Synthetic opinion"})
        self.assertEqual(operations[0][0], "open")
        self.assertEqual(operations[1][0], "start")
        self.assertEqual(operations[-1], "finish")
        self.assertEqual(output[-1]["question_answer"], "Fixture answer.")
        self.assertEqual(next(row["performance"]["input_tokens"] for row in output
                              if row.get("performance", {}).get("stage") == "provider.tokens"), 31)

    def test_adapter_preserves_usage_correlation_and_unknown_counters(self):
        messages = deque()
        transport = SimpleNamespace(receive=lambda **_: messages.popleft())
        turn = CodexTurn(transport, "thread", "turn", lambda _: None)
        def usage(thread="thread", turn_id="turn", total=None):
            return {"method": "thread/tokenUsage/updated", "params": {"threadId": thread, "turnId": turn_id,
                "tokenUsage": {"total": total, "last": {"inputTokens": 999}}}}
        messages.extend([usage(total={"inputTokens": 12, "cachedInputTokens": 5, "outputTokens": True}),
                         usage(thread="other", total={"inputTokens": 100}), usage(total=None)])
        first = turn.read(1)
        self.assertEqual(first.usage, {"input_tokens": 12, "cached_input_tokens": 5,
                                     "output_tokens": None, "reasoning_tokens": None})
        self.assertFalse(turn.read(1).correlated)
        self.assertEqual(turn.read(1).usage, dict.fromkeys(first.usage))

    def test_adapter_rejects_inherited_servers_before_model_dispatch(self):
        calls = []
        def call(method, params, **kwargs):
            calls.append(method)
            if method == "thread/start":
                self.assertIs(params["allowProviderModelFallback"], False)
                return {"thread": {"id": "thread"}}
            return {"data": [{"fixture": "unexpected MCP"}], "nextCursor": None}
        provider = CodexProvider(SimpleNamespace(call=call, model=DEFAULT_MODEL), Path("."))
        with self.assertRaisesRegex(RuntimeError, "inherited MCP"):
            provider.open("instructions", [])
        self.assertEqual(calls, ["thread/start", "mcpServerStatus/list"])

    def test_adapter_cancellation_is_once_and_bypasses_cancelled_receive_guard(self):
        calls = []
        turn = CodexTurn(SimpleNamespace(call=lambda *args, **kwargs: calls.append((args, kwargs))),
                         "thread", "turn", lambda _: None)
        turn.cancel()
        turn.cancel()
        self.assertEqual(calls, [(("turn/interrupt", {"threadId": "thread", "turnId": "turn"}),
                                  {"ignore_cancel": True, "timeout": 1})])

    def test_error_stages_are_allowlisted_and_do_not_inspect_private_error_text(self):
        cases = [(CompanionFailure("authentication", "Session rejected"), "provider.authentication"),
                 (CompanionFailure("quota", "Allowance unavailable"), "provider.quota"),
                 (CompanionFailure("provider", "Unable to start", stage="provider.start"), "provider.start"),
                 (ValueError("private-path auth rejected quota"), "runtime.integrity")]
        for error, expected in cases:
            output = companion.failure_message(error, "runtime.integrity")
            self.assertEqual(output["error_stage"], expected)
            self.assertNotIn("private-path", json.dumps(output))
        self.assertEqual(companion.failure_message(ValueError(), "unknown")['error_stage'], "provider.request")
        unexpected = RuntimeError("Synthetic failure")
        unexpected.stage = "private-path"
        self.assertEqual(companion.failure_message(unexpected, "unknown")['error_stage'], "provider.request")
        self.assertNotIn("error_stage", companion.failure_message(ValueError()))

    def test_containment_failures_preserve_cleanup_and_keep_launch_errors_distinct(self):
        for failure in ("create", "configure", "assign", "resume", "launch"):
            with self.subTest(failure=failure):
                api = SimpleNamespace(CreateJobObjectW=Mock(return_value=0 if failure == "create" else 12),
                    SetInformationJobObject=Mock(return_value=failure != "configure"),
                    AssignProcessToJobObject=Mock(return_value=failure != "assign"), CloseHandle=Mock())
                process = SimpleNamespace(_handle=11, _companion_job=None, poll=lambda: None,
                    kill=Mock(), wait=Mock(), stdin=Mock(), stdout=Mock(), stderr=Mock())
                launch_error = OSError("private executable path")
                expected = OSError if failure == "launch" else companion_process.ProcessContainmentError
                with patch.object(companion_process, "_kernel", return_value=api), \
                        patch.object(companion_process.subprocess, "Popen", return_value=process,
                                     side_effect=launch_error if failure == "launch" else None) as launch, \
                        patch.object(companion_process, "_resume_process",
                                     side_effect=OSError("private error") if failure == "resume" else None), \
                        self.assertRaises(expected) as caught:
                    companion_process.start_owned_process(["synthetic-runtime"])
                if failure == "launch":
                    self.assertIs(caught.exception, launch_error)
                if failure in ("create", "configure"):
                    launch.assert_not_called()
                if failure in ("assign", "resume"):
                    process.kill.assert_called_once()
                    process.wait.assert_called_once_with(timeout=5)
                    self.assertIsNone(process._companion_job)
                    process.stdin.close.assert_called_once()
                    process.stdout.close.assert_called_once()
                    process.stderr.close.assert_called_once()
                else:
                    process.kill.assert_not_called()
                if failure == "create":
                    api.CloseHandle.assert_not_called()
                else:
                    api.CloseHandle.assert_called_once_with(12)

    def test_companion_maps_typed_containment_separately_from_launch_failure(self):
        for error, expected in ((companion_process.ProcessContainmentError("private details"), "runtime.containment"),
                                (OSError("private executable path"), "provider.start")):
            with self.subTest(expected=expected), tempfile.TemporaryDirectory() as directory:
                with patch.object(companion, "PROFILE", Path(directory)), \
                        patch.object(companion, "find_runtime", return_value=Path("synthetic-runtime")), \
                        patch.object(companion, "checked_pin", return_value={}), \
                        patch.object(companion, "checked_model_selection", return_value=[]), \
                        patch.object(companion, "require_permitted"), patch.object(companion, "emit"), \
                        patch.object(companion, "start_owned_process", side_effect=error), \
                        self.assertRaises(CompanionFailure) as caught:
                    companion.Companion()
                output = companion.failure_message(caught.exception, "runtime.integrity")
                self.assertEqual(output["error_stage"], expected)
                self.assertNotIn("private details", json.dumps(output))
                self.assertNotIn("private executable", json.dumps(output))


if __name__ == "__main__":
    unittest.main()
