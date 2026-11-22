"""Offline valuation ranking, private-pipe, permission and provider integration checks."""
from copy import deepcopy
import json
from pathlib import Path
import unittest
import time
from unittest.mock import patch

import companion
import deferred_fixture
from companion_architecture_check import wire_request
from companion_request import normalize_request
from companion_tools import build_registry
from game_data_tools import GameDataTools
from market_batch_check import Transport as QuoteTransport
from quest_lookup import encoded_size
from quest_lookup_check import tool_message, completed
import saved_item_valuation as pricing

STAMP = "2026-10-01T12:00:00+00:00"
SUMMARY = {"scope": pricing.SCOPE, "snapshot_id": "a" * 32, "categories": ["retainers", "saddlebag"]}


class Transport(QuoteTransport):
    """Strong sale support for the existing ranking/deadline fixtures."""
    def __init__(self):
        super().__init__()
        self.sales = [{"pricePerUnit": 100000, "quantity": 1, "hq": hq,
                       "timestamp": int(time.time()) - i - 1} for hq in (False, True) for i in range(3)]


def arguments(**changes):
    return {"world_or_dc": "Alpha", "rank_by": "unit_price", "categories": ["saddlebag", "retainers"], "limit": 3, **changes}


class Local:
    def __init__(self, groups=None):
        self.groups = groups or [[1, 10, 0, 100], [2, 20, 0, 1], [3, 20, 1, 2]]
        self.calls = []
        self.mutate = None

    def __call__(self, args):
        self.calls.append(deepcopy(args))
        result = {**deepcopy(SUMMARY), "view": args["view"], "categories": sorted(args["categories"])}
        if args["view"] == "candidates":
            result.update(observed_containers=70, observed_slots=len(self.groups), unavailable_carried_containers=0,
                          carried_changed=False, captured_from_utc=STAMP, captured_to_utc=STAMP,
                          excluded_rows={"market_listings": 0, "placed_furnishings": 0},
                          rows=deepcopy(self.groups), complete=True)
        else:
            result["rows"] = [{"handle": h, "item_id": i, "is_hq": bool(hq), "name": "Synthetic item " + str(i),
                               "total_quantity": qty, "total_locations": 1, "locations_truncated": False,
                               "locations": [{"category": "retainers", "source": "PRIVATE SYNTHETIC RETAINER",
                                              "container": "RetainerPage7", "slot": 24, "quantity": qty, "captured_at_utc": STAMP}]}
                              for h, i, hq, qty in self.groups if h in args["handles"]]
        if self.mutate:
            self.mutate(result)
        return result


def request():
    value = wire_request(21)
    value.update(version=22, web_search=True, storage_lookup={"scope": "observed_storage_v1", "categories": ["retainers", "saddlebag"]},
                 valuation_lookup=deepcopy(SUMMARY))
    return value


