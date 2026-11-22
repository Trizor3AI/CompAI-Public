"""Synthetic Windows subprocess coordination checks; no network or user profiles."""
from datetime import datetime, timezone
from email.utils import format_datetime
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest
from urllib.error import HTTPError
from urllib.parse import urlsplit

from game_data_tools import GameDataTools
from market_batch import MarketBatch
from market_request_queue import (DEFAULT_COOLDOWN, START_INTERVAL,
                                  MarketRequestQueue, retry_after_seconds)

ROOT = Path(__file__).resolve().parents[2]


def worker(directory, mode, token, budget):
    directory = Path(directory)
    events = directory / "events.jsonl"
    def transport(url, timeout, maximum_bytes):
        path = urlsplit(url).path
        with events.open("a", encoding="utf-8") as stream:
            stream.write(json.dumps({"token": token, "path": path, "start": time.monotonic()}) + "\n")
        (directory / ("entered-" + token)).touch()
        time.sleep(.08)
        if path.endswith("/worlds"):
            value = [{"id": 402, "name": "Alpha"}]
        elif path.endswith("/marketable"):
            value = [1, 2, 3]
        elif mode == "limited":
            raise HTTPError(url, 429, "synthetic", {"Retry-After": "2"}, None)
        else:
            item_id = int(path.rsplit("/", 1)[1])
            value = {"itemID": item_id, "worldID": 402, "lastUploadTime": int(time.time() * 1000),
                     "hasData": True, "minPriceNQ": 120, "minPriceHQ": 150,
                     "listings": [], "recentHistory": [], "buyerName": "never persist this wire field"}
        return json.dumps(value).encode()
    tools = GameDataTools(True, directory, transport=transport,
                          cancelled=lambda: (directory / ("cancel-" + token)).exists())
    tools.market_queue.interval = .12
    tools.question_deadline = time.monotonic() + float(budget)
    if mode == "hold":
        with tools.market_queue.locked(tools._gate, tools.question_deadline):
            (directory / ("entered-" + token)).touch()
            while not (directory / ("release-" + token)).exists():
                tools._gate()
                time.sleep(.02)
        value = {"released": True}
    elif mode == "batch":
        # Batch reserves eight seconds of the overall question budget.
        value = MarketBatch(tools).fetch([2], "Alpha")
    else:
        value = tools.call("get_market_data", {"item_id": 1, "world_or_dc": "Alpha"})
    print(json.dumps(value))


