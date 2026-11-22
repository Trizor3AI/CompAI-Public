"""Offline wire43 resource contracts and explicit legacy compatibility using the C# producer."""
from copy import deepcopy
from datetime import datetime, timedelta
import json
from pathlib import Path
import unittest
from unittest.mock import patch

import activity_lookup as activities


PRODUCER = Path(__file__).resolve().parents[2] / ".test-tmp/resource-activity-producer.json"
CASES = {"materials_all", "materials_zero", "materials_empty", "materials_unavailable",
         "token_zero", "token_maximum", "missions_all", "missions_filtered", "missions_page", "missions_empty", "missions_unavailable"}


def summary(*categories):
    return {"scope": activities.SCOPE, "categories": list(categories)}


class ResourceActivityChecks(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not PRODUCER.is_file():
            raise RuntimeError("Required C# producer missing: run ActivityCache.Tests -- --produce-resource-fixtures "
                               ".test-tmp/resource-activity-producer.json first")
        cls.fixtures = json.loads(PRODUCER.read_text(encoding="utf-8-sig"))
        if not isinstance(cls.fixtures, dict) or set(cls.fixtures) != CASES:
            raise RuntimeError("Resource producer must contain all eleven expected C# cases, including token zero and maximum")
        # Preserve producer timestamps and bytes; a fixed clock makes TTL boundary checks deterministic.
        cls.now = max(datetime.fromisoformat(row["result"]["captured_at_utc"])
                      for row in cls.fixtures.values()) + timedelta(seconds=1)

    def fixture(self, name):
        return deepcopy(self.fixtures[name])

    def clock(self):
        now = self.now

        class FixedDateTime(datetime):
            @classmethod
            def now(cls, tz=None):
                return now.astimezone(tz) if tz is not None else now.replace(tzinfo=None)

        return patch.object(activities, "datetime", FixedDateTime)

    def validate(self, fixture, *, version=43, grant=None):
        with self.clock():
            return activities.validate_result(fixture["result"],
                summary(fixture["arguments"]["category"]) if grant is None else grant,
                fixture["arguments"], version=version)

    def legacy(self, fixture):
        result = deepcopy(fixture)
        category = result["arguments"]["category"]
        result["result"]["source"] = activities.LEGACY_V42_SOURCES[category]
        suffix = activities.HISTORICAL_COVERAGE if result["result"]["readiness"] == "last_observed_menu" else ""
        result["result"]["coverage"] = activities.LEGACY_V42_COVERAGE[category] + activities.COMMON_COVERAGE + suffix
        return result

    def reject_row_changes(self, name, changes):
        for change in changes:
            bad = self.fixture(name)
            bad["result"]["entries"][0].update(change)
            with self.subTest(name=name, change=change), self.assertRaises(RuntimeError):
                self.validate(bad)

    def test_all_csharp_cases_roundtrip_without_projection_loss_or_mutation(self):
        for name in sorted(CASES):
            fixture = self.fixture(name)
            before = deepcopy(fixture)
            with self.subTest(name=name):
                validated = self.validate(fixture)
                self.assertEqual(validated, before["result"])
                projected = activities.model_result(validated)
                restored = deepcopy(projected)
                defaults = restored.pop("entry_defaults", {})
                self.assertTrue(all(value is None for value in defaults.values()))
                restored["entries"] = [{**defaults, **row} for row in restored["entries"]]
                self.assertEqual(restored, before["result"])
                if projected["entries"]:
                    projected["entries"][0]["name"] = "Mutated model copy"
                self.assertEqual(fixture, before)

    def test_wire_and_permission_gates_apply_before_data_or_tool_use(self):
        from companion_architecture_check import wire_request
        from companion_provider import CompanionFailure
        from companion_request import normalize_request
        from companion_tools import build_registry

        for category in activities.RESOURCE_CATEGORIES:
            request = wire_request(43)
            request.update(activity_lookup=summary(category), web_search=False)
            callbacks = {"activities": lambda _: self.fail("Discovery or denied arguments queried private data")}
            registry = build_registry(normalize_request(request), callbacks, None, CompanionFailure)
            self.assertEqual(set(registry), {"query_activities"})
            tool = activities.permitted_tool(summary(category), version=43)
            self.assertEqual(tool["inputSchema"]["properties"]["category"]["enum"], [category])
            for unrelated in set(activities.SOURCES) - {category}:
                with self.subTest(category=category, unrelated=unrelated), self.assertRaises(RuntimeError):
                    registry["query_activities"].validate_arguments({"category": unrelated})
            denied = deepcopy(request)
            denied["activity_lookup"] = None
            self.assertNotIn("query_activities", build_registry(normalize_request(denied), {}, None, CompanionFailure))
            for version in range(33, 40):
                old = deepcopy(request)
                old["version"] = version
                with self.subTest(category=category, version=version), self.assertRaises(RuntimeError):
                    normalize_request(old)
        for name in CASES:
            fixture = self.fixture(name)
            for version in range(33, 40):
                with self.subTest(name=name, version=version), self.assertRaises(RuntimeError):
                    self.validate(fixture, version=version)
            for category in ("island_currencies", "island_progress", "cosmic_research"):
                with self.subTest(name=name, grant=category), self.assertRaises(RuntimeError):
                    self.validate(fixture, grant=summary(category))

    def test_legacy_resource_contracts_require_explicit_conversion_and_exclude_tokens(self):
        for name in sorted(CASES):
            original = self.fixture(name)
            before = deepcopy(original)
            legacy = self.legacy(original)
            for version in (40, 41, 42):
                with self.subTest(name=name, version=version):
                    with self.assertRaises(RuntimeError): self.validate(original, version=version)
                    if name.startswith("token_"):
                        with self.assertRaises(RuntimeError): self.validate(legacy, version=version)
                    else:
                        self.assertEqual(self.validate(legacy, version=version), legacy["result"])
            with self.subTest(name=name), self.assertRaises(RuntimeError): self.validate(legacy)
            self.assertEqual(original, before)

    def test_observed_zero_empty_success_and_unavailable_remain_distinct(self):
        zero = self.validate(self.fixture("materials_zero"))
        self.assertTrue(zero["available"])
        self.assertEqual([(row["value"], row["unit"]) for row in zero["entries"]], [(0, "items")])
        for prefix in ("materials", "missions"):
            empty = self.validate(self.fixture(prefix + "_empty"))
            unavailable = self.validate(self.fixture(prefix + "_unavailable"))
            self.assertEqual((empty["available"], empty["entry_count"], empty["entries"]), (True, 0, []))
            self.assertEqual((unavailable["available"], unavailable["entry_count"], unavailable["entries"]), (False, None, []))
            self.assertIsNone(empty["unavailable_reason"])
            self.assertTrue(unavailable["unavailable_reason"])
            for change in ({"entry_count": 0}, {"readiness": "last_observed_menu"}, {"unavailable_reason": None}):
                bad = self.fixture(prefix + "_unavailable")
                bad["result"].update(change)
                with self.subTest(prefix=prefix, change=change), self.assertRaises(RuntimeError):
                    self.validate(bad)
        self.assertIn("empty results do not prove zero stock", activities.CATEGORY_INSTRUCTIONS["island_materials"])
        self.assertIn("Felicitous Token", activities.CATEGORY_INSTRUCTIONS["island_materials"])
        mission_guidance = activities.CATEGORY_INSTRUCTIONS["cosmic_missions"]
        self.assertIn("missing medals and full completion remain unknown", mission_guidance)
        self.assertIn("not that no medals were earned", activities.COVERAGE["cosmic_missions"])
        self.assertRegex(mission_guidance, r"Gold does not (?:independently )?establish a separately earned silver medal")

    def test_material_rows_bound_categories_quantities_and_reference_metadata(self):
        changes = [{"group": f"pouch_category_{value}"} for value in (0, 2, 8)]
        changes += [{"value": value} for value in (-1, 1000, True, 1.5, None)]
        changes += [{"required": 999}, {"slot": 1}, {"unit": "cowries"}, {"text": None},
                    {"state": "owned"}, {"reference_kind": "cosmic_mission"},
                    {"reference_id": 0}, {"reference_id": 4294967296}, {"owner": "current character"}]
        self.reject_row_changes("materials_all", changes)
        for category in (1, 3, 4, 5, 6, 7):
            for quantity in (0, 999):
                valid = self.fixture("materials_all")
                valid["result"]["entries"][0].update(group=f"pouch_category_{category}", value=quantity,
                    name="Feuille synthétique", text="Matériaux observés")
                self.assertEqual(self.validate(valid), valid["result"])

    def test_felicitous_token_requires_exact_identity_label_and_cap(self):
        for name, quantity in (("token_zero", 0), ("token_maximum", 150)):
            token = self.validate(self.fixture(name))["entries"]
            self.assertEqual(len(token), 1)
            self.assertEqual({key: token[0][key] for key in ("id", "group", "reference_id", "text", "value")},
                             {"id": 109, "group": "pouch_category_10", "reference_id": 41668, "text": "Tokens", "value": quantity})
        self.reject_row_changes("token_maximum", [{"value": value} for value in (-1, 151, 999, True, None)] +
                                [{"id": 108}, {"reference_id": 41669}, {"text": ""}, {"text": "Unverified tokens"},
                                 {"reference_kind": "currency"}, {"group": "pouch_category_11"}])
        for category in (1, 3, 4, 5, 6, 7):
            for quantity in (0, 150, 151, 999):
                forged = self.fixture("token_maximum")
                forged["result"]["entries"][0].update(group=f"pouch_category_{category}", value=quantity)
                for version in (40, 41, 42, 43):
                    candidate = forged if version == 43 else self.legacy(forged)
                    with self.subTest(category=category, quantity=quantity, version=version), self.assertRaises(RuntimeError):
                        self.validate(candidate, version=version)
            # Reserve both public identities independently, even when only one
            # token identifier is smuggled into an otherwise valid resource row.
            # Use a single filtered row so ordering cannot cause a false pass.
            for identity in ({"id": 109}, {"reference_id": 41668}):
                forged = self.fixture("materials_zero")
                forged["result"]["entries"][0].update(group=f"pouch_category_{category}", value=151, **identity)
                for version in (40, 41, 42, 43):
                    candidate = forged if version == 43 else self.legacy(forged)
                    with self.subTest(category=category, identity=identity, version=version), self.assertRaises(RuntimeError):
                        self.validate(candidate, version=version)

    def test_legacy_resource_guidance_does_not_advertise_new_reader_features(self):
        for granted in (("island_materials",), ("cosmic_missions",), activities.RESOURCE_CATEGORIES):
            for version in (40, 41, 42):
                instructions = activities.permitted_instructions(summary(*granted), version=version)
                for category in granted:
                    with self.subTest(granted=granted, category=category, version=version):
                        self.assertIn(activities.LEGACY_V42_INSTRUCTIONS[category], instructions)
                        self.assertNotIn(activities.CATEGORY_INSTRUCTIONS[category], instructions)
                for unsupported in ("Felicitous Token", "512", "across browsed"):
                    self.assertNotIn(unsupported, instructions)
        for category in activities.RESOURCE_CATEGORIES:
            instructions = activities.permitted_instructions(summary(category), version=43)
            self.assertIn(activities.CATEGORY_INSTRUCTIONS[category], instructions)
            self.assertNotIn(activities.LEGACY_V42_INSTRUCTIONS[category], instructions)

    def test_mission_rows_allow_only_positive_medals_and_matching_job_groups(self):
        changes = [{"text": medal} for medal in ("Bronze", "gold", "Gold and Silver", "None", None)]
        changes += [{"slot": job} for job in (7, 19, None, True, 8)]
        changes += [{"group": "class_job_8"}, {"group": "planet_oizys"}, {"id": 513},
                    {"required": 1}, {"value": 0}, {"unit": "medals"}, {"state": "completed"},
                    {"reference_kind": "item"}, {"reference_id": 0}, {"reference_id": 65536}, {"planet": "Oizys"}]
        self.reject_row_changes("missions_all", changes)
        for job in (8, 18):
            for medal in ("Silver", "Gold"):
                valid = self.fixture("missions_all")
                valid["result"]["entries"][0].update(slot=job, group=f"class_job_{job}", text=medal)
                self.assertEqual(self.validate(valid), valid["result"])

    def test_duplicate_rows_references_and_forged_result_metadata_are_rejected(self):
        for name in ("materials_all", "missions_all"):
            for field in ("id", "reference_id"):
                bad = self.fixture(name)
                bad["result"]["entries"][1][field] = bad["result"]["entries"][0][field]
                if name == "missions_all" and field == "reference_id":
                    bad["result"]["entries"][1].update(slot=bad["result"]["entries"][0]["slot"],
                                                       group=bad["result"]["entries"][0]["group"])
                with self.subTest(name=name, duplicate=field), self.assertRaises(RuntimeError):
                    self.validate(bad)
            for field in activities.ROW_FIELDS:
                bad = self.fixture(name)
                del bad["result"]["entries"][0][field]
                with self.subTest(name=name, missing=field), self.assertRaises(RuntimeError):
                    self.validate(bad)
            for field, value in (("source", "Unverified source"), ("coverage", "Complete owned inventory and medals"),
                                 ("readiness", "loaded"), ("entry_count", 513), ("planet", "Current zone")):
                bad = self.fixture(name)
                bad["result"][field] = value
                with self.subTest(name=name, field=field), self.assertRaises(RuntimeError):
                    self.validate(bad)

    def test_accumulated_missions_up_to_512_require_wire43_and_bounded_paging(self):
        for total in (128, 129, 512):
            page = self.fixture("missions_page")
            page["arguments"].update(offset=total - 1, limit=1)
            page["result"].update(entry_count=total, matched_count=total, offset=total - 1,
                                   limit=1, next_offset=None, truncated=False)
            page["result"]["entries"][0].update(id=total, reference_id=1000 + total)
            with self.subTest(total=total):
                self.assertEqual(self.validate(page), page["result"])
                for version in (40, 41, 42):
                    legacy = self.legacy(page)
                    if total <= 128:
                        self.assertEqual(self.validate(legacy, version=version), legacy["result"])
                    else:
                        with self.assertRaises(RuntimeError): self.validate(legacy, version=version)
        for field in ("entry_count", "matched_count"):
            invalid = deepcopy(page)
            invalid["result"][field] = 513
            with self.subTest(field=field), self.assertRaises(RuntimeError): self.validate(invalid)
        invalid = deepcopy(page)
        invalid["result"]["entries"][0]["id"] = 513
        with self.assertRaises(RuntimeError): self.validate(invalid)

    def test_mission_identity_is_reference_and_job_only_on_wire43(self):
        repeated_reference = self.fixture("missions_all")
        first, second = repeated_reference["result"]["entries"]
        self.assertNotEqual(first["slot"], second["slot"])
        second["reference_id"] = first["reference_id"]
        self.assertEqual(self.validate(repeated_reference), repeated_reference["result"])
        for version in (40, 41, 42):
            with self.subTest(version=version), self.assertRaises(RuntimeError):
                self.validate(self.legacy(repeated_reference), version=version)
        second.update(slot=first["slot"], group=first["group"])
        with self.assertRaises(RuntimeError): self.validate(repeated_reference)

    def test_filters_and_pages_preserve_sparse_ids_and_partial_coverage(self):
        for name in ("materials_all", "missions_all"):
            original = self.fixture(name)
            rows = original["result"]["entries"]
            for filters in ({"group": rows[1]["group"]}, {"ids": [rows[1]["id"]]},
                            {"query": rows[1]["name"].upper()}, {"query": "No such observed entry"}):
                filtered = deepcopy(original)
                filtered["arguments"].update(filters)
                selected = [row for row in rows if
                            (not filters.get("group") or row["group"] == filters["group"]) and
                            (not filters.get("ids") or row["id"] in filters["ids"]) and
                            all(term in row["name"].casefold() or term in (row["text"] or "").casefold()
                                for term in filters.get("query", "").casefold().split())]
                filtered["result"].update(entries=selected, matched_count=len(selected))
                self.assertEqual(self.validate(filtered), filtered["result"])
                self.assertEqual(filtered["result"]["entry_count"], 2)
                bad = deepcopy(original)
                bad["arguments"].update(filters)
                with self.subTest(name=name, filters=filters), self.assertRaises(RuntimeError): self.validate(bad)
            for offset in (0, 1):
                page = deepcopy(original)
                page["arguments"].update(offset=offset, limit=1)
                page["result"].update(entries=rows[offset:offset + 1], offset=offset, limit=1,
                    next_offset=1 if offset == 0 else None, truncated=offset == 0)
                self.assertEqual(self.validate(page), page["result"])
            for field, value in (("next_offset", 1), ("offset", 1), ("limit", 49), ("truncated", True), ("matched_count", 3)):
                bad = deepcopy(original)
                bad["result"][field] = value
                with self.subTest(name=name, field=field), self.assertRaises(RuntimeError): self.validate(bad)
        skipped = self.fixture("missions_page")
        skipped["result"]["entries"][0] = self.fixture("missions_all")["result"]["entries"][1]
        with self.assertRaises(RuntimeError): self.validate(skipped)

    def test_retained_observations_expire_at_fifteen_minutes_and_reject_future_time(self):
        for name in ("materials_all", "missions_all", "materials_empty", "missions_empty", "token_zero", "token_maximum"):
            recent = self.fixture(name)
            recent["result"]["captured_at_utc"] = (self.now - timedelta(minutes=14, seconds=59)).isoformat()
            self.assertEqual(self.validate(recent), recent["result"])
            for age in (timedelta(minutes=15), timedelta(days=1), timedelta(seconds=-1)):
                expired = deepcopy(recent)
                expired["result"]["captured_at_utc"] = (self.now - age).isoformat()
                with self.subTest(name=name, age=age), self.assertRaises(RuntimeError): self.validate(expired)

    def test_csharp_results_traverse_actual_deferred_mock_without_unrelated_access(self):
        import companion
        import deferred_fixture
        from companion_architecture_check import wire_request
        from quest_lookup_check import completed, tool_message

        for name in sorted(CASES):
            fixture = self.fixture(name)
            category = fixture["arguments"]["category"]
            request = wire_request(43)
            request.update(activity_lookup=summary(category), web_search=False)
            client, calls = deferred_fixture.client([tool_message(tool="query_activities", arguments=fixture["arguments"]), *completed()])
            client.model, client.effort, client.web_search = request["model"], request["reasoning_effort"], False
            replies, queries = [], []
            client.send = replies.append

            def query(arguments):
                queries.append(arguments)
                return deepcopy(fixture["result"])

            with self.subTest(name=name), self.clock(), patch.object(companion, "emit") as emitted, patch.object(companion, "GameDataTools") as public:
                client.ask(request, activity_query=query)
            self.assertEqual(queries, [activities.validate_arguments(fixture["arguments"])])
            self.assertTrue(replies[0]["result"]["success"])
            self.assertEqual(json.loads(replies[0]["result"]["contentItems"][0]["text"]), activities.model_result(fixture["result"]))
            public.assert_not_called()
            self.assertTrue(any(call.args[0].get("question_answer") == "Observed answer." for call in emitted.call_args_list))
            self.assertNotIn("turn/interrupt", [method for method, _ in calls])
            deferred_fixture.assert_exposed(self, client, calls, [activities.permitted_tool(summary(category), version=43)])


if __name__ == "__main__":
    unittest.main()
