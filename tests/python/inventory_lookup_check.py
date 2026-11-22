"""Focused synthetic inventory-tool regression checks; never contacts a provider."""
import deferred_fixture
import copy
from datetime import datetime, timedelta, timezone
import json
import threading
import unittest
from unittest.mock import patch

import companion
from inventory_lookup import TOOL, validate_arguments, validate_result, validate_summary
from inventory_question import validate_request
from inventory_question_check import QuestionChecks
from quest_lookup import QuestQueryBridge
from quest_lookup_check import completed, summary as quest_summary, result as quest_result, tool_message, arguments as quest_arguments


def fixture():
    stamp = (datetime.now(timezone.utc) - timedelta(minutes=1)).isoformat()
    meta = {"captured_at_utc": stamp, "changed": False, "scope": "carried_inventory_1_4_lookup",
            "containers": [{"name": f"Inventory{i}", "slot_count": 35, "unavailable_reason": None,
                            "item_count": 2 if i == 1 else 0} for i in range(1, 5)],
            "item_slot_count": 2, "verified_pedigree_count": 0}
    rows = [{"bag": "Inventory1", "slot": index, "item_id": 42, "base_item_id": 42,
             "name": "TEST TOKEN", "quantity": count, "is_hq": False, "pedigree": None}
            for index, count in enumerate((3, 2))]
    return meta, {**meta, "total_matches": 2, "total_quantity": 5, "offset": 0, "next_offset": None, "rows": rows}


def arguments(**changes):
    return {"view": "items", "query": "", "ids": [], "bags": [], "offset": 0, "limit": 50, **changes}


def pedigree_fixture():
    meta, result = fixture()
    meta["item_slot_count"] = meta["containers"][0]["item_count"] = 6
    meta["verified_pedigree_count"] = 5
    pedigree = {"source": "ItemDetail", "status": "decoded_tooltip", "observed_at_utc": meta["captured_at_utc"],
                "kind": "fledgling", "grade": 7, "sex": "male",
                "father_stars": [1, 2, 3, 4, 1], "mother_stars": [4, 3, 2, 1, 4]}
    rows = [{"bag": "Inventory1", "slot": index, "item_id": 9567, "base_item_id": 9567,
             "name": "PRIVATE_SYNTHETIC_CARD", "quantity": 1, "is_hq": False,
             "pedigree": copy.deepcopy(pedigree) if index < 5 else None} for index in range(6)]
    rows[1]["pedigree"]["status"] = "user_verified_tooltip"
    return meta, {**meta, "total_matches": 6, "total_quantity": 6, "offset": 0, "next_offset": None, "rows": rows}


def request(meta, quest=None):
    return {"version": 8, "question": "How many TEST TOKEN?", "history": [], "snapshot": None,
            "inventory_lookup": meta, "quest_lookup": quest, "model": companion.DEFAULT_MODEL,
            "reasoning_effort": companion.DEFAULT_EFFORT, "web_search": False}


def inventory_message(ident=50, **changes):
    return tool_message(ident=ident, tool="query_inventory", **{"arguments": arguments(), **changes})


