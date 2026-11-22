"""Offline preview decoder, event isolation, and companion wiring fixtures only."""
import json
import unittest
from unittest.mock import patch

import companion
import inventory_question_check as question_checks
from streamed_answer import AnswerPreviewDecoder, AnswerPreviewStream, MAX_ENCODED_CHARS


def event(method, *, item=None, **params):
    return {"method": method, "params": {"threadId": "fixture-thread", "turnId": "fixture-turn",
                                         **({"item": item} if item is not None else {}), **params}}


def started(**changes):
    return event("item/started", item={"type": "agentMessage", "id": "answer-1", "text": "",
                                       "phase": "final_answer", **changes})


def delta(text, **changes):
    return event("item/agentMessage/delta", itemId="answer-1", delta=text, **changes)


def completed(text, **changes):
    return event("item/completed", item={"type": "agentMessage", "id": "answer-1", "text": text,
                                         "phase": "final_answer", **changes})


def turn_completed(status="completed"):
    return event("turn/completed", turn={"status": status})


class DecoderChecks(unittest.TestCase):
    def test_every_split_preserves_escapes_and_unicode(self):
        text = 'Quote: " slash: \\ / newline:\n tab:\t controls:\b\f\r snow:雪 emoji:😀'
        for ascii_only in (True, False):
            encoded = json.dumps({"answer": text}, ensure_ascii=ascii_only)
            for split in range(len(encoded) + 1):
                with self.subTest(ascii_only=ascii_only, split=split):
                    decoder = AnswerPreviewDecoder()
                    decoder.feed(encoded[:split])
                    decoder.feed(encoded[split:])
                    self.assertTrue(decoder.complete)
                    self.assertEqual(decoder.text, text)
                    self.assertFalse(any(0xD800 <= ord(c) <= 0xDFFF for c in decoder.text))

    def test_character_chunks_withhold_incomplete_escape_and_surrogate(self):
        decoder = AnswerPreviewDecoder()
        decoder.feed('{"answer":"safe\\uD83D')
        self.assertEqual(decoder.text, "safe")
        for character in "\\uDE0":
            decoder.feed(character)
            self.assertEqual(decoder.text, "safe")
        decoder.feed('0"}')
        self.assertEqual(decoder.text, "safe😀")
        self.assertTrue(decoder.complete)
        decoder = AnswerPreviewDecoder()
        decoder.feed('{"answer":"\ud83d')
        self.assertEqual(decoder.text, "")
        decoder.feed('\ude00"}')
        self.assertEqual(decoder.text, "😀")

    def test_whitespace_and_valid_json_escapes(self):
        decoder = AnswerPreviewDecoder()
        decoder.feed(' \n{\t"answer" \r: "a\\/b\\u0063"\n}\r ')
        self.assertEqual(decoder.text, "a/bc")
        self.assertTrue(decoder.complete)

    def test_malformed_shapes_escapes_and_surrogates_are_rejected(self):
        invalid = ['[]', '{}', '{"other":"x"}', '{"answer":null}', '{"answer":1}',
                   '{"answer":["x"]}', '{"answer":{"x":1}}', '{"answer":"x","extra":1}',
                   '{"answer":"x","answer":"y"}', '{"answer":"x"} more',
                   '{"answer":"bad\\x"}', '{"answer":"bad\\uQ000"}', '{"answer":"raw\n"}',
                   '{"answer":"\\uD800"}', '{"answer":"\\uDC00"}', '{"answer":"\\uD800x"}',
                   '{"answer":"\\uD800\\n"}', '{"answer":"\\uD800\\uD800"}',
                   '{"answer":"\udc00"}', '{"answer":"\ud800x"}']
        for value in invalid:
            with self.subTest(value=repr(value)), self.assertRaises(ValueError):
                AnswerPreviewDecoder().feed(value)

    def test_incomplete_documents_never_report_complete(self):
        for value in ('', '{', '{"answer', '{"answer":', '{"answer":"',
                      '{"answer":"x\\', '{"answer":"x\\u12', '{"answer":"x"'):
            decoder = AnswerPreviewDecoder()
            decoder.feed(value)
            self.assertFalse(decoder.complete)

    def test_encoded_and_decoded_bounds_count_non_bmp_as_one(self):
        decoder = AnswerPreviewDecoder()
        decoder.feed(json.dumps({"answer": "😀" * 6000}))
        self.assertTrue(decoder.complete)
        self.assertEqual(len(decoder.text), 6000)
        for encoded in (json.dumps({"answer": "x" * 6001}), json.dumps({"answer": "😀" * 6001}),
                        " " * (MAX_ENCODED_CHARS + 1)):
            with self.subTest(size=len(encoded)), self.assertRaises(ValueError):
                AnswerPreviewDecoder().feed(encoded)
        decoder = AnswerPreviewDecoder()
        decoder.feed(" " * MAX_ENCODED_CHARS)
        with self.assertRaises(ValueError):
            decoder.feed(" ")


