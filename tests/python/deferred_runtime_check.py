"""Exercise real QuestionRunner dispatch with explicit fake provider events; no live calls."""
from collections import deque
from copy import deepcopy
from datetime import datetime, timezone
import json
import threading
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

import companion
import activity_lookup as activities
from companion_architecture_check import wire_request
from companion_provider import CompanionFailure, ProviderEvent
from deferred_tools import MAX_DISCOVERY_CALLS, WRAPPER_TOOLS
from personality_details_lookup import INSTRUCTIONS as PERSONA_INSTRUCTIONS, TOOL as PERSONA_TOOL
from quest_lookup import encoded_size
from shared_tools_integration_check import FakeGameDataTools, public_result, request


PERSONA = "query_personality_details"


def tool(name, arguments, ident, **changes):
    return ProviderEvent(kind="tool", tool=name, arguments=arguments, request_id=ident,
                         is_request=True, strictly_correlated=True, **changes)


def discovery(*names, ident="discovery"):
    return tool("discover_tools", {"names": list(names)}, ident)


def invoke(name=PERSONA, arguments=None, ident="invoke"):
    return tool("invoke_tool", {"name": name, "arguments": {"section": "opinions"} if arguments is None else arguments}, ident)


def completed():
    return [ProviderEvent(kind="answer", text='{"answer":"Synthetic response."}'),
            ProviderEvent(kind="completed", status="completed")]


class Harness:
    """Record the entire provider exchange without adapting events or filtering replies."""
    def __init__(self, events, **changes):
        self.wire = request(**changes)
        self.pending = deque(events)
        self.replies, self.emitted = [], []
        self.before_event = lambda event: None
        self.reader = Mock(side_effect=lambda args: {**args, "text": "SYNTHETIC PRIVATE NOTE"})
        self.public = FakeGameDataTools()
        self.turn = SimpleNamespace(read=self.read,
            reply_tool=lambda event, success, result: self.replies.append((event, success, result)),
            reject_tool=Mock(), reject_request=Mock(), cancel=Mock(), finish=Mock())
        self.provider = SimpleNamespace(open=Mock(return_value="synthetic-thread"), start=Mock(return_value=self.turn))
        self.runner = companion.QuestionRunner()
        self.runner.provider_id = "codex"
        self.runner.model, self.runner.effort = self.wire["model"], self.wire["reasoning_effort"]
        self.runner.web_search = self.wire["web_search"]
        self.runner.cancel_event = threading.Event()
        self.runner.preflight = Mock(return_value={})
        self.runner.question_provider = lambda: self.provider

    def read(self, **_):
        event = self.pending.popleft()
        self.before_event(event)
        return event

    def run(self, **readers):
        with patch.object(companion, "emit", side_effect=self.emitted.append), \
                patch.object(companion, "GameDataTools", return_value=self.public):
            self.runner.ask(self.wire, personality_details_query=self.reader, **readers)


def activity_harness(events, provider="codex"):
    harness = Harness(events)
    harness.wire = wire_request(36)
    harness.wire.update(provider=provider, web_search=False,
                        activity_lookup={"scope": activities.SCOPE, "categories": ["free_company"]})
    harness.runner.provider_id, harness.runner.web_search = provider, False
    entries = []
    for ident, name, value, unit in ((1, "Company", None, None), (2, "Rank", 1, "rank"),
                                    (3, "Members", 0, "members"), (4, "Online members", 0, "members")):
        row = dict.fromkeys(activities.NULL_ENTRY_FIELDS)
        row.update(id=ident, group="company", name=name, state="observed", value=value, unit=unit,
                   text="Synthetic company" if ident == 1 else None)
        entries.append(row)
    result = {"scope": activities.SCOPE, "category": "free_company", "captured_at_utc": datetime.now(timezone.utc).isoformat(),
              "available": True, "unavailable_reason": None, "source": activities.SOURCES["free_company"],
              "coverage": activities.COVERAGE["free_company"] + activities.COMMON_COVERAGE,
              "readiness": "visible_menu_observation", "entry_count": 4, "entries": entries,
              "matched_count": 4, "offset": 0, "limit": 50, "next_offset": None, "truncated": False}
    return harness, result


