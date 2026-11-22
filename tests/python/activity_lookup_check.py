"""Offline activity contracts, permission boundaries and actual C# producer roundtrips."""
from copy import deepcopy
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import threading
import unittest
from unittest.mock import patch

import activity_lookup as activities


ROOT = Path(__file__).resolve().parents[2]
PRODUCER = ROOT / ".test-tmp/activity-results.json"
FC_PRODUCER = ROOT / ".test-tmp/pvp-fc-results.json"
MOG_PRODUCER = ROOT / ".test-tmp/mogpendium-results.json"
MOG_V35_PRODUCER = Path(__file__).with_name("fixtures") / "mogpendium-wire35.json"
ISLAND_PRODUCER = ROOT / ".test-tmp/island-activity-producer.json"
RESEARCH_PRODUCER = ROOT / ".test-tmp/research-progress-producer.json"


def summary(*categories):
    return {"scope": activities.SCOPE, "categories": list(categories)}


def fixtures():
    if not PRODUCER.is_file():
        raise RuntimeError("Run Activities.Tests -- --produce-fixtures .test-tmp/activity-results.json first")
    values = json.loads(PRODUCER.read_text(encoding="utf-8-sig"))
    for value in values:
        value["result"]["captured_at_utc"] = datetime.now(timezone.utc).isoformat()
    return values


def one(category):
    return next(row for row in fixtures() if row["result"]["category"] == category)


def fc_fixture(name="free_company_details"):
    if not FC_PRODUCER.is_file():
        raise RuntimeError("Run PvpFc.Tests -- --produce-fixtures .test-tmp/pvp-fc-results.json first")
    values = json.loads(FC_PRODUCER.read_text(encoding="utf-8-sig"))
    value = next(row for row in values if row["name"] == name)
    value["result"]["captured_at_utc"] = datetime.now(timezone.utc).isoformat()
    return value


def mog_fixtures():
    if not MOG_PRODUCER.is_file():
        raise RuntimeError("Run Mogpendium.Tests -- --produce-fixtures .test-tmp/mogpendium-results.json first")
    values = json.loads(MOG_PRODUCER.read_text(encoding="utf-8-sig"))
    for value in values:
        value["result"]["captured_at_utc"] = datetime.now(timezone.utc).isoformat()
    return values


def mog_fixture(category, *, has_detail=True, has_list=None):
    return next(row for row in mog_fixtures() if row["result"]["category"] == category and row["result"]["available"] and
                row["result"]["next_offset"] is None and row["arguments"].get("offset", 0) == 0 and
                not any(row["arguments"].get(key) for key in ("query", "group", "ids")) and
                (any(entry["id"] == 10 for entry in row["result"]["entries"]) == has_detail) and
                (has_list is None or any(entry["group"] == "observed_list" for entry in row["result"]["entries"]) == has_list))


def mog_v35_fixtures():
    values = json.loads(MOG_V35_PRODUCER.read_text(encoding="utf-8"))
    for value in values:
        value["result"]["captured_at_utc"] = datetime.now(timezone.utc).isoformat()
        value["wire_version"] = 35
    return values


def validate(fixture, *, version=None):
    return activities.validate_result(fixture["result"], summary(fixture["arguments"]["category"]), fixture["arguments"], version=version)


