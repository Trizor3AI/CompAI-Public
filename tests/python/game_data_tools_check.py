"""Focused offline fixtures for public game-data tools; never contacts either API."""
import copy
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import tempfile
import unittest
from urllib.error import HTTPError
from urllib.parse import parse_qs, urlsplit
from unittest.mock import patch

from game_data_tools import (GameDataTools, MAX_RESPONSE_BYTES, MAX_RESULT_BYTES,
                             _NoRedirect, _validate_url, validate_arguments, validate_result)


MARKET = {
    "itemID": 5121, "worldID": 402, "worldName": "Alpha", "lastUploadTime": 1700000000000,
    "worldUploadTimes": {"402": 1700000000000}, "hasData": True,
    "listingsCount": 42, "recentHistoryCount": 200, "unitsForSale": 300,
    "minPriceNQ": 150, "minPriceHQ": 200,
    "listings": [
        {"pricePerUnit": 150, "quantity": 20, "hq": False, "tax": 150,
         "lastReviewTime": 1700000000, "retainerName": "Do not retain this", "sellerID": "secret"},
        {"pricePerUnit": 200, "quantity": 2, "hq": True, "tax": 20}],
    "recentHistory": [{"pricePerUnit": 160, "quantity": 1, "hq": False,
                       "timestamp": 1699999900, "buyerName": "Do not retain buyer"}],
}


class FakeTransport:
    def __init__(self):
        self.urls = []
        self.market = copy.deepcopy(MARKET)
        self.search = {"results": [{"sheet": "Item", "row_id": 5121, "fields": {"Name": "Darksteel Ore"}}], "next": None}
        self.item = {"row_id": 5121, "fields": {"Name": "Darksteel Ore", "Description": "A metal.",
                                              "LevelItem@as(raw)": 55, "LevelEquip": 0,
                                              "CanBeHq": True, "IsUntradable": False,
                                              "ItemUICategory": {"fields": {"Name": "Stone"}}}}
    def __call__(self, url, timeout, maximum_bytes):
        self.urls.append(url)
        assert timeout == 10 and maximum_bytes == MAX_RESPONSE_BYTES
        path = urlsplit(url).path
        if path.endswith("/worlds"):
            data = [{"id": 402, "name": "Alpha"}]
        elif path.endswith("/data-centers"):
            data = [{"name": "Light", "region": "Europe", "worlds": [402]}]
        elif path == "/api/search":
            data = self.search
        elif path.startswith("/api/sheet/Item/"):
            data = self.item
        else:
            data = self.market
        return json.dumps(data).encode()