class InventoryLookupChecks(unittest.TestCase):
    def test_exact_locations_are_optional_bounded_and_validated_against_returned_rows(self):
        meta, result = fixture()
        self.assertEqual(validate_arguments(arguments()), arguments())  # Previous callers remain valid.
        query = arguments(locations=[{"bag": "Inventory1", "slot": 1}])
        self.assertEqual(validate_arguments(query), query)
        exact = {**result, "total_matches": 1, "total_quantity": 2, "rows": [result["rows"][1]]}
        self.assertEqual(validate_result(exact, validate_summary(meta), query), exact)
        with self.assertRaises(RuntimeError):
            validate_result(result, validate_summary(meta), query)
        wrong_card = {**exact, "total_quantity": 3, "rows": [result["rows"][0]]}
        with self.assertRaises(RuntimeError):
            validate_result(wrong_card, validate_summary(meta), query)
        for locations in (None, {}, [{"bag": "Inventory1", "slot": 35}],
                          [{"bag": "Inventory1", "slot": -1}], [{"bag": "Inventory1", "slot": True}],
                          [{"bag": "Retainer", "slot": 1}], [{"bag": "Inventory1", "slot": 1, "extra": 0}],
                          [{"bag": "Inventory1", "slot": 1}] * 2,
                          [{"bag": f"Inventory{index // 35 + 1}", "slot": index % 35} for index in range(51)]):
            with self.assertRaises(RuntimeError):
                validate_arguments(arguments(locations=locations))

    def test_exact_location_request_crosses_the_existing_bounded_inventory_tool(self):
        meta, result = fixture()
        query = arguments(locations=[{"bag": "Inventory1", "slot": 1}])
        result = {**result, "total_matches": 1, "total_quantity": 2, "rows": [result["rows"][1]]}
        client, calls = deferred_fixture.client([inventory_message(arguments=query), *completed()])
        seen = []
        with patch.object(companion, "emit"):
            client.ask(request(meta), inventory_query=lambda value: (seen.append(value), result)[1])
        self.assertEqual(seen, [query])
        policy = deferred_fixture.instructions(client, calls)
        for rule in ("A user-facing slot 12 is", "If beta highlighting is enabled", "You cannot trigger it yourself",
                     "Never convert storage slots into screen positions"):
            self.assertIn(rule, policy)

    def test_both_pedigree_statuses_survive_lookup_and_unknown_status_is_rejected(self):
        meta, result = pedigree_fixture()
        query = arguments(view="pedigrees")
        valid = validate_result(result, validate_summary(meta), query)
        self.assertEqual(valid["rows"][0]["pedigree"]["status"], "decoded_tooltip")
        self.assertEqual(valid["rows"][1]["pedigree"]["status"], "user_verified_tooltip")
        self.assertIsNone(valid["rows"][-1]["pedigree"])
        result["rows"][0]["pedigree"]["status"] = "captured"
        with self.assertRaises(RuntimeError):
            validate_result(result, validate_summary(meta), query)

    def test_context_distinguishes_available_from_unique_returned_pedigrees(self):
        meta, result = pedigree_fixture()
        first = {**result, "rows": result["rows"][:3], "next_offset": 3}
        second = {**result, "rows": result["rows"][3:], "offset": 3}
        queries = [arguments(view="pedigrees", limit=3), arguments(view="pedigrees", limit=3),
                   arguments(view="pedigrees", offset=3, limit=3)]
        events = [inventory_message(ident=index + 50, arguments=query) for index, query in enumerate(queries)]
        client, _ = deferred_fixture.client([*events, *completed()])
        returned = iter((first, first, second))
        with patch.object(companion, "emit") as emit:
            client.ask(request(meta), inventory_query=lambda _: next(returned))
        context = emit.call_args.args[0]["question_context"]
        self.assertIn("Pedigrees: 5 available in sent snapshot; 5 returned by inventory lookups.", context)
        self.assertNotIn("Verified pedigrees:", context)
        self.assertIn("3 local inventory lookups", context)
        for private in ("PRIVATE_SYNTHETIC_CARD", "9567", "father_stars", "mother_stars", "Inventory1"):
            self.assertNotIn(private, context)

    def test_context_does_not_claim_unrequested_or_unavailable_pedigrees_were_returned(self):
        meta, _ = pedigree_fixture()
        for events in ([], [inventory_message(arguments=arguments(view="pedigrees"))]):
            client, _ = deferred_fixture.client([*events, *completed()])
            with patch.object(companion, "emit") as emit:
                client.ask(request(meta), inventory_query=lambda _: {"reader_unavailable": True})
            context = emit.call_args.args[0]["question_context"]
            self.assertEqual(context, "Pedigrees: 5 available in sent snapshot; 0 returned by inventory lookups.")

    def test_summary_only_and_deterministic_result(self):
        meta, result = fixture()
        payload = request(meta)
        self.assertEqual(validate_request(payload), (payload["question"], None))
        client, calls = deferred_fixture.client([inventory_message(), *completed()])
        output = []
        client.send = output.append
        with patch.object(companion, "emit") as emit:
            client.ask(payload, inventory_query=lambda args: result)
        thread = next(p for m, p in calls if m == "thread/start")
        deferred_fixture.assert_exposed(self, client, calls, [TOOL])
        policy = deferred_fixture.instructions(client, calls)
        # Verify the composed policy keeps concise counts and the evidence limits together.
        for rule in ("You have 5 TEST TOKEN", "Do not automatically add 'last recorded'",
                     "Give locations when asked", "changed=false alone does not prove",
                     "lower-bound qualifier when requested bags are unavailable",
                     "Containers marked unavailable are unknown, not empty",
                     "do not call it live, server-verified or continuously monitored",
                     "decoded_tooltip or user_verified_tooltip", "need no manual confirmation",
                     "legacy-named verified_pedigree_count", "It does not mean all records were human-confirmed",
                     "maximum_speed, acceleration, endurance, stamina, cunning"):
            self.assertIn(rule, policy)
        for absent in ("scan and verify", "parent_star_order", "Use only user_verified_tooltip parent rows"):
            self.assertNotIn(absent, policy)
        context = json.loads(next(p for m, p in calls if m == "turn/start")["input"][1]["text"].split("\n", 1)[1])
        self.assertIsNone(context["inventory"])
        self.assertNotIn("rows", context["inventory_lookup"])
        self.assertEqual(json.loads(output[0]["result"]["contentItems"][0]["text"])["total_quantity"], 5)
        self.assertIn("1 local inventory lookups", emit.call_args.args[0]["question_context"])

    def test_permission_off_unrelated_answer_and_bad_correlation(self):
        meta, result = fixture()
        client, calls = deferred_fixture.client(completed())
        with patch.object(companion, "emit") as emit:
            client.ask(request(meta), inventory_query=lambda _: self.fail("Unrelated reply must not force data fetch"))
        self.assertEqual(emit.call_args.args[0]["question_context"],
                         "Pedigrees: 0 available in sent snapshot; 0 returned by inventory lookups.")
        for payload, message in ((request(None), inventory_message()),
                                 (request(meta), inventory_message(threadId="other"))):
            client, _ = deferred_fixture.client([message])
            with patch.object(companion, "emit"), self.assertRaises(RuntimeError):
                client.ask(payload, inventory_query=lambda _: self.fail("Denied before callback"))

    def test_shared_call_budget_and_duplicate_ids(self):
        meta, result = fixture()
        quests = quest_summary()
        events = [inventory_message(i) if i % 2 else tool_message(quest_arguments(), ident=i) for i in range(13)]
        client, _ = deferred_fixture.client(events)
        called = []
        with patch.object(companion, "emit"), self.assertRaises(RuntimeError):
            client.ask(request(meta, quests),
                       inventory_query=lambda args: (called.append("inventory"), result)[1],
                       quest_query=lambda args: (called.append("quest"), quest_result(quests, args))[1])
        self.assertEqual(len(called), 12)
        client, _ = deferred_fixture.client([inventory_message(5), tool_message(ident=5)])
        with patch.object(companion, "emit"), self.assertRaises(RuntimeError):
            client.ask(request(meta, quests), inventory_query=lambda args: result,
                       quest_query=lambda _: self.fail("Duplicate ID must be global"))

    def test_invalid_payload_results_and_pagination(self):
        meta, result = fixture()
        payload = request(meta)
        payload["snapshot"] = {}
        with self.assertRaises(RuntimeError):
            validate_request(payload)
        for args in (arguments(bags=["Retainer"]), arguments(ids=[True]), arguments(bags=["Inventory1"] * 2),
                     arguments(limit=51), arguments(extra=1), arguments(query="x\x00")):
            with self.assertRaises(RuntimeError):
                validate_arguments(args)
        normalized = validate_summary(meta)
        self.assertEqual(validate_result(result, normalized, arguments()), result)
        for mutation in (lambda r: r.update(total_quantity=6), lambda r: r.update(next_offset=2),
                         lambda r: r["rows"][0].update(bag="Inventory2"),
                         lambda r: r["rows"][1].update(slot=0), lambda r: r.update(changed=True)):
            bad = copy.deepcopy(result)
            mutation(bad)
            with self.assertRaises(RuntimeError):
                validate_result(bad, normalized, arguments())
        page = {**result, "rows": result["rows"][:1], "next_offset": 1}
        self.assertEqual(validate_result(page, normalized, arguments(limit=1))["total_quantity"], 5)

    def test_shared_pipe_sequence_and_category_matching(self):
        cancellation = threading.Event()
        sent, result = [], []
        ready = threading.Event()
        bridge = QuestQueryBridge(cancellation, lambda value: (sent.append(value), ready.set()), timeout=1)
        for index, (kind, args) in enumerate((("quest", quest_arguments()), ("inventory", arguments())), 1):
            ready.clear()
            worker = threading.Thread(target=lambda: result.append(bridge.query(args, kind)))
            worker.start()
            self.assertTrue(ready.wait(1))
            self.assertEqual(sent[-1][kind + "_query"]["id"], index)
            other = "quest" if kind == "inventory" else "inventory"
            with self.assertRaises(RuntimeError):
                bridge.accept({other + "_result": {"id": index, "result": {}}})
            bridge.accept({kind + "_result": {"id": index, "result": {"accepted": kind}}})
            worker.join(1)
            self.assertFalse(worker.is_alive())
        self.assertEqual(result, [{"accepted": "quest"}, {"accepted": "inventory"}])


if __name__ == "__main__":
    unittest.main()
