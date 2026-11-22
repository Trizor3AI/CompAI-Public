"""Local-only protocol, quota, and cancellation fixtures. No model request."""
import io
import json
from pathlib import Path
import tempfile
import threading
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import companion
from prepare_isolation_catalog import digest


class BridgeChecks(unittest.TestCase):
    def test_invalid_model_answers_are_recoverable_sanitized_and_never_retried(self):
        from inventory_question_check import QuestionChecks
        cases = (
            (None, "missing response"), ("", "missing response"), (" \n", "missing response"),
            ("private malformed response", "invalid JSON"),
            ('{"answer":"private unfinished response', "invalid JSON"),
            ("[" * 2000, "invalid JSON"),
            ({"private": "unexpected output type"}, "invalid answer shape"),
            ("null", "invalid answer shape"), ("[]", "invalid answer shape"),
            ("{}", "invalid answer shape"), ('{"answer":17}', "invalid answer shape"),
            ('{"answer":"private response","extra":true}', "invalid answer shape"),
            ('{"answer":""}', "answer length outside 1-6000 characters"),
            (json.dumps({"answer": "x" * 6001}), "answer length outside 1-6000 characters"),
        )
        for output, reason in cases:
            with self.subTest(reason=reason, output_type=type(output).__name__):
                events = [] if output is None else [{"method": "item/completed", "params": {
                    "item": {"type": "agentMessage", "text": output}}}]
                events.append({"method": "turn/completed", "params": {"turn": {"status": "completed"}}})
                client, calls = QuestionChecks().client(events)
                with patch.object(companion, "emit") as emitted, self.assertRaises(companion.CompanionFailure) as failure:
                    client.ask({"version": 2, "question": "Synthetic question", "snapshot": None})
                self.assertEqual(companion.failure_message(failure.exception, "provider.request"), {
                    "error": f"Question returned an invalid answer ({reason}); no automatic retry",
                    "error_kind": "provider", "error_stage": "provider.request"})
                self.assertNotIn("private", str(failure.exception))
                self.assertFalse(any("question_answer" in call.args[0] for call in emitted.call_args_list))
                self.assertEqual([method for method, _ in calls].count("turn/start"), 1)
                self.assertEqual([method for method, _ in calls].count("turn/interrupt"), 1)

    def test_pasted_markdown_and_explicit_retry_preserve_text_and_repeat_preflight(self):
        from inventory_question_check import QuestionChecks
        markdown = "**123,456 gil**. [Gil wiki](https://ffxiv.consolegameswiki.com/wiki/Gil)"
        client, calls = QuestionChecks().client([
            {"method": "item/completed", "params": {"item": {"type": "agentMessage", "text": "invalid JSON"}}},
            {"method": "turn/completed", "params": {"turn": {"status": "completed"}}},
            {"method": "item/completed", "params": {"item": {"type": "agentMessage", "text": json.dumps({"answer": markdown})}}},
            {"method": "turn/completed", "params": {"turn": {"status": "completed"}}},
        ])
        request = {"version": 3, "question": markdown, "snapshot": None, "history": []}
        with patch.object(client, "preflight", wraps=client.preflight) as preflight, patch.object(companion, "emit") as emitted:
            with self.assertRaises(companion.CompanionFailure):
                client.ask(request)
            self.assertEqual(preflight.call_count, 1)
            self.assertEqual([method for method, _ in calls].count("turn/start"), 1)
            client.ask(request)  # A separate explicit send, never an automatic retry.
            self.assertEqual(preflight.call_count, 2)
        turns = [params for method, params in calls if method == "turn/start"]
        self.assertEqual(len(turns), 2)
        self.assertTrue(all(turn["input"][0]["text"] == markdown for turn in turns))
        self.assertEqual([call.args[0]["question_answer"] for call in emitted.call_args_list
                          if "question_answer" in call.args[0]], [markdown])
        for answer in ("x", "x" * 6000, "\U0001f600" * 6000):
            self.assertEqual(companion.parse_question_answer(json.dumps({"answer": answer})), {"answer": answer})

    def test_failure_categories_use_structured_codes_and_sanitized_messages(self):
        for error, expected in (
            ({"codexErrorInfo": "unauthorized"}, "authentication"),
            ({"codexErrorInfo": "usageLimitExceeded"}, "quota"),
            ({"data": {"codexErrorInfo": "rateLimitExceeded"}}, "quota"),
            ({"codexErrorInfo": {"httpConnectionFailed": {"httpStatusCode": 401}}}, "authentication"),
            ({"codexErrorInfo": {"responseStreamDisconnected": {"httpStatusCode": 429}}}, "quota"),
            ({"codexErrorInfo": "serverOverloaded"}, "provider"),
            ({"message": "unauthorized private text"}, "provider"),
            ({"code": -32602}, "protocol"),
        ):
            with self.subTest(error=error):
                self.assertEqual(companion.provider_failure_kind(error), expected)
        client = object.__new__(companion.Companion)
        client.cancel_event, client.seq = threading.Event(), 0
        client.send = lambda _: None
        client.receive = lambda *a, **k: {"error": {"code": -32000,
            "data": {"codexErrorInfo": "unauthorized"}, "message": "private secret"}}
        with self.assertRaises(companion.CompanionFailure) as failure:
            client.call("account/read")
        self.assertEqual(companion.failure_message(failure.exception),
            {"error": "account/read failed (RPC -32000)", "error_kind": "authentication"})

    def test_invalid_arguments_can_correct_within_same_turn(self):
        from deferred_fixture import client as deferred_client
        from quest_lookup_check import summary, arguments, request, result, tool_message, completed
        meta = summary()
        client, calls = deferred_client([
            tool_message(arguments(limit=0)), tool_message(ident=51), *completed()])
        responses, queries = [], []
        client.send = responses.append
        def query(args):
            queries.append(args)
            return result(meta, args)
        with patch.object(companion, "emit"):
            client.ask(request(meta), quest_query=query)
        self.assertEqual(len(queries), 1)
        self.assertTrue(client.discovery_replies)
        self.assertFalse(responses[0]["result"]["success"])
        self.assertEqual(json.loads(responses[0]["result"]["contentItems"][0]["text"])["error_kind"], "invalid_arguments")
        self.assertTrue(responses[1]["result"]["success"])
        self.assertEqual(sum(method == "turn/start" for method, _ in calls), 1)
        self.assertNotIn("turn/interrupt", [method for method, _ in calls])

    def test_unavailable_reader_returns_unknown_without_retry_or_raw_exception(self):
        from deferred_fixture import client as deferred_client
        from quest_lookup_check import summary, request, tool_message, completed
        def unavailable(_):
            raise TimeoutError("private reader exception")
        for query in (unavailable, lambda _: {"reader_unavailable": True}):
            client, calls = deferred_client([tool_message(), *completed()])
            responses = []
            client.send = responses.append
            with patch.object(companion, "emit"):
                client.ask(request(summary()), quest_query=query)
            text = responses[0]["result"]["contentItems"][0]["text"]
            self.assertTrue(client.discovery_replies)
            self.assertFalse(responses[0]["result"]["success"])
            self.assertEqual(json.loads(text)["error_kind"], "reader")
            self.assertNotIn("private", text)
            self.assertNotIn("turn/interrupt", [method for method, _ in calls])

    def test_recoverable_lookup_failures_still_consume_call_and_byte_budgets(self):
        from deferred_fixture import client as deferred_client
        from quest_lookup_check import summary, request, arguments, tool_message
        for limit in (companion.MAX_LOOKUP_TOTAL_BYTES, 1):
            client, calls = deferred_client([tool_message(arguments(limit=0), ident=i) for i in range(13)])
            responses = []
            client.send = responses.append
            with patch.object(companion, "emit"), patch.object(companion, "MAX_LOOKUP_TOTAL_BYTES", limit):
                with self.assertRaises(companion.CompanionFailure) as failure:
                    client.ask(request(summary()), quest_query=lambda _: self.fail("Invalid arguments reached reader"))
            self.assertEqual(failure.exception.kind, "reader")
            self.assertTrue(client.discovery_replies)
            self.assertEqual(len(responses), 13 if limit > 1 else 1)
            self.assertEqual(sum(method == "turn/start" for method, _ in calls), 1)
            self.assertIn("turn/interrupt", [method for method, _ in calls])

    def test_permission_and_malformed_reader_results_stop_without_correction(self):
        from deferred_fixture import client as deferred_client
        from quest_lookup_check import summary, request, tool_message
        def denied(_):
            raise PermissionError("private denial")
        for query, expected in ((denied, "permission"), (lambda _: {"reader_unavailable": 1}, "protocol"),
                                (lambda _: {"unexpected": True}, "protocol")):
            client, calls = deferred_client([tool_message()])
            with patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure) as failure:
                client.ask(request(summary()), quest_query=query)
            self.assertEqual(failure.exception.kind, expected)
            self.assertTrue(client.discovery_replies)
            self.assertEqual(sum(method == "turn/start" for method, _ in calls), 1)
            self.assertIn("turn/interrupt", [method for method, _ in calls])
        with self.assertRaises(companion.CompanionFailure) as failure:
            companion.require_lookup_permission("game_context", {"view": "location"}, {"location_allowed": False})
        self.assertEqual(failure.exception.kind, "permission")
        with self.assertRaises(companion.CompanionFailure):
            companion.require_lookup_permission("personality_details", {"section": "opinions"}, {"sections": ["background"]})

    def test_failed_turn_retains_provider_category_and_interrupts_once(self):
        from inventory_question_check import QuestionChecks
        for info, expected in (("unauthorized", "authentication"), ("usageLimitExceeded", "quota"),
                               ("serverOverloaded", "provider")):
            client, calls = QuestionChecks().client([{"method": "turn/completed", "params": {
                "turn": {"status": "failed", "error": {"codexErrorInfo": info, "message": "private details"}}}}])
            with patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure) as failure:
                client.ask({"version": 2, "question": "A synthetic question", "snapshot": None})
            self.assertEqual(failure.exception.kind, expected)
            self.assertNotIn("private", str(failure.exception))
            self.assertEqual([method for method, _ in calls].count("turn/start"), 1)
            self.assertEqual([method for method, _ in calls].count("turn/interrupt"), 1)

    def test_authentication_rejection_cannot_be_hidden_by_status_or_quota_refresh(self):
        client = object.__new__(companion.Companion)
        client.account = lambda refresh=False: {"type": "chatgpt"}
        def unauthorized(*_):
            raise companion.CompanionFailure("authentication", "Session rejected")
        client.call = unauthorized
        with patch.object(companion, "emit"), self.assertRaises(companion.CompanionFailure) as failure:
            client.status()
        self.assertEqual(failure.exception.kind, "authentication")
        payload = {"version": 2, "question": "Synthetic question", "snapshot": None}
        stream = io.StringIO(json.dumps({"request": payload}) + "\n")
        stream.reconfigure = lambda **_: None
        with patch.object(companion.sys, "argv", ["companion.py", "bridge", "ask"]), \
             patch.object(companion.sys, "stdin", stream), \
             patch.object(companion.threading, "Thread", side_effect=lambda target, **_: SimpleNamespace(start=target)), \
             patch.object(companion, "Companion") as constructor, patch.object(companion, "emit") as emitted:
            constructor.return_value.ask.side_effect = companion.CompanionFailure("quota", "Quota unavailable")
            constructor.return_value.status.side_effect = companion.CompanionFailure("authentication", "Session rejected")
            self.assertEqual(companion.main(), 1)
            constructor.return_value.ask.assert_called_once()
        self.assertEqual([call.args[0]["error_kind"] for call in emitted.call_args_list], ["quota", "authentication"])

    def test_explicit_profile_directory_is_independent_of_package_location(self):
        observed = []
        class FakeCompanion:
            def __init__(self, *_):
                observed.append(companion.PROFILE)
            def status(self):
                pass
            def close(self):
                pass
        with tempfile.TemporaryDirectory() as directory:
            stable = Path(directory).resolve() / "plugin-state"
            with patch.object(companion, "PROFILE", companion.PROFILE), patch.object(companion, "Companion", FakeCompanion):
                for release in ("release-one", "release-two"):
                    with patch("sys.argv", [str(Path(directory) / release / "companion.py"), "--profile-dir", str(stable), "status"]):
                        self.assertEqual(companion.main(), 0)
            self.assertEqual(observed, [stable, stable])

    def test_cancelled_call_and_cached_replies_do_not_progress(self):
        client = companion.Companion.__new__(companion.Companion)
        client.cancel_event = threading.Event()
        client.cancel_event.set()
        client.inbox = companion.RuntimeInbox()
        client.inbox.feed(b'{"id":1,"result":{}}\n')
        client.send = lambda *_: self.fail("Cancelled RPC must not be sent")
        with self.assertRaisesRegex(RuntimeError, "cancelled"):
            client.call("turn/start", {"threadId": "fixture"})
        for target in (None, 1):
            with self.assertRaisesRegex(RuntimeError, "cancelled"):
                client.receive(target)
        self.assertEqual(len(client.inbox.pending), 1)

    def test_cancellation_during_turn_start_interrupts_acknowledged_turn(self):
        client = companion.Companion.__new__(companion.Companion)
        client.cancel_event = threading.Event()
        client.inbox = companion.RuntimeInbox()
        client.seq = 0
        client.pin = {"binary_sha256": "fixture", "catalog_sha256": "fixture", "runtime_files": {}}
        client.preflight = lambda: {}
        sent = []

        def send(message):
            sent.append(message)
            if message["method"] == "turn/start":
                client.cancel_event.set()
                result = {"turn": {"id": "fixture-turn"}}
            else:
                result = {}
            client.inbox.feed(json.dumps({"id": message["id"], "result": result}).encode() + b'\n')

        def call(method, params=None, **kwargs):
            if method == "thread/start":
                return {"thread": {"id": "fixture-thread"}}
            if method == "mcpServerStatus/list":
                return {"data": []}
            return companion.Companion.call(client, method, params, **kwargs)

        client.send, client.call = send, call
        with patch.object(companion, "emit"), self.assertRaisesRegex(RuntimeError, "cancelled"):
            client.prove(force=True, save=False)
        self.assertEqual([m["method"] for m in sent], ["turn/start", "turn/interrupt"])
        self.assertEqual(sent[-1]["params"]["turnId"], "fixture-turn")
        self.assertTrue(client.cancel_event.is_set())

    def test_quota_limit_and_missing_window_fail_closed(self):
        for response in (
            {"rateLimits": {"primary": {"usedPercent": 100}}},
            {"rateLimits": {"primary": {"usedPercent": 61}, "secondary": {"usedPercent": 100}}},
            {"rateLimits": {"primary": {"usedPercent": None}}},
            {"rateLimits": {"primary": {"usedPercent": 10}, "rateLimitReachedType": "hard"}},
            {},
        ):
            with self.subTest(response=response), self.assertRaises(RuntimeError):
                companion.quota_windows(response)
        self.assertEqual(companion.quota_windows({"rateLimits": {
            "primary": {"usedPercent": 89}, "secondary": {"usedPercent": 70}}})["primary"]["usedPercent"], 89)
        self.assertEqual(companion.quota_windows({"rateLimits": {
            "primary": {"usedPercent": 90}}}, enforce=False)["primary"]["usedPercent"], 90)

    def test_status_and_preflight_agree_for_remaining_weekly_allowance(self):
        client = companion.Companion.__new__(companion.Companion)
        client.account = lambda refresh=False: {"type": "chatgpt", "planType": "plus"}
        for weekly, expected in ((91, "available"), (99, "available"), (100, "blocked")):
            response = {"rateLimitsByLimitId": {"codex": {"primary": {"usedPercent": 61},
                        "secondary": {"usedPercent": weekly}, "rateLimitReachedType": None}}}
            client.call = lambda *args: response
            with self.subTest(weekly=weekly), patch.object(companion, "emit"):
                status = client.status()
            self.assertEqual(status["quota_guard"], expected)
            if expected == "available":
                self.assertEqual(companion.quota_windows(response)["secondary"]["usedPercent"], weekly)
                self.assertNotIn("quota_block_reason", status)
            else:
                self.assertIn("Weekly usage", status["quota_block_reason"])

    def test_provider_limit_and_unavailable_quota_have_specific_status_reasons(self):
        client = companion.Companion.__new__(companion.Companion)
        client.account = lambda refresh=False: {"type": "chatgpt"}
        for response, message in (({"rateLimits": {"primary": {"usedPercent": 5}, "rateLimitReachedType": "hard"}}, "Usage limit reached"),
                                  ({}, "quota unavailable")):
            client.call = lambda *args: response
            with patch.object(companion, "emit"):
                status = client.status()
            self.assertEqual(status["quota_guard"], "blocked")
            self.assertIn(message, status["quota_block_reason"])

    def test_protocol_failure_and_cancellation(self):
        client = companion.Companion.__new__(companion.Companion)
        client.inbox = companion.RuntimeInbox()
        client.cancel_event = threading.Event()
        client.inbox.feed(b'{"protocol_error":"malformed"}\n')
        with self.assertRaisesRegex(RuntimeError, "Invalid App Server protocol"):
            client.receive(timeout=1)
        client.cancel_event.set()
        with self.assertRaisesRegex(RuntimeError, "cancelled"):
            client.receive(timeout=1)

    def test_signed_out_preflight_stops_before_model_turn(self):
        client = companion.Companion.__new__(companion.Companion)
        client.account = lambda: {"type": None}
        client.call = lambda *_: self.fail("No App Server turn may start while signed out")
        with self.assertRaisesRegex(RuntimeError, "Official ChatGPT login required"):
            client.preflight()

    def test_logout_uses_no_argument_rpc_and_verifies_signed_out(self):
        client = companion.Companion.__new__(companion.Companion)
        states = iter(("chatgpt", None))
        client.account = lambda refresh=False: {"type": next(states)}
        calls = []
        client.call = lambda method, params=None: calls.append((method, params)) or {}
        with patch.object(companion, "emit") as emitted:
            client.logout()
        self.assertEqual(calls, [("account/logout", None)])
        emitted.assert_called_once_with({"logout": "completed"})

    def test_login_command_checks_status_without_second_token_refresh(self):
        actions = []

        class FakeCompanion:
            def __init__(self, *_):
                pass
            def login(self, open_browser=True):
                actions.append(("login", open_browser))
            def status(self, *, refresh=True):
                actions.append(("status", refresh))
            def close(self):
                pass

        with patch.object(companion, "Companion", FakeCompanion), patch("sys.argv", ["companion.py", "login"]):
            self.assertEqual(companion.main(), 0)
        self.assertEqual(actions, [("login", True), ("status", False)])

    def test_grok_plugin_bridge_requests_default_browser_through_device_event(self):
        import grok_oauth
        import grok_provider
        from grok_auth_check import synthetic_token

        events = []
        closed = []
        responses = iter(({'device_code': 'SYNTHETIC-DEVICE-CODE-NOT-REAL',
            'user_code': 'TEST-CODE', 'verification_uri': grok_oauth.VERIFY_URL,
            'expires_in': 600, 'interval': 5},
            {'access_token': synthetic_token('bridge-browser')}))

        class Input(io.StringIO):
            def reconfigure(self, **kwargs):pass

        class FakeRuntime:
            def __init__(self, *args, cancel_event, emit, **kwargs):
                self.cancel_event, self.emit = cancel_event, emit
            def login(self, open_browser=True):
                # Exercise the real device event through main -> AccountCompanion;
                # only account transport/runtime work and the polling wait are fake.
                with patch.object(self.cancel_event, 'wait', return_value=False):
                    grok_oauth.device_authorize(self.cancel_event, self.emit,
                        open_browser=open_browser, transport=lambda *args: next(responses))
            def status(self):
                return {'provider': 'grok', 'runtime_pin': 'verified',
                    'account': {'type': 'grok'}, 'quota_guard': 'available', 'models': []}
            def close(self):closed.append(True)

        with patch.object(grok_provider, 'GrokRuntime', FakeRuntime), \
                patch.object(companion, 'emit', events.append), \
                patch('sys.stdin', Input()), \
                patch('sys.argv', ['companion.py', '--provider', 'grok', 'bridge', 'login']):
            self.assertEqual(companion.main(), 0)
        self.assertEqual([event for event in events if 'authUrl' in event], [{
            'provider': 'grok', 'login': 'waiting for user', 'user_code': 'TEST-CODE',
            'authUrl': grok_oauth.VERIFY_URL + '?user_code=TEST-CODE', 'openBrowser': True}])
        self.assertEqual(closed, [True])

    def test_stale_cached_login_opens_fresh_browser_flow(self):
        client = companion.Companion.__new__(companion.Companion)
        client.cancel_event = threading.Event()
        calls = []
        started = False

        def call(method, params=None, **kwargs):
            nonlocal started
            calls.append((method, params))
            if method == "account/read":
                # Saved account metadata looks signed in, but refresh invalidates the session.
                account = None if params["refreshToken"] and not started else {"type": "chatgpt", "planType": "pro"}
                return {"account": account}
            if method == "account/login/start":
                started = True
                return {"authUrl": "https://auth.openai.com/offline-fixture", "loginId": "fixture"}
            self.fail("Unexpected RPC: " + method)

        client.call = call
        client.receive = lambda **_: {"method": "account/login/completed", "params": {"loginId": "fixture", "success": True}}
        with patch.object(companion, "emit") as emitted:
            client.login(open_browser=False)
        self.assertEqual(calls[0], ("account/read", {"refreshToken": True}))
        self.assertTrue(started)
        self.assertNotIn(unittest.mock.call({"login": "already connected"}), emitted.call_args_list)
        emitted.assert_any_call({"login": "completed", "account": {"type": "chatgpt", "planType": "pro"}})

    def test_login_url_rejects_ambiguous_authorities_before_opening(self):
        for url in ("https://auth.openai.com/login?state=fixture", "https://chatgpt.com:443/login",
                    "https://auth.chatgpt.com/login"):
            with self.subTest(url=url):
                self.assertTrue(companion.official_login_url(url))
        for url in ("http://auth.openai.com/login", "https://auth.openai.com.attacker.test/login",
                    "https://user@auth.openai.com/login", "https://@auth.openai.com/login",
                    "https://auth.openai.com:444/login", "https://auth.openai.com:invalid/login",
                    "\nhttps://auth.openai.com/login", "https://auth.openai.com/\rlogin",
                    "https://auth.openai.com\\@attacker.test/login", None):
            with self.subTest(url=url):
                self.assertFalse(companion.official_login_url(url))
        client = object.__new__(companion.Companion)
        client.account = lambda refresh=False: {"type": None}
        calls = []
        def call(method, params):
            calls.append(method)
            return {"authUrl": "https://user@auth.openai.com/login", "loginId": "fixture"}
        client.call = call
        with patch.object(companion, "emit") as emitted, patch.object(companion.webbrowser, "open") as browser:
            with self.assertRaisesRegex(RuntimeError, "Unexpected login URL"):
                client.login()
        self.assertEqual(calls, ["account/login/start", "account/login/cancel"])
        emitted.assert_not_called()
        browser.assert_not_called()

    def test_valid_login_refreshes_before_already_connected(self):
        client = companion.Companion.__new__(companion.Companion)
        refreshes = []
        client.account = lambda refresh=False: refreshes.append(refresh) or {"type": "chatgpt"}
        client.call = lambda *_: self.fail("No browser login is needed for a validated session")
        with patch.object(companion, "emit") as emitted:
            client.login(open_browser=False)
        self.assertEqual(refreshes, [True])
        emitted.assert_called_once_with({"login": "already connected"})

    def test_login_connection_error_does_not_pretend_signed_in_or_open_browser(self):
        client = companion.Companion.__new__(companion.Companion)
        client.account = lambda refresh=False: (_ for _ in ()).throw(TimeoutError("connection unavailable"))
        client.call = lambda *_: self.fail("Do not start login on an unknown connection failure")
        with patch.object(companion, "emit") as emitted, self.assertRaises(TimeoutError):
            client.login(open_browser=False)
        emitted.assert_not_called()

    def test_quota_rpc_failure_keeps_verified_account_but_blocks_send(self):
        client = companion.Companion.__new__(companion.Companion)
        client.account = lambda refresh=False: {"type": "chatgpt", "planType": "pro"}
        client.available_models = lambda: []
        for failure in (RuntimeError("account/rateLimits/read failed (RPC -32000)"), TimeoutError()):
            client.call = lambda *_: (_ for _ in ()).throw(failure)
            with patch.object(companion, "emit"):
                status = client.status()
            self.assertEqual(status["account"]["type"], "chatgpt")
            self.assertEqual(status["quota_guard"], "blocked")
            self.assertNotIn("quota", status)
            self.assertTrue(status["quota_block_reason"])

    def test_login_recovers_when_completion_event_is_missed(self):
        client = companion.Companion.__new__(companion.Companion)
        client.cancel_event = threading.Event()
        calls = []
        client.account = lambda refresh=False: {"type": None}
        client.receive = lambda **kwargs: (_ for _ in ()).throw(TimeoutError("No event"))

        def call(method, params=None, **kwargs):
            calls.append((method, params))
            if method == "account/login/start":
                return {"authUrl": "https://auth.openai.com/offline-fixture", "loginId": "fixture"}
            if method == "account/read":
                return {"account": {"type": "chatgpt", "planType": "plus"}}
            self.fail("Unexpected RPC")

        client.call = call
        with patch.object(companion, "emit") as emitted, patch.object(companion.time, "monotonic", side_effect=[0, 0]):
            client.login(open_browser=False)
        self.assertIn(("account/read", {"refreshToken": True}), calls)
        emitted.assert_any_call({"login": "completed", "account": {"type": "chatgpt", "planType": "plus"}})
        self.assertNotIn("account/login/cancel", [method for method, _ in calls])

    def test_login_poll_does_not_ignore_cancellation(self):
        client = companion.Companion.__new__(companion.Companion)
        client.cancel_event = threading.Event()
        client.account = lambda refresh=False: {"type": None}

        def receive(**kwargs):
            client.cancel_event.set()
            raise TimeoutError("No event")

        def call(method, params=None, **kwargs):
            if method == "account/login/start":
                return {"authUrl": "https://auth.openai.com/offline-fixture", "loginId": "fixture"}
            if method == "account/read" and not kwargs.get("ignore_cancel"):
                raise RuntimeError("Operation cancelled")
            return {"account": {"type": None}}

        client.receive, client.call = receive, call
        with patch.object(companion, "emit"), patch.object(companion.time, "monotonic", side_effect=[0, 0]), \
                self.assertRaisesRegex(RuntimeError, "cancelled"):
            client.login(open_browser=False)

    def test_changed_runtime_pin_fails_before_launch(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            catalog = root / "catalog.json"
            executable = root / "codex.exe"
            catalog.write_text("{}", encoding="utf-8")
            executable.write_bytes(b"changed binary")
            (root / "runtime-pin.json").write_text(json.dumps({
                "binary_sha256": "old hash", "catalog_sha256": "old hash"}), encoding="utf-8")
            (root / "validation-success.json").write_text("{}", encoding="utf-8")
            with patch.object(companion, "CATALOG", catalog):
                with self.assertRaisesRegex(RuntimeError, "pin changed"):
                    companion.checked_pin(executable)

    def test_private_runtime_ignores_desktop_and_path(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            bin_folder = root / "runtime" / "win-x64"
            bin_folder.mkdir(parents=True)
            executable = bin_folder / "codex.exe"
            executable.write_bytes(b"validated fixture")
            (bin_folder / "codex-code-mode-host.exe").write_bytes(b"private host fixture")
            with patch.object(companion, "ROOT", root), \
                    patch.dict(companion.os.environ, {"LOCALAPPDATA": str(root / "absent-desktop"), "PATH": ""}):
                self.assertEqual(companion.find_runtime(), executable.resolve())
                executable.unlink()
                with self.assertRaises(RuntimeError):
                    companion.find_runtime()

    def test_cached_proof_is_bound_to_code_host_too(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            client = companion.Companion.__new__(companion.Companion)
            client.pin = {"binary_sha256": "binary", "catalog_sha256": "catalog",
                          "runtime_files": {"codex.exe": "binary", "codex-code-mode-host.exe": "host"}}
            client.preflight = lambda: {}
            saved = {"passed": True, **client.pin}
            result_path = root / "proof-result.json"
            result_path.write_text(json.dumps(saved), encoding="utf-8")
            with patch.object(companion, "PROFILE", root), patch.object(companion, "emit"), \
                 patch.object(client, "call", side_effect=RuntimeError("fresh proof required")) as call:
                client.prove()
                call.assert_not_called()
                for runtime_files in ({}, {"codex.exe": "binary", "codex-code-mode-host.exe": "old host"}):
                    result_path.write_text(json.dumps({**saved, "runtime_files": runtime_files}), encoding="utf-8")
                    with self.assertRaisesRegex(RuntimeError, "fresh proof required"):
                        client.prove()


if __name__ == "__main__":
    unittest.main()
