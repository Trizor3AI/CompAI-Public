using System.Diagnostics;
using System.Text.Json;
using CompAI;

internal static class ParallelConnectionChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static async Task Until(Func<bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Parallel fixture did not reach expected state");
            await Task.Delay(20);
        }
    }

    private static async Task Authorize(CompanionConnection connection, Fixture fixture)
    {
        fixture.Scenario("login_status");
        connection.Start("status");
        await Until(() => !connection.View.Busy);
        Check(connection.View.CanTest, "synthetic account authorized");
        fixture.Scenario("ask_parallel");
    }

    private static bool Send(CompanionConnection connection, string id, bool gameContext = false,
        bool authError = false, Func<JsonElement, string>? lookup = null, string? failureKind = null, bool holdExit = false)
    {
        return connection.StartAsk(JsonSerializer.Serialize(new { id, authentication_error = authError, lookup = lookup is not null, failure_kind = failureKind, hold_exit = holdExit }),
            gameContext, "Question " + id, includeQuests: lookup is not null, questQuery: lookup);
    }

    internal static async Task Ownership()
    {
        using var fixture = new Fixture();
        using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
        await Authorize(connection, fixture);
        var first = connection.View.ActiveChatId;
        connection.Draft = "Question first";
        Check(Send(connection, "first"), "first send starts");
        await Until(() => connection.View.PartialAnswer == "Partial first");
        var firstGuard = connection.CaptureQuestionContinuation();
        Check(connection.NewChat(), "new chat allowed while first request runs");
        var second = connection.View.ActiveChatId;
        Check(!connection.View.ActiveChatBusy && connection.View.CanTest && connection.View.SendBlockReason is null, "second chat is independently sendable");
        connection.Draft = "Question second";
        Check(Send(connection, "second", holdExit: true), "second send starts while first remains held");
        await Until(() => connection.View.PartialAnswer == "Partial second");
        Check(File.Exists(fixture.ParallelFile("first", "ready")) && File.Exists(fixture.ParallelFile("second", "ready")) &&
            connection.View.ActiveRequestCount == 2 && firstGuard(), "two bridge processes receive requests before either is released");
        Check(!Send(connection, "duplicate"), "one request per chat");
        Check(connection.NewChat(), "third chat selectable while both requests run");
        var third = connection.View.ActiveChatId;
        connection.Draft = "Keep third draft";
        Check(!connection.View.CanTest && connection.View.SendBlockReason?.Contains("Two chats") == true && !Send(connection, "third"), "third request refused at cap");
        Check(connection.SaveChatNotes("Third notes") && connection.SetChatPersonality(PersonalityPresets.Armo), "unrelated chat settings remain editable");
        Check(!connection.DeleteChat(first) && !connection.DeleteChat(second), "running owners cannot be deleted");
        var deleted = connection.DeleteSelectedChats([first, second]);
        Check(deleted.DeletedIds.Count == 0 && deleted.Failures.Count == 2, "bulk delete protects both running owners");
        var actionsBefore = File.ReadAllLines(fixture.ActionLogFile).Length;
        connection.Start("logout");
        Check(File.ReadAllLines(fixture.ActionLogFile).Length == actionsBefore && connection.View.IsSignedIn, "account operation is exclusive");
        fixture.ReleaseParallel("second");
        Check(connection.SelectChat(second), "finishing owner remains selectable");
        await Until(() => connection.View.PartialAnswer == "Answer second");
        Check(connection.View.PendingQuestion == "Question second" && connection.View.History.Count == 0,
            "final answer remains visible without collapsing while awaiting process exit");
        Check(connection.SelectChat(third), "third owner reselected before final persistence");
        File.WriteAllText(fixture.ParallelFile("second", "exit"), "go");
        await Until(() => connection.View.ActiveRequestCount == 1);
        Check(connection.View.ActiveChatId == third && connection.View.History.Count == 0 && connection.Draft == "Keep third draft", "background completion leaves visible owner untouched");
        Check(connection.SelectChat(second) && connection.View.History.Single().Question == "Question second" &&
            connection.View.History.Single().Answer == "Answer second" && connection.View.PartialAnswer == "", "second answer saved only to second owner");
        Check(connection.SelectChat(first) && connection.View.PartialAnswer == "Partial first" && connection.View.PendingQuestion == "Question first", "first progress survives switching and sibling completion");
        connection.Cancel();
        await Until(() => !connection.View.Busy);
        Check(!firstGuard() && connection.Draft == "Question first" && connection.View.RetryQuestion == "Question first" && connection.View.History.Count == 0,
            "active cancellation restores only its owner draft and retry");
        Check(connection.SelectChat(second) && connection.View.RetryQuestion is null && connection.View.History.Count == 1, "cancelled sibling cannot overwrite completed chat");
        Check(!connection.StartAsk("{}", false, "stale owner", expectedChatId: first), "prepared request rejects a changed chat owner");
        var saved = new ChatStore(Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "chats"));
        Check(saved.Select(first) && saved.ActiveDraft == "Question first" && saved.ActiveHistory.View.Count == 0 &&
            saved.Select(second) && saved.ActiveHistory.View.Single().Answer == "Answer second" &&
            saved.Select(third) && saved.ActiveDraft == "Keep third draft", "all completed and recoverable state persists to its owner's file");
    }

    internal static async Task Cancellation()
    {
        using var fixture = new Fixture();
        var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
        try
        {
            await Authorize(connection, fixture);
            var first = connection.View.ActiveChatId;
            connection.Draft = "Question lookup";
            Func<bool>? callbackGuard = null;
            Check(Send(connection, "lookup", lookup: _ =>
            {
                callbackGuard = connection.CaptureQuestionContinuation();
                Check(callbackGuard(), "background callback captures its running owner");
                return "{}";
            }), "lookup owner starts");
            await Until(() => File.Exists(fixture.ParallelFile("lookup", "ready")));
            Check(connection.NewChat(), "second chat starts");
            var second = connection.View.ActiveChatId;
            connection.Draft = "Question plain";
            Check(Send(connection, "plain"), "plain sibling starts");
            await Until(() => connection.View.PartialAnswer == "Partial plain");
            File.WriteAllText(fixture.ParallelFile("lookup", "query"), "go");
            await Until(() => File.Exists(fixture.ParallelFile("lookup", "result")));
            connection.InvalidateQuestion();
            Check(callbackGuard is not null && !callbackGuard() && connection.CaptureQuestionContinuation()(),
                "game invalidation cancels background reader owner while plain visible sibling stays valid");
            await Until(() => connection.View.ActiveRequestCount == 1);
            Check(connection.SelectChat(first) && connection.Draft == "Question lookup" && connection.View.RetryQuestion == "Question lookup", "background cancellation recovers its owner");
            Check(connection.SelectChat(second), "plain owner remains selectable");
            connection.CancelAllQuestions();
            await Until(() => !connection.View.Busy);
            Check(connection.View.History.Count == 0 && connection.View.RetryQuestion == "Question plain", "permission revocation stops the remaining owner");

            connection.Draft = "Question unloadA";
            Check(Send(connection, "unloadA"), "unload owner A starts");
            await Until(() => connection.View.PartialAnswer == "Partial unloadA");
            Check(connection.SelectChat(first), "unload owner B selectable");
            connection.Draft = "Question unloadB";
            Check(Send(connection, "unloadB", gameContext: true), "unload owner B starts");
            await Until(() => connection.View.PartialAnswer == "Partial unloadB");
            connection.Dispose();
            var saved = new ChatStore(Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "chats"));
            Check(saved.Select(first) && saved.ActiveDraft == "Question unloadB" && saved.Select(second) && saved.ActiveDraft == "Question unloadA",
                "dispose restores both unfinished owner drafts before returning");
            await Until(() => !connection.View.Busy);
        }
        finally { connection.Dispose(); }
    }

    internal static async Task Authentication()
    {
        using var fixture = new Fixture();
        using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
        await Authorize(connection, fixture);
        var first = connection.View.ActiveChatId;
        Check(Send(connection, "auth", authError: true), "authentication failure owner starts");
        await Until(() => connection.View.PartialAnswer == "Partial auth");
        Check(connection.NewChat() && Send(connection, "sibling"), "sibling starts");
        var second = connection.View.ActiveChatId;
        await Until(() => connection.View.PartialAnswer == "Partial sibling");
        fixture.ReleaseParallel("auth");
        await Until(() => !connection.View.Busy);
        fixture.ReleaseParallel("sibling");
        Check(!connection.View.IsSignedIn && !connection.View.CanTest && connection.View.History.Count == 0 &&
            connection.View.RetryQuestion == "Question sibling" && connection.SelectChat(first) && connection.View.History.Count == 0,
            "authentication failure invalidates all live owners and late output cannot restore account or save an answer");
        Check(connection.SelectChat(second) && !Send(connection, "unauthorized"), "sibling cannot reauthorize another request");
    }

    internal static async Task FailClosed()
    {
        foreach (var kind in new[] { "protocol", "permission" })
        {
            using var fixture = new Fixture();
            using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
            await Authorize(connection, fixture);
            Check(Send(connection, "failed", failureKind: kind), "failed owner starts");
            await Until(() => connection.View.PartialAnswer == "Partial failed");
            Check(connection.NewChat() && Send(connection, "sibling"), "sibling starts");
            await Until(() => connection.View.PartialAnswer == "Partial sibling");
            fixture.ReleaseParallel("failed");
            await Until(() => connection.View.ActiveRequestCount == 1);
            if (kind == "protocol") fixture.ReleaseParallel("sibling");
            else connection.Cancel();
            await Until(() => !connection.View.Busy);
            Check(!connection.View.CanTest && connection.View.SendBlockReason?.Contains("refresh account status") == true &&
                !Send(connection, "must-refuse"), "sibling success or cancellation cannot clear required account refresh");
            await Authorize(connection, fixture);
            Check(connection.View.CanTest && connection.View.SendBlockReason is null, "successful explicit account status clears requirement");
        }
    }
}
