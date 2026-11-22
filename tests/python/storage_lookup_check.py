"""Offline storage permission, result and provider-pipe checks; synthetic data only."""
import deferred_fixture
from copy import deepcopy
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import threading
import unittest
from unittest.mock import patch

import companion
from companion_architecture_check import wire_request
from companion_request import normalize_request, recheck_request
from companion_tools import build_registry
import inventory_question_check as question_checks
from quest_lookup import QuestQueryBridge
from quest_lookup_check import completed, tool_message
import storage_lookup as storage


def summary(*categories):
    return {"scope": storage.SCOPE, "categories": list(categories or storage.CATEGORIES)}


def arguments(**changes):
    return {"view": "items", "category": "all", "query": "", "source": "", "offset": 0, "limit": 30, **changes}


def row(**changes):
    return {"category": "retainers", "source": "TEST RETAINER", "container": "RetainerMarket",
            "captured_at_utc": (datetime.now(timezone.utc) - timedelta(days=3)).isoformat(), "slot": 0,
            "item_id": 5121, "base_item_id": 5121, "name": "TEST ORE", "quantity": 12,
            "item_namespace": "item", "is_hq": False, "unit_price_gil": 550, **changes}


def result(**changes):
    return {"scope": storage.SCOPE, "view": "items", "total_matches": 1, "total_quantity": 12,
            "offset": 0, "next_offset": None, "rows": [row()], **changes}


def armoury_row():
    materia = [{"slot": slot, "materia_id": 0, "grade": 0, "item_id": None,
                "name": None, "is_empty": True} for slot in range(5)]
    materia[0] = {"slot": 0, "materia_id": 8, "grade": 0, "item_id": 5601,
                  "name": "TEST MATERIA", "is_empty": False}
    return row(category="armoury", source="Armoury Chest", container="ArmoryMainHand",
               quantity=1, unit_price_gil=None, materia=materia, materia_unavailable=None)


def request(categories=storage.CATEGORIES):
    payload = wire_request(20)
    payload["web_search"] = False
    payload["storage_lookup"] = summary(*categories)
    return payload


