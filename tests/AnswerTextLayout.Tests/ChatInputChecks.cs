using CompAI;
using System.Text;

internal static class ChatInputChecks
{
    internal static void Run()
    {
        var history = new ChatInputHistory();
        string[] previous = ["third", "second", "first"];
        var draft = "unsent Șț draft";
        Check(history.Move(false, draft, previous) is null, "Down leaves ordinary draft alone");
        Check(history.Move(true, draft, previous) == "third", "first Up recalls latest");
        Check(history.Move(true, "third", previous) == "second", "second Up recalls second latest");
        Check(history.Move(true, "second", previous) == "first", "third Up recalls oldest");
        Check(history.Move(true, "first", previous) is null, "oldest is bounded");
        Check(history.Move(false, "first", previous) == "second", "Down walks toward newest");
        Check(history.Move(false, "second", previous) == "third", "Down reaches newest");
        Check(history.Move(false, "third", previous) == draft, "Down restores original draft verbatim");
        history.Move(true, draft, previous);
        Check(history.Move(false, "edited recalled question", previous) is null, "editing breaks recall traversal");
        Check(history.Move(true, "edited recalled question", previous) == "third", "edited text becomes new original");
        Check(history.Move(false, "third", previous) == "edited recalled question", "edited text is recoverable");
        history.Move(true, draft, previous);
        history.Reset(); // Chat selection must not carry old draft/history to another chat.
        Check(history.Move(false, "other chat", previous) is null, "chat switch clears recall state");
        Check(history.Move(true, "other chat", []) is null, "empty history preserves draft");

        const string multiline = "Șț first\nsecond\nthird";
        var second = Encoding.UTF8.GetByteCount("Șț first\nsec");
        Check(!ChatInputHistory.AtBoundary(multiline, second, second, second, true) &&
            !ChatInputHistory.AtBoundary(multiline, second, second, second, false), "middle-line arrows remain native");
        Check(ChatInputHistory.AtBoundary(multiline, 4, 4, 4, true), "UTF-8 first-line cursor can recall older");
        var end = Encoding.UTF8.GetByteCount(multiline);
        Check(ChatInputHistory.AtBoundary(multiline, end, end, end, false), "last-line cursor can recall newer");
        Check(!ChatInputHistory.AtBoundary(multiline, 4, 0, 4, true), "selection is preserved");
        Check(!ChatInputHistory.AtBoundary(multiline, end + 1, end + 1, end + 1, true), "stale cursor rejected");
        Console.WriteLine("PASS input recall, draft restoration, chat isolation and UTF-8 multiline boundaries");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
