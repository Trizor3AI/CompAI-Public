"""Explicit continuation with synthetic snapshots and transports only."""
from contextlib import closing
from datetime import datetime, timedelta
import hashlib
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest
from unittest.mock import patch
from urllib.parse import urlsplit

from game_data_tools import GameDataTools
from saved_item_valuation import SavedItemValuation
from saved_item_valuation_check import Local, SUMMARY, Transport, arguments
from valuation_checkpoint import MAX_FILES, TTL_SECONDS, ValuationCheckpoints, options_digest


class Checks(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        for name, value in (("market_request_queue.START_INTERVAL", 0),
                            ("market_request_queue.DEFAULT_COOLDOWN", 0)):
            patcher = patch(name, value)
            patcher.start()
            self.addCleanup(patcher.stop)

    def local(self, identity="a" * 64, changed=False):
        local = Local([[i, i, 0, 1] for i in range(1, 201)])
        def mutate(value):
            if value["view"] == "candidates":
                value["resume_identity"] = identity
                value["carried_changed"] = changed
        local.mutate = mutate
        return local

    def tool(self, *, allowed=False, identity="a" * 64, changed=False, fail_batch=None):
        transport = Transport()
        transport.fail_batch = fail_batch
        public = GameDataTools(True, self.directory, transport=transport)
        public.resume_pricing_allowed = allowed
        local = self.local(identity, changed)
        return SavedItemValuation(SUMMARY, local, public), transport, local

    def partial(self):
        tool, transport, local = self.tool(fail_batch=2)
        result = tool.query(arguments())
        self.assertTrue(result["continuation"]["available"])
        self.assertEqual(result["coverage"]["failed_item_ids"], 100)
        self.assertEqual(transport.batches, 2)
        return tool, result

    def checkpoint(self):
        return next((self.directory / "valuation-resume-v1").glob("*.json"))

    @staticmethod
    def age_observation(value, seconds):
        def earlier(stamp):
            return (datetime.fromisoformat(stamp) - timedelta(seconds=seconds)).isoformat()
        value["fetched_at_utc"] = earlier(value["fetched_at_utc"])
        quote = value["quote"]
        if quote is not None:
            quote["fetched_at_utc"] = value["fetched_at_utc"]
            for quality in ("sales_nq", "sales_hq"):
                for field in ("earliest_sale_at_utc", "latest_sale_at_utc"):
                    if quote[quality][field] is not None:
                        quote[quality][field] = earlier(quote[quality][field])

    def evidence_path(self, state=None):
        state = state or json.loads(self.checkpoint().read_text())
        return self.directory / "market-scan-evidence-v1" / (state["evidence_id"] + ".json")

    def write_evidence(self, evidence, state=None):
        state = state or json.loads(self.checkpoint().read_text())
        payload = json.dumps(evidence, separators=(",", ":")).encode()
        self.evidence_path(state).write_bytes(payload)
        state["evidence_sha256"] = hashlib.sha256(payload).hexdigest()
        self.checkpoint().write_text(json.dumps(state))

    def age_scan(self, seconds=65):
        state = json.loads(self.checkpoint().read_text())
        state["started_at"] -= seconds
        state["expires_at"] -= seconds
        evidence = json.loads(self.evidence_path(state).read_text())
        for value in evidence["observations"]:
            self.age_observation(value, seconds)
        self.write_evidence(evidence, state)
        with closing(sqlite3.connect(self.directory / "market-quotes-v3.sqlite3")) as connection:
            with connection:
                rows = connection.execute("SELECT key,fetched,payload FROM quotes").fetchall()
                for key, fetched, payload in rows:
                    value = json.loads(payload)
                    self.age_observation(value, seconds)
                    connection.execute("UPDATE quotes SET fetched=?,payload=? WHERE key=?",
                                       (fetched - seconds, json.dumps(value), key))
        return evidence

    def test_explicit_resume_reuses_fresh_quotes_and_finishes_all_candidates(self):
        _, original = self.partial()
        resumed, transport, local = self.tool(allowed=True)
        result = resumed.query(arguments(resume=True))
        self.assertTrue(result["coverage"]["all_market_groups_priced"])
        self.assertEqual(result["coverage"]["priced_groups"], 200)
        self.assertEqual(result["coverage"]["cached_item_quotes"], 100)
        self.assertEqual(transport.batches, 1)
        self.assertEqual(result["rows"][0]["item_id"], 200)
        self.assertTrue(result["continuation"]["resumed"])
        self.assertFalse(result["continuation"]["available"])
        self.assertEqual(len(local.calls), 2)
        self.assertEqual(list((self.directory / "valuation-resume-v1").glob("*.json")), [])
        self.assertNotIn("resume_identity", json.dumps(result))

    def test_resume_without_explicit_current_user_authorization_stops_before_callback(self):
        self.partial()
        tool, transport, local = self.tool()
        result = tool.query(arguments(resume=True))
        self.assertFalse(result["ok"])
        self.assertEqual(local.calls, [])
        self.assertEqual(transport.urls, [])
        self.assertFalse(tool.query(arguments())["ok"])

    def test_explicit_continuation_cannot_silently_start_fresh_if_model_omits_flag(self):
        self.partial()
        self.checkpoint().unlink()
        tool, transport, _ = self.tool(allowed=True)
        self.assertFalse(tool.query(arguments())["ok"])
        self.assertEqual(transport.urls, [])

    def test_expired_checkpoint_requires_fresh_scan_without_network(self):
        self.partial()
        path = self.checkpoint()
        ledger = self.evidence_path()
        state = json.loads(path.read_text())
        state["started_at"] -= TTL_SECONDS + 1
        state["expires_at"] -= TTL_SECONDS + 1
        path.write_text(json.dumps(state))
        tool, transport, _ = self.tool(allowed=True)
        self.assertFalse(tool.query(arguments(resume=True))["ok"])
        self.assertEqual(transport.urls, [])
        self.assertFalse(path.exists())
        self.assertFalse(ledger.exists())

    def test_changed_character_permissions_or_content_digest_requires_fresh_scan(self):
        self.partial()
        tool, transport, local = self.tool(allowed=True, identity="b" * 64)
        self.assertFalse(tool.query(arguments(resume=True))["ok"])
        self.assertEqual(len(local.calls), 1)
        self.assertEqual(transport.urls, [])

    def test_changed_options_require_fresh_scan(self):
        self.partial()
        for changes in ({"world_or_dc": "Light"}, {"rank_by": "total_value"},
                        {"categories": ["retainers"]}):
            tool, transport, _ = self.tool(allowed=True)
            self.assertFalse(tool.query(arguments(resume=True, **changes))["ok"])
            self.assertEqual(transport.urls, [])

    def test_continuation_can_change_display_limit_without_changing_scan_evidence(self):
        self.partial()
        tool, transport, _ = self.tool(allowed=True)
        result = tool.query(arguments(resume=True, limit=1))
        self.assertTrue(result["continuation"]["resumed"])
        self.assertTrue(result["coverage"]["all_market_groups_priced"])
        self.assertEqual(result["coverage"]["priced_groups"], 200)
        self.assertEqual(result["coverage"]["cached_item_quotes"], 100)
        self.assertEqual(len(result["rows"]), 1)
        self.assertEqual(result["rows"][0]["item_id"], 200)
        self.assertEqual(transport.batches, 1)

    def test_known_changed_carried_snapshot_cannot_resume(self):
        self.partial()
        tool, transport, _ = self.tool(allowed=True, changed=True)
        self.assertFalse(tool.query(arguments(resume=True))["ok"])
        self.assertEqual(transport.urls, [])

    def test_explicit_resume_retains_dated_observations_and_only_fetches_unfinished_batch(self):
        self.partial()
        evidence = self.age_scan()
        tool, transport, _ = self.tool(allowed=True)
        # Keep an old observation as the top result so original dating is visible.
        transport.mutate = lambda value: [row.update(minPriceNQ=1, minPriceHQ=1)
                                         for row in value.get("items", {}).values()]
        result = tool.query(arguments(resume=True))
        self.assertTrue(result["coverage"]["all_market_groups_priced"])
        self.assertEqual(result["coverage"]["cached_item_quotes"], 100)
        self.assertEqual(result["coverage"]["retained_item_observations"], 100)
        self.assertEqual(result["rows"][0]["item_id"], 100)
        self.assertEqual(result["rows"][0]["quote_fetched_at_utc"],
                         evidence["observations"][99]["fetched_at_utc"])
        self.assertTrue(result["rows"][0]["retained_observation"])
        self.assertTrue(result["rows"][0]["possibly_outdated"])
        self.assertGreaterEqual(result["freshness"]["oldest_observation_age_seconds"], 65)
        batches = [urlsplit(url).path.rsplit("/", 1)[1] for url in transport.urls if "/402/" in url]
        self.assertTrue(batches[0].startswith("101,"))
        self.assertEqual(len(batches), 1)

    def test_fresh_scan_does_not_use_continuation_position(self):
        self.partial()
        self.age_scan()
        tool, transport, _ = self.tool()
        result = tool.query(arguments())
        self.assertTrue(result["coverage"]["all_market_groups_priced"])
        batches = [urlsplit(url).path.rsplit("/", 1)[1] for url in transport.urls if "/402/" in url]
        self.assertTrue(batches[0].startswith("1,"))
        self.assertTrue(batches[1].startswith("101,"))
        self.assertEqual(result["coverage"]["cached_item_quotes"], 0)
        self.assertEqual(result["coverage"]["retained_item_observations"], 0)
        self.assertNotIn("continuation", result)

    def test_another_429_keeps_prior_valid_quotes_and_original_fetch_time(self):
        _, first = self.partial()
        tool, transport, _ = self.tool(allowed=True, fail_batch=1)
        result = tool.query(arguments(resume=True))
        self.assertFalse(result["coverage"]["all_market_groups_priced"])
        self.assertEqual(result["coverage"]["priced_groups"], 100)
        self.assertEqual(result["coverage"]["failed_item_ids"], 100)
        self.assertEqual(result["rows"][0]["quote_fetched_at_utc"], first["rows"][0]["quote_fetched_at_utc"])
        self.assertEqual(transport.batches, 1)
        self.assertTrue(result["continuation"]["available"])

    def test_resumption_keeps_existing_question_budget_and_honest_partial_coverage(self):
        self.partial()
        tool, transport, _ = self.tool(allowed=True)
        clock = [10.0]
        tool.public_tools.question_deadline = 50.0
        def delayed(*args):
            value = transport(*args)
            if "/402/" in args[0]:
                clock[0] = 43.0  # Past the pricing reservation, before question deadline.
            return value
        tool.public_tools.transport = delayed
        with patch("market_batch.time.monotonic", side_effect=lambda: clock[0]):
            result = tool.query(arguments(resume=True))
        self.assertEqual(result["coverage"]["market_error"]["code"], "timeout")
        self.assertFalse(result["coverage"]["all_market_groups_priced"])
        self.assertEqual(result["coverage"]["priced_groups"], 100)
        self.assertEqual(result["coverage"]["failed_item_ids"], 100)
        self.assertEqual(transport.batches, 1)
        self.assertTrue(result["continuation"]["available"])

    def test_one_valuation_per_question_cannot_be_bypassed_with_resume(self):
        tool, _ = self.partial()
        tool.public_tools.resume_pricing_allowed = True
        before = len(tool.public_tools.transport.urls)
        self.assertFalse(tool.query(arguments(resume=True))["ok"])
        self.assertEqual(len(tool.public_tools.transport.urls), before)

    def test_checkpoints_contain_only_hashes_progress_and_times_and_are_bounded(self):
        self.partial()
        state = json.loads(self.checkpoint().read_text())
        self.assertEqual(set(state), {"version", "binding", "options", "next_batch", "started_at",
                                     "expires_at", "evidence_id", "evidence_sha256"})
        self.assertNotIn("PRIVATE", self.checkpoint().read_text())
        ledger = self.evidence_path().read_text()
        self.assertNotIn("PRIVATE", ledger)
        self.assertNotIn('"locations"', ledger)
        self.assertNotIn('"rows"', ledger)
        self.assertNotIn('"snapshot_id"', ledger)
        store = ValuationCheckpoints(self.directory)
        leftover = store.directory / ("f" * 64 + ".tmp")
        leftover.write_text("synthetic interrupted write")
        unrelated = store.directory / "unrelated.tmp"
        unrelated.write_text("keep unrelated files")
        ledger_leftover = store.evidence_directory / ("f" * 64 + ".tmp")
        ledger_leftover.write_text("synthetic interrupted ledger")
        unrelated_ledger = store.evidence_directory / "unrelated.tmp"
        unrelated_ledger.write_text("keep unrelated files")
        for index in range(MAX_FILES + 2):
            self.assertIsNotNone(store.save(format(index, "064x"), options_digest(arguments()), 1))
        self.assertLessEqual(len(list(store.directory.glob("*.json"))), MAX_FILES)
        self.assertLessEqual(len(list(store.evidence_directory.glob("*.json"))), MAX_FILES)
        self.assertFalse(leftover.exists())
        self.assertFalse(ledger_leftover.exists())
        self.assertTrue(unrelated.exists())
        self.assertTrue(unrelated_ledger.exists())

    def test_corrupt_checkpoint_fails_closed_without_network(self):
        self.partial()
        self.checkpoint().write_text('{"corrupt":true}')
        tool, transport, _ = self.tool(allowed=True)
        self.assertFalse(tool.query(arguments(resume=True))["ok"])
        self.assertEqual(transport.urls, [])

    def test_missing_or_modified_evidence_fails_closed_without_network(self):
        self.partial()
        path = self.evidence_path()
        for missing in (False, True):
            if missing:
                path.unlink()
            else:
                path.write_text("{}")
            tool, transport, _ = self.tool(allowed=True)
            self.assertFalse(tool.query(arguments(resume=True))["ok"])
            self.assertEqual(transport.urls, [])

    def test_ledger_extra_item_is_rejected_even_with_matching_file_hash(self):
        self.partial()
        evidence = json.loads(self.evidence_path().read_text())
        evidence["observations"][0]["item_id"] = 999
        evidence["observations"][0]["quote"]["item_id"] = 999
        self.write_evidence(evidence)
        tool, transport, _ = self.tool(allowed=True)
        self.assertFalse(tool.query(arguments(resume=True))["ok"])
        self.assertEqual(transport.urls, [])

    def test_retained_observation_is_not_overwritten_by_newer_public_cache(self):
        _, original = self.partial()
        self.age_scan()
        state = json.loads(self.checkpoint().read_text())
        evidence = json.loads(self.evidence_path(state).read_text())
        aged = evidence["observations"][99]["fetched_at_utc"]
        # Another fresh scan in a different process refreshes the public cache.
        from market_batch import MarketBatch
        MarketBatch(GameDataTools(True, self.directory, transport=Transport())).fetch([100], "Alpha")
        tool, transport, _ = self.tool(allowed=True, fail_batch=1)
        result = tool.query(arguments(resume=True))
        self.assertEqual(result["rows"][0]["item_id"], 100)
        self.assertEqual(result["rows"][0]["quote_fetched_at_utc"], aged)
        self.assertNotEqual(aged, original["rows"][0]["quote_fetched_at_utc"])
        self.assertFalse(result["coverage"]["all_market_groups_priced"])
        updated = json.loads(self.checkpoint().read_text())
        self.assertEqual(updated["started_at"], state["started_at"])
        self.assertEqual(updated["expires_at"], state["expires_at"])


if __name__ == "__main__":
    unittest.main()
