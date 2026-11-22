using System.Text.Json;
using CompAI;

var tests = new (string Name, Action Run)[]
{
    ("poll duplicates and incremental reveal retain one longest line", DuplicatesAndReveal),
    ("repeated dialogue after another line or a confirmed close stays ordered", RepeatedLines),
    ("each line expires independently since its last observation", PerLineExpiry),
    ("backwards time drops future observations", BackwardsClock),
    ("area changes preserve history and suppress a lingering old Talk page", AreaChanges),
    ("repeated area changes do not refresh retention", AreaExpiry),
    ("line count and character bounds drop oldest complete lines", Bounds),
    ("reveal growth also enforces the aggregate character bound", RevealBounds),
    ("snapshots and lookups stay frozen after later store changes", FrozenSnapshot),
    ("query summary and result expose only the exact scalar contract", WireContract),
    ("query rejects missing extra duplicate and malformed arguments", InvalidArguments),
    ("escaped multilingual pages stay within byte budget without text truncation", EscapedPages),
    ("empty and out-of-range pages terminate without integer overflow", EmptyPages),
    ("explicit clear resets history dropped count and duplicate state", Clear),
    ("oversized reader values cannot enter the bounded store", InvalidObservations),
    ("unchanged raw Talk pages avoid decoding without missing rapid pages", DecodeWork),
    ("expiry deadline skips repeated scans while preserving exact expiry", ExpiryWork),
};

var failures = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} observed NPC dialogue tests passed (offline).");
return failures == 0 ? 0 : 1;

static DateTimeOffset Now() => DateTimeOffset.Parse("2026-10-11T12:00:00Z");
static void DecodeWork()
{
    var cache = new NpcDialogueTextCache();
    var speaker = System.Text.Encoding.UTF8.GetBytes("Guide");
    var first = System.Text.Encoding.UTF8.GetBytes("First page.");
    var second = System.Text.Encoding.UTF8.GetBytes("Other page.");
    var calls = 0;
    string Decode(ReadOnlySpan<byte> bytes) { calls++; return System.Text.Encoding.UTF8.GetString(bytes); }
    for (var frame = 0; frame < 120; frame++)
    {
        var bytes = frame < 60 ? first : second;
        var line = cache.Read(speaker, bytes, Decode);
        Check(line.Text == (frame < 60 ? "First page." : "Other page."), "same-length page was missed");
        Check(cache.Matches(speaker, bytes), "raw stability recheck failed");
    }
    Check(calls == 4, "unchanged text was decoded again");
    Console.WriteLine($"  Work: 120 Talk updates / 2 pages: text decodes 480 -> {calls}.");
    var store = new NpcConversationStore();
    for (var frame = 0; frame < 12; frame++)
    {
        // Consecutive events, identical lengths, no time-based throttle.
        var bytes = frame % 2 == 0 ? first : second;
        store.Observe(cache.Read(speaker, bytes, Decode), Now().AddMilliseconds(frame));
    }
    Check(store.CaptureSnapshot(Now().AddMilliseconds(12)).Lines.Count == 12, "rapid pages were lost");
    first[0] = (byte)'X';
    Check(!cache.Matches(speaker, first), "cache retained caller-owned bytes");
    cache.Clear();
    Check(!cache.Matches(speaker, second), "closure/permission clear retained text");
    var before = calls;
    cache.Read(speaker, second, Decode);
    Check(calls == before + 2, "clear must force a fresh decode");
    Reject(() => cache.Read(new byte[513], first, Decode));
    Reject(() => cache.Read(speaker, new byte[16385], Decode));
    Reject(() => cache.Read(speaker, first, _ => new string('x', 2049)));
}

