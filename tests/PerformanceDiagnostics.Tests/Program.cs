using CompAI;
using System.Text.Json;

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static bool Accept(PerformanceDiagnostics.RequestMetrics request, string json)
{
    using var doc = JsonDocument.Parse(json);
    return request.Accept(doc.RootElement);
}

PerformanceDiagnostics.Clear();
Assert(PerformanceDiagnostics.Report.Contains("Input: unknown"), "Missing tokens must stay unknown");
for (var i = 0; i < 100; i++) PerformanceDiagnostics.Record("reader.inventory", i);
var report = PerformanceDiagnostics.Report;
Assert(report.Contains("last 99.0 ms; mean 83.5 ms (32 samples)"), "Old samples must be evicted");
Assert(ReferenceEquals(report, PerformanceDiagnostics.Report), "Unchanged report must be cached");
PerformanceDiagnostics.Record("private question", 9);
PerformanceDiagnostics.Record("reader.inventory", double.NaN);
PerformanceDiagnostics.Record("reader.inventory", -1);
Assert(report == PerformanceDiagnostics.Report, "Invalid samples must not change report");
PerformanceDiagnostics.Record("reader.menus.observe", 12.5);
using (PerformanceDiagnostics.Measure("reader.recent_menus")) { }
Assert(PerformanceDiagnostics.Report.Contains("Shop and glamour capture: last 12.5 ms") &&
    PerformanceDiagnostics.Report.Contains("Recent menu capture (GC, Bestiary, PvP):"),
    "Existing local menu timing scopes must not be silently discarded");
var first = PerformanceDiagnostics.BeginQuestion();
Assert(!Accept(first, "{\"stage\":\"reader.menus.observe\",\"elapsed_ms\":12.5}") &&
    !Accept(first, "{\"stage\":\"reader.recent_menus\",\"elapsed_ms\":12.5}"),
    "Local menu timing labels must not admit provider-supplied session measurements");
Assert(!Accept(first, "{\"stage\":\"provider.tokens\",\"input_tokens\":10,\"prompt\":\"private text\"}"), "Reject unknown fields");
Assert(!Accept(first, "{\"stage\":\"provider.tokens\",\"input_tokens\":10,\"input_tokens\":20}"), "Reject duplicate fields");
Assert(!Accept(first, "{\"stage\":\"provider.tokens\",\"input_tokens\":10,\"cached_input_tokens\":11}"), "Reject impossible subset");
Assert(!Accept(first, "{\"stage\":\"provider.tokens\",\"output_tokens\":true}"), "Reject boolean tokens");
Assert(!Accept(first, "{\"stage\":\"reader.inventory\",\"elapsed_ms\":-1}"), "Reject negative duration");
const string usage = "{\"stage\":\"provider.tokens\",\"input_tokens\":100,\"cached_input_tokens\":50,\"output_tokens\":20,\"reasoning_tokens\":null}";
Assert(Accept(first, usage) && Accept(first, usage), "Valid usage accepted");
Assert(PerformanceDiagnostics.Report.Contains("Input: 100; cached input: 50"), "Cumulative events replace, not sum");
Assert(PerformanceDiagnostics.Report.Contains("reasoning: unknown"), "Missing reasoning remains unknown");
var second = PerformanceDiagnostics.BeginQuestion();
Assert(second.Report.Contains("Input: unknown"), "New question must not show previous usage");
Assert(first.Report.Contains("Input: 100; cached input: 50"), "Concurrent question retains its own tokens");
Assert(Accept(second, "{\"stage\":\"provider.tokens\",\"input_tokens\":200}"), "Second request usage accepted");
Assert(Accept(first, "{\"stage\":\"tool.game_data\",\"elapsed_ms\":250}"), "First request tool timing accepted");
Assert(!second.Report.Contains("Public game-data lookup:"), "Other request does not inherit tool timing");
Assert(Accept(first, "{\"stage\":\"provider.activity\",\"tool_calls\":3,\"web_searches\":1}"), "Activity accepted");
Assert(Accept(first, "{\"stage\":\"provider.activity\",\"tool_calls\":2}"), "Repeated or stale counter tolerated");
Assert(first.Report.Contains("Observed tool calls: 3; web-search items: 1; reasoning items: unknown"), "Activity does not regress or invent absent counts");
Assert(first.Status.Contains("3 tools") && first.Status.Contains("1 web"), "Compact status displays only observed tool and web counts");
Assert(first.Status.Contains(" \u00b7 ") && !first.Status.Contains("\uFFFD"), "Compact status separators preserve Unicode");
Assert(!Accept(first, "{\"stage\":\"request.activity\",\"state\":\"private prompt\"}"), "Free text stage rejected");
first.Complete("Cancelled");
Assert(!Accept(first, usage), "Late telemetry cannot change completed request");
Assert(PerformanceDiagnostics.Report.Contains("Latest completed request:" + Environment.NewLine + "Cancelled"), "Completed report retained alongside active request");
second.Complete("Completed");
Assert(PerformanceDiagnostics.Report.Contains("Input: 200") && !PerformanceDiagnostics.Report.Contains("Input: 100"), "Only latest completion retained");
Assert(PerformanceDiagnostics.Report.Contains("Inventory capture:"), "Question reset must retain timing history");
using (PerformanceDiagnostics.Measure("storage.save")) { }
Assert(PerformanceDiagnostics.Report.Contains("Save chat:"), "Timing scope must record on disposal");
PerformanceDiagnostics.Clear();
Assert(!PerformanceDiagnostics.Report.Contains("Inventory capture:"), "Clear removes samples");
Console.WriteLine("Performance diagnostics: bounded samples, unknown counters, replacement, privacy schema, and reset passed.");
