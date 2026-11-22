"""Bounded, disposable previews of the existing structured answer stream.

This module never accepts a final answer or changes a provider request. Unsupported
or malformed streams fall back to the caller's ordinary completed-answer path.
"""
import time


MAX_ANSWER_CHARS = 6000
# A non-BMP scalar can occupy two six-character JSON Unicode escapes.
MAX_ENCODED_CHARS = MAX_ANSWER_CHARS * 12 + 256
UPDATE_INTERVAL = 0.1
_WHITESPACE = " \t\r\n"
_ESCAPES = {'"': '"', "\\": "\\", "/": "/", "b": "\b", "f": "\f",
            "n": "\n", "r": "\r", "t": "\t"}


class AnswerPreviewDecoder:
    """Decode only the literal {\"answer\": \"...\"} shape, without JSON repair."""

    def __init__(self):
        self.state = "object"
        self.encoded_chars = 0
        self.characters = []
        self.key_position = 0
        self.unicode_digits = ""
        self.high_surrogate = None

    @property
    def text(self):
        return "".join(self.characters)

    @property
    def complete(self):
        return self.state == "done"

    def feed(self, chunk):
        if not isinstance(chunk, str) or self.state == "invalid":
            raise ValueError("Invalid answer preview")
        self.encoded_chars += len(chunk)
        if self.encoded_chars > MAX_ENCODED_CHARS:
            self.state = "invalid"
            raise ValueError("Answer preview exceeded encoded limit")
        try:
            for character in chunk:
                self._consume(character)
        except ValueError:
            self.state = "invalid"
            raise
        return self.text

    def _append(self, character):
        value = ord(character)
        if self.high_surrogate is not None:
            if not 0xDC00 <= value <= 0xDFFF:
                raise ValueError("Unpaired answer preview surrogate")
            character = chr(0x10000 + ((self.high_surrogate - 0xD800) << 10) + value - 0xDC00)
            self.high_surrogate = None
        elif 0xD800 <= value <= 0xDBFF:
            self.high_surrogate = value
            return
        elif 0xDC00 <= value <= 0xDFFF:
            raise ValueError("Unpaired answer preview surrogate")
        if len(self.characters) >= MAX_ANSWER_CHARS:
            raise ValueError("Answer preview exceeded decoded limit")
        self.characters.append(character)

    def _consume(self, character):
        if self.state in ("object", "key", "colon", "value", "close", "done"):
            if character in _WHITESPACE:
                return
            expected, following = {
                "object": ("{", "key"), "key": ('"', "key_text"),
                "colon": (":", "value"), "value": ('"', "string"),
                "close": ("}", "done"), "done": (None, None),
            }[self.state]
            if character != expected:
                raise ValueError("Unexpected answer preview shape")
            self.state = following
        elif self.state == "key_text":
            literal = 'answer"'
            if character != literal[self.key_position]:
                raise ValueError("Unexpected answer preview key")
            self.key_position += 1
            if self.key_position == len(literal):
                self.state = "colon"
        elif self.state == "string":
            if character == '"':
                if self.high_surrogate is not None:
                    raise ValueError("Unpaired answer preview surrogate")
                self.state = "close"
            elif character == "\\":
                self.state = "escape"
            elif ord(character) < 0x20:
                raise ValueError("Unescaped answer preview control character")
            else:
                self._append(character)
        elif self.state == "escape":
            if character == "u":
                self.unicode_digits = ""
                self.state = "unicode"
            elif character in _ESCAPES:
                self._append(_ESCAPES[character])
                self.state = "string"
            else:
                raise ValueError("Invalid answer preview escape")
        elif self.state == "unicode":
            if character not in "0123456789abcdefABCDEF":
                raise ValueError("Invalid answer preview Unicode escape")
            self.unicode_digits += character
            if len(self.unicode_digits) == 4:
                self._append(chr(int(self.unicode_digits, 16)))
                self.state = "string"


class AnswerPreviewStream:
    """Correlate one explicit final-answer item; never combine assistant items."""

    def __init__(self, thread_id, turn_id, emit, clock=time.monotonic):
        self.thread_id, self.turn_id = thread_id, turn_id
        self.emit, self.clock = emit, clock
        self.item_id = None
        self.decoder = None
        self.item_finished = False
        self.disabled = False
        self.visible = ""
        self.last_update = float("-inf")

    def finish(self):
        self.disabled = True
        self.decoder = None
        if self.visible:
            self.emit({"question_partial": ""})
            self.visible = ""

    def handle(self, method, params):
        if (self.disabled or not isinstance(params, dict) or
                params.get("threadId") != self.thread_id or params.get("turnId") != self.turn_id):
            return
        if method == "item/started":
            item = params.get("item")
            if not isinstance(item, dict) or item.get("type") != "agentMessage":
                return
            if self.item_id is not None:
                # Even a second assistant item must not inherit a prior preview.
                self.finish()
                return
            if item.get("phase") != "final_answer":
                return
            item_id = item.get("id")
            if not isinstance(item_id, str) or not 1 <= len(item_id) <= 256:
                return
            self.item_id = item_id
            self.decoder = AnswerPreviewDecoder()
            self._feed(item.get("text", ""))
        elif method == "item/agentMessage/delta":
            if self.decoder is not None and params.get("itemId") == self.item_id:
                if self.item_finished:
                    self.finish()
                else:
                    self._feed(params.get("delta"))
        elif method == "item/completed" and self.decoder is not None:
            item = params.get("item")
            if not isinstance(item, dict) or item.get("type") != "agentMessage":
                return
            if item.get("id") != self.item_id or item.get("phase") not in (None, "final_answer"):
                self.finish()
                return
            try:
                completed = AnswerPreviewDecoder()
                completed.feed(item.get("text"))
                if (not completed.complete or not self.decoder.complete or
                        completed.text != self.decoder.text or not completed.text):
                    raise ValueError("Completed answer differs from preview")
            except ValueError:
                self.finish()
                return
            self.item_finished = True

    def _feed(self, chunk):
        try:
            text = self.decoder.feed(chunk)
        except ValueError:
            self.finish()
            return
        now = self.clock()
        if text != self.visible and now - self.last_update >= UPDATE_INTERVAL:
            self.emit({"question_partial": text})
            self.visible, self.last_update = text, now
