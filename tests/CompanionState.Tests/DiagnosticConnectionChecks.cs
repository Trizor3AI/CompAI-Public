using System.Diagnostics;
using System.Reflection;
using CompAI;

internal static class DiagnosticConnectionChecks
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Current(CompanionConnection connection) => typeof(CompanionConnection).GetProperty("ActiveQuestion", Flags)!.GetValue(connection)!;
    private static void Apply(CompanionConnection connection, object operation, string json) =>
        typeof(CompanionConnection).GetMethod("ApplyMessage", Flags)!.Invoke(connection, new[] { operation, json });
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Until(Func<bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Diagnostics fixture timed out");
            await Task.Delay(20);
        }
    }

    internal static async Task Correlation()
    {
        using var fixture = new Fixture();
        using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
        fixture.Scenario("login_status"); connection.Start("status");
        await Until(() => !connection.View.Busy);
        PerformanceDiagnostics.Clear();
        fixture.Scenario("ask_parallel");
        var firstChat = connection.View.ActiveChatId;
        Check(connection.StartAsk("{\"id\":\"diagnostic-first\"}", false, "Synthetic first"), "First request starts");
        await Until(() => connection.View.PartialAnswer.Length > 0);
        var first = Current(connection);
        Apply(connection, first, "{\"performance\":{\"stage\":\"provider.tokens\",\"input_tokens\":111}}");
        Apply(connection, first, "{\"performance\":{\"stage\":\"provider.activity\",\"tool_calls\":3,\"reasoning_items\":2}}");
        Check(connection.View.RequestDiagnostics.Contains("Input: 111") && connection.View.RequestStatus.Contains("3 tools"), "First request owns tokens and compact activity");
        Check(connection.NewChat(), "Second chat created");
        Check(connection.StartAsk("{\"id\":\"diagnostic-second\"}", false, "Synthetic second"), "Second request starts");
        await Until(() => connection.View.PartialAnswer.Length > 0);
        var second = Current(connection);
        Apply(connection, second, "{\"performance\":{\"stage\":\"provider.tokens\",\"input_tokens\":222}}");
        Check(connection.View.RequestDiagnostics.Contains("Input: 222") && !connection.View.RequestDiagnostics.Contains("Input: 111") &&
            !connection.View.RequestStatus.Contains("tools"), "Second request cannot inherit first request counts or tokens");
        Check(connection.SelectChat(firstChat), "First owner selected");
        fixture.ReleaseParallel("diagnostic-first");
        await Until(() => !connection.View.ActiveChatBusy);
        Check(PerformanceDiagnostics.Report.Contains("Input: 111") && PerformanceDiagnostics.Report.Contains("Input: 222"), "Latest completed report remains separate from active sibling");
        Check(connection.StartAsk("{\"id\":\"diagnostic-replacement\"}", false, "Synthetic replacement"), "Replacement starts after first completes");
        await Until(() => connection.View.PartialAnswer.Length > 0);
        Apply(connection, first, "{\"performance\":{\"stage\":\"provider.tokens\",\"input_tokens\":999}}");
        Apply(connection, first, "{\"performance\":{\"stage\":\"request.activity\",\"state\":\"searching\"}}");
        Check(connection.View.RequestDiagnostics.Contains("Input: unknown") && !PerformanceDiagnostics.Report.Contains("Input: 999") &&
            !connection.View.RequestStatus.StartsWith("Searching"), "Replaced operation telemetry is ignored before accepting observations");
        connection.CancelAllQuestions();
        await Until(() => !connection.View.Busy);
        Check(PerformanceDiagnostics.Report.Contains("Latest completed request:" + Environment.NewLine + "Cancelled"), "Cancellation retains final numeric report");
    }
}