static void ExpiryWork()
{
    var store = new NpcConversationStore();
    var now = Now();
    for (var i = 0; i < 128; i++) store.Observe(new("Guide", $"Line {i:D3}."), now);
    var before = store.ExpiryLinesInspected;
    for (var frame = 0; frame < 3600; frame++) store.Expire(now.AddMilliseconds(frame * 16));
    Check(store.ExpiryLinesInspected == before, "unexpired history was repeatedly scanned");
    Console.WriteLine("  Work: 3,600 expiry polls / 128 lines: lines inspected 460,800 -> 0.");
    Check(store.CaptureSnapshot(now.AddMinutes(30).AddTicks(-1)).Lines.Count == 128, "expired too early");
    Check(store.CaptureSnapshot(now.AddMinutes(30)).Lines.Count == 0 &&
        store.ExpiryLinesInspected - before == 128, "expiry deadline did not inspect and drop all lines");

    store.Clear();
    store.Observe(new("Guide", "Still visible."), now);
    store.Observe(new("Guide", "Still visible."), now.AddMinutes(29));
    Check(store.CaptureSnapshot(now.AddMinutes(30)).Lines.Count == 1, "renewed active page expired early");
    Check(store.CaptureSnapshot(now.AddMinutes(59)).Lines.Count == 0, "renewed page did not expire exactly");
}
static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
static JsonElement Args(string value) => JsonDocument.Parse(value).RootElement.Clone();
static JsonElement Wire(object value) => JsonSerializer.SerializeToElement(value);
static NpcDialogueLookupResult Query(NpcDialogueLookup lookup, int offset = 0, int limit = 10) =>
    lookup.Query(Args($"{{\"offset\":{offset},\"limit\":{limit}}}"));
static void Fields(JsonElement value, params string[] fields) => Check(
    value.EnumerateObject().Select(property => property.Name).Order().SequenceEqual(fields.Order()),
    "Unexpected wire fields: " + value);
static void Reject(Action action)
{
    try { action(); }
    catch (InvalidOperationException) { return; }
    catch (ArgumentException) { return; }
    throw new Exception("Expected bounded-input rejection.");
}

static void DuplicatesAndReveal()
{
    var store = new NpcConversationStore();
    var now = Now();
    store.Observe(new("Guide", "Wel"), now);
    store.Observe(new("Guide", "Welcome"), now.AddSeconds(1));
    store.Observe(new("Guide", "Welcome, traveller."), now.AddSeconds(2));
    store.Observe(new("Guide", "Welcome, traveller."), now.AddSeconds(3));
    store.Observe(new("Guide", "Wel"), now.AddSeconds(4));
    var snapshot = store.CaptureSnapshot(now.AddSeconds(4));
    Check(snapshot.Lines.Count == 1 && snapshot.Lines[0].Text == "Welcome, traveller." &&
        snapshot.Lines[0].ObservedAtUtc == now.AddSeconds(4) && snapshot.DroppedLines == 0,
        "Polling and reveal frames must refresh one longest observed line.");
    store.Observe(new("Someone else", "Welcome, traveller."), now.AddSeconds(5));
    Check(store.CaptureSnapshot(now.AddSeconds(5)).Lines.Count == 2, "A different speaker is a distinct observation.");
}

static void RepeatedLines()
{
    var store = new NpcConversationStore();
    var now = Now();
    store.Observe(new("Guide", "Again."), now);
    store.Observe(new("Guide", "A different line."), now.AddSeconds(1));
    store.Observe(new("Guide", "Again."), now.AddSeconds(2));
    store.Observe(null, now.AddSeconds(3));
    store.Observe(new("Guide", "Again."), now.AddSeconds(4));
    var snapshot = store.CaptureSnapshot(now.AddSeconds(4));
    Check(snapshot.Lines.Select(line => line.Text).SequenceEqual(
        new[] { "Again.", "A different line.", "Again.", "Again." }), "Repeated lines were globally deduplicated.");
}

