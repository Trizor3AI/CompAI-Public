"""Synthetic wire33 collection menus and compact Triple Triad producer/consumer boundaries."""
from copy import deepcopy
import json
from pathlib import Path
import unittest
from unittest.mock import patch

import collection_lookup as collections
import companion
from collection_lookup_check import prepared, event
from companion_architecture_check import wire_request
from companion_request import normalize_request
from quest_lookup import encoded_size
from quest_lookup_check import completed

ROOT = Path(__file__).resolve().parents[2]
NEW_CATEGORIES = ("occult_records", "yokai_minions", "collection_tools")


def summary(category):
    return {"scope": collections.SCOPE, "categories": [category]}


def request(category, version=33):
    value = wire_request(version)
    value["collection_lookup"] = summary(category)
    return value


class CollectionMenuChecks(unittest.TestCase):
    def test_new_grants_are_independent_and_previous_wire_categories_remain_frozen(self):
        for category in NEW_CATEGORIES:
            meta = summary(category)
            self.assertEqual(collections.validate_summary(meta, version=33), meta)
            self.assertEqual(normalize_request(request(category)).collection_lookup, meta)
            for version in range(23, 33):
                with self.subTest(category=category, version=version), self.assertRaises(RuntimeError):
                    collections.validate_summary(meta, version=version)
                with self.assertRaises(RuntimeError):
                    normalize_request(request(category, version))
            for unrelated in set(collections.SOURCES) - {category}:
                with self.assertRaises(RuntimeError):
                    collections.validate_arguments({"category": unrelated}, meta)
        previous = {"scope": collections.SCOPE, "categories": list(collections.V32_CATEGORIES)}
        self.assertEqual(collections.validate_summary(previous, version=32), previous)

    def test_collection_readers_actual_csharp_producer_survives_deferred_transport(self):
        path = ROOT / ".test-tmp/collection-menus-producer.json"
        self.assertTrue(path.exists(), "Run CollectionMenus.Tests --produce-fixtures .test-tmp/collection-menus-producer.json")
        fixtures = json.loads(path.read_text(encoding="utf-8"))
        self.assertEqual(set(fixtures), set(NEW_CATEGORIES))
        for category, value in fixtures.items():
            args = {"category": category}
            with self.subTest(category=category):
                collections.validate_result(value, summary(category), args)
                self.assertEqual(value["observed_unlocked_count"], 1)
                self.assertEqual(value["not_observed_unlocked_count"], 1)
                client, _, outputs = prepared([event(**args), *completed()])
                with patch.object(companion, "emit"):
                    client.ask(request(category), collection_query=lambda _, data=value: deepcopy(data))
                self.assertEqual(json.loads(outputs[0]["result"]["contentItems"][0]["text"]), value)

    def test_compact_parameters_are_contextual_and_strict(self):
        valid = {"category": "triple_triad", "state": "unconfirmed", "compact": True}
        self.assertEqual(collections.validate_arguments(valid)["limit"], 200)
        self.assertEqual(collections.validate_arguments({**valid, "compact": False})["limit"], 50)
        for changes in ({"category": "mounts"}, {"state": "all"}, {"state": "unknown"}, {"compact": 1},
                        {"compact": "true"}, {"limit": 201}, {"limit": 0}, {"limit": 200.0},
                        {"compact": False, "limit": 51}, {"unexpected": True}):
            with self.subTest(changes=changes), self.assertRaises(RuntimeError):
                collections.validate_arguments({**valid, **changes})

    def test_actual_compact_producer_preserves_counts_bounds_and_unsupported_reason(self):
        fixtures = self.card_fixtures()
        self.assertEqual(len(fixtures), 4)
        for fixture in fixtures:
            value, args = fixture["result"], fixture["arguments"]
            with self.subTest(fixture=fixture["name"]):
                collections.validate_result(value, summary("triple_triad"), args)
                self.assertEqual(value["catalog_count"], 476)
                self.assertEqual(value["observed_unlocked_count"], 50)
                self.assertEqual(value["not_observed_unlocked_count"], 425)
                self.assertEqual(value["unknown_count"], 1)
                self.assertLessEqual(encoded_size(value), 20000)
                self.assertTrue(all(row["unlocked"] is None for row in value["entries"]))
                if fixture["name"] == "triple_triad_unconfirmed_compact":
                    self.assertGreater(len(value["entries"]), 50)
                    self.assertEqual(value["limit"], 200)
                    self.assertEqual(value["matched_count"], 426)
                    self.assertTrue(all(row["unknown_reason"] is None for row in value["entries"]))
                if fixture["name"] == "triple_triad_unconfirmed_exception":
                    self.assertEqual(value["entries"][0]["unknown_reason"], "Synthetic unsupported mapping.")

    def test_null_reasons_are_only_accepted_for_compact_native_false_rows_with_coverage(self):
        fixtures = {fixture["name"]: fixture for fixture in self.card_fixtures()}
        fixture = fixtures["triple_triad_unconfirmed_compact"]
        value, args = deepcopy(fixture["result"]), fixture["arguments"]
        value["coverage"] = "No required caveat."
        with self.assertRaises(RuntimeError):
            collections.validate_result(value, summary("triple_triad"), args)
        fixture = fixtures["triple_triad_unconfirmed_default"]
        value, args = deepcopy(fixture["result"]), fixture["arguments"]
        value["entries"][0]["unknown_reason"] = None
        with self.assertRaises(RuntimeError):
            collections.validate_result(value, summary("triple_triad"), args)
        fixture = fixtures["triple_triad_unconfirmed_exception"]
        value, args = deepcopy(fixture["result"]), fixture["arguments"]
        value["entries"][0]["unknown_reason"] = None
        with self.assertRaises(RuntimeError):
            collections.validate_result(value, summary("triple_triad"), args)

    def test_unconfirmed_results_cannot_smuggle_confirmed_rows_or_invent_missing_counts(self):
        fixture = next(f for f in self.card_fixtures() if f["name"] == "triple_triad_unconfirmed_compact")
        value, args = deepcopy(fixture["result"]), fixture["arguments"]
        value["entries"][0].update(state="unlocked", unlocked=True, unknown_reason=None)
        with self.assertRaises(RuntimeError):
            collections.validate_result(value, summary("triple_triad"), args)
        value = deepcopy(fixture["result"])
        value["matched_count"] = 475
        with self.assertRaises(RuntimeError):
            collections.validate_result(value, summary("triple_triad"), args)

    @staticmethod
    def card_fixtures():
        return [fixture for fixture in json.loads((ROOT / ".test-tmp/collection-producer.json").read_text(encoding="utf-8"))
                if fixture["name"].startswith("triple_triad_unconfirmed_")]


if __name__ == "__main__":
    unittest.main()
