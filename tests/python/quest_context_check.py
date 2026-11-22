"""Quest protocol regressions: synthetic fixtures and fake transport, no provider requests."""
import copy
import json
import sys
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest.mock import patch

import companion
from inventory_question import validate_request
from inventory_question_check import QuestionChecks, pedigree_fixture
from quest_context import QUEST_INSTRUCTIONS, SCOPE, validate_quests


def fixture():
    return {"captured_at_utc": datetime.now(timezone.utc).isoformat(), "stale": False, "scope": SCOPE,
            "catalog_rows": 5533, "observed_quest_count": 5532, "active_count": 1,
            "observed_completed_count": 3000, "matched_count": 1, "active_omitted": 0, "matched_omitted": 0,
            "rows": [
                {"native_id": 100, "row_id": 65636, "name": "Synthetic active quest", "name_truncated": False,
                 "accepted": True, "completed": False, "sequence": 3,
                 "journal_text": "A recorded paragraph.", "journal_status": "included"},
                {"native_id": 101, "row_id": 65637, "name": "Synthetic completed quest", "name_truncated": False,
                 "accepted": False, "completed": True, "sequence": None,
                 "journal_text": None, "journal_status": "unavailable"}]}


def request(quests=None):
    return {"version": 5, "question": "What is my next quest step?", "snapshot": None, "history": [], "quests": quests}


class QuestChecks(unittest.TestCase):
    def test_v5_quest_only_and_disabled_preserve_inventory_return_contract(self):
        for quests in (None, fixture()):
            self.assertEqual(validate_request(request(quests)), ("What is my next quest step?", None))
        self.assertIsNone(validate_quests(None))

    def test_v5_inventory_and_pedigrees_remain_individual(self):
        payload = pedigree_fixture()
        payload.update(version=5, quests=fixture())
        _, snapshot = validate_request(payload)
        self.assertEqual(len(snapshot["slot_items"]), 2)
        self.assertNotEqual(snapshot["slot_items"][0]["pedigree"]["father_stars"],
                            snapshot["slot_items"][1]["pedigree"]["father_stars"])

    def test_old_versions_do_not_accept_quests(self):
        for version in (1, 2, 3, 4):
            payload = request(fixture())
            payload["version"] = version
            with self.subTest(version=version), self.assertRaises(RuntimeError):
                validate_request(payload)

    def test_exact_fields_and_types(self):
        mutations = [
            lambda q: q.update(character_id=1), lambda q: q.pop("stale"), lambda q: q.update(stale=1),
            lambda q: q.update(catalog_rows=True), lambda q: q.update(observed_quest_count="5532"),
            lambda q: q.update(active_count=31), lambda q: q.update(observed_completed_count=6000),
            lambda q: q.update(matched_count=0), lambda q: q.update(active_omitted=1),
            lambda q: q["rows"][0].update(accepted=1), lambda q: q["rows"][0].update(completed=1),
            lambda q: q["rows"][0].update(native_id=True), lambda q: q["rows"][0].update(sequence=False),
            lambda q: q["rows"][0].update(row_id=123), lambda q: q["rows"][0].update(secret="extra"),
            lambda q: q["rows"][0].update(native_id=6008, row_id=71544),
            lambda q: q["rows"][0].update(journal_text="x" * 513),
            lambda q: q["rows"][0].update(journal_status="unavailable"),
            lambda q: q["rows"][1].update(journal_status="included", journal_text="not active"),
            lambda q: q["rows"][0].update(name="x" * 201),
            lambda q: q["rows"].append(copy.deepcopy(q["rows"][0])),
        ]
        for index, mutate in enumerate(mutations):
            payload = fixture()
            mutate(payload)
            with self.subTest(index=index), self.assertRaises(RuntimeError):
                validate_request(request(payload))

    def test_timestamp_is_explicit_utc_and_not_future(self):
        for stamp in ("2026-01-01", "2026-01-01T10:00:00", "2026-01-01T10:00:00+03:00",
                      "2026-02-30T10:00:00Z", "2026-01-01X10:00:00Z", 5,
                      (datetime.now(timezone.utc) + timedelta(minutes=10)).isoformat()):
            payload = fixture()
            payload["captured_at_utc"] = stamp
            with self.subTest(stamp=stamp), self.assertRaises(RuntimeError):
                validate_quests(payload)

    def test_unknown_and_negative_are_distinct_and_repeatable_can_be_both(self):
        payload = fixture()
        payload["rows"][0]["completed"] = None
        self.assertIsNone(validate_quests(payload)["rows"][0]["completed"])
        payload["rows"][0]["completed"] = False
        self.assertIs(validate_quests(payload)["rows"][0]["completed"], False)
        payload["rows"][0]["completed"] = True
        self.assertIs(validate_quests(payload)["rows"][0]["completed"], True)

    def test_overall_size_limit(self):
        payload = fixture()
        payload.update(active_count=30, matched_count=0, rows=[])
        for i in range(30):
            row = copy.deepcopy(fixture()["rows"][0])
            row.update(native_id=i+1, row_id=65537+i, name="界" * 200, journal_text="界" * 512)
            payload["rows"].append(row)
        with self.assertRaises(RuntimeError):
            validate_quests(payload)

    def test_attached_context_and_instructions_only_sent_when_enabled(self):
        for enabled in (False, True):
            client, calls = QuestionChecks().client([
                {"method": "item/completed", "params": {"item": {"type": "agentMessage", "text": '{"answer":"Synthetic answer."}'}}},
                {"method": "turn/completed", "params": {"turn": {"status": "completed"}}},
            ])
            with patch.object(companion, "emit") as emitted:
                client.ask(request(fixture() if enabled else None))
            instructions = next(p for m, p in calls if m == "thread/start")["developerInstructions"]
            turn = next(p for m, p in calls if m == "turn/start")
            sent = json.loads(turn["input"][1]["text"].split("\n", 1)[1])
            self.assertEqual("quests" in sent, enabled)
            self.assertEqual(QUEST_INSTRUCTIONS in instructions, enabled)
            context = emitted.call_args.args[0]["question_context"]
            self.assertEqual("Quest capture" in context, enabled)
            self.assertNotIn("No inventory", context)

    def test_invalidated_quest_context_never_starts_model_turn(self):
        client, calls = QuestionChecks().client([])
        payload = request(fixture())
        def preflight():
            payload["quests"]["rows"][0]["native_id"] = -1
            return {}
        client.preflight = preflight
        with self.assertRaises(RuntimeError):
            client.ask(payload)
        self.assertNotIn("turn/start", [m for m, _ in calls])


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "--fixture":
        fixtures = json.loads(Path(sys.argv[2]).read_text(encoding="utf-8"))
        for payload in fixtures:
            validate_request(request(payload))
        print(f"Validated {len(fixtures)} C# producer fixtures with Python protocol.")
        sys.argv = sys.argv[:1]
    # Avoid rerunning the imported inventory suite during this focused check.
    unittest.main(defaultTest="QuestChecks")