static void PerLineExpiry()
{
    var store = new NpcConversationStore();
    var now = Now();
    store.Observe(new("Guide", "First."), now);
    store.Observe(new("Guide", "Second."), now.AddMinutes(10));
    store.Observe(new("Guide", "Second."), now.AddMinutes(20));
    var snapshot = store.CaptureSnapshot(now.AddMinutes(30));
    Check(snapshot.Lines.Count == 1 && snapshot.Lines[0].Text == "Second." && snapshot.DroppedLines == 1,
        "A fresh later line must not extend the earlier line's retention.");
    store.Expire(now.AddMinutes(49));
    Check(store.CaptureSnapshot(now.AddMinutes(49)).Lines.Count == 1, "Duplicate observations refresh their own line.");
    snapshot = store.CaptureSnapshot(now.AddMinutes(50));
    Check(snapshot.Lines.Count == 0 && snapshot.DroppedLines == 2, "Exact expiry boundary must remove the line.");
}

static void BackwardsClock()
{
    var store = new NpcConversationStore();
    store.Observe(new("Guide", "Future."), Now());
    var snapshot = store.CaptureSnapshot(Now().AddSeconds(-1));
    Check(snapshot.Lines.Count == 0 && snapshot.DroppedLines == 1, "Future observations cannot survive a reversed clock.");
}

static void AreaChanges()
{
    var store = new NpcConversationStore();
    var now = Now();
    store.Observe(new("Guide", "Earlier."), now);
    store.Observe(new("Guide", "Leaving"), now.AddSeconds(1));
    store.OnAreaChanged(now.AddSeconds(2));
    store.Observe(new("Guide", "Leaving"), now.AddSeconds(3));
    store.Observe(new("Guide", "Leaving this place."), now.AddSeconds(4));
    var snapshot = store.CaptureSnapshot(now.AddSeconds(4));
    Check(snapshot.Lines.Count == 2 && snapshot.Lines.All(line => line.FromPreviousArea) &&
        snapshot.Lines[1].ObservedAtUtc == now.AddSeconds(1), "Lingering Talk refreshed old-area data.");
    store.Observe(new("Guide", "Arrived."), now.AddSeconds(5));
    snapshot = store.CaptureSnapshot(now.AddSeconds(5));
    Check(snapshot.Lines.Count == 3 && !snapshot.Lines[2].FromPreviousArea, "Different new-area line was suppressed.");
    store.OnAreaChanged(now.AddSeconds(6));
    store.Observe(null, now.AddSeconds(7));
    store.Observe(new("Guide", "Arrived."), now.AddSeconds(8));
    snapshot = store.CaptureSnapshot(now.AddSeconds(8));
    Check(snapshot.Lines.Count == 4 && snapshot.Lines[2].FromPreviousArea && !snapshot.Lines[3].FromPreviousArea,
        "A closed then reopened identical line must be new-area dialogue.");
}

static void AreaExpiry()
{
    var store = new NpcConversationStore();
    var now = Now();
    var line = new NpcDialogueContext("Guide", "Historical.");
    store.Observe(line, now);
    store.OnAreaChanged(now.AddMinutes(1));
    store.OnAreaChanged(now.AddMinutes(10));
    store.Observe(line, now.AddMinutes(29));
    var snapshot = store.CaptureSnapshot(now.AddMinutes(30));
    Check(snapshot.Lines.Count == 0 && snapshot.DroppedLines == 1, "Zone transitions refreshed the historical timer.");
    store.Observe(line, now.AddMinutes(31));
    Check(store.CaptureSnapshot(now.AddMinutes(31)).Lines.Count == 0, "Expired lingering Talk was recaptured.");
    store.Observe(null, now.AddMinutes(32));
    store.Observe(line, now.AddMinutes(33));
    Check(store.CaptureSnapshot(now.AddMinutes(33)).Lines.Count == 1, "Closing Talk must release area suppression.");
}

