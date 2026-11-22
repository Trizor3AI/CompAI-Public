using System.Numerics;
using CompAI;

internal static class ChatScrollChecks
{
    internal static void Run()
    {
        var state = new ChatScrollState();
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        bool Frame(int count = 2, string pending = "", float y = 600, float max = 600,
            float height = 400, float content = 1000, bool away = false, bool towardBottom = false) =>
            state.Update(count, pending, new Vector2(800, height), content, y, max, away, towardBottom);

        Check(Frame(), "Opening a saved chat follows its latest message.");
        Frame();
        Check(!Frame(), "A settled idle chat does not continually schedule scroll commands.");

        state.RequestFollow();
        Check(Frame(pending: "Next question", max: 660, height: 340, content: 1080),
            "Sending still follows when the busy header shrinks the viewport first.");
        Check(Frame(pending: "Next question", y: 660, max: 740, height: 340, content: 1080),
            "Follow survives delayed scroll bounds for the pending question.");
        Frame(pending: "Next question", y: 740, max: 740, height: 340, content: 1080);
        Check(!Frame(pending: "Next question", y: 740, max: 740, height: 340, content: 1080),
            "Pending wait settles without pinning every frame.");
        Check(Frame(count: 3, y: 740, max: 680, content: 1800),
            "The completed reply follows even as the busy header disappears.");
        Check(Frame(count: 3, y: 680, max: 1400, content: 1800),
            "The longer answer's scroll range may arrive a frame later.");
        Frame(count: 3, y: 1400, max: 1400, content: 1800);

        Check(!Frame(count: 3, y: 1100, max: 1400, content: 1800, away: true),
            "Deliberate scrolling cancels automatic follow.");
        Check(!Frame(count: 4, y: 1100, max: 1400, content: 2000),
            "A new answer does not interrupt reading older messages.");
        Check(!Frame(count: 4, y: 1100, max: 1600, content: 2000),
            "Delayed layout cannot reenable follow after user scrolls away.");

        state.RequestFollow();
        Check(Frame(count: 5, y: 1100, max: 1600, content: 2200),
            "Sending from older history follows even if the response completes between frames.");
        Check(!Frame(count: 5, y: 1000, max: 1800, content: 2200, away: true),
            "The user can cancel follow during its settling frames.");
        Check(!Frame(count: 5, y: 1000, max: 1800, content: 2200),
            "The interrupted scroll does not resume by itself.");
        Frame(count: 5, y: 1800, max: 1800, content: 2200, towardBottom: true);
        Check(Frame(count: 6, y: 1800, max: 1800, content: 2500),
            "Returning to the bottom resumes following later answers.");

        state.Reset();
        Check(Frame(y: 100), "Switching chats follows the new chat, not the old scroll position.");
        Frame();
        Frame();
        Check(!Frame(y: 300), "A scrollbar position change in a stable layout stops follow.");
        Check(!Frame(count: 3, y: 300, content: 1400), "That position survives the next answer.");

        state.Reset();
        Frame(pending: "Streaming", content: 1100);
        Frame(pending: "Streaming", content: 1100);
        Frame(pending: "Streaming", content: 1100);
        Check(Frame(pending: "Streaming", content: 1200), "Growing streamed text keeps bottom follow.");
        Check(!Frame(pending: "Streaming", content: 1300, y: 100, away: true),
            "Reading older text cancels follow during streaming.");
        Check(!Frame(pending: "Streaming", content: 1400, y: 100),
            "Later stream chunks preserve the reader's position.");
        Check(!Frame(pending: "Streaming", content: 1600, y: 100, height: 300),
            "Resize and remeasurement do not force a reader to the bottom.");
        state.RequestFollow();
        Check(Frame(pending: "Streaming", content: 1600, y: 100, height: 300),
            "Explicit follow still works after resize and streaming.");

        state.Reset();
        Frame(pending: "Streaming");
        Frame(pending: "Streaming");
        Frame(pending: "Streaming");
        Check(!Frame(pending: "Streaming", y: 583, away: true),
            "A one-line upward scroll cancels follow even close to the bottom.");
        Check(!Frame(pending: "Streaming", y: 583, content: 1017),
            "The next streamed line must not pull a reader back through the 24px bottom margin.");
        Check(!Frame(pending: "Streaming", y: 583, max: 617, content: 1017),
            "Delayed bounds preserve the small manual scroll.");
        Frame(pending: "Streaming", y: 617, max: 617, content: 1017, towardBottom: true);
        Check(Frame(pending: "Streaming", y: 617, max: 617, content: 1034),
            "Scrolling all the way back to the bottom resumes stream following.");

        state.Reset();
        Frame(pending: "Streaming");
        Check(!Frame(pending: "Streaming", y: 583, content: 1017, away: true),
            "A small manual scroll interrupts an outstanding bottom-follow settle.");
        Check(!Frame(pending: "Streaming", y: 583, max: 617, content: 1034),
            "The interrupted settle stays cancelled while the stream and delayed bounds advance.");
        Check(!Frame(pending: "Streaming", y: 583, max: 634, content: 1034),
            "The reader stays put when the second delayed bound arrives.");
        state.RequestFollow();
        Check(Frame(pending: "New question", y: 583, max: 634, content: 1100),
            "An explicit send still restores follow after a small manual scroll.");

        // Native ImGui repro: a quarter-wheel scroll during streaming leaves a
        // 34px gap. A 60px viewport increase then clamps that reader to its new
        // maximum before drawing. The former atBottom branch resumed following.
        state.Reset();
        Frame(pending: "Streaming", y: 672, max: 672, content: 1093);
        Check(!Frame(pending: "Streaming", y: 655, max: 689, content: 1110, away: true),
            "A small wheel gesture detaches from the native stream tail.");
        Check(!Frame(pending: "Streaming", y: 646, max: 646, height: 460, content: 1127),
            "A viewport clamp to the bottom cannot change the reader's follow intent.");
        Check(!Frame(pending: "Streaming", y: 646, max: 663, height: 460, content: 1144),
            "The next stream chunk cannot pull the clamped reader down.");
        Check(!Frame(count: 3, y: 646, max: 680, height: 460, content: 1800),
            "Pending-to-completed replacement retains detached intent.");
        Check(!Frame(count: 3, y: 646, max: 1340, height: 460, content: 1800),
            "The completed answer's delayed bounds do not resume following.");
        Check(!Frame(count: 3, y: 1200, max: 1340, height: 460, content: 1800, towardBottom: true),
            "Scrolling downward within history does not resume follow before the bottom.");
        Frame(count: 3, y: 1340, max: 1340, height: 460, content: 1800, towardBottom: true);
        Check(Frame(count: 4, y: 1340, max: 1340, height: 460, content: 1900),
            "A deliberate wheel or scrollbar return to the bottom restores follow.");

        state.Reset();
        Frame();
        Frame(y: 583, away: true);
        Check(!Frame(y: 0, max: 0, content: 400),
            "Remeasured content fitting the viewport does not reattach a reader.");
        Check(!Frame(y: 0, max: 0, content: 1000),
            "Growth after a zero-range layout retains detached intent.");
        Check(!Frame(), "A position-only return to the bottom is not proof of a user gesture.");
        state.RequestFollow();
        Check(Frame(content: 1100), "Explicit send still follows after a layout-induced bottom position.");

        Console.WriteLine("PASS chat scroll: send, delayed layout, reply, manual gestures, viewport clamps, streaming, fast reply and chat switch.");
    }
}
