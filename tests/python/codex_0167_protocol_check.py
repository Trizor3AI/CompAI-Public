"""Selected 0.167 wire changes through the real bridge with synthetic transport.

Pinned upstream commit 086ccc833bc9cf01024ad2dd6af7dbd421b6bf9a:
app-server-protocol/src/protocol/v2/item.rs flattens lifecycle times into items;
account.rs adds an optional workspace name and another account type.
No provider process, account, game state or network is used here.
"""
import json
import unittest
from unittest.mock import patch

import companion
from companion_provider import CodexProvider
import inventory_question_check as inventory_checks


class UpdatedWireChecks(unittest.TestCase):
    def test_flat_item_lifecycle_times_preserve_answer_and_one_send(self):
        for timing in ({}, {"startedAtMs": None, "completedAtMs": None},
                       {"startedAtMs": 1794080000000, "completedAtMs": 1794080000123}):
            with self.subTest(timing=timing):
                client, calls = inventory_checks.QuestionChecks().client([
                    {"method": "item/completed", "params": {"threadId": "fixture-thread",
                        "turnId": "fixture-turn", "item": {"id": "message-fixture", "type": "agentMessage",
                            "text": json.dumps({"answer": "Synthetic completed answer."}), **timing}}},
                    {"method": "turn/completed", "params": {"threadId": "fixture-thread",
                        "turn": {"id": "fixture-turn", "status": "completed", "error": None}}},
                ])
                with patch.object(companion, "emit") as emitted:
                    client.ask({"version": 2, "question": "Synthetic protocol check", "snapshot": None})
                answers = [event.args[0] for event in emitted.call_args_list if "question_answer" in event.args[0]]
                self.assertEqual(len(answers), 1)
                self.assertEqual(answers[0]["question_answer"], "Synthetic completed answer.\n\n"
                                 "No inventory snapshot supplied; owned items and quantities are unavailable.")
                self.assertNotIn("startedAtMs", answers[0])
                self.assertNotIn("completedAtMs", answers[0])
                self.assertEqual(sum(method == "turn/start" for method, _ in calls), 1)

    def test_optional_workspace_and_account_email_are_not_projected(self):
        for workspace in (None, "PRIVATE_SYNTHETIC_WORKSPACE"):
            client = object.__new__(companion.Companion)
            calls = []
            def call(method, params):
                calls.append((method, params))
                return {"account": {"type": "chatgpt", "planType": "plus",
                    "email": "synthetic@example.invalid", "workspaceName": workspace}}
            client.call = call
            self.assertEqual(client.account(), {"type": "chatgpt", "planType": "plus"})
            self.assertEqual(calls, [("account/read", {"refreshToken": False})])

    def test_other_account_variant_cannot_fall_back_to_paid_provider(self):
        client = object.__new__(companion.Companion)
        calls = []
        def call(method, params):
            calls.append(method)
            return {"account": {"type": "gcp", "projectId": "synthetic-project"}}
        client.call = call
        with self.assertRaises(companion.CompanionFailure) as failure:
            CodexProvider(client, ".").readiness()
        self.assertEqual(failure.exception.kind, "authentication")
        self.assertEqual(calls, ["account/read"])


if __name__ == "__main__":
    unittest.main()