class ActivityLookupChecks(unittest.TestCase):
    def test_model_projection_roundtrips_producer_results_without_mutation(self):
        for fixture in fixtures() + [fc_fixture(), fc_fixture("free_company_details_unknown")] + mog_fixtures() + mog_v35_fixtures():
            category = fixture["arguments"]["category"]
            version = fixture.get("wire_version", 37 if category == "mahjong" else 36 if category in activities.MOG_CATEGORIES else 34 if category == "free_company_details" else 33)
            with self.subTest(category=category, version=version, arguments=fixture["arguments"]):
                original = deepcopy(fixture["result"])
                validated = validate(fixture, version=version)
                projected = activities.model_result(validated)
                self.assertEqual(validated, original)
                self.assertIsNot(projected, validated)
                restored = deepcopy(projected)
                defaults = restored.pop("entry_defaults", {})
                self.assertTrue(all(value is None for value in defaults.values()))
                restored["entries"] = [{**defaults, **row} for row in restored["entries"]]
                self.assertEqual(restored, original)
                self.assertEqual({key: value for key, value in projected.items() if key not in ("entries", "entry_defaults")},
                                 {key: value for key, value in original.items() if key != "entries"})
                if projected["entries"]:
                    projected["entries"][0]["name"] = "Changed projected row"
                    self.assertEqual(validated, original)
                else:
                    self.assertNotIn("entry_defaults", projected)

    def test_model_projection_omits_only_explicit_null_entry_values(self):
        fixture = fc_fixture("free_company")
        for entry in fixture["result"]["entries"]:
            if entry["id"] in (3, 4):
                entry["value"] = 0
        validated = validate(fixture, version=33)
        projected = activities.model_result(validated)
        self.assertIs(projected["truncated"], False)
        self.assertIsNone(projected["next_offset"])
        self.assertIsNone(projected["unavailable_reason"])
        self.assertEqual(projected["entries"][2]["value"], 0)
        self.assertIs(type(projected["entries"][2]["value"]), int)
        self.assertNotIn("text", projected["entries"][2])
        self.assertIsNone(projected["entry_defaults"]["text"])
        for field in activities.NULL_ENTRY_FIELDS:
            broken = deepcopy(fixture)
            del broken["result"]["entries"][0][field]
            with self.subTest(missing=field), self.assertRaises(RuntimeError):
                validate(broken, version=33)
        broken = deepcopy(fixture)
        broken["result"]["entries"][2]["value"] = False
        with self.assertRaises(RuntimeError):
            validate(broken, version=33)

    def test_model_projection_does_not_inflate_a_small_filtered_result(self):
        fixture = fc_fixture("free_company")
        fixture["arguments"]["ids"] = [1]
        fixture["result"].update(entries=fixture["result"]["entries"][:1], matched_count=1)
        validated = validate(fixture, version=33)
        projected = activities.model_result(validated)
        self.assertEqual(projected, validated)
        self.assertNotIn("entry_defaults", projected)
        projected["entries"][0]["name"] = "Changed projected row"
        self.assertNotEqual(projected, validated)

    def test_full_mogpendium_list_guidance_requires_wire36_list_category(self):
        for version in (35, 36, 37, 38):
            for category in activities.MOG_CATEGORIES:
                instructions = activities.permitted_instructions(summary(category), version=version)
                with self.subTest(version=version, category=category):
                    self.assertEqual(activities.MOG_LIST_QUERY_INSTRUCTIONS in instructions,
                                     version in (36, 37, 38) and category in activities.MOG_LIST_CATEGORIES)
        instructions = activities.permitted_instructions(summary("mogpendium_weekly"), version=36)
        self.assertIn("For targeted questions", instructions)
        self.assertIn("does not establish a current event", instructions)
        self.assertIn("may be historical", instructions)
        self.assertNotIn(activities.MOG_LIST_QUERY_INSTRUCTIONS,
                         activities.permitted_instructions(summary("free_company"), version=33))

    def test_wire35_selected_details_keep_exact_contract_and_cannot_accept_list_rows(self):
        import companion
        import deferred_fixture
        from companion_architecture_check import wire_request
        from companion_request import normalize_request
        from companion_provider import CompanionFailure
        from companion_tools import build_registry
        from quest_lookup_check import tool_message

        for fixture in mog_v35_fixtures():
            category = fixture["result"]["category"]
            self.assertEqual(validate(fixture, version=35), fixture["result"])
            request = wire_request(35)
            request.update(activity_lookup=summary(category), web_search=False)
            registry = build_registry(normalize_request(request), {"activities": lambda _: {}}, None, CompanionFailure)
            registration = registry["query_activities"]
            self.assertEqual(registration.validate_result(fixture["result"], summary(category), fixture["arguments"]), fixture["result"])
            self.assertIn(activities.MOG_V35_INSTRUCTIONS[category], registration.instructions)
            self.assertNotIn(activities.CATEGORY_INSTRUCTIONS[category], registration.instructions)
            self.assertNotIn("list/detail", registration.definition["description"])

            # Spoof every metadata field as legacy and return just one filtered list row.
            # This isolates the version-bound row guard from coverage/count differences.
            current = mog_fixture(category, has_list=True)
            row = deepcopy(next(row for row in current["result"]["entries"] if row["group"] == "observed_list"))
            forged = deepcopy(fixture)
            forged["arguments"]["ids"] = [row["id"]]
            forged["result"].update(entries=[row], matched_count=1)
            with self.subTest(category=category), self.assertRaises(RuntimeError):
                registration.validate_result(forged["result"], summary(category), forged["arguments"])
            client, calls = deferred_fixture.client([tool_message(tool="query_activities", arguments=forged["arguments"])])
            client.model, client.effort, client.web_search = request["model"], request["reasoning_effort"], False
            replies = []
            client.send = replies.append
            with patch.object(companion, "emit") as emitted, self.assertRaises(CompanionFailure) as failure:
                client.ask(request, activity_query=lambda _: forged["result"])
            self.assertEqual(failure.exception.kind, "protocol")
            self.assertFalse(replies[-1]["result"]["success"])
            self.assertIn("turn/interrupt", [method for method, _ in calls])
            self.assertFalse(any("question_answer" in call.args[0] for call in emitted.call_args_list))

    def test_wire36_observed_list_detail_fallback_and_partial_queries(self):
        for category in activities.MOG_LIST_CATEGORIES:
            combined = mog_fixture(category, has_list=True)
            self.assertEqual(validate(combined, version=36), combined["result"])
            for keep_detail, keep_list in ((False, True), (True, False), (False, False)):
                fixture = deepcopy(combined)
                rows = [row for row in fixture["result"]["entries"] if
                        (keep_detail or row["id"] not in (10, 11, 12)) and (keep_list or row["group"] != "observed_list")]
                fixture["result"].update(entries=rows, entry_count=len(rows), matched_count=len(rows))
                if keep_detail or keep_list:
                    self.assertEqual(validate(fixture, version=36), fixture["result"])
                else:
                    with self.assertRaises(RuntimeError): validate(fixture, version=36)
            for missing in (1, 2, 3, 4, 10, 11):
                broken = deepcopy(combined)
                rows = [row for row in broken["result"]["entries"] if row["id"] != missing]
                broken["result"].update(entries=rows, entry_count=len(rows), matched_count=len(rows))
                with self.subTest(category=category, missing=missing), self.assertRaises(RuntimeError):
                    validate(broken, version=36)
            filtered = deepcopy(combined)
            filtered["arguments"]["group"] = "observed_list"
            filtered["result"]["entries"] = [row for row in filtered["result"]["entries"] if row["group"] == "observed_list"]
            filtered["result"]["matched_count"] = len(filtered["result"]["entries"])
            self.assertEqual(validate(filtered, version=36), filtered["result"])
            filtered["arguments"].update(group="", ids=[101])
            filtered["result"].update(entries=[row for row in filtered["result"]["entries"] if row["id"] == 101], matched_count=1)
            self.assertEqual(validate(filtered, version=36), filtered["result"])

    def test_observed_list_row_bounds_and_week_choice_structure(self):
        for category in activities.MOG_LIST_CATEGORIES:
            fixture = mog_fixture(category, has_list=True)
            for key, bad in (("name", "x" * 129), ("text", "x" * 129), ("text", "Week\n1"),
                             ("value", 1), ("unit", "items"), ("required", 1), ("slot", 3),
                             ("reference_kind", "item"), ("reference_id", 65536), ("state", "completed")):
                broken = deepcopy(fixture)
                next(row for row in broken["result"]["entries"] if row["id"] == 100)[key] = bad
                with self.subTest(category=category, key=key), self.assertRaises(RuntimeError): validate(broken, version=36)
            broken = deepcopy(fixture)
            next(row for row in broken["result"]["entries"] if row["id"] == 100).update(reference_id=None, reference_kind=None)
            if category == "mogpendium_minimog":
                self.assertEqual(validate(broken, version=36), broken["result"])
            else:
                with self.assertRaises(RuntimeError): validate(broken, version=36)
            broken = deepcopy(fixture)
            rows = [row for row in broken["result"]["entries"] if row["group"] == "observed_list"]
            if category == "mogpendium_weekly":
                rows[1]["text"] = rows[0]["text"]
            else:
                rows[1]["text"] = "Different displayed week"
            with self.assertRaises(RuntimeError): validate(broken, version=36)
            broken = deepcopy(fixture)
            next(row for row in broken["result"]["entries"] if row["id"] == 100)["id"] = 131
            broken["result"]["entries"].sort(key=lambda row: row["id"])
            with self.assertRaises(RuntimeError): validate(broken, version=36)
        mini = mog_fixture("mogpendium_minimog", has_list=True)
        last = max(row["id"] for row in mini["result"]["entries"] if row["group"] == "observed_list")
        mini["result"]["entries"] = [row for row in mini["result"]["entries"] if row["id"] != last]
        mini["result"]["entry_count"] -= 1
        mini["result"]["matched_count"] -= 1
        with self.assertRaises(RuntimeError): validate(mini, version=36)
        instructions = activities.permitted_instructions(summary("mogpendium_minimog"), version=36)
        for caveat in ("generic activity label", "hidden targets", "missing unlocks", "current/selected week", "unavailable, not empty"):
            self.assertIn(caveat, instructions)

    def test_csharp_mogpendium_producers_roundtrip(self):
        values = mog_fixtures()
        self.assertEqual({row["result"]["category"] for row in values if row["result"]["available"]}, set(activities.MOG_CATEGORIES))
        self.assertTrue(any(not row["result"]["available"] for row in values))
        for value in values:
            with self.subTest(name=value.get("name"), category=value["result"]["category"]):
                self.assertEqual(validate(value), value["result"])

    def test_mogpendium_wire36_registry_permission_and_old_version_rejection(self):
        from companion_architecture_check import wire_request
        from companion_request import normalize_request, recheck_request
        from companion_provider import CompanionFailure
        from companion_tools import build_registry, registry_instructions
        from deferred_tools import DeferredTools
        from collection_lookup import SCOPE as collection_scope

        for category in activities.MOG_CATEGORIES:
            request = wire_request(36)
            request.update(activity_lookup=summary(category), web_search=False)
            approved = normalize_request(request)
            registry = build_registry(approved, {"activities": lambda _: self.fail("Registry construction executed a query")}, None, CompanionFailure)
            self.assertEqual(set(registry), {"query_activities"})
            instructions = registry_instructions(registry)
            self.assertIn(activities.CATEGORY_INSTRUCTIONS[category], instructions)
            self.assertFalse(approved.public_data_allowed)
            for version in (33, 34):
                denied = deepcopy(request)
                denied["version"] = version
                with self.subTest(category=category, version=version), self.assertRaises(RuntimeError):
                    normalize_request(denied)
            denied = deepcopy(request)
            denied["activity_lookup"] = None
            with self.assertRaises(RuntimeError):
                recheck_request(denied, approved)
            empty = normalize_request(denied)
            self.assertEqual(build_registry(empty, {}, None, CompanionFailure), {})
        all_categories = wire_request(37)
        old_categories = [category for category in activities.SOURCES
                          if category not in (*activities.RESOURCE_CATEGORIES, *activities.RESEARCH_CATEGORIES, "island_currencies", "free_company_credits", "selected_macro")]
        all_categories.update(activity_lookup=summary(*old_categories), web_search=False)
        registry = build_registry(normalize_request(all_categories), {"activities": lambda _: {}}, None, CompanionFailure)
        deferred = DeferredTools(registry)
        for label in ("Mogpendium", "Standard", "Weekly", "Minimog", "Ultimog", "event"):
            self.assertIn(label, deferred.instructions)
        self.assertNotIn(activities.MOG_INSTRUCTIONS, deferred.instructions)
        self.assertIn(activities.MOG_INSTRUCTIONS, deferred.discover({"names": ["query_activities"]})["instructions"])

        # Legacy collection-tool access is availability only and cannot expose the new activity reader.
        for version in (33, 34):
            legacy = wire_request(version)
            legacy.update(collection_lookup={"scope": collection_scope, "categories": ["collection_tools"]}, web_search=False)
            registry = build_registry(normalize_request(legacy), {"collections": lambda _: {}}, None, CompanionFailure)
            self.assertEqual(set(registry), {"query_collections"})

    def test_mogpendium_row_bounds_reference_namespaces_and_unknown_states(self):
        fixture = mog_fixture("mogpendium_standard")
        self.assertIsNone(fixture["result"]["next_offset"], "Shape fixture must contain a whole result")
        mutations = ((1, "value", 0), (1, "value", 65536), (1, "unit", "week"),
                     (2, "text", "x" * 129), (3, "text", "Everything completed"),
                     (4, "reference_kind", "cs_bonus_content"), (4, "reference_id", 1000001),
                     (10, "reference_kind", "item"), (10, "reference_id", 65536),
                     (10, "text", "bad\ntext"), (10, "text", "\ud800"),
                     (11, "value", 0), (11, "value", 65536), (11, "unit", "claims"),
                     (100, "value", 256), (100, "text", "Current personal assignment"),
                     (100, "reference_kind", "item"), (100, "state", "completed"))
        for ident, key, bad in mutations:
            malformed = deepcopy(fixture)
            next(row for row in malformed["result"]["entries"] if row["id"] == ident)[key] = bad
            with self.subTest(ident=ident, key=key, bad=repr(bad)), self.assertRaises(RuntimeError):
                validate(malformed)
        varying = deepcopy(fixture)
        next(row for row in varying["result"]["entries"] if row["id"] == 100).update(value=None, unit=None)
        self.assertEqual(validate(varying), varying["result"])
        for category in activities.MOG_CATEGORIES[1:]:
            malformed = mog_fixture(category)
            catalog = deepcopy(next(row for row in fixture["result"]["entries"] if row["id"] == 100))
            malformed["arguments"]["ids"] = [100]
            malformed["result"].update(entries=[catalog], matched_count=1)
            with self.subTest(category=category), self.assertRaises(RuntimeError):
                validate(malformed)

    def test_mogpendium_whole_result_shape_does_not_reject_filtered_or_paged_metadata(self):
        fixture = mog_fixture("mogpendium_standard")
        self.assertIsNone(fixture["result"]["next_offset"])
        for missing in (1, 2, 3, 4, 10, 11):
            malformed = deepcopy(fixture)
            malformed["result"]["entries"] = [row for row in malformed["result"]["entries"] if row["id"] != missing]
            malformed["result"]["entry_count"] -= 1
            malformed["result"]["matched_count"] -= 1
            with self.subTest(missing=missing), self.assertRaises(RuntimeError):
                validate(malformed)
        for ids in ([1], [10, 11], [11], [100], [512]):
            filtered = deepcopy(fixture)
            filtered["arguments"]["ids"] = ids
            filtered["result"]["entries"] = [row for row in filtered["result"]["entries"] if row["id"] in ids]
            filtered["result"]["matched_count"] = len(filtered["result"]["entries"])
            with self.subTest(ids=ids):
                self.assertEqual(validate(filtered), filtered["result"])
        for offset in (0, 2, fixture["result"]["entry_count"] - 2):
            page = deepcopy(fixture)
            following = offset + 2 if offset + 2 < page["result"]["entry_count"] else None
            page["arguments"].update(offset=offset, limit=2)
            page["result"].update(entries=page["result"]["entries"][offset:offset + 2], offset=offset,
                                  limit=2, next_offset=following, truncated=following is not None)
            self.assertEqual(validate(page), page["result"])
        for category in activities.MOG_CATEGORIES:
            instructions = activities.permitted_instructions(summary(category))
            self.assertIn("selected event may be historical", instructions)
            self.assertIn("read row id 1", instructions)
            self.assertIn("filtered or paged results may omit it", instructions)
        self.assertNotIn(activities.MOG_INSTRUCTIONS, activities.permitted_instructions(summary("free_company")))

    def test_mogpendium_currency_pairs_and_standard_selected_catalog_relationship(self):
        fixture = mog_fixture("mogpendium_standard")
        malformed = deepcopy(fixture)
        reward = next(row for row in malformed["result"]["entries"] if row["id"] == 11)
        reward["reference_id"] = 1 if reward["reference_id"] != 1 else 2
        with self.assertRaises(RuntimeError):
            validate(malformed)
        secondary = mog_fixture("mogpendium_weekly")
        rows = [row for row in secondary["result"]["entries"] if row["id"] not in (5, 12)]
        currency = deepcopy(next(row for row in rows if row["id"] == 4))
        currency.update(id=5, name="Secondary reward currency", reference_id=999999)
        reward = deepcopy(next(row for row in rows if row["id"] == 11))
        reward.update(id=12, name="Displayed secondary reward", reference_id=999999)
        for extras, accepted in (([currency], True), ([currency, reward], True), ([reward], False)):
            paired = deepcopy(secondary)
            paired["result"]["entries"] = sorted(deepcopy(rows + extras), key=lambda row: row["id"])
            paired["result"].update(entry_count=len(rows + extras), matched_count=len(rows + extras))
            if accepted:
                self.assertEqual(validate(paired), paired["result"])
            else:
                with self.assertRaises(RuntimeError):
                    validate(paired)
        malformed = deepcopy(fixture)
        selection = next(row for row in malformed["result"]["entries"] if row["id"] == 10)
        used = {row["reference_id"] for row in malformed["result"]["entries"] if row["id"] >= 100}
        selection["reference_id"] = next(ident for ident in range(1, 65536) if ident not in used)
        with self.assertRaises(RuntimeError):
            validate(malformed)
        malformed = deepcopy(fixture)
        next(row for row in malformed["result"]["entries"] if row["id"] == 100)["id"] = 299
        malformed["result"]["entries"].sort(key=lambda row: row["id"])
        with self.assertRaises(RuntimeError):
            validate(malformed)

    def test_csharp_producers_roundtrip_every_gold_saucer_category(self):
        values = fixtures()
        self.assertEqual({row["result"]["category"] for row in values},
                         {"race_chocobo", "triple_triad_decks", "verminion", "mahjong"})
        for value in values:
            with self.subTest(category=value["result"]["category"]):
                self.assertEqual(validate(value), value["result"])
        extra = ROOT / ".test-tmp/pvp-fc-results.json"
        if extra.is_file():
            values = json.loads(extra.read_text(encoding="utf-8-sig"))
            # This producer includes progression fixtures alongside the activity results.
            count = 0
            for value in values:
                if value.get("result", {}).get("scope") != activities.SCOPE:
                    continue
                value["result"]["captured_at_utc"] = datetime.now(timezone.utc).isoformat()
                self.assertEqual(validate(value), value["result"])
                count += 1
            self.assertGreater(count, 0)

    def test_version_and_permission_gates_precede_rows(self):
        for category in activities.SOURCES:
            grant = summary(category)
            self.assertEqual(activities.validate_summary(grant, version=42), grant)
            for version in (33, 34, 35, 36, 37, 38, 39, 40, 41):
                denied = (category == "selected_macro" or
                          category == "free_company_credits" and version < 41 or
                          category in activities.RESOURCE_CATEGORIES and version < 40 or
                          category in activities.RESEARCH_CATEGORIES and version < 39 or
                          category == "island_currencies" and version < 38 or
                          category in activities.BOOK_CATEGORIES and version < 37 or
                          category in activities.MOG_CATEGORIES and version < 35 or
                          category == "free_company_details" and version < 34)
                if denied:
                    with self.assertRaises(RuntimeError): activities.validate_summary(grant, version=version)
                else:
                    self.assertEqual(activities.validate_summary(grant, version=version), grant)
            for version in (1, 23, 31, 32, 43, True):
                with self.subTest(category=category, version=version), self.assertRaises(RuntimeError):
                    activities.validate_summary(grant, version=version)
            self.assertEqual(activities.permitted_tool(grant)["inputSchema"]["properties"]["category"]["enum"], [category])
            text = activities.permitted_instructions(grant)
            self.assertIn(activities.CATEGORY_INSTRUCTIONS[category], text)
            for unrelated in set(activities.SOURCES) - {category}:
                self.assertNotIn(activities.CATEGORY_INSTRUCTIONS[unrelated], text)
                with self.assertRaises(RuntimeError):
                    activities.validate_arguments({"category": unrelated}, grant)
        for malformed in ({}, {"scope": activities.SCOPE}, summary(), summary("mahjong", "mahjong"), summary("invalid")):
            with self.assertRaises(RuntimeError):
                activities.validate_summary(malformed)

    def test_arguments_reject_unknown_fields_types_and_bounds(self):
        base = {"category": "triple_triad_decks"}
        for change in ({"limit": 51}, {"limit": True}, {"offset": -1}, {"offset": 513},
                       {"ids": [1, 1]}, {"ids": [0]}, {"ids": [513]}, {"ids": [True]},
                       {"ids": list(range(1, 22))}, {"query": "x" * 101}, {"group": "x" * 65},
                       {"group": "bad\ntext"}, {"state": "all"}, {"query": "\ud800"}):
            with self.subTest(change=change), self.assertRaises(RuntimeError):
                activities.validate_arguments({**base, **change})

    def test_row_units_reference_namespaces_and_configuration_ids_are_strict(self):
        cases = [
            ("race_chocobo", 0, "value", True),
            ("race_chocobo", 0, "value", 256),
            ("race_chocobo", 1, "required", 1),
            ("race_chocobo", 3, "unit", "absolute_stats"),
            ("race_chocobo", 3, "value", 501),
            ("triple_triad_decks", 0, "reference_kind", "item"),
            ("triple_triad_decks", 0, "reference_id", 476),
            ("triple_triad_decks", 0, "slot", 2),
            ("triple_triad_decks", 0, "group", "deck_2"),
            ("triple_triad_decks", 2, "text", "You do not own this card"),
            ("verminion", 0, "reference_kind", "item"),
            ("verminion", 0, "slot", 24),
        ]
        for category, index, key, bad in cases:
            fixture = one(category)
            fixture["result"]["entries"][index][key] = bad
            with self.subTest(category=category, index=index, key=key), self.assertRaises(RuntimeError):
                validate(fixture)

    def test_result_metadata_rejects_extra_fields_invented_coverage_and_wrong_paging(self):
        for key, bad in (("source", "Invented source"), ("coverage", "Everything is owned"),
                         ("owner", 7), ("entry_count", True), ("next_offset", 50),
                         ("matched_count", 51), ("offset", 1), ("readiness", "loaded")):
            fixture = one("triple_triad_decks")
            fixture["result"][key] = bad
            with self.subTest(key=key), self.assertRaises(RuntimeError):
                validate(fixture)
        fixture = one("triple_triad_decks")
        fixture["result"]["entries"][1] = deepcopy(fixture["result"]["entries"][0])
        with self.assertRaises(RuntimeError):
            validate(fixture)

    def test_unknown_mahjong_cannot_become_an_empty_success(self):
        fixture = one("mahjong")
        self.assertIsNone(validate(fixture)["entry_count"])
        fixture["result"].update(available=True, unavailable_reason=None, entry_count=0, readiness="visible_menu_observation")
        with self.assertRaises(RuntimeError):
            validate(fixture)
        for category in ("race_chocobo", "triple_triad_decks", "verminion"):
            fixture = one(category)
            fixture["arguments"]["ids"] = [512]
            fixture["result"].update(entry_count=0, entries=[], matched_count=0)
            with self.subTest(category=category), self.assertRaises(RuntimeError):
                validate(fixture)

    def test_retained_snapshot_expiry_and_future_timestamp(self):
        fixture = one("race_chocobo")
        fixture["result"]["readiness"] = "last_observed_menu"
        fixture["result"]["coverage"] += activities.HISTORICAL_COVERAGE
        fixture["result"]["captured_at_utc"] = (datetime.now(timezone.utc) - timedelta(minutes=14)).isoformat()
        self.assertEqual(validate(fixture), fixture["result"])
        for delta in (timedelta(minutes=-15), timedelta(seconds=1), timedelta(seconds=5)):
            fixture["result"]["captured_at_utc"] = (datetime.now(timezone.utc) + delta).isoformat()
            with self.subTest(delta=delta), self.assertRaises(RuntimeError):
                validate(fixture)

    def test_filter_contract_and_compact_pagination(self):
        fixture = one("triple_triad_decks")
        fixture["arguments"].update(group="deck_1", query="Synthetic", limit=2)
        fixture["result"].update(entries=fixture["result"]["entries"][:2], matched_count=5, limit=2, next_offset=2, truncated=True)
        self.assertEqual(validate(fixture), fixture["result"])
        fixture["arguments"]["group"] = "deck_2"
        with self.assertRaises(RuntimeError):
            validate(fixture)

    def test_fc_details_required_rows_apply_only_to_complete_unfiltered_results(self):
        complete = fc_fixture()
        self.assertEqual(validate(complete), complete["result"])
        required = {1}
        for missing in required:
            malformed = deepcopy(complete)
            malformed["result"]["entries"] = [row for row in malformed["result"]["entries"] if row["id"] != missing]
            # Keep every other count/paging invariant valid so the required-ID guard is exercised.
            malformed["result"]["entry_count"] -= 1
            malformed["result"]["matched_count"] -= 1
            with self.subTest(missing=missing), self.assertRaises(RuntimeError):
                validate(malformed)

        for keep in ({1}, {1, 2, 3}, {1, 10, 20}, {1, 20, 201}):
            partial = deepcopy(complete)
            partial["result"]["entries"] = [row for row in partial["result"]["entries"] if row["id"] in keep]
            total = len(partial["result"]["entries"])
            partial["result"].update(entry_count=total, matched_count=total)
            with self.subTest(partial=keep):
                self.assertEqual(validate(partial), partial["result"])

        board = deepcopy(complete)
        board["arguments"]["group"] = "board"
        board["result"].update(entries=[row for row in board["result"]["entries"] if row["id"] == 20], matched_count=1)
        self.assertEqual(validate(board), board["result"])
        absent = deepcopy(complete)
        absent["arguments"]["ids"] = [512]
        absent["result"].update(entries=[], matched_count=0)
        self.assertEqual(validate(absent), absent["result"])
        # A first unfiltered page can legitimately end before the board, and later pages omit the profile.
        for offset in (0, 2, complete["result"]["entry_count"] - 2):
            page = deepcopy(complete)
            total = page["result"]["entry_count"]
            following = offset + 2 if offset + 2 < total else None
            page["arguments"].update(offset=offset, limit=2)
            page["result"].update(entries=page["result"]["entries"][offset:offset + 2], offset=offset,
                                  limit=2, next_offset=following, truncated=following is not None)
            with self.subTest(offset=offset):
                self.assertEqual(validate(page), page["result"])

    def test_fc_details_bound_board_text_and_typed_rank_entitlement_references(self):
        fixture = fc_fixture()
        board = next(row for row in fixture["result"]["entries"] if row["id"] == 20)
        self.assertIn("\n", board["text"])
        for ident, key, bad in ((20, "text", "bad\ttext"), (20, "text", "bad\rtext"),
                                (20, "text", "x" * 1025), (20, "text", "\ud800"),
                                (2, "text", "World\nname"), (3, "reference_id", 4),
                                (100, "name", "x" * 16), (100, "slot", 2),
                                (201, "reference_id", 2), (201, "reference_kind", "item"),
                                (201, "value", 31)):
            malformed = deepcopy(fixture)
            next(row for row in malformed["result"]["entries"] if row["id"] == ident)[key] = bad
            with self.subTest(ident=ident, key=key), self.assertRaises(RuntimeError):
                validate(malformed)

    def test_racing_rating_must_fit_a_resolved_class_when_both_rows_are_returned(self):
        fixture = one("race_chocobo")
        rating = next(row for row in fixture["result"]["entries"] if row["id"] == 4)
        rating_class = next(row for row in fixture["result"]["entries"] if row["id"] == 5)
        self.assertIsNotNone(rating_class["reference_id"], "Producer must resolve a class for this cross-row guard")
        # Keep the rating itself within its legal 1..500 range; only the class relationship is invalid.
        rating["value"] = rating_class["required"] + 1
        with self.assertRaises(RuntimeError):
            validate(fixture)

    def test_actual_provider_dispatch_and_local_pipe_bind_channels_ids_and_replays(self):
        import companion
        import deferred_fixture
        from companion_architecture_check import wire_request
        from quest_lookup import QuestQueryBridge
        from quest_lookup_check import completed, tool_message

        for fixture in fixtures() + [fc_fixture(), fc_fixture("free_company_details_unknown")] + mog_fixtures() + mog_v35_fixtures():
            category = fixture["arguments"]["category"]
            with self.subTest(category=category):
                version = fixture.get("wire_version", 37 if category == "mahjong" else 36 if category in activities.MOG_CATEGORIES else 34 if category == "free_company_details" else 33)
                request = wire_request(version)
                request["activity_lookup"] = summary(category)
                request["web_search"] = False
                client, calls = deferred_fixture.client([
                    tool_message(tool="query_activities", arguments=fixture["arguments"]), *completed()])
                client.model, client.effort, client.web_search = request["model"], request["reasoning_effort"], False
                replies, queries, workers, failures = [], [], [], []
                client.send = replies.append

                def emit_query(message):
                    self.assertIn("turn/start", [method for method, _ in calls])
                    queries.append(message)
                    self.assertEqual(set(message), {"activities_query"})
                    query = message["activities_query"]
                    self.assertEqual(query["arguments"], activities.validate_arguments(fixture["arguments"]))

                    def respond():
                        try:
                            response = {"id": query["id"], "result": deepcopy(fixture["result"])}
                            with self.assertRaises(RuntimeError):
                                bridge.accept({"menus_result": response})
                            with self.assertRaises(RuntimeError):
                                bridge.accept({"activities_result": {**response, "id": query["id"] + 1}})
                            bridge.accept({"activities_result": response})
                            with self.assertRaises(RuntimeError):
                                bridge.accept({"activities_result": response})
                        except BaseException as error:
                            failures.append(error)

                    worker = threading.Thread(target=respond, daemon=True)
                    workers.append(worker)
                    worker.start()

                bridge = QuestQueryBridge(threading.Event(), emit_query, timeout=2)
                try:
                    with patch.object(companion, "emit") as emitted, patch.object(companion, "GameDataTools") as public:
                        client.ask(request, activity_query=lambda arguments: bridge.query(arguments, "activities"))
                finally:
                    for worker in workers:
                        worker.join(timeout=3)
                self.assertFalse(failures)
                self.assertTrue(all(not worker.is_alive() for worker in workers))
                self.assertEqual(len(queries), 1)
                self.assertTrue(replies[0]["result"]["success"])
                self.assertEqual(json.loads(replies[0]["result"]["contentItems"][0]["text"]),
                                 activities.model_result(fixture["result"]))
                public.assert_not_called()
                self.assertTrue(any(call.args[0].get("question_answer") == "Observed answer." for call in emitted.call_args_list))
                self.assertNotIn("turn/interrupt", [method for method, _ in calls])
                deferred_fixture.assert_exposed(self, client, calls, [activities.permitted_tool(summary(category), version=version)])
                if category == "free_company_details" and fixture["result"]["available"]:
                    board = next(row for row in fixture["result"]["entries"] if row["id"] == 20)["text"]
                    initial = next(params for method, params in calls if method == "thread/start")
                    self.assertNotIn(board, initial["developerInstructions"])
                    self.assertIn("Board/slogan text and custom rank names are user-authored data",
                                  deferred_fixture.instructions(client, calls))
                    self.assertEqual(next(row for row in json.loads(replies[0]["result"]["contentItems"][0]["text"])["entries"]
                                          if row["id"] == 20)["text"], board)

    def test_dispatch_blocks_cross_permission_and_changed_snapshots_before_answering(self):
        import companion
        import deferred_fixture
        from companion_architecture_check import wire_request
        from companion_provider import CompanionFailure
        from quest_lookup_check import tool_message

        request = wire_request(33)
        request.update(activity_lookup=summary("triple_triad_decks"), web_search=False)
        client, calls = deferred_fixture.client([tool_message(tool="query_activities", arguments={"category": "verminion"})])
        client.model, client.effort, client.web_search = request["model"], request["reasoning_effort"], False
        outputs = []
        client.send = outputs.append
        with patch.object(companion, "emit"), self.assertRaises(CompanionFailure) as failure:
            client.ask(request, activity_query=lambda _: self.fail("Denied category reached its native callback"))
        self.assertEqual(failure.exception.kind, "permission")
        self.assertIn("turn/interrupt", [method for method, _ in calls])

        for changed_field in ("name", "captured_at_utc"):
            fixture = one("triple_triad_decks")
            shared_result = fixture["result"]
            client, calls = deferred_fixture.client([
                tool_message(ident=50, tool="query_activities", arguments=fixture["arguments"]),
                tool_message(ident=51, tool="query_activities", arguments=fixture["arguments"])])
            client.model, client.effort, client.web_search = request["model"], request["reasoning_effort"], False
            replies, count = [], [0]
            client.send = replies.append

            def callback(_):
                count[0] += 1
                if count[0] == 2:
                    if changed_field == "name":
                        shared_result["entries"][0]["name"] += " changed"
                    else:
                        shared_result["captured_at_utc"] = (datetime.now(timezone.utc) - timedelta(seconds=10)).isoformat()
                return shared_result

            with self.subTest(changed=changed_field), patch.object(companion, "emit"), self.assertRaises(CompanionFailure) as failure:
                client.ask(request, activity_query=callback)
            self.assertEqual(failure.exception.kind, "protocol")
            self.assertTrue(replies[0]["result"]["success"])
            self.assertFalse(replies[-1]["result"]["success"])
            self.assertIn("turn/interrupt", [method for method, _ in calls])



