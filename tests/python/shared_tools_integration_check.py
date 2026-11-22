"""Fake-transport v14 tool integration checks; no runtime, AI, filesystem cache, or network."""
import copy
from datetime import datetime, timezone
import json
import unittest
from unittest.mock import patch

import companion
from deferred_tools import WRAPPER_TOOLS
import game_data_tools as public
import inventory_question_check as question_checks
from personality_details_check import request
from personality_details_lookup import TOOL as PERSONA_TOOL
from quest_lookup import encoded_size
from quest_lookup_check import completed, tool_message


PUBLIC_ARGS = {"find_item": {"name": "Darksteel Ore"}, "get_item": {"item_id": 5121},
               "get_market_data": {"item_id": 5121, "world_or_dc": "Alpha"}}


def public_result(name, *, error=None, large=False):
    stamp = datetime.now(timezone.utc).isoformat()
    urls = {"find_item": "https://v2.xivapi.com/api/search?sheets=Item",
            "get_item": "https://v2.xivapi.com/api/sheet/Item/5121",
            "get_market_data": "https://universalis.app/api/v2/Alpha/5121"}
    value = {"tool": name, "ok": error is None, "source_url": urls[name], "fetched_at_utc": stamp,
             "cache_hit": False, "data": None, "error": None}
    if error:
        value.update(fetched_at_utc=None, error={"code": error, "message": public.ERROR_MESSAGES[error]})
    elif name == "find_item":
        value["data"] = {"matches": [{"item_id": 5121, "name": "Darksteel Ore"}], "more_results": False}
    elif name == "get_item":
        value["data"] = {"item_id": 5121, "name": "Darksteel Ore", "description": "界" * 2048 if large else "An ore.",
                         "item_level": 50, "equip_level": 1, "can_be_hq": False,
                         "is_untradable": False, "category": "Metal"}
    else:
        value["data"] = {"item_id": 5121, "location": {"kind": "world", "name": "Alpha", "world_id": 408},
                         "upstream_updated_at_utc": stamp, "world_upload_times": [], "has_data": False,
                         "listing_count": 0, "recent_history_count": 0, "units_for_sale": 0,
                         "minimum_asking_price_per_unit": {"nq": None, "hq": None},
                         "nq_listings": [], "hq_listings": [], "recent_sales": [], "coverage": "No reported data."}
    return public.validate_result(value)


class FakeGameDataTools:
    def __init__(self, **kwargs):
        self.options, self.calls = kwargs, []
        self.make_result = public_result

    def call(self, name, args):
        self.calls.append((name, copy.deepcopy(args)))
        return self.make_result(name)


def event(tool, ident=50, args=None, **changes):
    return tool_message(ident=ident, tool="invoke_tool",
                        arguments={"name": tool, "arguments": args if args is not None else PUBLIC_ARGS.get(tool, {"section": "opinions"})},
                        **changes)


def discover(*names, ident="discovery", **changes):
    return tool_message(ident=ident, tool="discover_tools", arguments={"names": list(names)}, **changes)


def decoded(output):
    return json.loads(output["result"]["contentItems"][0]["text"])


def prepared(messages=None, **changes):
    payload = request(**changes)
    client, calls = question_checks.QuestionChecks().client(completed() if messages is None else messages)
    client.model, client.effort, client.web_search = payload["model"], payload["reasoning_effort"], payload["web_search"]
    outputs = []
    client.send = outputs.append
    return payload, client, calls, outputs