class StorageChecks(unittest.TestCase):
    def test_summary_is_permission_only_and_legacy_contracts_stay_closed(self):
        payload = request()
        prepared = normalize_request(payload)
        self.assertEqual(prepared.context({})["storage_lookup"], summary())
        self.assertNotIn("TEST RETAINER", json.dumps(prepared.context({})))
        payload["storage_lookup"]["categories"].remove("housing")
        self.assertIn("housing", prepared.storage_lookup["categories"])
        with self.assertRaises(RuntimeError): recheck_request(payload, prepared)
        for version in range(1, 20):
            legacy = wire_request(version)
            legacy["storage_lookup"] = None
            with self.subTest(version=version), self.assertRaises(RuntimeError): normalize_request(legacy)
        empty = wire_request(20)
        self.assertIsNone(normalize_request(empty).storage_lookup)
        del empty["storage_lookup"]
        with self.assertRaises(RuntimeError): normalize_request(empty)
        for invalid in (summary("housing", "housing"), {"scope": storage.SCOPE, "categories": []},
                        summary("unknown"), {**summary(), "source_key": "private"}):
            with self.subTest(summary=invalid), self.assertRaises(RuntimeError): storage.validate_summary(invalid)

    def test_schema_arguments_and_rows_enforce_each_category_permission(self):
        for category in storage.CATEGORIES:
            meta = summary(category)
            registry = build_registry(normalize_request(request([category])), {"storage": lambda _: {}}, None,
                                      companion.CompanionFailure)
            self.assertEqual(set(registry), {"query_storage", "search_saved_items"})
            self.assertEqual(registry["query_storage"].definition["inputSchema"]["properties"]["category"]["enum"],
                             ["all", category])
            self.assertEqual(registry["query_storage"].validate_arguments(arguments(category=category))["category"], category)
            allowed = row(category=category, unit_price_gil=550 if category == "retainers" else None)
            if category == "key_items":
                allowed.update(item_id=2000123, base_item_id=2000123, item_namespace="event_item", container="KeyItems")
            if category == "armoury":
                allowed = armoury_row()
            storage.validate_result(result(rows=[allowed], total_quantity=allowed["quantity"]), meta, arguments())
            forbidden = next(value for value in storage.CATEGORIES if value != category)
            with self.assertRaises(RuntimeError): registry["query_storage"].validate_arguments(arguments(category=forbidden))
            with self.assertRaises(RuntimeError): storage.validate_result(result(), meta, arguments(category=forbidden))
            with self.assertRaises(RuntimeError):
                storage.validate_result(result(rows=[row(category=forbidden, unit_price_gil=None)]), meta, arguments())

    def test_cached_prices_containers_and_pagination_remain_honest(self):
        storage.validate_result(result(), summary(), arguments())  # Historical observations are valid, not live.
        storage.validate_result(result(rows=[row(unit_price_gil=None)]), summary(), arguments())
        container = {key: value for key, value in row().items() if key in storage.COMMON_FIELDS}
        container.update(capacity=20, occupied_slots=1)
        storage.validate_result(result(view="containers", total_quantity=None, rows=[container]), summary(),
                                arguments(view="containers", query="retainer market", source="TEST"))
        storage.validate_result(result(total_matches=0, total_quantity=0, rows=[]), summary(), arguments())
        storage.validate_result(result(total_matches=2, total_quantity=24, next_offset=1), summary(), arguments(limit=1))
        storage.validate_result(result(total_matches=10000, total_quantity=9999990000, next_offset=1,
                                       rows=[row(quantity=999999)]), summary(), arguments(limit=1))
        for changes in ({"total_quantity": 13}, {"total_matches": 10001}, {"next_offset": 1},
                        {"offset": True}, {"rows": [row(unit_price_gil=0)]},
                        {"rows": [row(captured_at_utc=(datetime.now(timezone.utc) + timedelta(minutes=5)).isoformat())]},
                        {"rows": [row(quantity=True)]}, {"rows": [row(source_key="private")]},
                        {"rows": [row(name="x" * 257)]}, {"rows": [row(source="bad\nlabel")]},
                        {"rows": [row(category="housing")]}, {"rows": [row(slot=1000)]}):
            with self.subTest(changes=changes), self.assertRaises(RuntimeError):
                storage.validate_result(result(**changes), summary(), arguments())
        for args in (arguments(limit=0), arguments(limit=31), arguments(offset=True),
                     arguments(query="x" * 201), arguments(source="x" * 129), arguments(extra="private")):
            with self.subTest(args=args), self.assertRaises(RuntimeError): storage.validate_arguments(args)
        with self.assertRaises(RuntimeError): storage.validate_result(result(), summary(), arguments(query="other"))
        with self.assertRaises(RuntimeError): storage.validate_result(result(), summary(), arguments(source="other"))
        oversized = [row(slot=i, name="\u03b1" * 256) for i in range(30)]
        with self.assertRaises(RuntimeError):
            storage.validate_result(result(total_matches=30, total_quantity=360, rows=oversized), summary(), arguments())

    def test_crystals_and_event_items_preserve_identity_and_reject_namespace_confusion(self):
        crystals = row(category="crystals", container="Crystals", item_id=2, base_item_id=2, unit_price_gil=None)
        storage.validate_result(result(rows=[crystals]), summary("crystals"), arguments())
        retainer_crystals = row(container="RetainerCrystals", item_id=2, base_item_id=2, unit_price_gil=None)
        storage.validate_result(result(rows=[retainer_crystals]), summary("retainers"), arguments())
        event_item = row(category="key_items", container="KeyItems", item_id=2000123,
                         base_item_id=2000123, item_namespace="event_item", unit_price_gil=None)
        storage.validate_result(result(rows=[event_item]), summary("key_items"), arguments())
        legacy = row()
        del legacy["item_namespace"]
        storage.validate_result(result(rows=[legacy]), summary("retainers"), arguments())
        missing = dict(event_item)
        del missing["item_namespace"]
        invalid = [missing, {**event_item, "item_namespace": "item"},
                   {**event_item, "item_id": 2000124}, {**event_item, "base_item_id": 123},
                   {**event_item, "item_id": 3000000, "base_item_id": 3000000},
                   {**event_item, "is_hq": True}, {**event_item, "unit_price_gil": 550},
                   row(item_namespace="event_item"), row(item_id=2000123), row(item_id=3000000), row(base_item_id=2000123),
                   {**crystals, "item_namespace": "event_item"}]
        for malformed in invalid:
            with self.subTest(row=malformed), self.assertRaises(RuntimeError):
                storage.validate_result(result(rows=[malformed]), summary(), arguments())
        with self.assertRaises(RuntimeError):
            storage.validate_result(result(rows=[event_item]), summary("crystals"), arguments())

    def test_armoury_requires_complete_materia_and_separate_permission(self):
        observed = armoury_row()
        payload = result(rows=[observed], total_quantity=1)
        storage.validate_result(payload, summary("armoury"), arguments(category="armoury"))
        # Unknown sheet metadata does not turn a nonempty native slot into empty.
        unresolved = deepcopy(payload)
        unresolved["rows"][0]["materia"][0].update(item_id=None, name=None)
        storage.validate_result(unresolved, summary("armoury"), arguments())
        for mutate in (
            lambda r: r.pop("materia"),
            lambda r: r.update(materia=None),
            lambda r: r.update(materia=r["materia"][:4]),
            lambda r: r.update(materia_unavailable="missing"),
            lambda r: r["materia"][0].update(slot=1),
            lambda r: r["materia"][0].update(grade=21),
            lambda r: r["materia"][0].update(materia_id=65536),
            lambda r: r["materia"][0].update(is_empty=True),
            lambda r: r["materia"][0].update(item_id=True),
            lambda r: r["materia"][0].update(name="bad\nmetadata"),
            lambda r: r["materia"][1].update(item_id=5601),
            lambda r: r["materia"][1].update(grade=1),
            lambda r: r["materia"][1].update(is_empty=1),
            lambda r: r.update(unit_price_gil=100),
            lambda r: r.update(is_hq=True),
            lambda r: r.update(quantity=2),
            lambda r: r.update(slot=50),
            lambda r: r.update(category="retainers"),
        ):
            malformed = deepcopy(payload)
            mutate(malformed["rows"][0])
            with self.subTest(row=malformed), self.assertRaises(RuntimeError):
                storage.validate_result(malformed, summary(), arguments())
        with self.assertRaises(RuntimeError):
            storage.validate_result(payload, summary("retainers"), arguments())
        legacy = result()
        extended = deepcopy(legacy)
        extended["rows"][0].update(materia=None, materia_unavailable=None)
        storage.validate_result(legacy, summary(), arguments())
        storage.validate_result(extended, summary(), arguments())

    def test_armoury_is_on_demand_and_cannot_be_priced(self):
        args, answer = arguments(category="armoury"), result(rows=[armoury_row()], total_quantity=1)
        client, calls = deferred_fixture.client([tool_message(tool="query_storage", arguments=args), *completed()])
        outputs, reads = [], []
        client.send = outputs.append
        with patch.object(companion, "emit"):
            client.ask(request(["armoury"]), storage_query=lambda value: reads.append(value) or answer)
        self.assertEqual(reads, [args])
        self.assertEqual(json.loads(outputs[0]["result"]["contentItems"][0]["text"]), answer)
        registry = build_registry(normalize_request(request(["armoury"])), {"storage": lambda _: {}},
                                  None, companion.CompanionFailure)
        self.assertEqual(set(registry), {"query_storage", "search_saved_items"})
        self.assertNotIn("price_saved_items", registry)
        with self.assertRaises(companion.CompanionFailure) as denied:
            companion.require_lookup_permission("storage", args, summary("retainers"))
        self.assertEqual(denied.exception.kind, "permission")

    def test_real_bridge_routes_only_on_demand_with_no_public_services(self):
        args, payload, answer = arguments(), request(["retainers"]), result()
        client, calls = deferred_fixture.client([tool_message(tool="query_storage", arguments=args), *completed()])
        outputs, reads = [], []
        client.send = outputs.append
        def query(value):
            self.assertIn("turn/start", [method for method, _ in calls])
            reads.append(value)
            return answer
        with patch.object(companion, "emit"), patch.object(companion, "GameDataTools") as public:
            client.ask(payload, storage_query=query)
        public.assert_not_called()
        self.assertEqual(reads, [args])
        self.assertEqual(json.loads(outputs[0]["result"]["contentItems"][0]["text"]), answer)
        thread = next(params for method, params in calls if method == "thread/start")
        expected = build_registry(normalize_request(payload), {"storage": query}, None, companion.CompanionFailure)
        self.assertEqual(list(expected), ["query_storage", "search_saved_items"])
        deferred_fixture.assert_exposed(self, client, calls, [entry.definition for entry in expected.values()])
        self.assertIn("not live server inventory", deferred_fixture.instructions(client, calls))
        turn = next(params for method, params in calls if method == "turn/start")
        self.assertNotIn("TEST RETAINER", json.dumps(turn["input"]))
        client, _ = deferred_fixture.client(completed())
        with patch.object(companion, "emit"):
            client.ask(payload, storage_query=lambda _: self.fail("Unrequested storage must stay local"))

    def test_revoked_category_and_private_result_fail_before_model_sees_data(self):
        for args, returned, expected in ((arguments(category="housing"), result(), "permission"),
                                         (arguments(), result(rows=[row(source_key="private")]), "protocol")):
            client, _ = deferred_fixture.client([tool_message(tool="query_storage", arguments=args)])
            outputs, reads = [], []
            client.send = outputs.append
            def query(value):
                reads.append(value)
                return returned
            with patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure) as failure:
                client.ask(request(["retainers"]), storage_query=query)
            self.assertEqual(failure.exception.kind, expected)
            self.assertEqual(len(reads), int(expected == "protocol"))
            self.assertNotIn("private", json.dumps(outputs))

    def test_storage_uses_existing_call_and_total_byte_budgets(self):
        for budget in (companion.MAX_LOOKUP_TOTAL_BYTES, 1):
            client, calls = deferred_fixture.client([
                tool_message(tool="query_storage", arguments=arguments(), ident=ident) for ident in range(13)])
            outputs, reads = [], []
            client.send = outputs.append
            def query(args):
                reads.append(args)
                return result()
            with patch.object(companion, "emit"), patch.object(companion, "MAX_LOOKUP_TOTAL_BYTES", budget), \
                    self.assertRaises(companion.CompanionFailure) as failure:
                client.ask(request(["retainers"]), storage_query=query)
            self.assertEqual(failure.exception.kind, "reader")
            self.assertIn("shared 12-call budget" if budget > 1 else "shared 1-byte result budget",
                          str(failure.exception))
            self.assertEqual(len(reads), 12 if budget > 1 else 1)
            self.assertEqual([method for method, _ in calls].count("turn/start"), 1)
            self.assertIn("turn/interrupt", [method for method, _ in calls])

    def test_pipe_correlates_storage_and_shares_sequence(self):
        messages = []
        bridge = QuestQueryBridge(threading.Event(), messages.append, timeout=1)
        output = []
        worker = threading.Thread(target=lambda: output.append(bridge.query(arguments(), "storage")))
        worker.start()
        for _ in range(100):
            if messages: break
            threading.Event().wait(.005)
        self.assertEqual(messages, [{"storage_query": {"id": 1, "arguments": arguments()}}])
        with self.assertRaises(RuntimeError): bridge.accept({"inventory_result": {"id": 1, "result": result()}})
        bridge.accept({"storage_result": {"id": 1, "result": result()}})
        worker.join(2)
        self.assertFalse(worker.is_alive())
        self.assertEqual(len(output), 1)
        with self.assertRaises(RuntimeError): bridge.accept({"storage_result": {"id": 1, "result": result()}})

    def test_synthetic_csharp_fixture_when_explicitly_provided(self):
        directory = Path(__file__).resolve().parents[2] / ".test-tmp"
        paths = [path for name in ("storage-results.json", "storage-extra-fixture.json", "armoury-results.json")
                 if (path := directory / name).exists()]
        if not paths:
            self.skipTest("C# storage producer fixtures are not available")
        for path in paths:
            fixtures = json.loads(path.read_text(encoding="utf-8"))
            self.assertGreater(len(fixtures), 0)
            for fixture in fixtures:
                with self.subTest(file=path.name, name=fixture["name"]):
                    storage.validate_result(fixture["result"], fixture["summary"], fixture["arguments"])


if __name__ == "__main__":
    unittest.main()