def reader_fixtures():
    result = []
    for name in ("field-record", "portable-archive", "mahjong"):
        path = ROOT / ".test-tmp" / (name + "-results.json")
        if not path.is_file():
            raise RuntimeError("Missing new-reader C# producer: " + name)
        values = json.loads(path.read_text(encoding="utf-8-sig"))
        for fixture in values:
            fixture["result"]["captured_at_utc"] = datetime.now(timezone.utc).isoformat()
            result.append(fixture)
    return result


class SelectedReaderChecks(unittest.TestCase):
    def test_actual_producers_validate_and_roundtrip_compact_projection(self):
        for fixture in reader_fixtures():
            with self.subTest(name=fixture.get("name"), arguments=fixture["arguments"]):
                result = validate(fixture, version=37)
                projected = activities.model_result(result)
                defaults = projected.pop("entry_defaults", {})
                projected["entries"] = [{**defaults, **row} for row in projected["entries"]]
                self.assertEqual(projected, result)
                self.assertNotIn("ContentId", json.dumps(result))
                self.assertNotIn("body", {row["group"] for row in result["entries"]})

    def test_archive_current_and_exact_legacy_coverage_preserve_rows_and_permissions(self):
        fixture = next(row for row in reader_fixtures() if row["result"]["category"] == "portable_archive" and
                       row["result"]["available"] and row["result"]["entry_count"] == 2 and len(row["result"]["entries"]) == 2)
        self.assertEqual(fixture["result"]["coverage"], activities.COVERAGE["portable_archive"] + activities.COMMON_COVERAGE,
                         "Regenerate the current Archive C# producer before running this check")
        for coverage in (activities.COVERAGE["portable_archive"], activities.LEGACY_PORTABLE_ARCHIVE_COVERAGE):
            for historical in (False, True):
                candidate = deepcopy(fixture)
                candidate["result"]["readiness"] = "last_observed_menu" if historical else "visible_menu_observation"
                candidate["result"]["coverage"] = coverage + activities.COMMON_COVERAGE + (activities.HISTORICAL_COVERAGE if historical else "")
                with self.subTest(legacy=coverage == activities.LEGACY_PORTABLE_ARCHIVE_COVERAGE, historical=historical):
                    self.assertEqual(validate(candidate, version=37), candidate["result"])
                    self.assertEqual(candidate["result"]["entries"], fixture["result"]["entries"])
                    with self.assertRaises(RuntimeError):
                        validate(candidate, version=36)
                    with self.assertRaises(RuntimeError):
                        activities.validate_result(candidate["result"], summary("field_record"), candidate["arguments"], version=37)
                    malformed = deepcopy(candidate)
                    malformed["result"]["entries"][0]["state"] = "unlocked"
                    with self.assertRaises(RuntimeError):
                        validate(malformed, version=37)

    def test_archive_rejects_any_unrecognized_coverage(self):
        fixture = next(row for row in reader_fixtures() if row["result"]["category"] == "portable_archive" and row["result"]["available"])
        for coverage in ("All archive entries and unlock states are known.",
                         activities.COVERAGE["portable_archive"] + " All entries are unlocked.",
                         activities.LEGACY_PORTABLE_ARCHIVE_COVERAGE + " Lore is available."):
            malformed = deepcopy(fixture)
            malformed["result"]["coverage"] = coverage + activities.COMMON_COVERAGE
            with self.subTest(coverage=coverage), self.assertRaises(RuntimeError):
                validate(malformed, version=37)

    def test_archive_answer_guidance_is_permission_scoped_and_keeps_internal_metadata_internal(self):
        from companion_request import normalize_request
        from companion_tools import build_registry
        from companion_provider import CompanionFailure
        from companion_architecture_check import wire_request
        request = wire_request(37)
        request.update(activity_lookup=summary("portable_archive"), web_search=False)
        registration = build_registry(normalize_request(request), {"activities": lambda _: {}}, None, CompanionFailure)["query_activities"]
        instructions = registration.instructions
        for guidance in ("entry's normal name", "internal metadata", "unless the user explicitly asks",
                         "No body text is supplied", "does not establish registration", "lookup is unavailable",
                         "do not substitute a selection from older chat"):
            self.assertIn(guidance, instructions)
        self.assertNotIn(activities.CATEGORY_INSTRUCTIONS["portable_archive"],
                         activities.permitted_instructions(summary("field_record"), version=37))

    def test_wire37_request_registry_is_permission_scoped_without_web(self):
        from companion_request import normalize_request
        from companion_tools import build_registry
        from companion_provider import CompanionFailure
        from companion_architecture_check import wire_request
        for category in (*activities.BOOK_CATEGORIES, "mahjong"):
            request = wire_request(37)
            request.update(activity_lookup=summary(category), web_search=False)
            normalized = normalize_request(request)
            registry = build_registry(normalized, {"activities": lambda _: {}}, None, CompanionFailure)
            self.assertIn("query_activities", registry)
            self.assertEqual(activities.permitted_tool(normalized.activity_lookup, version=37)["inputSchema"]["properties"]["category"]["enum"], [category])
            request["activity_lookup"] = None
            self.assertNotIn("query_activities", build_registry(normalize_request(request), {}, None, CompanionFailure))

    def test_books_cannot_be_granted_on_old_wires_and_mahjong_cannot_gain_values(self):
        for fixture in reader_fixtures():
            if not fixture["result"]["available"]:
                continue
            for version in (33, 34, 35, 36):
                with self.subTest(version=version, category=fixture["result"]["category"]), self.assertRaises(RuntimeError):
                    validate(fixture, version=version)
        for version in (33, 34, 35, 36):
            old = one("mahjong")
            old["result"]["source"] = activities.LEGACY_MAHJONG_SOURCE
            old["result"]["coverage"] = activities.LEGACY_MAHJONG_COVERAGE + activities.COMMON_COVERAGE
            self.assertEqual(validate(old, version=version), old["result"])

    def test_new_rows_reject_forged_fields_and_expired_observations(self):
        for fixture in reader_fixtures():
            if not fixture["result"]["available"] or not fixture["result"]["entries"]:
                continue
            for key, value in (("name", "Forged field"), ("state", "owned"), ("group", "unseen_entries"), ("slot", 1), ("text", "x" * 257)):
                bad = deepcopy(fixture)
                bad["result"]["entries"][0][key] = value
                with self.subTest(category=bad["result"]["category"], key=key), self.assertRaises(RuntimeError):
                    validate(bad, version=37)
            bad = deepcopy(fixture)
            bad["result"]["captured_at_utc"] = (datetime.now(timezone.utc) - timedelta(minutes=16)).isoformat()
            bad["result"]["readiness"] = "last_observed_menu"
            bad["result"]["coverage"] = activities.COVERAGE[bad["result"]["category"]] + activities.COMMON_COVERAGE + activities.HISTORICAL_COVERAGE
            with self.assertRaises(RuntimeError): validate(bad, version=37)