class ValuationChecks(unittest.TestCase):
    def setUp(self):
        patcher = patch("market_request_queue.START_INTERVAL", 0)
        patcher.start()
        self.addCleanup(patcher.stop)

    def test_complete_ranking_quality_and_quantity_modes_keep_locations(self):
        for metric, expected_id, expected_hq in (("unit_price", 20, True), ("total_value", 10, False)):
            local, transport = Local(), Transport()
            tool = pricing.SavedItemValuation(SUMMARY, local, GameDataTools(True, transport=transport))
            args = arguments(rank_by=metric)
            result = tool.validate_result(tool.query(args), SUMMARY, args)
            row = result["rows"][0]
            self.assertEqual((row["item_id"], row["is_hq"]), (expected_id, expected_hq))
            self.assertEqual(row["locations"][0]["source"], "PRIVATE SYNTHETIC RETAINER")
            self.assertTrue(result["coverage"]["all_market_groups_priced"])
            self.assertEqual(len(local.calls), 2)
            self.assertNotIn("PRIVATE", " ".join(transport.urls))
            self.assertNotIn("handle", row)
            self.assertLess(encoded_size(result), 20000)

    def test_ten_full_retainers_winner_at_last_item_without_model_paging(self):
        local, transport = Local([[i, i, 0, 1] for i in range(1, 1751)]), Transport()
        tool = pricing.SavedItemValuation(SUMMARY, local, GameDataTools(True, transport=transport))
        result = tool.query(arguments(limit=1))
        self.assertEqual(result["rows"][0]["item_id"], 1750)
        self.assertEqual(result["coverage"]["priced_groups"], 1750)
        self.assertEqual(result["coverage"]["http_requests"], 20)
        self.assertEqual([call["view"] for call in local.calls], ["candidates", "details"])
        self.assertLess(encoded_size(result), 3000)
        again = tool.query(arguments())
        self.assertFalse(again["ok"])
        self.assertEqual(len(local.calls), 2)

    def test_partial_lookup_never_claims_complete_ranking(self):
        local, transport = Local([[i, i, 0, 1] for i in range(1, 201)]), Transport()
        transport.fail_batch = 2
        tool = pricing.SavedItemValuation(SUMMARY, local, GameDataTools(True, transport=transport))
        result = tool.query(arguments())
        self.assertEqual(result["rows"][0]["item_id"], 100)
        self.assertEqual(result["coverage"]["priced_groups"], 100)
        self.assertEqual(result["coverage"]["unpriced_groups"], 100)
        self.assertFalse(result["coverage"]["all_market_groups_priced"])
        self.assertEqual(result["coverage"]["market_error"]["code"], "rate_limited")

    def test_no_quotes_does_not_establish_winner_or_zero_price(self):
        transport = Transport()
        def no_price(raw):
            for row in raw["items"].values():
                row.update(minPriceNQ=0, minPriceHQ=0, recentHistory=[])
        transport.mutate = no_price
        local = Local()
        result = pricing.SavedItemValuation(SUMMARY, local, GameDataTools(True, transport=transport)).query(arguments())
        self.assertEqual(result["rows"], [])
        self.assertFalse(result["coverage"]["all_market_groups_priced"])
        self.assertEqual(result["coverage"]["unpriced_groups"], 3)
        self.assertEqual(len(local.calls), 1)

    def test_permissions_registered_only_with_online_and_matching_sources(self):
        value = request()
        prepared = normalize_request(value)
        registry = build_registry(prepared, {"storage": lambda _: None, "valuation": Local()},
                                  GameDataTools(True, transport=Transport()), companion.CompanionFailure)
        self.assertIn("price_saved_items", registry)
        with self.assertRaises(companion.CompanionFailure) as denial:
            companion.require_lookup_permission("valuation", arguments(categories=["fc_chest"]), SUMMARY)
        self.assertEqual(denial.exception.kind, "permission")
        for mutate in (lambda v: v.update(web_search=False), lambda v: v.update(storage_lookup=None),
                       lambda v: v["valuation_lookup"].update(categories=["inventory"])):
            invalid = request()
            mutate(invalid)
            with self.assertRaises(RuntimeError): normalize_request(invalid)
        value.update(web_search=False, valuation_lookup=None)
        registry = build_registry(normalize_request(value), {"storage": lambda _: None}, None, companion.CompanionFailure)
        self.assertNotIn("price_saved_items", registry)

    def test_snapshot_tampering_and_permission_revocation_fail_before_release(self):
        for mutation in (lambda r: r.update(snapshot_id="b" * 32),
                         lambda r: r.update(complete=False),
                         lambda r: r["rows"].append(r["rows"][0]),
                         lambda r: r.update(categories=["fc_chest"])):
            local, transport = Local(), Transport()
            local.mutate = mutation
            with self.assertRaises(RuntimeError):
                pricing.SavedItemValuation(SUMMARY, local, GameDataTools(True, transport=transport)).query(arguments())
            self.assertEqual(transport.urls, [])
        local = Local()
        def denied(args):
            if args["view"] == "details": raise PermissionError()
            return local(args)
        with self.assertRaises(PermissionError):
            pricing.SavedItemValuation(SUMMARY, denied, GameDataTools(True, transport=Transport())).query(arguments())

    def test_compact_results_limit_locations_without_dropping_ranked_items(self):
        local = Local([[i, i, 0, 20] for i in range(1, 11)])
        def many_details(raw):
            if raw["view"] != "details": return
            for row in raw["rows"]:
                template = row["locations"][0]
                row.update(total_locations=20, locations=[{**template, "slot": i, "quantity": 1,
                            "source": "\u0219" * 32, "container": "\u021b" * 32} for i in range(20)])
        local.mutate = many_details
        result = pricing.SavedItemValuation(SUMMARY, local, GameDataTools(True, transport=Transport())).query(arguments(limit=10))
        self.assertLessEqual(encoded_size(result), 20000)
        self.assertEqual(len(result["rows"]), 10)
        self.assertTrue(all(row["locations_truncated"] and row["locations"] for row in result["rows"]))

    def test_provider_bridge_exposes_one_tool_and_only_final_ranking(self):
        local, transport = Local(), Transport()
        def bind(value):
            if value["view"] == "candidates":
                value["resume_identity"] = "b" * 64
        local.mutate = bind
        client, calls = deferred_fixture.client([tool_message(tool="price_saved_items", arguments=arguments()), *completed()])
        client.web_search = True
        sent = []
        client.send = sent.append
        with patch.object(companion, "GameDataTools", return_value=GameDataTools(True, transport=transport)), patch.object(companion, "emit") as emitted:
            client.ask(request(), storage_query=lambda _: None, valuation_query=local)
        responses = [row["result"] for row in sent if "result" in row]
        self.assertEqual(len(responses), 1)
        self.assertNotIn(SUMMARY["snapshot_id"], json.dumps(responses))
        self.assertNotIn("b" * 64, json.dumps(client.wire_replies))
        self.assertNotIn("resume_identity", json.dumps(client.wire_replies))
        self.assertEqual(len(local.calls), 2)
        answer = next(call.args[0] for call in emitted.call_args_list if "question_answer" in call.args[0])
        self.assertIn("\nMarket estimate: ", answer["question_context"])

    def test_inflated_listing_cannot_outrank_better_recent_sales(self):
        transport = Transport()
        def prices(raw):
            for key, row in raw["items"].items():
                row["minPriceNQ"] = 999999 if key == "10" else 500
                row["recentHistory"] = [{"pricePerUnit": price, "quantity": 1, "hq": False,
                    "timestamp": int(time.time()) - n - 1}
                    for n, price in enumerate([100, 102, 999999] if key == "10" else [500, 501, 502])]
        transport.mutate = prices
        result = pricing.SavedItemValuation(SUMMARY, Local(), GameDataTools(True, transport=transport)).query(arguments())
        self.assertEqual([(row["item_id"], row["estimated_price_per_unit"]) for row in result["rows"]], [(20, 500), (10, 102)])
        self.assertEqual(result["rows"][1]["asking_price_per_unit"], 999999)
        self.assertEqual(result["coverage"]["insufficient_recent_sales_groups"], 1)  # HQ cannot borrow NQ sales.
        self.assertFalse(result["coverage"]["all_market_groups_priced"])

    def test_thin_sales_excluded_and_median_can_price_without_listing(self):
        transport = Transport()
        def prices(raw):
            for key, row in raw["items"].items():
                row["recentHistory"] = [{"pricePerUnit": 101 + n, "quantity": 99, "hq": False,
                    "timestamp": int(time.time()) - n - 1} for n in range(4 if key == "20" else 2)]
                if key == "20": row.update(minPriceNQ=0)
        transport.mutate = prices
        result = pricing.SavedItemValuation(SUMMARY, Local(), GameDataTools(True, transport=transport)).query(arguments())
        self.assertEqual(len(result["rows"]), 1)
        row = result["rows"][0]
        self.assertIsNone(row["asking_price_per_unit"])
        self.assertEqual(row["recent_sales"]["median_price_per_unit"], 102.5)
        self.assertEqual(row["estimated_price_per_unit"], 102)
        self.assertEqual(result["coverage"]["insufficient_recent_sales_groups"], 2)

    def test_cached_quotes_preserve_age_and_show_visible_notice(self):
        tools = GameDataTools(True, transport=Transport())
        first = pricing.SavedItemValuation(SUMMARY, Local(), tools).query(arguments())
        second = pricing.SavedItemValuation(SUMMARY, Local(), tools).query(arguments())
        self.assertEqual(second["coverage"]["http_requests"], 0)
        self.assertEqual(first["rows"][0]["quote_fetched_at_utc"], second["rows"][0]["quote_fetched_at_utc"])
        self.assertIn("cached", second["pricing_notice"])
        self.assertIn("possibly outdated", second["pricing_notice"])
        self.assertTrue(all(row["possibly_outdated"] for row in second["rows"]))

    def test_unknown_or_future_upload_time_is_not_claimed_fresh(self):
        rows = [{"upstream_updated_at_utc": value, "cache_hit": False}
                for value in (None, "2099-01-01T00:00:00+00:00")]
        notice, summary = pricing.freshness(rows, pricing.datetime.now(pricing.timezone.utc))
        self.assertIn("possibly outdated", notice)
        self.assertEqual(summary["unknown_upload_quotes"], 2)
        self.assertTrue(all(row["upload_age_seconds_at_lookup"] is None for row in rows))

    def test_csharp_fixture_contract(self):
        path = Path(".test-tmp/valuation-csharp-fixture.json")
        self.assertTrue(path.is_file(), "Run the C# valuation fixture producer first")
        fixture = json.loads(path.read_text(encoding="utf-8-sig"))
        pricing.validate_summary(fixture["summary"])
        pricing.validate_snapshot(fixture["candidates"], fixture["summary"], fixture["arguments"])
        pricing.validate_details(fixture["details"], fixture["summary"], {**fixture["arguments"], "view": "details", "handles": [r["handle"] for r in fixture["details"]["rows"]]}, fixture["candidates"])


if __name__ == "__main__":
    unittest.main()
