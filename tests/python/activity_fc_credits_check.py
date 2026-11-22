"""Offline wire43 FC credit contracts and explicit wire41/42 compatibility using the C# producer."""
from copy import deepcopy
from datetime import datetime, timedelta
import json
from pathlib import Path
import unittest
from unittest.mock import patch

import activity_lookup as activities


PRODUCER = Path(__file__).resolve().parents[2] / ".test-tmp/fc-credit-activity-producer.json"
CATEGORY = "free_company_credits"
CASES = {"all", "zero", "maximum", "unavailable", "topics", "topics_zero"}
SOURCE = "Visible ready agent-owned FreeCompany Topics or FreeCompanyCreditShop; displayed company credit balance"
COVERAGE = (
    "Company credits observed on the open Free Company Topics tab or in the Company Credit item shop. Retained for 15 minutes after observation; "
    "a closed menu does not refresh the balance. The observation does not independently establish a named "
    "company identity, membership, spending permission, stock or affordability. Missing or unreadable data is "
    "unknown, not zero. No chat, roster, company actions or purchases are included."
)


def summary(*categories):
    return {"scope": activities.SCOPE, "categories": list(categories)}


class FreeCompanyCreditsActivityChecks(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not PRODUCER.is_file():
            raise RuntimeError("Required C# producer missing: run ActivityCache.Tests -- --produce-fc-credit-fixtures "
                               ".test-tmp/fc-credit-activity-producer.json first")
        cls.fixtures = json.loads(PRODUCER.read_text(encoding="utf-8-sig"))
        if not isinstance(cls.fixtures, dict) or set(cls.fixtures) != CASES:
            raise RuntimeError("FC credit producer must contain all, zero, maximum, unavailable, topics and topics_zero C# cases")
        # Do not rewrite producer timestamps. A fixed validation clock keeps TTL checks deterministic.
        cls.now = max(datetime.fromisoformat(case["result"]["captured_at_utc"])
                      for case in cls.fixtures.values()) + timedelta(seconds=1)

    def fixture(self, name="all"):
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
            return activities.validate_result(fixture["result"], summary(CATEGORY) if grant is None else grant,
                                              fixture["arguments"], version=version)

    def legacy(self, fixture):
        result = deepcopy(fixture)
        result["result"]["source"] = activities.LEGACY_V42_SOURCES[CATEGORY]
        suffix = activities.HISTORICAL_COVERAGE if result["result"]["readiness"] == "last_observed_menu" else ""
        result["result"]["coverage"] = activities.LEGACY_V42_COVERAGE[CATEGORY] + activities.COMMON_COVERAGE + suffix
        return result

    def test_required_csharp_cases_roundtrip_exact_contract_and_projection_without_mutation(self):
        for name in sorted(CASES):
            fixture = self.fixture(name)
            before = deepcopy(fixture)
            with self.subTest(name=name):
                self.assertEqual(fixture["arguments"], {"category": CATEGORY})
                self.assertEqual(fixture["result"]["source"], SOURCE)
                suffix = activities.HISTORICAL_COVERAGE if name != "unavailable" else ""
                self.assertEqual(fixture["result"]["coverage"], COVERAGE + activities.COMMON_COVERAGE + suffix)
                validated = self.validate(fixture)
                self.assertEqual(validated, before["result"])
                projected = activities.model_result(validated)
                restored = deepcopy(projected)
                defaults = restored.pop("entry_defaults", {})
                self.assertTrue(all(value is None for value in defaults.values()))
                restored["entries"] = [{**defaults, **row} for row in restored["entries"]]
                self.assertEqual(restored, before["result"])
                if projected["entries"]:
                    projected["entries"][0]["value"] = 7
                self.assertEqual(fixture, before)
        self.assertEqual(self.validate(self.fixture("zero"))["entries"][0]["value"], 0)
        self.assertEqual(self.validate(self.fixture("maximum"))["entries"][0]["value"], 4294967295)
        for name in ("topics", "topics_zero"):
            row = self.validate(self.fixture(name))["entries"][0]
            self.assertEqual(row["name"], "Company credits shown on Topics")
            self.assertEqual(row["value"], 0 if name == "topics_zero" else 23456)

    def test_legacy_shop_contracts_are_explicit_and_topics_cannot_cross_old_wire(self):
        for name in sorted(CASES):
            original = self.fixture(name)
            before = deepcopy(original)
            legacy = self.legacy(original)
            for version in (41, 42):
                with self.subTest(name=name, version=version):
                    with self.assertRaises(RuntimeError): self.validate(original, version=version)
                    if name.startswith("topics"):
                        with self.assertRaises(RuntimeError): self.validate(legacy, version=version)
                    else:
                        self.assertEqual(self.validate(legacy, version=version), legacy["result"])
            with self.subTest(name=name), self.assertRaises(RuntimeError): self.validate(legacy)
            self.assertEqual(original, before)

    def test_native_uint_balance_and_only_one_fixed_unattributed_row_are_accepted(self):
        changes = [{"value": value} for value in (-1, 4294967296, True, False, 1.5, "12345", None)]
        changes += [{"id": 2}, {"group": "company"}, {"name": "Named FC balance"}, {"state": "owned"},
                    {"text": "Example Company"}, {"required": 4294967295}, {"unit": "gil"}, {"slot": 1},
                    {"reference_id": 123, "reference_kind": "free_company"},
                    {"reference_id": 1, "reference_kind": "item"}, {"company_id": 123}]
        for change in changes:
            fixture = self.fixture()
            fixture["result"]["entries"][0].update(change)
            with self.subTest(change=change), self.assertRaises(RuntimeError):
                self.validate(fixture)
        for field in activities.ROW_FIELDS:
            fixture = self.fixture()
            del fixture["result"]["entries"][0][field]
            with self.subTest(missing=field), self.assertRaises(RuntimeError):
                self.validate(fixture)

    def test_shape_zero_and_unavailable_are_distinct(self):
        zero = self.validate(self.fixture("zero"))
        unavailable = self.validate(self.fixture("unavailable"))
        self.assertEqual((zero["available"], zero["entry_count"], zero["matched_count"]), (True, 1, 1))
        self.assertEqual((unavailable["available"], unavailable["entry_count"], unavailable["entries"]), (False, None, []))
        for change in ({"entry_count": 0}, {"entry_count": 2}, {"matched_count": 2},
                       {"entries": [], "matched_count": 0}, {"entries": self.fixture()["result"]["entries"] * 2},
                       {"next_offset": 1}, {"truncated": True}):
            fixture = self.fixture()
            fixture["result"].update(change)
            with self.subTest(change=change), self.assertRaises(RuntimeError): self.validate(fixture)
        for change in ({"entry_count": 0}, {"readiness": "last_observed_menu"}, {"unavailable_reason": None},
                       {"entries": self.fixture()["result"]["entries"]}):
            fixture = self.fixture("unavailable")
            fixture["result"].update(change)
            with self.subTest(unavailable_change=change), self.assertRaises(RuntimeError): self.validate(fixture)

    def test_current_wire_and_explicit_category_grant_gate_discovery_validation_and_rechecks(self):
        from companion_architecture_check import wire_request
        from companion_provider import CompanionFailure
        from companion_request import normalize_request, recheck_request
        from companion_tools import build_registry

        request = wire_request(43)
        request.update(activity_lookup=summary(CATEGORY), web_search=False)
        approved = normalize_request(request)
        callbacks = {"activities": lambda _: self.fail("Discovery or denied access queried data")}
        registry = build_registry(approved, callbacks, None, CompanionFailure)
        self.assertEqual(set(registry), {"query_activities"})
        registration = registry["query_activities"]
        self.assertEqual(registration.definition["inputSchema"]["properties"]["category"]["enum"], [CATEGORY])
        self.assertIn("FC credits", registration.definition["description"])
        self.assertIn(activities.CATEGORY_INSTRUCTIONS[CATEGORY], registration.instructions)
        self.assertNotIn("entries", json.dumps(approved.context({})))
        for unrelated in set(activities.SOURCES) - {CATEGORY}:
            with self.subTest(unrelated=unrelated), self.assertRaises(RuntimeError):
                registration.validate_arguments({"category": unrelated})
        for version in range(1, 41):
            old = wire_request(version)
            old.update(activity_lookup=summary(CATEGORY), web_search=False)
            with self.subTest(version=version), self.assertRaises(RuntimeError): normalize_request(old)
        for version in range(33, 41):
            for name in CASES:
                with self.subTest(version=version, case=name), self.assertRaises(RuntimeError):
                    self.validate(self.fixture(name), version=version)
        for other in ("free_company", "free_company_details", "island_currencies"):
            with self.subTest(grant=other), self.assertRaises(RuntimeError):
                self.validate(self.fixture(), grant=summary(other))
        denied = deepcopy(request)
        denied["activity_lookup"] = None
        with self.assertRaises(RuntimeError): recheck_request(denied, approved)
        self.assertNotIn("query_activities", build_registry(normalize_request(denied), callbacks, None, CompanionFailure))

    def test_exact_source_coverage_and_readiness_reject_unsupported_identity_claims(self):
        for name in CASES:
            for change in ({"source": activities.SOURCES["free_company"]}, {"source": activities.LEGACY_V42_SOURCES[CATEGORY]},
                           {"coverage": "The current named company's live balance."}, {"coverage": COVERAGE},
                           {"readiness": "loaded"}, {"company_name": "Example Company"}):
                fixture = self.fixture(name)
                fixture["result"].update(change)
                with self.subTest(name=name, change=change), self.assertRaises(RuntimeError): self.validate(fixture)
        visible = self.fixture()
        visible["result"].update(readiness="visible_menu_observation", coverage=COVERAGE + activities.COMMON_COVERAGE)
        self.assertEqual(self.validate(visible), visible["result"])

    def test_retained_balance_expires_at_fifteen_minutes_and_rejects_future_capture(self):
        for name in ("all", "zero", "maximum", "topics", "topics_zero"):
            recent = self.fixture(name)
            recent["result"]["captured_at_utc"] = (self.now - timedelta(minutes=14, seconds=59)).isoformat()
            self.assertEqual(self.validate(recent), recent["result"])
            for age in (timedelta(minutes=15), timedelta(days=1), timedelta(seconds=-1)):
                bad = deepcopy(recent)
                bad["result"]["captured_at_utc"] = (self.now - age).isoformat()
                with self.subTest(name=name, age=age), self.assertRaises(RuntimeError): self.validate(bad)
        future = self.fixture("unavailable")
        future["result"]["captured_at_utc"] = (self.now + timedelta(seconds=3)).isoformat()
        with self.assertRaises(RuntimeError): self.validate(future)

    def test_query_group_id_and_offset_filters_preserve_the_one_row_snapshot(self):
        original = self.fixture()
        for filters, keep in (({"query": "COMPANY CREDITS"}, True), ({"query": "absent phrase"}, False),
                              ({"group": "credits"}, True), ({"group": "company"}, False),
                              ({"ids": [1]}, True), ({"ids": [2]}, False)):
            fixture = deepcopy(original)
            fixture["arguments"].update(filters)
            fixture["result"].update(entries=original["result"]["entries"] if keep else [], matched_count=int(keep))
            with self.subTest(filters=filters): self.assertEqual(self.validate(fixture), fixture["result"])
        page = deepcopy(original)
        page["arguments"].update(offset=1, limit=1)
        page["result"].update(offset=1, limit=1, entries=[])
        self.assertEqual(self.validate(page), page["result"])
        for filters in ({"query": "absent phrase"}, {"group": "company"}, {"ids": [2]}):
            forged = deepcopy(original)
            forged["arguments"].update(filters)
            with self.subTest(forged_filters=filters), self.assertRaises(RuntimeError): self.validate(forged)

    def test_fresh_csharp_cases_traverse_deferred_mock_without_unrelated_access(self):
        import companion
        import deferred_fixture
        from companion_architecture_check import wire_request
        from quest_lookup_check import completed, tool_message

        for name in sorted(CASES):
            fixture = self.fixture(name)
            request = wire_request(43)
            request.update(activity_lookup=summary(CATEGORY), web_search=False)
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
            deferred_fixture.assert_exposed(self, client, calls, [activities.permitted_tool(summary(CATEGORY), version=43)])

    def test_revoked_read_cannot_deliver_a_balance_or_final_answer(self):
        import companion
        import deferred_fixture
        from companion_architecture_check import wire_request
        from quest_lookup_check import tool_message

        request = wire_request(43)
        request.update(activity_lookup=summary(CATEGORY), web_search=False)
        client, _ = deferred_fixture.client([tool_message(tool="query_activities", arguments={"category": CATEGORY})])
        client.model, client.effort, client.web_search = request["model"], request["reasoning_effort"], False
        replies = []
        client.send = replies.append

        def revoked(_):
            raise PermissionError("Synthetic FC access revocation")

        with self.clock(), patch.object(companion, "emit") as emitted, self.assertRaises(companion.CompanionFailure) as failure:
            client.ask(request, activity_query=revoked)
        self.assertEqual(failure.exception.kind, "permission")
        self.assertFalse(replies[-1]["result"]["success"])
        self.assertFalse(any("question_answer" in call.args[0] for call in emitted.call_args_list))

    def test_latest_guidance_preserves_other_categories_and_keeps_fc_identity_limits(self):
        instructions = activities.permitted_instructions(summary(CATEGORY), version=43)
        self.assertIn("Topics", instructions)
        self.assertNotIn(activities.LEGACY_V42_INSTRUCTIONS[CATEGORY], instructions)
        for version in (41, 42):
            legacy = activities.permitted_instructions(summary(CATEGORY), version=version)
            with self.subTest(version=version):
                self.assertIn(activities.LEGACY_V42_INSTRUCTIONS[CATEGORY], legacy)
                self.assertNotIn(activities.CATEGORY_INSTRUCTIONS[CATEGORY], legacy)
                self.assertNotIn("Topics", legacy)
                self.assertIn("item shop", legacy)
        for phrase in ("Free Company permission", "separately cached FC name", "spending permission",
                       "only a returned zero", "15 minutes", "Mention age/identity limits only when relevant"):
            self.assertIn(phrase, instructions)
        for category in (*activities.BOOK_CATEGORIES, *activities.MOG_LIST_CATEGORIES):
            guidance = activities.permitted_instructions(summary(category), version=43)
            self.assertIn(activities.CATEGORY_INSTRUCTIONS[category], guidance)
            self.assertNotIn(activities.CATEGORY_INSTRUCTIONS[CATEGORY], guidance)
            self.assertEqual(activities.MOG_LIST_QUERY_INSTRUCTIONS in guidance, category in activities.MOG_LIST_CATEGORIES)
        self.assertLessEqual(len(activities.permitted_tool(summary(*activities.SOURCES), version=43)["description"]), 512)


if __name__ == "__main__":
    unittest.main()