def island_fixture(values=(12345, 678)):
    rows = [{"id": index, "group": "balances", "name": name, "state": "observed", "text": None,
             "value": amount, "required": None, "unit": "cowries", "reference_id": item,
             "reference_kind": "item", "slot": None}
            for index, name, item, amount in zip((1, 2), ("Seafarer's Cowrie", "Islander's Cowrie"),
                                                  (37549, 37550), values)]
    return {"arguments": {"category": "island_currencies"}, "result": {
        "scope": activities.SCOPE, "category": "island_currencies",
        "captured_at_utc": datetime.now(timezone.utc).isoformat(), "available": True,
        "unavailable_reason": None,
        "coverage": activities.COVERAGE["island_currencies"] + activities.COMMON_COVERAGE,
        "source": activities.SOURCES["island_currencies"], "readiness": "visible_menu_observation",
        "entry_count": 2, "entries": rows, "matched_count": 2, "offset": 0, "limit": 50,
        "next_offset": None, "truncated": False}}


def island_producers():
    if not ISLAND_PRODUCER.is_file():
        raise RuntimeError("Generate the current C# Island activity producer at " + str(ISLAND_PRODUCER))
    values = json.loads(ISLAND_PRODUCER.read_text(encoding="utf-8-sig"))
    if not isinstance(values, dict) or set(values) != {"all", "zero", "unavailable"}:
        raise RuntimeError("Unexpected C# Island producer fixture shape")
    for result in values.values():
        result["captured_at_utc"] = datetime.now(timezone.utc).isoformat()
    return {name: {"arguments": {"category": "island_currencies"}, "result": result}
            for name, result in values.items()}