class StreamChecks(unittest.TestCase):
    def setUp(self):
        self.output = []
        self.now = 0.0
        self.stream = AnswerPreviewStream("fixture-thread", "fixture-turn", self.output.append,
                                          clock=lambda: self.now)

    def handle(self, message):
        self.stream.handle(message["method"], message["params"])

    def test_only_explicit_final_answer_and_exact_ids_stream(self):
        for phase in (None, "commentary", "other"):
            self.handle(started(phase=phase))
            self.handle(delta('{"answer":"hidden'))
        self.handle(started(type="reasoning"))
        self.handle(delta('{"answer":"hidden'))
        for key in ("threadId", "turnId"):
            for wrong in (None, "wrong"):
                message = started()
                message["params"][key] = wrong
                self.handle(message)
        self.assertEqual(self.output, [])
        self.handle(started())
        for key in ("threadId", "turnId", "itemId"):
            for wrong in (None, "wrong"):
                message = delta('{"answer":"hidden')
                message["params"][key] = wrong
                self.handle(message)
        self.assertEqual(self.output, [])
        self.handle(delta('{"answer":"visible'))
        self.assertEqual(self.output, [{"question_partial": "visible"}])

    def test_rate_limit_coalesces_complete_text_and_clear_bypasses_limit(self):
        self.handle(started())
        self.handle(delta('{"answer":"a'))
        self.now = .05
        self.handle(delta("b"))
        self.assertEqual(self.output, [{"question_partial": "a"}])
        self.now = .101
        self.handle(delta("c"))
        self.assertEqual(self.output[-1], {"question_partial": "abc"})
        self.stream.finish()
        self.assertEqual(self.output[-1], {"question_partial": ""})
        self.stream.finish()
        self.handle(delta("late"))
        self.assertEqual(len(self.output), 3)

    def test_malformed_stream_clears_and_cannot_restart(self):
        self.handle(started())
        self.handle(delta('{"answer":"shown'))
        self.handle(delta('" ,"extra":1}'))
        self.assertEqual(self.output, [{"question_partial": "shown"}, {"question_partial": ""}])
        self.handle(started(id="answer-2"))
        self.now = 1
        self.handle(delta('{"answer":"late"}'))
        self.assertEqual(len(self.output), 2)

    def test_other_assistant_item_clears_without_combining(self):
        self.handle(started())
        self.handle(delta('{"answer":"first'))
        self.handle(started(id="other", phase="commentary"))
        self.assertEqual(self.output[-1], {"question_partial": ""})
        self.handle(delta("second"))
        self.assertEqual(len(self.output), 2)

    def test_tool_and_reasoning_events_never_become_answer_text(self):
        self.handle(started())
        self.handle(delta('{"answer":"public'))
        self.handle(event("item/reasoning/textDelta", itemId="answer-1", delta="secret"))
        self.handle(event("item/tool/call", tool="query_inventory", arguments={}))
        self.handle(event("item/completed", item={"id": "tool", "type": "webSearch", "text": "tool"}))
        self.assertEqual(self.output, [{"question_partial": "public"}])

    def test_malformed_initial_text_and_competing_completed_item_disable_preview(self):
        for invalid in (None, False, [], 0):
            with self.subTest(initial=invalid):
                self.setUp()
                self.handle(started(text=invalid))
                self.handle(delta('{"answer":"hidden"}'))
                self.assertEqual(self.output, [])
                self.assertTrue(self.stream.disabled)
        self.setUp()
        self.handle(started())
        self.handle(delta('{"answer":"shown"}'))
        self.handle(completed('{"answer":"other"}', id="other"))
        self.assertEqual(self.output[-1], {"question_partial": ""})

    def test_completed_mismatch_invalid_json_and_incomplete_stream_clear(self):
        cases = [('{"answer":"preview"}', '{"answer":"different"}'),
                 ('{"answer":"preview"}', '{"answer":"preview","extra":0}'),
                 ('{"answer":"preview', '{"answer":"preview"}')]
        for streamed, final in cases:
            with self.subTest(final=final):
                self.setUp()
                self.handle(started())
                self.handle(delta(streamed))
                self.handle(completed(final))
                self.assertEqual(self.output[-1], {"question_partial": ""})

    def test_completed_match_retains_preview_until_finish_and_rejects_late_delta(self):
        self.handle(started())
        self.handle(delta('{"answer":"ready"}'))
        self.handle(completed('{"answer":"ready"}'))
        self.assertEqual(self.output, [{"question_partial": "ready"}])
        self.handle(delta("late"))
        self.assertEqual(self.output[-1], {"question_partial": ""})

    def test_oversized_or_non_string_delta_clears_visible_preview(self):
        for invalid in (None, 5, {}, " " * (MAX_ENCODED_CHARS + 1)):
            with self.subTest(kind=type(invalid).__name__):
                self.setUp()
                self.handle(started())
                self.handle(delta('{"answer":"shown'))
                self.handle(delta(invalid))
                self.assertEqual(self.output[-1], {"question_partial": ""})