static void Bounds()
{
    var now = Now();
    var store = new NpcConversationStore();
    for (var index = 0; index < 140; index++) store.Observe(new("Guide", $"Line {index:D3}."), now);
    var snapshot = store.CaptureSnapshot(now);
    Check(snapshot.Lines.Count == NpcConversationStore.MaxLines && snapshot.DroppedLines == 12 &&
        snapshot.Lines[0].Text == "Line 012." && snapshot.Lines[^1].Text == "Line 139.", "Count bound lost order.");
    store.Clear();
    for (var index = 0; index < 20; index++)
        store.Observe(new(new string('S', 128), index.ToString("D3") + new string('x', 2045)), now);
    snapshot = store.CaptureSnapshot(now);
    Check(snapshot.Lines.Count == 15 && snapshot.DroppedLines == 5 &&
        snapshot.Lines.Sum(line => line.Speaker.Length + line.Text.Length) <= NpcConversationStore.MaxCharacters &&
        snapshot.Lines[0].Text.StartsWith("005"), "Character budget must include speakers and drop whole oldest lines.");
}

static void RevealBounds()
{
    var now = Now();
    var store = new NpcConversationStore();
    for (var index = 0; index < 16; index++)
        store.Observe(new("", index.ToString("D2") + new string('x', 1998)), now);
    store.Observe(new("", "Last"), now);
    store.Observe(new("", "Last" + new string('y', 2044)), now);
    var snapshot = store.CaptureSnapshot(now);
    Check(snapshot.DroppedLines == 1 && snapshot.Lines.Count == 16 && snapshot.Lines[^1].Text.Length == 2048 &&
        snapshot.Lines.Sum(line => line.Text.Length) <= NpcConversationStore.MaxCharacters,
        "A growing reveal must evict oldest text without truncating the revealed line.");
}

static void FrozenSnapshot()
{
    var now = Now();
    var store = new NpcConversationStore();
    store.Observe(new("Guide", "Saved"), now);
    var snapshot = store.CaptureSnapshot(now);
    var lookup = new NpcDialogueLookup(snapshot);
    store.Observe(new("Guide", "Saved full sentence."), now.AddSeconds(1));
    store.OnAreaChanged(now.AddSeconds(2));
    store.Clear();
    Check(snapshot.Lines[0].Text == "Saved" && !snapshot.Lines[0].FromPreviousArea &&
        snapshot.Lines[0].ObservedAtUtc == now && Query(lookup).lines[0].text == "Saved",
        "Frozen observations changed after later store updates.");
    var input = new[] { new NpcDialogueLine("Speaker", "Input", now, false) };
    var copy = new NpcConversationSnapshot(now, 0, input);
    input[0] = input[0] with { Text = "Mutated" };
    Check(copy.Lines[0].Text == "Input", "Snapshot retained its caller's mutable array.");
    var returned = Query(lookup);
    returned.lines[0] = returned.lines[0] with { text = "Mutated output" };
    Check(Query(lookup).lines[0].text == "Saved", "Mutable result array reached the frozen lookup.");
}

static void WireContract()
{
    var now = Now();
    var store = new NpcConversationStore();
    store.Observe(new("", "An unattributed observed line."), now);
    var lookup = new NpcDialogueLookup(store.CaptureSnapshot(now));
    var summary = Wire(lookup.Summary);
    Fields(summary, "scope", "captured_at_utc", "total_lines", "dropped_lines");
    Check(summary.GetProperty("scope").GetString() == "observed_npc_dialogue" &&
        !summary.GetRawText().Contains("An unattributed"), "Summary must contain counts and timestamp only.");
    var result = Wire(Query(lookup));
    Fields(result, "scope", "captured_at_utc", "total_lines", "dropped_lines", "offset", "next_offset", "lines");
    Fields(result.GetProperty("lines")[0], "speaker", "text", "observed_at_utc", "from_previous_area");
    Check(result.GetProperty("lines")[0].GetProperty("speaker").GetString() == "", "No speaker identity may be inferred.");
}