class IslandCurrencyChecks(unittest.TestCase):
    def test_index_and_legacy_export_sources_keep_exact_coverage_pairs(self):
        fixture = island_fixture()
        validate(fixture, version=38)
        legacy = deepcopy(fixture)
        legacy["result"]["source"] = activities.LEGACY_ISLAND_SOURCE
        suffix = activities.COMMON_COVERAGE + (activities.HISTORICAL_COVERAGE
            if legacy["result"]["readiness"] == "last_observed_menu" else "")
        legacy["result"]["coverage"] = activities.LEGACY_ISLAND_COVERAGE + suffix
        validate(legacy, version=38)
        for source, coverage in ((activities.SOURCES["island_currencies"], legacy["result"]["coverage"]),
                                 (activities.LEGACY_ISLAND_SOURCE, fixture["result"]["coverage"]),
                                 ("Unverified island source", fixture["result"]["coverage"])):
            mixed = deepcopy(fixture)
            mixed["result"].update(source=source, coverage=coverage)
            with self.assertRaises(RuntimeError):
                validate(mixed, version=38)

    def test_csharp_producer_contract_projection_and_zero(self):
        values = island_producers()
        for name, fixture in values.items():
            with self.subTest(name=name):
                original = deepcopy(fixture["result"])
                self.assertEqual(validate(fixture, version=38), original)
                projected = activities.model_result(original)
                defaults = projected.pop("entry_defaults", {})
                projected["entries"] = [{**defaults, **row} for row in projected["entries"]]
                self.assertEqual(projected, original)
                self.assertEqual(fixture["result"], original)
        self.assertEqual([row["value"] for row in values["all"]["result"]["entries"]], [12345, 678])
        self.assertEqual([row["value"] for row in values["zero"]["result"]["entries"]], [0, 0])
        self.assertFalse(values["unavailable"]["result"]["available"])
        self.assertIsNone(values["unavailable"]["result"]["entry_count"])
        self.assertEqual(values["unavailable"]["result"]["entries"], [])

    def test_rows_preserve_localized_names_and_reject_forged_fields(self):
        fixture = island_fixture((0, 2147483647))
        fixture["result"]["entries"][0]["name"] = "Monnaie insulaire locale"
        self.assertEqual(validate(fixture, version=38), fixture["result"])
        for index, change in ((0, {"id": 3}), (0, {"group": "inventory"}), (0, {"state": "owned"}),
                              (0, {"name": ""}), (0, {"name": "bad\nname"}), (0, {"name": "\ud800"}),
                              (0, {"name": "x" * 129}), (0, {"value": True}), (0, {"value": -1}),
                              (0, {"value": 2147483648}), (0, {"value": None}), (0, {"value": 1.0}),
                              (0, {"unit": "items"}), (0, {"reference_kind": "currency"}),
                              (0, {"reference_id": 37550}), (1, {"reference_id": 37549}),
                              (0, {"reference_id": None, "reference_kind": None}),
                              (0, {"text": "Owned island"}), (0, {"required": 2147483647}),
                              (0, {"slot": 1}), (0, {"native_maximum": 99999999})):
            broken = deepcopy(fixture)
            broken["result"]["entries"][index].update(change)
            with self.subTest(index=index, change=change), self.assertRaises(RuntimeError):
                validate(broken, version=38)
        for key in activities.ROW_FIELDS:
            broken = deepcopy(fixture)
            del broken["result"]["entries"][0][key]
            with self.subTest(missing=key), self.assertRaises(RuntimeError):
                validate(broken, version=38)

    def test_complete_pair_required_but_filtered_and_paged_results_are_valid(self):
        fixture = island_fixture()
        for entries in ([], fixture["result"]["entries"][:1], list(reversed(fixture["result"]["entries"])),
                        [fixture["result"]["entries"][0]] * 2,
                        fixture["result"]["entries"] + [fixture["result"]["entries"][0]]):
            broken = deepcopy(fixture)
            broken["result"].update(entries=deepcopy(entries), entry_count=len(entries), matched_count=len(entries))
            with self.subTest(entries=entries), self.assertRaises(RuntimeError):
                validate(broken, version=38)
        for ids in ([1], [2], [512]):
            filtered = deepcopy(fixture)
            filtered["arguments"]["ids"] = ids
            rows = [row for row in filtered["result"]["entries"] if row["id"] in ids]
            filtered["result"].update(entries=rows, matched_count=len(rows))
            self.assertEqual(validate(filtered, version=38), filtered["result"])
        for offset in (0, 1):
            page = deepcopy(fixture)
            page["arguments"].update(offset=offset, limit=1)
            page["result"].update(entries=page["result"]["entries"][offset:offset + 1], offset=offset, limit=1,
                                  next_offset=1 if offset == 0 else None, truncated=offset == 0)
            self.assertEqual(validate(page, version=38), page["result"])

    def test_observed_retained_and_unavailable_states_do_not_invent_balances(self):
        fixture = island_fixture()
        fixture["result"]["readiness"] = "last_observed_menu"
        fixture["result"]["coverage"] += activities.HISTORICAL_COVERAGE
        fixture["result"]["captured_at_utc"] = (datetime.now(timezone.utc) - timedelta(minutes=14)).isoformat()
        self.assertEqual(validate(fixture, version=38), fixture["result"])
        for age in (timedelta(minutes=15), timedelta(seconds=-1)):
            broken = deepcopy(fixture)
            broken["result"]["captured_at_utc"] = (datetime.now(timezone.utc) - age).isoformat()
            with self.subTest(age=age), self.assertRaises(RuntimeError):
                validate(broken, version=38)
        for key, bad in (("source", "Unproven island memory"), ("coverage", "All island data"),
                         ("readiness", "loaded"), ("available", False), ("entry_count", 0)):
            broken = island_fixture()
            broken["result"][key] = bad
            with self.subTest(key=key), self.assertRaises(RuntimeError):
                validate(broken, version=38)
        missing = island_producers()["unavailable"]
        missing["result"].update(available=True, unavailable_reason=None, entry_count=0,
                                 readiness="visible_menu_observation")
        with self.assertRaises(RuntimeError):
            validate(missing, version=38)

    def test_wire38_permission_and_guidance_preserve_existing_categories(self):
        grant = summary("island_currencies")
        for version in (33, 34, 35, 36, 37):
            with self.subTest(version=version), self.assertRaises(RuntimeError):
                activities.validate_summary(grant, version=version)
            for fixture in island_producers().values():
                with self.subTest(version=version, available=fixture["result"]["available"]), self.assertRaises(RuntimeError):
                    validate(fixture, version=version)
        self.assertEqual(activities.validate_summary(grant, version=38), grant)
        with self.assertRaises(RuntimeError):
            activities.validate_result(island_fixture()["result"], summary("free_company"),
                                       {"category": "island_currencies"}, version=38)
        tool = activities.permitted_tool(grant, version=38)
        self.assertEqual(tool["inputSchema"]["properties"]["category"]["enum"], ["island_currencies"])
        self.assertIn("Island cowries", tool["description"])
        instructions = activities.permitted_instructions(grant, version=38)
        for text in ("Currencies permission", "Islekeep's Index", "export menu is a fallback", "15 minutes", "Closing preserves it without refreshing",
                     "Mention age only when it changes the answer"):
            self.assertIn(text, instructions)
        for category in (*activities.BOOK_CATEGORIES, *activities.MOG_LIST_CATEGORIES):
            category_instructions = activities.permitted_instructions(summary(category), version=38)
            self.assertIn(activities.CATEGORY_INSTRUCTIONS[category], category_instructions)
            self.assertNotIn(activities.CATEGORY_INSTRUCTIONS["island_currencies"], category_instructions)
            self.assertEqual(activities.MOG_LIST_QUERY_INSTRUCTIONS in category_instructions,
                             category in activities.MOG_LIST_CATEGORIES)
        old_categories = [category for category in activities.SOURCES
                          if category not in (*activities.RESOURCE_CATEGORIES, *activities.RESEARCH_CATEGORIES, "free_company_credits", "selected_macro")]
        self.assertLessEqual(len(activities.permitted_tool(summary(*old_categories), version=38)["description"]), 512)

    def test_csharp_producer_through_deferred_mock_is_permission_scoped(self):
        import companion
        import deferred_fixture
        from companion_architecture_check import wire_request
        from companion_provider import CompanionFailure
        from companion_request import normalize_request
        from companion_tools import build_registry
        from quest_lookup_check import completed, tool_message

        request = wire_request(38)
        request.update(activity_lookup=summary("island_currencies"), web_search=False)
        for name, fixture in island_producers().items():
            client, calls = deferred_fixture.client([
                tool_message(tool="query_activities", arguments=fixture["arguments"]), *completed()])
            client.model, client.effort, client.web_search = request["model"], request["reasoning_effort"], False
            replies, queries = [], []
            client.send = replies.append

            def query(arguments):
                queries.append(arguments)
                return deepcopy(fixture["result"])

            with self.subTest(name=name), patch.object(companion, "emit") as emitted, \
                    patch.object(companion, "GameDataTools") as public:
                client.ask(request, activity_query=query)
            self.assertEqual(queries, [activities.validate_arguments(fixture["arguments"])])
            self.assertTrue(replies[0]["result"]["success"])
            self.assertEqual(json.loads(replies[0]["result"]["contentItems"][0]["text"]),
                             activities.model_result(fixture["result"]))
            public.assert_not_called()
            self.assertTrue(any(call.args[0].get("question_answer") == "Observed answer." for call in emitted.call_args_list))
            self.assertNotIn("turn/interrupt", [method for method, _ in calls])
            deferred_fixture.assert_exposed(self, client, calls,
                                           [activities.permitted_tool(request["activity_lookup"], version=38)])
        denied = deepcopy(request)
        denied["activity_lookup"] = None
        self.assertNotIn("query_activities", build_registry(normalize_request(denied), {}, None, CompanionFailure))
        denied["activity_lookup"] = summary("free_company")
        registry = build_registry(normalize_request(denied), {"activities": lambda _: self.fail("Denied query reached reader")},
                                  None, CompanionFailure)
        with self.assertRaises(RuntimeError):
            registry["query_activities"].validate_arguments({"category": "island_currencies"})


