"""Synthetic public plan normalization, request authorization and caching. No live requests."""
from copy import deepcopy
import json
from tempfile import TemporaryDirectory
import unittest
from urllib.parse import parse_qs, urlsplit

import gear_plan_lookup as gear
from game_data_tools import GameDataTools, _validate_url, validate_result
from companion_provider import CompanionFailure
from companion_request import normalize_request
from companion_tools import build_registry
from shared_tools_integration_check import request

SHARE = "11111111-2222-3333-4444-555555555555"
URL = "https://xivgear.app/sl/" + SHARE
OTHER = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"


def plan():
    return {"name": "Synthetic plan", "job": "MNK", "level": 100, "partyBonus": 5,
            "food": 123, "description": "UNTRUSTED_DESCRIPTION", "sims": [{"secret": "OMIT_SIM"}],
            "items": {"Weapon": {"id": 101, "materia": [{"id": 201}, {"id": -1}],
                                 "relicStats": {"crit": 447, "skillspeed": 108}},
                      "Head": {"id": 102, "materia": [{"id": 201}]}}}


class Transport:
    def __init__(self):
        self.base = plan()
        self.urls = []
        self.mismatch = False
        self.computed_settings = {}

    def __call__(self, url, timeout, maximum):
        self.urls.append(url)
        if urlsplit(url).path == "/fulldata":
            index = int(parse_qs(urlsplit(url).query)["onlySetIndex"][0])
            row = deepcopy(self.base.get("sets", [self.base])[index])
            if self.mismatch: row["items"]["Head"]["id"] = 999
            row["computedStats"] = {"job": row.get("jobOverride") or "MNK", "level": 100, "skillspeed": 958, "crit": 3300,
                                    "effectiveFoodBonuses": {"crit": 91}, "effectiveMedicineBonuses": {"strength": 200} if row.get("medicine") else {},
                                    "jobStats": {"text": "OMIT_NESTED"}}
            value = {"job": "MNK", "level": 100, "partyBonus": 5, "sets": [row]}
            value.update(self.computed_settings)
        else:
            value = self.base
        return json.dumps(value).encode()


def tool(fake, directory=None, online=True):
    service = GameDataTools(online, cache_dir=directory, transport=fake)
    service.allowed_gear_plan_ids = frozenset({SHARE})
    return service


def args(stats=False, index=None, url=URL):
    return {"url": url, "set_index": index, "include_stats": stats}


