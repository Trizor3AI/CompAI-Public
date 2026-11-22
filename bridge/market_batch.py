"""Bounded public market quotes for locally aggregated inventory candidates.

Only item IDs and an explicitly selected world/DC cross the public network boundary.
The caller owns inventory permissions, locations, grouping and the final ranking.
Official API contract (100 IDs; items.* fields; unresolvedItems):
https://github.com/Universalis-FFXIV/Universalis/blob/v2/src/Universalis.Application/Controllers/V2/CurrentlyShownController.cs
Marketable IDs: https://docs.universalis.app/ (GET /api/v2/marketable).
"""
from collections import OrderedDict
import hashlib
import json
import math
from pathlib import Path
import re
import sqlite3
from statistics import median
import time
import urllib.parse

from game_data_tools import (ERROR_MESSAGES, GameDataError, GameDataTools, MARKET_TTL,
                             STATIC_TTL, UNIVERSALIS, _encoded,
                             _fail, _integer, _keys, _now, _require, _stamp, _text)
from market_request_queue import _unlinked

MAX_ITEMS = 4096
BATCH_SIZE = 100
MAX_REQUESTS = 44  # 41 quote batches, marketable IDs, worlds and data centers.
DEADLINE_SECONDS = 45
REQUEST_TIMEOUT = 8
MAX_MARKETABLE_ITEMS = 65536
SALES_LIMIT = 20
SALES_WINDOW_DAYS = 7
SALES_WINDOW_SECONDS = SALES_WINDOW_DAYS * 86400
MAX_QUOTE_CACHE_ITEMS = 4096
MAX_QUOTE_BYTES = 4096
MAX_QUOTE_DB_BYTES = 32 * 1024 * 1024
QUOTE_DB_APPLICATION = 0x434D5133
QUOTE_FIELDS = ("itemID", "worldID", "dcName", "lastUploadTime", "hasData", "minPriceNQ", "minPriceHQ",
                "recentHistory.pricePerUnit", "recentHistory.quantity", "recentHistory.hq", "recentHistory.timestamp")


def _fields(multi):
    return ",".join(("itemIDs", "worldID", "dcName", "unresolvedItems") +
                    tuple("items." + name for name in QUOTE_FIELDS)) if multi else ",".join(QUOTE_FIELDS)


def _validate_url(url):
    parsed = urllib.parse.urlsplit(url)
    _require(parsed.scheme == "https" and parsed.hostname == "universalis.app" and
             parsed.port is None and parsed.username is None and parsed.password is None and not parsed.fragment)
    if parsed.path in ("/api/v2/worlds", "/api/v2/data-centers", "/api/v2/marketable"):
        _require(not parsed.query)
        return
    match = re.fullmatch(r"/api/v2/([^/]+)/([1-9][0-9]{0,5}(?:,[1-9][0-9]{0,5})*)", parsed.path)
    _require(match is not None)
    location = urllib.parse.unquote(match[1])
    _require(0 < len(location) <= 64 and "/" not in location and not any(ord(c) < 32 for c in location))
    ids = match[2].split(",")
    _require(len(ids) <= BATCH_SIZE and len(set(ids)) == len(ids))
    pairs = urllib.parse.parse_qsl(parsed.query, strict_parsing=True)
    _require(len(pairs) == 4 and dict(pairs) ==
             {"listings": "0", "entries": str(SALES_LIMIT), "entriesWithin": str(SALES_WINDOW_SECONDS),
              "fields": _fields(len(ids) > 1)})


def _price(value):
    if value is None or value == 0 and type(value) in (int, float):
        return None
    if type(value) is float:
        _require(math.isfinite(value) and value.is_integer())
        value = int(value)
    return _integer(value, 1, 2**31 - 1)


