"""Explicit reader-fixture adapter for the real deferred transport contract.

The raw QuestionChecks.client remains unchanged for protocol-denial tests.
Only successful discovery replies are separated from legacy reader-output sinks;
wire_replies always records every actual outgoing message.
"""
from copy import deepcopy
import json

import companion
from companion_tools import TOOL_LABELS
from deferred_tools import DISCOVER_TOOL, INVOKE_TOOL


CATALOGUE_MARKER = "Catalogue descriptions are capability summaries, not evidence about the user or game state:\n"


def adapt_messages(messages, discovery_batches=()):
    messages = list(messages)
    batches = [list(batch) for batch in discovery_batches]
    if any(not 1 <= len(batch) <= 3 or len(set(batch)) != len(batch) for batch in batches):
        raise ValueError("Fixture discovery batches must contain 1-3 unique names")
    used_ids = {message.get("id") for message in messages
                if isinstance(message, dict) and type(message.get("id")) in (int, str)}
    adapted, discovered, discovery_ids = [], set(), set()
    for original in messages:
        message = deepcopy(original)
        params = message.get("params") if isinstance(message, dict) else None
        name = params.get("tool") if isinstance(params, dict) else None
        if (isinstance(message, dict) and message.get("method") == "item/tool/call" and isinstance(name, str)
                and name in TOOL_LABELS):
            if name not in discovered:
                index = len(discovery_ids) + 1
                ident = "fixture-discovery-" + str(index)
                while ident in used_ids:
                    index += 1
                    ident = "fixture-discovery-" + str(index)
                used_ids.add(ident)
                discovery_ids.add(ident)
                names = next((batch for batch in batches if name in batch), [name])
                discovered.update(names)
                discovery = deepcopy(message)
                discovery["id"] = ident
                discovery["params"].update(tool=DISCOVER_TOOL, arguments={"names": names})
                adapted.append(discovery)
            params.update(tool=INVOKE_TOOL, arguments={"name": name, "arguments": params.get("arguments")})
        adapted.append(message)
    return adapted, discovery_ids


class _DeferredFixtureCompanion(companion.Companion):
    @property
    def send(self):
        return self._record_reply

    @send.setter
    def send(self, sink):
        self._reader_reply_sink = sink

    def _record_reply(self, message):
        self.wire_replies.append(deepcopy(message))
        if message.get("id") in self._discovery_ids:
            self.discovery_replies.append(deepcopy(message))
            if message.get("result", {}).get("success") is True:
                return
        self._reader_reply_sink(message)


def client(messages, discovery_batches=()):
    # Import lazily to avoid changing raw fixtures or creating an import cycle.
    from inventory_question_check import QuestionChecks
    adapted, discovery_ids = adapt_messages(messages, discovery_batches)
    value, calls = QuestionChecks().client(adapted)
    value._reader_reply_sink = value.send
    value._discovery_ids = discovery_ids
    value.wire_replies, value.discovery_replies = [], []
    value.__class__ = _DeferredFixtureCompanion
    return value, calls


def discovered_results(value):
    return [json.loads(message["result"]["contentItems"][0]["text"])
            for message in value.discovery_replies if message.get("result", {}).get("success") is True]


def instructions(value, calls):
    initial = next(params for method, params in calls if method == "thread/start")["developerInstructions"]
    return initial + "".join(result["instructions"] for result in discovered_results(value))


def assert_exposed(check, value, calls, expected):
    initial = next(params for method, params in calls if method == "thread/start")
    check.assertEqual([row["name"] for row in initial["dynamicTools"]],
                      [DISCOVER_TOOL, INVOKE_TOOL] if expected else [])
    if not expected:
        check.assertNotIn(CATALOGUE_MARKER, initial["developerInstructions"])
        return
    catalogue = json.loads(initial["developerInstructions"].split(CATALOGUE_MARKER, 1)[1].split("\n", 1)[0])
    check.assertEqual(catalogue, [{"name": row["name"], "description": row["description"]} for row in expected])
    by_name = {row["name"]: row for row in expected}
    for result in discovered_results(value):
        for definition in result["tools"]:
            check.assertIn(definition["name"], by_name)
            check.assertEqual(definition, by_name[definition["name"]])
