"""Offline progression permission, evidence, bounded deferred transport and producer checks."""
from copy import deepcopy
from datetime import datetime, timedelta, timezone
import io
import json
from pathlib import Path
import threading
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import companion
import deferred_fixture
import progression_lookup
from progression_lookup import (SCOPE, SOURCES, STATES, READINESS, STATE_COUNTS, COUNT_FIELDS, ROW_FIELDS,
                                TOOL, INSTRUCTIONS, MAX_RESULT_BYTES, V25_CATEGORIES, V26_CATEGORIES, V27_CATEGORIES, V28_CATEGORIES, V30_CATEGORIES, V31_CATEGORIES,
                                POSITIVE_COMPLETION_CATEGORIES,
                                CATEGORY_INSTRUCTIONS, MENU_INSTRUCTIONS, permitted_tool, permitted_instructions,
                                validate_arguments, validate_result, validate_summary)
from collection_lookup_check import prepared
from companion_architecture_check import wire_request
from companion_request import normalize_request, recheck_request
from companion_tools import build_registry
from quest_lookup import QuestQueryBridge, encoded_size
from quest_lookup_check import completed, tool_message


def request(categories=None, **changes):
    value = wire_request(25)
    value["progression_lookup"] = {"scope": SCOPE, "categories": list(V25_CATEGORIES) if categories is None else categories}
    value.update(changes)
    return value


def modern_request(categories=None, **changes):
    value = wire_request(26)
    value["progression_lookup"] = {"scope": SCOPE, "categories": list(V26_CATEGORIES) if categories is None else categories}
    value.update(changes)
    return value


def latest_request(categories=None, **changes):
    value = wire_request(27)
    value["progression_lookup"] = {"scope": SCOPE, "categories": list(V27_CATEGORIES) if categories is None else categories}
    value.update(changes)
    return value


def current_request(categories=None, **changes):
    value = wire_request(32)
    value["progression_lookup"] = {"scope": SCOPE, "categories": list(SOURCES) if categories is None else categories}
    value.update(changes)
    return value


def row(ident, state, **changes):
    return {**dict.fromkeys(ROW_FIELDS), "id": ident, "name": f"Synthetic Progress {ident}", "state": state,
            "unknown_reason": "Synthetic unknown readiness." if state in ("unknown", "not_observed_complete") else None,
            **changes}


def result(category="achievements", **changes):
    if category == "battle_chocobo":
        entries = [row(1, "observed", rank=10, experience=25, experience_required=100),
                   row(2, "observed", rank=0), row(3, "observed", rank=10), row(4, "observed", rank=0),
                   row(5, "observed", progress=0)]
    elif category == "pvp":
        entries = [row(1, "observed", rank=30, experience=500000), row(2, "observed", rank=4, experience=25)]
    elif category == "achievement_progress":
        entries = [row(1, "observed", progress=17, required=20)]
    elif category == "achievements":
        entries = [row(1, "completed", completed=True), row(2, "not_completed", completed=False), row(3, "unknown")]
    elif category in POSITIVE_COMPLETION_CATEGORIES:
        entries = [row(1, "completed", completed=True), row(2, "not_observed_complete"), row(3, "unknown")]
    elif category == "hunting_log":
        entries = [row(10001, "completed", completed=True, rank=1, progress=5, required=5),
                   row(10002, "not_observed_complete", rank=1, progress=0, required=5), row(10003, "unknown")]
    elif category == "challenge_log":
        entries = [row(1, "completed", completed=True), row(47, "not_observed_complete", progress=0, required=3), row(56, "unknown")]
    elif category == "shared_fates":
        entries = [row(1, "observed", rank=2, progress=4, required=6), row(2, "unknown")]
    else:
        entries = [row(1, "observed", level=71, experience=4, experience_required=6), row(2, "unknown")]
    counts = {key: sum(entry["state"] == state for entry in entries) for state, key in STATE_COUNTS.items()}
    return {"scope": SCOPE, "category": category, "captured_at_utc": datetime.now(timezone.utc).isoformat(),
            "available": True, "unavailable_reason": None, "coverage": "Synthetic bounded named observations.",
            "source": SOURCES[category], "readiness": READINESS[category], "catalog_count": len(entries), **counts,
            "entries": entries, "matched_count": len(entries), "offset": 0, "limit": 50,
            "next_offset": None, "truncated": False, **changes}


def unavailable(category="achievements"):
    return result(category, available=False, unavailable_reason="Synthetic unavailable",
                  readiness="unavailable", **dict.fromkeys(COUNT_FIELDS), entries=[], matched_count=0)


def saved_result(category="shared_fates"):
    value = result(category)
    observed = value["entries"][0]
    newest = datetime.now(timezone.utc) - timedelta(days=1)
    older = newest - timedelta(days=2)
    value.update(readiness="saved_menu_observations", source="Saved local " + SOURCES[category],
                 captured_at_utc=newest.isoformat(), catalog_count=2, observed_count=2, unknown_count=0,
                 entries=[{**observed, "observed_at_utc": older.isoformat(), "observed_page": 0},
                          {**observed, "id": 2, "observed_at_utc": newest.isoformat(), "observed_page": 1}])
    return value


def event(ident=50, **arguments):
    return tool_message(ident=ident, tool="query_progression", arguments={"category": "achievements", **arguments})


