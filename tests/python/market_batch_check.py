"""Offline Universalis batch fixtures. No user inventory, credentials or HTTP calls."""
import json
from contextlib import closing
from datetime import datetime, timedelta, timezone
from pathlib import Path
import sqlite3
import tempfile
import time
from unittest import TestCase, main
from unittest.mock import patch
from urllib.error import HTTPError
from urllib.parse import parse_qs, urlsplit

from game_data_tools import GameDataTools, MAX_RESPONSE_BYTES
from market_batch import (MarketBatch, MAX_ITEMS, MAX_QUOTE_BYTES, MAX_QUOTE_DB_BYTES,
                          SALES_LIMIT, SALES_WINDOW_SECONDS, _QuoteCache, _validate_url)


class Transport:
    def __init__(self, ids=range(1, 4097)):
        self.ids = list(ids)
        self.urls = []
        self.mutate = None
        self.fail_batch = None
        self.batches = 0
        self.sales = []

    def __call__(self, url, timeout, maximum_bytes):
        self.urls.append(url)
        assert 0 < timeout <= 8 and maximum_bytes == MAX_RESPONSE_BYTES
        path = urlsplit(url).path
        if path.endswith("/worlds"):
            value = [{"id": 402, "name": "Alpha"}]
        elif path.endswith("/data-centers"):
            value = [{"name": "Light", "worlds": [402]}]
        elif path.endswith("/marketable"):
            value = self.ids
        else:
            self.batches += 1
            if self.batches == self.fail_batch:
                raise HTTPError(url, 429, "not included", {}, None)
            ids = list(map(int, path.split("/")[-1].split(",")))
            identity = {"worldID": 402} if "/402/" in path else {"dcName": "Light"}
            def item(item_id):
                return {"itemID": item_id, **identity, "minPriceNQ": item_id * 10,
                        "minPriceHQ": item_id * 20, "lastUploadTime": 1700000000000,
                        "recentHistory": self.sales,
                        "privateRetainerName": "discard this field"}
            value = item(ids[0]) if len(ids) == 1 else {
                "itemIDs": ids, "items": {str(i): item(i) for i in ids},
                **identity, "unresolvedItems": []}
            if self.mutate:
                self.mutate(value)
        return json.dumps(value).encode()