def research_producers():
    if not RESEARCH_PRODUCER.is_file():
        raise RuntimeError("Generate the current C# research progress producer at " + str(RESEARCH_PRODUCER))
    values = json.loads(RESEARCH_PRODUCER.read_text(encoding="utf-8-sig"))
    expected = {"island_partial", "island_cap", "island_unavailable", "cosmic_all", "cosmic_filtered", "cosmic_unavailable"}
    if not isinstance(values, dict) or set(values) != expected:
        raise RuntimeError("Unexpected C# research progress producer fixture shape")
    for fixture in values.values():
        if not isinstance(fixture, dict) or set(fixture) != {"arguments", "result"}:
            raise RuntimeError("Unexpected C# research progress fixture entry")
        fixture["result"]["captured_at_utc"] = datetime.now(timezone.utc).isoformat()
    return values


class ResearchProgressChecks(unittest.TestCase):
    def test_csharp_results_roundtrip_without_projection_loss_or_mutation(self):
        values = research_producers()
        for name, fixture in values.items():
            with self.subTest(name=name):
                original = deepcopy(fixture["result"])
                self.assertEqual(validate(fixture, version=39), original)
                projected = activities.model_result(original)
                defaults = projected.pop("entry_defaults", {})
                projected["entries"] = [{**defaults, **row} for row in projected["entries"]]
                self.assertEqual(projected, original)
                self.assertEqual(fixture["result"], original)
        self.assertEqual(values["cosmic_all"]["result"]["entry_count"], 99)
        self.assertEqual(values["cosmic_filtered"]["result"]["matched_count"], 9)
        for name in ("island_unavailable", "cosmic_unavailable"):
            self.assertFalse(values[name]["result"]["available"])
            self.assertIsNone(values[name]["result"]["entry_count"])
            self.assertEqual(values[name]["result"]["entries"], [])

    def test_island_cap_requires_consistent_pair_and_nullable_experience(self):
        values = research_producers()
        cap = values["island_cap"]
        partial = values["island_partial"]
        self.assertEqual([row["text"] for row in cap["result"]["entries"]], ["Maximum rank", "No further rank"])
        self.assertIsNone(cap["result"]["entries"][1]["value"])
        self.assertIsNone(cap["result"]["entries"][1]["required"])
        for fixture, other in ((cap, partial), (partial, cap)):
            mixed = deepcopy(fixture)
            mixed["result"]["entries"][1] = deepcopy(other["result"]["entries"][1])
            with self.subTest(cap=fixture is cap), self.assertRaises(RuntimeError):
                validate(mixed, version=39)
        malformed = deepcopy(cap)
        malformed["result"]["entries"][1].update(value=0, required=None)
        with self.assertRaises(RuntimeError): validate(malformed, version=39)
        malformed = deepcopy(partial)
        malformed["result"]["entries"][1]["value"] = malformed["result"]["entries"][1]["required"]
        with self.assertRaises(RuntimeError): validate(malformed, version=39)
        zero = deepcopy(partial)
        zero["result"]["entries"][1]["value"] = 0
        self.assertEqual(validate(zero, version=39), zero["result"])

    def test_island_rows_enforce_bounds_types_and_exact_states(self):
        fixture = research_producers()["island_partial"]
        changes = ((0, {"id": 3}), (0, {"value": 0}), (0, {"value": 256}), (0, {"value": True}),
                   (0, {"text": "Owned island"}), (0, {"required": 255}), (0, {"unit": "level"}),
                   (0, {"group": "balances"}), (0, {"reference_id": 1, "reference_kind": "item"}),
                   (1, {"text": "Maximum rank"}), (1, {"value": -1}), (1, {"value": 1.0}),
                   (1, {"value": 2147483648}), (1, {"required": 0}), (1, {"required": 2147483648}),
                   (1, {"value": None}), (1, {"required": None}), (1, {"slot": 1}),
                   (1, {"state": "complete"}), (1, {"name": "bad\nname"}), (1, {"name": "\ud800"}))
        for index, change in changes:
            bad = deepcopy(fixture)
            bad["result"]["entries"][index].update(change)
            with self.subTest(change=change), self.assertRaises(RuntimeError): validate(bad, version=39)
        for entries in (fixture["result"]["entries"][:1], list(reversed(fixture["result"]["entries"])),
                        [fixture["result"]["entries"][0]] * 2):
            bad = deepcopy(fixture)
            bad["result"].update(entries=deepcopy(entries), entry_count=len(entries), matched_count=len(entries))
            with self.assertRaises(RuntimeError): validate(bad, version=39)

    def test_island_filtered_rows_and_pagination_do_not_fabricate_the_other_half(self):
        for fixture in (research_producers()["island_partial"], research_producers()["island_cap"]):
            for ids in ([1], [2], [512]):
                filtered = deepcopy(fixture)
                filtered["arguments"]["ids"] = ids
                rows = [row for row in filtered["result"]["entries"] if row["id"] in ids]
                filtered["result"].update(entries=rows, matched_count=len(rows))
                self.assertEqual(validate(filtered, version=39), filtered["result"])
            for offset in (0, 1):
                page = deepcopy(fixture)
                page["arguments"].update(offset=offset, limit=1)
                page["result"].update(entries=page["result"]["entries"][offset:offset + 1], offset=offset, limit=1,
                                      next_offset=1 if offset == 0 else None, truncated=offset == 0)
                self.assertEqual(validate(page, version=39), page["result"])

    def test_cosmic_id_job_and_analysis_mapping_cover_all_eleven_classes(self):
        rows = research_producers()["cosmic_filtered"]["result"]["entries"]
        stage = deepcopy(next(row for row in rows if row["slot"] is None))
        analysis = deepcopy(next(row for row in rows if row["slot"] is not None))
        for job in range(8, 19):
            for slot in range(1, 10):
                row = deepcopy(stage if slot <= 2 else analysis)
                row.update(id=(job - 8) * 10 + slot, group=f"class_job_{job}", reference_id=job,
                           value=20 if slot <= 2 else 65535, slot=None if slot <= 2 else slot - 2)
                self.assertEqual(activities.validate_row(row, "cosmic_research", version=39), row)
                row["value"] = 0
                self.assertEqual(activities.validate_row(row, "cosmic_research", version=39), row)

    def test_cosmic_rows_reject_forged_references_requirements_and_slot_meanings(self):
        fixture = research_producers()["cosmic_filtered"]
        stage_index = next(index for index, row in enumerate(fixture["result"]["entries"]) if row["slot"] is None)
        analysis_index = next(index for index, row in enumerate(fixture["result"]["entries"]) if row["slot"] is not None)
        stage = fixture["result"]["entries"][stage_index]
        analysis = fixture["result"]["entries"][analysis_index]
        changes = [(stage_index, change) for change in (
            {"id": (stage["reference_id"] - 8) * 10 + 10}, {"reference_id": 7}, {"reference_id": 19},
            {"reference_id": None, "reference_kind": None}, {"reference_kind": "item"}, {"group": "tools"},
            {"value": -1}, {"value": 21}, {"value": True}, {"value": None}, {"value": 1.0},
            {"required": 20}, {"slot": 1}, {"unit": "owned"}, {"text": "x" * 257},
            {"text": "bad\nlabel"}, {"state": "owned"}, {"name": ""}, {"name": "\ud800"})]
        changes += [(analysis_index, change) for change in (
            {"value": 65536}, {"value": -1}, {"required": 65535}, {"text": "upgrade available"},
            {"slot": None}, {"slot": analysis["slot"] + 1}, {"unit": "stage"}, {"capacity": 99999})]
        for index, change in changes:
            bad = deepcopy(fixture)
            bad["result"]["entries"][index].update(change)
            with self.subTest(change=change), self.assertRaises(RuntimeError): validate(bad, version=39)
        for key in activities.ROW_FIELDS:
            bad = deepcopy(fixture)
            del bad["result"]["entries"][stage_index][key]
            with self.subTest(missing=key), self.assertRaises(RuntimeError): validate(bad, version=39)

    def test_cosmic_public_labels_are_optional_and_do_not_infer_stage_relationships(self):
        fixture = research_producers()["cosmic_filtered"]
        stages = [row for row in fixture["result"]["entries"] if row["slot"] is None]
        self.assertEqual(len(stages), 2)
        stages[0].update(value=20, text="Localized public stage label")
        stages[1].update(value=0, text=None)
        self.assertEqual(validate(fixture, version=39), fixture["result"])

    def test_cosmic_filters_counts_paging_and_empty_matches_remain_distinct(self):
        fixture = research_producers()["cosmic_filtered"]
        self.assertEqual(len(fixture["result"]["entries"]), 9)
        page = deepcopy(fixture)
        page["arguments"].update(offset=2, limit=3)
        page["result"].update(entries=page["result"]["entries"][2:5], offset=2, limit=3, next_offset=5, truncated=True)
        self.assertEqual(validate(page, version=39), page["result"])
        queried = deepcopy(fixture)
        query = queried["result"]["entries"][0]["name"]
        queried["arguments"]["query"] = query
        rows = [row for row in queried["result"]["entries"] if all(
            term in row["name"].casefold() or term in (row["text"] or "").casefold() for term in query.casefold().split())]
        queried["result"].update(entries=rows, matched_count=len(rows))
        self.assertEqual(validate(queried, version=39), queried["result"])
        empty = deepcopy(fixture)
        empty["arguments"]["ids"] = [512]
        empty["result"].update(entries=[], matched_count=0)
        self.assertEqual(validate(empty, version=39), empty["result"])
        self.assertTrue(empty["result"]["available"])
        self.assertEqual(empty["result"]["entry_count"], 99)
        for key, value in (("entry_count", 9), ("entry_count", 98), ("entry_count", 100), ("matched_count", 100),
                           ("next_offset", 1), ("offset", 1), ("limit", 49), ("truncated", True)):
            bad = deepcopy(fixture)
            bad["result"][key] = value
            with self.subTest(key=key, value=value), self.assertRaises(RuntimeError): validate(bad, version=39)
        for arguments in ({"group": "class_job_7"}, {"query": "NoSuchPublicJob"}, {"ids": [512]}):
            bad = deepcopy(fixture)
            bad["arguments"].update(arguments)
            with self.subTest(arguments=arguments), self.assertRaises(RuntimeError): validate(bad, version=39)
        missing = deepcopy(fixture)
        missing["result"].update(entries=missing["result"]["entries"][:-1], matched_count=8)
        with self.assertRaises(RuntimeError): validate(missing, version=39)
        skipped = research_producers()["cosmic_all"]
        skipped["result"]["entries"] = skipped["result"]["entries"][1:]
        skipped["result"]["next_offset"] = len(skipped["result"]["entries"])
        with self.assertRaises(RuntimeError): validate(skipped, version=39)

    def test_closed_observations_expire_and_unavailable_is_not_zero_progress(self):
        values = research_producers()
        for name in ("island_partial", "island_cap", "cosmic_all", "cosmic_filtered"):
            retained = deepcopy(values[name])
            retained["result"].update(readiness="last_observed_menu",
                captured_at_utc=(datetime.now(timezone.utc) - timedelta(minutes=14)).isoformat())
            retained["result"]["coverage"] = (activities.COVERAGE[retained["arguments"]["category"]] +
                                               activities.COMMON_COVERAGE + activities.HISTORICAL_COVERAGE)
            self.assertEqual(validate(retained, version=39), retained["result"])
            for age in (timedelta(minutes=15), timedelta(seconds=-1)):
                bad = deepcopy(retained)
                bad["result"]["captured_at_utc"] = (datetime.now(timezone.utc) - age).isoformat()
                with self.subTest(name=name, age=age), self.assertRaises(RuntimeError): validate(bad, version=39)
        for name in ("island_unavailable", "cosmic_unavailable"):
            bad = deepcopy(values[name])
            bad["result"].update(available=True, unavailable_reason=None, entry_count=0, readiness="visible_menu_observation")
            with self.assertRaises(RuntimeError): validate(bad, version=39)

    def test_metadata_and_permission_contracts_remain_exact(self):
        values = research_producers()
        for name, fixture in values.items():
            for version in range(33, 39):
                with self.subTest(name=name, version=version), self.assertRaises(RuntimeError): validate(fixture, version=version)
            for key, value in (("source", "Unverified memory"), ("coverage", "Owned tools and all island inventory"),
                               ("readiness", "loaded"), ("extra", 1)):
                bad = deepcopy(fixture)
                bad["result"][key] = value
                with self.subTest(name=name, key=key), self.assertRaises(RuntimeError): validate(bad, version=39)
            with self.assertRaises(RuntimeError):
                activities.validate_result(fixture["result"], summary("island_currencies"), fixture["arguments"], version=39)

    def test_wire39_registry_guidance_and_migration_are_permission_scoped(self):
        from companion_architecture_check import wire_request
        from companion_request import normalize_request
        from companion_provider import CompanionFailure
        from companion_tools import build_registry
        for category in activities.RESEARCH_CATEGORIES:
            request = wire_request(39)
            request.update(activity_lookup=summary(category), web_search=False)
            registry = build_registry(normalize_request(request), {"activities": lambda _: self.fail("Discovery queried data")}, None, CompanionFailure)
            self.assertEqual(set(registry), {"query_activities"})
            tool = activities.permitted_tool(summary(category), version=39)
            self.assertEqual(tool["inputSchema"]["properties"]["category"]["enum"], [category])
            self.assertIn(activities.CATEGORY_INSTRUCTIONS[category], registry["query_activities"].instructions)
            for unrelated in set(activities.SOURCES) - {category}:
                self.assertNotIn(activities.CATEGORY_INSTRUCTIONS[unrelated], registry["query_activities"].instructions)
            for version in range(33, 39):
                old = deepcopy(request)
                old["version"] = version
                with self.subTest(category=category, version=version), self.assertRaises(RuntimeError): normalize_request(old)
        guidance = activities.permitted_instructions(summary("cosmic_research"), version=39)
        for phrase in ("named job", "class_job_8", "class_job_18", "99-row", "15 minutes", "Closing preserves it without refreshing",
                       "Required amounts", "capacity limits", "tool is owned or upgraded", "public stage text/label",
                       "not a tool level", "zero does not establish", "Do not infer an ordering"):
            self.assertIn(phrase, guidance)
        old_categories = [category for category in activities.SOURCES if category not in (*activities.RESOURCE_CATEGORIES, "free_company_credits", "selected_macro")]
        self.assertLessEqual(len(activities.permitted_tool(summary(*old_categories), version=39)["description"]), 512)

    def test_csharp_results_through_deferred_mock_never_enable_unrelated_readers(self):
        import companion
        import deferred_fixture
        from companion_architecture_check import wire_request
        from companion_provider import CompanionFailure
        from companion_request import normalize_request
        from companion_tools import build_registry
        from quest_lookup_check import completed, tool_message
        for name, fixture in research_producers().items():
            category = fixture["arguments"]["category"]
            request = wire_request(39)
            request.update(activity_lookup=summary(category), web_search=False)
            client, calls = deferred_fixture.client([tool_message(tool="query_activities", arguments=fixture["arguments"]), *completed()])
            client.model, client.effort, client.web_search = request["model"], request["reasoning_effort"], False
            replies, queries = [], []
            client.send = replies.append

            def query(arguments):
                queries.append(arguments)
                return deepcopy(fixture["result"])

            with self.subTest(name=name), patch.object(companion, "emit") as emitted, patch.object(companion, "GameDataTools") as public:
                client.ask(request, activity_query=query)
            self.assertEqual(queries, [activities.validate_arguments(fixture["arguments"])])
            self.assertTrue(replies[0]["result"]["success"])
            self.assertEqual(json.loads(replies[0]["result"]["contentItems"][0]["text"]), activities.model_result(fixture["result"]))
            public.assert_not_called()
            self.assertTrue(any(call.args[0].get("question_answer") == "Observed answer." for call in emitted.call_args_list))
            self.assertNotIn("turn/interrupt", [method for method, _ in calls])
            deferred_fixture.assert_exposed(self, client, calls, [activities.permitted_tool(request["activity_lookup"], version=39)])
            denied = deepcopy(request)
            denied["activity_lookup"] = None
            self.assertNotIn("query_activities", build_registry(normalize_request(denied), {}, None, CompanionFailure))
            denied["activity_lookup"] = summary("island_currencies")
            registry = build_registry(normalize_request(denied), {"activities": lambda _: self.fail("Denied query reached reader")}, None, CompanionFailure)
            with self.assertRaises(RuntimeError): registry["query_activities"].validate_arguments(fixture["arguments"])


if __name__ == "__main__":
    unittest.main()
