"""Focused offline persona-note validation and private-pipe checks; no provider requests."""
import copy
import threading
import unittest

from inventory_question import validate_request
from personality_details_lookup import SECTIONS, validate_arguments, validate_result, validate_summary
from quest_lookup import QuestQueryBridge


def request(**changes):
    return {"version": 14, "question": "What does the character think about cooking?", "snapshot": None,
            "history": [], "quest_lookup": None, "model": "gpt-6-sol", "reasoning_effort": "medium",
            "web_search": False, "inventory_lookup": None, "chat_notes": "", "history_lookup": None,
            "personality": {"name": "Armo", "instructions": "Be concise."}, "game_context_lookup": None,
            "spoilers_allowed": False, "npc_dialogue_lookup": None, "character_details_lookup": None,
            "personality_details_lookup": {"sections": ["opinions"]}, **changes}


class PersonalityDetailsChecks(unittest.TestCase):
    def test_v14_metadata_only_exact_shape_and_old_request_compatibility(self):
        value = request()
        self.assertEqual(validate_request(value), (value["question"], None))
        missing = copy.deepcopy(value)
        del missing["personality_details_lookup"]
        with self.assertRaises(RuntimeError):
            validate_request(missing)
        for version in (10, 11, 12, 13):
            old = copy.deepcopy(missing)
            old["version"] = version
            if version < 13:
                for field in ("spoilers_allowed", "npc_dialogue_lookup", "character_details_lookup"):
                    del old[field]
            if version < 11:
                del old["game_context_lookup"]
            self.assertEqual(validate_request(old), (old["question"], None))
            old["personality_details_lookup"] = None
            with self.subTest(version=version), self.assertRaises(RuntimeError):
                validate_request(old)
        with self.assertRaises(RuntimeError):
            validate_request(request(personality_details_lookup={"sections": ["opinions"], "text": "PRIVATE NOTE"}))

    def test_no_persona_cannot_grant_notes(self):
        validate_request(request(personality=None, personality_details_lookup=None))
        with self.assertRaises(RuntimeError):
            validate_request(request(personality=None))

    def test_summary_rejects_empty_unknown_duplicate_or_injected_sections(self):
        self.assertIsNone(validate_summary(None))
        for value in ({"sections": []}, {"sections": ["unknown"]}, {"sections": ["opinions", "opinions"]},
                      {"sections": "opinions"}, {"sections": [True]}, {"sections": [[]]},
                      {"sections": ["opinions"], "notes": "PRIVATE NOTE"}):
            with self.subTest(value=value), self.assertRaises(RuntimeError):
                validate_summary(value)
        original = {"sections": list(SECTIONS)}
        frozen = validate_summary(original)
        original["sections"].clear()
        self.assertEqual(frozen["sections"], list(SECTIONS))

    def test_arguments_require_available_section_and_exact_fields(self):
        summary = {"sections": ["opinions"]}
        self.assertEqual(validate_arguments({"section": "opinions"}, summary), {"section": "opinions"})
        for value in ({}, {"section": "background"}, {"section": "unknown"}, {"section": True},
                      {"section": "opinions", "text": "PRIVATE NOTE"}):
            with self.subTest(value=value), self.assertRaises(RuntimeError):
                validate_arguments(value, summary)

    def test_result_matches_request_and_never_reads_unavailable_notes(self):
        summary, args = {"sections": ["opinions"]}, {"section": "opinions"}
        good = {"section": "opinions", "text": "Enjoys discussing recipes."}
        self.assertEqual(validate_result(good, summary, args), good)
        for value in ({**good, "section": "background"}, {**good, "extra": "bad"},
                      {"section": "opinions"}, {**good, "text": None}):
            with self.subTest(value=value), self.assertRaises(RuntimeError):
                validate_result(value, summary, args)
        with self.assertRaises(RuntimeError):
            validate_result(good, None, args)
        with self.assertRaises(RuntimeError):
            validate_result(good, {"sections": ["background"]}, args)

    def test_utf16_limit_matches_csharp_and_preserves_multiline_unicode(self):
        summary, args = {"sections": ["opinions"]}, {"section": "opinions"}
        for text in ("a" * 2000, "\U0001f431" * 1000, "Preferă ceaiul.\nDislikes\tnoise."):
            self.assertEqual(validate_result({**args, "text": text}, summary, args)["text"], text)
        for text in ("a" * 2001, "\U0001f431" * 1001, "bad\x00note", "\ud800", "", " \r\n\t"):
            with self.subTest(length=len(text)), self.assertRaises(RuntimeError):
                validate_result({**args, "text": text}, summary, args)

    def test_private_bridge_correlation_and_cancellation(self):
        cancelled, ready = threading.Event(), threading.Event()
        sent, received, errors = [], [], []
        bridge = QuestQueryBridge(cancelled, lambda value: (sent.append(value), ready.set()), timeout=2)
        args, result = {"section": "opinions"}, {"section": "opinions", "text": "Likes cooking."}
        def run():
            try:
                received.append(bridge.query(args, "personality_details"))
            except Exception as error:
                errors.append(error)
        worker = threading.Thread(target=run)
        worker.start()
        try:
            self.assertTrue(ready.wait(1))
            self.assertEqual(sent, [{"personality_details_query": {"id": 1, "arguments": args}}])
            for value in ({"quest_result": {"id": 1, "result": result}},
                          {"personality_details_result": {"id": 2, "result": result}}):
                with self.assertRaises(RuntimeError):
                    bridge.accept(value)
            reply = {"personality_details_result": {"id": 1, "result": result}}
            bridge.accept(reply)
            worker.join(1)
            self.assertFalse(worker.is_alive())
            self.assertFalse(errors)
            self.assertEqual(received, [result])
            with self.assertRaises(RuntimeError):
                bridge.accept(reply)
        finally:
            cancelled.set()
            worker.join(1)
        with self.assertRaises(RuntimeError):
            bridge.query(args, "personality_details")


if __name__ == "__main__":
    unittest.main()