class Checks(unittest.TestCase):
    def test_known_share_formats_and_indices(self):
        for url in (URL, "https://xivgear.app/#/sl/" + SHARE,
                    "https://xivgear.app/?page=sl%7C" + SHARE,
                    "https://share.xivgear.app/share/" + SHARE):
            self.assertEqual(gear.parse_share_url(url), (SHARE, None))
        self.assertEqual(gear.parse_share_url(URL + "?onlySetIndex=2"), (SHARE, 2))
        self.assertEqual(gear.share_ids_in_texts(["Compare " + URL + ".", "https://example.com/" + SHARE]), {SHARE})

    def test_host_path_and_query_boundaries(self):
        invalid = [URL.replace("https:", "http:"), URL.replace("xivgear.app", "xivgear.app.evil.test"),
                   URL.replace("xivgear.app", "user@xivgear.app"), URL.replace("xivgear.app", "xivgear.app:443"),
                   URL + "?secret=private", URL + "?onlySetIndex=1&onlySetIndex=2", URL + "#other",
                   URL + "/../" + OTHER, URL + "\n", URL + "?onlySetIndex=-1",
                   URL + "?page=sl|" + OTHER, URL + "?onlySetIndex=1&selectedIndex=2"]
        for url in invalid:
            with self.subTest(url=url), self.assertRaises(ValueError): gear.parse_share_url(url)
        for url in ("https://api.xivgear.app/putset", "https://api.xivgear.app/shortlink/" + SHARE + "?x=1",
                    "https://api.xivgear.app/fulldata?url=https%3A%2F%2Flocalhost%2F&onlySetIndex=0"):
            with self.assertRaises(ValueError): _validate_url(url)

    def test_registry_only_exposes_literal_user_share_with_online_access(self):
        payload = request(web_search=True)
        public = tool(Transport())
        readers = {"personality_details": lambda _: {}}
        self.assertNotIn("get_gear_plan", build_registry(normalize_request(payload), readers, public, CompanionFailure))
        payload["question"] = "Compare this reference " + URL
        registry = build_registry(normalize_request(payload), readers, public, CompanionFailure)
        self.assertIn("get_gear_plan", registry)
        self.assertEqual(public.allowed_gear_plan_ids, {SHARE})
        payload["web_search"] = False
        self.assertNotIn("get_gear_plan", build_registry(normalize_request(payload), readers, None, CompanionFailure))

    def test_permission_and_unapproved_share_block_even_cached_reads(self):
        fake = Transport(); service = tool(fake)
        self.assertTrue(service.call("get_gear_plan", args())["ok"])
        service.online_allowed = False
        self.assertEqual(service.call("get_gear_plan", args())["error"]["code"], "online_disabled")
        service.online_allowed = True
        self.assertEqual(service.call("get_gear_plan", args(url=URL.replace(SHARE, OTHER)))["error"]["code"], "unapproved_plan")
        self.assertEqual(len(fake.urls), 1)

    def test_single_reference_filters_nested_payload_and_preserves_materia(self):
        fake = Transport(); result = tool(fake).call("get_gear_plan", args(True))
        self.assertTrue(result["ok"], result)
        selected = result["data"]["selected"]
        self.assertEqual(selected["computed_stats"], {"crit": 3300, "skillspeed": 958})
        self.assertEqual(selected["food_stat_bonuses"], {"crit": 91})
        self.assertEqual(selected["items"][0]["materia_item_ids"], [201, None])
        self.assertEqual(selected["items"][0]["relic_stats"]["skillspeed"], 108)
        self.assertIn("not current equipment", result["data"]["coverage"])
        for omitted in ("UNTRUSTED_DESCRIPTION", "OMIT_SIM", "OMIT_NESTED", "computedStats"):
            self.assertNotIn(omitted, json.dumps(result))
        self.assertEqual(len(fake.urls), 2)
        for url in fake.urls: _validate_url(url)
        self.assertLess(len(json.dumps(result)), 2500)

    def test_multi_job_quality_and_medicine_are_not_lost(self):
        fake = Transport()
        fake.base.update(jobOverride="SAM", medicine=456)
        fake.base["items"]["Head"]["forceNq"] = True
        result = tool(fake).call("get_gear_plan", args(True))
        self.assertTrue(result["ok"], result)
        selected = result["data"]["selected"]
        self.assertEqual(selected["job"], "SAM")
        self.assertEqual(selected["medicine_item_id"], 456)
        self.assertEqual(selected["medicine_stat_bonuses"], {"strength": 200})
        self.assertTrue(selected["items"][1]["force_nq"])

    def test_many_sets_require_selection_and_only_selected_set_is_returned(self):
        fake = Transport(); fake.base = {"job": "MNK", "level": 100, "sets": [plan() for _ in range(25)]}
        service = tool(fake)
        summary = service.call("get_gear_plan", args(True))
        self.assertTrue(summary["ok"]); self.assertIsNone(summary["data"]["selected"])
        self.assertEqual(len(summary["data"]["sets"]), 24); self.assertTrue(summary["data"]["more_sets"])
        self.assertEqual(len(fake.urls), 1, "summary must not trigger computed-stat work")
        selected = service.call("get_gear_plan", args(True, 24))
        self.assertTrue(selected["ok"], selected); self.assertEqual(selected["data"]["selected"]["index"], 24)
        self.assertEqual(len(selected["data"]["selected"]["items"]), 2)

    def test_nullable_job_override_and_effective_settings_are_consistent(self):
        fake = Transport(); fake.base["jobOverride"] = None
        result = tool(fake).call("get_gear_plan", args(True))
        self.assertTrue(result["ok"], result)
        self.assertEqual(result["data"]["selected"]["job"], "MNK")
        fake = Transport(); fake.computed_settings["race"] = "Midlander"
        result = tool(fake).call("get_gear_plan", args(True))
        self.assertTrue(result["ok"], result)
        self.assertEqual(result["data"]["selected"]["race"], "Midlander")
        for field, original, changed in (("race", "Midlander", "Highlander"),
                                         ("partyBonus", 5, 3), ("ilvlSync", 730, 790),
                                         ("race", "Midlander", None)):
            fake = Transport(); fake.base[field] = original; fake.computed_settings[field] = changed
            with self.subTest(field=field, changed=changed):
                result = tool(fake).call("get_gear_plan", args(True))
                self.assertFalse(result["ok"], result)
                self.assertEqual(result["error"]["code"], "unsupported_plan")

    def test_canonical_cache_and_disk_reload_avoid_repeat_network(self):
        with TemporaryDirectory() as directory:
            fake = Transport(); service = tool(fake, directory)
            first = service.call("get_gear_plan", args(True))
            self.assertTrue(first["ok"])
            second = service.call("get_gear_plan", args(True, url="https://share.xivgear.app/share/" + SHARE))
            self.assertTrue(second["cache_hit"])
            third = tool(fake, directory).call("get_gear_plan", args(True))
            self.assertTrue(third["cache_hit"]); self.assertEqual(len(fake.urls), 2)

    def test_custom_mismatched_or_invalid_data_cannot_be_a_calculated_answer(self):
        for modify in (lambda p: p.update(customItems=[{"id": 101}]),
                       lambda p: p["items"]["Weapon"].update(id=-5),
                       lambda p: p.update(food=True)):
            fake = Transport(); modify(fake.base)
            result = tool(fake).call("get_gear_plan", args(True))
            self.assertFalse(result["ok"]); self.assertEqual(result["error"]["code"], "unsupported_plan")
        fake = Transport(); fake.mismatch = True
        self.assertFalse(tool(fake).call("get_gear_plan", args(True))["ok"])
        result = tool(Transport()).call("get_gear_plan", args())
        result["data"]["selected"]["items"][0]["materia_item_ids"] = [True]
        with self.assertRaises(ValueError): validate_result(result)


if __name__ == "__main__": unittest.main()
