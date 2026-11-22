using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace CompAI;

// Session-only numeric observations. Never retain questions, responses, identifiers,
// paths, tool arguments, account data, or unrecognized runtime fields.
internal static class PerformanceDiagnostics
{
    internal const int SampleLimit = 32;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Queue<double>> Samples = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["plugin.startup"] = "Plugin startup",
        ["storage.load"] = "Load saved chats",
        ["storage.save"] = "Save chat",
        ["storage.overview"] = "Storage overview refresh",
        ["client.question"] = "Question incl. bridge/cleanup",
        ["client.account"] = "Account operation incl. bridge/cleanup",
        ["client.synthetic"] = "Synthetic test incl. bridge/cleanup",
        ["client.first_answer"] = "First answer received incl. startup",
        ["runtime.startup"] = "Runtime startup",
        ["runtime.preflight"] = "Runtime preflight",
        ["provider.first_answer"] = "First provider answer",
        ["provider.turn"] = "Provider turn",
        ["reader.inventory"] = "Inventory capture",
        ["reader.character"] = "Character detail capture",
        ["reader.currency"] = "Currency capture",
        ["reader.duty"] = "Duty capture",
        ["reader.collections"] = "Collectible capture",
        ["reader.progression"] = "Progression capture",
        ["reader.activities.observe"] = "Activity menu capture",
        ["reader.menus.observe"] = "Shop and glamour capture",
        ["reader.recent_menus"] = "Recent menu capture (GC, Bestiary, PvP)",
        ["reader.crafting_log"] = "Crafting log capture",
        ["reader.game_definitions"] = "Local game definitions",
        ["reader.gc_supply"] = "Grand Company delivery capture",
        ["reader.npc_target"] = "Selected target capture",
        ["reader.quests"] = "Quest capture",
        ["reader.npc"] = "NPC dialogue decode",
        ["tool.inventory"] = "Inventory lookup",
        ["tool.currencies"] = "Currency lookup",
        ["tool.duties"] = "Duty lookup",
        ["tool.collections"] = "Collectible lookup",
        ["tool.progression"] = "Progression lookup",
        ["tool.crafting_log"] = "Crafting log lookup",
        ["tool.game_definitions"] = "Local definition lookup",
        ["tool.gc_supply"] = "Grand Company delivery lookup",
        ["tool.npc_target"] = "Selected target lookup",
        ["tool.storage"] = "Storage cache lookup",
        ["tool.quests"] = "Quest lookup",
        ["tool.history"] = "Older chat lookup",
        ["tool.game_context"] = "Game context lookup",
        ["tool.npc_dialogue"] = "NPC dialogue lookup",
        ["tool.character_details"] = "Character detail lookup",
        ["tool.personality_details"] = "Personality detail lookup",
        ["tool.game_data"] = "Public game-data lookup",
        ["tool.menus"] = "Observed menu lookup",
        ["tool.activities"] = "Activity lookup",
        ["tool.valuation"] = "Saved item valuation",
    };
    private static readonly string[] TokenKeys = { "input_tokens", "cached_input_tokens", "output_tokens", "reasoning_tokens" };
    private static readonly string[] ActivityKeys = { "tool_calls", "web_searches", "reasoning_items" };
    private static readonly Dictionary<string, string> ActivityLabels = new(StringComparer.Ordinal)
    {
        ["starting"] = "Starting", ["working"] = "Working", ["tools"] = "Checking data",
        ["searching"] = "Searching web", ["model"] = "Model activity", ["answer"] = "Receiving reply",
        ["cancelling"] = "Cancelling", ["finishing"] = "Finishing",
    };
    private static readonly List<RequestMetrics> Active = new(2);
    private static RequestMetrics? latestCompleted;
    private static string? cachedReport;

    internal readonly struct TimingScope : IDisposable
    {
        private readonly string stage;
        private readonly long started;
        private readonly RequestMetrics? request;
        internal TimingScope(string stage, RequestMetrics? request = null)
        { this.stage = stage; this.request = request; this.started = Stopwatch.GetTimestamp(); }
        public void Dispose()
        {
            var ms = Stopwatch.GetElapsedTime(this.started).TotalMilliseconds;
            if (this.request is null) Record(this.stage, ms); else this.request.Record(this.stage, ms);
        }
    }

    internal static TimingScope Measure(string stage, RequestMetrics? request = null) => new(stage, request);
    private static bool ValidTime(double value) => double.IsFinite(value) && value >= 0 && value <= 86_400_000;
    private static bool IsRequestStage(string stage) => stage.StartsWith("runtime.", StringComparison.Ordinal) ||
        stage.StartsWith("provider.", StringComparison.Ordinal) || stage.StartsWith("tool.", StringComparison.Ordinal) ||
        stage is "client.question" or "client.first_answer";

    internal static void Record(string stage, double milliseconds)
    {
        if (!Labels.ContainsKey(stage) || IsRequestStage(stage) || !ValidTime(milliseconds)) return;
        lock (Gate)
        {
            if (!Samples.TryGetValue(stage, out var values)) Samples[stage] = values = new();
            if (values.Count == SampleLimit) values.Dequeue();
            values.Enqueue(milliseconds);
            cachedReport = null;
        }
    }

    internal static RequestMetrics BeginQuestion()
    {
        lock (Gate)
        {
            var request = new RequestMetrics();
            if (Active.Count == 2) Active.RemoveAt(0);
            Active.Add(request);
            cachedReport = null;
            return request;
        }
    }

    // Owns only numeric observations and fixed labels. No chat/provider IDs or content.
    internal sealed class RequestMetrics
    {
        private readonly long started = Stopwatch.GetTimestamp();
        private readonly Dictionary<string, (int Count, double Total, double Last)> timings = new(StringComparer.Ordinal);
        private long?[] tokens = new long?[4];
        private long?[] counts = new long?[3];
        private string activity = "starting";
        private long activityStarted = Stopwatch.GetTimestamp();
        private double? elapsed;
        private string outcome = "Working";
        private bool completed;
        private string? requestCachedReport;
        private long reportSecond = -1;

        internal void Record(string stage, double milliseconds)
        {
            if (!Labels.ContainsKey(stage) || !IsRequestStage(stage) || !ValidTime(milliseconds)) return;
            lock (Gate)
            {
                if (this.completed) return;
                var old = this.timings.GetValueOrDefault(stage);
                this.timings[stage] = (Math.Min(old.Count + 1, 1_000_000), Math.Min(old.Total + milliseconds, 86_400_000), milliseconds);
                this.requestCachedReport = null;
                cachedReport = null;
            }
        }

        internal void SetActivity(string value)
        {
            if (!ActivityLabels.ContainsKey(value)) return;
            lock (Gate)
            {
                if (this.completed || this.activity == "cancelling" && value != "cancelling") return;
                this.activity = value;
                this.activityStarted = Stopwatch.GetTimestamp();
                this.requestCachedReport = null;
                cachedReport = null;
            }
        }

        internal string Status
        {
            get
            {
                lock (Gate)
                {
                    var activity = this.activity;
                    // Silence is not evidence of thinking or a continuing search.
                    if (activity is "model" or "searching" && Stopwatch.GetElapsedTime(this.activityStarted).TotalSeconds > 15)
                        activity = "working";
                    var summary = $"{(this.completed ? this.outcome : ActivityLabels[activity])} \u00b7 {Duration(this.elapsed ?? Stopwatch.GetElapsedTime(this.started).TotalMilliseconds)}";
                    if (this.counts[0] is > 0) summary += $" \u00b7 {this.counts[0]} tools";
                    if (this.counts[1] is > 0) summary += $" \u00b7 {this.counts[1]} web";
                    return summary;
                }
            }
        }

        internal string Report
        {
            get
            {
                lock (Gate)
                {
                    var second = (long)((this.elapsed ?? Stopwatch.GetElapsedTime(this.started).TotalMilliseconds) / 1000);
                    if (this.requestCachedReport is not null && this.reportSecond == second) return this.requestCachedReport;
                    var text = new StringBuilder(); this.Append(text);
                    this.reportSecond = second;
                    return this.requestCachedReport = text.ToString();
                }
            }
        }

        internal void Complete(string result)
        {
            lock (Gate)
            {
                if (this.completed) return;
                this.elapsed = Stopwatch.GetElapsedTime(this.started).TotalMilliseconds;
                this.Record("client.question", this.elapsed.Value);
                this.outcome = result is "Completed" or "Cancelled" ? result : "Failed";
                this.completed = true;
                Active.Remove(this);
                latestCompleted = this;
                this.requestCachedReport = null;
                cachedReport = null;
            }
        }

        internal bool Accept(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("stage", out var stageValue) ||
                stageValue.ValueKind != JsonValueKind.String) return false;
            var stage = stageValue.GetString()!;
            var keys = value.EnumerateObject().Select(x => x.Name).ToArray();
            if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Length) return false;
            lock (Gate)
            {
                if (this.completed) return false;
                if (stage is "provider.tokens" or "provider.activity")
                {
                    var allowed = stage == "provider.tokens" ? TokenKeys : ActivityKeys;
                    if (keys.Any(x => x != "stage" && !allowed.Contains(x, StringComparer.Ordinal))) return false;
                    var observed = new long?[allowed.Length];
                    for (var i = 0; i < allowed.Length; i++)
                    {
                        if (!value.TryGetProperty(allowed[i], out var count) || count.ValueKind == JsonValueKind.Null) continue;
                        if (count.ValueKind != JsonValueKind.Number || !count.TryGetInt64(out var number) || number < 0 || number > 1_000_000_000_000) return false;
                        observed[i] = number;
                    }
                    if (stage == "provider.tokens")
                    {
                        if (observed[1] > observed[0] || observed[3] > observed[2]) return false;
                        this.tokens = observed;
                    }
                    else
                    {
                        // Cumulative activity cannot go backwards or erase a previous observation.
                        for (var i = 0; i < observed.Length; i++)
                            if (observed[i] is { } count && (this.counts[i] is null || count >= this.counts[i])) this.counts[i] = count;
                    }
                    this.requestCachedReport = null;
                    cachedReport = null;
                    return true;
                }
                if (stage == "request.activity")
                {
                    if (keys.Length != 2 || !value.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String ||
                        !ActivityLabels.ContainsKey(state.GetString()!)) return false;
                    this.SetActivity(state.GetString()!);
                    return true;
                }
                if (!Labels.ContainsKey(stage) || !IsRequestStage(stage) || keys.Length != 2 ||
                    !value.TryGetProperty("elapsed_ms", out var elapsed) || elapsed.ValueKind != JsonValueKind.Number ||
                    !elapsed.TryGetDouble(out var ms) || !ValidTime(ms)) return false;
                this.Record(stage, ms);
                return true;
            }
        }

        private void Append(StringBuilder text)
        {
            text.AppendLine(this.Status);
            foreach (var (stage, label) in Labels)
                if (this.timings.TryGetValue(stage, out var value))
                    text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                        $"{label}: {value.Total:0.0} ms total ({value.Count} observations; last {value.Last:0.0} ms)"));
            text.AppendLine($"Input: {Count(this.tokens[0])}; cached input: {Count(this.tokens[1])}");
            text.AppendLine($"Output: {Count(this.tokens[2])}; reasoning: {Count(this.tokens[3])}");
            text.AppendLine($"Observed tool calls: {Count(this.counts[0])}; web-search items: {Count(this.counts[1])}; reasoning items: {Count(this.counts[2])}");
            text.AppendLine("Tool calls include tool discovery. Counts stop at 1,024 unique events per category. Observed reasoning items are not model API calls; API-call count is unavailable.");
        }

        internal void ClearMeasurements()
        {
            lock (Gate)
            {
                this.timings.Clear(); this.tokens = new long?[4]; this.counts = new long?[3];
                this.requestCachedReport = null;
            }
        }
    }

    internal static void Clear()
    {
        lock (Gate)
        {
            Samples.Clear(); latestCompleted = null;
            foreach (var request in Active) request.ClearMeasurements();
            cachedReport = null;
        }
    }

    internal static string Report
    {
        get
        {
            lock (Gate)
            {
                if (Active.Count == 0 && cachedReport is not null) return cachedReport;
                var text = new StringBuilder();
                for (var i = 0; i < Active.Count; i++)
                { text.AppendLine($"Active request {i + 1}:"); text.AppendLine(Active[i].Report); }
                text.AppendLine("Latest completed request:");
                if (latestCompleted is { } completed) text.AppendLine(completed.Report);
                else text.AppendLine("None recorded. Input: unknown; output: unknown.");
                text.AppendLine("App-session measurements (separate from requests; up to 32 samples per stage):");
                foreach (var (stage, label) in Labels)
                    if (Samples.TryGetValue(stage, out var values) && values.Count > 0)
                        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                            $"{label}: last {values.Last():0.0} ms; mean {values.Average():0.0} ms ({values.Count} samples)"));
                text.Append("Times can overlap; do not add stages. Cached input is part of input; reasoning is part of output. Unknown is not zero. Counters do not convert to subscription percentages.\nSession only; no prompt text or account/character identifiers recorded.");
                var report = text.ToString();
                if (Active.Count == 0) cachedReport = report;
                return report;
            }
        }
    }

    private static string Duration(double milliseconds)
    {
        var seconds = Math.Max(0, (long)(milliseconds / 1000));
        return $"{seconds / 60}:{seconds % 60:00}";
    }
    private static string Count(long? value) => value?.ToString("N0", CultureInfo.InvariantCulture) ?? "unknown";
}
