"""Inventory question regressions using synthetic bags and a fake transport only."""
import copy
from collections import deque
from datetime import datetime, timedelta, timezone
import json
import threading
import unittest
from unittest.mock import patch

import companion
from inventory_question import MAX_REQUEST_BYTES, source_context, validate_history, validate_request


def fixture():
    bags = [{"name": f"Inventory{i}", "slot_count": 35, "unavailable_reason": None, "items": []} for i in range(1, 5)]
    for index, quantity in ((0, 11), (1, 7)):
        bags[index]["items"] = [{"slot": 0, "item_id": 50975, "base_item_id": 50975,
                                "name": "Phantom Dispeller β", "quantity": quantity, "is_hq": False}]
    return {"version": 1, "question": "How many Phantom Dispeller β do I have and how can I acquire more?",
            "snapshot": {"captured_at_utc": datetime.now(timezone.utc).isoformat(), "changed": False,
                         "scope": "carried_inventory_1_4", "containers": bags}}


def pedigree_fixture(status="decoded_tooltip"):
    payload = fixture()
    payload.update(version=4, history=[], web_search=False)
    payload["question"] = "Compare the covering permissions in my carried bags."
    captured = datetime.now(timezone.utc) - timedelta(seconds=5)
    payload["snapshot"]["captured_at_utc"] = captured.isoformat()
    observed = (captured - timedelta(seconds=1)).isoformat()
    pedigree = {"source": "ItemDetail", "status": status, "observed_at_utc": observed,
                "kind": "covering_permission", "grade": 6, "sex": "female",
                "father_stars": [4, 3, 2, 1, 4], "mother_stars": [1, 2, 3, 4, 1]}
    for bag in payload["snapshot"]["containers"]:
        bag["items"] = []
    for bag, slot, father in ((0, 2, [4, 3, 2, 1, 4]), (1, 7, [1, 4, 2, 4, 3])):
        card = {"slot": slot, "item_id": 9611, "base_item_id": 9611, "name": "G6-F Covering Permission",
                "quantity": 1, "is_hq": False, "pedigree": copy.deepcopy(pedigree)}
        card["pedigree"]["father_stars"] = father
        payload["snapshot"]["containers"][bag]["items"] = [card]
    return payload


