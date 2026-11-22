"""Synthetic menu producer/validator checks; no game, account or provider calls."""
from copy import deepcopy
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import sys
import threading
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "bridge"))
from menu_lookup import (CATEGORIES, MAX_ENTRIES, SCOPE, TOOL, validate_arguments, validate_result,
                         validate_summary, permitted_instructions, permitted_tool)
from quest_lookup import QuestQueryBridge


class MenuChecks(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        path = ROOT / ".test-tmp" / "menu-results.json"
        if not path.is_file():
            raise RuntimeError("Run Menu.Tests with --produce-fixtures .test-tmp/menu-results.json first")
        cls.fixtures = json.loads(path.read_text(encoding="utf-8"))

    def fixture(self, name="shop"):
        return deepcopy(next(value for value in self.fixtures if value["name"] == name))

    def validate(self, fixture, categories=None):
        return validate_result(fixture["result"], {"scope": SCOPE, "categories": categories or sorted(CATEGORIES)}, fixture["arguments"])

    def test_all_csharp_producers(self):
        self.assertEqual(len(self.fixtures), 13)
        for fixture in self.fixtures:
            with self.subTest(name=fixture["name"]):
                self.validate(fixture)
        self.assertEqual(self.fixture("outfit_bounds")["result"]["entries"][0]["id"], 8800)

    def test_glamour_csharp_producers(self):
        path = ROOT / ".test-tmp" / "glamour-producer.json"
        self.assertTrue(path.is_file(), "Run Glamour.Tests with --produce-fixtures .test-tmp/glamour-producer.json first")
        fixtures = json.loads(path.read_text(encoding="utf-8"))
        self.assertEqual(len(fixtures), 6)
        for fixture in fixtures:
            with self.subTest(name=fixture["name"]):
                self.validate(fixture)

    def test_provider_requests_cross_real_menu_bridge_for_loaded_and_closed_menus(self):
        # Previous fixtures bypassed QuestQueryBridge, which rejected every menu
        # category before a plugin query could be emitted. Exercise that channel
        # inside an answer, using C# producer JSON and a synthetic provider only.
        import companion
        import deferred_fixture
        from companion_architecture_check import wire_request
        from quest_lookup_check import completed, tool_message

        for name in ("shop", "exchange", "armoire", "dresser", "dresser_spelling", "plates", "unavailable"):
            with self.subTest(name=name):
                fixture = self.fixture(name)
                payload = wire_request(31)
                payload["web_search"] = False
                payload["menu_lookup"] = {"scope": SCOPE, "categories": sorted(CATEGORIES)}
                client, calls = deferred_fixture.client([
                    tool_message(tool="query_menus", arguments=fixture["arguments"]), *completed()])
                client.model, client.effort, client.web_search = payload["model"], payload["reasoning_effort"], False
                replies, sent, responders, failures = [], [], [], []
                client.send = replies.append

                def emit_query(message):
                    self.assertIn("turn/start", [method for method, _ in calls])
                    sent.append(message)
                    self.assertEqual(set(message), {"menus_query"})
                    query = message["menus_query"]
                    self.assertEqual(query["arguments"], validate_arguments(fixture["arguments"]))

                    def respond():
                        try:
                            response = {"id": query["id"], "result": deepcopy(fixture["result"])}
                            # Responses must remain bound to both this ID and the
                            # menus channel; adding the channel must not weaken it.
                            with self.assertRaises(RuntimeError):
                                bridge.accept({"quest_result": response})
                            with self.assertRaises(RuntimeError):
                                bridge.accept({"menus_result": {**response, "id": query["id"] + 1}})
                            bridge.accept({"menus_result": response})
                            with self.assertRaises(RuntimeError):
                                bridge.accept({"menus_result": response})
                        except BaseException as error:
                            failures.append(error)

                    worker = threading.Thread(target=respond, daemon=True)
                    responders.append(worker)
                    worker.start()

                bridge = QuestQueryBridge(threading.Event(), emit_query, timeout=2)
                try:
                    with patch.object(companion, "emit") as emitted, patch.object(companion, "GameDataTools") as public:
                        client.ask(payload, menu_query=lambda args: bridge.query(args, "menus"))
                finally:
                    for worker in responders:
                        worker.join(timeout=3)
                self.assertFalse(failures)
                self.assertTrue(all(not worker.is_alive() for worker in responders))
                public.assert_not_called()
                self.assertEqual(len(sent), 1)
                self.assertTrue(replies[0]["result"]["success"])
                self.assertEqual(json.loads(replies[0]["result"]["contentItems"][0]["text"]), fixture["result"])
                self.assertTrue(any(call.args[0].get("question_answer") == "Observed answer."
                                    for call in emitted.call_args_list))
                self.assertNotIn("turn/interrupt", [method for method, _ in calls])

    def test_follow_suggested_spelling_in_one_question(self):
        import companion
        import deferred_fixture
        from companion_architecture_check import wire_request
        from companion_provider import CompanionFailure
        from quest_lookup_check import completed, tool_message

        typo, exact = self.fixture("dresser_spelling"), self.fixture("dresser_corrected")
        self.assertEqual(typo["result"]["suggested_names"], [exact["arguments"]["query"]])
        self.assertNotIn("suggested_names", exact["result"])
        self.assertEqual(exact["result"]["entries"][0]["name"], exact["arguments"]["query"])
        for case in ("same observation", "changed observation", "changed row"):
            with self.subTest(case=case):
                followup = deepcopy(exact)
                if case == "changed observation":
                    followup["result"]["source"] = "Different synthetic observation"
                if case == "changed row":
                    followup["result"]["entries"][0]["item_id"] += 1
                pages = [typo, followup] if case != "changed row" else [typo, exact, followup]
                messages = [tool_message(ident=50+i, tool="query_menus", arguments=page["arguments"])
                            for i, page in enumerate(pages)]
                client, calls = deferred_fixture.client([*messages, *completed()])
                payload = wire_request(31)
                payload.update(web_search=False, menu_lookup={"scope": SCOPE, "categories": ["glamour_dresser"]})
                client.model, client.effort, client.web_search = payload["model"], payload["reasoning_effort"], False
                replies, responses = [], iter(pages)
                client.send = replies.append

                def lookup(args):
                    page = next(responses)
                    self.assertEqual(args, validate_arguments(page["arguments"]))
                    return page["result"]

                with patch.object(companion, "emit") as emitted:
                    if case == "same observation":
                        client.ask(payload, menu_query=lookup)
                        self.assertNotIn("turn/interrupt", [m for m, _ in calls])
                        self.assertTrue(all(reply["result"]["success"] for reply in replies))
                        self.assertTrue(any("question_answer" in c.args[0] for c in emitted.call_args_list))
                    else:
                        with self.assertRaises(CompanionFailure) as caught:
                            client.ask(payload, menu_query=lookup)
                        self.assertEqual(caught.exception.kind, "protocol")
                        self.assertFalse(replies[-1]["result"]["success"])
                        self.assertIn("turn/interrupt", [m for m, _ in calls])
                self.assertEqual(len(replies), len(pages))

    def test_menu_bridge_rejects_bad_selectors_before_emitting(self):
        sent = []
        bridge = QuestQueryBridge(threading.Event(), sent.append, timeout=.01)
        for arguments in ({}, {"category": "inventory"}, {"category": "shops", "item_ids": [True]},
                          {"category": "shops", "query": "x"}):
            with self.subTest(arguments=arguments), self.assertRaises(RuntimeError):
                bridge.query(arguments, "menus")
        self.assertEqual(sent, [])

    def test_missing_reader_reply_is_recoverable_but_malformed_result_is_protocol_failure(self):
        import companion
        import deferred_fixture
        from companion_architecture_check import wire_request
        from companion_provider import CompanionFailure
        from quest_lookup_check import tool_message

        for timeout in (True, False):
            with self.subTest(timeout=timeout):
                payload = wire_request(31)
                payload["web_search"] = False
                payload["menu_lookup"] = {"scope": SCOPE, "categories": ["shops"]}
                client, calls = deferred_fixture.client([tool_message(tool="query_menus", arguments={"category": "shops"})])
                client.model, client.effort, client.web_search = payload["model"], payload["reasoning_effort"], False
                replies, sent = [], []
                client.send = replies.append
                bridge = QuestQueryBridge(threading.Event(), sent.append, timeout=.01)
                query = (lambda args: bridge.query(args, "menus")) if timeout else lambda args: {"unexpected": True}
                with patch.object(companion, "emit") as emitted, self.assertRaises(CompanionFailure) as failure:
                    client.ask(payload, menu_query=query)
                self.assertEqual(failure.exception.kind, "reader" if timeout else "protocol")
                self.assertFalse(replies[0]["result"]["success"])
                self.assertIn("turn/interrupt", [method for method, _ in calls])
                self.assertFalse(any("question_answer" in call.args[0] for call in emitted.call_args_list))
                if timeout:
                    self.assertEqual(len(sent), 1)
                    self.assertIsNone(bridge.pending)
                    with self.assertRaises(RuntimeError):
                        bridge.accept({"menus_result": {"id": 1, "result": self.fixture()["result"]}})

    def test_summary_permission_scopes_and_independent_tools(self):
        self.assertIsNone(validate_summary(None))
        for value in ({}, [], {"scope": SCOPE, "categories": []}, {"scope": SCOPE, "categories": ["shops", "shops"]},
                      {"scope": SCOPE, "categories": ["inventory"]}, {"scope": SCOPE, "categories": [True]},
                      {"scope": SCOPE, "categories": [["shops"]]}, {"scope": "other", "categories": ["shops"]},
                      {"scope": SCOPE, "categories": ["shops"], "entries": []}):
            with self.subTest(value=value), self.assertRaises(RuntimeError):
                validate_summary(value)
        summary = {"scope": SCOPE, "categories": ["armoire"]}
        tool = permitted_tool(summary)
        self.assertEqual(tool["inputSchema"]["properties"]["category"]["enum"], ["armoire"])
        tool["inputSchema"]["properties"]["category"]["enum"].append("shops")
        self.assertEqual(permitted_tool(summary)["inputSchema"]["properties"]["category"]["enum"], ["armoire"])
        self.assertEqual(set(TOOL["inputSchema"]["properties"]["category"]["enum"]), CATEGORIES)
        with self.assertRaises(RuntimeError):
            validate_arguments({"category": "shops"}, summary)
        with self.assertRaises(RuntimeError):
            self.validate(self.fixture(), ["armoire"])
        self.assertIn("separately permitted balances", permitted_instructions(summary))
        self.assertIn("not duplicate ownership", permitted_instructions(summary))

    def test_argument_types_bounds_and_empty_queries(self):
        valid = {"category": "shops"}
        for value in (None, [], {}, {"category": "inventory"}, {"category": ["shops"]}, {**valid, "extra": 1},
                      {**valid, "query": None}, {**valid, "query": False}, {**valid, "query": "x"},
                      {**valid, "query": "ab\n"}, {**valid, "query": "\u0085ab"}, {**valid, "query": " " * 101},
                      {**valid, "query": "😀" * 51}, {**valid, "query": "\ud800"}, {**valid, "item_ids": {}},
                      {**valid, "item_ids": [True]}, {**valid, "item_ids": [0]}, {**valid, "item_ids": [4294967296]},
                      {**valid, "item_ids": [1, 1]}, {**valid, "item_ids": list(range(1, 22))},
                      {**valid, "offset": -1}, {**valid, "offset": MAX_ENTRIES}, {**valid, "offset": True},
                      {**valid, "limit": 0}, {**valid, "limit": 51}, {**valid, "limit": 1.0}):
            with self.subTest(value=value), self.assertRaises(RuntimeError):
                validate_arguments(value)
        for query in ("", "  ", "ab", "😀", "界" * 100):
            self.assertEqual(validate_arguments({**valid, "query": query})["query"], query.strip())
        self.assertEqual(validate_arguments(valid)["limit"], 50)
        self.assertEqual(validate_arguments({**valid, "offset": 9999})["offset"], 9999)

    def test_result_metadata_types_and_unknown_fields(self):
        changes = [{"scope": "inventory"}, {"category": "armoire"}, {"available": 1}, {"truncated": 0},
                   {"source": " "}, {"source": "x" * 257}, {"readiness": "ab\n"}, {"coverage": "x" * 1501},
                   {"captured_at_utc": (datetime.now(timezone.utc) + timedelta(days=1)).isoformat()},
                   {"captured_at_utc": "invalid"}, {"catalog_count": True}, {"catalog_count": 10001},
                   {"matched_count": 2}, {"limit": True}, {"limit": 1}, {"offset": 1},
                   {"unavailable_reason": "Unexpected"}, {"player_id": "private"}, {"entries": {}}]
        for change in changes:
            fixture = self.fixture()
            fixture["result"].update(change)
            with self.subTest(change=change), self.assertRaises(RuntimeError):
                self.validate(fixture)
        fixture = self.fixture()
        del fixture["result"]["readiness"]
        with self.assertRaises(RuntimeError):
            self.validate(fixture)

    def test_entry_bounds_and_private_or_inferred_fields(self):
        changes = [{"id": 0}, {"id": 10001}, {"id": True}, {"item_id": 0}, {"item_id": 4294967296},
                   {"quantity": 0}, {"quantity": True}, {"quantity": -1}, {"quantity": 4294967296},
                   {"name": " "}, {"name": "😀" * 65}, {"location": "x" * 193}, {"unknown_reason": ""},
                   {"group_id": True}, {"group_id": -1}, {"slot": 65536}, {"is_hq": 1},
                   {"owned_quantity": 10}, {"stock": 10}, {"affordable": True}, {"native_pointer": 123},
                   {"owner_id": 123}, {"purchase_callback": [1, 2]}, {"role": "stored"}, {"costs": {}}]
        for change in changes:
            fixture = self.fixture()
            fixture["result"]["entries"][0].update(change)
            with self.subTest(change=change), self.assertRaises(RuntimeError):
                self.validate(fixture)

    def test_cost_shapes_preserve_unknown_and_multiple_requirements(self):
        self.assertEqual(len(self.fixture("exchange")["result"]["entries"][0]["costs"]), 2)
        named = self.fixture("named_currency")
        self.validate(named)
        cost = named["result"]["entries"][0]["costs"][0]
        self.assertEqual(cost["name"], "Company credits")
        self.assertIsNone(cost["item_id"])
        self.assertGreater(cost["amount"], 0)
        self.assertIsNotNone(cost["unknown_reason"])
        changes = [{"kind": "currency"}, {"kind": []}, {"item_id": None}, {"item_id": 0}, {"item_id": True},
                   {"amount": 0}, {"amount": True}, {"amount": 4294967296}, {"amount": None},
                   {"name": "x" * 129}, {"unknown_reason": " "}, {"owned": 999},
                   {"kind": "unknown", "item_id": None}, {"kind": "gil", "item_id": 1}]
        for change in changes:
            fixture = self.fixture()
            fixture["result"]["entries"][0]["costs"][0].update(change)
            with self.subTest(change=change), self.assertRaises(RuntimeError):
                self.validate(fixture)
        fixture = self.fixture("unknown_cost")
        fixture["result"]["entries"][0]["costs"][0]["amount"] = 10
        self.validate(fixture)  # An observed amount with an unmapped currency stays unknown.
        fixture = self.fixture()
        fixture["result"]["entries"][0]["costs"] = []
        self.validate(fixture)  # Empty costs are allowed by the generic contract and explicitly never imply free.
        fixture = self.fixture()
        fixture["result"]["entries"][0]["costs"] *= 9
        with self.assertRaises(RuntimeError):
            self.validate(fixture)

    def test_storage_and_reference_roles_do_not_add_ownership(self):
        for name in ("armoire", "dresser", "plates"):
            self.validate(self.fixture(name))
            fixture = self.fixture(name)
            fixture["result"]["entries"][0]["costs"] = self.fixture()["result"]["entries"][0]["costs"]
            with self.assertRaises(RuntimeError):
                self.validate(fixture)
        fixture = self.fixture("plates")
        fixture["result"]["entries"][0]["quantity"] = 1
        with self.assertRaises(RuntimeError):
            self.validate(fixture)
        self.assertEqual(len(self.fixture("plates")["result"]["entries"]), 2)
        self.assertIsNone(self.fixture("dresser")["result"]["entries"][0]["quantity"])

    def test_filters_pagination_and_stable_entry_order(self):
        for change in ({"next_offset": 20}, {"next_offset": 21}, {"next_offset": 23}, {"truncated": False},
                       {"entries": []}, {"next_offset": True}, {"next_offset": 10000}):
            fixture = self.fixture("pagination")
            fixture["result"].update(change)
            with self.subTest(change=change), self.assertRaises(RuntimeError):
                self.validate(fixture)
        for ordering in ("reverse", "duplicate"):
            fixture = self.fixture("pagination")
            rows = fixture["result"]["entries"]
            if ordering == "reverse":
                rows.reverse()
            else:
                rows[1]["id"] = rows[0]["id"]
            with self.assertRaises(RuntimeError):
                self.validate(fixture)
        for selector in ({"item_ids": [999]}, {"query": "not_present"}):
            fixture = self.fixture()
            fixture["arguments"].update(selector)
            with self.assertRaises(RuntimeError):
                self.validate(fixture)
        fixture = self.fixture()
        fixture["arguments"]["query"] = "SYNTHETIC item"
        self.validate(fixture)

    def test_unavailable_is_distinct_from_empty(self):
        self.assertIsNone(self.fixture("unavailable")["result"]["catalog_count"])
        self.assertEqual(self.fixture("missing")["result"]["catalog_count"], 1)
        for change in ({"catalog_count": 0}, {"unavailable_reason": None}, {"unavailable_reason": " "},
                       {"matched_count": 1}, {"truncated": True}, {"next_offset": 1},
                       {"entries": self.fixture()["result"]["entries"]}):
            fixture = self.fixture("unavailable")
            fixture["result"].update(change)
            with self.subTest(change=change), self.assertRaises(RuntimeError):
                self.validate(fixture)

    def test_dresser_spelling_suggestions_preserve_filtered_rows_and_scope(self):
        fixture = self.fixture("dresser_spelling")
        self.assertEqual(self.validate(fixture)["suggested_names"], ["Figments of the Shallows"])
        self.assertEqual(fixture["result"]["entries"], [])
        self.assertEqual(fixture["result"]["matched_count"], 0)
        for names in (None, [], "Figments", [None], ["Augmented Deepshadow Greaves of Casting"],
                      ["Figments of the Shallows"] * 2, ["Frigment of the Shadows"]):
            malformed = deepcopy(fixture)
            malformed["result"]["suggested_names"] = names
            with self.subTest(names=names), self.assertRaises(RuntimeError):
                self.validate(malformed)
        for change in ({"offset": 1}, {"item_ids": [100]}, {"category": "armoire"}):
            malformed = deepcopy(fixture)
            malformed["arguments"].update(change)
            malformed["result"].update({key: change[key] for key in ("offset", "category") if key in change})
            with self.subTest(change=change), self.assertRaises(RuntimeError):
                self.validate(malformed)
        with self.assertRaises(RuntimeError):
            self.validate(fixture, categories=["armoire"])
        for fixture_name in ("unavailable", "shop", "dresser"):
            malformed = self.fixture(fixture_name)
            malformed["result"]["suggested_names"] = ["Synthetic Item"]
            with self.assertRaises(RuntimeError):
                self.validate(malformed)

    def test_result_wire_bound_is_independently_enforced(self):
        fixture = self.fixture()
        result = fixture["result"]
        row = result["entries"][0]
        row.update(name="界" * 128, location="界" * 192)
        row["costs"] = [dict(row["costs"][0], name="界" * 128) for _ in range(8)]
        result["entries"] = [dict(deepcopy(row), id=index + 1) for index in range(50)]
        result.update(catalog_count=50, matched_count=50)
        with self.assertRaises(RuntimeError):
            self.validate(fixture)


if __name__ == "__main__":
    unittest.main()