class ProgressionChecks(unittest.TestCase):
    def test_current_wire32_preserves_older_progression_categories(self):
        summary = {"scope": SCOPE, "categories": list(V28_CATEGORIES)}
        for version in (28, 29):
            self.assertEqual(validate_summary(summary, version=version), summary)
        self.assertEqual(validate_summary({"scope": SCOPE, "categories": list(V30_CATEGORIES)}, version=30)["categories"], list(V30_CATEGORIES))
        self.assertEqual(validate_summary({"scope": SCOPE, "categories": list(V31_CATEGORIES)}, version=31)["categories"], list(V31_CATEGORIES))
        self.assertEqual(validate_summary({"scope": SCOPE, "categories": list(SOURCES)}, version=32)["categories"], list(SOURCES))
        with self.assertRaises(RuntimeError):
            validate_summary(summary, version=33)

    def test_activity_logs_require_v31_and_separate_permissions(self):
        for category in ("hunting_log", "sightseeing_log", "challenge_log"):
            meta = {"scope": SCOPE, "categories": [category]}
            for version in (25, 26, 27, 28, 29, 30):
                with self.subTest(category=category, version=version), self.assertRaises(RuntimeError):
                    normalize_request({**wire_request(version), "progression_lookup": meta})
            approved = normalize_request(current_request([category]))
            registry = build_registry(approved, {"progression": lambda _: None}, None, companion.CompanionFailure)
            self.assertEqual(registry["query_progression"].definition["inputSchema"]["properties"]["category"]["enum"], [category])
            client, _, outputs = prepared([event(category=category)])
            with patch.object(progression_lookup, "permitted_tool", return_value=deepcopy(TOOL)), \
                    patch.object(progression_lookup, "validate_arguments", side_effect=lambda args, *_: args), \
                    patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure) as failure:
                client.ask(current_request(["variant_notebook"]), progression_query=lambda _: self.fail("Unpermitted log callback"))
            self.assertEqual(failure.exception.kind, "permission")
            self.assertFalse(outputs[0]["result"]["success"])

    def test_activity_logs_reject_false_completion_invented_counters_and_saved_evidence(self):
        for category in ("hunting_log", "sightseeing_log", "challenge_log"):
            meta = {"scope": SCOPE, "categories": [category]}
            value = result(category)
            validate_result(value, meta, {"category": category})
            for fields in ({"completed": False}, {"state": "not_completed"},
                           {"observed_at_utc": value["captured_at_utc"], "observed_page": 0}):
                bad = deepcopy(value)
                bad["entries"][1].update(fields)
                with self.subTest(category=category, fields=fields), self.assertRaises(RuntimeError):
                    validate_result(bad, meta, {"category": category})
            bad = deepcopy(value)
            bad.update(readiness="saved_menu_observations", source="Saved local " + SOURCES[category])
            with self.assertRaises(RuntimeError):
                validate_result(bad, meta, {"category": category})
        bad = result("challenge_log")
        bad["entries"][1].update(id=2, progress=2, required=3)
        with self.assertRaises(RuntimeError):
            validate_result(bad, {"scope": SCOPE, "categories": ["challenge_log"]}, {"category": "challenge_log"})
        self.assertIn("weekly reset", permitted_instructions({"scope": SCOPE, "categories": ["challenge_log"]}))

    def test_challenge_targets_and_inferred_completed_totals_keep_partial_progress_unknown(self):
        meta, arguments = {"scope": SCOPE, "categories": ["challenge_log"]}, {"category": "challenge_log"}
        value = result("challenge_log")
        value["entries"][0].update(progress=3, required=3)
        value["entries"][1].update(id=2, progress=None, required=5)
        self.assertIs(validate_result(value, meta, arguments), value)
        self.assertIsNone(value["entries"][1]["progress"])
        for fields in ({"required": 0}, {"required": -1}, {"required": 2147483648}, {"required": True},
                       {"progress": 0}, {"progress": 2}, {"progress": 5}):
            bad = deepcopy(value)
            bad["entries"][1].update(fields)
            with self.subTest(fields=fields), self.assertRaises(RuntimeError):
                validate_result(bad, meta, arguments)
        bad = deepcopy(value)
        bad["entries"][0].update(progress=2)
        with self.assertRaises(RuntimeError):
            validate_result(bad, meta, arguments)
        # Cached rows from previous releases may still omit both fields.
        old = result("challenge_log")
        self.assertIs(validate_result(old, meta, arguments), old)
        saved = deepcopy(value)
        saved.update(readiness="saved_menu_observations", source="Saved local " + SOURCES["challenge_log"])
        for entry in saved["entries"]:
            entry.update(observed_at_utc=saved["captured_at_utc"], observed_page=0)
        self.assertIs(validate_result(saved, meta, arguments), saved)
        guidance = permitted_instructions(meta)
        self.assertIn("independently available even when progress is null", guidance)
        self.assertIn("reopening does not add support", guidance)

    def test_corroborated_leve_counters_survive_live_and_saved_transport_without_expanding_other_rows(self):
        meta, arguments = {"scope": SCOPE, "categories": ["challenge_log"]}, {"category": "challenge_log"}
        for ident, required in ((11, 5), (12, 20)):
            value = result("challenge_log")
            value["entries"][1].update(id=ident, progress=3, required=required)
            self.assertIs(validate_result(value, meta, arguments), value)
            value.update(readiness="saved_menu_observations", source="Saved local " + SOURCES["challenge_log"])
            for entry in value["entries"]:
                entry.update(observed_at_utc=value["captured_at_utc"], observed_page=4)
            self.assertIs(validate_result(value, meta, arguments), value)
        bad = result("challenge_log")
        bad["entries"][1].update(id=13, progress=3, required=5)
        with self.assertRaises(RuntimeError):
            validate_result(bad, meta, arguments)
        guidance = permitted_instructions(meta)
        self.assertIn("two stable samples", guidance)
        self.assertIn("closing it retains the last verified reading", guidance)

    def test_activity_log_csharp_producer_survives_deferred_transport(self):
        path = Path(__file__).resolve().parents[2] / ".test-tmp" / "activity-logs-producer.json"
        self.assertTrue(path.exists(), "Produce .test-tmp/activity-logs-producer.json with ActivityLogs.Tests")
        fixtures = json.loads(path.read_text(encoding="utf-8"))
        self.assertEqual(len(fixtures), 18)
        challenge = next(fixture["result"] for fixture in fixtures if fixture["name"] == "challenge_log")
        leve = next(entry for entry in challenge["entries"] if entry["id"] == 11)
        self.assertEqual((leve["progress"], leve["required"], leve["completed"]), (3, 5, None))
        categories, unavailable_seen, page_seen = set(), set(), set()
        for fixture in fixtures:
            with self.subTest(name=fixture["name"]):
                value, arguments = fixture["result"], fixture["arguments"]
                category = arguments["category"]
                categories.add(category)
                validate_result(value, {"scope": SCOPE, "categories": [category]}, arguments)
                if not value["available"]: unavailable_seen.add(category)
                if value["truncated"]: page_seen.add(category)
                client, _, outputs = prepared([event(**arguments), *completed()])
                with patch.object(companion, "emit"):
                    client.ask(current_request([category]), progression_query=lambda _, data=value: deepcopy(data))
                self.assertEqual(json.loads(outputs[0]["result"]["contentItems"][0]["text"]), value)
        self.assertEqual(categories, {"hunting_log", "sightseeing_log", "challenge_log"})
        self.assertEqual(unavailable_seen, categories)
        self.assertEqual(page_seen, categories)

    def test_variant_requires_v30_and_explicit_permission(self):
        meta = {"scope": SCOPE, "categories": ["variant_notebook"]}
        for version in (25, 26, 27, 28, 29):
            with self.subTest(version=version), self.assertRaises(RuntimeError):
                normalize_request({**wire_request(version), "progression_lookup": meta})
        approved = normalize_request(current_request(["variant_notebook"]))
        registry = build_registry(approved, {"progression": lambda _: None}, None, companion.CompanionFailure)
        self.assertEqual(set(registry), {"query_progression"})
        schema = registry["query_progression"].definition["inputSchema"]["properties"]
        self.assertEqual(schema["category"]["enum"], ["variant_notebook"])
        self.assertNotIn("not_completed", schema["state"]["enum"])
        for wire in (latest_request(), current_request(["trust"])):
            client, _, outputs = prepared([event(category="variant_notebook")])
            with patch.object(progression_lookup, "permitted_tool", return_value=deepcopy(TOOL)), \
                    patch.object(progression_lookup, "validate_arguments", side_effect=lambda args, *_: args), \
                    patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure) as failure:
                client.ask(wire, progression_query=lambda _: self.fail("Unpermitted Variant callback"))
            self.assertEqual(failure.exception.kind, "permission")
            self.assertFalse(outputs[0]["result"]["success"])

    def test_variant_negative_fields_saved_metadata_and_changes_fail_closed(self):
        meta = {"scope": SCOPE, "categories": ["variant_notebook"]}
        value = result("variant_notebook")
        validate_result(value, meta, {"category": "variant_notebook"})
        for fields in ({"completed": False}, {"state": "not_completed"}, {"progress": 0},
                       {"observed_at_utc": value["captured_at_utc"], "observed_page": 0}):
            bad = deepcopy(value)
            bad["entries"][1].update(fields)
            with self.subTest(fields=fields), self.assertRaises(RuntimeError):
                validate_result(bad, meta, {"category": "variant_notebook"})
        changed = deepcopy(value)
        changed["entries"][0]["name"] = "Changed record"
        values = iter((value, changed))
        client, _, outputs = prepared([event(category="variant_notebook"), event(ident=51, category="variant_notebook")])
        with patch.object(companion, "emit"), self.assertRaises(RuntimeError):
            client.ask(current_request(["variant_notebook"]), progression_query=lambda _: next(values))
        self.assertTrue(outputs[0]["result"]["success"])
        instructions = permitted_instructions(meta)
        self.assertIn("VVDNotebookContents RowIds", instructions)
        self.assertIn("negative readiness", instructions)
        self.assertIn("Criterion", instructions)

    def test_variant_csharp_producer_survives_deferred_transport(self):
        path = Path(__file__).resolve().parents[2] / ".test-tmp" / "variant-notebook-producer.json"
        self.assertTrue(path.exists(), "Produce .test-tmp/variant-notebook-producer.json with VariantNotebook.Tests")
        fixtures = json.loads(path.read_text(encoding="utf-8"))
        self.assertGreaterEqual(len(fixtures), 7)
        states, unavailable_seen, page_seen = set(), False, False
        for fixture in fixtures:
            with self.subTest(name=fixture["name"]):
                value, arguments = fixture["result"], fixture["arguments"]
                meta = {"scope": SCOPE, "categories": ["variant_notebook"]}
                validate_result(value, meta, arguments)
                states.update(entry["state"] for entry in value["entries"])
                unavailable_seen |= not value["available"]
                page_seen |= value["truncated"]
                client, _, outputs = prepared([event(**arguments), *completed()])
                with patch.object(companion, "emit"):
                    client.ask(current_request(["variant_notebook"]), progression_query=lambda _, data=value: deepcopy(data))
                self.assertEqual(json.loads(outputs[0]["result"]["contentItems"][0]["text"]), value)
        self.assertEqual(states, {"completed", "not_observed_complete"})
        self.assertTrue(unavailable_seen and page_seen)

    def test_saved_menu_rows_preserve_per_row_times_and_partial_coverage(self):
        for category in ("shared_fates", "trust"):
            value = saved_result(category)
            summary = {"scope": SCOPE, "categories": [category]}
            self.assertIs(validate_result(value, summary, {"category": category}), value)
            self.assertNotEqual(value["entries"][0]["observed_at_utc"], value["captured_at_utc"])
            partial = {**value, "entries": value["entries"][:1], "matched_count": 1}
            self.assertIs(validate_result(partial, summary, {"category": category, "ids": [1]}), partial)
            self.assertIn("saved_menu_observations", permitted_instructions(summary))

    def test_saved_menu_metadata_cannot_be_missing_future_or_applied_to_other_evidence(self):
        for category in ("shared_fates", "trust"):
            value = saved_result(category)
            summary = {"scope": SCOPE, "categories": [category]}
            changes = [{"observed_page": -1}, {"observed_page": 3 if category == "shared_fates" else 80},
                       {"observed_page": True}, {"observed_at_utc": None},
                       {"observed_at_utc": datetime.now(timezone.utc).isoformat()}]
            for fields in changes:
                malformed = deepcopy(value)
                malformed["entries"][0].update(fields)
                with self.subTest(category=category, fields=fields), self.assertRaises(RuntimeError):
                    validate_result(malformed, summary, {"category": category})
            for field in ("observed_page", "observed_at_utc"):
                malformed = deepcopy(value)
                del malformed["entries"][0][field]
                with self.assertRaises(RuntimeError):
                    validate_result(malformed, summary, {"category": category})
            malformed = result(category)
            malformed["entries"][0].update(observed_at_utc=value["captured_at_utc"], observed_page=0)
            with self.assertRaises(RuntimeError):
                validate_result(malformed, summary, {"category": category})
        value = result("achievements", readiness="saved_menu_observations", source="Saved local " + SOURCES["achievements"])
        with self.assertRaises(RuntimeError):
            validate_result(value, {"scope": SCOPE, "categories": ["achievements"]}, {"category": "achievements"})

    def test_saved_row_dates_and_pages_remain_frozen_within_answer(self):
        first = saved_result("trust")
        for fields in ({"observed_page": 2},
                       {"observed_at_utc": (datetime.fromisoformat(first["entries"][0]["observed_at_utc"]) - timedelta(days=1)).isoformat()}):
            changed = deepcopy(first)
            changed["entries"][0].update(fields)
            client, _, outputs = prepared([event(category="trust"), event(ident=51, category="trust")])
            values = iter((first, changed))
            with self.subTest(fields=fields), patch.object(companion, "emit"), self.assertRaises(RuntimeError):
                client.ask(current_request(["trust"]), progression_query=lambda _: next(values))
            self.assertTrue(outputs[0]["result"]["success"])
            self.assertFalse(outputs[1]["result"]["success"])

    def test_saved_logs_keep_unknowns_and_reject_cross_book_metadata(self):
        for category in ("hunting_log", "challenge_log"):
            value = result(category)
            value.update(readiness="saved_menu_observations", source="Saved local " + SOURCES[category])
            for entry in value["entries"]:
                entry.update(observed_at_utc=value["captured_at_utc"], observed_page=0)
            summary = {"scope": SCOPE, "categories": [category]}
            self.assertIs(validate_result(value, summary, {"category": category}), value)
            for fields in ({"observed_page": 60 if category == "hunting_log" else 15}, {"completed": False}):
                bad = deepcopy(value)
                bad["entries"][0].update(fields)
                with self.subTest(category=category, fields=fields), self.assertRaises(RuntimeError):
                    validate_result(bad, summary, {"category": category})
            if category == "hunting_log":
                for fields in ({"observed_page": 5}, {"rank": 2}):
                    bad = deepcopy(value)
                    bad["entries"][0].update(fields)
                    with self.assertRaises(RuntimeError):
                        validate_result(bad, summary, {"category": category})

    def test_recent_records_expire_per_row_and_challenge_reset(self):
        fixed = datetime(2026, 10, 27, 8, 0, 0, tzinfo=timezone.utc)
        class FixedDatetime(datetime):
            @classmethod
            def now(cls, tz=None):
                return fixed
        for category in ("challenge_log", "achievement_progress"):
            value = result(category, captured_at_utc=fixed.isoformat(), readiness="saved_menu_observations",
                           source="Saved local " + SOURCES[category])
            for entry in value["entries"]:
                entry.update(observed_at_utc=fixed.isoformat(), observed_page=0)
            summary = {"scope": SCOPE, "categories": [category]}
            with patch.object(progression_lookup, "datetime", FixedDatetime):
                self.assertIs(validate_result(value, summary, {"category": category}), value)
                for age in ([timedelta(seconds=1), timedelta(minutes=15)] if category == "challenge_log" else [timedelta(minutes=15)]):
                    bad = deepcopy(value)
                    bad["entries"][0]["observed_at_utc"] = (fixed - age).isoformat()
                    with self.subTest(category=category, age=age), self.assertRaises(RuntimeError):
                        validate_result(bad, summary, {"category": category})

    def test_guidance_contains_only_granted_categories_and_no_duplicate_shared_safety(self):
        self.assertEqual(set(CATEGORY_INSTRUCTIONS), set(SOURCES))
        for category in SOURCES:
            guidance = permitted_instructions({"scope": SCOPE, "categories": [category]})
            self.assertEqual(guidance.count(INSTRUCTIONS), 1)
            self.assertEqual(guidance.count("Never send private progression state"), 1)
            self.assertEqual(guidance.count("12 calls and 120KB"), 1)
            self.assertIn(CATEGORY_INSTRUCTIONS[category], guidance)
            for unrelated in SOURCES.keys() - {category}:
                self.assertNotIn(CATEGORY_INSTRUCTIONS[unrelated], guidance)
            self.assertEqual(guidance.count(MENU_INSTRUCTIONS), 1 if category in progression_lookup.SAVED_PAGE_LIMITS else 0)
        all_guidance = permitted_instructions({"scope": SCOPE, "categories": list(SOURCES)})
        self.assertEqual(all_guidance.count(INSTRUCTIONS), 1)
        self.assertEqual(all_guidance.count(MENU_INSTRUCTIONS), 1)
        self.assertTrue(all(all_guidance.count(paragraph) == 1 for paragraph in CATEGORY_INSTRUCTIONS.values()))

    def test_progress_record_requires_v27_and_does_not_expand_old_achievement_grants(self):
        for version in (25, 26):
            wire = wire_request(version)
            wire["progression_lookup"] = {"scope": SCOPE, "categories": ["achievement_progress"]}
            with self.subTest(version=version), self.assertRaises(RuntimeError):
                normalize_request(wire)
            with self.assertRaises(RuntimeError):
                validate_summary(wire["progression_lookup"], version=version)
            wire["progression_lookup"]["categories"] = list(V25_CATEGORIES if version == 25 else V26_CATEGORIES)
            self.assertEqual(normalize_request(wire).progression_lookup, wire["progression_lookup"])
        approved = normalize_request(latest_request(["achievement_progress"]))
        registry = build_registry(approved, {"progression": lambda _: None}, None, companion.CompanionFailure)
        schema = registry["query_progression"].definition["inputSchema"]["properties"]
        self.assertEqual(schema["category"]["enum"], ["achievement_progress"])
        self.assertEqual(schema["state"]["enum"], ["all", "observed"])
        for wire in (request(["achievements"]), modern_request(["achievements"]), latest_request(["achievements"])):
            client, _, outputs = prepared([event(category="achievement_progress")])
            with patch.object(progression_lookup, "permitted_tool", return_value=deepcopy(TOOL)), \
                    patch.object(progression_lookup, "validate_arguments", side_effect=lambda args, *_: args), \
                    patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure) as failure:
                client.ask(wire, progression_query=lambda _: self.fail("Progress category was not granted"))
            self.assertEqual(failure.exception.kind, "permission")
            self.assertFalse(outputs[0]["result"]["success"])

    def test_progress_record_preserves_uint_counters_without_inferring_completion(self):
        meta, arguments = {"scope": SCOPE, "categories": ["achievement_progress"]}, {"category": "achievement_progress"}
        for current, target in ((0, 1), (1, 1), (4294967295, 4294967295), (4294967295, 1)):
            value = result("achievement_progress")
            value["entries"][0].update(progress=current, required=target)
            validate_result(value, meta, arguments)
            self.assertIsNone(value["entries"][0]["completed"])
        for fields in ({"progress": -1}, {"progress": True}, {"progress": 4294967296}, {"progress": 1.0},
                       {"required": 0}, {"required": True}, {"required": 4294967296}, {"required": None},
                       {"completed": True}, {"rank": 1}, {"level": 1}, {"experience": 1},
                       {"state": "unknown", "unknown_reason": "Unloaded"}, {"state": "completed"},
                       {"unknown_reason": "unexpected"}):
            value = result("achievement_progress")
            value["entries"][0].update(fields)
            with self.subTest(fields=fields), self.assertRaises(RuntimeError):
                validate_result(value, meta, arguments)
        self.assertIn("PREVIOUS menu selection", permitted_instructions(meta))
        self.assertIn("can exceed required", permitted_instructions(meta))
        self.assertIn("Match the returned", permitted_instructions(meta))

    def test_progress_record_is_single_cached_id_and_filtered_misses_are_not_zeros(self):
        meta, arguments = {"scope": SCOPE, "categories": ["achievement_progress"]}, {"category": "achievement_progress"}
        first = result("achievement_progress")
        validate_result(first, meta, arguments)
        miss = result("achievement_progress", entries=[], matched_count=0)
        validate_result(miss, meta, {**arguments, "ids": [2]})
        validate_result(miss, meta, {**arguments, "query": "absent"})
        self.assertEqual((miss["catalog_count"], miss["observed_count"]), (1, 1))
        for value in (result("achievement_progress", catalog_count=0, observed_count=0, entries=[], matched_count=0),
                      result("achievement_progress", catalog_count=2, observed_count=2, matched_count=2,
                             entries=[first["entries"][0], {**first["entries"][0], "id": 2}]),
                      result("achievement_progress", readiness="visible_selected_achievement_progress")):
            with self.assertRaises(RuntimeError):
                validate_result(value, meta, arguments)
        with self.assertRaises(RuntimeError):
            validate_result(first, meta, {**arguments, "ids": [2]})
        client, _, outputs = prepared([event(category="achievement_progress", ids=[2]), *completed()])
        with patch.object(companion, "emit"):
            client.ask(latest_request(["achievement_progress"]), progression_query=lambda _: miss)
        transported = json.loads(outputs[0]["result"]["contentItems"][0]["text"])
        self.assertEqual(transported["entries"], [])
        self.assertEqual(transported["observed_count"], 1)

    def test_progress_record_id_and_counters_are_frozen_within_answer(self):
        first = result("achievement_progress")
        for fields in ({"id": 2}, {"progress": 18}, {"required": 21}, {"name": "Other cached achievement"}):
            changed = deepcopy(first)
            changed["entries"][0].update(fields)
            client, _, outputs = prepared([event(category="achievement_progress"), event(ident=51, category="achievement_progress")])
            values = iter((first, changed))
            with self.subTest(fields=fields), patch.object(companion, "emit"), self.assertRaises(RuntimeError):
                client.ask(latest_request(["achievement_progress"]), progression_query=lambda _: next(values))
            self.assertTrue(outputs[0]["result"]["success"])
            self.assertFalse(outputs[1]["result"]["success"])

    def test_gathering_requires_v26_explicit_permission_and_preserves_v25(self):
        with self.assertRaises(RuntimeError):
            normalize_request(request(["gathering_log"]))
        with self.assertRaises(RuntimeError):
            validate_summary({"scope": SCOPE, "categories": ["gathering_log"]}, version=25)
        with self.assertRaises(RuntimeError):
            validate_summary(None, version=24)
        with self.assertRaises(RuntimeError):
            validate_summary(None, version=True)
        self.assertEqual(normalize_request(request()).progression_lookup["categories"], list(V25_CATEGORIES))
        wire = modern_request(["gathering_log"])
        approved = normalize_request(wire)
        registry = build_registry(approved, {"progression": lambda _: None}, None, companion.CompanionFailure)
        self.assertEqual(set(registry), {"query_progression"})
        schema = registry["query_progression"].definition["inputSchema"]["properties"]
        self.assertEqual(schema["category"]["enum"], ["gathering_log"])
        self.assertEqual(set(schema["state"]["enum"]), {"all", "completed", "not_observed_complete", "unknown"})
        self.assertIsNone(approved.collection_lookup)
        wire["progression_lookup"]["categories"] = ["trust"]
        self.assertEqual(approved.progression_lookup["categories"], ["gathering_log"])
        with self.assertRaises(RuntimeError):
            recheck_request(wire, approved)
        self.assertIsNone(normalize_request(modern_request(progression_lookup=None)).progression_lookup)

    def test_gathering_denial_is_fatal_despite_bypassed_schema_and_argument_validation(self):
        for wire in (request(), modern_request(["aether_currents"])):
            client, _, outputs = prepared([event(category="gathering_log")])
            with patch.object(progression_lookup, "permitted_tool", return_value=deepcopy(TOOL)), \
                    patch.object(progression_lookup, "validate_arguments", side_effect=lambda args, *_: args), \
                    patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure) as failure:
                client.ask(wire, progression_query=lambda _: self.fail("Unpermitted gathering callback"))
            self.assertEqual(failure.exception.kind, "permission")
            self.assertFalse(outputs[-1]["result"]["success"])

    def test_gathering_positive_only_not_held_items_or_verified_incomplete(self):
        meta, arguments = {"scope": SCOPE, "categories": ["gathering_log"]}, {"category": "gathering_log"}
        value = result("gathering_log")
        validate_result(value, meta, arguments)
        self.assertIs(value["entries"][0]["completed"], True)
        self.assertIsNone(value["entries"][1]["completed"])
        for fields in ({"completed": False}, {"state": "not_completed"}, {"item_id": 88}, {"rank": 1}):
            invalid = deepcopy(value)
            invalid["entries"][1].update(fields)
            with self.subTest(fields=fields), self.assertRaises(RuntimeError):
                validate_result(invalid, meta, arguments)
        with self.assertRaises(RuntimeError):
            validate_arguments({"category": "gathering_log", "state": "not_completed"})
        client, calls, outputs = prepared([event(category="gathering_log"), *completed()])
        with patch.object(companion, "emit"):
            client.ask(modern_request(["gathering_log"]), progression_query=lambda _: value)
        self.assertTrue(outputs[0]["result"]["success"])
        deferred_fixture.assert_exposed(self, client, calls, [permitted_tool(meta)])
        self.assertIn("IDs are GatheringItem RowIds", permitted_instructions(meta))
        self.assertIn("excludes fishing", permitted_instructions(meta))

    def test_gathering_rows_and_current_access_remain_guarded(self):
        first = result("gathering_log")
        changed = deepcopy(first)
        changed["entries"][0]["name"] = "Changed synthetic gathered item"
        client, _, outputs = prepared([event(category="gathering_log"), event(ident=51, category="gathering_log")])
        values = iter((first, changed))
        with patch.object(companion, "emit"), self.assertRaises(RuntimeError):
            client.ask(modern_request(["gathering_log"]), progression_query=lambda _: next(values))
        self.assertTrue(outputs[0]["result"]["success"])
        self.assertFalse(outputs[1]["result"]["success"])
        client, _, outputs = prepared([event(category="gathering_log")])
        def revoked(_):
            raise PermissionError("Synthetic gathering access revoked")
        with patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure) as failure:
            client.ask(modern_request(["gathering_log"]), progression_query=revoked)
        self.assertEqual(failure.exception.kind, "permission")
        self.assertFalse(outputs[0]["result"]["success"])

    def test_wire25_exact_summary_and_legacy_shapes(self):
        wire = request(["achievements", "trust"])
        approved = normalize_request(wire)
        self.assertEqual(approved.progression_lookup, wire["progression_lookup"])
        self.assertEqual(approved.context({})["progression_lookup"], wire["progression_lookup"])
        wire["progression_lookup"]["categories"].append("shared_fates")
        self.assertEqual(approved.progression_lookup["categories"], ["achievements", "trust"])
        with self.assertRaises(RuntimeError):
            recheck_request(wire, approved)
        self.assertIsNone(normalize_request(request(progression_lookup=None)).progression_lookup)
        for version in range(1, 25):
            legacy = wire_request(version)
            self.assertIsNone(normalize_request(legacy).progression_lookup)
            legacy["progression_lookup"] = None
            with self.subTest(version=version), self.assertRaises(RuntimeError):
                normalize_request(legacy)
        for mutation in (lambda value: value.pop("progression_lookup"), lambda value: value.update(version=30),
                         lambda value: value.update(progression_lookup={"scope": SCOPE})):
            value = request()
            mutation(value)
            with self.assertRaises(RuntimeError):
                normalize_request(value)

    def test_summary_is_exact_and_permissions_are_independent(self):
        for value in ({}, {"scope": SCOPE}, {"scope": SCOPE, "categories": []},
                      {"scope": "all_progression", "categories": ["trust"]},
                      {"scope": SCOPE, "categories": ["trust", "trust"]},
                      {"scope": SCOPE, "categories": ["duties"]},
                      {"scope": SCOPE, "categories": [True]},
                      {"scope": SCOPE, "categories": "trust"},
                      {"scope": SCOPE, "categories": ["trust"], "entries": []}):
            with self.subTest(value=value), self.assertRaises(RuntimeError):
                validate_summary(value)
        for category in V25_CATEGORIES:
            wire = request([category])
            approved = normalize_request(wire)
            registry = build_registry(approved, {"progression": lambda _: None}, None, companion.CompanionFailure)
            self.assertEqual(set(registry), {"query_progression"})
            schema = registry["query_progression"].definition["inputSchema"]["properties"]
            self.assertEqual(schema["category"]["enum"], [category])
            self.assertEqual(set(schema["state"]["enum"]), {"all", *STATES[category]})
            schema["category"]["enum"].append("invalid")
            self.assertEqual(approved.progression_lookup["categories"], [category])

    def test_arguments_are_category_specific_and_bounded(self):
        for category in SOURCES:
            expected = {"category": category, "query": "", "ids": [], "state": "all", "offset": 0, "limit": 50}
            self.assertEqual(validate_arguments({"category": category}), expected)
            for state in STATES[category]:
                validate_arguments({"category": category, "state": state, "ids": [4294967295], "offset": 8192})
        for changes in ({}, {"category": "duties"}, {"category": True}, {"category": ["trust"]},
                        {"query": "x" * 101}, {"query": "🐣" * 51}, {"query": "\ud800"},
                        {"query": "bad\n"}, {"query": "\u0085"}, {"query": None},
                        {"ids": [True]}, {"ids": [0]}, {"ids": [4294967296]}, {"ids": [1, 1]},
                        {"ids": list(range(1, 22))}, {"ids": "1"}, {"offset": True}, {"offset": 8193},
                        {"limit": 0}, {"limit": 51}, {"limit": 1.0}, {"state": False}, {"state": "observed"},
                        {"url": "https://example.invalid"}):
            arguments = {"category": "achievements", **changes} if changes else {}
            with self.subTest(arguments=arguments), self.assertRaises(RuntimeError):
                validate_arguments(arguments)
        with self.assertRaises(RuntimeError):
            validate_arguments({"category": "aether_currents", "state": "not_completed"})
        with self.assertRaises(RuntimeError):
            validate_arguments({"category": "trust"}, {"scope": SCOPE, "categories": ["achievements"]})

    def test_category_evidence_keeps_loaded_false_separate_from_unknown(self):
        meta = current_request()["progression_lookup"]
        for category in SOURCES:
            validate_result(result(category), meta, {"category": category})
            validate_result(unavailable(category), meta, {"category": category})
        self.assertIs(result()["entries"][1]["completed"], False)
        self.assertIsNone(result("aether_currents")["entries"][1]["completed"])
        self.assertIn("Never treat null as false", INSTRUCTIONS)
        self.assertIn("loaded main completion bitmap", permitted_instructions(meta))
        self.assertIn("Never send private progression", INSTRUCTIONS)
        changes = [
            ("achievements", 1, {"completed": None}), ("achievements", 1, {"completed": 0}),
            ("achievements", 0, {"completed": 1}), ("achievements", 2, {"completed": False}),
            ("achievements", 0, {"rank": 1}), ("achievements", 1, {"unknown_reason": "unexpected"}),
            ("aether_currents", 1, {"completed": False}), ("aether_currents", 1, {"state": "not_completed", "completed": False}),
            ("aether_currents", 1, {"unknown_reason": None}),
            ("shared_fates", 0, {"rank": 0}), ("shared_fates", 0, {"rank": 11}),
            ("shared_fates", 0, {"progress": 7}), ("shared_fates", 0, {"required": 65536}),
            ("shared_fates", 0, {"level": 1}), ("shared_fates", 1, {"progress": 0}),
            ("trust", 0, {"level": 0}), ("trust", 0, {"level": 201}),
            ("trust", 0, {"experience": 7}), ("trust", 0, {"experience": True}),
            ("trust", 0, {"experience_required": 4294967296}), ("trust", 0, {"rank": 1}),
            ("trust", 1, {"level": 1}), ("trust", 0, {"extra": 1}),
        ]
        for category, index, fields in changes:
            bad = result(category)
            bad["entries"][index].update(fields)
            with self.subTest(category=category, fields=fields), self.assertRaises(RuntimeError):
                validate_result(bad, meta, {"category": category})

    def test_aether_discovery_counts_and_legacy_source(self):
        meta, args = current_request(["aether_currents"])["progression_lookup"], {"category": "aether_currents"}
        current = result("aether_currents")
        current["entries"][0].update(progress=9, required=9)
        current["entries"][1].update(progress=3, required=15)
        validate_result(current, meta, args)
        for count in (0, 15):
            full = deepcopy(current)
            full["entries"][1]["progress"] = count
            validate_result(full, meta, args)
            self.assertIsNone(full["entries"][1]["completed"])
        for index, changes in (
            (0, {"progress": 8}), (1, {"progress": -1}), (1, {"progress": True}),
            (1, {"progress": 16}), (1, {"required": 16}), (1, {"required": None}),
            (1, {"progress": None}), (1, {"required": 0}), (1, {"completed": False}),
            (2, {"progress": 0, "required": 9}),
        ):
            bad = deepcopy(current)
            bad["entries"][index].update(changes)
            with self.subTest(index=index, changes=changes), self.assertRaises(RuntimeError):
                validate_result(bad, meta, args)
        legacy = result("aether_currents", source=progression_lookup.LEGACY_AETHER_SOURCE)
        validate_result(legacy, meta, args)
        legacy["entries"][0].update(progress=9, required=9)
        with self.assertRaises(RuntimeError):
            validate_result(legacy, meta, args)
        with self.assertRaises(RuntimeError):
            validate_result(result("aether_currents", source="Unverified current source"), meta, args)
        with self.assertRaises(RuntimeError):
            validate_result(current, current_request(["achievements"])["progression_lookup"], args)
        self.assertIn("lower bound", permitted_instructions(meta))
        self.assertIn("Summary counts cover zones", permitted_instructions(meta))

    def test_counts_provenance_filters_paging_and_unavailable_are_strict(self):
        meta, args = request()["progression_lookup"], {"category": "achievements"}
        first = result()
        validate_result(result(entries=[], matched_count=0, **dict.fromkeys(COUNT_FIELDS, 0)), meta, args)
        validate_result(result(entries=first["entries"][:1], limit=1, next_offset=1, truncated=True), meta, {**args, "limit": 1})
        validate_result(result(entries=first["entries"][1:], offset=1), meta, {**args, "offset": 1})
        validate_result(result(entries=[], offset=8192), meta, {**args, "offset": 8192})
        for state, entry in zip(STATES["achievements"], first["entries"]):
            validate_result(result(entries=[entry], matched_count=1), meta, {**args, "state": state})
        for fields, arguments in (({"category": "trust"}, args), ({"source": "arbitrary"}, args),
                                  ({"readiness": "positive_observations_only"}, args), ({"catalog_count": 4}, args),
                                  ({"unknown_count": True}, args), ({"unavailable_reason": "unexpected"}, args),
                                  ({"matched_count": 2}, args), ({"truncated": True}, args), ({"next_offset": True}, args),
                                  ({"offset": 1}, args), ({"coverage": ""}, args),
                                  ({"captured_at_utc": (datetime.now(timezone.utc) + timedelta(days=1)).isoformat()}, args),
                                  ({}, {**args, "ids": [1]}), ({}, {**args, "query": "absent"}),
                                  ({"entries": [first["entries"][0]] * 3}, args)):
            with self.subTest(fields=fields, arguments=arguments), self.assertRaises(RuntimeError):
                validate_result(result(**fields), meta, arguments)
        for key in COUNT_FIELDS:
            bad = unavailable()
            bad[key] = 0
            with self.subTest(key=key), self.assertRaises(RuntimeError):
                validate_result(bad, meta, args)

    def test_deferred_dispatch_is_on_demand_with_permission_only_context(self):
        client, calls, outputs = prepared([event(), *completed()])
        reads = []
        def query(arguments):
            self.assertIn("turn/start", [method for method, _ in calls])
            reads.append(arguments)
            return result()
        with patch.object(companion, "emit") as emit:
            client.ask(request(), progression_query=query)
        self.assertEqual(reads, [validate_arguments({"category": "achievements"})])
        deferred_fixture.assert_exposed(self, client, calls, [permitted_tool(request()["progression_lookup"])])
        self.assertIn(INSTRUCTIONS, deferred_fixture.instructions(client, calls))
        turn = next(parameters for method, parameters in calls if method == "turn/start")
        self.assertNotIn('"entries"', json.dumps(turn))
        self.assertNotIn("Synthetic Progress", json.dumps(turn))
        self.assertTrue(outputs[0]["result"]["success"])
        self.assertIn("local progression lookups", emit.call_args.args[0]["question_context"])
        self.assertTrue(any(call.args[0].get("performance", {}).get("stage") == "tool.progression" for call in emit.call_args_list))

    def test_result_size_and_native_text_bounds_are_enforced(self):
        meta, arguments = request()["progression_lookup"], {"category": "achievements"}
        for fields in ({"name": "Bad\nname"}, {"name": "🐣" * 65}, {"name": "\ud800"},
                       {"unknown_reason": "x" * 257}, {"unknown_reason": "\u0085"}):
            value = result()
            value["entries"][2].update(fields)
            with self.subTest(fields=fields), self.assertRaises(RuntimeError):
                validate_result(value, meta, arguments)
        entries = [row(ident, "unknown", name="🐣" * 64, unknown_reason="x" * 256) for ident in range(1, 51)]
        value = result(entries=entries, matched_count=50, **{
            **dict.fromkeys(COUNT_FIELDS, 0), "catalog_count": 50, "unknown_count": 50})
        self.assertGreater(encoded_size(value), MAX_RESULT_BYTES)
        with self.assertRaises(RuntimeError):
            validate_result(value, meta, arguments)

    def test_missing_permission_unused_reader_and_missing_callback_do_not_read(self):
        client, calls, _ = prepared(completed())
        with patch.object(companion, "emit"):
            client.ask(request(progression_lookup=None), progression_query=lambda _: self.fail("Not permitted"))
        self.assertEqual(next(params for method, params in calls if method == "thread/start")["dynamicTools"], [])
        client, _, _ = prepared(completed())
        with patch.object(companion, "emit"):
            client.ask(request(), progression_query=lambda _: self.fail("Not requested"))
        client, _, outputs = prepared([event()])
        with patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure) as failure:
            client.ask(request(progression_lookup=None), progression_query=lambda _: self.fail("Disabled"))
        self.assertEqual(failure.exception.kind, "permission")
        self.assertFalse(outputs[0]["result"]["success"])
        client, calls, _ = prepared(completed())
        with self.assertRaises(RuntimeError):
            client.ask(request())
        self.assertFalse(calls)

    def test_category_denial_is_fatal_even_if_tool_schema_and_validator_are_bypassed(self):
        for category in ("aether_currents", "shared_fates", "trust"):
            client, _, outputs = prepared([event(category=category)])
            with patch.object(progression_lookup, "permitted_tool", return_value=deepcopy(TOOL)), \
                    patch.object(progression_lookup, "validate_arguments", side_effect=lambda args, *_: args), \
                    patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure) as failure:
                client.ask(request(["achievements"]), progression_query=lambda _: self.fail("Unauthorized callback"))
            self.assertEqual(failure.exception.kind, "permission")
            self.assertFalse(outputs[0]["result"]["success"])

    def test_direct_and_undiscovered_calls_are_rejected(self):
        for tool, arguments in (("query_progression", {"category": "achievements"}),
                                ("invoke_tool", {"name": "query_progression", "arguments": {"category": "achievements"}})):
            client, _, _ = prepared([tool_message(ident=50, tool=tool, arguments=arguments)], raw=True)
            with self.subTest(tool=tool), patch.object(companion, "emit"), self.assertRaises(RuntimeError):
                client.ask(request(), progression_query=lambda _: self.fail("Undiscovered"))

    def test_capture_and_returned_rows_are_frozen_per_category(self):
        first = result()
        for fields in ({"captured_at_utc": (datetime.fromisoformat(first["captured_at_utc"]) - timedelta(seconds=1)).isoformat()},
                       {"coverage": "Changed bounded coverage"},
                       {"entries": [{**first["entries"][0], "name": "Changed name"}, *first["entries"][1:]]},
                       {"entries": [{**first["entries"][0], "completed": False, "state": "not_completed"}, *first["entries"][1:]],
                        "completed_count": 0, "not_completed_count": 2}):
            client, _, outputs = prepared([event(), event(ident=51)])
            values = iter((first, {**first, **fields}))
            with self.subTest(fields=fields), patch.object(companion, "emit"), self.assertRaises(RuntimeError):
                client.ask(request(), progression_query=lambda _: next(values))
            self.assertTrue(outputs[0]["result"]["success"])
            self.assertFalse(outputs[1]["result"]["success"])
        client, _, outputs = prepared([event(), event(ident=51, category="trust"), *completed()])
        with patch.object(companion, "emit"):
            client.ask(request(), progression_query=lambda args: result(args["category"]))
        self.assertTrue(all(value["result"]["success"] for value in outputs))

    def test_result_category_mismatch_and_revocation_cancel_before_delivery(self):
        for query in (lambda _: result("trust"), lambda _: (_ for _ in ()).throw(PermissionError("Revoked"))):
            client, _, outputs = prepared([event()])
            with patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure):
                client.ask(request(), progression_query=query)
            self.assertFalse(outputs[0]["result"]["success"])
        client, _, outputs = prepared([event()])
        def cancelled(_):
            client.cancel_event.set()
            return result()
        with patch.object(companion, "emit"), self.assertRaises(RuntimeError):
            client.ask(request(), progression_query=cancelled)
        self.assertFalse(outputs[0]["result"]["success"])

    def test_shared_call_byte_budgets_and_argument_correction_are_preserved(self):
        value, reads = result(), []
        client, _, outputs = prepared([event(ident=50 + index) for index in range(13)])
        with patch.object(companion, "emit"), self.assertRaisesRegex(RuntimeError, "12-call budget"):
            client.ask(request(), progression_query=lambda args: reads.append(args) or value)
        self.assertEqual(len(reads), 12)
        self.assertFalse(outputs[-1]["result"]["success"])
        client, _, outputs = prepared([event(ids=[0]), event(ident=51), *completed()])
        with patch.object(companion, "emit"):
            client.ask(request(), progression_query=lambda _: value)
        self.assertFalse(outputs[0]["result"]["success"])
        self.assertTrue(outputs[1]["result"]["success"])
        client, _, outputs = prepared([event(), event(ident=51)])
        with patch.object(companion, "emit"), patch.object(companion, "MAX_LOOKUP_TOTAL_BYTES", encoded_size(value)), \
                self.assertRaisesRegex(RuntimeError, "byte result budget"):
            client.ask(request(), progression_query=lambda _: value)
        self.assertFalse(outputs[1]["result"]["success"])

    def test_pipe_correlates_kind_id_single_reply_and_normalizes_arguments(self):
        messages, responses, ready = [], [], threading.Event()
        def emit(value):
            messages.append(value)
            ready.set()
        bridge = QuestQueryBridge(threading.Event(), emit, timeout=2)
        worker = threading.Thread(target=lambda: responses.append(bridge.query({"category": "trust"}, "progression")))
        worker.start()
        self.assertTrue(ready.wait(2))
        ident = messages[0]["progression_query"]["id"]
        for invalid in ({"collection_result": {"id": ident, "result": {}}},
                        {"progression_result": {"id": ident + 1, "result": {}}},
                        {"progression_result": {"id": True, "result": {}}}):
            with self.assertRaises(RuntimeError):
                bridge.accept(invalid)
        bridge.accept({"progression_result": {"id": ident, "result": {"synthetic": True}}})
        worker.join(2)
        self.assertFalse(worker.is_alive())
        self.assertEqual(messages[0]["progression_query"]["arguments"], validate_arguments({"category": "trust"}))
        self.assertEqual(responses, [{"synthetic": True}])
        with self.assertRaises(RuntimeError):
            bridge.accept({"progression_result": {"id": ident, "result": {}}})

    def test_cli_wires_v25_callback_and_result_without_runtime(self):
        payload = request()
        reply = {"progression_result": {"id": 1, "result": {"synthetic": True}}}
        stream = io.StringIO(json.dumps({"request": payload}) + "\n" + json.dumps(reply) + "\n")
        stream.reconfigure = lambda **_: None
        with patch.object(companion.sys, "argv", ["companion.py", "bridge", "ask"]), \
                patch.object(companion.sys, "stdin", stream), \
                patch.object(companion.threading, "Thread", side_effect=lambda target, **_: SimpleNamespace(start=target)), \
                patch.object(companion, "Companion") as constructor, patch.object(companion, "emit"), \
                patch.object(companion.QuestQueryBridge, "accept") as accept, \
                patch.object(companion.QuestQueryBridge, "query", return_value="fixture") as query:
            self.assertEqual(companion.main(), 0)
            callback = constructor.return_value.ask.call_args.kwargs["progression_query"]
            self.assertEqual(callback({"category": "trust"}), "fixture")
        accept.assert_called_once_with(reply)
        query.assert_called_once_with({"category": "trust"}, "progression")
        self.assertEqual(constructor.return_value.ask.call_args.args, (payload,))

    def test_csharp_producer_survives_deferred_transport(self):
        path = Path(__file__).resolve().parents[2] / ".test-tmp" / "progression-producer.json"
        self.assertTrue(path.exists(), "Produce required fixtures with tests/Progression.Tests --produce-fixtures .test-tmp/progression-producer.json")
        fixtures = json.loads(path.read_text(encoding="utf-8"))
        self.assertGreaterEqual(len(fixtures), 8)
        categories, states = set(), set()
        unavailable_seen = page_seen = False
        for fixture in fixtures:
            with self.subTest(name=fixture["name"]):
                value, arguments = fixture["result"], fixture["arguments"]
                meta = {"scope": SCOPE, "categories": [arguments["category"]]}
                # These two producers intentionally carry short-lived observations. Freeze the validation
                # clock at their capture so rerunning offline fixtures later does not alter their meaning.
                fixed = datetime.fromisoformat(value["captured_at_utc"])
                class FixtureDatetime(datetime):
                    @classmethod
                    def now(cls, tz=None):
                        return fixed
                timed = value["readiness"] == "saved_menu_observations" and value["category"] in ("challenge_log", "achievement_progress")
                with patch.object(progression_lookup, "datetime", FixtureDatetime if timed else datetime):
                    validate_result(value, meta, arguments)
                self.assertLessEqual(encoded_size(value), MAX_RESULT_BYTES)
                categories.add(value["category"])
                states.update(entry["state"] for entry in value["entries"])
                unavailable_seen |= not value["available"]
                page_seen |= value["truncated"]
                client, _, outputs = prepared([event(**arguments), *completed()])
                with patch.object(companion, "emit"), patch.object(progression_lookup, "datetime", FixtureDatetime if timed else datetime):
                    client.ask(current_request([arguments["category"]]), progression_query=lambda _, data=value: deepcopy(data))
                self.assertEqual(json.loads(outputs[0]["result"]["contentItems"][0]["text"]), value)
        self.assertEqual(categories, {*V25_CATEGORIES, "hunting_log", "challenge_log", "achievement_progress"})
        self.assertEqual(states, set(STATE_COUNTS))
        self.assertTrue(unavailable_seen)
        self.assertTrue(page_seen)

    def test_gathering_log_csharp_producer_survives_deferred_transport(self):
        path = Path(__file__).resolve().parents[2] / ".test-tmp" / "gathering-log-producer.json"
        self.assertTrue(path.exists(), "Produce required .test-tmp/gathering-log-producer.json with GatheringLog.Tests")
        fixtures = json.loads(path.read_text(encoding="utf-8"))
        self.assertGreaterEqual(len(fixtures), 4)
        meta = {"scope": SCOPE, "categories": ["gathering_log"]}
        states, unavailable_seen, page_seen = set(), False, False
        for fixture in fixtures:
            with self.subTest(name=fixture["name"]):
                value, arguments = fixture["result"], fixture["arguments"]
                self.assertEqual(arguments["category"], "gathering_log")
                validate_result(value, meta, arguments)
                self.assertLessEqual(encoded_size(value), MAX_RESULT_BYTES)
                states.update(entry["state"] for entry in value["entries"])
                unavailable_seen |= not value["available"]
                page_seen |= value["truncated"]
                client, _, outputs = prepared([event(**arguments), *completed()])
                with patch.object(companion, "emit"):
                    client.ask(modern_request(["gathering_log"]), progression_query=lambda _, data=value: deepcopy(data))
                self.assertEqual(json.loads(outputs[0]["result"]["contentItems"][0]["text"]), value)
        self.assertEqual(states, {"completed", "not_observed_complete", "unknown"})
        self.assertTrue(unavailable_seen)
        self.assertTrue(page_seen)

    def test_achievement_progress_csharp_producer_survives_deferred_transport(self):
        path = Path(__file__).resolve().parents[2] / ".test-tmp" / "achievement-progress-producer.json"
        self.assertTrue(path.exists(), "Produce required .test-tmp/achievement-progress-producer.json with AchievementProgress.Tests")
        fixtures = json.loads(path.read_text(encoding="utf-8"))
        self.assertGreaterEqual(len(fixtures), 5)
        meta = {"scope": SCOPE, "categories": ["achievement_progress"]}
        unavailable_seen = miss_seen = overflow_seen = False
        for fixture in fixtures:
            with self.subTest(name=fixture["name"]):
                value, arguments = fixture["result"], fixture["arguments"]
                self.assertEqual(arguments["category"], "achievement_progress")
                validate_result(value, meta, arguments)
                unavailable_seen |= not value["available"]
                miss_seen |= value["available"] and not value["entries"] and bool(arguments.get("ids") or arguments.get("query"))
                overflow_seen |= any(entry["progress"] > entry["required"] for entry in value["entries"])
                client, _, outputs = prepared([event(**arguments), *completed()])
                with patch.object(companion, "emit"):
                    client.ask(latest_request(["achievement_progress"]), progression_query=lambda _, data=value: deepcopy(data))
                self.assertEqual(json.loads(outputs[0]["result"]["contentItems"][0]["text"]), value)
        self.assertTrue(unavailable_seen)
        self.assertTrue(miss_seen)
        self.assertTrue(overflow_seen)

    def test_fishing_requires_v28_explicit_categories_and_preserves_v27(self):
        for version, categories in ((25, V25_CATEGORIES), (26, V26_CATEGORIES), (27, V27_CATEGORIES)):
            self.assertEqual(validate_summary({"scope": SCOPE, "categories": list(categories)}, version=version)["categories"],
                             list(categories))
            for category in ("fishing_log", "spearfishing_log"):
                wire = wire_request(version)
                wire["progression_lookup"] = {"scope": SCOPE, "categories": [category]}
                with self.subTest(version=version, category=category), self.assertRaises(RuntimeError):
                    normalize_request(wire)
                with self.assertRaises(RuntimeError):
                    validate_summary(wire["progression_lookup"], version=version)
        for category in ("fishing_log", "spearfishing_log"):
            wire = current_request([category])
            approved = normalize_request(wire)
            registry = build_registry(approved, {"progression": lambda _: None}, None, companion.CompanionFailure)
            self.assertEqual(set(registry), {"query_progression"})
            schema = registry["query_progression"].definition["inputSchema"]["properties"]
            self.assertEqual(schema["category"]["enum"], [category])
            self.assertEqual(set(schema["state"]["enum"]), {"all", "completed", "not_observed_complete", "unknown"})
            self.assertIsNone(approved.crafting_log_lookup)
            wire["progression_lookup"]["categories"] = ["trust"]
            with self.assertRaises(RuntimeError):
                recheck_request(wire, approved)

    def test_fishing_permission_gate_precedes_callback_and_unknowns_stay_null(self):
        for category in ("fishing_log", "spearfishing_log"):
            meta = {"scope": SCOPE, "categories": [category]}
            value = result(category)
            validate_result(value, meta, {"category": category})
            self.assertIs(value["entries"][0]["completed"], True)
            self.assertIsNone(value["entries"][1]["completed"])
            for fields in ({"completed": False}, {"state": "not_completed"}, {"rank": 1}, {"item_id": 99}):
                bad = deepcopy(value)
                bad["entries"][1].update(fields)
                with self.subTest(category=category, fields=fields), self.assertRaises(RuntimeError):
                    validate_result(bad, meta, {"category": category})
            for wire in (latest_request(), current_request(["gathering_log"])):
                client, _, outputs = prepared([event(category=category)])
                with patch.object(progression_lookup, "permitted_tool", return_value=deepcopy(TOOL)), \
                        patch.object(progression_lookup, "validate_arguments", side_effect=lambda args, *_: args), \
                        patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure) as failure:
                    client.ask(wire, progression_query=lambda _: self.fail("Unpermitted fishing callback"))
                self.assertEqual(failure.exception.kind, "permission")
                self.assertFalse(outputs[0]["result"]["success"])
            self.assertIn("RowIds", permitted_instructions(meta))
            self.assertIn("native false", permitted_instructions(meta))

    def test_fishing_csharp_producer_survives_deferred_transport(self):
        path = Path(__file__).resolve().parents[2] / ".test-tmp" / "fishing-log-producer.json"
        self.assertTrue(path.exists(), "Produce required .test-tmp/fishing-log-producer.json with FishingLog.Tests")
        fixtures = json.loads(path.read_text(encoding="utf-8"))
        self.assertGreaterEqual(len(fixtures), 8)
        categories, states, unavailable_seen, page_seen = set(), set(), False, False
        for fixture in fixtures:
            with self.subTest(name=fixture["name"]):
                value, arguments = fixture["result"], fixture["arguments"]
                category = arguments["category"]
                meta = {"scope": SCOPE, "categories": [category]}
                validate_result(value, meta, arguments)
                categories.add(category)
                states.update(entry["state"] for entry in value["entries"])
                unavailable_seen |= not value["available"]
                page_seen |= value["truncated"]
                client, _, outputs = prepared([event(**arguments), *completed()])
                with patch.object(companion, "emit"):
                    client.ask(current_request([category]), progression_query=lambda _, data=value: deepcopy(data))
                self.assertEqual(json.loads(outputs[0]["result"]["contentItems"][0]["text"]), value)
        self.assertEqual(categories, {"fishing_log", "spearfishing_log"})
        self.assertEqual(states, {"completed", "not_observed_complete", "unknown"})
        self.assertTrue(unavailable_seen and page_seen)


if __name__ == "__main__":
    unittest.main()