def _sales_summaries(raw, fetched_at_utc, has_data):
    """Median uses transactions, not units. Never retain buyer/seller identity fields."""
    _require(isinstance(raw, list) and len(raw) <= SALES_LIMIT)
    fetched_seconds = _stamp(fetched_at_utc).timestamp()
    selected = {False: [], True: []}
    for sale in raw:
        _require(isinstance(sale, dict) and type(sale.get("hq")) is bool)
        price = _price(sale.get("pricePerUnit"))
        _require(price is not None)
        quantity = _integer(sale.get("quantity"), 1, 2**31 - 1)
        timestamp = _integer(sale.get("timestamp"), 1)
        # Filter again locally; an API/cache anomaly must not turn old or future
        # transactions into evidence for a current seven-day estimate.
        if has_data and fetched_seconds - SALES_WINDOW_SECONDS < timestamp <= fetched_seconds:
            selected[sale["hq"]].append((price, quantity, timestamp))
    summaries = []
    for quality in (False, True):
        rows = selected[quality]
        typical = median(row[0] for row in rows) if rows else None
        if isinstance(typical, float) and typical.is_integer():
            typical = int(typical)
        summaries.append({"median_price_per_unit": typical, "sale_count": len(rows),
                          "quantity": sum(row[1] for row in rows),
                          "latest_sale_at_utc": GameDataTools._unix(max(row[2] for row in rows)) if rows else None,
                          "earliest_sale_at_utc": GameDataTools._unix(min(row[2] for row in rows)) if rows else None,
                          "window_days": SALES_WINDOW_DAYS, "sample_limit": SALES_LIMIT,
                          "sample_limit_reached": len(raw) == SALES_LIMIT})
    return summaries


def _validate_sales_summary(value, fetched_at_utc):
    _keys(value, "median_price_per_unit sale_count quantity latest_sale_at_utc earliest_sale_at_utc window_days sample_limit sample_limit_reached")
    count = _integer(value["sale_count"], 0, SALES_LIMIT)
    quantity = _integer(value["quantity"], count, count * (2**31 - 1))
    _require(type(value["window_days"]) is int and value["window_days"] == SALES_WINDOW_DAYS)
    _require(type(value["sample_limit"]) is int and value["sample_limit"] == SALES_LIMIT)
    _require(type(value["sample_limit_reached"]) is bool)
    if not count:
        _require(quantity == 0 and value["median_price_per_unit"] is None and
                 value["latest_sale_at_utc"] is None and value["earliest_sale_at_utc"] is None)
        return
    typical = value["median_price_per_unit"]
    _require(type(typical) in (int, float) and math.isfinite(typical) and 1 <= typical <= 2**31 - 1 and
             float(typical * 2).is_integer())
    first, last = _stamp(value["earliest_sale_at_utc"]), _stamp(value["latest_sale_at_utc"])
    fetched = _stamp(fetched_at_utc)
    _require(fetched.timestamp() - SALES_WINDOW_SECONDS < first.timestamp() <= last.timestamp() <= fetched.timestamp())


def _validate_location(location):
    _keys(location, "kind name world_id")
    _require(location["kind"] in ("world", "data_center"))
    _text(location["name"], 64)
    if location["kind"] == "world":
        _integer(location["world_id"], 1, 99999)
    else:
        _require(location["world_id"] is None)
    return location


def _validate_observation(value, location):
    _keys(value, "version location item_id quote unresolved fetched_at_utc")
    _require(type(value["version"]) is int and value["version"] == 3 and value["location"] == location)
    item_id = _integer(value["item_id"], 1, 999999)
    _stamp(value["fetched_at_utc"])
    _require(type(value["unresolved"]) is bool and len(_encoded(value)) <= MAX_QUOTE_BYTES)
    row = value["quote"]
    if value["unresolved"]:
        _require(row is None)
        return value
    _keys(row, "item_id nq hq upstream_updated_at_utc fetched_at_utc sales_nq sales_hq")
    _require(type(row["item_id"]) is int and row["item_id"] == item_id)
    for field in ("nq", "hq"):
        if row[field] is not None:
            _integer(row[field], 1, 2**31 - 1)
    if row["upstream_updated_at_utc"] is not None:
        _stamp(row["upstream_updated_at_utc"])
    _require(row["fetched_at_utc"] == value["fetched_at_utc"])
    for quality in ("sales_nq", "sales_hq"):
        _validate_sales_summary(row[quality], row["fetched_at_utc"])
    _require(row["sales_nq"]["sale_count"] + row["sales_hq"]["sale_count"] <= SALES_LIMIT)
    _require(row["sales_nq"]["sample_limit_reached"] == row["sales_hq"]["sample_limit_reached"])
    return value


