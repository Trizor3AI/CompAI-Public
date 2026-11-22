"""Cross-process Universalis admission, pacing and cooldown; no request data on disk.

One OS lock covers a bounded public lookup and its normalized-cache publication.
The OS releases it on process exit. Scheduling is serial, not guaranteed FIFO.
The fixed state file contains timestamps only, never URLs or private holdings.
"""
from contextlib import contextmanager
from datetime import timezone
from email.utils import parsedate_to_datetime
import errno
import json
import math
import os
from pathlib import Path
import threading
import time

START_INTERVAL = .5  # Conservative application policy, not a provider limit.
POLL_SECONDS = .05
DEFAULT_COOLDOWN = 15
MAX_STATE_BYTES = 512
MAX_TIMESTAMP = 10**129  # Allows every bounded numeric Retry-After without shortening it.


class MarketQueueError(RuntimeError):
    def __init__(self, code):
        super().__init__(code)
        self.code = code


def retry_after_seconds(value, now=None):
    """Parse bounded delay-seconds/HTTP-date. Never shorten a valid long delay."""
    now = time.time() if now is None else now
    if not isinstance(value, str) or len(value) > 128:
        return DEFAULT_COOLDOWN
    value = value.strip()
    if value.isascii() and value.isdigit():
        return int(value)
    try:
        stamp = parsedate_to_datetime(value)
        if stamp.tzinfo is None:
            return DEFAULT_COOLDOWN
        return max(0, stamp.astimezone(timezone.utc).timestamp() - now)
    except (ValueError, TypeError, OverflowError):
        return DEFAULT_COOLDOWN


def _unlinked(path):
    for candidate in (path, *path.parents):
        if candidate.is_symlink() or getattr(candidate, "is_junction", lambda: False)():
            raise OSError("Linked coordination path")


class MarketRequestQueue:
    def __init__(self, cache_dir=None):
        self.directory = Path(cache_dir) / "market-coordination-v1" if cache_dir is not None else None
        self.interval = START_INTERVAL
        self._thread_lock = threading.RLock()
        self._depth = 0
        self._state = {"version": 1, "next_start": 0, "cooldown_until": 0}

    @staticmethod
    def _check(gate, deadline):
        gate()
        if time.monotonic() >= deadline:
            raise MarketQueueError("timeout")

    @staticmethod
    def _notify(progress, message):
        if progress is not None:
            progress(message)

    @staticmethod
    def _sleep(gate, deadline, delay=POLL_SECONDS):
        MarketRequestQueue._check(gate, deadline)
        time.sleep(max(0, min(delay, POLL_SECONDS, deadline - time.monotonic())))

    @staticmethod
    def _lock(descriptor, unlock=False):
        os.lseek(descriptor, 0, os.SEEK_SET)
        if os.name == "nt":
            import msvcrt
            msvcrt.locking(descriptor, msvcrt.LK_UNLCK if unlock else msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.flock(descriptor, fcntl.LOCK_UN if unlock else fcntl.LOCK_EX | fcntl.LOCK_NB)

    def _read_state(self):
        if self.directory is None:
            return
        path = self.directory / "state.json"
        _unlinked(path)
        try:
            with path.open("rb") as stream:
                raw = stream.read(MAX_STATE_BYTES + 1)
        except FileNotFoundError:
            self._state = {"version": 1, "next_start": 0, "cooldown_until": 0}
            return
        if len(raw) > MAX_STATE_BYTES:
            raise ValueError("Coordination state too large")
        state = json.loads(raw)
        if (not isinstance(state, dict) or set(state) != {"version", "next_start", "cooldown_until"} or
                type(state["version"]) is not int or state["version"] != 1):
            raise ValueError("Invalid coordination state")
        for field in ("next_start", "cooldown_until"):
            value = state[field]
            if type(value) not in (int, float) or not math.isfinite(value) or not 0 <= value <= MAX_TIMESTAMP:
                raise ValueError("Invalid coordination timestamp")
        self._state = state

    def _write_state(self):
        if self.directory is None:
            return
        target, temporary = self.directory / "state.json", self.directory / "state.tmp"
        _unlinked(target)
        _unlinked(temporary)
        # The single shared lock also owns this fixed temporary filename.
        with temporary.open("wb") as stream:
            stream.write(json.dumps(self._state, separators=(",", ":")).encode("ascii"))
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, target)

    @contextmanager
    def locked(self, gate, deadline, progress=None):
        self._check(gate, deadline)
        notified = False
        while not self._thread_lock.acquire(blocking=False):
            if not notified:
                self._notify(progress, "Waiting for another public market lookup.")
                notified = True
            self._sleep(gate, deadline)
        descriptor, acquired = None, False
        try:
            if self._depth:
                self._depth += 1
                try:
                    self._check(gate, deadline)
                    yield self
                finally:
                    self._depth -= 1
                return
            try:
                if self.directory is not None:
                    _unlinked(self.directory)
                    self.directory.mkdir(parents=True, exist_ok=True)
                    lock_path = self.directory / "queue.lock"
                    _unlinked(lock_path)
                    descriptor = os.open(lock_path, os.O_RDWR | os.O_CREAT | getattr(os, "O_BINARY", 0), 0o600)
                    while True:
                        self._check(gate, deadline)
                        try:
                            self._lock(descriptor)
                            acquired = True
                            break
                        except OSError as error:
                            if error.errno not in (errno.EACCES, errno.EAGAIN, errno.EDEADLK):
                                raise
                            if not notified:
                                self._notify(progress, "Waiting for another public market lookup.")
                                notified = True
                            self._sleep(gate, deadline)
                    if os.fstat(descriptor).st_size == 0:
                        os.write(descriptor, b"\0")
                self._read_state()
            except (OSError, ValueError, TypeError, OverflowError, RecursionError):
                raise MarketQueueError("unavailable") from None
            self._depth = 1
            try:
                self._check(gate, deadline)
                yield self
            finally:
                self._depth = 0
        finally:
            try:
                if descriptor is not None:
                    try:
                        if acquired:
                            self._lock(descriptor, unlock=True)
                    finally:
                        os.close(descriptor)
            finally:
                self._thread_lock.release()

    def before_request(self, gate, deadline, progress=None):
        if not self._depth:
            raise MarketQueueError("unavailable")
        notified = False
        while True:
            self._check(gate, deadline)
            now = time.time()
            delay = max(self._state["next_start"], self._state["cooldown_until"]) - now
            if delay <= 0:
                break
            cooldown = self._state["cooldown_until"] > now
            if not notified:
                self._notify(progress, "Universalis is cooling down; waiting within this request's time budget."
                             if cooldown else "Pacing public market requests.")
                notified = True
            if delay >= deadline - time.monotonic():
                raise MarketQueueError("rate_limited" if cooldown else "timeout")
            self._sleep(gate, deadline, delay)
        self._state["next_start"] = time.time() + self.interval
        try:
            self._write_state()
        except OSError:
            raise MarketQueueError("unavailable") from None
        self._check(gate, deadline)

    def rate_limited(self, retry_after, progress=None):
        until = time.time() + retry_after_seconds(retry_after)
        self._state["cooldown_until"] = max(self._state["cooldown_until"], until)
        try:
            self._write_state()
        except OSError:
            raise MarketQueueError("unavailable") from None
        self._notify(progress, "Universalis rate limit reached; shared cooldown saved. Try the lookup again later.")
