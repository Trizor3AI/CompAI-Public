using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CompAI;

// Navigation is temporary UI state. Questions already belong to the saved chat;
// recalling one never sends it, and moving past the newest restores the draft.
internal sealed class ChatInputHistory
{
    private string[] questions = [];
    private string originalDraft = "";
    private string? recalled;
    private int index = -1;

    internal void Reset()
    {
        this.questions = [];
        this.originalDraft = "";
        this.recalled = null;
        this.index = -1;
    }

    internal string? Move(bool older, string draft, IEnumerable<string> newestFirst)
    {
        if (this.index >= 0 && draft != this.recalled) this.Reset();
        if (this.index < 0)
        {
            if (!older) return null;
            this.questions = newestFirst.Where(text => !string.IsNullOrWhiteSpace(text) && text.Length <= 2000)
                .Take(100).ToArray();
            if (this.questions.Length == 0) return null;
            this.originalDraft = draft;
        }
        if (older)
        {
            if (this.index + 1 >= this.questions.Length) return null;
            return this.recalled = this.questions[++this.index];
        }
        if (--this.index >= 0) return this.recalled = this.questions[this.index];
        var restored = this.originalDraft;
        this.Reset();
        return restored;
    }

    // ImGui cursor positions are UTF-8 byte offsets. Only take unmodified arrows
    // at the boundary line, leaving selection and multiline editing to the widget.
    internal static bool AtBoundary(string text, int cursor, int selectionStart, int selectionEnd, bool older)
    {
        if (selectionStart != selectionEnd || cursor < 0) return false;
        var bytes = Encoding.UTF8.GetBytes(text);
        if (cursor > bytes.Length) return false;
        return older ? bytes.AsSpan(0, cursor).IndexOf((byte)'\n') < 0
            : bytes.AsSpan(cursor).IndexOf((byte)'\n') < 0;
    }
}
