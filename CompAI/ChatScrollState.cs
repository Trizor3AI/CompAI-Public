using System;
using System.Numerics;

namespace CompAI;

// Retain the user's follow intent across changes to the busy header, wrapped
// content and ImGui's one-frame-delayed scroll bounds.
internal sealed class ChatScrollState
{
    private bool initialized;
    private bool following = true;
    private bool followRequested;
    private int settleFrames;
    private int historyCount;
    private string pendingQuestion = "";
    private Vector2 viewport;
    private float scrollMax;
    private float contentHeight;

    internal void RequestFollow() => this.followRequested = true;

    internal void Reset()
    {
        this.initialized = false;
        this.following = true;
        this.followRequested = false;
        this.settleFrames = 0;
    }

    internal bool Update(int historyCount, string pendingQuestion, Vector2 viewport,
        float contentHeight, float scrollY, float scrollMax, bool userScrolledAway,
        bool userScrolledTowardBottom = false)
    {
        var changed = !this.initialized || historyCount != this.historyCount ||
            pendingQuestion != this.pendingQuestion || viewport != this.viewport ||
            Math.Abs(contentHeight - this.contentHeight) > 0.5f ||
            Math.Abs(scrollMax - this.scrollMax) > 0.5f;
        if (this.followRequested)
        {
            this.following = true;
            this.settleFrames = 2;
        }

        // ImGui clamps ScrollY when the busy header disappears or the viewport
        // grows. That can put a detached reader at the new bottom without any
        // user input. Only a deliberate return to the bottom may resume follow;
        // position alone cannot distinguish the gesture from a layout clamp.
        var atBottom = scrollMax - scrollY <= 1f;
        if (userScrolledAway)
        {
            this.following = false;
            this.settleFrames = 0;
        }
        else if (atBottom && userScrolledTowardBottom)
            this.following = true;
        else if (!atBottom && !changed && this.settleFrames == 0)
            this.following = false;

        if (this.following && changed) this.settleFrames = 2;
        var scrollToBottom = this.following && this.settleFrames > 0;
        if (this.settleFrames > 0) --this.settleFrames;
        this.followRequested = false;
        this.initialized = true;
        this.historyCount = historyCount;
        this.pendingQuestion = pendingQuestion;
        this.viewport = viewport;
        this.scrollMax = scrollMax;
        this.contentHeight = contentHeight;
        return scrollToBottom;
    }
}