class QuestionChecks(unittest.TestCase):
    def test_v4_preserves_ten_parent_stars_on_each_card_not_aggregate(self):
        payload = pedigree_fixture()
        _, data = validate_request(payload)
        expected = [{"bag": bag["name"], **item}
                    for bag in payload["snapshot"]["containers"] for item in bag["items"]]
        self.assertEqual(data["slot_items"], expected)
        self.assertNotEqual(data["slot_items"][0]["pedigree"]["father_stars"],
                            data["slot_items"][1]["pedigree"]["father_stars"])
        self.assertEqual(data["totals"], [{"item_id": 9611, "name": "G6-F Covering Permission",
                                          "quantity": 2, "hq_quantity": 0}])
        details = data["chocobo_details"]
        self.assertEqual(details["status"], "decoded_tooltip")
        self.assertEqual(details["coverage"], {"observed_card_slots": 2, "verified_card_slots": 2,
                                               "unavailable_card_slots": [], "unavailable_bags": []})
        self.assertEqual(details["parent_star_order"],
                         ["maximum_speed", "acceleration", "endurance", "stamina", "cunning"])
        for field in ("own_stats", "abilities", "colour"):
            self.assertIsNone(details[field])

    def test_v4_preserves_legacy_confirmation_and_labels_mixed_summary_as_decoded(self):
        for statuses, expected_status in ((["user_verified_tooltip"] * 2, "user_verified_tooltip"),
                                          (["decoded_tooltip"] * 2, "decoded_tooltip"),
                                          (["user_verified_tooltip", "decoded_tooltip"], "decoded_tooltip")):
            payload = pedigree_fixture()
            for bag, status in zip(payload["snapshot"]["containers"], statuses):
                bag["items"][0]["pedigree"]["status"] = status
            _, data = validate_request(payload)
            with self.subTest(statuses=statuses):
                self.assertEqual(data["chocobo_details"]["status"], expected_status)
                self.assertEqual([item["pedigree"]["status"] for item in data["slot_items"]], statuses)
                self.assertEqual(data["chocobo_details"]["coverage"]["verified_card_slots"], 2)

    def test_v4_partial_coverage_names_missing_slots_and_unavailable_bags(self):
        payload = pedigree_fixture()
        bags = payload["snapshot"]["containers"]
        bags[1]["items"][0]["pedigree"] = None
        missing = copy.deepcopy(bags[1]["items"][0])
        missing.update(slot=11)
        del missing["pedigree"]
        bags[1]["items"].append(missing)
        bags[2].update(slot_count=None, unavailable_reason="Not loaded")
        bags[3]["items"] = [{"slot": 3, "item_id": 50975, "base_item_id": 50975,
                              "name": "Phantom Dispeller β", "quantity": 18, "is_hq": False, "pedigree": None}]
        _, data = validate_request(payload)
        details = data["chocobo_details"]
        self.assertEqual(details["status"], "partial")
        self.assertEqual(details["coverage"], {
            "observed_card_slots": 3, "verified_card_slots": 1,
            "unavailable_card_slots": [
                {"bag": "Inventory2", "slot": 7, "reason": "Complete tooltip pedigree not available"},
                {"bag": "Inventory2", "slot": 11, "reason": "Complete tooltip pedigree not available"}],
            "unavailable_bags": ["Inventory3"]})
        self.assertIsNone(data["slot_items"][2]["pedigree"])
        self.assertIn("unknown", data["absence_means"])

    def test_v4_unobserved_or_missing_pedigrees_never_imply_complete_scan(self):
        for empty in (True, False):
            payload = pedigree_fixture()
            for bag in payload["snapshot"]["containers"]:
                if empty:
                    bag["items"] = []
                else:
                    for item in bag["items"]:
                        item["pedigree"] = None
            _, data = validate_request(payload)
            with self.subTest(empty=empty):
                self.assertEqual(data["chocobo_details"]["status"], "unavailable")
                self.assertEqual(data["chocobo_details"]["coverage"]["verified_card_slots"], 0)
        payload = pedigree_fixture()
        payload["snapshot"]["containers"][3].update(slot_count=None, unavailable_reason="Not loaded")
        self.assertEqual(validate_request(payload)[1]["chocobo_details"]["status"], "partial")

    def test_v4_all_supported_card_ranges_validate_kind_grade_and_sex(self):
        for first, kind, sex in ((9561, "fledgling", "male"), (9570, "fledgling", "female"),
                                 (9579, "retired_registration", "male"), (9588, "retired_registration", "female"),
                                 (9597, "covering_permission", "male"), (9606, "covering_permission", "female")):
            for grade in (1, 9):
                payload = pedigree_fixture()
                item = payload["snapshot"]["containers"][0]["items"][0]
                item.update(item_id=first + grade - 1, base_item_id=first + grade - 1, name=None)
                item["pedigree"].update(kind=kind, grade=grade, sex=sex)
                with self.subTest(kind=kind, sex=sex, grade=grade):
                    self.assertEqual(validate_request(payload)[1]["slot_items"][0]["pedigree"]["grade"], grade)

    def test_v4_pedigree_rejects_unknown_extra_or_malformed_fields_for_both_statuses(self):
        mutations = [lambda p: p.update(status="captured"), lambda p: p.update(status="decoded"),
                     lambda p: p.update(status=None), lambda p: p.update(source="memory"),
                     lambda p: p.update(grade=True), lambda p: p.update(grade=0), lambda p: p.update(grade=10),
                     lambda p: p.update(grade=6.0), lambda p: p.update(kind="covering_permission "),
                     lambda p: p.update(sex="unknown"), lambda p: p.update(own_stars=[4] * 5),
                     lambda p: p.pop("mother_stars"), lambda p: p.update(father_stars=[4] * 4),
                     lambda p: p.update(father_stars=[4] * 6), lambda p: p.update(father_stars=[4, 3, 2, 1, 0]),
                     lambda p: p.update(mother_stars=[1, 2, 3, 4, 5]),
                     lambda p: p.update(father_stars=[True, 2, 3, 4, 1]),
                     lambda p: p.update(mother_stars=[1, 2, 3, 4, 1.0]),
                     lambda p: p.update(father_stars="43214"), lambda p: p.update(mother_stars=None)]
        for status in ("decoded_tooltip", "user_verified_tooltip"):
            for index, mutate in enumerate(mutations):
                payload = pedigree_fixture(status)
                mutate(payload["snapshot"]["containers"][0]["items"][0]["pedigree"])
                with self.subTest(status=status, mutation=index), self.assertRaises(RuntimeError):
                    validate_request(payload)
        for invalid in (False, [], "verified", 1):
            payload = pedigree_fixture()
            payload["snapshot"]["containers"][0]["items"][0]["pedigree"] = invalid
            with self.subTest(pedigree=invalid), self.assertRaises(RuntimeError):
                validate_request(payload)

    def test_v4_pedigree_rejects_mismatched_card_identity_or_stack(self):
        changes = [{"item_id": 9602, "base_item_id": 9602},  # Male card, female tooltip.
                   {"item_id": 9610, "base_item_id": 9610},  # Wrong grade.
                   {"item_id": 9593, "base_item_id": 9593},  # Retired, not permission.
                   {"item_id": 9560, "base_item_id": 9560}, {"item_id": 9615, "base_item_id": 9615},
                   {"item_id": 1009611}, {"quantity": 2}, {"is_hq": True}]
        for change in changes:
            payload = pedigree_fixture()
            payload["snapshot"]["containers"][0]["items"][0].update(change)
            with self.subTest(change=change), self.assertRaises(RuntimeError):
                validate_request(payload)

    def test_v4_pedigree_timestamps_are_utc_not_future_or_after_capture_tolerance(self):
        payload = pedigree_fixture()
        now = datetime.now(timezone.utc)
        captured = now - timedelta(seconds=10)
        payload["snapshot"]["captured_at_utc"] = captured.isoformat()
        item = payload["snapshot"]["containers"][0]["items"][0]
        # Both cards need to predate this fixture's older capture.
        for bag in payload["snapshot"]["containers"]:
            for card in bag["items"]:
                card["pedigree"]["observed_at_utc"] = captured.isoformat()
        invalid = ["yesterday", "", None, 1, captured.replace(tzinfo=None).isoformat(),
                   captured.astimezone(timezone(timedelta(hours=2))).isoformat(),
                   (captured + timedelta(seconds=2, microseconds=1)).isoformat(),
                   (now + timedelta(seconds=1)).isoformat()]
        for stamp in invalid:
            item["pedigree"]["observed_at_utc"] = stamp
            with self.subTest(stamp=stamp), self.assertRaises(RuntimeError):
                validate_request(payload, now=now)
        item["pedigree"]["observed_at_utc"] = (captured + timedelta(seconds=2)).isoformat().replace("+00:00", "Z")
        self.assertEqual(validate_request(payload, now=now)[1]["chocobo_details"]["coverage"]["verified_card_slots"], 2)
        # A future observation is rejected even when it is within the capture tolerance.
        payload["snapshot"]["captured_at_utc"] = now.isoformat()
        item["pedigree"]["observed_at_utc"] = (now + timedelta(microseconds=1)).isoformat()
        with self.assertRaises(RuntimeError):
            validate_request(payload, now=now)

    def test_v1_v2_v3_item_schemas_stay_unchanged(self):
        for version in (1, 2, 3):
            payload = fixture()
            payload["version"] = version
            if version == 3:
                payload["history"] = []
            for pedigree in (None, pedigree_fixture()["snapshot"]["containers"][0]["items"][0]["pedigree"]):
                payload["snapshot"]["containers"][0]["items"][0]["pedigree"] = pedigree
                with self.subTest(version=version, pedigree=pedigree), self.assertRaises(RuntimeError):
                    validate_request(payload)
            del payload["snapshot"]["containers"][0]["items"][0]["pedigree"]
            _, inventory = validate_request(payload)
            self.assertNotIn("pedigree", inventory["slot_items"][0])
            self.assertEqual(inventory["chocobo_details"]["status"], "unavailable")

    def test_v4_history_roundtrip_and_web_mode_preserve_individual_pedigrees(self):
        client, calls = self.client([
            {"method": "item/completed", "params": {"item": {"type": "webSearch"}}},
            {"method": "item/completed", "params": {"item": {"type": "agentMessage", "text": '{"answer":"A limited comparison of the two captured cards."}'}}},
            {"method": "turn/completed", "params": {"turn": {"status": "completed"}}},
        ])
        client.web_search = True
        payload = pedigree_fixture()
        payload["snapshot"]["containers"][1]["items"][0]["pedigree"]["status"] = "user_verified_tooltip"
        payload.update(web_search=True, history=[{"question": "What is my breeding goal?", "answer": "Long-term parent stars."}])
        with patch.object(companion, "emit") as emitted:
            client.ask(payload)
        context = json.loads(next(p for m, p in calls if m == "turn/start")["input"][1]["text"].split("\n", 1)[1])
        self.assertEqual(context["history"], payload["history"])
        self.assertEqual(context["inventory"]["slot_items"], validate_request(payload)[1]["slot_items"])
        policy = next(p for m, p in calls if m == "thread/start")["developerInstructions"]
        self.assertIn("decoded_tooltip or user_verified_tooltip", policy)
        self.assertIn("need no manual confirmation", policy)
        self.assertIn("only user_verified_tooltip\nrecords were previously confirmed by the user", policy)
        self.assertNotIn("scan and verify", policy)
        result = emitted.call_args.args[0]
        self.assertEqual(result["question_answer"], "A limited comparison of the two captured cards.")
        self.assertTrue(result["search_performed"])
        self.assertIn("Historical carried-bag capture", result["question_context"])

    def test_v4_revalidates_pedigree_before_starting_turn(self):
        client, calls = self.client([])
        payload = pedigree_fixture()
        def preflight():
            payload["snapshot"]["containers"][0]["items"][0]["pedigree"]["mother_stars"] = [4]
            return {}
        client.preflight = preflight
        with self.assertRaises(RuntimeError):
            client.ask(payload)
        self.assertNotIn("turn/start", [m for m, _ in calls])

    def test_v4_general_question_without_snapshot_has_no_inventory_disclaimer(self):
        client, _ = self.client([
            {"method": "item/completed", "params": {"item": {"type": "agentMessage", "text": '{"answer":"Fixture answer."}'}}},
            {"method": "turn/completed", "params": {"turn": {"status": "completed"}}},
        ])
        payload = {"version": 4, "question": "Aether Currents?", "snapshot": None, "history": [], "web_search": False}
        with patch.object(companion, "emit") as emitted:
            client.ask(payload)
        self.assertEqual(emitted.call_args.args[0]["question_context"], "")
        self.assertEqual(emitted.call_args.args[0]["question_answer"], "Fixture answer.")
        for patch_value in ({"web_search": 1}, {"history": [{"question": "q", "answer": "a"}] * 5},
                            {"other": "data"}, {"version": 4.0}):
            with self.subTest(patch_value=patch_value), self.assertRaises(RuntimeError):
                validate_request(dict(payload, **patch_value))

    def test_request_byte_limit_matches_compact_wire_json(self):
        payload = fixture()
        payload["version"] = 3
        payload["question"] = "q"
        for bag in payload["snapshot"]["containers"]:
            bag["items"] = [{"slot": slot, "item_id": 1000 + slot,
                             "base_item_id": 1000 + slot, "name": "X" * 200,
                             "quantity": 1, "is_hq": False} for slot in range(35)]
        payload["history"] = [{"question": "q", "answer": "A" * 5900} for _ in range(4)]
        compact = len(json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8"))
        spaced = len(json.dumps(payload, ensure_ascii=False).encode("utf-8"))
        self.assertLessEqual(compact, MAX_REQUEST_BYTES)
        # The attachment envelope is larger; non-attachment context retains
        # its separate 64-KiB compact-JSON limit.
        self.assertLessEqual(compact, 65536)
        self.assertGreater(spaced, 65536)
        self.assertEqual(validate_request(payload)[0], "q")

    def test_v3_history_bounds_and_exact_fields(self):
        payload = {"version": 3, "question": "Where can I get more?", "snapshot": None,
                   "history": [{"question": "Phantom Dispeller β?", "answer": "Previously reported 18."}]}
        self.assertEqual(validate_request(payload), (payload["question"], None))
        self.assertEqual(validate_history(payload["history"]), payload["history"])
        invalid = [
            [payload["history"][0]] * 5,
            [{"question": "x", "answer": ""}],
            [{"question": "x", "answer": "a" * 8001}],
            [{"question": "x", "answer": "a", "pending": True}],
            [{"question": "x"}],
            [{"question": "x", "answer": "a" * 7000}] * 4,
            {"question": "x", "answer": "a"},
        ]
        for history in invalid:
            with self.subTest(history_type=type(history), history_length=len(history)):
                changed = dict(payload, history=history)
                with self.assertRaises(RuntimeError):
                    validate_request(changed)
        with self.assertRaises(RuntimeError):
            validate_request(dict(payload, other="unapproved"))

    def test_v3_old_changed_snapshot_is_historical(self):
        payload = fixture()
        payload["version"] = 3
        payload["history"] = []
        payload["snapshot"]["changed"] = True
        payload["snapshot"]["captured_at_utc"] = (datetime.now(timezone.utc) - timedelta(days=30)).isoformat()
        _, snapshot = validate_request(payload)
        self.assertEqual(snapshot["totals"][0]["quantity"], 18)
        self.assertEqual(snapshot["changed_since_capture"], True)
        self.assertEqual(snapshot["snapshot_status"], "historical_as_of_capture")

    def test_v2_without_snapshot_has_no_inventory_data(self):
        question, inventory = validate_request({"version": 2, "question": "What are Aether Currents?", "snapshot": None})
        self.assertEqual(question, "What are Aether Currents?")
        self.assertIsNone(inventory)

    def test_v2_historical_snapshot_accepts_changed_flag_and_marks_as_of_capture(self):
        for changed in (False, True):
            with self.subTest(changed=changed):
                payload = fixture()
                payload["version"] = 2
                payload["snapshot"]["changed"] = changed
                payload["snapshot"]["captured_at_utc"] = (datetime.now(timezone.utc) - timedelta(days=90)).isoformat()
                _, inventory = validate_request(payload)
                self.assertEqual(inventory["totals"][0]["quantity"], 18)
                self.assertEqual(inventory["changed_since_capture"], changed)
                self.assertEqual(inventory["snapshot_status"], "historical_as_of_capture")
                self.assertIn("current quantities unknown", inventory["quantity_interpretation"])
                self.assertIn("captured_at_utc", inventory)

    def test_v2_rejects_future_invalid_and_malformed_snapshot(self):
        mutations = [
            lambda p: p["snapshot"].update(captured_at_utc=(datetime.now(timezone.utc) + timedelta(seconds=3)).isoformat()),
            lambda p: p["snapshot"].update(captured_at_utc="yesterday"),
            lambda p: p["snapshot"].update(changed=1),
            lambda p: p["snapshot"].update(changed=None),
            lambda p: p["snapshot"]["containers"][0].update(slot_count=10),
            lambda p: p["snapshot"]["containers"][0]["items"][0].update(quantity=True),
        ]
        for mutate in mutations:
            payload = fixture()
            payload["version"] = 2
            mutate(payload)
            with self.subTest(mutation=mutate), self.assertRaises(RuntimeError):
                validate_request(payload)

    def test_v1_rejects_historical_and_missing_snapshot(self):
        payload = fixture()
        payload["snapshot"]["captured_at_utc"] = (datetime.now(timezone.utc) - timedelta(seconds=31)).isoformat()
        with self.assertRaisesRegex(RuntimeError, "stale"):
            validate_request(payload)
        payload["snapshot"] = None
        with self.assertRaises(RuntimeError):
            validate_request(payload)

    def test_totals_are_aggregated_by_id_not_fixed_fixture(self):
        _, data = validate_request(fixture())
        self.assertEqual(data["totals"][0]["quantity"], 18)
        self.assertEqual(data["totals"][0]["hq_quantity"], 0)
        self.assertEqual(data["unavailable_bags"], [])

    def test_duplicate_base_ids_keep_distinct_capture_locations_and_stack_details(self):
        payload = fixture()
        extra = copy.deepcopy(payload["snapshot"]["containers"][0]["items"][0])
        extra.update(slot=7, item_id=10050975, quantity=3, is_hq=True)
        payload["snapshot"]["containers"][0]["items"].append(extra)
        _, data = validate_request(payload)
        expected = [{"bag": bag["name"], **item}
                    for bag in payload["snapshot"]["containers"] for item in bag["items"]]
        self.assertEqual(data["slot_index_base"], 0)
        self.assertEqual(data["slot_items"], expected)
        self.assertEqual([(item["bag"], item["slot"]) for item in data["slot_items"]],
                         [("Inventory1", 0), ("Inventory1", 7), ("Inventory2", 0)])
        self.assertEqual(data["totals"], [{"item_id": 50975, "name": "Phantom Dispeller β",
                                          "quantity": 21, "hq_quantity": 3}])
        # The model receives the preserved slots, rather than a later totals-only projection.
        client, calls = self.client([
            {"method": "item/completed", "params": {"item": {"type": "agentMessage", "text": '{"answer":"Fixture."}'}}},
            {"method": "turn/completed", "params": {"turn": {"status": "completed"}}},
        ])
        with patch.object(companion, "emit"):
            client.ask(payload)
        context = next(p for m, p in calls if m == "turn/start")["input"][1]["text"]
        self.assertEqual(json.loads(context.split("\n", 1)[1])["inventory"]["slot_items"], expected)

    def test_uncaptured_chocobo_details_are_unavailable_even_with_observed_empty_bags(self):
        payload = fixture()
        for bag in payload["snapshot"]["containers"]:
            bag["items"] = []
        for unavailable in (False, True):
            with self.subTest(unavailable=unavailable):
                if unavailable:
                    payload["snapshot"]["containers"][0].update(slot_count=None, unavailable_reason="Not loaded")
                _, data = validate_request(payload)
                self.assertEqual(data["chocobo_details"], {
                    "status": "unavailable", "reason": "Not captured by the inventory reader",
                    "pedigree": None, "parentage": None, "abilities": None})
                self.assertEqual(data["slot_items"], [])
                self.assertEqual(data["totals"], [])
                self.assertEqual(data["unavailable_bags"], ["Inventory1"] if unavailable else [])
                self.assertIn("unknown" if unavailable else "zero", data["absence_means"])

    def test_absence_and_unavailable_are_distinct(self):
        payload = fixture()
        for bag in payload["snapshot"]["containers"]:
            bag["items"] = []
        self.assertIn("zero", validate_request(payload)[1]["absence_means"])
        bag = payload["snapshot"]["containers"][0]
        bag.update(slot_count=None, unavailable_reason="Not loaded")
        self.assertIn("unknown", validate_request(payload)[1]["absence_means"])

    def test_invalid_payloads_fail_closed(self):
        mutations = [
            lambda p: p["snapshot"].update(changed=True),
            lambda p: p["snapshot"].update(captured_at_utc=(datetime.now(timezone.utc) - timedelta(seconds=31)).isoformat()),
            lambda p: p["snapshot"].update(captured_at_utc=(datetime.now(timezone.utc) + timedelta(seconds=60)).isoformat()),
            lambda p: p["snapshot"].update(character_name="not permitted"),
            lambda p: p["snapshot"]["containers"][0].update(slot_count=10),
            lambda p: p["snapshot"]["containers"][0]["items"][0].update(quantity=True),
            lambda p: p["snapshot"]["containers"][0]["items"].append(copy.deepcopy(p["snapshot"]["containers"][0]["items"][0])),
            lambda p: p.update(question="x" * 2001),
        ]
        for mutate in mutations:
            payload = fixture()
            mutate(payload)
            with self.subTest(mutation=mutate), self.assertRaises(RuntimeError):
                validate_request(payload)

    def test_reference_is_dated_and_specific(self):
        self.assertEqual(source_context("Phantom Dispeller beta")["items"][0]["item_id"], 50975)
        self.assertEqual(source_context("Where can I get an unknown item?")["items"], [])

    def client(self, messages):
        client = companion.Companion.__new__(companion.Companion)
        client.cancel_event = threading.Event()
        client.preflight = lambda: {"primary": {"usedPercent": 20}}
        calls = []

        def call(method, params=None, **kwargs):
            calls.append((method, params))
            if method == "thread/start":
                return {"thread": {"id": "fixture-thread"}}
            if method == "turn/start":
                return {"turn": {"id": "fixture-turn"}}
            return {"data": []}

        events = deque(messages)
        client.call, client.receive, client.send = call, lambda **_: events.popleft(), lambda _: None
        return client, calls

    def test_ordinary_question_never_uses_proof_or_dynamic_snapshot(self):
        client, calls = self.client([
            {"method": "item/completed", "params": {"item": {"type": "agentMessage", "text": '{"answer":"18 in the captured bags."}'}}},
            {"method": "turn/completed", "params": {"turn": {"status": "completed"}}},
        ])
        with patch.object(companion, "emit") as emitted:
            client.ask(fixture())
        thread = next(p for m, p in calls if m == "thread/start")
        self.assertEqual(thread["dynamicTools"], [])
        turn = next(p for m, p in calls if m == "turn/start")
        context = turn["input"][1]["text"]
        self.assertEqual(json.loads(context.split("\n", 1)[1])["inventory"]["totals"][0]["quantity"], 18)
        self.assertNotIn(companion.AUDIT_SCRIPT, context)
        result = emitted.call_args.args[0]
        answer = result["question_answer"]
        self.assertIn("18", answer)
        self.assertIn("bundled; no live search", answer)
        self.assertEqual(result["question_sources"], source_context(fixture()["question"])["items"][0]["sources"])
        self.assertNotIn("https://", answer)
        self.assertNotIn("turn/interrupt", [m for m, _ in calls])

    def test_v2_general_question_sends_no_quantities_or_fabricated_snapshot(self):
        client, calls = self.client([
            {"method": "item/completed", "params": {"item": {"type": "agentMessage", "text": '{"answer":"Aether Currents are used to unlock flying."}'}}},
            {"method": "turn/completed", "params": {"turn": {"status": "completed"}}},
        ])
        payload = {"version": 2, "question": "What are Aether Currents?", "snapshot": None}
        with patch.object(companion, "emit") as emitted:
            client.ask(payload)
        turn = next(p for m, p in calls if m == "turn/start")
        context = turn["input"][1]["text"]
        self.assertIsNone(json.loads(context.split("\n", 1)[1])["inventory"])
        self.assertNotIn('"totals"', context)
        result = emitted.call_args.args[0]
        self.assertIn("No inventory snapshot supplied", result["question_answer"])
        self.assertNotIn("Snapshot:", result["question_answer"])
        self.assertEqual(result["question_sources"], [])

    def test_v2_historical_changed_question_marks_answer_as_of_capture(self):
        client, calls = self.client([
            {"method": "item/completed", "params": {"item": {"type": "agentMessage", "text": '{"answer":"18 in the captured bags, as of the capture."}'}}},
            {"method": "turn/completed", "params": {"turn": {"status": "completed"}}},
        ])
        payload = fixture()
        payload["version"] = 2
        payload["snapshot"]["changed"] = True
        payload["snapshot"]["captured_at_utc"] = (datetime.now(timezone.utc) - timedelta(days=7)).isoformat()
        with patch.object(companion, "emit") as emitted:
            client.ask(payload)
        context = next(p for m, p in calls if m == "turn/start")["input"][1]["text"]
        inventory = json.loads(context.split("\n", 1)[1])["inventory"]
        self.assertIs(inventory["changed_since_capture"], True)
        self.assertEqual(inventory["snapshot_status"], "historical_as_of_capture")
        answer = emitted.call_args.args[0]["question_answer"]
        self.assertIn("Inventory as of capture", answer)
        self.assertIn("Inventory changed since capture", answer)
        self.assertIn("Current holdings are unverified", answer)

    def test_v3_followup_uses_history_and_resolves_previous_item_reference(self):
        client, calls = self.client([
            {"method": "item/completed", "params": {"item": {"type": "agentMessage",
                "text": '{"answer":"The previous report said 18 as of that capture; there is no new count."}'}}},
            {"method": "turn/completed", "params": {"turn": {"status": "completed"}}},
        ])
        payload = {"version": 3, "question": "Where can I acquire more?", "snapshot": None,
                   "history": [{"question": "How many Phantom Dispeller β did I have?",
                                "answer": "18 in the captured bags as of Monday."}]}
        with patch.object(companion, "emit") as emitted:
            client.ask(payload)
        turn = next(p for m, p in calls if m == "turn/start")
        context = turn["input"][1]["text"]
        data = json.loads(context.split("\n", 1)[1])
        self.assertIsNone(data["inventory"])
        self.assertEqual(data["history"], payload["history"])
        self.assertEqual(data["reference"]["items"][0]["item_id"], 50975)
        self.assertNotIn('"totals"', context)
        result = emitted.call_args.args[0]
        self.assertEqual(result["question_answer"], "The previous report said 18 as of that capture; there is no new count.")
        self.assertIn("Bundled acquisition reference catalog checked", result["question_context"])
        self.assertNotIn("No NEW inventory snapshot", result["question_context"])
        self.assertEqual(result["question_sources"], source_context("Phantom Dispeller β")["items"][0]["sources"])
        self.assertNotIn("turn/interrupt", [m for m, _ in calls])

    def test_v3_general_question_has_no_inventory_or_tool_disclaimer(self):
        client, calls = self.client([
            {"method": "item/completed", "params": {"item": {"type": "agentMessage",
                "text": '{"answer":"Aether Currents unlock flying in an area."}'}}},
            {"method": "turn/completed", "params": {"turn": {"status": "completed"}}},
        ])
        payload = {"version": 3, "question": "What are Aether Currents?", "snapshot": None,
                   "history": []}
        with patch.object(companion, "emit") as emitted:
            client.ask(payload)
        result = emitted.call_args.args[0]
        self.assertEqual(result["question_answer"], "Aether Currents unlock flying in an area.")
        self.assertEqual(result["question_context"], "")
        self.assertEqual(result["question_sources"], [])
        self.assertNotIn("Each request starts a new model thread",
                         next(p for m, p in calls if m == "thread/start")["developerInstructions"])

    def test_v3_historical_context_keeps_capture_time_out_of_model_body(self):
        client, _ = self.client([
            {"method": "item/completed", "params": {"item": {"type": "agentMessage",
                "text": '{"answer":"The captured carried bags contained 18 then."}'}}},
            {"method": "turn/completed", "params": {"turn": {"status": "completed"}}},
        ])
        payload = fixture()
        payload["version"] = 3
        payload["history"] = []
        payload["snapshot"]["changed"] = True
        payload["snapshot"]["captured_at_utc"] = (datetime.now(timezone.utc) - timedelta(days=30)).isoformat()
        with patch.object(companion, "emit") as emitted:
            client.ask(payload)
        result = emitted.call_args.args[0]
        self.assertEqual(result["question_answer"], "The captured carried bags contained 18 then.")
        self.assertIn("Historical carried-bag capture as of", result["question_context"])
        self.assertIn("Changes were observed before sending", result["question_context"])
        self.assertIn("Only observed carried bags 1-4", result["question_context"])

    def test_v3_history_revalidated_after_preflight(self):
        client, calls = self.client([])
        payload = {"version": 3, "question": "And then?", "snapshot": None,
                   "history": [{"question": "Phantom Dispeller β?", "answer": "18 as of capture."}]}
        def preflight():
            payload["history"][0]["answer"] = ""
            return {}
        client.preflight = preflight
        with self.assertRaises(RuntimeError):
            client.ask(payload)
        self.assertNotIn("turn/start", [m for m, _ in calls])

    def test_stale_after_preflight_never_starts_model_turn(self):
        client, calls = self.client([])
        payload = fixture()
        def preflight():
            payload["snapshot"]["captured_at_utc"] = (datetime.now(timezone.utc) - timedelta(seconds=31)).isoformat()
            return {}
        client.preflight = preflight
        with self.assertRaisesRegex(RuntimeError, "stale"):
            client.ask(payload)
        self.assertNotIn("turn/start", [m for m, _ in calls])

    def test_unexpected_tool_is_rejected_and_turn_interrupted(self):
        client, calls = self.client([{"id": 99, "method": "item/tool/call", "params": {"tool": "unexpected"}}])
        with patch.object(companion, "emit"), self.assertRaisesRegex(RuntimeError, "Unexpected tool"):
            client.ask(fixture())
        self.assertEqual(calls[-1][0], "turn/interrupt")


if __name__ == "__main__":
    unittest.main()
