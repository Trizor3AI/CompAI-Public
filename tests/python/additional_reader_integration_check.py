"""Synthetic reader routing through the real bridge and fake provider transport."""
import deferred_fixture
from copy import deepcopy
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import sys
import threading
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "bridge"))

import companion
import game_definitions_lookup
import gc_supply_lookup
import npc_target_lookup
from companion_architecture_check import wire_request
from companion_request import normalize_request, recheck_request
import inventory_question_check as question_checks
from quest_lookup import QuestQueryBridge
from quest_lookup_check import completed, tool_message

READERS = {
    "game_definitions": (game_definitions_lookup, "game-definitions-results.json", "recipe"),
    "gc_supply": (gc_supply_lookup, "gc-supply-result.json", "all_jobs"),
    "npc_target": (npc_target_lookup, "npc-target-result.json", "event_npc"),
}


def summary(kind, *, allow_npcs=True, allow_players=True, version=19):
    if kind == "npc_target":
        return {"scope": "observed_target_npc"} if version == 17 else {
            "scope": "observed_selected_target", "allow_npcs": allow_npcs, "allow_players_and_objects": allow_players}
    if kind == "game_definitions" and version < 19:
        return {"scope": "local_game_definitions"}
    return {"scope": READERS[kind][0].SCOPE}


def tool(kind, approved=None):
    module = READERS[kind][0]
    return module.permitted_tool(approved or summary(kind)) if kind in ("npc_target", "game_definitions") else module.TOOL


def instructions(kind, approved=None):
    module = READERS[kind][0]
    return module.permitted_instructions(approved or summary(kind)) if kind in ("npc_target", "game_definitions") else module.INSTRUCTIONS


def request(enabled=(), *, version=19, allow_npcs=True, allow_players=True):
    value = wire_request(version)
    value["web_search"] = False
    for kind in enabled:
        value[kind + "_lookup"] = summary(kind, version=version, allow_npcs=allow_npcs, allow_players=allow_players)
    return value


def event(kind, arguments, ident=50, approved=None):
    return tool_message(ident=ident, tool=tool(kind, approved)["name"], arguments=arguments)


def prepared(events):
    client, calls = deferred_fixture.client(events)
    selected = request()
    client.model, client.effort, client.web_search = selected["model"], selected["reasoning_effort"], False
    outputs = []
    client.send = outputs.append
    return client, calls, outputs


def context_from(calls):
    turn = next(params for method, params in calls if method == "turn/start")
    return json.loads(turn["input"][1]["text"].split("\n", 1)[1])


