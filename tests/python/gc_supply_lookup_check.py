"""Synthetic Grand Company schema, freshness, field-boundary and C# producer checks."""
from copy import deepcopy
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import unittest

from game_context_lookup import timestamp
from gc_supply_lookup import SCOPE, INSTRUCTIONS, validate_arguments, validate_summary, validate_result


def result(**changes):
    return {"scope": SCOPE, "captured_at_utc": datetime.now(timezone.utc).isoformat(), "available": True,
            "unavailable_reason": None, "coverage": "Visible menu only; completion and server freshness unknown.",
            "entries": [{"position": 0, "class_job_id": 8, "class_job_abbreviation": "CRP", "category": "supply",
                         "item_id": 1000, "name": "Synthetic Item", "quantity_requested": 1,
                         "experience_reward": 12345, "seal_reward": 678, "turn_in_available": False, "bonus_reward": None}],
            "matched_count": 1, "truncated": False, **changes}


class GcSupplyChecks(unittest.TestCase):
    def test_exact_summary_and_argument_schema(self):
        self.assertIsNone(validate_summary(None))
        self.assertEqual(validate_summary({"scope": SCOPE}), {"scope": SCOPE})
        self.assertEqual(validate_arguments({}), {"category": "all"})
        for value in ({}, {"scope": "daily_checklist"}, {"scope": SCOPE, "items": []}):
            with self.assertRaises(RuntimeError):
                validate_summary(value)
        for value in ([], {"category": None}, {"category": "expert"}, {"category": []}, {"item_ids": [1]}):
            with self.assertRaises(RuntimeError):
                validate_arguments(value)

    def test_boolean_states_do_not_invent_completion(self):
        value = result()
        for eligibility in (True, False, None):
            value["entries"][0].update(turn_in_available=eligibility, bonus_reward=None)
            validate_result(value, {"scope": SCOPE}, {})
        self.assertIn("False does not mean delivered", INSTRUCTIONS)
        self.assertIn("bonus_reward is deliberately always null", INSTRUCTIONS)
        self.assertIn("Listed rows may remain after delivery", INSTRUCTIONS)
        for changes in ({"turn_in_available": 0}, {"bonus_reward": 1}, {"bonus_reward": True},
                        {"bonus_reward": False}, {"completed": True}, {"num_possessed": 5}):
            value = result()
            value["entries"][0].update(changes)
            with self.assertRaises(RuntimeError):
                validate_result(value, {"scope": SCOPE}, {})

    def test_job_mapping_quantity_rewards_and_filters_are_strict(self):
        for changes in ({"class_job_id": 18}, {"class_job_abbreviation": "FSH"}, {"position": 11},
                        {"position": True}, {"category": "provisioning"}, {"item_id": 0}, {"quantity_requested": 0},
                        {"experience_reward": -1}, {"seal_reward": True}, {"name": "bad\nname"}):
            value = result()
            value["entries"][0].update(changes)
            with self.subTest(changes=changes), self.assertRaises(RuntimeError):
                validate_result(value, {"scope": SCOPE}, {})
        with self.assertRaises(RuntimeError):
            validate_result(result(), {"scope": SCOPE}, {"category": "provisioning"})
        validate_result(result(entries=[], matched_count=0), {"scope": SCOPE}, {"category": "provisioning"})

    def test_stale_unknown_and_unavailable_results_are_distinct(self):
        now = datetime.now(timezone.utc)
        stale = result(captured_at_utc=(now - timedelta(seconds=31)).isoformat())
        with self.assertRaises(RuntimeError):
            validate_result(stale, {"scope": SCOPE}, {}, now=now)
        stale.update(available=False, unavailable_reason="Expired", entries=[], matched_count=0)
        validate_result(stale, {"scope": SCOPE}, {}, now=now)
        for changes in ({"captured_at_utc": (now + timedelta(seconds=3)).isoformat()}, {"available": False},
                        {"truncated": True}, {"truncated": 0}, {"matched_count": True},
                        {"matched_count": 2}, {"entries": result()["entries"] * 2, "matched_count": 2},
                        {"unavailable_reason": "Unexpected"}, {"entries": [], "matched_count": 0}):
            with self.subTest(changes=changes), self.assertRaises(RuntimeError):
                validate_result(result(**changes), {"scope": SCOPE}, {}, now=now)

    def test_csharp_producer_results(self):
        path = Path(__file__).resolve().parents[2] / ".test-tmp" / "gc-supply-result.json"
        self.assertTrue(path.exists(), "Run GcSupply.Tests --produce-fixtures .test-tmp/gc-supply-result.json")
        fixtures = json.loads(path.read_text(encoding="utf-8"))
        self.assertEqual({fixture["name"] for fixture in fixtures}, {"all_jobs", "provisioning", "unavailable", "stale"})
        for fixture in fixtures:
            value = deepcopy(fixture["result"])
            with self.subTest(fixture=fixture["name"]):
                validate_result(value, {"scope": SCOPE}, fixture["arguments"], now=timestamp(value["captured_at_utc"]))

    def test_recent_menu_observation_expires_without_exposing_current_eligibility(self):
        now = datetime.now(timezone.utc)
        value = result(readiness="last_observed_menu", captured_at_utc=(now - timedelta(minutes=14)).isoformat())
        value["entries"][0]["turn_in_available"] = None
        validate_result(value, {"scope": SCOPE}, {}, now=now)
        for eligible in (False, True):
            invalid = deepcopy(value)
            invalid["entries"][0]["turn_in_available"] = eligible
            with self.assertRaises(RuntimeError):
                validate_result(invalid, {"scope": SCOPE}, {}, now=now)
        for change in ({"readiness": "live"}, {"readiness": "visible_menu_observation"},
                       {"captured_at_utc": (now - timedelta(minutes=15)).isoformat()}):
            with self.subTest(change=change), self.assertRaises(RuntimeError):
                validate_result({**value, **change}, {"scope": SCOPE}, {}, now=now)


if __name__ == "__main__":
    unittest.main()