def validate_retained_evidence(value, item_ids, world_or_dc, started_at):
    """Scan history is dated evidence, never a fresh-cache TTL extension."""
    _keys(value, "version location observations")
    _require(type(value["version"]) is int and value["version"] == 1)
    rows = value["observations"]
    _require(isinstance(rows, list) and len(rows) <= MAX_ITEMS)
    if value["location"] is None:
        _require(not rows)
        return value
    location = _validate_location(value["location"])
    needle = world_or_dc.strip().casefold()
    _require(needle == location["name"].casefold() or
             location["kind"] == "world" and needle == str(location["world_id"]))
    allowed, seen = set(item_ids), set()
    now = time.time()
    for row in rows:
        _validate_observation(row, location)
        item_id = row["item_id"]
        _require(item_id in allowed and item_id not in seen)
        seen.add(item_id)
        _require(started_at - MARKET_TTL <= _stamp(row["fetched_at_utc"]).timestamp() <= now)
    return value


class _QuoteCache:
    """Optional bounded item/world cache. Scan history is stored separately."""
    def __init__(self, cache_dir):
        self.path = Path(cache_dir) / "market-quotes-v3.sqlite3" if cache_dir is not None else None
        self.memory = OrderedDict()

    @staticmethod
    def key(item_id, location):
        return hashlib.sha256(_encoded([location, item_id])).hexdigest()

    def _connect(self, write=False, deadline=None):
        _unlinked(self.path)
        for suffix in ("-journal", "-wal", "-shm"):
            _unlinked(Path(str(self.path) + suffix))
        if self.path.exists() and self.path.stat().st_size > MAX_QUOTE_DB_BYTES:
            raise OSError("Public quote cache is oversized")
        if write:
            self.path.parent.mkdir(parents=True, exist_ok=True)
        connection = sqlite3.connect(self.path, timeout=.02)
        try:
            stop = min(deadline if deadline is not None else float("inf"), time.monotonic() + .2)
            connection.set_progress_handler(lambda: int(time.monotonic() >= stop), 1000)
            connection.setlimit(sqlite3.SQLITE_LIMIT_LENGTH, MAX_QUOTE_BYTES * 2)
            schema = connection.execute("SELECT name,type FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'").fetchall()
            if not schema and write:
                connection.execute("PRAGMA journal_mode=DELETE")
                page_size = connection.execute("PRAGMA page_size").fetchone()[0]
                connection.execute("PRAGMA max_page_count=" + str(MAX_QUOTE_DB_BYTES // page_size))
                connection.execute("PRAGMA application_id=" + str(QUOTE_DB_APPLICATION))
                connection.execute("PRAGMA user_version=3")
                connection.execute("CREATE TABLE quotes (key TEXT PRIMARY KEY, fetched REAL NOT NULL, payload TEXT NOT NULL)")
                schema = [("quotes", "table")]
            columns = [(row[1], row[2], row[3], row[5]) for row in connection.execute("PRAGMA table_info(quotes)")]
            if (schema != [("quotes", "table")] or
                    connection.execute("PRAGMA application_id").fetchone()[0] != QUOTE_DB_APPLICATION or
                    connection.execute("PRAGMA user_version").fetchone()[0] != 3 or
                    columns != [("key", "TEXT", 0, 1), ("fetched", "REAL", 1, 0), ("payload", "TEXT", 1, 0)]):
                raise sqlite3.DatabaseError("Unexpected public quote cache schema")
            if write:
                page_size = connection.execute("PRAGMA page_size").fetchone()[0]
                connection.execute("PRAGMA max_page_count=" + str(MAX_QUOTE_DB_BYTES // page_size))
            return connection
        except Exception:
            connection.close()
            raise

    def _remember(self, key, value):
        self.memory[key] = value
        self.memory.move_to_end(key)
        while len(self.memory) > MAX_QUOTE_CACHE_ITEMS:
            self.memory.popitem(last=False)

    def get_many(self, ids, location, deadline=None):
        keys = {self.key(item_id, location): item_id for item_id in ids}
        candidates = {key: self.memory[key] for key in keys if key in self.memory}
        if self.path is not None and self.path.exists():
            connection = None
            try:
                connection = self._connect(deadline=deadline)
                placeholders = ",".join("?" for _ in keys)
                for key, payload in connection.execute(
                        "SELECT key,payload FROM quotes WHERE key IN (" + placeholders +
                        ") AND typeof(payload)='text' AND length(CAST(payload AS BLOB))<=?",
                        [*keys, MAX_QUOTE_BYTES]):
                    if len(payload.encode("utf-8")) <= MAX_QUOTE_BYTES:
                        candidates[key] = json.loads(payload)
            except (sqlite3.Error, OSError, ValueError, RecursionError):
                pass
            finally:
                if connection is not None:
                    connection.close()
        result = []
        for key, value in candidates.items():
            try:
                _validate_observation(value, location)
                _require(value["item_id"] == keys[key] and
                         0 <= time.time() - _stamp(value["fetched_at_utc"]).timestamp() <= MARKET_TTL)
                self._remember(key, value)
                result.append(value)
            except (ValueError, TypeError, KeyError, OverflowError, RecursionError):
                self.memory.pop(key, None)
        return result

    def put_many(self, observations, deadline=None):
        if not observations:
            return
        rows = []
        for value in observations:
            key = self.key(value["item_id"], value["location"])
            self._remember(key, value)
            rows.append((key, _stamp(value["fetched_at_utc"]).timestamp(), _encoded(value).decode("utf-8")))
        if self.path is None:
            return
        connection = None
        try:
            connection = self._connect(write=True, deadline=deadline)
            with connection:
                connection.executemany("INSERT OR REPLACE INTO quotes VALUES (?,?,?)", rows)
                connection.execute("DELETE FROM quotes WHERE key IN "
                                   "(SELECT key FROM quotes ORDER BY fetched DESC,key DESC LIMIT -1 OFFSET ?)",
                                   (MAX_QUOTE_CACHE_ITEMS,))
        except (sqlite3.Error, OSError, ValueError):
            pass  # Public-cache failure never replaces a successful public quote.
        finally:
            if connection is not None:
                connection.close()


class _Session(GameDataTools):
    """Shares the existing gate, metadata/cache and no-redirect opener, with a total deadline."""
    def __init__(self, parent):
        self.parent = parent
        self.cache_dir = parent.cache_dir
        self._cache = parent._cache
        self._opener = parent._opener
        self.market_queue = parent.market_queue
        self.progress = parent.progress
        self.transport = parent.transport
        if not hasattr(parent, "_market_quote_cache"):
            parent._market_quote_cache = _QuoteCache(parent.cache_dir)
        self.quote_cache = parent._market_quote_cache
        self._source_url = None
        self.request_count = 0
        self.deadline = min(time.monotonic() + DEADLINE_SECONDS,
                            getattr(parent, "question_deadline", float("inf")) - 8)

    def _gate(self):
        self.parent._gate()
        if time.monotonic() >= self.deadline:
            _fail("timeout")

    def _market_deadline(self):
        return self.deadline

    def _request(self, url):
        self._gate()
        _validate_url(url)
        if self.request_count >= MAX_REQUESTS:
            _fail("timeout")
        self._source_url = url
        return self._request_queued(url, REQUEST_TIMEOUT, self.deadline)

    def _http_request(self, url, timeout, deadline, market):
        self._gate()
        self.request_count += 1
        return super()._http_request(url, timeout, deadline, market)

    def marketable(self):
        with self._market_scope():
            return self._marketable_locked()

    def _marketable_locked(self):
        self._gate()
        cached = getattr(self.parent, "_marketable_ids_cache", None)
        if cached is not None:
            stored_at, values = cached
            if 0 <= time.time() - stored_at <= STATIC_TTL:
                return values
        raw = self._request(UNIVERSALIS + "/api/v2/marketable")
        _require(isinstance(raw, list) and 1 <= len(raw) <= MAX_MARKETABLE_ITEMS)
        values = frozenset(_integer(item, 1, 999999) for item in raw)
        _require(len(values) == len(raw))
        self.parent._marketable_ids_cache = (time.time(), values)
        return values

    def quotes(self, ids, location):
        with self._market_scope():
            return self._quotes_locked(ids, location)

    def cached_quotes(self, ids, location):
        self._gate()
        return self.quote_cache.get_many(ids, location, self.deadline)

    def _quotes_locked(self, ids, location):
        cached = self.cached_quotes(ids, location)
        cached_ids = {value["item_id"] for value in cached}
        ids = [item_id for item_id in ids if item_id not in cached_ids]
        if not ids:
            return cached, cached_ids
        path_location = str(location["world_id"]) if location["kind"] == "world" else location["name"]
        url = self._query_url(UNIVERSALIS, "/api/v2/" + urllib.parse.quote(path_location, safe="") +
                              "/" + ",".join(map(str, ids)), listings=0, entries=SALES_LIMIT,
                              entriesWithin=SALES_WINDOW_SECONDS, fields=_fields(len(ids) > 1))
        try:
            raw = self._request(url)
        except GameDataError as error:
            if error.code != "not_found" or len(ids) != 1:
                raise
            # The final request can become a singleton after another process
            # fills part of this batch while it waits for queue admission.
            observation = _validate_observation({
                "version": 3, "location": location, "item_id": ids[0],
                "quote": None, "unresolved": True, "fetched_at_utc": _now()
            }, location)
            self._gate()
            self.quote_cache.put_many([observation], self.deadline)
            return cached + [observation], cached_ids
        _require(isinstance(raw, dict))
        self._check_location(raw, location)
        if len(ids) == 1:
            rows, unresolved = {str(ids[0]): raw}, []
        else:
            _require(isinstance(raw.get("itemIDs"), list) and raw["itemIDs"] == ids)
            rows, unresolved = raw.get("items"), raw.get("unresolvedItems")
            _require(isinstance(rows, dict) and isinstance(unresolved, list))
        stamp, quotes = _now(), []
        for key_id, row in rows.items():
            _require(isinstance(row, dict))
            item_id = _integer(row.get("itemID"), 1, 999999)
            _require(str(item_id) == key_id and item_id in ids)
            self._check_location(row, location)
            updated = self._unix(row.get("lastUploadTime"), True)
            has_data = row.get("hasData", updated is not None)
            _require(type(has_data) is bool)
            nq, hq = _price(row.get("minPriceNQ")), _price(row.get("minPriceHQ"))
            sales_nq, sales_hq = _sales_summaries(row.get("recentHistory", []), stamp, has_data)
            quotes.append({"item_id": item_id, "nq": nq if has_data else None,
                           "hq": hq if has_data else None, "upstream_updated_at_utc": updated,
                           "fetched_at_utc": stamp, "sales_nq": sales_nq, "sales_hq": sales_hq})
        for item in unresolved:
            _integer(item, 1, 999999)
        all_ids = [row["item_id"] for row in quotes] + unresolved
        _require(len(all_ids) == len(ids) and set(all_ids) == set(ids))
        by_id = {row["item_id"]: row for row in quotes}
        observations = [_validate_observation({
            "version": 3, "location": location, "item_id": item,
            "quote": by_id.get(item), "unresolved": item in unresolved, "fetched_at_utc": stamp
        }, location) for item in ids]
        self._gate()
        self.quote_cache.put_many(observations, self.deadline)
        return cached + observations, cached_ids

    @staticmethod
    def _check_location(raw, location):
        if location["kind"] == "world":
            _require(type(raw.get("worldID")) is int and raw["worldID"] == location["world_id"])
        else:
            _require(isinstance(raw.get("dcName"), str) and raw["dcName"].casefold() == location["name"].casefold())


class MarketBatch:
    """Fetch public minima and bounded recent sales without model-driven pagination.

    completed_count includes quoted, unresolved and market-prohibited item types. A
    returned quote with null prices is a completed lookup with no reported asking price.
    failed_item_ids are incomplete lookups, never zero-valued items. Stop on first error,
    retain completed batches, and never retry or silently reuse stale quote data.
    """
    def __init__(self, public_tools):
        self.public_tools = public_tools

    def fetch(self, item_ids, world_or_dc, resume_from=0, retained=None, scan_started_at=None):
        _require(isinstance(item_ids, (list, tuple)) and len(item_ids) <= MAX_ITEMS)
        ids = sorted(set(_integer(item, 1, 999999) for item in item_ids))
        _text(world_or_dc, 64)
        _require(not any(ord(c) < 32 for c in world_or_dc))
        _integer(resume_from, 0, 40)
        if retained is not None:
            _require(type(scan_started_at) in (int, float) and math.isfinite(scan_started_at))
            validate_retained_evidence(retained, ids, world_or_dc, scan_started_at)
        session = _Session(self.public_tools)
        result = {"location": None, "fetched_at_utc": None, "quotes": [],
                  "nonmarketable_item_ids": [], "unresolved_item_ids": [], "failed_item_ids": [],
                  "error": None, "requested_count": len(ids), "completed_count": 0,
                  "cache_hits": 0, "retained_observations": 0, "request_count": 0, "next_batch": 0}
        complete, observations = set(), {}
        def accept(value, cached=False, dated=False):
            item_id = value["item_id"]
            if item_id in complete:
                return
            observations[item_id] = value
            if value["unresolved"]:
                result["unresolved_item_ids"].append(item_id)
            else:
                result["quotes"].append({**value["quote"], "cache_hit": cached,
                                         "retained_observation": dated})
            result["cache_hits"] += int(cached)
            result["retained_observations"] += int(dated)
            complete.add(item_id)
        try:
            session._gate()
            if retained is not None:
                result["location"] = retained["location"]
                for value in retained["observations"]:
                    accept(value, cached=True, dated=True)
            if ids:
                location = session._resolve_location(world_or_dc)
                _require(result["location"] is None or result["location"] == location)
                result["location"] = location
                marketable = session.marketable()
                result["nonmarketable_item_ids"] = [item for item in ids if item not in marketable]
                complete.update(result["nonmarketable_item_ids"])
                eligible = [item for item in ids if item in marketable]
                batches = [eligible[start:start + BATCH_SIZE] for start in range(0, len(eligible), BATCH_SIZE)]
                # New scans accept only fresh item/world entries. Explicit resumes
                # keep their separately validated dated observations unchanged.
                for batch in batches:
                    for value in session.cached_quotes(batch, result["location"]):
                        accept(value, cached=True)
                order = list(range(len(batches)))
                if order:
                    pivot = resume_from % len(order)
                    order = order[pivot:] + order[:pivot]
                for index in order:
                    batch = [item for item in batches[index] if item not in complete]
                    if not batch:
                        continue
                    result["next_batch"] = index
                    values, cached_ids = session.quotes(batch, result["location"])
                    for value in values:
                        accept(value, cached=value["item_id"] in cached_ids)
            session._gate()
        except GameDataError as error:
            result["error"] = {"code": error.code, "message": ERROR_MESSAGES[error.code]}
            if error.code in ("online_disabled", "cancelled"):
                # Revocation/cancellation invalidates the whole operation, including
                # already-fetched public quotes joined to private candidate IDs.
                result["quotes"] = []
                result["nonmarketable_item_ids"] = []
                result["unresolved_item_ids"] = []
                observations.clear()
                complete.clear()
        except (ValueError, TypeError, KeyError, AttributeError, IndexError, OverflowError, RecursionError):
            result["error"] = {"code": "invalid_response", "message": ERROR_MESSAGES["invalid_response"]}
        result["fetched_at_utc"] = _now()
        result["failed_item_ids"] = [item for item in ids if item not in complete]
        result["completed_count"] = len(complete)
        result["request_count"] = session.request_count
        result["evidence"] = {"version": 1, "location": result["location"],
                              "observations": [observations[item] for item in sorted(observations)]}
        return result