class CompanionPreviewChecks(unittest.TestCase):
    payload = {"version": 2, "question": "Explain a synthetic example.", "snapshot": None}

    def run_question(self, messages):
        client, calls = question_checks.QuestionChecks().client(messages)
        output = []
        with patch.object(companion, "emit", side_effect=output.append):
            client.ask(self.payload)
        return calls, output

    def test_streaming_does_not_change_provider_requests_or_final_answer(self):
        text = '{"answer":"Same final answer."}'
        baseline_calls, baseline = self.run_question([completed(text), turn_completed()])
        calls, output = self.run_question([started(), delta(text), completed(text), turn_completed()])
        self.assertEqual(calls, baseline_calls)
        self.assertEqual(len([method for method, _ in calls if method == "turn/start"]), 1)
        self.assertEqual(output[-1], baseline[-1])
        self.assertEqual([row for row in output if "question_partial" in row],
                         [{"question_partial": "Same final answer."}, {"question_partial": ""}])

    def test_missing_phase_falls_back_to_unchanged_final_answer(self):
        text = '{"answer":"Final only."}'
        _, output = self.run_question([started(phase=None), delta(text), completed(text), turn_completed()])
        self.assertFalse(any("question_partial" in row for row in output))
        self.assertIn("Final only.", output[-1]["question_answer"])

    def test_disagreeing_final_replaces_preview_without_changing_acceptance(self):
        _, output = self.run_question([started(), delta('{"answer":"Draft."}'),
                                       completed('{"answer":"Final."}'), turn_completed()])
        self.assertIn({"question_partial": ""}, output)
        self.assertIn("Final.", output[-1]["question_answer"])

    def test_failed_turn_invalid_final_and_cancel_clear_without_saving(self):
        cases = [[started(), delta('{"answer":"shown"}'), completed('{"answer":"shown"}'),
                  turn_completed("interrupted")],
                 [started(), delta('{"answer":"shown"}'), completed('{"answer":42}'), turn_completed()],
                 [started(), delta('{"answer":"shown')]]
        for messages in cases:
            with self.subTest(count=len(messages)):
                client, calls = question_checks.QuestionChecks().client(messages)
                receive = client.receive
                def cancel_after_messages(**kwargs):
                    try:
                        return receive(**kwargs)
                    except IndexError:
                        client.cancel_event.set()
                        raise RuntimeError("Operation cancelled") from None
                client.receive = cancel_after_messages
                output = []
                with patch.object(companion, "emit", side_effect=output.append), self.assertRaises(RuntimeError):
                    client.ask(self.payload)
                self.assertEqual([row for row in output if "question_partial" in row][-1], {"question_partial": ""})
                self.assertEqual(output[-1]["performance"]["stage"], "provider.tokens")
                self.assertFalse(any("question_answer" in row for row in output))
                self.assertEqual(len([method for method, _ in calls if method == "turn/start"]), 1)
                self.assertIn("turn/interrupt", [method for method, _ in calls])


if __name__ == "__main__":
    unittest.main()