class SharedToolsIntegrationChecks(unittest.TestCase):
    def test_persona_registered_summary_only_and_read_when_requested(self):
        payload, client, calls, outputs = prepared([discover("query_personality_details"), event("query_personality_details"), *completed()])
        reads = []
        def query(args):
            self.assertIn("turn/start", [method for method, _ in calls])
            reads.append(args)
            return {**args, "text": "PRIVATE OPINION: enjoys technical cooking."}
        with patch.object(companion, "emit"), patch.object(companion, "GameDataTools") as factory:
            client.ask(payload, personality_details_query=query)
        factory.assert_not_called()
        thread = next(params for method, params in calls if method == "thread/start")
        self.assertEqual({tool["name"] for tool in thread["dynamicTools"]}, WRAPPER_TOOLS)
        self.assertEqual(decoded(outputs[0])["tools"], [PERSONA_TOOL])
        turn = next(params for method, params in calls if method == "turn/start")
        self.assertNotIn("PRIVATE OPINION", json.dumps(turn))
        context = json.loads(turn["input"][1]["text"].split("\n", 1)[1])
        self.assertEqual(context["personality_details_lookup"], {"sections": ["opinions"]})
        self.assertEqual(context["personality"], payload["personality"])
        self.assertEqual(reads, [{"section": "opinions"}])
        self.assertNotIn("PRIVATE OPINION", json.dumps(decoded(outputs[0])))
        self.assertIn("PRIVATE OPINION", outputs[1]["result"]["contentItems"][0]["text"])

    def test_absent_persona_details_cannot_be_requested_and_missing_callback_fails_before_send(self):
        payload, client, calls, _ = prepared(personality_details_lookup=None)
        with patch.object(companion, "emit"):
            client.ask(payload, personality_details_query=lambda _: self.fail("No details permission"))
        self.assertEqual(next(p for m, p in calls if m == "thread/start")["dynamicTools"], [])
        payload, client, _, outputs = prepared([event("query_personality_details")], personality_details_lookup=None)
        with patch.object(companion, "emit"), self.assertRaises(RuntimeError):
            client.ask(payload, personality_details_query=lambda _: self.fail("No details permission"))
        self.assertFalse(outputs[0]["result"]["success"])
        payload, client, calls, _ = prepared()
        with self.assertRaises(RuntimeError):
            client.ask(payload)
        self.assertFalse(calls)
        payload, client, _, outputs = prepared([discover("query_personality_details"),
                                                event("query_personality_details", args={"section": "background"})])
        with patch.object(companion, "emit"), self.assertRaises(RuntimeError):
            client.ask(payload, personality_details_query=lambda _: self.fail("Unavailable section"))
        self.assertFalse(outputs[-1]["result"]["success"])

    def test_online_off_has_no_public_tools_or_network_client_even_for_model_attempts(self):
        for name in (None, *PUBLIC_ARGS):
            messages = completed() if name is None else [event(name)]
            payload, client, calls, outputs = prepared(messages, personality_details_lookup=None)
            with patch.object(companion, "emit"), patch.object(companion, "GameDataTools") as factory:
                if name is None:
                    client.ask(payload)
                else:
                    with self.assertRaises(RuntimeError):
                        client.ask(payload)
                    self.assertFalse(outputs[0]["result"]["success"])
            factory.assert_not_called()
            self.assertEqual(next(p for m, p in calls if m == "thread/start")["dynamicTools"], [])

    def test_online_tools_dispatch_exact_arguments_and_return_structured_results(self):
        messages = [discover(*PUBLIC_ARGS)] + [event(name, ident=index) for index, name in enumerate(PUBLIC_ARGS)] + completed()
        payload, client, calls, outputs = prepared(messages, web_search=True, personality_details_lookup=None)
        fake = FakeGameDataTools()
        with patch.object(companion, "emit"), patch.object(companion, "GameDataTools", return_value=fake) as factory:
            client.ask(payload)
        self.assertTrue(factory.call_args.kwargs["online_allowed"])
        self.assertEqual(fake.calls, list(PUBLIC_ARGS.items()))
        self.assertEqual({tool["name"] for tool in next(p for m, p in calls if m == "thread/start")["dynamicTools"]}, WRAPPER_TOOLS)
        self.assertEqual(decoded(outputs[0])["tools"], [tool for tool in public.TOOLS if tool["name"] in PUBLIC_ARGS])
        self.assertEqual([decoded(out)["tool"] for out in outputs[1:]], list(PUBLIC_ARGS))
        self.assertTrue(all(out["result"]["success"] for out in outputs))

    def test_persona_and_public_tools_share_call_and_byte_budgets(self):
        messages = [discover("query_personality_details", "get_item")] + [
            event("query_personality_details" if index % 2 == 0 else "get_item", ident=index) for index in range(13)]
        payload, client, _, outputs = prepared(messages, web_search=True)
        fake, reads = FakeGameDataTools(), []
        def query(args):
            reads.append(args)
            return {**args, "text": "Likes cooking."}
        with patch.object(companion, "emit"), patch.object(companion, "GameDataTools", return_value=fake), self.assertRaises(RuntimeError):
            client.ask(payload, personality_details_query=query)
        self.assertEqual(len(reads), 6)
        self.assertEqual(len(fake.calls), 6)
        self.assertEqual(sum(out["result"]["success"] for out in outputs[1:]), 12)
        self.assertFalse(outputs[-1]["result"]["success"])
        messages = [discover("query_personality_details", "get_item"), event("query_personality_details", ident=0)] + [
            event("get_item", ident=i) for i in range(1, 13)]
        payload, client, _, outputs = prepared(messages, web_search=True)
        fake.make_result = lambda name: public_result(name, large=True)
        with patch.object(companion, "emit"), patch.object(companion, "GameDataTools", return_value=fake), self.assertRaises(RuntimeError):
            client.ask(payload, personality_details_query=lambda args: {**args, "text": "界" * 2000})
        self.assertLess(len(outputs[1:]), 13)
        self.assertFalse(outputs[-1]["result"]["success"])
        sizes = [encoded_size(decoded(out)) for out in outputs[1:-1]]
        self.assertLessEqual(sum(sizes), 120000)
        self.assertGreater(sum(sizes) + encoded_size(public_result("get_item", large=True)), 120000)

    def test_correlation_duplicate_ids_and_cancelled_questions_never_call_sources(self):
        for tool in ("query_personality_details", "get_item"):
            for changes in ({"threadId": "other-thread"}, {"turnId": "other-turn"}):
                payload, client, _, _ = prepared([discover(tool), event(tool, **changes)], web_search=True)
                fake = FakeGameDataTools()
                with patch.object(companion, "emit"), patch.object(companion, "GameDataTools", return_value=fake), self.assertRaises(RuntimeError):
                    client.ask(payload, personality_details_query=lambda _: self.fail("Bad correlation"))
                self.assertFalse(fake.calls)
        payload, client, _, outputs = prepared([discover("get_item", "query_personality_details"),
                                               event("get_item", ident=7), event("query_personality_details", ident=7)], web_search=True)
        fake = FakeGameDataTools()
        with patch.object(companion, "emit"), patch.object(companion, "GameDataTools", return_value=fake), self.assertRaises(RuntimeError):
            client.ask(payload, personality_details_query=lambda _: self.fail("Repeated id"))
        self.assertEqual(len(fake.calls), 1)
        self.assertFalse(outputs[-1]["result"]["success"])
        payload, client, _, _ = prepared([discover("get_item"), event("get_item")], web_search=True, personality_details_lookup=None)
        receive = client.receive
        client.receive = lambda **kwargs: (client.cancel_event.set(), receive(**kwargs))[1]
        fake = FakeGameDataTools()
        with patch.object(companion, "emit"), patch.object(companion, "GameDataTools", return_value=fake), self.assertRaises(RuntimeError):
            client.ask(payload)
        self.assertFalse(fake.calls)

    def test_structured_service_error_reaches_model_without_aborting_answer(self):
        payload, client, calls, outputs = prepared([discover("get_market_data"), event("get_market_data"), *completed()],
                                                 web_search=True, personality_details_lookup=None)
        fake = FakeGameDataTools()
        fake.make_result = lambda name: public_result(name, error="unavailable")
        with patch.object(companion, "emit") as emitted, patch.object(companion, "GameDataTools", return_value=fake):
            client.ask(payload)
        self.assertTrue(outputs[1]["result"]["success"])
        result = decoded(outputs[1])
        self.assertFalse(result["ok"])
        self.assertEqual(result["error"]["code"], "unavailable")
        self.assertEqual(emitted.call_args.args[0]["question_answer"], "Observed answer.")
        self.assertNotIn("turn/interrupt", [method for method, _ in calls])

    def test_persona_or_online_permission_changes_during_preflight_abort_before_turn(self):
        for mutate in (lambda payload: payload.update(personality_details_lookup=None),
                       lambda payload: payload["personality_details_lookup"]["sections"].append("background"),
                       lambda payload: payload["personality"].update(instructions="Changed core")):
            payload, client, calls, _ = prepared()
            client.preflight = lambda: mutate(payload)
            with patch.object(companion, "emit"), self.assertRaises(RuntimeError):
                client.ask(payload, personality_details_query=lambda _: self.fail("Changed after freeze"))
            self.assertNotIn("turn/start", [method for method, _ in calls])
        for allowed in (False, True):
            payload, client, calls, _ = prepared(web_search=allowed, personality_details_lookup=None)
            client.preflight = lambda: payload.update(web_search=not allowed)
            fake = FakeGameDataTools()
            with patch.object(companion, "emit"), patch.object(companion, "GameDataTools", return_value=fake), self.assertRaises(RuntimeError):
                client.ask(payload)
            self.assertNotIn("turn/start", [method for method, _ in calls])
            self.assertFalse(fake.calls)


if __name__ == "__main__":
    unittest.main()