class Checks(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.directory = Path(self.temp.name)
        self.processes = []
        self.addCleanup(self.cleanup)

    def cleanup(self):
        for process in self.processes:
            if process.poll() is None:
                process.kill()
            process.communicate(timeout=5)
        self.temp.cleanup()

    def start(self, mode, token, budget=12):
        process = subprocess.Popen([sys.executable, "-B", str(ROOT / "tools/run.py"),
                                    "market_request_queue_check", "--worker",
                                    str(self.directory), mode, token, str(budget)],
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
                                   creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        self.processes.append(process)
        return process

    def entered(self, token):
        deadline = time.monotonic() + 4
        while not (self.directory / ("entered-" + token)).exists():
            self.assertLess(time.monotonic(), deadline, "synthetic worker did not enter")
            time.sleep(.01)

    def result(self, process):
        stdout, stderr = process.communicate(timeout=6)
        self.assertEqual(process.returncode, 0, stderr)
        return json.loads(stdout)

    def events(self):
        path = self.directory / "events.jsonl"
        return [json.loads(line) for line in path.read_text().splitlines()] if path.exists() else []

    def test_retry_after_delta_http_date_and_invalid_values(self):
        now = datetime(2026, 1, 1, tzinfo=timezone.utc).timestamp()
        self.assertEqual(START_INTERVAL, .5)
        self.assertEqual(retry_after_seconds("120", now), 120)
        self.assertEqual(retry_after_seconds("86400", now), 86400)
        self.assertEqual(retry_after_seconds("9" * 128, now), int("9" * 128))
        stamp = format_datetime(datetime.fromtimestamp(now + 37, timezone.utc), usegmt=True)
        self.assertEqual(retry_after_seconds(stamp, now), 37)
        past = format_datetime(datetime.fromtimestamp(now - 1, timezone.utc), usegmt=True)
        self.assertEqual(retry_after_seconds(past, now), 0)
        for value in (None, "", "-1", "1.5", "NaN", "tomorrow", "٣", "1" * 129):
            self.assertEqual(retry_after_seconds(value, now), DEFAULT_COOLDOWN)

    def test_simultaneous_identical_ordinary_lookup_uses_one_public_result(self):
        first = self.start("ordinary", "first")
        self.entered("first")
        second = self.start("ordinary", "second")
        a, b = self.result(first), self.result(second)
        self.assertTrue(a["ok"] and b["ok"])
        self.assertFalse(a["cache_hit"])
        self.assertTrue(b["cache_hit"])
        self.assertEqual(a["fetched_at_utc"], b["fetched_at_utc"])
        self.assertEqual(len(self.events()), 2)  # Worlds and one item.
        files = list(self.directory.glob("gamedata-v1-*.json"))
        self.assertTrue(files)
        self.assertNotIn("never persist", "".join(p.read_text() for p in files))

    def test_simultaneous_identical_batch_quotes_are_deduplicated(self):
        first = self.start("batch", "first")
        self.entered("first")
        second = self.start("batch", "second")
        a, b = self.result(first), self.result(second)
        self.assertIsNone(a["error"])
        self.assertIsNone(b["error"])
        self.assertEqual(a["completed_count"], 1)
        self.assertEqual(b["completed_count"], 1)
        quotes = [row for row in self.events() if row["path"].endswith("/2")]
        self.assertEqual(len(quotes), 1)
        self.assertEqual(a["cache_hits"] + b["cache_hits"], 1)

    def test_ordinary_and_batch_share_serial_pacing(self):
        first = self.start("ordinary", "first")
        self.entered("first")
        second = self.start("batch", "second")
        self.assertTrue(self.result(first)["ok"])
        self.assertIsNone(self.result(second)["error"])
        events = self.events()
        self.assertGreaterEqual(len(events), 4)
        for left, right in zip(events, events[1:]):
            self.assertGreaterEqual(right["start"] - left["start"], .105)

    def test_429_cooldown_is_shared_and_never_retries_current_lookup(self):
        first = self.result(self.start("limited", "limited"))
        self.assertEqual(first["error"]["code"], "rate_limited")
        before = len(self.events())
        second = self.result(self.start("batch", "batch", 8.2))
        self.assertEqual(second["error"]["code"], "rate_limited")
        self.assertEqual(second["failed_item_ids"], [2])
        self.assertEqual(second["completed_count"], 0)
        self.assertEqual(second["request_count"], 0)
        self.assertEqual(len(self.events()), before)
        state = json.loads((self.directory / "market-coordination-v1/state.json").read_text())
        self.assertEqual(set(state), {"version", "next_start", "cooldown_until"})
        self.assertGreater(state["cooldown_until"], time.time())

    def test_wait_obeys_question_deadline(self):
        holding = self.start("hold", "holder")
        self.entered("holder")
        started = time.monotonic()
        value = self.result(self.start("ordinary", "waiting", .15))
        self.assertEqual(value["error"]["code"], "timeout")
        self.assertLess(time.monotonic() - started, 2)
        self.assertEqual(self.events(), [])
        (self.directory / "release-holder").touch()
        self.result(holding)

    def test_cancellation_while_waiting_makes_no_request(self):
        holding = self.start("hold", "holder")
        self.entered("holder")
        waiting = self.start("ordinary", "waiting")
        time.sleep(.15)
        (self.directory / "cancel-waiting").touch()
        value = self.result(waiting)
        self.assertEqual(value["error"]["code"], "cancelled")
        self.assertEqual(self.events(), [])
        (self.directory / "release-holder").touch()
        self.result(holding)

    def test_process_death_releases_admission(self):
        holding = self.start("hold", "holder")
        self.entered("holder")
        holding.kill()
        holding.communicate(timeout=3)
        value = self.result(self.start("ordinary", "after"))
        self.assertTrue(value["ok"])

    def test_corrupt_coordination_state_fails_closed(self):
        directory = self.directory / "market-coordination-v1"
        directory.mkdir()
        for content in ('{"version":1,"next_start":NaN,"cooldown_until":0}', '{"corrupt":true}', "x" * 513):
            (directory / "state.json").write_text(content)
            value = self.result(self.start("ordinary", "blocked"))
            self.assertEqual(value["error"]["code"], "unavailable")
            self.assertEqual(self.events(), [])

    def test_permission_revocation_during_pacing_stops_network(self):
        status = []
        tools = GameDataTools(True, self.directory, progress=status.append)
        queue = tools.market_queue
        with queue.locked(tools._gate, time.monotonic() + 1):
            queue._state["next_start"] = time.time() + .2
            calls = 0
            def gate():
                nonlocal calls
                calls += 1
                if calls >= 3:
                    tools.online_allowed = False
                tools._gate()
            with self.assertRaisesRegex(RuntimeError, "Online Access"):
                queue.before_request(gate, time.monotonic() + 1, status.append)
        self.assertTrue(status)

    def test_expired_cooldown_allows_a_later_explicit_request(self):
        queue = MarketRequestQueue(self.directory)
        with queue.locked(lambda: None, time.monotonic() + 1):
            queue._state["cooldown_until"] = time.time() - 1
            queue._write_state()
        value = self.result(self.start("ordinary", "later"))
        self.assertTrue(value["ok"])


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--worker":
        worker(*sys.argv[2:])
    else:
        unittest.main()