class Checks(TestCase):
    def setUp(self):
        patcher = patch("market_request_queue.START_INTERVAL", 0)
        patcher.start()
        self.addCleanup(patcher.stop)

    def test_all_ten_full_retainers_are_batched_and_compact(self):
        fake = Transport()
        result = MarketBatch(GameDataTools(True, transport=fake)).fetch(list(range(1, 1751)), "Alpha")
        self.assertIsNone(result["error"])
        self.assertEqual(result["completed_count"], 1750)
        self.assertEqual(len(result["quotes"]), 1750)
        self.assertEqual(result["request_count"], 20)  # 18 batches + worlds + marketable
        self.assertNotIn("discard this field", json.dumps(result))
        for url in fake.urls[2:]:
            params = parse_qs(urlsplit(url).query)
            self.assertEqual(params["listings"], ["0"])
            self.assertEqual(params["entries"], [str(SALES_LIMIT)])
            self.assertEqual(params["entriesWithin"], [str(SALES_WINDOW_SECONDS)])
            self.assertIn("items.minPriceNQ", params["fields"][0])
            self.assertIn("items.recentHistory.pricePerUnit", params["fields"][0])
            self.assertNotIn("buyerName", params["fields"][0])

    def test_maximum_candidates_fit_total_request_limit_and_dc_schema(self):
        fake = Transport()
        result = MarketBatch(GameDataTools(True, transport=fake)).fetch(list(range(1, MAX_ITEMS + 1)), "Light")
        self.assertIsNone(result["error"])
        self.assertEqual(result["request_count"], 44)
        self.assertEqual(result["completed_count"], MAX_ITEMS)
        self.assertEqual(result["location"], {"kind": "data_center", "name": "Light", "world_id": None})

    def test_nonmarketable_unknown_quote_and_unresolved_are_distinct(self):
        fake = Transport([2, 3, 4])
        def mutate(value):
            value["items"]["2"].update(minPriceNQ=0, minPriceHQ=None)
            value["items"]["3"].update(hasData=False)
            del value["items"]["4"]
            value["unresolvedItems"] = [4]
        fake.mutate = mutate
        result = MarketBatch(GameDataTools(True, transport=fake)).fetch([1, 2, 3, 4], "402")
        self.assertEqual(result["nonmarketable_item_ids"], [1])
        self.assertEqual(result["unresolved_item_ids"], [4])
        self.assertEqual(result["failed_item_ids"], [])
        self.assertEqual(result["completed_count"], 4)
        self.assertTrue(all(r["nq"] is None and r["hq"] is None for r in result["quotes"]))

    def test_rate_limit_stops_without_retry_and_preserves_partial_coverage(self):
        fake = Transport()
        fake.fail_batch = 2
        result = MarketBatch(GameDataTools(True, transport=fake)).fetch(list(range(1, 251)), "Alpha")
        self.assertEqual(result["error"]["code"], "rate_limited")
        self.assertEqual(result["completed_count"], 100)
        self.assertEqual(result["failed_item_ids"], list(range(101, 251)))
        self.assertEqual(fake.batches, 2)

    def test_permission_cancel_and_question_deadline_stop_before_network(self):
        for mode in ("offline", "cancelled", "late"):
            fake = Transport()
            tools = GameDataTools(mode != "offline", transport=fake, cancelled=lambda: mode == "cancelled")
            if mode == "late":
                tools.question_deadline = 0
            result = MarketBatch(tools).fetch([2], "Alpha")
            self.assertEqual(result["error"]["code"], {"offline": "online_disabled", "cancelled": "cancelled", "late": "timeout"}[mode])
            self.assertEqual(fake.urls, [])
            self.assertEqual(result["evidence"]["observations"], [])

    def test_revocation_and_total_deadline_stop_midway(self):
        for mode in ("revoke", "timeout"):
            fake = Transport()
            tools = GameDataTools(True)
            clock = [10.0]
            def transport(*args):
                value = fake(*args)
                if fake.batches == 1:
                    if mode == "revoke":
                        tools.online_allowed = False
                    else:
                        clock[0] = 56.0
                return value
            tools.transport = transport
            with patch("market_batch.time.monotonic", side_effect=lambda: clock[0]):
                result = MarketBatch(tools).fetch(list(range(1, 251)), "Alpha")
            self.assertEqual(result["error"]["code"], "online_disabled" if mode == "revoke" else "timeout")
            self.assertEqual(result["quotes"], [])  # revoked response is not released
            self.assertEqual(result["evidence"]["observations"], [])
            self.assertEqual(fake.batches, 1)

    def test_public_cache_reuses_quotes_but_obeys_gate_and_keeps_fetch_timestamp(self):
        fake = Transport()
        tools = GameDataTools(True, transport=fake)
        batch = MarketBatch(tools)
        original = batch.fetch([2, 3], "Alpha")
        repeated = batch.fetch([3, 2], "Alpha")
        self.assertEqual(repeated["request_count"], 0)
        self.assertEqual(repeated["cache_hits"], 2)
        self.assertFalse(original["quotes"][0]["cache_hit"])
        self.assertTrue(repeated["quotes"][0]["cache_hit"])
        self.assertEqual([{**row, "cache_hit": False} for row in repeated["quotes"]], original["quotes"])
        tools.online_allowed = False
        self.assertEqual(batch.fetch([2, 3], "Alpha")["error"]["code"], "online_disabled")
        self.assertEqual(len(fake.urls), 3)

    def test_malformed_scope_identity_prices_and_incomplete_batch_fail_closed(self):
        changes = [lambda v: v.update(worldID=9),
                   lambda v: v["items"]["2"].update(itemID=9),
                   lambda v: v["items"]["2"].update(minPriceNQ=True),
                   lambda v: v["items"]["2"].update(minPriceNQ=1.5),
                   lambda v: v["items"].pop("2"),
                   lambda v: v.update(unresolvedItems=[2])]
        for mutate in changes:
            fake = Transport()
            fake.mutate = mutate
            result = MarketBatch(GameDataTools(True, transport=fake)).fetch([2, 3], "Alpha")
            self.assertEqual(result["error"]["code"], "invalid_response")
            self.assertEqual(result["failed_item_ids"], [2, 3])
            self.assertEqual(result["quotes"], [])

    def test_single_item_integral_price_and_missing_resource(self):
        fake = Transport()
        fake.mutate = lambda v: v.update(minPriceNQ=12.0)
        result = MarketBatch(GameDataTools(True, transport=fake)).fetch([2], "Alpha")
        self.assertEqual(result["quotes"][0]["nq"], 12)
        self.assertNotIn("items.", parse_qs(urlsplit(fake.urls[-1]).query)["fields"][0])
        def missing(*args):
            if "/402/" in args[0]:
                raise HTTPError(args[0], 404, "missing", {}, None)
            return fake(*args)
        result = MarketBatch(GameDataTools(True, transport=missing)).fetch([2], "Alpha")
        self.assertEqual(result["unresolved_item_ids"], [2])
        self.assertIsNone(result["error"])

    def test_oversized_metadata_stops_before_quotes_and_urls_are_narrow(self):
        fake = Transport()
        def transport(*args):
            if args[0].endswith("marketable"):
                return b"x" * (MAX_RESPONSE_BYTES + 1)
            return fake(*args)
        result = MarketBatch(GameDataTools(True, transport=transport)).fetch([2], "Alpha")
        self.assertEqual(result["error"]["code"], "invalid_response")
        self.assertEqual(fake.batches, 0)
        for url in ("http://universalis.app/api/v2/worlds", "https://universalis.app.evil/api/v2/worlds",
                    "https://universalis.app/api/v2/worlds?x=1", "https://universalis.app/api/v2/402/2?listings=100"):
            with self.assertRaises(ValueError):
                _validate_url(url)

    def test_recent_sales_separate_quality_filter_time_and_ignore_identities(self):
        fake = Transport()
        now = int(time.time())
        def sale(price, quantity, hq, timestamp):
            return {"pricePerUnit": price, "quantity": quantity, "hq": hq, "timestamp": timestamp,
                    "buyerName": "never retained", "buyerID": "never retained"}
        fake.sales = [sale(100, 1, False, now - 3), sale(102, 99, False, now - 2),
                      sale(1000000, 1, False, now - 1), sale(400, 10, True, now - 4),
                      sale(401, 2, True, now - 5), sale(1, 99, False, now - SALES_WINDOW_SECONDS - 1),
                      sale(999999, 1, True, now + 100)]
        result = MarketBatch(GameDataTools(True, transport=fake)).fetch([2], "Alpha")
        self.assertIsNone(result["error"])
        row = result["quotes"][0]
        self.assertEqual(row["sales_nq"]["median_price_per_unit"], 102)
        self.assertEqual(row["sales_nq"]["sale_count"], 3)
        self.assertEqual(row["sales_nq"]["quantity"], 101)
        self.assertEqual(row["sales_hq"]["median_price_per_unit"], 400.5)
        self.assertEqual(row["sales_hq"]["sale_count"], 2)
        self.assertEqual(row["sales_hq"]["quantity"], 12)
        self.assertEqual(row["sales_hq"]["window_days"], 7)
        self.assertFalse(row["sales_hq"]["sample_limit_reached"])
        self.assertNotIn("buyer", json.dumps(result))
        self.assertNotIn("never retained", json.dumps(result))

    def test_full_sales_batch_fits_body_and_existing_cache_limits(self):
        fake = Transport()
        now = int(time.time())
        fake.sales = [{"pricePerUnit": 2**31 - 1, "quantity": 2**31 - 1,
                       "hq": bool(i % 2), "timestamp": now - i} for i in range(SALES_LIMIT)]
        tools = GameDataTools(True, transport=fake)
        batch = MarketBatch(tools)
        result = batch.fetch(list(range(1, 101)), "Alpha")
        self.assertIsNone(result["error"])
        self.assertEqual(result["completed_count"], 100)
        self.assertEqual(fake.batches, 1)
        self.assertEqual(result["quotes"][0]["sales_nq"]["sale_count"], 10)
        self.assertTrue(result["quotes"][0]["sales_nq"]["sample_limit_reached"])
        self.assertTrue(result["quotes"][0]["sales_hq"]["sample_limit_reached"])
        self.assertEqual(len(tools._market_quote_cache.memory), 100)
        self.assertTrue(all(len(json.dumps(value).encode()) <= MAX_QUOTE_BYTES
                            for value in tools._market_quote_cache.memory.values()))
        repeated = batch.fetch(list(range(1, 101)), "Alpha")
        self.assertIsNone(repeated["error"])
        self.assertEqual(repeated["cache_hits"], 100)
        self.assertEqual(repeated["request_count"], 0)
        self.assertTrue(all(row["cache_hit"] for row in repeated["quotes"]))

    def test_no_sales_does_not_invent_zero_or_use_wrong_quality(self):
        fake = Transport()
        fake.sales = [{"pricePerUnit": 123, "quantity": 2, "hq": True, "timestamp": int(time.time()) - 1}]
        result = MarketBatch(GameDataTools(True, transport=fake)).fetch([2], "Alpha")
        row = result["quotes"][0]
        self.assertEqual(row["sales_nq"]["sale_count"], 0)
        self.assertIsNone(row["sales_nq"]["median_price_per_unit"])
        self.assertIsNone(row["sales_nq"]["latest_sale_at_utc"])
        self.assertEqual(row["sales_hq"]["median_price_per_unit"], 123)

    def test_malformed_or_oversized_sale_samples_fail_without_partial_batch(self):
        for bad in ({"pricePerUnit": 0}, {"quantity": 0}, {"hq": "false"}, {"timestamp": True},
                    {"pricePerUnit": 2.5}, {"timestamp": None}):
            fake = Transport()
            fake.sales = [{"pricePerUnit": 10, "quantity": 1, "hq": False,
                           "timestamp": int(time.time()) - 1, **bad}]
            result = MarketBatch(GameDataTools(True, transport=fake)).fetch([2], "Alpha")
            self.assertEqual(result["error"]["code"], "invalid_response")
            self.assertEqual(result["quotes"], [])
        fake = Transport()
        fake.sales = [{"pricePerUnit": 10, "quantity": 1, "hq": False, "timestamp": int(time.time()) - 1}] * (SALES_LIMIT + 1)
        result = MarketBatch(GameDataTools(True, transport=fake)).fetch([2], "Alpha")
        self.assertEqual(result["error"]["code"], "invalid_response")

    def test_old_quote_cache_schema_is_not_reused(self):
        fake = Transport()
        tools = GameDataTools(True, transport=fake)
        batch = MarketBatch(tools)
        batch.fetch([2], "Alpha")
        old = next(iter(tools._market_quote_cache.memory.values()))
        old["version"] = 2
        result = batch.fetch([2], "Alpha")
        self.assertIsNone(result["error"])
        self.assertEqual(result["cache_hits"], 0)
        self.assertEqual(result["request_count"], 1)

    def test_item_world_cache_reuses_overlap_across_different_groupings(self):
        fake = Transport()
        tools = GameDataTools(True, transport=fake)
        batch = MarketBatch(tools)
        original = batch.fetch(list(range(1, 101)), "Alpha")
        result = batch.fetch([99, 100, 101, 102, 103], "402")
        self.assertIsNone(result["error"])
        self.assertEqual(result["cache_hits"], 2)
        self.assertEqual(result["request_count"], 1)
        self.assertTrue(urlsplit(fake.urls[-1]).path.endswith("/101,102,103"))
        self.assertEqual(next(row for row in result["quotes"] if row["item_id"] == 99)["fetched_at_utc"],
                         next(row for row in original["quotes"] if row["item_id"] == 99)["fetched_at_utc"])
        dc = batch.fetch([99], "Light")
        self.assertEqual(dc["cache_hits"], 0)
        self.assertEqual(dc["location"]["kind"], "data_center")

    def test_new_lookups_never_reuse_item_observations_older_than_sixty_seconds(self):
        fake = Transport()
        tools = GameDataTools(True, transport=fake)
        batch = MarketBatch(tools)
        batch.fetch([2], "Alpha")
        value = next(iter(tools._market_quote_cache.memory.values()))
        old = (datetime.now(timezone.utc) - timedelta(seconds=61)).isoformat()
        value["fetched_at_utc"] = value["quote"]["fetched_at_utc"] = old
        result = batch.fetch([2], "Alpha")
        self.assertEqual(result["cache_hits"], 0)
        self.assertEqual(result["request_count"], 1)
        self.assertNotEqual(result["quotes"][0]["fetched_at_utc"], old)

    def test_sqlite_cache_is_bounded_and_corruption_is_an_optional_cache_miss(self):
        with tempfile.TemporaryDirectory() as directory:
            fake = Transport()
            with patch("market_batch.MAX_QUOTE_CACHE_ITEMS", 8):
                result = MarketBatch(GameDataTools(True, directory, transport=fake)).fetch(list(range(1, 13)), "Alpha")
            self.assertIsNone(result["error"])
            path = Path(directory) / "market-quotes-v3.sqlite3"
            self.assertLessEqual(path.stat().st_size, MAX_QUOTE_DB_BYTES)
            with closing(sqlite3.connect(path)) as connection:
                self.assertEqual(connection.execute("SELECT COUNT(*) FROM quotes").fetchone()[0], 8)
                self.assertEqual(connection.execute("PRAGMA user_version").fetchone()[0], 3)
                connection.execute("PRAGMA user_version=99")
            result = MarketBatch(GameDataTools(True, directory, transport=fake)).fetch([12], "Alpha")
            self.assertIsNone(result["error"])
            self.assertEqual(result["cache_hits"], 0)

    def test_queue_cache_recheck_can_reduce_batch_to_unresolved_singleton(self):
        fake = Transport()
        tools = GameDataTools(True, transport=fake)
        batch = MarketBatch(tools)
        batch.fetch([2], "Alpha")
        original_get = _QuoteCache.get_many
        calls = [0]
        def race(cache, ids, location, deadline=None):
            calls[0] += 1
            # The initial prepass misses; queue admission sees another process's
            # newly published observation for item 2.
            return [] if calls[0] == 1 else original_get(cache, ids, location, deadline)
        def missing(*args):
            if urlsplit(args[0]).path.endswith("/3"):
                raise HTTPError(args[0], 404, "missing", {}, None)
            return fake(*args)
        tools.transport = missing
        with patch.object(_QuoteCache, "get_many", race):
            result = batch.fetch([2, 3], "Alpha")
        self.assertIsNone(result["error"])
        self.assertEqual(result["completed_count"], 2)
        self.assertEqual(result["cache_hits"], 1)
        self.assertEqual(result["unresolved_item_ids"], [3])
        self.assertEqual([row["item_id"] for row in result["quotes"]], [2])
        repeated = batch.fetch([2, 3], "Alpha")
        self.assertEqual(repeated["request_count"], 0)
        self.assertEqual(repeated["cache_hits"], 2)


if __name__ == "__main__":
    main()
