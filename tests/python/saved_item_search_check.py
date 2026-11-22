"""Synthetic combined-search permission, paging and shared-budget checks."""
import deferred_fixture
import json
import unittest
from unittest.mock import patch

import companion
from companion_request import normalize_request
from companion_tools import build_registry
from companion_architecture_check import wire_request
from inventory_lookup_check import fixture
import inventory_question_check as questions
from quest_lookup_check import completed, tool_message
from quest_lookup import encoded_size
import storage_lookup_check as storage
import saved_item_search as search


def arguments(**changes):
    return {"query": "TEST", "carried_offset": 0, "storage_offset": 0, "limit": 15, **changes}


def setup(carried=True, categories=("saddlebag",)):
    meta, bag = fixture()
    payload = wire_request(21)
    payload["inventory_lookup"] = meta if carried else None
    payload["storage_lookup"] = storage.summary(*categories)
    cached = storage.result(rows=[storage.row(category=categories[0], source="TEST SOURCE", unit_price_gil=None)])
    return payload, bag, cached


def many_retainer_reader(name="TEST TOKEN"):
    # Ten synthetic retainers, five 35-slot bags each. No saved user data is read.
    rows = [storage.row(source=f"TEST RETAINER {retainer}", container=f"RetainerPage{page}",
                        slot=slot, quantity=slot + 1, name=name, unit_price_gil=None)
            for retainer in range(10) for page in range(5) for slot in range(35)]
    total_quantity = sum(row["quantity"] for row in rows)
    def query(args):
        offset, limit = args["offset"], args["limit"]
        selected = rows[offset:offset + limit]
        return storage.result(total_matches=len(rows), total_quantity=total_quantity, offset=offset,
                              next_offset=offset + len(selected) if offset + len(selected) < len(rows) else None,
                              rows=selected)
    return query


