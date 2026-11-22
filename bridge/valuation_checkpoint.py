"""Bounded private scan history; never persists candidate arrays or quantities.

The checkpoint contains hashes, a batch index and timestamps. Its separate ledger
retains dated public observations, whose queried item IDs can reveal interests or
holdings. Both stay in the private profile. Every resume validates a fresh snapshot.
"""
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import time

from market_request_queue import MarketQueueError, MarketRequestQueue, _unlinked

TTL_SECONDS = 600
MAX_FILES = 8
MAX_BYTES = 1024
MAX_EVIDENCE_BYTES = 6 * 1024 * 1024


def options_digest(arguments):
    value = {key: arguments[key] for key in ("world_or_dc", "rank_by", "categories")}
    value["world_or_dc"] = value["world_or_dc"].strip().casefold()
    value["categories"] = sorted(value["categories"])
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


class ValuationCheckpoints:
    def __init__(self, cache_dir):
        self.directory = Path(cache_dir) / "valuation-resume-v1" if cache_dir is not None else None
        self.evidence_directory = Path(cache_dir) / "market-scan-evidence-v1" if cache_dir is not None else None
        self.writer = MarketRequestQueue(self.directory) if self.directory is not None else None

    def _path(self, binding, options):
        digest = hashlib.sha256((binding + options).encode("ascii")).hexdigest()
        return self.directory / (digest + ".json")

    def load(self, binding, options):
        if self.directory is None:
            return None
        try:
            with self.writer.locked(lambda: None, time.monotonic() + .2):
                if self.evidence_directory.exists():
                    _unlinked(self.evidence_directory)
                    self._prune_evidence()
                return self._load_locked(binding, options)
        except (MarketQueueError, OSError):
            return None

    def _load_locked(self, binding, options):
        try:
            path = self._path(binding, options)
            _unlinked(path)
            with path.open("rb") as stream:
                raw = stream.read(MAX_BYTES + 1)
            if len(raw) > MAX_BYTES:
                return None
            value = json.loads(raw)
            if (not isinstance(value, dict) or
                    set(value) != {"version", "binding", "options", "next_batch", "started_at", "expires_at",
                                   "evidence_id", "evidence_sha256"} or
                    type(value["version"]) is not int or value["version"] != 2 or
                    value["binding"] != binding or value["options"] != options or
                    type(value["next_batch"]) is not int or not 0 <= value["next_batch"] <= 40 or
                    type(value["started_at"]) not in (int, float) or type(value["expires_at"]) not in (int, float) or
                    not 0 <= time.time() - value["started_at"] < TTL_SECONDS or
                    value["expires_at"] != value["started_at"] + TTL_SECONDS):
                return None
            if not all(isinstance(value[field], str) and re.fullmatch(r"[0-9a-f]{64}", value[field])
                       for field in ("evidence_id", "evidence_sha256")):
                return None
            evidence_path = self.evidence_directory / (value["evidence_id"] + ".json")
            _unlinked(evidence_path)
            with evidence_path.open("rb") as stream:
                raw_evidence = stream.read(MAX_EVIDENCE_BYTES + 1)
            if (len(raw_evidence) > MAX_EVIDENCE_BYTES or
                    hashlib.sha256(raw_evidence).hexdigest() != value["evidence_sha256"]):
                return None
            value["evidence"] = json.loads(raw_evidence)
            return value
        except (OSError, ValueError, TypeError, OverflowError, RecursionError):
            return None

    def save(self, binding, options, next_batch, started_at=None, evidence=None):
        if self.directory is None:
            return None
        try:
            # A separate short-lived writer lock never waits behind HTTP. It
            # permits a fixed temporary file and removes crash leftovers safely.
            with self.writer.locked(lambda: None, time.monotonic() + .2):
                return self._save_locked(binding, options, next_batch, started_at, evidence)
        except MarketQueueError:
            return None

    def _save_locked(self, binding, options, next_batch, started_at, evidence):
        temporary, evidence_temporary = None, None
        try:
            _unlinked(self.directory)
            self.directory.mkdir(parents=True, exist_ok=True)
            path = self._path(binding, options)
            _unlinked(path)
            started_at = time.time() if started_at is None else started_at
            if not 0 <= time.time() - started_at < TTL_SECONDS:
                return None
            evidence = evidence if evidence is not None else {"version": 1, "location": None, "observations": []}
            raw_evidence = json.dumps(evidence, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
            if len(raw_evidence) > MAX_EVIDENCE_BYTES:
                return None
            evidence_id = os.urandom(32).hex()
            value = {"version": 2, "binding": binding, "options": options, "next_batch": next_batch,
                     "started_at": started_at, "expires_at": started_at + TTL_SECONDS,
                     "evidence_id": evidence_id, "evidence_sha256": hashlib.sha256(raw_evidence).hexdigest()}
            payload = json.dumps(value, separators=(",", ":")).encode("ascii")
            if len(payload) > MAX_BYTES:
                return None
            for old in self.directory.glob("*.tmp"):
                if re.fullmatch(r"[0-9a-f]{64}(?:\.[0-9a-f]{32})?\.tmp", old.name) and not old.is_symlink():
                    old.unlink(missing_ok=True)
            _unlinked(self.evidence_directory)
            self.evidence_directory.mkdir(parents=True, exist_ok=True)
            self._prune_evidence()
            evidence_path = self.evidence_directory / (evidence_id + ".json")
            evidence_temporary = evidence_path.with_suffix(".tmp")
            _unlinked(evidence_temporary)
            with evidence_temporary.open("xb") as stream:
                stream.write(raw_evidence)
            os.replace(evidence_temporary, evidence_path)
            temporary = path.with_suffix(".tmp")
            with temporary.open("xb") as stream:
                stream.write(payload)
            os.replace(temporary, path)
            files = [p for p in self.directory.glob("*.json")
                     if re.fullmatch(r"[0-9a-f]{64}\.json", p.name) and not p.is_symlink()]
            files.sort(key=lambda p: p.stat().st_mtime, reverse=True)
            for old in files[MAX_FILES:]:
                old.unlink(missing_ok=True)
            self._prune_evidence()
            return datetime.fromtimestamp(value["expires_at"], timezone.utc).isoformat()
        except (OSError, ValueError, TypeError, OverflowError):
            return None
        finally:
            for candidate in (temporary, evidence_temporary):
                if candidate is None:
                    continue
                try:
                    candidate.unlink(missing_ok=True)
                except OSError:
                    pass

    def _prune_evidence(self):
        """Called only under the profile writer lock; touch our exact names only."""
        referenced = set()
        for path in self.directory.glob("*.json"):
            if not re.fullmatch(r"[0-9a-f]{64}\.json", path.name) or path.is_symlink():
                continue
            try:
                with path.open("rb") as stream:
                    raw = stream.read(MAX_BYTES + 1)
                if len(raw) <= MAX_BYTES:
                    value = json.loads(raw)
                    expiry = value.get("expires_at")
                    if type(expiry) in (int, float) and expiry <= time.time():
                        path.unlink(missing_ok=True)
                        continue
                    ident = value.get("evidence_id")
                    if isinstance(ident, str) and re.fullmatch(r"[0-9a-f]{64}", ident):
                        referenced.add(ident)
            except (OSError, ValueError, TypeError, AttributeError, RecursionError):
                continue
        for path in self.evidence_directory.iterdir():
            if (not path.is_symlink() and
                    re.fullmatch(r"[0-9a-f]{64}\.(?:json|tmp)", path.name) and
                    (path.suffix == ".tmp" or path.stem not in referenced)):
                path.unlink(missing_ok=True)

    def clear(self, binding, options):
        if self.directory is None:
            return
        try:
            with self.writer.locked(lambda: None, time.monotonic() + .2):
                path = self._path(binding, options)
                _unlinked(path)
                path.unlink(missing_ok=True)
                if self.evidence_directory.exists():
                    _unlinked(self.evidence_directory)
                    self._prune_evidence()
        except (OSError, MarketQueueError):
            pass
