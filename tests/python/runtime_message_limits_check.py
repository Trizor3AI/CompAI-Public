"""Synthetic pipe and queue bounds. No provider, account, game or network access."""
import io
import json
import queue
import threading
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import companion
from grok_provider import GrokRuntime


def frame(value):
    return json.dumps(value, ensure_ascii=False).encode('utf-8') + b'\n'


def client(stream, **limits):
    result = companion.Companion.__new__(companion.Companion)
    result.inbox = companion.RuntimeInbox(**limits)
    result.process = SimpleNamespace(stdout=stream, stdin=io.BytesIO())
    result.cancel_event = threading.Event()
    return result


class ReadSpy(io.BytesIO):
    def __init__(self, data):
        super().__init__(data)
        self.sizes = []

    def readline(self, size=-1):
        self.sizes.append(size)
        return super().readline(size)


class RuntimeMessageLimitsChecks(unittest.TestCase):
    def test_pipe_stops_before_allocating_an_oversized_line(self):
        for newline in (b'', b'\n'):
            stream = ReadSpy(b'x' * 4096 + newline)
            value = client(stream, frame_limit=64)
            with patch.object(companion, 'MAX_RUNTIME_FRAME_BYTES', 64):
                value._read()
            self.assertEqual(stream.sizes, [65])
            self.assertEqual(stream.tell(), 65)
            with self.assertRaisesRegex(companion.CompanionFailure, 'size limit'):
                value.receive()
            self.assertEqual(value.inbox.pending_bytes, 0)

    def test_exact_frame_limit_multibyte_and_final_eof_line(self):
        for ending in (b'\n', b'\r\n', b''):
            raw = frame({'id': 1, 'result': '猫🙂'}).rstrip(b'\n') + ending
            value = client(ReadSpy(raw), frame_limit=len(raw))
            with patch.object(companion, 'MAX_RUNTIME_FRAME_BYTES', len(raw)):
                value._read()
            self.assertEqual(value.receive(1)['result'], '猫🙂')
            for _ in range(2):
                with self.assertRaisesRegex(companion.CompanionFailure, 'exited'):
                    value.receive()
            self.assertEqual(value.inbox.pending_bytes, 0)

    def test_byte_and_count_limits_are_terminal_and_out_of_band(self):
        raw = frame({'id': 1, 'result': {}})
        for inbox in (companion.RuntimeInbox(byte_limit=len(raw) * 2, count_limit=10),
                      companion.RuntimeInbox(byte_limit=4096, count_limit=2)):
            self.assertTrue(inbox.feed(raw))
            self.assertTrue(inbox.feed(raw))
            self.assertFalse(inbox.feed(raw))
            self.assertFalse(inbox.feed(raw))
            self.assertEqual(inbox.pending_bytes, 0)
            self.assertFalse(inbox.pending)
            with self.assertRaisesRegex(companion.CompanionFailure, 'memory limits'):
                inbox.get(1)

    def test_unmatched_frames_stay_charged_and_ordered(self):
        first, second = frame({'id': 1}), frame({'id': 2})
        inbox = companion.RuntimeInbox(byte_limit=len(first) + len(second), count_limit=2)
        inbox.feed(first)
        with self.assertRaises(queue.Empty):
            inbox.get(2, timeout=.001)
        self.assertEqual(inbox.pending_bytes, len(first))
        inbox.feed(second)
        self.assertEqual(inbox.get(2), {'id': 2})
        self.assertEqual(inbox.pending_bytes, len(first))
        self.assertEqual(inbox.get(), {'id': 1})
        self.assertEqual(inbox.pending_bytes, 0)
        self.assertTrue(inbox.feed(second))
        with self.assertRaises(queue.Empty):
            inbox.get(3, timeout=.001)
        inbox.feed(first)
        self.assertFalse(inbox.feed(second))
        with self.assertRaisesRegex(companion.CompanionFailure, 'memory limits'):
            inbox.get(2)

    def test_grok_wait_services_reverse_requests_and_preserves_notifications(self):
        runtime = GrokRuntime.__new__(GrokRuntime)
        runtime.process = SimpleNamespace(stdin=io.BytesIO())
        runtime.write_lock, runtime.cancel_event = threading.Lock(), threading.Event()
        runtime.inbox, runtime.seq, runtime.active = companion.RuntimeInbox(label='Grok'), 0, False
        notice = {'method': 'progress'}
        # Both sides may start their RPC counters at 1.
        reverse = {'method': 'session/request_permission', 'id': 1}
        reply = {'id': 1, 'result': {'ok': True}}
        for item in (notice, reverse, reply):
            runtime.inbox.feed(frame(item))
        self.assertEqual(runtime.call('synthetic'), {'ok': True})
        outbound = [json.loads(row) for row in runtime.process.stdin.getvalue().splitlines()]
        self.assertEqual(outbound[-1]['result']['outcome'], {'outcome': 'cancelled'})
        self.assertEqual(runtime.inbox.pending_bytes, len(frame(notice)))
        self.assertEqual(runtime.receive(), notice)

    def test_grok_reader_reports_overflow_and_incomplete_lines_after_exit(self):
        raw = frame({'method': 'progress'})
        for payload, reason in ((raw * 2, 'memory limits'), (raw[:-1], 'Invalid Grok protocol')):
            runtime = GrokRuntime.__new__(GrokRuntime)
            inbox = companion.RuntimeInbox(label='Grok', byte_limit=len(raw))
            runtime._read(SimpleNamespace(stdout=io.BytesIO(payload)), inbox)
            with self.assertRaisesRegex(companion.CompanionFailure, reason):
                inbox.get(1)
            self.assertEqual(inbox.pending_bytes, 0)
            with self.assertRaisesRegex(companion.CompanionFailure, reason):
                inbox.require_writable()

    def test_many_small_notifications_preserve_sequence_and_free_credit(self):
        inbox = companion.RuntimeInbox()
        for round_number in range(2):
            for index in range(1000):
                self.assertTrue(inbox.feed(frame({'method': 'progress', 'params': {'index': index}})))
            for index in range(1000):
                self.assertEqual(inbox.get()['params']['index'], index)
            self.assertEqual(inbox.pending_bytes, 0)

    def test_raw_pending_payloads_do_not_retain_decoded_trees(self):
        inbox = companion.RuntimeInbox()
        raw = frame({'id': 'other', 'result': [{} for _ in range(1000)]})
        inbox.feed(raw)
        with self.assertRaises(queue.Empty):
            inbox.get('waiting', timeout=.001)
        self.assertEqual(inbox.pending[0], [raw, 'other'])

    def test_malformed_utf8_json_deep_objects_and_invalid_ids_are_sanitized(self):
        secret = 'PRIVATE-FIXTURE-CONTENT'
        for raw in (b'\xff' + secret.encode(), ('{"secret":"' + secret).encode(),
                    b'[' * 2000 + b'0' + b']' * 2000,
                    frame({'id': []}), frame({'id': True}), frame({'id': {}}), b'[]',
                    json.dumps({'id': 1}).encode('utf-16')):
            inbox = companion.RuntimeInbox()
            inbox.feed(raw)
            with self.assertRaisesRegex(companion.CompanionFailure, 'Invalid App Server protocol') as raised:
                inbox.get()
            self.assertNotIn(secret, str(raised.exception))
            self.assertEqual(inbox.pending_bytes, 0)
            self.assertFalse(inbox.feed(frame({'id': 1})))

    def test_empty_null_and_missing_ids_do_not_alias_integer_ack(self):
        inbox = companion.RuntimeInbox()
        for value in ({'id': None}, {'method': 'notification'}, {'id': '1'}, {'id': 1}):
            inbox.feed(frame(value))
        self.assertEqual(inbox.get(1), {'id': 1})
        self.assertEqual(inbox.get(), {'id': None})
        self.assertEqual(inbox.get(), {'method': 'notification'})
        self.assertEqual(inbox.get(), {'id': '1'})

    def test_waiting_consumer_is_woken_on_failure(self):
        inbox = companion.RuntimeInbox(count_limit=1)
        started = threading.Event()
        errors = []

        def consume():
            started.set()
            try:
                inbox.get(42, timeout=5)
            except companion.CompanionFailure as error:
                errors.append(str(error))

        thread = threading.Thread(target=consume, daemon=True)
        thread.start()
        self.assertTrue(started.wait(1))
        inbox.feed(frame({'id': 1}))
        inbox.feed(frame({'id': 2}))
        thread.join(1)
        self.assertFalse(thread.is_alive())
        self.assertEqual(len(errors), 1)
        self.assertIn('memory limits', errors[0])

    def test_pipe_failure_and_close_prevent_later_delivery(self):
        class Broken:
            def readline(self, size):
                raise OSError('PRIVATE-FIXTURE-PATH')
        value = client(Broken())
        value._read()
        with self.assertRaisesRegex(companion.CompanionFailure, 'pipe could not be read') as raised:
            value.receive()
        self.assertNotIn('PRIVATE', str(raised.exception))
        value = client(io.BytesIO())
        value.inbox.feed(frame({'id': 1}))
        with patch.object(companion, 'close_owned_process') as closed:
            value.close()
        closed.assert_called_once_with(value.process)
        self.assertFalse(value.inbox.feed(frame({'id': 2})))
        with self.assertRaisesRegex(companion.CompanionFailure, 'closed'):
            value.receive(1)

    def test_failed_or_ended_transport_does_not_write_or_flush(self):
        for error in (None, 'App Server pending messages exceeded memory limits'):
            value = client(io.BytesIO())
            value.inbox.finish(error)
            with self.assertRaises(companion.CompanionFailure):
                value.send({'method': 'turn/interrupt'})
            self.assertEqual(value.process.stdin.getvalue(), b'')

    def test_valid_image_echo_models_quota_and_unicode_send(self):
        # Maximum aggregate4MiB image input becomes ~5.6MiB base64 in an echoed
        # user message. Transport bounds must not accidentally ban attachments.
        image_text = 'A' * (((2 * 1024 * 1024 + 2) // 3) * 4)
        echo = {'method': 'item/completed', 'params': {'item': {'type': 'userMessage',
                'content': [{'type': 'image', 'url': 'data:image/png;base64,' + image_text} for _ in range(2)]}}}
        models = {'id': 1, 'result': {'data': [{'id': 'model-' + str(i), 'name': 'Model',
                  'description': 'Synthetic model listing'} for i in range(100)]}}
        quota = {'id': 2, 'result': {'rateLimits': {'primary': {'usedPercent': 25}}}}
        value = client(io.BytesIO(frame(echo) + frame(models) + frame(quota)))
        value._read()
        self.assertEqual(value.receive(1), models)
        self.assertEqual(value.receive(2), quota)
        self.assertEqual(value.receive(), echo)
        value = client(io.BytesIO())
        value.send({'text': '猫🙂'})
        self.assertEqual(json.loads(value.process.stdin.getvalue()), {'text': '猫🙂'})

    def test_final_envelope_limit_precedes_strip_and_json_parse(self):
        for answer in (' ' * (companion.MAX_ANSWER_ENVELOPE_CHARS + 1),
                       '{"answer":"' + 'x' * companion.MAX_ANSWER_ENVELOPE_CHARS + '"}'):
            with patch.object(companion.json, 'loads', side_effect=AssertionError('must not parse')):
                with self.assertRaisesRegex(companion.CompanionFailure, 'envelope exceeded'):
                    companion.parse_question_answer(answer)
        text = '🙂' * 6000
        self.assertEqual(companion.parse_question_answer(json.dumps({'answer': text})), {'answer': text})


if __name__ == '__main__':
    unittest.main()
