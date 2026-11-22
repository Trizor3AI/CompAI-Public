using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CompAI;

internal static class CompanionLineReaderChecks
{
    internal static async Task Boundaries()
    {
        var maximum = CompanionLineReader.MaximumCharacters;
        foreach (var ending in new[] { "\n", "\r\n", "\r", "" })
        {
            var exact = new string('x', maximum);
            var reader = new CompanionLineReader(new FragmentReader(exact + ending, 4096));
            Check(await reader.ReadLineAsync(default) == exact, "exact limit accepted with every line ending and EOF");
            Check(await reader.ReadLineAsync(default) is null, "no phantom line at EOF");
            await Expect<InvalidDataException>(() => new CompanionLineReader(
                new FragmentReader(exact + "x" + ending, 4096)).ReadLineAsync(default).AsTask());
        }

        var mixed = new CompanionLineReader(new FragmentReader("\nfirst\r\n\rsecond\rthird\nlast", 1));
        foreach (var expected in new[] { "", "first", "", "second", "third", "last" })
            Check(await mixed.ReadLineAsync(default) == expected, "split CRLF, bare CR, empty and final unterminated lines preserved");
        Check(await mixed.ReadLineAsync(default) is null, "mixed frames end at EOF");
        Check(await new CompanionLineReader(new StringReader("")).ReadLineAsync(default) is null, "empty EOF returns null");

        // The emoji straddles the 4,096-character boundary. Its two UTF-16 code
        // units count toward the existing limit, including when bytes split too.
        var unicode = new string('a', 4095) + "😀" + new string('b', maximum - 4097);
        using var bytes = new FragmentStream(Encoding.UTF8.GetBytes(unicode + "\r\n"), 3);
        using var decoded = new StreamReader(bytes, new UTF8Encoding(false, true), false, 128);
        Check(await new CompanionLineReader(decoded).ReadLineAsync(default) == unicode,
            "incremental UTF-8 decoding and split surrogate pair preserve exact-limit Unicode");
        await Expect<InvalidDataException>(() => new CompanionLineReader(
            new FragmentReader(unicode + "x\n", 4096)).ReadLineAsync(default).AsTask());
        var endingPair = new string('a', maximum - 2) + "😀";
        Check(await new CompanionLineReader(new FragmentReader(endingPair + "\n", 4096)).ReadLineAsync(default) == endingPair,
            "surrogate pair fitting exactly at the line limit is preserved");
        await Expect<InvalidDataException>(() => new CompanionLineReader(
            new FragmentReader("a" + endingPair + "\n", 4096)).ReadLineAsync(default).AsTask());
    }

    internal static async Task BoundedReadAhead()
    {
        var source = new EndlessReader();
        await Expect<InvalidDataException>(() => new CompanionLineReader(source).ReadLineAsync(default).AsTask());
        Check(source.CharactersRead <= CompanionLineReader.MaximumCharacters + CompanionLineReader.BufferCharacters &&
              source.LargestRead <= CompanionLineReader.BufferCharacters,
            "endless newline-free input is rejected with only a fixed buffer of read-ahead");

        var frames = new FragmentReader("one\ntwo\r\nthree\nfour\r", 4096);
        var reader = new CompanionLineReader(frames);
        foreach (var expected in new[] { "one", "two", "three", "four" })
            Check(await reader.ReadLineAsync(default) == expected, "read-ahead frames remain in order");
        Check(frames.ReadCalls == 1, "multiple short frames reuse one buffered read");
    }

    internal static async Task Cancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var source = new FragmentReader("one\ntwo\n", 4096);
        var reader = new CompanionLineReader(source);
        await Expect<OperationCanceledException>(() => reader.ReadLineAsync(cancelled.Token).AsTask());
        Check(source.ReadCalls == 0, "pre-cancelled read does not consume input");
        Check(await reader.ReadLineAsync(default) == "one", "uncancelled first frame remains available");
        await Expect<OperationCanceledException>(() => reader.ReadLineAsync(cancelled.Token).AsTask());
        Check(await reader.ReadLineAsync(default) == "two", "cancelled buffered read leaves the next frame intact");