static void InvalidArguments()
{
    var lookup = new NpcDialogueLookup(new NpcConversationStore().CaptureSnapshot(Now()));
    foreach (var json in new[] { "null", "[]", "{}", "{\"offset\":0}", "{\"limit\":1}",
        "{\"offset\":0,\"limit\":1,\"other\":true}", "{\"offset\":0,\"offset\":1,\"limit\":1}",
        "{\"offset\":0,\"limit\":1,\"limit\":2}", "{\"offset\":-1,\"limit\":1}",
        "{\"offset\":0,\"limit\":0}", "{\"offset\":0,\"limit\":21}",
        "{\"offset\":0.5,\"limit\":1}", "{\"offset\":0,\"limit\":1.5}",
        "{\"offset\":2147483648,\"limit\":1}", "{\"offset\":\"0\",\"limit\":1}",
        "{\"offset\":0,\"limit\":true}" })
        Reject(() => lookup.Query(Args(json)));
}

static void EscapedPages()
{
    var now = Now();
    var store = new NpcConversationStore();
    for (var index = 0; index < 10; index++)
        store.Observe(new(new string('語', 128), index.ToString() + new string('語', 2047)), now);
    var snapshot = store.CaptureSnapshot(now);
    var lookup = new NpcDialogueLookup(snapshot);
    var received = new List<string>();
    var offset = 0;
    do
    {
        var page = Query(lookup, offset);
        Check(JsonSerializer.SerializeToUtf8Bytes(page).Length <= NpcDialogueLookup.MaxBytes && page.lines.Length > 0,
            "Escaped response exceeded its byte budget or stalled.");
        Check(page.lines.Length == 1 && page.lines[0].text.Length == 2048 && page.total_lines == 10,
            "Worst-case escaped lines must be paged whole.");
        received.AddRange(page.lines.Select(line => line.text));
        if (page.next_offset is not { } next) break;
        Check(next > offset, "Cursor must advance.");
        offset = next;
    } while (received.Count <= 10);
    Check(received.SequenceEqual(snapshot.Lines.Select(line => line.Text)), "Pagination truncated skipped or duplicated text.");
}

static void EmptyPages()
{
    var store = new NpcConversationStore();
    var lookup = new NpcDialogueLookup(store.CaptureSnapshot(Now()));
    foreach (var offset in new[] { 0, 1, int.MaxValue })
    {
        var page = Query(lookup, offset);
        Check(page.lines.Length == 0 && page.next_offset is null && page.offset == offset, "Empty pagination must terminate.");
    }
    store.Observe(new("Guide", "One"), Now());
    var beyond = Query(new(store.CaptureSnapshot(Now())), int.MaxValue);
    Check(beyond.lines.Length == 0 && beyond.next_offset is null && beyond.total_lines == 1, "Large offset overflowed.");
}

static void Clear()
{
    var store = new NpcConversationStore();
    var now = Now();
    store.Observe(new("Guide", "Same"), now);
    store.OnAreaChanged(now.AddSeconds(1));
    store.Expire(now.AddMinutes(30));
    Check(store.CaptureSnapshot(now.AddMinutes(30)).DroppedLines == 1, "Fixture must have a dropped line.");
    store.Clear();
    var empty = store.CaptureSnapshot(now.AddMinutes(30));
    Check(empty.Lines.Count == 0 && empty.DroppedLines == 0, "Clear must reset both observations and counts.");
    store.Observe(new("Guide", "Same"), now.AddMinutes(31));
    Check(store.CaptureSnapshot(now.AddMinutes(31)).Lines.Count == 1, "Clear retained the old zone suppression state.");
}

static void InvalidObservations()
{
    var store = new NpcConversationStore();
    foreach (var line in new[] { new NpcDialogueContext("Guide", ""), new("Guide", new string('x', 2049)),
        new(new string('x', 129), "Text"), new(null!, "Text"), new("Guide", null!) })
        Reject(() => store.Observe(line, Now()));
    Check(store.CaptureSnapshot(Now()).Lines.Count == 0, "An invalid observation entered the store.");
}
