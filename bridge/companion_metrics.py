"""Bounded numeric performance events; never retain request or tool content."""
from contextlib import contextmanager
from functools import wraps
import math
import time


TIMING_STAGES = frozenset({
    "runtime.startup", "runtime.preflight", "provider.first_answer", "provider.turn",
    "tool.inventory", "tool.currencies", "tool.duties", "tool.collections", "tool.progression", "tool.crafting_log", "tool.quests", "tool.history", "tool.game_context", "tool.npc_dialogue",
    "tool.character_details", "tool.personality_details", "tool.game_data",
    "tool.game_definitions", "tool.gc_supply", "tool.npc_target", "tool.storage", "tool.menus", "tool.activities", "tool.valuation",
})
TOKEN_FIELDS = {
    "input_tokens": "inputTokens", "cached_input_tokens": "cachedInputTokens",
    "output_tokens": "outputTokens", "reasoning_tokens": "reasoningOutputTokens",
}
MAX_COUNTER = (1 << 63) - 1  # Pinned App Server schema uses signed int64 counters.


def emit_elapsed(emit, stage, started, *, clock=time.perf_counter):
    if stage not in TIMING_STAGES:
        return
    elapsed = (clock() - started) * 1000
    if math.isfinite(elapsed) and elapsed >= 0:
        emit({"performance": {"stage": stage, "elapsed_ms": round(elapsed, 3)}})


@contextmanager
def measured(stage, emit, *, clock=time.perf_counter):
    started = clock()
    try:
        yield
    finally:
        emit_elapsed(emit, stage, started, clock=clock)


def timed(stage, emit):
    def decorate(function):
        @wraps(function)
        def wrapped(*args, **kwargs):
            with measured(stage, emit):
                return function(*args, **kwargs)
        return wrapped
    return decorate


class TurnMetrics:
    """One fresh ephemeral thread per question, with latest cumulative usage only.

    Pinned 0.160.0 ThreadTokenUsageUpdatedNotification: tokenUsage.total covers
    all provider responses in this thread; last covers only the last response.
    Do not sum repeated totals or substitute last when total is unavailable.
    """
    def __init__(self, emit, *, clock=time.perf_counter):
        self.emit, self.clock = emit, clock
        self.started = clock()
        self.first_seen = False
        self.finished = False
        self.counters = {key: None for key in TOKEN_FIELDS}
        # Provider item IDs are ephemeral, bounded correlation keys only; never emitted.
        self._observed = {key: set() for key in ("tool_calls", "web_searches", "reasoning_items")}
        self._observed_any = set()
        self._state = None

    def activity(self, state):
        if state not in ("starting", "working", "tools", "searching", "model", "answer", "finishing"):
            return
        if state != self._state:
            self._state = state
            self.emit({"performance": {"stage": "request.activity", "state": state}})

    def _count(self, category, identifier):
        if type(identifier) not in (int, str) or isinstance(identifier, str) and not 1 <= len(identifier) <= 256:
            return False
        observed = self._observed[category]
        if identifier in observed or len(observed) >= 1024:
            return False
        observed.add(identifier)
        self._observed_any.add(category)
        self.emit({"performance": {"stage": "provider.activity", **{
            key: len(value) if key in self._observed_any else None for key, value in self._observed.items()}}})
        return True

    def tool(self, identifier):
        if self._count("tool_calls", identifier):
            self.activity("tools")

    def observe_event(self, event):
        if not event.correlated or not event.strictly_correlated or event.is_request:
            return
        if event.kind == "reasoning":
            if self._count("reasoning_items", event.observation_id):
                self.activity("model")
        elif event.kind in ("search", "search_started"):
            if self._count("web_searches", event.observation_id):
                self.activity("searching")
            if event.kind == "search":
                self.activity("working")


    def observe(self, method, params, thread_id, turn_id):
        if (method != "thread/tokenUsage/updated" or not isinstance(params, dict) or
                params.get("threadId") != thread_id or params.get("turnId") != turn_id):
            return
        usage = params.get("tokenUsage")
        total = usage.get("total") if isinstance(usage, dict) else None
        # A later missing/malformed payload must not make older counters appear final.
        self.counters = {
            key: value if type(value := (total.get(source) if isinstance(total, dict) else None)) is int
            and 0 <= value <= MAX_COUNTER else None
            for key, source in TOKEN_FIELDS.items()
        }

    def first_answer(self):
        if not self.first_seen:
            self.first_seen = True
            emit_elapsed(self.emit, "provider.first_answer", self.started, clock=self.clock)

    def preview(self, value):
        if value.get("question_partial"):
            self.first_answer()
            self.activity("answer")
        self.emit(value)

    def finish(self):
        if self.finished:
            return
        self.finished = True
        emit_elapsed(self.emit, "provider.turn", self.started, clock=self.clock)
        self.emit({"performance": {"stage": "provider.tokens", **self.counters}})