class Checks(unittest.TestCase):
    def setUp(self):
        # These validate normalization/cache boundaries; real pacing is covered
        # by the cross-process queue fixtures.
        patcher = patch("market_request_queue.START_INTERVAL", 0)
        patcher.start()
        self.addCleanup(patcher.stop)

    def test_arguments_are_closed_and_typed(self):
        for name, args in [("get_item", {"item_id": True}), ("get_item", {"item_id": 1000001}),
                           ("find_item", {"name": ""}), ("find_item", {"name": "ore\nquery"}),
                           ("get_market_data", {"item_id": 1, "world_or_dc": "Alpha", "url": "https://example.com"})]:
            with self.assertRaises(ValueError):
                validate_arguments(name, args)

    def test_search_escapes_untrusted_grammar_and_limits_results(self):
        transport = FakeTransport()
        tool = GameDataTools(True, transport=transport)
        name = 'Ore" +Secret=true \\ test'
        result = tool.call("find_item", {"name": name})
        self.assertTrue(result["ok"])
        query = parse_qs(urlsplit(transport.urls[0]).query)
        self.assertEqual(query["query"], ["Name~" + json.dumps(name)])
        self.assertEqual(query["fields"], ["Name"])
        self.assertEqual(query["limit"], ["8"])
        self.assertEqual(result["data"]["matches"][0]["item_id"], 5121)

    def test_detail_preserves_unknown_fields(self):
        transport = FakeTransport()
        transport.item["fields"].pop("CanBeHq")
        result = GameDataTools(True, transport=transport).call("get_item", {"item_id": 5121})
        self.assertTrue(result["ok"])
        self.assertIsNone(result["data"]["can_be_hq"])
        self.assertEqual(result["data"]["category"], "Stone")
        self.assertEqual(result["data"]["item_level"], 55)

    def test_market_world_and_quality_provenance(self):
        transport = FakeTransport()
        result = GameDataTools(True, transport=transport).call("get_market_data", {"item_id": 5121, "world_or_dc": "aLPha"})
        self.assertTrue(result["ok"])
        data = result["data"]
        self.assertEqual(data["location"], {"kind": "world", "name": "Alpha", "world_id": 402})
        self.assertEqual(data["minimum_asking_price_per_unit"], {"nq": 150, "hq": 200})
        self.assertEqual(len(data["nq_listings"]), 1)
        self.assertEqual(len(data["hq_listings"]), 1)
        self.assertEqual(data["listing_count"], 42)
        self.assertNotIn("Do not retain", json.dumps(result))
        self.assertNotEqual(data["upstream_updated_at_utc"], result["fetched_at_utc"])
        self.assertLess(len(json.dumps(result).encode()), MAX_RESULT_BYTES)
        self.assertEqual(len(transport.urls), 2)
        self.assertIn("/402/5121?", result["source_url"])

    def test_dc_resolution_and_unknown_location(self):
        transport = FakeTransport()
        transport.market.pop("worldID")
        transport.market["dcName"] = "Light"
        tool = GameDataTools(True, transport=transport)
        result = tool.call("get_market_data", {"item_id": 5121, "world_or_dc": "light"})
        self.assertTrue(result["ok"])
        self.assertEqual(result["data"]["location"]["kind"], "data_center")
        calls = len(transport.urls)
        result = tool.call("get_market_data", {"item_id": 5121, "world_or_dc": "my home world"})
        self.assertEqual(result["error"]["code"], "unknown_location")
        self.assertEqual(len(transport.urls), calls)  # Cached official lists, no guessed market endpoint.

    def test_unknown_prices_never_become_zero_quote(self):
        transport = FakeTransport()
        transport.market.update(hasData=False, lastUploadTime=0, minPriceNQ=0, minPriceHQ=0,
                                listings=[], recentHistory=[], worldUploadTimes={})
        result = GameDataTools(True, transport=transport).call("get_market_data", {"item_id": 5121, "world_or_dc": "402"})
        self.assertTrue(result["ok"])
        self.assertEqual(result["data"]["minimum_asking_price_per_unit"], {"nq": None, "hq": None})
        self.assertIsNone(result["data"]["upstream_updated_at_utc"])

    def test_integral_float_minimum_is_supported_without_rounding(self):
        for minimum, ok in ((500.0, True), (500.5, False), (float("inf"), False), (True, False)):
            fake = FakeTransport()
            fake.market["minPriceNQ"] = minimum
            result = GameDataTools(True, transport=fake).call("get_market_data", {"item_id": 5121, "world_or_dc": "Alpha"})
            self.assertEqual(result["ok"], ok)
            if ok:
                self.assertIs(type(result["data"]["minimum_asking_price_per_unit"]["nq"]), int)

    def test_offline_and_cancel_prevent_network_even_with_cache(self):
        transport = FakeTransport()
        tool = GameDataTools(True, transport=transport)
        args = {"item_id": 5121}
        self.assertTrue(tool.call("get_item", args)["ok"])
        tool.online_allowed = False
        self.assertEqual(tool.call("get_item", args)["error"]["code"], "online_disabled")
        self.assertEqual(len(transport.urls), 1)
        tool.online_allowed = True
        tool.cancelled = lambda: True
        self.assertEqual(tool.call("get_item", args)["error"]["code"], "cancelled")
        self.assertEqual(len(transport.urls), 1)

    def test_revocation_between_location_and_market_stops_second_request(self):
        fake = FakeTransport()
        tool = GameDataTools(True)
        def transport(*args):
            value = fake(*args)
            tool.online_allowed = False
            return value
        tool.transport = transport
        self.assertEqual(tool.call("get_market_data", {"item_id": 5121, "world_or_dc": "Alpha"})["error"]["code"], "online_disabled")
        self.assertEqual(len(fake.urls), 1)

    def test_cache_persistence_ttl_and_safe_cache_failure(self):
        with tempfile.TemporaryDirectory() as directory:
            fake = FakeTransport()
            args = {"item_id": 5121}
            self.assertTrue(GameDataTools(True, directory, transport=fake).call("get_item", args)["ok"])
            tool = GameDataTools(True, directory, transport=fake)
            self.assertTrue(tool.call("get_item", args)["cache_hit"])
            self.assertEqual(len(fake.urls), 1)
            file = next(Path(directory).glob("*.json"))
            payload = json.loads(file.read_text())
            payload["stored_at"] = (datetime.now(timezone.utc) - timedelta(days=2)).isoformat()
            file.write_text(json.dumps(payload))
            self.assertFalse(GameDataTools(True, directory, transport=fake).call("get_item", args)["cache_hit"])
            self.assertEqual(len(fake.urls), 2)
            file.write_text('{"corrupted":true}')
            self.assertTrue(GameDataTools(True, directory, transport=fake).call("get_item", args)["ok"])
            # An unwritable/non-directory cache should not spoil a valid public response.
            self.assertTrue(GameDataTools(True, file, transport=fake).call("get_item", args)["ok"])

    def test_corrupt_cache_identity_is_not_reused(self):
        with tempfile.TemporaryDirectory() as directory:
            fake = FakeTransport()
            args = {"item_id": 5121, "world_or_dc": "Alpha"}
            result = GameDataTools(True, directory, transport=fake).call("get_market_data", args)
            self.assertTrue(result["ok"])
            file = next(p for p in Path(directory).glob("*.json") if json.loads(p.read_text())["key"].startswith("get_market_data:"))
            for mutate in (lambda r: r["data"].update(item_id=1),
                           lambda r: r["data"]["location"].update(name="Odin", world_id=66),
                           lambda r: r.update(tool="get_item")):
                payload = json.loads(file.read_text())
                mutate(payload["data"])
                file.write_text(json.dumps(payload))
                calls = len(fake.urls)
                value = GameDataTools(True, directory, transport=fake).call("get_market_data", args)
                self.assertTrue(value["ok"])
                self.assertFalse(value["cache_hit"])
                self.assertEqual(len(fake.urls), calls + 1)

    def test_nullable_or_malformed_item_category(self):
        for category, ok in ((None, True), ([], False), ({"fields": []}, False)):
            fake = FakeTransport()
            fake.item["fields"]["ItemUICategory"] = category
            result = GameDataTools(True, transport=fake).call("get_item", {"item_id": 5121})
            self.assertEqual(result["ok"], ok)

    def test_fixed_urls_and_redirects(self):
        for url in ("http://v2.xivapi.com/api/search", "https://v2.xivapi.com.evil/api/search",
                    "https://user@v2.xivapi.com/api/search", "https://universalis.app:444/api/v2/worlds",
                    "https://universalis.app/api/v2/lists/private", "https://v2.xivapi.com/api/search?custom=x"):
            with self.assertRaises(ValueError):
                _validate_url(url)
        with self.assertRaisesRegex(RuntimeError, "redirect"):
            _NoRedirect().redirect_request(None, None, 302, "", {}, "https://evil.invalid/")

    def test_failures_are_structured_not_exception_or_hallucinated_data(self):
        cases = [
            (lambda *args: b"x" * (MAX_RESPONSE_BYTES + 1), "invalid_response"),
            (lambda *args: b'{"bad":"shape"}', "invalid_response"),
            (lambda *args: b'[]', "invalid_response"),
            (lambda *args: b'not json', "invalid_response"),
        ]
        for transport, expected in cases:
            value = GameDataTools(True, transport=transport).call("get_item", {"item_id": 5121})
            self.assertEqual(value["error"]["code"], expected)
            self.assertIsNone(value["data"])
            validate_result(value)
        def http_error(*args):
            raise HTTPError(args[0], 429, "rate limited", {}, None)
        value = GameDataTools(True, transport=http_error).call("get_item", {"item_id": 5121})
        self.assertEqual(value["error"]["code"], "rate_limited")

    def test_malformed_market_quality_and_identity_are_rejected(self):
        for change in (lambda x: x.update(itemID=1), lambda x: x["listings"][0].update(hq="false")):
            fake = FakeTransport()
            change(fake.market)
            value = GameDataTools(True, transport=fake).call("get_market_data", {"item_id": 5121, "world_or_dc": "Alpha"})
            self.assertEqual(value["error"]["code"], "invalid_response")


if __name__ == "__main__":
    unittest.main()
