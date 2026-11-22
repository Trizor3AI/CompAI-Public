using System.Diagnostics;
using System.Text.Json;
using CompAI;

internal static class ResponseTimeoutChecks
{
    internal static async Task LauncherContract()
    {
        using var fixture = new Fixture();
        var minutes = 0;
        using var connection = new CompanionConnection(preparePython: Fixture.PreparePython,
            responseLimitMinutes: () => minutes);
        await Authorize(connection, fixture);
        Check(!ReadArguments(fixture).Single().Contains("--response-timeout-seconds"),
            "account status must not receive the question-only timeout option");
        foreach (var (request, selectedMinutes, expectedSeconds) in new (string, int, int)[]
        {
            ("{}", 0, 300),
            ("{\"reasoning_effort\":\"medium\"}", 0, 300),
            ("{\"reasoning_effort\":\"high\"}", 0, 600),
            ("{\"request\":{\"reasoning_effort\":\"high\"}}", 0, 600),
            ("{\"reasoning_effort\":\"default\"}", 0, 300),
            ("{\"reasoning_effort\":\"high\"}", 15, 900),
        })
        {
            minutes = selectedMinutes;
            fixture.Scenario("ask_success");
            Check(connection.StartAsk(request, false, "Synthetic timeout argument"), "valid question must start");
            await WaitIdle(connection);
            CheckTimeoutArgument(ReadArguments(fixture).Last(), expectedSeconds);
            Check(File.ReadAllText(fixture.RequestFile).TrimEnd() == request,
                "timeout selection must not rewrite the private request payload");
        }
        Check(connection.View.History.Count == 6 && connection.View.IsSignedIn,
            "bounded fixture answers must complete with the verified account intact");

        foreach (var seconds in new[] { 60, 300, 600, 900, 1800 })
        {
            Check(CompanionConnection.ProcessTimeout("ask", seconds) == TimeSpan.FromSeconds(seconds + 60),
                "outer ask timeout must preserve response duration plus startup and cleanup grace");
            Check(CompanionConnection.ProcessTimeout("test", seconds) == TimeSpan.FromMinutes(3) &&
                  CompanionConnection.ProcessTimeout("status", seconds) == TimeSpan.FromSeconds(30) &&
                  CompanionConnection.ProcessTimeout("logout", seconds) == TimeSpan.FromSeconds(30) &&
                  CompanionConnection.ProcessTimeout("login", seconds) == TimeSpan.FromMinutes(6),
                "response preference must not change account or synthetic-test deadlines");
        }
    }

    internal static async Task FrozenBeforePreparation()
    {
        using var fixture = new Fixture();
        var minutes = 0;
        var preferenceReads = 0;
        var pausePreparation = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var connection = new CompanionConnection(
            responseLimitMinutes: () => { preferenceReads++; return minutes; },
            preparePython: async (directory, token) =>
            {
                if (pausePreparation)
                {
                    entered.TrySetResult();
                    await released.Task.WaitAsync(token);
                }
                return await Fixture.PreparePython(directory, token);
            });
        await Authorize(connection, fixture);
        var readsBefore = preferenceReads;
        pausePreparation = true;
        fixture.Scenario("ask_success");
        Check(connection.StartAsk("{\"reasoning_effort\":\"high\"}", false, "Frozen timeout"),
            "question must be accepted before preparation completes");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        minutes = 15;
        Check(ReadArguments(fixture).Length == 1 && preferenceReads == readsBefore + 1,
            "one preference snapshot must precede preparation and no question process may have launched yet");
        released.TrySetResult();
        await WaitIdle(connection);
        CheckTimeoutArgument(ReadArguments(fixture).Last(), 600);
        Check(preferenceReads == readsBefore + 1, "running question must not reread a changed preference");

        pausePreparation = false;
        Check(connection.StartAsk("{}", false, "Next timeout"), "later explicit question must start");
        await WaitIdle(connection);
        CheckTimeoutArgument(ReadArguments(fixture).Last(), 900);
        Check(preferenceReads == readsBefore + 2, "next explicit question must read the changed preference once");
    }

    internal static async Task InvalidInputs()
    {
        using var fixture = new Fixture();
        var minutes = 0;
        var preparations = 0;
        using var connection = new CompanionConnection(responseLimitMinutes: () => minutes,
            preparePython: (directory, token) =>
            {
                preparations++;
                return Fixture.PreparePython(directory, token);
            });
        await Authorize(connection, fixture);
        var beforePreparations = preparations;
        var beforeArguments = File.ReadAllBytes(fixture.ArgumentsLogFile);
        connection.Draft = "Keep this draft";
        foreach (var request in new[]
        {
            "{", "null", "[]", "{\"request\":[]}",
            "{\"request\":{},\"request\":{\"reasoning_effort\":\"high\"}}",
            "{\"reasoning_effort\":\"medium\",\"reasoning_effort\":\"high\"}",
            "{\"reasoning_effort\":null}", "{\"reasoning_effort\":10}",
            "{\"request\":{\"reasoning_effort\":true}}",
        })
            Check(!connection.StartAsk(request, false, "Invalid timing request"), "malformed timing input must be rejected");
        foreach (var invalidMinutes in new[] { -1, 31, int.MaxValue })
        {
            minutes = invalidMinutes;
            Check(!connection.StartAsk("{}", false, "Invalid timing preference"), "invalid fixed preference must be rejected");
        }
        Check(!connection.View.Busy && connection.View.IsSignedIn && connection.View.History.Count == 0 &&
              connection.Draft == "Keep this draft" && preparations == beforePreparations &&
              beforeArguments.SequenceEqual(File.ReadAllBytes(fixture.ArgumentsLogFile)),
            "rejected requests must not prepare or launch processes, consume drafts or alter account/history state");
    }

    private static async Task Authorize(CompanionConnection connection, Fixture fixture)
    {
        fixture.Scenario("login_status");
        connection.Start("status");
        await WaitIdle(connection);
        Check(connection.View.IsSignedIn && connection.View.CanTest, "synthetic status must authorize the fixture");
    }

    private static string[][] ReadArguments(Fixture fixture) => File.ReadAllLines(fixture.ArgumentsLogFile)
        .Select(line => JsonSerializer.Deserialize<string[]>(line)!).ToArray();

    private static void CheckTimeoutArgument(string[] arguments, int expectedSeconds)
    {
        var position = Array.IndexOf(arguments, "--response-timeout-seconds");
        Check(position >= 0 && arguments.Count(value => value == "--response-timeout-seconds") == 1 &&
              arguments[position + 1] == expectedSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
              position + 2 == Array.IndexOf(arguments, "bridge") && arguments[^1] == "ask",
            $"ask must pass exactly {expectedSeconds} seconds as a global option before bridge ask");
    }

    private static async Task WaitIdle(CompanionConnection connection)
    {
        var timer = Stopwatch.StartNew();
        while (connection.View.Busy)
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Response timeout fixture did not finish");
            await Task.Delay(20);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