class SavedItemSearchChecks(unittest.TestCase):
    def test_searches_bags_and_saddlebag_preserving_exact_locations_and_ages(self):
        payload, bag, cached = setup()
        reads = []
        registry = build_registry(normalize_request(payload),
            {"inventory": lambda args: (reads.append(("inventory", args)), bag)[1],
             "storage": lambda args: (reads.append(("storage", args)), cached)[1]}, None, companion.CompanionFailure)
        tool = registry["search_saved_items"]
        args = tool.validate_arguments(arguments())
        result = tool.validate_result(tool.query(args), tool.summary, args)
        self.assertEqual(result, {"carried": bag, "storage": cached})
        self.assertEqual([reader for reader, _ in reads], ["inventory", "storage"])
        self.assertEqual(reads[1][1]["category"], "all")
        self.assertNotEqual(result["carried"]["captured_at_utc"], result["storage"]["rows"][0]["captured_at_utc"])
        self.assertEqual(tool.reader_counts(args), {"inventory": 1, "storage": 1})

    def test_combined_search_preserves_crystal_and_event_item_namespaces(self):
        payload, bag, cached = setup(categories=("crystals", "key_items"))
        cached["rows"] = [storage.row(category="crystals", container="Crystals", item_id=2,
                                      base_item_id=2, quantity=12, name="TEST CRYSTAL", unit_price_gil=None),
                           storage.row(category="key_items", container="KeyItems", item_id=2000123,
                                      base_item_id=2000123, item_namespace="event_item", quantity=1,
                                      name="TEST CRYSTAL", unit_price_gil=None)]
        cached.update(total_matches=2, total_quantity=13)
        tool = build_registry(normalize_request(payload), {"inventory": lambda _: bag, "storage": lambda _: cached},
                              None, companion.CompanionFailure)["search_saved_items"]
        result = tool.validate_result(tool.query(arguments()), tool.summary, arguments())
        rows = result["storage"]["rows"]
        self.assertEqual([item["item_namespace"] for item in rows], ["item", "event_item"])
        self.assertEqual([item["base_item_id"] for item in rows], [2, 2000123])
        self.assertEqual(result["storage"]["total_quantity"], 13)

    def test_namespace_confusion_is_rejected_before_provider_reply(self):
        payload, bag, cached = setup(categories=("key_items",))
        cached["rows"] = [storage.row(category="key_items", container="KeyItems", item_id=2000123,
                                      base_item_id=123, item_namespace="item", name="PRIVATE EVENT ITEM",
                                      unit_price_gil=None)]
        client, _ = deferred_fixture.client([
            tool_message(tool="search_saved_items", arguments=arguments(query=""))])
        sent = []
        client.send = sent.append
        with patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure) as failure:
            client.ask(payload, inventory_query=lambda _: bag, storage_query=lambda _: cached)
        self.assertEqual(failure.exception.kind, "protocol")
        self.assertNotIn("PRIVATE EVENT ITEM", json.dumps(sent))

    def test_disabled_sources_never_called_and_unobserved_is_not_zero(self):
        payload, _, cached = setup(carried=False)
        registry = build_registry(normalize_request(payload), {"storage": lambda _: cached}, None, companion.CompanionFailure)
        tool = registry["search_saved_items"]
        self.assertEqual(tool.definition["inputSchema"]["properties"]["carried_offset"]["type"], "null")
        with self.assertRaises(PermissionError): tool.validate_arguments(arguments())
        args = tool.validate_arguments(arguments(carried_offset=None))
        result = tool.query(args)
        self.assertIsNone(result["carried"])
        with self.assertRaises(RuntimeError):
            tool.validate_result({**result, "carried": {}}, tool.summary, args)
        with self.assertRaises(RuntimeError):
            tool.validate_result({**result, "storage": {"reader_unavailable": 1}}, tool.summary, args)
        payload["storage_lookup"] = None
        self.assertNotIn("search_saved_items", build_registry(normalize_request(payload), {}, None, companion.CompanionFailure))

    def test_partial_availability_and_permission_revocation_stay_distinct(self):
        payload, bag, _ = setup()
        def unavailable(_): raise TimeoutError()
        tool = build_registry(normalize_request(payload), {"inventory": lambda _: bag, "storage": unavailable},
                              None, companion.CompanionFailure)["search_saved_items"]
        result = tool.query(arguments())
        self.assertEqual(result, {"carried": bag, "storage": {"reader_unavailable": True}})
        tool.validate_result(result, tool.summary, arguments())
        def denied(_): raise PermissionError()
        tool = build_registry(normalize_request(payload), {"inventory": lambda _: bag, "storage": denied},
                              None, companion.CompanionFailure)["search_saved_items"]
        with self.assertRaises(PermissionError): tool.query(arguments())

    def test_large_search_totals_cover_all_matches_without_paging_all_slots(self):
        payload, _, cached = setup(carried=False, categories=("retainers",))
        cached.update(total_matches=1750, total_quantity=1750, next_offset=15,
                      rows=[storage.row(name="TEST TOKEN", slot=i, quantity=1, unit_price_gil=None) for i in range(15)])
        seen = []
        tool = build_registry(normalize_request(payload), {"storage": lambda args: (seen.append(args), cached)[1]},
                              None, companion.CompanionFailure)["search_saved_items"]
        args = tool.validate_arguments(arguments(carried_offset=None))
        result = tool.validate_result(tool.query(args), tool.summary, args)
        self.assertEqual(len(seen), 1)
        self.assertEqual(result["storage"]["total_quantity"], 1750)
        self.assertEqual(len(result["storage"]["rows"]), 15)
        self.assertEqual(result["storage"]["next_offset"], 15)

    def test_bridge_sends_only_requested_results_and_records_both_readers(self):
        payload, bag, cached = setup()
        client, calls = deferred_fixture.client([tool_message(tool="search_saved_items", arguments=arguments()), *completed()])
        sent, events = [], []
        client.send = sent.append
        with patch.object(companion, "emit", events.append), patch.object(companion, "GameDataTools") as public:
            client.ask(payload, inventory_query=lambda _: bag, storage_query=lambda _: cached)
        public.assert_not_called()
        self.assertEqual(json.loads(sent[0]["result"]["contentItems"][0]["text"]), {"carried": bag, "storage": cached})
        context = next(event["question_context"] for event in events if "question_context" in event)
        self.assertIn("1 local inventory lookups", context)
        self.assertIn("1 storage-cache lookups", context)
        turn = next(params for method, params in calls if method == "turn/start")
        self.assertNotIn("TEST SOURCE", json.dumps(turn["input"]))

    def test_two_reader_search_cannot_double_shared_call_budget(self):
        payload, bag, cached = setup()
        client, calls = deferred_fixture.client([
            tool_message(tool="search_saved_items", arguments=arguments(), ident=i) for i in range(7)])
        reads = []
        with patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure) as failure:
            client.ask(payload, inventory_query=lambda _: (reads.append("inventory"), bag)[1],
                       storage_query=lambda _: (reads.append("storage"), cached)[1])
        self.assertEqual(len(reads), 12)
        self.assertIn("turn/interrupt", [method for method, _ in calls])
        self.assertEqual(failure.exception.kind, "reader")
        self.assertIn("shared 12-call budget", str(failure.exception))
        self.assertIn("Narrow", str(failure.exception))

    def test_two_reader_search_is_not_partly_executed_with_one_call_remaining(self):
        payload, _, cached = setup()
        events = [tool_message(tool="search_saved_items", arguments=arguments(carried_offset=None), ident=i)
                  for i in range(11)]
        events.append(tool_message(tool="search_saved_items", arguments=arguments(), ident=11))
        client, calls = deferred_fixture.client(events)
        reads = []
        with patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure) as failure:
            client.ask(payload, inventory_query=lambda _: self.fail("Partial composite callback exceeded budget"),
                       storage_query=lambda _: (reads.append("storage"), cached)[1])
        self.assertEqual(len(reads), 11)
        self.assertEqual(failure.exception.kind, "reader")
        self.assertIn("shared 12-call budget", str(failure.exception))
        self.assertEqual([method for method, _ in calls].count("turn/start"), 1)
        self.assertIn("turn/interrupt", [method for method, _ in calls])

    def test_many_retainer_pages_are_valid_until_shared_call_budget(self):
        payload, bag, _ = setup(categories=("retainers",))
        events = [tool_message(tool="search_saved_items", ident=i,
                              arguments=arguments(query="", carried_offset=0 if i == 0 else None, storage_offset=i * 15))
                  for i in range(12)]
        client, calls = deferred_fixture.client(events)
        outputs, reads = [], []
        client.send = outputs.append
        query = many_retainer_reader()
        with patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure) as failure:
            client.ask(payload, inventory_query=lambda _: (reads.append("inventory"), bag)[1],
                       storage_query=lambda args: (reads.append("storage"), query(args))[1])
        accepted = [json.loads(out["result"]["contentItems"][0]["text"])
                    for out in outputs if out["result"]["success"]]
        self.assertEqual(reads, ["inventory"] + ["storage"] * 11)
        self.assertEqual(len(accepted), 11)
        self.assertTrue(all(page["storage"]["total_matches"] == 1750 for page in accepted))
        self.assertTrue(all(page["storage"]["total_quantity"] == 31500 for page in accepted))
        self.assertEqual(accepted[-1]["storage"]["next_offset"], 165)
        self.assertEqual(failure.exception.kind, "reader")
        self.assertIn("shared 12-call budget", str(failure.exception))
        self.assertEqual([method for method, _ in calls].count("turn/start"), 1)

    def test_valid_large_pages_exhaust_bytes_without_forwarding_rejected_page(self):
        payload, _, _ = setup(carried=False, categories=("retainers",))
        events = [tool_message(tool="search_saved_items", ident=i,
                              arguments=arguments(query="", carried_offset=None, storage_offset=i * 15))
                  for i in range(12)]
        client, calls = deferred_fixture.client(events)
        outputs, read_pages = [], []
        client.send = outputs.append
        query = many_retainer_reader("TEST " + "\u754c" * 100)
        def read(args):
            page = query(args)
            read_pages.append(page)
            return page
        with patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure) as failure:
            client.ask(payload, storage_query=read)
        accepted = [json.loads(out["result"]["contentItems"][0]["text"])
                    for out in outputs if out["result"]["success"]]
        size = sum(encoded_size(page) for page in accepted)
        self.assertLessEqual(size, 120000)
        self.assertGreater(size + encoded_size({"carried": None, "storage": read_pages[-1]}), 120000)
        self.assertEqual(len(read_pages), len(accepted) + 1)
        self.assertLessEqual(len(read_pages), 12)
        self.assertFalse(outputs[-1]["result"]["success"])
        self.assertEqual(failure.exception.kind, "reader")
        self.assertIn("shared 120000-byte result budget", str(failure.exception))
        self.assertIn("turn/interrupt", [method for method, _ in calls])

    def test_untrusted_call_after_budget_is_still_a_protocol_failure(self):
        payload, bag, cached = setup()
        for invalid in ({"threadId": "wrong-thread"}, {"ident": 5}):
            events = [tool_message(tool="search_saved_items", arguments=arguments(), ident=i) for i in range(6)]
            events.append(tool_message(tool="search_saved_items", arguments=arguments(), **{"ident": 6, **invalid}))
            client, _ = deferred_fixture.client(events)
            reads = []
            with self.subTest(invalid=invalid), patch.object(companion, "emit"), \
                    self.assertRaises(companion.CompanionFailure) as failure:
                client.ask(payload, inventory_query=lambda _: (reads.append("inventory"), bag)[1],
                           storage_query=lambda _: (reads.append("storage"), cached)[1])
            self.assertEqual(len(reads), 12)
            self.assertEqual(failure.exception.kind, "protocol")
            self.assertNotIn("budget", str(failure.exception))

    def test_invalid_private_rows_rejected_before_reply(self):
        payload, bag, cached = setup()
        cached["rows"][0]["source_key"] = "PRIVATE KEY"
        client, _ = deferred_fixture.client([tool_message(tool="search_saved_items", arguments=arguments())])
        sent = []
        client.send = sent.append
        with patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure) as failure:
            client.ask(payload, inventory_query=lambda _: bag, storage_query=lambda _: cached)
        self.assertEqual(failure.exception.kind, "protocol")
        self.assertNotIn("PRIVATE KEY", str(failure.exception))
        self.assertNotIn("PRIVATE KEY", json.dumps(sent))


if __name__ == "__main__": unittest.main()