        using var pending = new CancellationTokenSource();
        var blocking = new BlockingReader();
        var read = new CompanionLineReader(blocking).ReadLineAsync(pending.Token).AsTask();
        await blocking.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        pending.Cancel();
        await Expect<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    internal static async Task ProcessLifecycle()
    {
        foreach (var variant in new[] { "lf", "crlf", "eof", "oversized", "unterminated", "truncated", "cancel" })
        {
            using var fixture = new Fixture();
            using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
            fixture.Scenario("login_status");
            connection.Start("status");
            await Until(() => !connection.View.Busy);
            Check(connection.View.IsSignedIn && connection.View.CanTest, "synthetic account authorizes fixture");
            fixture.Scenario("ask_bounded_output");
            connection.Draft = "Keep the failed request";
            Check(connection.StartAsk(JsonSerializer.Serialize(new { variant }), false, "Bounded request"), "synthetic request starts");
            if (variant == "cancel")
            {
                await Until(() => File.Exists(fixture.CancelReadyFile));
                connection.Cancel();
            }
            await Until(() => !connection.View.Busy);
            var success = variant is "lf" or "crlf" or "eof";
            Check(connection.View.History.Count == (success ? 1 : 0), "only completed bounded answers enter history: " + variant);
            Check(connection.View.PartialAnswer.Length == 0 && connection.View.QuestionAnswer == (success ? "Bounded answer" : "No answer"),
                "failed or cancelled frames leave no partial or completed answer: " + variant);
            if (!success)
                Check(connection.Draft == "Keep the failed request" && connection.View.RetryQuestion == "Bounded request",
                    "failure retains draft and explicit retry: " + variant);
            if (!success && variant != "cancel")
                Check(!connection.View.CanTest, "protocol failure requires refreshed authorization: " + variant);
            var processId = int.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(fixture.RequestFile)!, "bounded-pid.txt")));
            await Until(() => Exited(processId));
            Check(File.ReadAllLines(fixture.ActionLogFile).Count(action => action == "ask") == 1,
                "no automatic question retry after completion/failure/cancellation: " + variant);
        }
    }

    private static bool Exited(int processId)
    {
        try { using var process = Process.GetProcessById(processId); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }

    private static async Task Until(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Bounded bridge fixture did not finish");
            await Task.Delay(20);
        }
    }

    private static async Task Expect<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class FragmentReader(string value, int fragmentSize) : TextReader
    {
        private int position;
        internal int ReadCalls;
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.ReadCalls++;
            var count = Math.Min(Math.Min(buffer.Length, fragmentSize), value.Length - this.position);
            value.AsMemory(this.position, count).CopyTo(buffer);
            this.position += count;
            return ValueTask.FromResult(count);
        }
    }

    private sealed class EndlessReader : TextReader
    {
        internal int CharactersRead;
        internal int LargestRead;
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.CharactersRead += buffer.Length;
            this.LargestRead = Math.Max(this.LargestRead, buffer.Length);
            if (this.CharactersRead > CompanionLineReader.MaximumCharacters + CompanionLineReader.BufferCharacters)
                throw new Exception("Reader consumed unbounded input before rejecting the frame");
            buffer.Span.Fill('x');
            return ValueTask.FromResult(buffer.Length);
        }
    }

    private sealed class BlockingReader : TextReader
    {
        private bool first = true;
        internal readonly TaskCompletionSource Waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (this.first)
            {
                this.first = false;
                buffer.Span.Fill('x');
                return buffer.Length;
            }
            this.Waiting.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private sealed class FragmentStream(byte[] bytes, int fragmentSize) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, fragmentSize)], cancellationToken);
    }
}
