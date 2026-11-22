"""Synthetic Grok Unicode boundaries; no runtime process, account or network."""
from collections import deque
import io
import json
import threading
from types import SimpleNamespace
import unittest

from companion import parse_question_answer
from companion_provider import RuntimeInbox
from grok_provider import GrokRuntime, GrokTurn


MACRO = ('/echo "Cafe\u0301 and caf\u00e9"\n'
         '/echo "\u0391\u03bb\u03c6\u03b1 \u201cquotes\u201d"\n'
         '/echo "finished" <wait.1>')


class GrokUnicodeChecks(unittest.TestCase):
    def test_question_and_context_reach_wire_as_unicode(self):
        runtime = GrokRuntime.__new__(GrokRuntime)
        runtime.sid, runtime.active, runtime.seq = 'synthetic', False, 0
        runtime.write_lock = threading.Lock()
        runtime.inbox = RuntimeInbox(label='Grok')
        runtime.process = SimpleNamespace(stdin=io.BytesIO())
        runtime.start('synthetic', MACRO, {'text': MACRO}, None, lambda _: None)
        wire = runtime.process.stdin.getvalue()
        prompt = json.loads(wire)['params']['prompt'][0]['text']
        self.assertTrue(prompt.startswith(MACRO))
        self.assertEqual(json.loads(prompt.split('\n')[-1])['text'], MACRO)
        self.assertIn('café'.encode(), wire)
        self.assertNotIn(b'\\u00e9', wire)

    def test_structured_answers_decode_once_in_preview_and_final(self):
        for expected in (MACRO, r'/echo "literal \u0041 and C:\temp"'):
            for ascii_json in (False, True):
                with self.subTest(text=expected, ascii_json=ascii_json):
                    turn, answer = self.complete(json.dumps({'answer': expected}, ensure_ascii=ascii_json))
                    self.assertEqual(answer, expected)
                    self.assertEqual(turn.decoder.text, expected)

    def test_plain_text_fallback_preserves_commands(self):
        for expected in (MACRO, r'/echo "literal \u0041"'):
            with self.subTest(text=expected):
                _, answer = self.complete(expected)
                self.assertEqual(answer, expected)

    @staticmethod
    def complete(content):
        messages = deque([
            {'method': 'session/update', 'params': {'sessionId': 'synthetic', 'update': {
                'sessionUpdate': 'agent_message_chunk', 'content': {'text': content}}}},
            {'id': 1, 'result': {'stopReason': 'end_turn'}},
        ])
        runtime = SimpleNamespace(active=True, receive=lambda _: messages.popleft())
        turn = GrokTurn(runtime, 'synthetic', 1, lambda _: None)
        turn.read(0)
        final = turn.read(0)
        return turn, parse_question_answer(final.text)['answer']


if __name__ == '__main__':
    unittest.main()