class DeferredRuntimeChecks(unittest.TestCase):
    def test_activity_projection_reaches_each_provider_after_checks_without_mutating_source(self):
        for provider in ("codex", "copilot", "grok"):
            harness, result = activity_harness([
                discovery("query_activities"), invoke("query_activities", {"category": "free_company"}), *completed()], provider)
            original = deepcopy(result)
            with self.subTest(provider=provider):
                harness.run(activity_query=lambda args: result)
                delivered = harness.replies[-1][2]
                self.assertEqual(delivered, activities.model_result(original))
                self.assertEqual(result, original)
                self.assertLess(encoded_size(delivered), encoded_size(original))
                self.assertIs(delivered["truncated"], False)
                self.assertEqual(delivered["entries"][2]["value"], 0)
                self.assertNotIn("text", delivered["entries"][2])
                self.assertIsNone(delivered["entry_defaults"]["text"])
                final = next(row for row in harness.emitted if "question_answer" in row)
                self.assertIn("1 local activity lookups", final["question_context"])
                stages = [row["performance"]["stage"] for row in harness.emitted if "performance" in row]
                self.assertIn("tool.activities", stages)
                harness.turn.cancel.assert_not_called()

    def test_activity_projection_never_reduces_original_result_budget(self):
        for allowed_calls in (0, 2):
            harness, result = activity_harness([discovery("query_activities"), *[
                invoke("query_activities", {"category": "free_company"}, ident=index) for index in range(allowed_calls + 1)]])
            original_size = encoded_size(result)
            limit = original_size * allowed_calls if allowed_calls else original_size - 1
            self.assertLess(encoded_size(activities.model_result(result)), limit)
            with self.subTest(allowed_calls=allowed_calls), \
                    patch.object(companion, "MAX_LOOKUP_TOTAL_BYTES", limit), \
                    patch.object(companion, "activity_model_result", wraps=activities.model_result) as project:
                with self.assertRaises(CompanionFailure) as error:
                    harness.run(activity_query=lambda args: result)
                self.assertEqual(error.exception.kind, "reader")
                self.assertIn("byte result budget", str(error.exception))
                self.assertEqual(project.call_count, allowed_calls)
                self.assertEqual(len(harness.replies), 1 + allowed_calls)
                harness.turn.cancel.assert_called_once()
                self.assertFalse(any("question_answer" in row for row in harness.emitted))

    def test_invalid_activity_result_is_rejected_before_projection(self):
        for change in ("missing_null", "false_value", "projected_result"):
            harness, result = activity_harness([
                discovery("query_activities"), invoke("query_activities", {"category": "free_company"})])
            if change == "missing_null":
                del result["entries"][0]["reference_id"]
            elif change == "false_value":
                result["entries"][2]["value"] = False
            else:
                result = activities.model_result(result)
            with self.subTest(change=change), patch.object(companion, "activity_model_result") as project:
                with self.assertRaises(CompanionFailure) as error:
                    harness.run(activity_query=lambda args: result)
                self.assertEqual(error.exception.kind, "protocol")
                project.assert_not_called()
                self.assertEqual(len(harness.replies), 1)
                harness.turn.cancel.assert_called_once()

    def test_only_current_explicit_pricing_phrase_authorizes_continuation(self):
        for question, allowed in (
            ("Continue pricing", True), (" resume Pricing! ", True),
            ("Resume pricing.", True), ("Continue", False),
            ("What is valuable?", False), ("Do not continue pricing", False),
        ):
            with self.subTest(question=question):
                harness = Harness(completed(), web_search=True)
                harness.wire["question"] = question
                harness.wire["history"] = [{"question": "Continue pricing", "answer": "Older request."}]
                harness.run()
                self.assertIs(harness.public.resume_pricing_allowed, allowed)

    def assert_interrupted(self, harness, kind=None):
        with self.assertRaises(CompanionFailure if kind else RuntimeError) as failure:
            harness.run()
        if kind:
            self.assertEqual(failure.exception.kind, kind)
        harness.turn.cancel.assert_called_once()
        harness.turn.finish.assert_called_once()
        harness.provider.start.assert_called_once()
        self.assertFalse(any("question_answer" in row for row in harness.emitted))
        return failure.exception

    def test_discovery_returns_only_selected_metadata_and_invocation_retains_attribution(self):
        harness = Harness([discovery(PERSONA, "get_item"), invoke(),
                           invoke("get_item", {"item_id": 5121}, ident="public"), *completed()], web_search=True)
        harness.run()
        instructions, definitions = harness.provider.open.call_args.args
        self.assertEqual({row["name"] for row in definitions}, WRAPPER_TOOLS)
        self.assertNotIn(PERSONA_INSTRUCTIONS, instructions)
        self.assertIn(PERSONA, instructions)
        self.assertNotIn("SYNTHETIC PRIVATE NOTE", instructions)
        details = harness.replies[0][2]
        self.assertEqual(details["tools"][0], PERSONA_TOOL)
        self.assertEqual([row["name"] for row in details["tools"]], [PERSONA, "get_item"])
        self.assertIn(PERSONA_INSTRUCTIONS, details["instructions"])
        self.assertNotIn("SYNTHETIC PRIVATE NOTE", json.dumps(details))
        self.assertEqual([reply[0].tool for reply in harness.replies], ["discover_tools", "invoke_tool", "invoke_tool"])
        self.assertTrue(all(reply[1] for reply in harness.replies))
        self.assertEqual(harness.reader.call_count, 1)
        self.assertEqual(harness.public.calls, [("get_item", {"item_id": 5121})])
        final = next(row for row in harness.emitted if "question_answer" in row)
        self.assertIn("1 optional personality detail lookups", final["question_context"])
        self.assertIn("1 public game-data lookups", final["question_context"])
        stages = [row["performance"]["stage"] for row in harness.emitted if "performance" in row]
        self.assertIn("tool.personality_details", stages)
        self.assertIn("tool.game_data", stages)
        harness.turn.cancel.assert_not_called()

    def test_disabled_undiscovered_and_direct_tools_are_rejected_without_reading(self):
        cases = [
            ([discovery(PERSONA)], {"personality_details_lookup": None}, "permission"),
            ([invoke()], {"personality_details_lookup": None}, "permission"),
            ([invoke()], {}, "permission"),
            ([discovery("get_item")], {}, "permission"),
            ([tool(PERSONA, {"section": "opinions"}, "direct")], {}, None),
            ([discovery(PERSONA), tool(PERSONA, {"section": "opinions"}, "direct")], {}, None),
        ]
        for events, changes, kind in cases:
            with self.subTest(events=events, changes=changes):
                harness = Harness(events, **changes)
                self.assert_interrupted(harness, kind)
                harness.reader.assert_not_called()
                self.assertFalse(harness.public.calls)
                if changes:
                    self.assertEqual(harness.provider.open.call_args.args[1], [])

    def test_discovery_and_invocation_share_duplicate_id_rejection(self):
        cases = [
            ([discovery(PERSONA, ident="same"), invoke(ident="same")], 0),
            ([discovery(PERSONA, ident="same"), discovery(PERSONA, ident="same")], 0),
            ([discovery(PERSONA), invoke(ident="same"), discovery(PERSONA, ident="same")], 1),
        ]
        for events, expected_reads in cases:
            with self.subTest(events=events):
                harness = Harness(events)
                self.assert_interrupted(harness, "protocol")
                self.assertEqual(harness.reader.call_count, expected_reads)
                harness.turn.reject_tool.assert_called_once()

    def test_uncorrelated_discovery_and_invocation_never_read(self):
        for name, arguments in (("discover_tools", {"names": [PERSONA]}),
                                ("invoke_tool", {"name": PERSONA, "arguments": {"section": "opinions"}})):
            for changes in ({"is_request": False}, {"strictly_correlated": False}, {"request_id": True}):
                values = dict(kind="tool", tool=name, arguments=arguments, request_id="bad",
                              is_request=True, strictly_correlated=True)
                values.update(changes)
                events = [] if name == "discover_tools" else [discovery(PERSONA)]
                harness = Harness([*events, ProviderEvent(**values)])
                with self.subTest(name=name, changes=changes):
                    self.assert_interrupted(harness, "protocol")
                    harness.reader.assert_not_called()

    def test_malformed_underlying_arguments_allow_bounded_same_turn_correction(self):
        harness = Harness([discovery(PERSONA), invoke(arguments={"section": 42}, ident="bad"),
                           invoke(ident="corrected"), *completed()])
        harness.run()
        self.assertFalse(harness.replies[1][1])
        self.assertEqual(harness.replies[1][2]["error_kind"], "invalid_arguments")
        self.assertEqual(harness.reader.call_args.args, ({"section": "opinions"},))
        self.assertEqual(harness.reader.call_count, 1)
        harness.provider.start.assert_called_once()
        harness.turn.cancel.assert_not_called()
        harness = Harness([discovery(PERSONA)] + [
            invoke(arguments={"section": 42}, ident=index) for index in range(companion.MAX_LOOKUP_CALLS + 1)])
        error = self.assert_interrupted(harness, "reader")
        self.assertIn("call budget", str(error))
        self.assertEqual(len(harness.replies), 1 + companion.MAX_LOOKUP_CALLS)
        self.assertTrue(all(not reply[1] for reply in harness.replies[1:]))
        harness.reader.assert_not_called()

    def test_permission_denial_is_fatal_even_after_discovery(self):
        harness = Harness([discovery(PERSONA), invoke(arguments={"section": "background"})])
        self.assert_interrupted(harness, "permission")
        harness.reader.assert_not_called()
        self.assertEqual(len(harness.replies), 1)

    def test_discovery_calls_do_not_spend_reader_allowance(self):
        events = [discovery(PERSONA, ident=f"discover-{index}") for index in range(MAX_DISCOVERY_CALLS)]
        events += [invoke(ident=index) for index in range(companion.MAX_LOOKUP_CALLS)]
        harness = Harness([*events, *completed()])
        harness.run()
        self.assertEqual(harness.reader.call_count, companion.MAX_LOOKUP_CALLS)
        self.assertEqual(len(harness.replies), MAX_DISCOVERY_CALLS + companion.MAX_LOOKUP_CALLS)
        harness.turn.cancel.assert_not_called()
        exhausted = Harness([*events, invoke(ident="excess")])
        self.assertIn("call budget", str(self.assert_interrupted(exhausted, "reader")))
        self.assertEqual(exhausted.reader.call_count, companion.MAX_LOOKUP_CALLS)

    def test_discovery_limit_never_opens_a_reader_or_retries(self):
        harness = Harness([discovery(PERSONA, ident=index) for index in range(MAX_DISCOVERY_CALLS + 1)])
        self.assert_interrupted(harness, "reader")
        self.assertEqual(len(harness.replies), MAX_DISCOVERY_CALLS)
        harness.reader.assert_not_called()

    def test_reader_result_budget_excludes_discovery_metadata(self):
        result = {"section": "opinions", "text": "SYNTHETIC PRIVATE NOTE"}
        limit = encoded_size(result) * 2
        harness = Harness([discovery(PERSONA), invoke(ident=1), invoke(ident=2), invoke(ident=3)])
        with patch.object(companion, "MAX_LOOKUP_TOTAL_BYTES", limit):
            self.assertIn("byte result budget", str(self.assert_interrupted(harness, "reader")))
        self.assertGreater(encoded_size(harness.replies[0][2]), limit)
        self.assertEqual([reply[0].request_id for reply in harness.replies], ["discovery", 1, 2])
        self.assertEqual(harness.reader.call_count, 3)

    def test_public_result_budget_is_enforced_after_discovery(self):
        harness = Harness([discovery("get_item")] + [
            invoke("get_item", {"item_id": 5121}, ident=index) for index in range(companion.MAX_LOOKUP_CALLS)],
            web_search=True, personality_details_lookup=None)
        harness.public.make_result = lambda name: public_result(name, large=True)
        self.assert_interrupted(harness, "reader")
        delivered = sum(companion.lookup_result_size(reply[2]) for reply in harness.replies[1:])
        self.assertLessEqual(delivered, companion.MAX_LOOKUP_TOTAL_BYTES)
        self.assertGreater(delivered + companion.lookup_result_size(public_result("get_item", large=True)),
                           companion.MAX_LOOKUP_TOTAL_BYTES)
        self.assertLess(len(harness.public.calls), companion.MAX_LOOKUP_CALLS)

    def test_cancellation_before_discovery_or_invocation_never_calls_reader(self):
        for cancel_on in ("discover_tools", "invoke_tool"):
            harness = Harness([discovery(PERSONA), invoke()])
            def before_event(event):
                if event.tool == cancel_on:
                    harness.runner.cancel_event.set()
            harness.before_event = before_event
            with self.subTest(cancel_on=cancel_on):
                self.assert_interrupted(harness)
                harness.reader.assert_not_called()
                self.assertFalse(harness.public.calls)


if __name__ == "__main__":
    unittest.main()