class AdditionalReaderIntegrationChecks(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.fixtures = {}
        for kind, (_, filename, _) in READERS.items():
            path = ROOT / ".test-tmp" / filename
            if not path.is_file():
                raise RuntimeError("Required synthetic C# producer fixture missing: " + str(path))
            cls.fixtures[kind] = json.loads(path.read_text(encoding="utf-8"))
            if not isinstance(cls.fixtures[kind], list) or not cls.fixtures[kind]:
                raise RuntimeError("Synthetic producer fixtures must be nonempty arrays")

    def fixture(self, kind, name=None):
        selected = name or READERS[kind][2]
        fixture = deepcopy(next(value for value in self.fixtures[kind] if value["name"] == selected))
        if kind in ("gc_supply", "npc_target"):
            # Refresh synthetic timestamps only in memory; preserve the explicitly stale unavailable example.
            now = datetime.now(timezone.utc)
            if selected == "stale":
                now -= timedelta(seconds=31)
            fixture["result"]["captured_at_utc"] = now.isoformat()
        return fixture

    def test_v18_initial_context_contains_only_exact_permission_summaries(self):
        payload = request(READERS)
        approved = normalize_request(payload)
        self.assertFalse(approved.web_search)
        self.assertFalse(approved.public_data_allowed)
        context = approved.context({})
        for kind, (module, _, _) in READERS.items():
            field = kind + "_lookup"
            self.assertEqual(context[field], summary(kind))
            self.assertIsNone(getattr(normalize_request(request()), field))
            legacy = wire_request(16)
            legacy[field] = None
            with self.subTest(kind=kind), self.assertRaises(RuntimeError):
                normalize_request(legacy)
            injected = deepcopy(payload)
            injected[field]["entries"] = []
            with self.assertRaises(RuntimeError):
                normalize_request(injected)
            revoked = deepcopy(payload)
            revoked[field] = None
            with self.assertRaises(RuntimeError):
                recheck_request(revoked, approved)
        self.assertNotIn('"entries"', json.dumps(context))
        self.assertNotIn('"npc"', json.dumps(context))

    def test_each_csharp_fixture_routes_after_turn_start_with_online_disabled(self):
        for kind, (module, _, _) in READERS.items():
            for original in self.fixtures[kind]:
                with self.subTest(kind=kind, fixture=original["name"]):
                    fixture = self.fixture(kind, original["name"])
                    client, calls, outputs = prepared([event(kind, fixture["arguments"]), *completed()])
                    reads = []

                    def query(arguments):
                        self.assertIn("turn/start", [method for method, _ in calls])
                        reads.append(arguments)
                        return deepcopy(fixture["result"])

                    with patch.object(companion, "emit") as emitted, patch.object(companion, "GameDataTools") as public:
                        client.ask(request([kind]), **{kind + "_query": query})
                    public.assert_not_called()
                    self.assertEqual(reads, [module.validate_arguments(fixture["arguments"])])
                    thread = next(params for method, params in calls if method == "thread/start")
                    deferred_fixture.assert_exposed(self, client, calls, [tool(kind)])
                    self.assertIn(instructions(kind), deferred_fixture.instructions(client, calls))
                    context = context_from(calls)
                    self.assertEqual(context[kind + "_lookup"], summary(kind))
                    self.assertNotIn('"entries"', json.dumps(context))
                    self.assertNotIn('"npc"', json.dumps(context))
                    self.assertTrue(outputs[0]["result"]["success"])
                    transported = json.loads(outputs[0]["result"]["contentItems"][0]["text"])
                    self.assertEqual(transported, fixture["result"])
                    self.assertFalse(emitted.call_args.args[0]["search_performed"])
                    self.assertTrue(any(call.args[0].get("performance", {}).get("stage") == "tool." + kind
                                        for call in emitted.call_args_list))

    def test_player_and_npc_scopes_are_enforced_on_csharp_results_before_transport(self):
        for fixture_name, allow_npcs, allow_players, accepted in (
                ("player", False, True, True), ("player", True, False, False),
                ("event_npc", True, False, True), ("event_npc", False, True, False),
                ("event_object", False, True, True), ("event_object", True, False, False)):
            with self.subTest(fixture=fixture_name, allow_npcs=allow_npcs, allow_players=allow_players):
                fixture = self.fixture("npc_target", fixture_name)
                payload = request(["npc_target"], allow_npcs=allow_npcs, allow_players=allow_players)
                permitted = payload["npc_target_lookup"]
                client, calls, outputs = prepared([event("npc_target", fixture["arguments"], approved=permitted), *completed()])
                reads = []

                def query(arguments):
                    reads.append(arguments)
                    return deepcopy(fixture["result"])

                with patch.object(companion, "emit"), patch.object(companion, "GameDataTools") as public:
                    if accepted:
                        client.ask(payload, npc_target_query=query)
                    else:
                        with self.assertRaises(companion.CompanionFailure) as error:
                            client.ask(payload, npc_target_query=query)
                        self.assertEqual(error.exception.kind, "protocol")
                public.assert_not_called()
                self.assertEqual(reads, [fixture["arguments"]])
                self.assertEqual(outputs[0]["result"]["success"], accepted)
                thread = next(params for method, params in calls if method == "thread/start")
                deferred_fixture.assert_exposed(self, client, calls, [tool("npc_target", permitted)])
                self.assertIn(instructions("npc_target", permitted), deferred_fixture.instructions(client, calls))
                self.assertEqual(context_from(calls)["npc_target_lookup"], permitted)
                if accepted:
                    result = json.loads(outputs[0]["result"]["contentItems"][0]["text"])
                    self.assertEqual(result, fixture["result"])
                else:
                    self.assertNotIn(fixture["result"]["target"]["name"], json.dumps(outputs))

    def test_target_flag_revocation_and_legacy_tool_names_cannot_cross_versions(self):
        payload = request(["npc_target"])
        approved = normalize_request(payload)
        for flag in ("allow_npcs", "allow_players_and_objects"):
            revoked = deepcopy(payload)
            revoked["npc_target_lookup"][flag] = False
            with self.subTest(flag=flag), self.assertRaises(RuntimeError): recheck_request(revoked, approved)
        for version, unexpected_tool in ((17, "query_selected_target"), (18, "query_npc_target")):
            client, _, outputs = prepared([tool_message(ident=55, tool=unexpected_tool, arguments={})])
            with self.subTest(version=version), patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure) as error:
                client.ask(request(["npc_target"], version=version),
                           npc_target_query=lambda _: self.fail("Cross-version tool must not reach native reader"))
            self.assertEqual(error.exception.kind, "permission")
            self.assertFalse(outputs[0]["result"]["success"])

    def test_v17_legacy_npc_summary_tool_and_response_remain_usable(self):
        fixture = self.fixture("npc_target", "event_npc")
        observed = fixture["result"]
        legacy = {key: value for key, value in observed.items() if key != "target"}
        legacy["scope"] = "observed_target_npc"
        legacy["npc"] = {key: observed["target"][key] for key in ("name", "data_id", "kind")}
        payload = request(["npc_target"], version=17)
        permitted = payload["npc_target_lookup"]
        client, calls, outputs = prepared([event("npc_target", {}, approved=permitted), *completed()])
        with patch.object(companion, "emit"), patch.object(companion, "GameDataTools") as public:
            client.ask(payload, npc_target_query=lambda _: legacy)
        public.assert_not_called()
        deferred_fixture.assert_exposed(self, client, calls, [tool("npc_target", permitted)])
        self.assertEqual(context_from(calls)["npc_target_lookup"], {"scope": "observed_target_npc"})
        self.assertEqual(json.loads(outputs[0]["result"]["contentItems"][0]["text"]), legacy)
        rejected = deepcopy(legacy)
        rejected["npc"]["kind"] = "player"
        client, _, outputs = prepared([event("npc_target", {}, approved=permitted)])
        with patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure) as error:
            client.ask(payload, npc_target_query=lambda _: rejected)
        self.assertEqual(error.exception.kind, "protocol")
        self.assertFalse(outputs[0]["result"]["success"])
        self.assertNotIn(rejected["npc"]["name"], json.dumps(outputs))

    def test_all_three_tools_route_independently_and_audit_actual_use(self):
        fixtures = {kind: self.fixture(kind) for kind in READERS}
        events = [event(kind, fixture["arguments"], 50 + index) for index, (kind, fixture) in enumerate(fixtures.items())]
        client, calls, outputs = prepared([*events, *completed()])
        reads = []

        def callback(kind):
            def query(arguments):
                reads.append((kind, arguments))
                return deepcopy(fixtures[kind]["result"])
            return query

        callbacks = {kind + "_query": callback(kind) for kind in READERS}
        with patch.object(companion, "emit") as emitted, patch.object(companion, "GameDataTools") as public:
            client.ask(request(READERS), **callbacks)
        public.assert_not_called()
        self.assertEqual([kind for kind, _ in reads], list(READERS))
        self.assertTrue(all(output["result"]["success"] for output in outputs))
        thread = next(params for method, params in calls if method == "thread/start")
        deferred_fixture.assert_exposed(self, client, calls, [tool(kind) for kind in READERS])
        audit = emitted.call_args.args[0]["question_context"]
        for phrase in ("local game-definition lookups", "Grand Company delivery-menu lookups", "selected-target lookups"):
            self.assertIn(phrase, audit)

    def test_absent_permissions_and_unused_readers_never_call_callbacks(self):
        def forbidden(_):
            self.fail("An absent or unrequested reader was called")

        callbacks = {kind + "_query": forbidden for kind in READERS}
        client, calls, _ = prepared(completed())
        with patch.object(companion, "emit"):
            client.ask(request(), **callbacks)
        self.assertEqual(next(params for method, params in calls if method == "thread/start")["dynamicTools"], [])
        for kind in READERS:
            with self.subTest(kind=kind):
                fixture = self.fixture(kind)
                client, _, outputs = prepared([event(kind, fixture["arguments"])])
                with patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure) as error:
                    client.ask(request(), **callbacks)
                self.assertEqual(error.exception.kind, "permission")
                self.assertFalse(outputs[0]["result"]["success"])
                client, _, _ = prepared(completed())
                with patch.object(companion, "emit"):
                    client.ask(request([kind]), **callbacks)
                client, calls, _ = prepared(completed())
                with self.assertRaises(companion.CompanionFailure) as error:
                    client.ask(request([kind]))
                self.assertEqual(error.exception.kind, "reader")
                self.assertFalse(calls)

    def test_invalid_arguments_are_correctable_without_invoking_the_reader(self):
        invalid = {"game_definitions": {"kind": "recipes", "row_id": 0},
                   "gc_supply": {"category": "expert"}, "npc_target": {"data_id": 123}}
        for kind in READERS:
            with self.subTest(kind=kind):
                fixture = self.fixture(kind)
                client, _, outputs = prepared([event(kind, invalid[kind]), event(kind, fixture["arguments"], 51), *completed()])
                reads = []
                with patch.object(companion, "emit"):
                    client.ask(request([kind]), **{kind + "_query": lambda arguments: reads.append(arguments) or deepcopy(fixture["result"])})
                self.assertEqual(len(reads), 1)
                self.assertFalse(outputs[0]["result"]["success"])
                self.assertTrue(outputs[1]["result"]["success"])

    def test_reader_permission_revocation_and_expired_observations_fail_closed(self):
        def revoked(_):
            raise PermissionError("Synthetic access revocation")

        for kind in READERS:
            fixture = self.fixture(kind)
            client, _, outputs = prepared([event(kind, fixture["arguments"])])
            with self.subTest(kind=kind), patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure) as error:
                client.ask(request([kind]), **{kind + "_query": revoked})
            self.assertEqual(error.exception.kind, "permission")
            self.assertFalse(outputs[0]["result"]["success"])
        for kind in ("gc_supply", "npc_target"):
            fixture = self.fixture(kind)
            fixture["result"]["captured_at_utc"] = (datetime.now(timezone.utc) - timedelta(seconds=31)).isoformat()
            client, _, outputs = prepared([event(kind, fixture["arguments"])])
            with self.subTest(stale=kind), patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure) as error:
                client.ask(request([kind]), **{kind + "_query": lambda _: fixture["result"]})
            self.assertEqual(error.exception.kind, "protocol")
            self.assertFalse(outputs[0]["result"]["success"])
            self.assertNotIn("Synthetic item", json.dumps(outputs))
            self.assertNotIn("Synthetic Merchant", json.dumps(outputs))

    def test_three_readers_share_the_existing_twelve_call_budget(self):
        fixtures = {kind: self.fixture(kind) for kind in READERS}
        order = [list(READERS)[index % 3] for index in range(13)]
        client, _, outputs = prepared([event(kind, fixtures[kind]["arguments"], 50 + index) for index, kind in enumerate(order)])
        reads = []
        callbacks = {kind + "_query": (lambda args, kind=kind: reads.append(kind) or deepcopy(fixtures[kind]["result"]))
                     for kind in READERS}
        with patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure):
            client.ask(request(READERS), **callbacks)
        self.assertEqual(reads, order[:12])
        self.assertEqual(len(outputs), 13)
        self.assertTrue(all(output["result"]["success"] for output in outputs[:12]))
        self.assertFalse(outputs[-1]["result"]["success"])

    def test_query_bridge_correlates_each_new_result_kind_id_and_single_reply(self):
        for kind, (module, _, _) in READERS.items():
            with self.subTest(kind=kind):
                fixture = self.fixture(kind)
                arguments = module.validate_arguments(fixture["arguments"])
                ready, cancelled = threading.Event(), threading.Event()
                messages, responses, failures = [], [], []

                def emit(value):
                    messages.append(value)
                    ready.set()

                bridge = QuestQueryBridge(cancelled, emit, timeout=2)

                def query():
                    try:
                        responses.append(bridge.query(arguments, kind))
                    except Exception as error:
                        failures.append(error)

                worker = threading.Thread(target=query, daemon=True)
                worker.start()
                try:
                    self.assertTrue(ready.wait(2))
                    with bridge.lock:
                        ident = bridge.pending
                    for wrong in set(READERS) - {kind}:
                        with self.assertRaises(RuntimeError):
                            bridge.accept({wrong + "_result": {"id": ident, "result": {}}})
                    for wrong_id in (ident + 1, True):
                        with self.assertRaises(RuntimeError):
                            bridge.accept({kind + "_result": {"id": wrong_id, "result": {}}})
                    bridge.accept({kind + "_result": {"id": ident, "result": fixture["result"]}})
                    worker.join(2)
                    self.assertFalse(worker.is_alive())
                    self.assertEqual(failures, [])
                    self.assertEqual(messages, [{kind + "_query": {"id": ident, "arguments": arguments}}])
                    self.assertEqual(responses, [fixture["result"]])
                    with self.assertRaises(RuntimeError):
                        bridge.accept({kind + "_result": {"id": ident, "result": {}}})
                finally:
                    cancelled.set()
                    worker.join(2)


if __name__ == "__main__":
    unittest.main()
