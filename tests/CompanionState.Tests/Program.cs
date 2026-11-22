using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CompAI;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Bounded bridge frames preserve line endings, EOF and UTF-16 size boundaries", CompanionLineReaderChecks.Boundaries),
    ("Bounded bridge frames reject endless input and preserve efficient read-ahead", CompanionLineReaderChecks.BoundedReadAhead),
    ("Bounded bridge frames observe cancellation during buffered and pending reads", CompanionLineReaderChecks.Cancellation),
    ("Bounded bridge frames preserve process cleanup, draft recovery and explicit retry", CompanionLineReaderChecks.ProcessLifecycle),
    ("Response limits reach the launched process and preserve account operation deadlines", ResponseTimeoutChecks.LauncherContract),
    ("Response limits freeze before asynchronous preparation and changes apply only to later questions", ResponseTimeoutChecks.FrozenBeforePreparation),
    ("Response limits reject malformed requests or invalid settings before preparing or launching", ResponseTimeoutChecks.InvalidInputs),
    ("Attachments remain immutable per-chat through overlap, cancel, retry, removal and unload", AttachmentConnectionChecks.Ownership),
    ("Embedded Python prerequisites reject tampering and report bounded sanitized startup failures", PythonRuntimeChecks.Run),
    ("Actual embedded Python validates and starts with a relocated isolated standard library", PythonRuntimeChecks.ActualBundle),
    ("Numeric diagnostics survive cancellation without changing chat or account state", TestCancelledDiagnostics),
    ("Request diagnostics isolate parallel owners and reject replaced-operation telemetry", DiagnosticConnectionChecks.Correlation),
    ("Storage debounce, final flush and capacity guard stay local", ConnectionStorageChecks.Run),
    ("Login URLs reject credentials, nonstandard ports and control characters", TestLoginUrls),
    ("Provider switching preserves chats and disabled providers never launch account or question operations", TestProviderAccounts),
    ("Grok provider status, login, signed-out credential recovery and chat ownership", TestGrokProviderAccounts),
    ("Local Grok sign-out stays separate from runtime readiness and rejects unrelated frames", TestGrokLocalLogout),
    ("Streaming preview and game-chat completion lifecycle", TestStreamingReplies),
    ("Plugin-window requests stay local after command replies, including cancellation and failure", TestPluginWindowReplies),
    ("Game chat output preserves Unicode and URLs without control payloads", TestGameChatFormat),
    ("Answer speaker snapshots survive personality switches before game-chat delivery", TestAnswerSpeaker),
    ("ChatHistory bounds, excerpts, source preservation, and labels", TestHistory),
    ("Login and status restore authorization without clearing chat", TestLoginAndStatus),
    ("Model discovery is bounded, refreshed and independent of authentication", TestModelDiscovery),
    ("Answers retain their model and reasoning across settings and reload", TestAnswerModelLabels),
    ("Chat selection is independent of new-chat defaults and locked while busy", TestPerChatSelection),
    ("Missing login status fails closed", TestMissingLoginStatus),
    ("Ask is refused while busy or unauthorized", TestAskRefusal),
    ("Only exit-zero completed answers enter history", TestAskOutcomes),
    ("Failure categories preserve verified accounts except authentication", TestFailureCategories),
    ("Recovered account status separates prior request failure and cancellation from readiness", TestRequestStatusRecovery),
    ("Invalid model answers allow explicit resend without an account refresh", TestInvalidAnswerRecovery),
    ("Lookup limits preserve sign-in and allow a narrowed explicit retry", TestLookupLimitRecovery),
    ("Local reader page budget preserves sign-in without accepting oversized results", TestLocalReaderPageBudget),
    ("Reader unavailability recovers while permission and protocol failures stop", TestReaderFailures),
    ("Quota failure without a reset deadline schedules only a bounded status refresh", TestQuotaFailureRefresh),
    ("New chat preserves prior tab and selection restores context", TestNewChat),
    ("Game invalidation preserves completed inventory history", TestInventoryInvalidation),
    ("Game invalidation cancels quest-only pending questions", TestQuestInvalidation),
    ("Private quest queries require permission and return bounded results", TestQuestLookupBridge),
    ("Quest query replay and oversized replies fail closed", TestQuestLookupRejection),
    ("Cancellation prevents a computed quest result from being sent", TestQuestLookupCancellation),
    ("Question continuation guards expire on cancel and cannot revive in the next operation", TestQuestionContinuation),
    ("Signout preserves chats and reload restores saved history", TestSignoutAndReload),
    ("Quota reset refreshes authorization without sending a question", TestQuotaReset),
    ("Inventory and quest tools share sequence and permission gates", TestMixedLookupBridge),
    ("Inventory find keys stay callback-bound, owner-bound and unusable after cancellation", TestInventoryFindKey),
    ("Shop find keys stay callback-bound, owner-bound and unusable after cancellation", TestShopFindKey),
    ("Send clears drafts immediately and preserves recovery or newly typed text", TestDraftSendLifecycle),
    ("Manual retry retains per-chat failed questions without replaying requests or drafts", TestManualRetry),
    ("Missing companion files explain repair and preserve manual retry", TestMissingCompanionFiles),
    ("Chat notes are per-chat and locked during requests", TestChatNotes),
    ("Personality snapshots stay per-chat and cannot change during requests", TestChatPersonality),
    ("Invalid personality snapshots block sends until explicitly repaired", TestInvalidChatPersonality),
    ("Failed personality replacements keep the previous selection sendable", TestPersonalityReplacementFailure),
    ("Preset routing preserves chat content, refuses busy changes, and survives failed saves", TestPresetRouting),
    ("Older history lookup respects its permission and shared sequence", TestHistoryLookupBridge),
    ("Game context lookup respects permission and shared sequence", TestGameContextLookupBridge),
    ("Currency lookup shares sequence and refuses a missing permission callback", TestCurrencyLookupBridge),
    ("Currency-only questions cancel on game invalidation", TestCurrencyInvalidation),
    ("Additional readers share dispatch limits, fail closed without callbacks, and retain invalidation boundaries", TestAdditionalReaderBridge),
    ("Private valuation transport stays separate from ordinary budgets and rejects scope or sequence changes", TestValuationBridge),
    ("Allowance display separates remaining quota, reset and observation age", TestAllowanceDisplay),
    ("Parallel requests overlap, preserve owners and enforce the two-chat limit", ParallelConnectionChecks.Ownership),
    ("Parallel cancellation, revocation, callbacks and unload remain owner safe", ParallelConnectionChecks.Cancellation),
    ("Parallel authentication failure cancels siblings and cannot restore sign-in", ParallelConnectionChecks.Authentication),
    ("Parallel sibling outcomes cannot clear protocol or permission refresh requirements", ParallelConnectionChecks.FailClosed),
};
var failures = 0;
foreach (var (name, run) in tests.Where(test => args.Length == 0 || args.Any(filter => test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))))
{
    try { await run(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
return failures == 0 ? 0 : 1;

static async Task TestValuationBridge()
{
    async Task Run(string mode, bool success, int expectedValuation, int expectedOrdinary,
        bool provideCallback = true, bool oversized = false)
    {
        using var fixture = new Fixture();
        using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
        await Authorize(connection, fixture);
        fixture.Scenario("ask_valuation");
        var valuationCalls = 0; var ordinaryCalls = 0;
        var request = JsonSerializer.Serialize(new { mode });
        Check(connection.StartAsk(request, false, "Synthetic valuation", includeQuests: true,
            questQuery: _ => { ordinaryCalls++; return "{}"; }, valuationQuery: !provideCallback ? null : argument =>
            {
                valuationCalls++;
                var candidates = argument.GetProperty("view").GetString() == "candidates";
                var size = oversized ? ValuationLookup.MaxCandidateBytes + 1 : candidates ? 350000 : 60000;
                return JsonSerializer.Serialize(new { rows = new string('x', size) });
            }), "valuation synthetic request starts");
        await WaitIdle(connection);
        Check(valuationCalls == expectedValuation && ordinaryCalls == expectedOrdinary,
            $"Callbacks escaped dispatch bounds ({mode}: {valuationCalls}/{ordinaryCalls}).");
        Check(success ? connection.View.History.Count == 1 && connection.View.CanTest : connection.View.History.Count == 0 && !connection.View.CanTest,
            $"Valuation protocol outcome incorrect ({mode}).");
    }
    await Run("success", true, 2, 12);
    await Run("third", false, 2, 0);
    await Run("scope", false, 1, 0);
    await Run("details_first", false, 0, 0);
    await Run("ordinary_excess", false, 2, 12);
    await Run("success", false, 0, 0, provideCallback: false);
    await Run("success", false, 1, 0, oversized: true);
}

static async Task TestCancelledDiagnostics()
{
    using var fixture = new Fixture();
    using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
    await Authorize(connection, fixture);
    fixture.Scenario("ask_cancel");
    Check(connection.StartAsk("{}", false, "cancel diagnostics"), "diagnostic request starts");
    await WaitUntil(() => File.Exists(fixture.CancelReadyFile), "diagnostic request receives input");
    connection.Cancel();
    PerformanceDiagnostics.Clear();
    var before = connection.View;
    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
    var operation = typeof(CompanionConnection).GetProperty("ActiveQuestion", flags)!.GetValue(connection)!;
    var apply = typeof(CompanionConnection).GetMethod("ApplyMessage", flags)!;
    apply.Invoke(connection, new object[] { operation, "{\"performance\":{\"stage\":\"provider.turn\",\"elapsed_ms\":45}}" });
    apply.Invoke(connection, new object[] { operation, "{\"performance\":{\"stage\":\"provider.tokens\",\"input_tokens\":12}}" });
    apply.Invoke(connection, new object[] { operation, "{\"question_answer\":\"Cancelled output must not be saved\"}" });
    Check(PerformanceDiagnostics.Report.Contains("last 45.0 ms") && PerformanceDiagnostics.Report.Contains("Input: 12"),
        "final numeric diagnostics remain available after cancellation");
    Check(connection.View.Account == before.Account && connection.View.IsSignedIn == before.IsSignedIn &&
        connection.View.History.Count == before.History.Count && connection.View.QuestionAnswer != "Cancelled output must not be saved",
        "diagnostics do not revive or authorize the cancelled operation");
    await WaitIdle(connection);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static async Task TestProviderAccounts()
{
    Check(CompanionConnection.IsAllowedLoginUrl("https://github.com/login/device", "copilot") &&
          CompanionConnection.IsAllowedLoginUrl("https://github.com/login/oauth/authorize?state=fixture", "copilot") &&
          CompanionConnection.IsAllowedLoginUrl("https://accounts.x.ai/oauth2/device", "grok"), "exact official device/OAuth pages allowed");
    foreach (var url in new[] { "https://github.com/anything", "https://github.com/login/oauth/other", "https://github.com/login/device/other",
                 "https://github.com.evil.example/login/device", "https://user@github.com/login/device", "https://github.com:444/login/device",
                 "http://github.com/login/device", "https://@github.com/login/device", "https://accounts.x.ai/oauth2/device" })
        Check(!CompanionConnection.IsAllowedLoginUrl(url, "copilot"), "unexpected Copilot login destination rejected");
    Check(!CompanionConnection.IsAllowedLoginUrl("https://accounts.x.ai/anything", "grok") &&
          !CompanionConnection.IsAllowedLoginUrl("https://auth.openai.com/authorize", "grok") &&
          !CompanionConnection.IsAllowedLoginUrl("https://github.com/login/device"), "login hosts remain provider scoped");

    foreach (var provider in new[] { "copilot" })
    {
        using var fixture = new Fixture();
        var prepareCount = 0;
        using var connection = new CompanionConnection(() => new("auto", "none", provider),
            preparePython: (directory, cancellation) =>
            {
                ++prepareCount;
                return Fixture.PreparePython(directory, cancellation);
            });
        foreach (var action in new[] { "status", "login", "logout", "test" }) connection.Start(action);
        Check(!connection.View.Busy && !connection.View.CanTest && connection.CurrentProvider == provider &&
              connection.ChatModelSelection?.Provider == provider &&
              connection.View.SendBlockReason?.Contains("integration remains disabled") == true &&
              !connection.StartAsk(JsonSerializer.Serialize(new { provider }), false, "blocked") &&
              !connection.SetProvider("copilot") &&
              prepareCount == 0 && !File.Exists(fixture.ProviderLogFile),
            "saved experimental selection is preserved but no account action or question prepares Python or launches a process");
        Check(connection.SyntheticTestBlockReason?.Contains("Codex only") == true,
            "Codex-specific proof stays unavailable on blocked providers");

        fixture.Scenario("provider_hold_status");
        Check(connection.SetProvider("codex") && connection.CurrentProvider == "codex" && connection.View.Busy &&
              !connection.View.IsSignedIn && connection.ModelOptions.Count == 0,
            "explicit switch to supported Codex refreshes account without changing saved chat model");
        fixture.Release(); await WaitIdle(connection);
        Check(connection.View.IsSignedIn && connection.View.Account.Contains("ChatGPT") &&
              connection.ChatModelSelection?.Provider == provider &&
              connection.View.SendBlockReason?.Contains(CompanionConnection.ProviderLabel(provider)) == true &&
              !connection.StartAsk("{}", false, "wrong saved chat"),
            "switch never silently rewrites the old provider or sends through Codex");
        Check(connection.SetChatModelSelection(new("gpt-6-sol", "medium", "codex")) &&
              connection.View.SendBlockReason is null,
            "explicit model selection permits future Codex replies");
        Check(!connection.StartAsk(JsonSerializer.Serialize(new { provider }), false, "wrong request provider"),
            "wire provider cannot select a disabled runtime through Codex");
        fixture.Scenario("ask_success");
        Check(connection.StartAsk("{}", false, "Codex question"), "normal Codex question remains available");
        await WaitIdle(connection);
        Check(connection.View.History.Count == 1 && File.ReadAllLines(fixture.ProviderLogFile).All(value => value == "codex") &&
              !connection.SetProvider("other") && connection.CurrentProvider == "codex",
            "only explicit Codex operations launch and history is retained");
    }
}

static async Task TestGrokLocalLogout()
{
    using var fixture = new Fixture();
    using var connection = new CompanionConnection(() => new("auto", "none", "grok"), preparePython: Fixture.PreparePython);
    foreach (var scenario in new[] { "local_logout_ok", "local_logout_false", "local_logout_extra",
        "local_logout_duplicate", "local_logout_late_status", "local_logout_wrong_provider", "local_logout_failure_exit", "local_logout_status_only" })
    {
        fixture.Scenario(scenario);
        connection.Start("logout");
        await WaitIdle(connection);
        var success = scenario == "local_logout_ok";
        Check(connection.View.State.Contains("cleared locally") == success, "only a completed exact local logout reports success: " + scenario);
        Check(!connection.View.IsSignedIn && !connection.View.CanTest && connection.ModelOptions.Count == 0,
            "local sign-out cannot grant request readiness: " + scenario);
        Check(!connection.View.State.Contains("pin verified"), "local removal makes no runtime integrity claim");
    }
    fixture.Scenario("local_logout_ok");
    connection.Start("status");
    await WaitIdle(connection);
    Check(!connection.View.State.Contains("cleared locally") && !connection.View.CanTest,
        "status operations cannot accept local logout frames");
    fixture.Scenario("provider_status");
    connection.Start("status");
    await WaitIdle(connection);
    Check(connection.View.IsSignedIn && connection.View.CanTest, "later explicit verified status can restore readiness");
}

static async Task TestGrokProviderAccounts()
{
    using var fixture = new Fixture();
    using var connection = new CompanionConnection(() => new("auto", "none", "grok"),
        preparePython: Fixture.PreparePython, utcNow: () => DateTimeOffset.FromUnixTimeSeconds(2000000000));
    void CheckReportedAllowance(string observation)
    {
        Check(connection.AllowanceSummary == "Left: Primary 99%" &&
              connection.View.Quota.Contains("primary: 99% remaining") &&
              connection.AllowanceDetails.Contains("Primary: 99% remaining; resets in 1h") &&
              connection.AllowanceDetails.Contains(observation),
            "Grok's single float-valued allowance window retains its percentage, reset and observation: " + observation);
    }
    Check(connection.CurrentProvider == "grok", "new-chat default selects Grok account explicitly");
    Check(connection.ChatModelSelection?.Provider == "grok",
        $"new chat saves its Grok selection (model: {connection.ChatModelSelection}; error: {connection.ChatModelError}; storage: {connection.View.StorageError})");
    fixture.Scenario("logout");
    connection.Start("logout"); await WaitIdle(connection);
    Check(!connection.View.IsSignedIn && File.ReadAllLines(fixture.ActionLogFile).SequenceEqual(["logout"]),
        "encrypted sign-in can be cleared before any successful account status");
    fixture.Scenario("provider_status");
    connection.Start("status"); await WaitIdle(connection);
    Check(connection.View.IsSignedIn && connection.View.CanTest && connection.View.Account.Contains("Grok") &&
          connection.ModelOptions.Single().Provider == "grok" && connection.ModelOptions.Single().Efforts.Single() == "none",
        "verified Grok status discovers only its own models");
    CheckReportedAllowance("Account status snapshot");
    fixture.Scenario("provider_hold_status");
    connection.Start("status");
    Check(connection.View.Busy && connection.AllowanceSummary == "Left: —",
        "an explicit Grok refresh clears the prior allowance while checking");
    fixture.Release(); await WaitIdle(connection);
    Check(connection.View.IsSignedIn && connection.View.CanTest, "Grok refresh restores request readiness");
    CheckReportedAllowance("Account status snapshot");
    Check(!connection.StartAsk("{}", false, "missing provider") &&
          !connection.StartAsk("{\"request\":{\"version\":21,\"provider\":\"codex\"}}", false, "wrong provider") &&
          !connection.StartAsk("{\"provider\":\"copilot\",\"provider\":\"grok\"}", false, "duplicate provider"),
        "Grok sends require an exact unambiguous wire provider");
    fixture.Scenario("ask_success");
    Check(connection.StartAsk("{\"request\":{\"version\":21,\"provider\":\"grok\"}}", false, "Grok question"), "matching Grok question starts");
    Check(!connection.SetProvider("codex"), "provider cannot change during a question");
    await WaitIdle(connection);
    Check(connection.View.History.Count == 1 && File.ReadAllLines(fixture.ProviderLogFile).All(value => value == "grok"),
        "operations freeze provider and preserve completed answer");
    CheckReportedAllowance("Before the last reply");
    fixture.Scenario("provider_hold_status");
    Check(connection.SetProvider("codex") && connection.View.Busy && !connection.View.IsSignedIn && connection.ModelOptions.Count == 0,
        "switch clears old account authorization and catalog");
    fixture.Release(); await WaitIdle(connection);
    Check(connection.ChatModelSelection?.Provider == "grok", "saved chat retains Grok after switching accounts");
    Check(connection.View.SendBlockReason?.Contains("Grok") == true, "provider mismatch explains the saved Grok chat");
    Check(!connection.StartAsk("{}", false, "wrong saved chat"), "saved chat never silently switches providers");
    Check(connection.SetChatModelSelection(new("gpt-6-sol", "medium", "codex")), "explicit Codex model selection accepted");
    fixture.Scenario("provider_status");
    Check(connection.SetProvider("grok"), "explicit account switch back to Grok accepted"); await WaitIdle(connection);
    Check(connection.SetChatModelSelection(new("auto", "none", "grok")) && connection.View.History.Count == 1,
        "explicit Grok model selection keeps history");
    foreach (var scenario in new[] { "provider_missing_tag", "provider_wrong_tag", "provider_wrong_account", "provider_duplicate_tag" })
    {
        fixture.Scenario(scenario); connection.Start("status"); await WaitIdle(connection);
        Check(!connection.View.IsSignedIn && !connection.View.CanTest && connection.ModelOptions.Count == 0,
            "invalid status provider/account identity fails closed");
    }
    foreach (var nativeBrowser in new[] { false, true })
    {
        var beforeOpen = Dalamud.Utility.Util.OpenedLinks.Count;
        fixture.Scenario(nativeBrowser ? "provider_device_login_native" : "provider_device_login"); connection.Start("login");
        await WaitUntil(() => connection.LoginUserCode.Length > 0 || !connection.View.Busy, "device login code");
        Check(connection.View.Busy && connection.LoginUserCode == "ABCD-EFGH" &&
              connection.View.LoginUrl == "https://accounts.x.ai/oauth2/device", "device login retains scoped manual URL and code");
        Check(connection.View.ConnectionError is null, "device sign-in details stay out of error copying");
        connection.OpenPendingLoginUrl(); connection.OpenPendingLoginUrl();
        Check(Dalamud.Utility.Util.OpenedLinks.Count == beforeOpen + (nativeBrowser ? 0 : 1),
            "explicit browser choice is respected; the default-browser link opens only once");
        fixture.Release(); await WaitIdle(connection);
        Check(connection.LoginUserCode == "" && connection.View.LoginUrl == "" && connection.View.IsSignedIn,
            "completed login clears temporary device fields");
    }
    fixture.Scenario("provider_device_login_bad_browser"); connection.Start("login"); await WaitIdle(connection);
    Check(!connection.View.IsSignedIn && connection.View.LoginUrl == "", "invalid browser choice fails closed");
    fixture.Scenario("logout"); connection.Start("logout"); await WaitIdle(connection);
    var actionCount = File.ReadAllLines(fixture.ActionLogFile).Length;
    connection.Start("logout"); await WaitIdle(connection);
    Check(!connection.View.IsSignedIn && File.ReadAllLines(fixture.ActionLogFile).Length == actionCount + 1 &&
          connection.View.History.Count == 1, "signed-out credential recovery remains available without deleting chats");
}

static Task TestLoginUrls()
{
    foreach (var host in new[] { "auth.openai.com", "auth.chatgpt.com", "chatgpt.com" })
    {
        Check(CompanionConnection.IsAllowedLoginUrl($"https://{host}/authorize?state=example"), "official login rejected");
        Check(CompanionConnection.IsAllowedLoginUrl($"https://{host}:443/authorize"), "standard TLS port rejected");
    }
    foreach (var url in new[]
    {
        "https://user@auth.openai.com/authorize", "https://user:password@auth.openai.com/authorize", "https://@auth.openai.com/authorize",
        "https://auth.openai.com:8443/authorize", "http://auth.openai.com/authorize",
        "https://auth.openai.com.attacker.example/authorize", "https://attacker.example/authorize",
        "\nhttps://auth.openai.com/authorize", "https://auth.openai.com/author\tize",
        "https://auth.openai.com/authorize\u007f", " https://auth.openai.com/authorize", "https://auth.openai.com/authorize ",
        "https://auth.openai.com/authorize?state=a b", "https://auth.openai.com/author\\ize", "https://auth.openai.com/" + new string('a', 8192),
    }) Check(!CompanionConnection.IsAllowedLoginUrl(url), "unsafe login URL accepted");
    return Task.CompletedTask;
}

static Task TestHistory()
{
    var history = new ChatHistory();
    const string oldUrl = "https://na.finalfantasyxiv.com/old";
    history.Add("q0", "a0", [new QuestionSource("old", oldUrl)], null, true);
    for (var i = 1; i <= 21; i++) history.Add("q" + i, "a" + i, [], null, false);
    Check(history.View.Count == 22 && history.View[0].Question == "q0", "all stored turns should stay readable");
    Check(history.ContainsSource(oldUrl), "source from a stored older turn should remain available");
    Check(history.ContainsInventory, "inventory ancestry should remain until clear");
    Check(history.Context().Length == 22 && history.Context()[0].Question == "q0",
        "short complete exchanges should fit without a four-turn cutoff");
    history.Add("scope", "answer", [], "Scope: synthetic only", false);
    Check(history.Context()[^1].Answer == "answer\n\nScope: synthetic only", "scope label should be transmitted with answer");
    for (var i = 22; i < 1002; i++) history.Add("q" + i, "a" + i, [], null, false);
    Check(history.ContainsSource(oldUrl) && history.ContainsInventory &&
          history.Export().Length > ChatHistory.MaxStoredTurns,
        "in-memory overflow awaiting preservation must retain sources and inventory ancestry");
    history.Clear();
    Check(!history.ContainsInventory && history.View.Count == 0, "clear should remove inventory ancestry and visible turns");
    for (var i = 0; i < 4; i++) history.Add("q" + i, new string('x', 7000), [], null, false);
    var context = history.Context();
    Check(context.Length == 3 && context[0].Question == "q1" && context[^1].Question == "q3",
        "24,000 serialized byte budget should retain three whole newest pairs");
    history.Add("too long", new string('x', 8001), [], null, false);
    Check(history.Context().Length > 0 && history.Context()[^1].Answer.Length == 8001, "an 8001-character answer can fit whole");
    history.Add("budget overflow", new string('x', 24001), [], null, false);
    Check(history.Context().Length == 1 && history.Context()[0].IsExcerpt &&
          history.ContextStats.OmissionReason == "latest_exchange_excerpt",
        "an entry beyond the actual budget must retain a visibly labelled excerpt");
    return Task.CompletedTask;
}

static async Task TestLoginAndStatus()
{
    using var fixture = new Fixture();
    using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
    fixture.Scenario("login_status");
    connection.Start("login");
    await WaitIdle(connection);
    Check(connection.View.CanTest && connection.View.IsSignedIn && !connection.View.Busy,
        "login should clear Busy and restore CanTest after verified status");
    await Ask(connection, fixture, "first", false);
    Check(connection.View.History.Count == 1, "ask should create one history turn");
    fixture.Scenario("login_status");
    connection.Start("login");
    await WaitIdle(connection);
    Check(connection.View.CanTest && connection.View.History.Count == 1, "login should preserve chat and authorization");
    fixture.Scenario("status");
    connection.Start("status");
    await WaitIdle(connection);
    Check(connection.View.CanTest && connection.View.History.Count == 1, "status should preserve chat and authorization");
}

static async Task TestMissingLoginStatus()
{
    using var fixture = new Fixture();
    using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
    fixture.Scenario("login_no_status");
    connection.Start("login");
    await WaitIdle(connection);
    Check(!connection.View.Busy && !connection.View.CanTest && !connection.View.IsSignedIn,
        "login with no status must fail closed");
    Check(!connection.StartAsk("{}", false, "forbidden"), "ask should reject absent authorization");
}

static async Task TestModelDiscovery()
{
    using var fixture = new Fixture();
    using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
    await Authorize(connection, fixture);
    var previous = connection.ModelOptions;
    Check(previous.Count == 2 && previous[1].Id == "gpt-6-luna" &&
        previous[1].Efforts.SequenceEqual(["low", "medium", "high"]), "status must expose exact choices");
    foreach (var scenario in new[] { "status_no_models", "status_bad_models", "status_duplicate_models", "status_unknown_effort", "status_invalid_id" })
    {
        fixture.Scenario(scenario);
        connection.Start("status");
        await WaitIdle(connection);
        Check(connection.View.CanTest && connection.View.IsSignedIn && connection.ModelOptions.Count == 0 &&
            connection.ModelDiscoveryError is not null, "invalid discovery must block selection without signing out: " + scenario);
    }
    Check(previous.Count == 2, "previous model view must remain immutable");
    await Authorize(connection, fixture);
    fixture.Scenario("logout");
    connection.Start("logout");
    await WaitIdle(connection);
    Check(connection.ModelOptions.Count == 0 && !connection.View.IsSignedIn, "signout must clear discovered choices");
}

static async Task TestAnswerModelLabels()
{
    using var fixture = new Fixture();
    using (var connection = new CompanionConnection(preparePython: Fixture.PreparePython))
    {
        await Authorize(connection, fixture);
        fixture.Scenario("ask_model");
        Check(connection.StartAsk("{\"model\":\"gpt-6-luna\",\"reasoning_effort\":\"low\"}", false, "first choice"), "ask must start");
        await WaitIdle(connection);
        fixture.Scenario("ask_model");
        Check(connection.StartAsk("{\"model\":\"gpt-6-sol\",\"reasoning_effort\":\"high\"}", false, "second choice"), "second ask must start");
        await WaitIdle(connection);
        var context = connection.HistoryContext.Exchanges;
        Check(context.Length == 2 && context[0].Answer.Contains("Model: gpt-6-luna; reasoning: low.") &&
            context[1].Answer.Contains("Model: gpt-6-sol; reasoning: high."), "each answer must retain its own selection");
    }
    using var reloaded = new CompanionConnection(preparePython: Fixture.PreparePython);
    Check(reloaded.HistoryContext.Exchanges[0].Answer.Contains("Model: gpt-6-luna; reasoning: low."),
        "saved answer model metadata must survive reload");
}

static async Task TestPerChatSelection()
{
    using var fixture = new Fixture();
    var defaults = new CompanionModelSettings("gpt-6-sol", "medium");
    string firstId;
    using (var connection = new CompanionConnection(() => defaults, preparePython: Fixture.PreparePython))
    {
        await Authorize(connection, fixture);
        firstId = connection.View.ActiveChatId;
        Check(connection.ChatModelSelection == defaults, "first chat must snapshot defaults");
        Check(connection.SetChatModelSelection(new("gpt-6-luna", "low")), "save first chat override");
        defaults = new("gpt-6-sol", "high");
        Check(connection.ChatModelSelection == new CompanionModelSettings("gpt-6-luna", "low"),
            "changing defaults must not affect current chat");
        Check(connection.NewChat() && connection.ChatModelSelection == defaults, "new chat must snapshot new defaults");
        Check(connection.SelectChat(firstId) && connection.ChatModelSelection == new CompanionModelSettings("gpt-6-luna", "low"),
            "switching chat must restore its selection");
        fixture.Scenario("hold_status");
        connection.Start("status");
        Check(!connection.SetChatModelSelection(defaults), "cannot change chat model during an action");
        fixture.Release();
        await WaitIdle(connection);
        Check(!connection.SetChatModelSelection(new("unknown", "medium")), "undiscovered model must be refused");
    }
    defaults = new("gpt-6-sol", "low");
    using var reloaded = new CompanionConnection(() => defaults, preparePython: Fixture.PreparePython);
    Check(reloaded.View.ActiveChatId == firstId && reloaded.ChatModelSelection == new CompanionModelSettings("gpt-6-luna", "low"),
        "reload with a new default must retain the saved chat selection");
}

static async Task TestAskRefusal()
{
    using var fixture = new Fixture();
    using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
    Check(!connection.StartAsk("{}", false, "unauthorized"), "initial ask should be refused");
    fixture.Scenario("hold_status");
    connection.Start("status");
    Check(connection.View.Busy && !connection.StartAsk("{}", false, "busy"), "ask should be refused while status is busy");
    fixture.Release();
    await WaitIdle(connection);
    Check(connection.View.CanTest && connection.View.History.Count == 0, "status should complete without a chat turn");
}

static async Task TestAskOutcomes()
{
    using var fixture = new Fixture();
    using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
    await Authorize(connection, fixture);
    await Ask(connection, fixture, "successful", false);
    Check(connection.View.History.Count == 1 && connection.View.History[0].Question == "successful",
        "successful answer should enter history");
    Check(File.ReadAllText(fixture.RequestFile).Contains("request-marker", StringComparison.Ordinal),
        "fake process should receive the exact request line");
    fixture.Scenario("ask_fail");
    Check(connection.StartAsk("{\"marker\":\"request-marker\"}", false, "failed"), "authorized failed ask should start");
    await WaitIdle(connection);
    Check(connection.View.History.Count == 1, "nonzero exit should not enter history even after an answer line");
    await Authorize(connection, fixture);
    fixture.Scenario("ask_cancel");
    Check(connection.StartAsk("{\"marker\":\"request-marker\"}", false, "cancelled"), "cancelled ask should start");
    await WaitUntil(() => File.Exists(fixture.CancelReadyFile), "fake ask did not receive request");
    connection.Cancel();
    await WaitIdle(connection);
    Check(connection.View.History.Count == 1, "cancelled ask should not enter history");
}

static async Task TestFailureCategories()
{
    using var fixture = new Fixture();
    using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
    foreach (var kind in new[] { "reader", "provider", "quota", "permission", "protocol", "authentication" })
    {
        await Authorize(connection, fixture);
        var priorCount = connection.View.History.Count;
        fixture.Scenario("ask_error_" + kind);
        Check(connection.StartAsk("{}", false, "failed " + kind), "failure fixture must start");
        await WaitIdle(connection);
        Check(connection.View.History.Count == priorCount && connection.View.QuestionAnswer == "No answer",
            "an error followed by an answer and exit zero must not save the answer");
        Check(connection.View.IsSignedIn == (kind != "authentication"), "only authentication failure invalidates identity");
        Check(connection.View.CanTest == (kind is "reader" or "provider"), "send readiness must follow the failure category");
        Check(connection.ModelOptions.Count == (kind == "authentication" ? 0 : 2), "non-auth failures must retain known models");
        if (kind is "reader" or "provider")
        {
            await Ask(connection, fixture, "explicit retry " + kind, false);
            Check(connection.View.History.Count == priorCount + 1, "explicit user retry should recover without another login");
        }
    }
}

static async Task TestRequestStatusRecovery()
{
    using var fixture = new Fixture();
    using var connection = new CompanionConnection(() => new("auto", "none", "grok"), preparePython: Fixture.PreparePython);
    const string request = "{\"provider\":\"grok\"}";
    await Authorize(connection, fixture);
    fixture.Scenario("ask_error_protocol");
    Check(connection.StartAsk(request, false, "Keep the failed Grok question"), "Grok failure starts");
    await WaitIdle(connection);
    var failure = connection.View.RetryState;
    var owner = connection.View.ActiveChatId;
    Check(connection.View.ConnectionError == "Companion refused: [provider.request] Sanitized fixture failure" &&
          connection.View.ConnectionError == failure && connection.View.PreviousRequestError is null,
        "copy uses only the current displayed failure, excluding question, account and allowance");
    Check(!connection.View.CanTest && connection.View.StatusSummary(true) == "Request failed — click for details" &&
          failure.Contains("Sanitized fixture failure"), "current request failure retains its own status");

    fixture.Scenario("provider_wrong_tag");
    connection.Start("status"); await WaitIdle(connection);
    Check(connection.View.ConnectionError == connection.View.State && connection.View.PreviousRequestError == failure,
        "a failed refresh exposes its own error and the previous request error independently");
    Check(!connection.View.CanTest && !connection.View.StatusSummary(false).StartsWith("Ready") &&
          connection.View.RetryState == failure && connection.View.PreviousRequestDetail.Contains(failure),
        "failed refresh cannot report ready or erase the original request error");
    fixture.Scenario("provider_hold_status");
    connection.Start("status");
    Check(connection.View.ConnectionError is null && connection.View.PreviousRequestError == failure,
        "checking account status has no copyable connection error");
    Check(connection.View.Busy && !connection.View.StatusSummary(false).StartsWith("Ready"),
        "pending refresh cannot report recovered readiness");
    fixture.Release(); await WaitIdle(connection);
    Check(connection.View.ConnectionError is null && connection.View.PreviousRequestError == failure,
        "copying the previous failure after refresh cannot include the new account status");
    Check(connection.View.CanTest && connection.View.State == "Connected (runtime pin verified)" &&
          connection.View.StatusSummary(false) == "Ready — retry available" &&
          connection.View.StatusSummary(true) == "Attention — click for details",
        "verified refresh presents readiness while other current notices retain priority");
    Check(connection.View.PreviousRequestDetail == "Previous request: " + failure &&
          connection.View.RetryQuestion == "Keep the failed Grok question" && connection.View.History.Count == 0,
        "refresh retains failed-question text and diagnostic without inventing a completed answer");
    Check(File.ReadAllLines(fixture.ActionLogFile).Count(action => action == "ask") == 1,
        "account recovery never automatically retries the question");
    Check(connection.NewChat() && connection.View.StatusSummary(false) == "Ready" &&
          connection.View.PreviousRequestDetail.Length == 0 && connection.View.PreviousRequestError is null && connection.SelectChat(owner) &&
          connection.View.StatusSummary(false) == "Ready — retry available",
        "prior failure detail and retry indicator stay with their owning chat");

    fixture.Scenario("ask_cancel");
    Check(connection.StartAsk(request, false, "Keep the cancelled Grok question"), "explicit new Grok request starts");
    await WaitUntil(() => File.Exists(fixture.CancelReadyFile), "Grok cancellation fixture not ready");
    connection.Cancel(); await WaitIdle(connection);
    Check(connection.View.ConnectionError is null && connection.View.PreviousRequestError is null,
        "explicit cancellation does not expose an earlier failure for copying");
    Check(connection.View.StatusSummary(false) == "Cancelled — click for details" &&
          connection.View.RetryState.Contains("cancelled", StringComparison.OrdinalIgnoreCase),
        "fresh cancellation is not misclassified using an older account outcome");
    await Authorize(connection, fixture);
    Check(connection.View.StatusSummary(false) == "Ready — retry available" &&
          connection.View.PreviousRequestDetail.Contains("cancelled", StringComparison.OrdinalIgnoreCase) &&
          connection.View.RetryQuestion == "Keep the cancelled Grok question",
        "successful refresh retains cancellation identity and manual retry");
    fixture.Scenario("ask_success");
    Check(connection.StartAsk(request, false, connection.View.RetryQuestion!), "explicit retry starts");
    await WaitIdle(connection);
    Check(connection.View.StatusSummary(false) == "Ready" && connection.View.RetryQuestion is null &&
          connection.View.PreviousRequestDetail.Length == 0 && connection.View.History.Count == 1,
        "successful retry clears only the now-resolved retry indicator");
}

static async Task TestInvalidAnswerRecovery()
{
    const string markdown = "**123,456 gil**. [Gil wiki](https://ffxiv.consolegameswiki.com/wiki/Gil)";
    foreach (var (code, reason) in new[] {
        ("missing", "missing response"), ("json", "invalid JSON"),
        ("shape", "invalid answer shape"), ("length", "answer length outside 1-6000 characters") })
    {
        using var fixture = new Fixture();
        using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
        await Authorize(connection, fixture);
        connection.Draft = markdown;
        var request = JsonSerializer.Serialize(new { question = markdown });
        fixture.Scenario("ask_invalid_answer_" + code);
        Check(connection.StartAsk(request, false, markdown), "pasted Markdown send starts");
        await WaitIdle(connection);
        Check(connection.View.IsSignedIn && connection.View.CanTest && connection.View.SendBlockReason is null,
            "invalid model output preserves verified identity and readiness for an explicit send");
        Check(connection.View.State.Contains("[provider.request]", StringComparison.Ordinal) &&
              connection.View.State.Contains(reason, StringComparison.Ordinal), "sanitized answer failure explains its reason");
        Check(connection.View.History.Count == 0 && connection.View.QuestionAnswer == "No answer" &&
              connection.View.RetryQuestion == markdown && connection.Draft == markdown,
            "rejected answer is not saved and the pasted question remains available");
        Check(File.ReadAllLines(fixture.ActionLogFile).SequenceEqual(["status", "ask"]),
            "failure does not launch a retry or account refresh");
        using (var sent = JsonDocument.Parse(File.ReadAllText(fixture.RequestFile)))
            Check(sent.RootElement.GetProperty("question").GetString() == markdown, "Markdown input reaches the bridge unchanged");

        fixture.Scenario("ask_success");
        Check(connection.StartAsk(request, false, connection.View.RetryQuestion!), "explicit resend starts without refreshing status");
        await WaitIdle(connection);
        Check(connection.View.History.Count == 1 && connection.View.History[0].Question == markdown &&
              connection.View.RetryQuestion is null, "only the completed explicit resend enters history");
        Check(File.ReadAllLines(fixture.ActionLogFile).SequenceEqual(["status", "ask", "ask"]),
            "only the user's explicit second send launches another question");
    }
}

static async Task TestLookupLimitRecovery()
{
    foreach (var limit in new[] { "calls", "bytes" })
    {
        using var fixture = new Fixture();
        using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
        await Authorize(connection, fixture);
        connection.Draft = "Compare everything";
        fixture.Scenario("ask_lookup_limit_" + limit);
        Check(connection.StartAsk("{}", false, connection.Draft), "broad question starts");
        await WaitIdle(connection);
        Check(connection.View.IsSignedIn && connection.View.CanTest && connection.View.SendBlockReason is null,
            "lookup exhaustion does not masquerade as an account failure");
        Check(connection.View.State.Contains("lookup", StringComparison.OrdinalIgnoreCase) &&
              connection.View.State.Contains("narrow", StringComparison.OrdinalIgnoreCase), "limit explains a useful next action");
        Check(connection.View.History.Count == 0 && connection.View.QuestionAnswer == "No answer" &&
              connection.View.RetryQuestion == "Compare everything", "failed or late answers are not saved; question is recoverable");
        Check(File.ReadAllLines(fixture.ActionLogFile).SequenceEqual(["status", "ask"]),
            "no automatic account refresh or replay follows the limit");
        fixture.Scenario("ask_success");
        Check(connection.StartAsk("{}", false, "Compare only these two items"), "explicit narrowed question starts directly");
        await WaitIdle(connection);
        Check(connection.View.History.Count == 1 && connection.View.History[0].Question == "Compare only these two items" &&
              File.ReadAllLines(fixture.ActionLogFile).SequenceEqual(["status", "ask", "ask"]),
            "only the successful explicit question enters history");
    }
}

static async Task TestReaderFailures()
{
    using var fixture = new Fixture();
    using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
    foreach (var failure in new Exception[] { new TimeoutException("private timeout"), new IOException("private reader failure"),
        new UnauthorizedAccessException("private permission failure"), new InvalidDataException("private protocol failure"),
        new OperationCanceledException("permission revoked") })
    {
        await Authorize(connection, fixture);
        fixture.Scenario("ask_lookup");
        var before = connection.View.History.Count;
        Check(connection.StartAsk("{}", false, "reader fixture", includeQuests: true, questQuery: _ => throw failure),
            "reader fixture must start");
        await WaitIdle(connection);
        Check(connection.View.IsSignedIn, "reader/permission/protocol failures cannot erase verified identity");
        var recoverable = failure is TimeoutException or IOException;
        Check(connection.View.History.Count == before + (recoverable ? 1 : 0), "only ordinary unavailable readers may complete");
        Check(connection.View.CanTest == recoverable, "fatal lookup failures require a status refresh before sending");
        if (recoverable)
        {
            using var reply = JsonDocument.Parse(File.ReadAllText(fixture.QuestReplyFile));
            var marker = reply.RootElement.GetProperty("quest_result").GetProperty("result");
            Check(marker.EnumerateObject().Count() == 1 && marker.GetProperty("reader_unavailable").GetBoolean(),
                "unavailability must use the bounded marker, without raw exception content");
        }
        else Check(!File.Exists(fixture.QuestReplyFile), "fatal failures must not send a lookup result");
    }
}

static async Task TestQuotaFailureRefresh()
{
    using var fixture = new Fixture();
    var now = DateTimeOffset.FromUnixTimeSeconds(2000000000);
    using var connection = new CompanionConnection(preparePython: Fixture.PreparePython, utcNow: () => now);
    await Authorize(connection, fixture);
    fixture.Scenario("ask_error_quota");
    Check(connection.StartAsk("{}", false, "quota rejected"), "quota failure fixture starts");
    await WaitIdle(connection);
    Check(connection.View.IsSignedIn && !connection.View.CanTest, "quota failure preserves identity and blocks sends");
    File.Delete(fixture.RequestFile);
    now = now.AddMinutes(5).AddSeconds(-1);
    Check(!connection.View.Busy, "missing reset deadline must wait the five-minute floor");
    fixture.Scenario("status");
    now = now.AddSeconds(1);
    _ = connection.View;
    await WaitIdle(connection);
    Check(connection.View.CanTest && !File.Exists(fixture.RequestFile), "automatic recovery reads status only");
    Check(connection.View.History.Count == 0, "no failed question is retried or saved automatically");
}

static async Task TestQuestionContinuation()
{
    using var fixture = new Fixture();
    using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
    Check(!connection.CaptureQuestionContinuation()(), "idle connections cannot authorize a queued capture");
    await Authorize(connection, fixture);
    fixture.Scenario("ask_cancel");
    Check(connection.StartAsk("{}", false, "first guarded question",
        characterDetailsQuery: _ => throw new Exception("No tool call expected")), "first ask should start");
    var first = connection.CaptureQuestionContinuation();
    Check(first(), "the active question should authorize its queued capture");
    await WaitUntil(() => File.Exists(fixture.CancelReadyFile), "first ask did not receive its request");
    connection.Cancel();
    Check(!first(), "cancel must invalidate a queued capture immediately, before process exit");
    await WaitIdle(connection);
    Check(!first(), "the cancelled capture must remain invalid while idle");

    fixture.Scenario("ask_cancel");
    Check(connection.StartAsk("{}", false, "second guarded question",
        characterDetailsQuery: _ => throw new Exception("No tool call expected")), "the next ask should start");
    var second = connection.CaptureQuestionContinuation();
    Check(second() && !first(), "clearing cancellation for a new ask must not revive an earlier generation");
    await WaitUntil(() => File.Exists(fixture.CancelReadyFile), "second ask did not receive its request");
    connection.InvalidateQuestion();
    Check(!second() && !first(), "game-context invalidation must immediately invalidate both queued captures");
    await WaitIdle(connection);
    Check(connection.View.History.Count == 0, "neither cancelled question should enter history");
}

static async Task TestNewChat()
{
    using var fixture = new Fixture();
    using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
    await Authorize(connection, fixture);
    await Ask(connection, fixture, "source", false);
    Check(connection.View.History.Count == 1 && connection.View.QuestionSources.Count > 0,
        "precondition: answer has a source link");
    var oldId = connection.View.ActiveChatId;
    Check(connection.NewChat(), "new chat should succeed while idle");
    Check(connection.View.ActiveChatId != oldId && connection.View.Chats.Count == 2 &&
          connection.View.History.Count == 0 && connection.View.QuestionSources.Count == 0 &&
          connection.View.QuestionAnswer == "No question sent" && connection.HistoryContext.Exchanges.Length == 0,
        "new chat should start empty while retaining old tab");
    Check(connection.SelectChat(oldId) && connection.View.History.Count == 1 &&
          connection.HistoryContext.Exchanges.Single().Question == "source",
        "selecting old tab should restore history and model context");
}

static async Task TestInventoryInvalidation()
{
    using var fixture = new Fixture();
    using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
    await Authorize(connection, fixture);
    await Ask(connection, fixture, "inventory question", true);
    Check(connection.View.History.Count == 1, "inventory answer should enter history");
    await Ask(connection, fixture, "follow-up without inventory", false);
    Check(connection.View.History.Count == 2, "follow-up should enter same chat");
    connection.InvalidateQuestion();
    Check(connection.View.History.Count == 2 && connection.HistoryContext.Exchanges.Length == 2 &&
          connection.View.QuestionAnswer.Contains("Answer", StringComparison.Ordinal),
        "game invalidation should preserve completed messages and context");
}

static async Task TestQuestInvalidation()
{
    using var fixture = new Fixture();
    using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
    await Authorize(connection, fixture);
    fixture.Scenario("ask_cancel");
    Check(connection.StartAsk("{\"marker\":\"request-marker\"}", false, "quest question", true),
        "authorized quest-only question should start");
    await WaitUntil(() => File.Exists(fixture.CancelReadyFile), "fake quest ask did not receive request");
    connection.InvalidateQuestion();
    await WaitIdle(connection);
    Check(connection.View.History.Count == 0 && string.IsNullOrEmpty(connection.View.PendingQuestion),
        "invalidated quest-only question should be cancelled without saving an answer");
}

static async Task TestQuestLookupBridge()
{
    using var fixture = new Fixture();
    using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
    await Authorize(connection, fixture);
    var calls = 0;
    string Query(JsonElement arguments)
    {
        calls++;
        Check(arguments.GetProperty("view").GetString() == "active", "query arguments must reach the callback");
        return "{\"marker\":\"quest-result\"}";
    }
    fixture.Scenario("ask_lookup");
    Check(connection.StartAsk("{}", false, "active quests", true, Query), "quest ask must start");
    await WaitIdle(connection);
    Check(calls == 1 && connection.View.History.Count == 1 &&
        File.ReadAllText(fixture.QuestReplyFile).Contains("quest-result", StringComparison.Ordinal),
        "permitted lookup must return its private result and complete the answer");
    fixture.Scenario("ask_lookup");
    Check(connection.StartAsk("{}", false, "access disabled", false, Query), "ordinary ask must start");
    await WaitIdle(connection);
    Check(calls == 1 && connection.View.History.Count == 1,
        "disabled quest permission must not invoke callback or save an unexpected-tool answer");
}

static async Task TestQuestLookupRejection()
{
    using var fixture = new Fixture();
    using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
    await Authorize(connection, fixture);
    var calls = 0;
    fixture.Scenario("ask_lookup_replay");
    Check(connection.StartAsk("{}", false, "replay", true, _ => { calls++; return "{}"; }), "ask must start");
    await WaitIdle(connection);
    Check(calls == 1 && connection.View.History.Count == 0, "duplicate lookup ID must fail without a second callback");
    await Authorize(connection, fixture);
    fixture.Scenario("ask_lookup");
    Check(connection.StartAsk("{}", false, "oversized", true,
        _ => JsonSerializer.Serialize(new { text = new string('x', 20001) })), "ask must start");
    await WaitIdle(connection);
    Check(connection.View.History.Count == 0 && !File.Exists(fixture.QuestReplyFile),
        "oversized result must not be transmitted or saved as an answer");
}

static async Task TestQuestLookupCancellation()
{
    using var fixture = new Fixture();
    using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
    using var started = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    await Authorize(connection, fixture);
    fixture.Scenario("ask_lookup");
    Check(connection.StartAsk("{}", false, "cancel lookup", true, _ =>
    {
        started.Set();
        Check(release.Wait(TimeSpan.FromSeconds(5)), "test did not release query callback");
        return "{\"marker\":\"must-not-be-sent\"}";
    }), "ask must start");
    await WaitUntil(() => started.IsSet, "query callback did not start");
    connection.InvalidateQuestion();
    release.Set();
    await WaitIdle(connection);
    Check(connection.View.History.Count == 0 &&
        (!File.Exists(fixture.QuestReplyFile) || !File.ReadAllText(fixture.QuestReplyFile).Contains("must-not-be-sent")),
        "revocation during lookup must prevent result transmission");
}

static async Task TestSignoutAndReload()
{
    using var fixture = new Fixture();
    string chatId;
    using (var connection = new CompanionConnection(preparePython: Fixture.PreparePython))
    {
        await Authorize(connection, fixture);
        await Ask(connection, fixture, "saved before signout", true);
        chatId = connection.View.ActiveChatId;
        fixture.Scenario("logout");
        connection.Start("logout");
        await WaitIdle(connection);
        Check(!connection.View.CanTest && connection.View.History.Count == 1 &&
              connection.View.ActiveChatId == chatId, "signout should retain completed chat while removing authorization");
        var actionCount = File.ReadAllLines(fixture.ActionLogFile).Length;
        connection.Start("logout");
        Check(!connection.View.Busy && File.ReadAllLines(fixture.ActionLogFile).Length == actionCount,
            "Codex still refuses another logout while signed out");
    }
    using var reloaded = new CompanionConnection(preparePython: Fixture.PreparePython);
    Check(reloaded.View.ActiveChatId == chatId && reloaded.View.History.Single().Question == "saved before signout" &&
          reloaded.HistoryContext.Exchanges.Single().Question == "saved before signout",
        "new connection should restore active history from local storage");
}

static async Task TestQuotaReset()
{
    using var fixture = new Fixture();
    var now = DateTimeOffset.FromUnixTimeSeconds(2000000000);
    using var connection = new CompanionConnection(preparePython: Fixture.PreparePython, utcNow: () => now);
    fixture.Scenario("status_blocked");
    connection.Start("status");
    await WaitIdle(connection);
    Check(!connection.View.CanTest && connection.View.IsSignedIn, "exhausted quota must block sends");
    now = now.AddSeconds(64);
    Check(!connection.View.Busy && !connection.View.CanTest, "must wait until reset plus server grace");
    fixture.Scenario("status");
    now = now.AddSeconds(1);
    _ = connection.View;
    await WaitIdle(connection);
    Check(connection.View.CanTest && !File.Exists(fixture.RequestFile), "refresh must enable sends without a model request");
    fixture.Scenario("status_blocked");
    connection.Start("status");
    await WaitIdle(connection);
    Check(!connection.View.Busy && !connection.View.CanTest, "stale reset must not spin refresh every frame");
}

static async Task TestChatNotes()
{
    using var fixture = new Fixture();
    using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
    var first = connection.View.ActiveChatId;
    Check(connection.SaveChatNotes("Long-term breeding goal"), "notes save without login");
    Check(connection.HistoryContext.Notes == "Long-term breeding goal", "send snapshot contains saved notes");
    Check(connection.NewChat() && connection.ChatNotes == "", "new chats must not inherit unrelated notes");
    Check(connection.SaveChatNotes("Different goal") && connection.SelectChat(first) && connection.ChatNotes == "Long-term breeding goal",
        "switching preserves per-chat notes");
    fixture.Scenario("hold_status");
    connection.Start("status");
    Check(!connection.SaveChatNotes("must not change"), "busy notes cannot invalidate an active request");
    fixture.Release();
    await WaitIdle(connection);
    Check(connection.ChatNotes == "Long-term breeding goal", "rejected note change preserves previous text");
}

static async Task TestChatPersonality()
{
    using var fixture = new Fixture();
    var currentDefault = PersonalityPresets.Armo;
    using var connection = new CompanionConnection(preparePython: Fixture.PreparePython, defaultPersonality: () => currentDefault);
    var first = connection.View.ActiveChatId;
    Check(connection.ChatPersonality == PersonalityPresets.Armo, "new chat snapshots the configured personality");
    var revision = connection.HistoryRevision;
    Check(connection.SetChatPersonality(PersonalityPresets.None) && connection.HistoryRevision > revision,
        "explicit change saves the snapshot and invalidates prepared context");
    Check(connection.HistoryContext.Personality == PersonalityPresets.None, "request snapshot uses the chosen personality");
    Check(connection.NewChat() && connection.ChatPersonality == PersonalityPresets.Armo, "new chat uses current defaults");
    currentDefault = PersonalityPresets.None;
    Check(connection.ChatPersonality == PersonalityPresets.Armo, "changing defaults does not rewrite an existing chat");
    Check(connection.SelectChat(first) && connection.ChatPersonality == PersonalityPresets.None,
        "switch restores the independent personality");
    fixture.Scenario("hold_status");
    connection.Start("status");
    Check(!connection.SetChatPersonality(PersonalityPresets.Armo), "busy conversation cannot change its request personality");
    fixture.Release();
    await WaitIdle(connection);
    Check(connection.ChatPersonality == PersonalityPresets.None, "rejected change preserves the snapshot");
    using var reloaded = new CompanionConnection(preparePython: Fixture.PreparePython, defaultPersonality: () => PersonalityPresets.Armo);
    Check(reloaded.ChatPersonality == PersonalityPresets.None, "reload retains saved choice rather than new-chat defaults");
}

static async Task TestPresetRouting()
{
    using var fixture = new Fixture();
    var chatsPath = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "chats");
    var seed = new ChatStore(chatsPath, defaultPersonality: () => PersonalityPresets.Armo);
    var target = seed.ActiveId;
    var model = new CompanionModelSettings("gpt-6-luna", "low");
    Check(seed.SetActiveModelSelection(model) && seed.SetActiveNotes("Keep target notes"), "prepare target settings");
    seed.ActiveHistory.Add("Target question", "Target answer", [], null, false);
    seed.SetDraft("Target draft");
    Check(seed.SaveActive(), "persist target");
    var previous = seed.NewChat();
    Check(seed.SetActivePersonality(PersonalityPresets.None), "prepare unrelated current chat");
    using var connection = new CompanionConnection(preparePython: Fixture.PreparePython, defaultPersonality: () => PersonalityPresets.None);
    connection.Draft = "Previous unsent draft";
    var revision = connection.HistoryRevision;
    Check(connection.ContinuePersonalityChat(PersonalityPresets.Armo) && connection.View.ActiveChatId == target &&
          connection.HistoryRevision == revision + 1 && connection.ChatModelSelection == model &&
          connection.ChatNotes == "Keep target notes" && connection.Draft == "Target draft" &&
          connection.View.QuestionAnswer == "Target answer" && connection.View.History.Single().Question == "Target question" &&
          !connection.View.Busy && !File.Exists(fixture.RequestFile),
        "routing without login restores content, refreshes visible answer/context, and sends no question");
    var reloaded = new ChatStore(chatsPath);
    Check(reloaded.ActiveId == target && reloaded.Select(previous) && reloaded.ActiveDraft == "Previous unsent draft",
        "previous draft and chosen index persisted");
    Check(connection.ContinuePersonalityChat(PersonalityPresets.Armo) && connection.View.Chats.Count == 2,
        "repeated routing reuses the same exact chat");
    revision = connection.HistoryRevision;
    Check(!connection.ContinuePersonalityChat(new PersonalityPreset("invalid", "Broken", "text")) &&
          connection.View.ActiveChatId == target && connection.HistoryRevision == revision,
        "invalid route leaves selected chat and prepared context untouched");
    fixture.Scenario("hold_status");
    connection.Start("status");
    Check(connection.View.Busy && !connection.ContinuePersonalityChat(PersonalityPresets.None) &&
          connection.View.ActiveChatId == target && connection.HistoryRevision == revision,
        "exclusive account action refuses routing before saving or selecting");
    fixture.Release();
    await WaitIdle(connection);
    connection.Draft = "Keep draft after failure";
    var targetPath = Path.Combine(chatsPath, target + ".json");
    using (var locked = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        Check(connection.ContinuePersonalityChat(PersonalityPresets.None) && connection.View.ActiveChatId == previous &&
              connection.SelectChat(target) && connection.Draft == "Keep draft after failure" &&
              connection.View.QuestionAnswer == "Target answer" && connection.StorageInfo.UnsavedChats == 1,
            "save failure preserves content while allowing navigation");
    using (var locked = new FileStream(Path.Combine(chatsPath, "active.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        Check(connection.ContinuePersonalityChat(PersonalityPresets.None) && connection.View.ActiveChatId == previous,
            "index failure does not trap selected chat");
    Check(connection.NewChat() && connection.ChatPersonality == PersonalityPresets.None && !File.Exists(fixture.RequestFile),
        "ordinary new-chat default remains unchanged and no route auto-sends");
    var disposed = new CompanionConnection(preparePython: Fixture.PreparePython);
    var active = disposed.View.ActiveChatId;
    revision = disposed.HistoryRevision;
    disposed.Dispose();
    Check(!disposed.ContinuePersonalityChat(PersonalityPresets.Armo) && disposed.View.ActiveChatId == active &&
          disposed.HistoryRevision == revision, "disposed connection refuses routing");
}

static async Task TestInvalidChatPersonality()
{
    foreach (var invalid in new[] { "missing", "malformed", "default" })
    {
        using var fixture = new Fixture();
        if (invalid != "default")
        {
            string id;
            using (var seed = new CompanionConnection(preparePython: Fixture.PreparePython, defaultPersonality: () => PersonalityPresets.Armo))
                id = seed.View.ActiveChatId;
            var path = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "chats", id + ".json");
            var data = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            if (invalid == "missing") data.Remove("Personality");
            else data["Personality"] = JsonSerializer.SerializeToNode(new PersonalityPreset("invalid", "Broken", "text"));
            File.WriteAllText(path, data.ToJsonString());
        }
        using var connection = new CompanionConnection(preparePython: Fixture.PreparePython, defaultPersonality: () => invalid == "default"
            ? new PersonalityPreset("invalid", "Broken", "text") : PersonalityPresets.Armo);
        await Authorize(connection, fixture);
        connection.Draft = "keep this draft";
        Check(connection.ChatPersonality == PersonalityPresets.None && connection.ChatPersonalityError is not null &&
              connection.ChatPersonalityBlockReason is not null && connection.View.SendBlockReason is not null,
            "invalid " + invalid + " snapshot must not silently authorize the neutral display fallback");
        Check(connection.SyntheticTestBlockReason is null, "a chat personality problem must not block unrelated synthetic diagnostics");
        Check(!connection.StartAsk("{}", false, "keep this draft") && !connection.View.Busy &&
              connection.Draft == "keep this draft" && !File.Exists(fixture.RequestFile),
            "blocked personality must refuse before clearing the draft or starting a request");
        Check(connection.SetChatPersonality(PersonalityPresets.Armo) && connection.ChatPersonalityBlockReason is null &&
              connection.View.SendBlockReason is null, "explicit saved selection must repair the send gate");
        await Ask(connection, fixture, "repaired", false);
        Check(connection.View.History.Count == 1, "repaired personality must permit a completed fake reply");
    }
}

static async Task TestPersonalityReplacementFailure()
{
    using var fixture = new Fixture();
    using var connection = new CompanionConnection(preparePython: Fixture.PreparePython, defaultPersonality: () => PersonalityPresets.Armo);
    await Authorize(connection, fixture);
    var path = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "chats", connection.View.ActiveChatId + ".json");
    using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        Check(!connection.SetChatPersonality(PersonalityPresets.None) && connection.ChatPersonalityError is not null &&
              connection.ChatPersonality == PersonalityPresets.Armo && connection.ChatPersonalityBlockReason is null &&
              connection.View.SendBlockReason is null, "failed replacement must keep the valid prior personality sendable");
    }
    Check(!connection.SetChatPersonality(new PersonalityPreset("invalid", "Broken", "text")) &&
          connection.ChatPersonality == PersonalityPresets.Armo && connection.ChatPersonalityBlockReason is null,
        "invalid replacement must also preserve the prior valid selection");
    await Ask(connection, fixture, "keep previous personality", false);
    Check(connection.View.History.Count == 1, "a replacement error must not prevent a reply with the prior personality");
}

static async Task TestHistoryLookupBridge()
{
    foreach (var enabled in new[] { true, false })
    {
        using var fixture = new Fixture();
        using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
        await Authorize(connection, fixture);
        fixture.Scenario("ask_history_lookup");
        var calls = new List<string>();
        Check(connection.StartAsk("{}", true, "past decision", includeQuests: true,
            questQuery: _ => { calls.Add("quest"); return "{}"; },
            inventoryQuery: _ => { calls.Add("inventory"); return "{}"; },
            includeHistoryLookup: enabled, historyQuery: _ => { calls.Add("history"); return "{}"; }), "lookup send starts");
        await WaitIdle(connection);
        Check(enabled ? calls.SequenceEqual(["history", "quest", "inventory"]) : calls.Count == 0,
            "all three tools must share sequence and deny unpermitted history");
        Check(connection.View.History.Count == (enabled ? 1 : 0), "denied retrieval cannot become a saved answer");
    }
}

static async Task TestGameContextLookupBridge()
{
    foreach (var enabled in new[] { true, false })
    {
        using var fixture = new Fixture();
        using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
        await Authorize(connection, fixture);
        fixture.Scenario("ask_game_context_lookup");
        var calls = new List<string>();
        Check(connection.StartAsk("{}", true, "where am I", includeQuests: true,
            questQuery: _ => { calls.Add("quest"); return "{}"; },
            inventoryQuery: _ => { calls.Add("inventory"); return "{}"; },
            includeHistoryLookup: true, historyQuery: _ => { calls.Add("history"); return "{}"; },
            includeGameContext: enabled, gameContextQuery: _ => { calls.Add("game_context"); return "{}"; }), "send starts");
        await WaitIdle(connection);
        Check(enabled ? calls.SequenceEqual(["game_context", "history", "quest", "inventory"]) : calls.Count == 0,
            "all four tools must share sequence and deny unpermitted game context");
        Check(connection.View.History.Count == (enabled ? 1 : 0), "denied lookup cannot save an answer");
    }
}

static async Task TestCurrencyLookupBridge()
{
    foreach (var enabled in new[] { true, false })
    {
        using var fixture = new Fixture();
        using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
        await Authorize(connection, fixture);
        fixture.Scenario("ask_currency_lookup");
        var calls = new List<string>();
        Check(connection.StartAsk("{}", true, "my currencies", includeQuests: true,
            questQuery: _ => { calls.Add("quest"); return "{}"; },
            inventoryQuery: _ => { calls.Add("inventory"); return "{}"; },
            includeHistoryLookup: true, historyQuery: _ => { calls.Add("history"); return "{}"; },
            currencyQuery: enabled ? _ => { calls.Add("currency"); return "{}"; } : null), "send starts");
        await WaitIdle(connection);
        Check(enabled ? calls.SequenceEqual(["currency", "history", "quest", "inventory"]) : calls.Count == 0,
            "currency callback shares sequence and absent callback cannot read data");
        Check(connection.View.History.Count == (enabled ? 1 : 0), "denied lookup cannot save an answer");
    }
}

static async Task TestCurrencyInvalidation()
{
    using var fixture = new Fixture();
    using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
    await Authorize(connection, fixture);
    fixture.Scenario("ask_cancel");
    Check(connection.StartAsk("{}", false, "my currencies", currencyQuery: _ => "{}"), "send starts");
    await WaitUntil(() => File.Exists(fixture.CancelReadyFile), "fake currency ask did not receive request");
    connection.InvalidateQuestion();
    await WaitIdle(connection);
    Check(connection.View.History.Count == 0 && string.IsNullOrEmpty(connection.View.PendingQuestion),
        "currency-only answer must cancel on logout/character invalidation");
}

static async Task TestAdditionalReaderBridge()
{
    foreach (var missing in new[] { "", "game_definitions", "gc_supply", "npc_target", "storage", "collection", "progression", "crafting_log", "menus", "activities" })
    {
        using var fixture = new Fixture();
        using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
        await Authorize(connection, fixture);
        fixture.Scenario("ask_additional_lookup");
        var calls = new List<string>();
        Func<JsonElement, string>? Reader(string name) => name == missing ? null : _ => { calls.Add(name); return "{}"; };
        Check(connection.StartAsk("{}", false, "local definitions and observations",
            gameDefinitionsQuery: Reader("game_definitions"), gcSupplyQuery: Reader("gc_supply"),
            npcTargetQuery: Reader("npc_target"), storageQuery: Reader("storage"),
            collectionQuery: Reader("collection"), progressionQuery: Reader("progression"),
            craftingLogQuery: Reader("crafting_log"), menuQuery: Reader("menus"), activityQuery: Reader("activities")), "additional reader request starts");
        await WaitIdle(connection);
        var expected = new[] { "game_definitions", "gc_supply", "npc_target", "storage", "collection", "progression", "crafting_log", "menus", "activities" };
        Check(calls.SequenceEqual(missing == "" ? expected : expected.TakeWhile(name => name != missing)),
            "new tool dispatch shares sequence and stops at the absent callback");
        Check(connection.View.History.Count == (missing == "" ? 1 : 0), "missing reader cannot save a completed answer");
    }
    foreach (var kind in new[] { "gc_supply", "npc_target", "game_definitions", "storage", "collection", "progression", "crafting_log", "menus", "activities" })
    {
        using var fixture = new Fixture();
        using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
        await Authorize(connection, fixture);
        fixture.Scenario("ask_hold");
        Check(connection.StartAsk("{}", false, "invalidation check",
            gameDefinitionsQuery: kind == "game_definitions" ? _ => "{}" : null,
            gcSupplyQuery: kind == "gc_supply" ? _ => "{}" : null,
            npcTargetQuery: kind == "npc_target" ? _ => "{}" : null,
            storageQuery: kind == "storage" ? _ => "{}" : null,
            collectionQuery: kind == "collection" ? _ => "{}" : null,
            progressionQuery: kind == "progression" ? _ => "{}" : null,
            craftingLogQuery: kind == "crafting_log" ? _ => "{}" : null,
            menuQuery: kind == "menus" ? _ => "{}" : null,
            activityQuery: kind == "activities" ? _ => "{}" : null), "invalidation request starts");
        connection.InvalidateQuestion();
        fixture.Release();
        await WaitIdle(connection);
        Check(connection.View.History.Count == (kind == "game_definitions" ? 1 : 0),
            "only private reader questions are invalidated by character changes");
    }
}

static async Task TestLocalReaderPageBudget()
{
    foreach (var oversized in new[] { false, true })
    {
        using var fixture = new Fixture();
        using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
        await Authorize(connection, fixture);
        fixture.Scenario("ask_menu_pages");
        var callbacks = 0;
        Check(connection.StartAsk("{}", false, "Compare saved glamour plates", menuQuery: _ =>
        {
            callbacks++;
            return JsonSerializer.Serialize(new { entries = new string('x', oversized ? 20000 : 15500) });
        }), "menu paging starts");
        await WaitIdle(connection);
        Check(callbacks == (oversized ? 1 : 8) && connection.View.History.Count == 0,
            "the over-budget page must never be sent or save a partial answer");
        if (oversized)
            Check(connection.View.SendBlockReason is not null, "an oversized single result remains a protocol failure");
        else
        {
            Check(connection.View.IsSignedIn && connection.View.SendBlockReason is null &&
                connection.View.State.Contains("data lookup limit") && !connection.View.State.Contains("Reinstall"),
                "ordinary accumulated pages must report a reader budget, not a broken install or account");
            fixture.Scenario("ask_ok");
            Check(connection.StartAsk("{}", false, "Only compare two plates"), "explicit narrower send needs no account refresh");
            await WaitIdle(connection);
            Check(connection.View.History.Count == 1, "narrowed explicit question completes");
        }
    }
}

static async Task TestAllowanceDisplay()
{
    using var fixture = new Fixture();
    var now = DateTimeOffset.FromUnixTimeSeconds(2000000000);
    using var connection = new CompanionConnection(preparePython: Fixture.PreparePython, utcNow: () => now);
    fixture.Scenario("status_allowance");
    connection.Start("status");
    await WaitIdle(connection);
    Check(connection.AllowanceSummary == "Left: 5h 75% · 7d 60%" &&
          connection.View.Quota == "primary: 75% remaining; secondary: 60% remaining" &&
          connection.AllowanceDetails.Contains("5h: 75% remaining; resets in 1h") &&
          connection.AllowanceDetails.Contains("7d: 60% remaining") &&
          connection.AllowanceDetails.Contains("Allowance (last reported)") &&
          connection.AllowanceDetails.Contains("Received ") &&
          connection.AllowanceDetails.Contains("shared subscription allowance"),
        "compact and account displays must invert server usage into remaining allowance and retain reset information");
    now = now.AddHours(2);
    Check(connection.AllowanceSummary == "Left: 5h 75%* · 7d 60%" &&
          connection.AllowanceDetails.Contains("reset due; refresh status") &&
          connection.AllowanceDetails.Contains("* The reported reset time has passed."),
        "only expired windows get an explained marker; a passed deadline must not invent renewed allowance");
    fixture.Scenario("logout");
    connection.Start("logout");
    await WaitIdle(connection);
    Check(connection.AllowanceSummary == "Left: —" && connection.AllowanceDetails.Contains("unavailable"),
        "signed-out quota must not display old allowance");
}

static async Task TestDraftSendLifecycle()
{
    using var fixture = new Fixture();
    using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
    await Authorize(connection, fixture);
    connection.Draft = "  same question  ";
    fixture.Scenario("ask_hold");
    Check(connection.StartAsk("{}", false, "same question"), "send starts");
    Check(connection.Draft == "" && connection.View.PendingQuestion == "same question", "input clears while submitted question remains visible");
    connection.Draft = "same question"; // A deliberate new draft may even match the sent text.
    fixture.Release();
    await WaitIdle(connection);
    Check(connection.Draft == "same question" && connection.View.History.Count == 1, "successful answer must not erase a new draft");
    foreach (var cancel in new[] { false, true })
    {
        await Authorize(connection, fixture);
        connection.Draft = "  retry this  ";
        fixture.Scenario(cancel ? "ask_cancel" : "ask_fail");
        Check(connection.StartAsk("{}", false, "retry this") && connection.Draft == "", "accepted send clears exact draft");
        if (cancel)
        {
            await WaitUntil(() => File.Exists(fixture.CancelReadyFile), "cancel fixture not ready");
            connection.Cancel();
        }
        await WaitIdle(connection);
        Check(connection.Draft == "  retry this  ", "failed/cancelled question restores original input including whitespace");
    }
    await Authorize(connection, fixture);
    fixture.Scenario("ask_cancel");
    connection.Draft = "cancel and type";
    Check(connection.StartAsk("{}", false, "cancel and type"), "last send starts");
    await WaitUntil(() => File.Exists(fixture.CancelReadyFile), "cancel fixture not ready");
    connection.Draft = "new unrelated draft";
    connection.Cancel();
    await WaitIdle(connection);
    Check(connection.Draft == "new unrelated draft", "cancellation must not replace newly typed input");
    using (var unloading = new CompanionConnection(preparePython: Fixture.PreparePython))
    {
        await Authorize(unloading, fixture);
        fixture.Scenario("ask_cancel");
        unloading.Draft = "keep across reload";
        Check(unloading.StartAsk("{}", false, "keep across reload"), "unload fixture starts");
        await WaitUntil(() => File.Exists(fixture.CancelReadyFile), "unload fixture not ready");
    }
    using var reloaded = new CompanionConnection(preparePython: Fixture.PreparePython);
    Check(reloaded.Draft == "keep across reload", "unloading must save the unfinished question as a recoverable draft");
}

static async Task TestMissingCompanionFiles()
{
    using var fixture = new Fixture();
    using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
    await Authorize(connection, fixture);
    connection.Draft = "keep this failed question";
    var script = Path.Combine(Path.GetDirectoryName(Plugin.PluginInterface.AssemblyLocation.FullName)!, "companion", "companion.py");
    var moved = script + ".fixture-disabled";
    File.Move(script, moved);
    try
    {
        Check(connection.StartAsk("{}", false, connection.Draft), "missing-file attempt starts from a previously verified account");
        await WaitIdle(connection);
        Check(connection.View.State.Contains("companion/companion.py is missing", StringComparison.Ordinal) &&
              connection.View.State.Contains("CompAI.dll", StringComparison.Ordinal), "failure identifies the missing component and installation repair");
        Check(!connection.View.CanTest && connection.View.RetryQuestion == "keep this failed question" &&
              connection.Draft == "keep this failed question" && connection.View.History.Count == 0,
            "setup failure blocks sends while preserving the draft and retry");
        connection.Draft = "a different draft";
        foreach (var action in new[] { "status", "login" })
        {
            connection.Start(action);
            await WaitIdle(connection);
            Check(!connection.View.CanTest && connection.View.RetryQuestion == "keep this failed question" &&
                  connection.Draft == "a different draft" && connection.View.PendingQuestion.Length == 0,
                "account actions cannot repair missing files or replace the failed question");
        }
    }
    finally { File.Move(moved, script); }
    await Authorize(connection, fixture);
    Check(connection.View.CanTest && connection.View.RetryQuestion == "keep this failed question" &&
          connection.View.History.Count == 0 && !File.Exists(fixture.RequestFile),
        "restoring the component and refreshing restores readiness without automatically resending");
}

static async Task TestManualRetry()
{
    using var fixture = new Fixture();
    using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
    await Authorize(connection, fixture);
    var firstChat = connection.View.ActiveChatId;
    connection.Draft = "retry this question";
    fixture.Scenario("ask_fail");
    Check(connection.StartAsk("{\"marker\":\"old-context\"}", true, "retry this question"), "first attempt starts");
    await WaitIdle(connection);
    Check(connection.View.RetryQuestion == "retry this question" && connection.View.History.Count == 0 &&
          connection.Draft == "retry this question", "failed response retains its question and draft, not a history turn");
    Check(!connection.StartAsk("{}", false, connection.View.RetryQuestion!), "retry cannot bypass unverified account status");
    await Authorize(connection, fixture);
    Check(connection.View.RetryQuestion == "retry this question" && connection.View.History.Count == 0,
        "account refresh keeps retry available without sending it");

    Check(connection.NewChat() && connection.View.RetryQuestion is null, "another chat cannot retry the first chat's question");
    var secondChat = connection.View.ActiveChatId;
    fixture.Scenario("ask_cancel");
    Check(connection.StartAsk("{}", false, "second chat question"), "second chat starts");
    await WaitUntil(() => File.Exists(fixture.CancelReadyFile), "cancel fixture not ready");
    connection.Cancel();
    await WaitIdle(connection);
    Check(connection.View.RetryQuestion == "second chat question", "cancelled request can be manually retried");
    Check(connection.SelectChat(firstChat) && connection.View.RetryQuestion == "retry this question",
        "switching back restores only this chat's failed question");
    await Authorize(connection, fixture);
    connection.Draft = "unrelated new draft";
    fixture.Scenario("ask_success");
    var retryQuestion = connection.View.RetryQuestion!;
    Check(connection.StartAsk("{\"marker\":\"fresh-context\"}", false, retryQuestion), "manual retry starts with rebuilt request");
    Check(connection.View.RetryQuestion is null && !connection.StartAsk("{}", false, retryQuestion),
        "accepted retry hides its action and refuses duplicate sends while busy");
    await WaitIdle(connection);
    Check(connection.View.History.Count == 1 && connection.View.History[0].Question == retryQuestion &&
          connection.View.RetryQuestion is null && connection.Draft == "unrelated new draft",
        "successful retry creates exactly one turn and leaves unrelated draft intact");
    Check(File.ReadAllText(fixture.RequestFile).Contains("fresh-context", StringComparison.Ordinal),
        "retry uses supplied fresh context, not an old request body");
    Check(connection.SelectChat(secondChat) && connection.View.RetryQuestion == "second chat question",
        "another chat's retry survives this chat's successful send");
    Check(connection.DeleteChat(secondChat) && connection.View.RetryQuestion is null,
        "deleting a failed chat leaves no retry action in the remaining chat");
}

static async Task TestMixedLookupBridge()
{
    foreach (var (mode, enabled, succeeds) in new[] {
        ("ask_mixed_lookup", true, true), ("ask_mixed_lookup", false, false),
        ("ask_mixed_replay", true, false) })
    {
        using var fixture = new Fixture();
        using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
        await Authorize(connection, fixture);
        fixture.Scenario(mode);
        var inventoryCalls = 0;
        var questCalls = 0;
        Check(connection.StartAsk("{}", enabled, "mixed lookup", includeQuests: true,
            questQuery: _ => { questCalls++; return "{\"rows\":[]}"; },
            inventoryQuery: _ => { inventoryCalls++; return "{\"rows\":[]}"; }), "ask starts");
        await WaitIdle(connection);
        Check(inventoryCalls == (enabled ? 1 : 0) && questCalls == (succeeds ? 1 : 0),
            "permission and cross-tool replay gates must protect callbacks");
        Check(connection.View.History.Count == (succeeds ? 1 : 0), "only complete mixed lookup persists");
    }
}

static async Task TestInventoryFindKey()
{
    using var fixture = new Fixture();
    using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
    using var started = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    await Authorize(connection, fixture);
    RejectOutsideCallback();
    var owner = connection.View.ActiveChatId;
    var key = Guid.NewGuid();
    fixture.Scenario("ask_mixed_lookup");
    Check(connection.StartAsk("{}", true, "find in the first chat", includeQuests: true,
        questQuery: _ => "{}", inventoryQuery: _ =>
        {
            started.Set();
            Check(release.Wait(TimeSpan.FromSeconds(5)), "inventory callback was not released");
            connection.AttachInventoryFindKey(key);
            return "{}";
        }), "inventory owner starts");
    string sibling;
    try
    {
        await WaitUntil(() => started.IsSet, "inventory callback did not start");
        RejectOutsideCallback();
        Check(connection.NewChat(), "another chat is selectable while the inventory callback waits");
        sibling = connection.View.ActiveChatId;
        fixture.Scenario("ask_success");
        Check(connection.StartAsk("{}", false, "unkeyed sibling answer"), "sibling request starts");
        await WaitUntil(() => !connection.View.ActiveChatBusy && connection.View.History.Count == 1,
            "sibling answer did not complete independently");
        Check(connection.View.History.Single().InventoryFindKey is null, "unrelated answer acquired an inventory key");
    }
    finally { release.Set(); }
    await WaitIdle(connection);
    Check(connection.View.ActiveChatId == sibling && connection.View.History.Single().InventoryFindKey is null,
        "background callback attached its key to the selected sibling");
    Check(connection.SelectChat(owner) && connection.View.History.Single().InventoryFindKey == key &&
        connection.View.History.Single().Question == "find in the first chat", "completed key lost its originating answer");
    RejectOutsideCallback();

    Check(connection.NewChat(), "cancelled lookup gets its own chat");
    started.Reset();
    release.Reset();
    var cancelledKey = Guid.NewGuid();
    var lateRejected = 0;
    fixture.Scenario("ask_mixed_lookup");
    Check(connection.StartAsk("{}", true, "cancel this keyed lookup", includeQuests: true,
        questQuery: _ => "{}", inventoryQuery: _ =>
        {
            connection.AttachInventoryFindKey(cancelledKey);
            started.Set();
            Check(release.Wait(TimeSpan.FromSeconds(5)), "cancelled inventory callback was not released");
            try { connection.AttachInventoryFindKey(Guid.NewGuid()); }
            catch (OperationCanceledException) { Interlocked.Exchange(ref lateRejected, 1); }
            return "{}";
        }), "cancelled inventory owner starts");
    try
    {
        await WaitUntil(() => started.IsSet, "cancelled inventory callback did not start");
        connection.Cancel();
    }
    finally { release.Set(); }
    await WaitIdle(connection);
    Check(Volatile.Read(ref lateRejected) == 1 && connection.View.History.Count == 0 && connection.View.PartialAnswer == "",
        "cancelled callback accepted a late key or exposed a usable completed answer");
    Check(connection.SelectChat(owner) && connection.View.History.Single().InventoryFindKey == key &&
        connection.SelectChat(sibling) && connection.View.History.Single().InventoryFindKey is null,
        "cancelled key changed a completed answer in another chat");

    void RejectOutsideCallback()
    {
        try { connection.AttachInventoryFindKey(Guid.NewGuid()); }
        catch (OperationCanceledException) { return; }
        throw new Exception("inventory find key was accepted outside a tool callback");
    }
}

static async Task TestShopFindKey()
{
    using var fixture = new Fixture();
    using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
    using var started = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    await Authorize(connection, fixture);
    void RejectOutside()
    {
        try { connection.AttachShopFindKey(Guid.NewGuid()); }
        catch (OperationCanceledException) { return; }
        throw new Exception("shop key accepted outside its callback");
    }
    RejectOutside();
    var owner = connection.View.ActiveChatId;
    var key = Guid.NewGuid();
    fixture.Scenario("ask_shop_lookup");
    Check(connection.StartAsk("{}", false, "find an exact current shop offer", menuQuery: _ =>
    {
        started.Set(); Check(release.Wait(TimeSpan.FromSeconds(5)), "shop callback not released");
        connection.AttachShopFindKey(key); return "{}";
    }), "shop question starts");
    string sibling;
    try
    {
        await WaitUntil(() => started.IsSet, "shop callback did not start");
        RejectOutside();
        Check(connection.NewChat(), "sibling chat opens"); sibling = connection.View.ActiveChatId;
        fixture.Scenario("ask_success");
        Check(connection.StartAsk("{}", false, "unkeyed sibling"), "sibling question starts");
        await WaitUntil(() => !connection.View.ActiveChatBusy && connection.View.History.Count == 1, "sibling did not finish");
        Check(connection.View.History.Single().ShopFindKey is null, "shop key leaked into sibling");
    }
    finally { release.Set(); }
    await WaitIdle(connection);
    Check(connection.View.ActiveChatId == sibling && connection.View.History.Single().ShopFindKey is null,
        "shop key followed current selection instead of callback owner");
    Check(connection.SelectChat(owner) && connection.View.History.Single().ShopFindKey == key &&
        connection.View.History.Single().InventoryFindKey is null, "shop key not attached only to original answer");
    RejectOutside();
    Check(connection.NewChat(), "cancel chat opens"); started.Reset(); release.Reset();
    var rejected = 0;
    fixture.Scenario("ask_shop_lookup");
    Check(connection.StartAsk("{}", false, "cancel shop lookup", menuQuery: _ =>
    {
        connection.AttachShopFindKey(Guid.NewGuid()); started.Set();
        Check(release.Wait(TimeSpan.FromSeconds(5)), "cancelled shop callback not released");
        try { connection.AttachShopFindKey(Guid.NewGuid()); }
        catch (OperationCanceledException) { Interlocked.Exchange(ref rejected, 1); }
        return "{}";
    }), "cancellable shop question starts");
    try { await WaitUntil(() => started.IsSet, "cancelled shop callback did not start"); connection.Cancel(); }
    finally { release.Set(); }
    await WaitIdle(connection);
    Check(Volatile.Read(ref rejected) == 1 && connection.View.History.Count == 0, "cancelled shop key became usable");
    Check(connection.SelectChat(owner) && connection.View.History.Single().ShopFindKey == key,
        "cancelled request disturbed completed shop key");
}

static async Task Authorize(CompanionConnection connection, Fixture fixture)
{
    fixture.Scenario("status");
    connection.Start("status");
    await WaitIdle(connection);
    Check(connection.View.CanTest, "fake verified status should authorize asks");
}

static async Task TestStreamingReplies()
{
    using var fixture = new Fixture();
    using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
    await Authorize(connection, fixture);
    await Ask(connection, fixture, "Default off", false, replyInGameChat: true);
    Check(connection.TakeGameChatMessage() is null, "default must not print old replies");
    connection.SetGameChatReplies(true);
    Check(connection.TakeGameChatMessage() is null, "enabling must not replay history");
    fixture.Scenario("ask_stream_hold");
    Check(connection.StartAsk("{}", false, "Stream", replyInGameChat: true), "stream should start");
    await WaitUntil(() => connection.View.PartialAnswer.Length == 12000, "escaped non-BMP stream should fit");
    Check(connection.View.History.Count == 1 && connection.TakeGameChatMessage() is null,
        "partial text must not enter history or game chat");
    fixture.Release();
    await WaitIdle(connection);
    Check(connection.View.PartialAnswer == "" && connection.View.History.Count == 2, "final replaces partial once");
    Check(connection.TakeGameChatMessage()?.Text == connection.View.History[^1].Answer &&
        connection.TakeGameChatMessage() is null, "one successful final should print once");

    fixture.Scenario("ask_stream_cancel");
    Check(connection.StartAsk("{}", false, "Cancel", replyInGameChat: true), "cancel case should start");
    await WaitUntil(() => connection.View.PartialAnswer.Length > 0, "cancel stream should appear");
    connection.Cancel();
    Check(connection.View.PartialAnswer == "", "cancel must clear preview immediately");
    await WaitIdle(connection);
    var cancelled = connection.TakeGameChatMessage();
    Check(cancelled is not null && cancelled.Text.Contains("cancelled") && !cancelled.Text.Contains("😀") &&
        connection.View.History.Count == 2, "cancel may print status but never partial text");

    fixture.Scenario("ask_stream_hold");
    Check(connection.StartAsk("{}", false, "Disable", replyInGameChat: true), "disable case should start");
    await WaitUntil(() => connection.View.PartialAnswer.Length > 0, "disable stream should appear");
    connection.SetGameChatReplies(false);
    connection.SetGameChatReplies(true);
    fixture.Release();
    await WaitIdle(connection);
    Check(connection.TakeGameChatMessage() is null, "reenabling must not resurrect pending output");

    fixture.Scenario("ask_stream_hold");
    Check(connection.StartAsk("{}", false, "Logout", replyInGameChat: true), "logout case should start");
    await WaitUntil(() => connection.View.PartialAnswer.Length > 0, "logout stream should appear");
    connection.DiscardGameChatReplies(); // Game logout/character transition drops this output.
    fixture.Release();
    await WaitIdle(connection);
    Check(connection.TakeGameChatMessage() is null, "logout must discard in-flight game output");

    fixture.Scenario("ask_fail");
    Check(connection.StartAsk("{}", false, "Fail after final", replyInGameChat: true), "failure case should start");
    await WaitIdle(connection);
    var failure = connection.TakeGameChatMessage();
    Check(failure is not null && !failure.Text.Contains("Answer [guide]") && failure.Text.Contains("failed"),
        "a final payload from a failing process must only print failure status");
    Check(connection.View.PartialAnswer == "", "failure must leave no preview");
}

static async Task TestPluginWindowReplies()
{
    using var fixture = new Fixture();
    using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
    await Authorize(connection, fixture);
    connection.SetGameChatReplies(true);
    await Ask(connection, fixture, "Command question", false, replyInGameChat: true);
    Check(connection.TakeGameChatMessage() is not null && connection.TakeGameChatMessage() is null,
        "command reply should be delivered once before switching to plugin input");

    fixture.Scenario("ask_stream_hold");
    Check(connection.StartAsk("{}", false, "Plugin question"), "plugin request should start");
    await WaitUntil(() => connection.View.PartialAnswer.Length > 0, "plugin stream should appear");
    Check(connection.TakeGameChatMessage() is null, "plugin preview must stay out of game chat");
    fixture.Release();
    await WaitIdle(connection);
    Check(connection.View.History.Count == 2 && connection.View.History[^1].Question == "Plugin question" &&
        connection.View.PartialAnswer == "" && connection.TakeGameChatMessage() is null,
        "plugin final must enter history without inheriting command delivery");

    fixture.Scenario("ask_stream_cancel");
    Check(connection.StartAsk("{}", false, "Cancel plugin question"), "plugin cancellation case starts");
    await WaitUntil(() => connection.View.PartialAnswer.Length > 0, "plugin cancellation preview appears");
    connection.Cancel();
    await WaitIdle(connection);
    Check(connection.View.History.Count == 2 && connection.View.PartialAnswer == "" &&
        connection.View.RetryQuestion == "Cancel plugin question" && connection.TakeGameChatMessage() is null,
        "cancelled plugin question remains retryable without printing a game-chat cancellation");

    fixture.Scenario("ask_fail");
    Check(connection.StartAsk("{}", false, "Failed plugin question"), "plugin failure case starts");
    await WaitIdle(connection);
    Check(connection.View.History.Count == 2 && connection.View.RetryQuestion == "Failed plugin question" &&
        connection.TakeGameChatMessage() is null,
        "failed plugin question remains retryable without printing a game-chat error");
}

static Task TestGameChatFormat()
{
    var text = "**Salut șțâîă β** [guide](https://example.com/test)\n\0\u0002\u0003" +
        string.Concat(Enumerable.Repeat(" 😀", 900));
    var lines = GameChatReply.Format(text);
    Check(lines.Count > 1 && lines.All(line => Encoding.UTF8.GetByteCount(line) <= GameChatReply.MaxLineBytes),
        "output must have bounded UTF-8 lines");
    var joined = string.Join("", lines);
    Check(joined.Contains("Salut șțâîă β") && joined.Contains("guide (https://example.com/test)") &&
        !joined.Any(char.IsControl) && !joined.Contains("**") && !joined.Contains('\uFFFD'),
        "preserve Romanian, Unicode, and sources without raw payloads or broken surrogates");
    Check(GameChatReply.Format("\n\0\u0002").Count == 0, "empty/control-only output prints nothing");
    Check(GameChatReply.Format("**literal** [input]", "Ștefan Test", markdown: false).Single() ==
        "[Ștefan Test]: **literal** [input]", "local user echo preserves input instead of parsing Markdown");
    Check(GameChatReply.Format("hello", "ArmoE").Single() == "[ArmoE]: hello",
        "reply prefix must identify its actual speaker");
    Check(GameChatReply.Format("hello", "[\u0002Armo\n]:").Single() == "[Armo]: hello",
        "speaker labels cannot inject line breaks or payloads");
    const string mapAnswer = "Go to [Town (X:12.3, Y:24.5)](compai-map://132/2?x=12.3&y=24.5).";
    Check(GameChatReply.Format(mapAnswer, "Armo").Single() == "[Armo]: Go to Town (X:12.3, Y:24.5)." &&
        AnswerLinks.ExtractMaps(mapAnswer).Count == 1,
        "Plain formatting keeps map labels while structured formatting can render validated inline actions");
    return Task.CompletedTask;
}

static async Task TestAnswerSpeaker()
{
    using var fixture = new Fixture();
    using var connection = new CompanionConnection(preparePython: Fixture.PreparePython, defaultPersonality: () => PersonalityPresets.Armo);
    await Authorize(connection, fixture);
    connection.SetGameChatReplies(true);
    fixture.Scenario("ask_hold");
    Check(connection.StartAsk("{}", false, "First speaker", replyInGameChat: true), "first question starts");
    Check(connection.View.PendingPersonalityName == "Armo", "pending label uses send snapshot");
    fixture.Release();
    await WaitIdle(connection);
    var other = new PersonalityPreset(Guid.NewGuid().ToString("N"), "ArmoE", "A different tone.");
    Check(connection.SetChatPersonality(other),
        "personality can change after completion: " + connection.ChatPersonalityError + " / " + connection.View.StorageError);
    var reply = connection.TakeGameChatMessage();
    Check(reply?.Speaker == "Armo" && reply.PersonalityId == PersonalityPresets.Armo.Id &&
        connection.View.History.Single().PersonalityName == "Armo", "queued and saved labels must not follow current selection");
    await Ask(connection, fixture, "Second speaker", false, replyInGameChat: true);
    Check(connection.View.History[^1].PersonalityName == "ArmoE" &&
        connection.TakeGameChatMessage()?.PersonalityId == other.Id, "next answer uses new personality identity");
    Check(!connection.HistoryContext.Exchanges[0].Answer.Contains("Armo"),
        "display metadata is not added to provider answer context");
    Check(connection.SetChatPersonality(PersonalityPresets.None), "None remains selectable");
    await Ask(connection, fixture, "No persona", false, replyInGameChat: true);
    Check(connection.View.History[^1].PersonalityName == "Companion" &&
        connection.TakeGameChatMessage()?.Speaker == "Companion", "None uses generic label");
}

static async Task Ask(CompanionConnection connection, Fixture fixture, string question, bool inventory,
    bool replyInGameChat = false)
{
    fixture.Scenario("ask_success");
    Check(connection.StartAsk("{\"marker\":\"request-marker\"}", inventory, question, replyInGameChat: replyInGameChat),
        "authorized ask should start");
    await WaitIdle(connection);
}

static async Task WaitIdle(CompanionConnection connection)
{
    await WaitUntil(() => !connection.View.Busy, "companion did not become idle");
}

static async Task WaitUntil(Func<bool> predicate, string error)
{
    var timer = Stopwatch.StartNew();
    while (!predicate())
    {
        if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(error);
        await Task.Delay(20);
    }
}

sealed class Fixture : IDisposable
{
    // Explicit test-only injection. Production never falls back to PATH Python.
    internal static Task<string> PreparePython(string directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult("python");
    }
    private readonly string root = Path.Combine(Path.GetTempPath(), "CompanionStateTests-" + Guid.NewGuid().ToString("N"));
    public string RequestFile => Path.Combine(this.root, "companion", "request.txt");
    public string ActionLogFile => Path.Combine(this.root, "companion", "actions.txt");
    public string ProviderLogFile => Path.Combine(this.root, "companion", "providers.txt");
    public string ArgumentsLogFile => Path.Combine(this.root, "companion", "arguments.jsonl");
    public string CancelReadyFile => Path.Combine(this.root, "companion", "cancel-ready.flag");
    public string QuestReplyFile => Path.Combine(this.root, "companion", "quest-reply.txt");
    public string ParallelFile(string id, string kind) => Path.Combine(this.root, "companion", $"{id}-{kind}.flag");
    public void ReleaseParallel(string id) => File.WriteAllText(this.ParallelFile(id, "release"), "go");
    public Fixture()
    {
        var directory = Path.Combine(this.root, "companion");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "companion.py"), FakePython, new UTF8Encoding(false));
        Plugin.PluginInterface = new FakePluginInterface(Path.Combine(this.root, "CompAI.dll"),
            Path.Combine(this.root, "config"));
    }
    public void Scenario(string name)
    {
        File.Delete(this.CancelReadyFile);
        File.Delete(this.QuestReplyFile);
        File.Delete(Path.Combine(this.root, "companion", "release.flag"));
        File.WriteAllText(Path.Combine(this.root, "companion", "scenario.json"), JsonSerializer.Serialize(new { name }));
    }
    public void Release() => File.WriteAllText(Path.Combine(this.root, "companion", "release.flag"), "go");
    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch { }
    }
    private const string FakePython = """
import json, pathlib, sys, time
folder = pathlib.Path(__file__).resolve().parent
mode = json.loads((folder / 'scenario.json').read_text(encoding='utf-8'))['name']
action = sys.argv[-1]
provider = sys.argv[sys.argv.index('--provider') + 1] if '--provider' in sys.argv else 'codex'
with (folder / 'actions.txt').open('a', encoding='utf-8') as actions: actions.write(action + '\n')
with (folder / 'providers.txt').open('a', encoding='utf-8') as providers: providers.write(provider + '\n')
with (folder / 'arguments.jsonl').open('a', encoding='utf-8') as arguments: arguments.write(json.dumps(sys.argv) + '\n')
if mode.startswith('local_logout_'):
    if mode == 'local_logout_status_only':
        print(json.dumps({'provider':'grok', 'runtime_pin':'verified', 'account':{'type':'grok'}, 'quota_guard':'available'}), flush=True)
        sys.exit(0)
    message = {'provider':'grok', 'local_logout':True}
    if mode == 'local_logout_false': message['local_logout'] = False
    if mode == 'local_logout_extra': message['runtime_pin'] = 'verified'
    if mode == 'local_logout_wrong_provider': message['provider'] = 'codex'
    print(json.dumps(message), flush=True)
    if mode == 'local_logout_duplicate': print(json.dumps(message), flush=True)
    if mode == 'local_logout_late_status': print(json.dumps({'provider':'grok', 'runtime_pin':'verified', 'account':{'type':'grok'}}), flush=True)
    sys.exit(1 if mode == 'local_logout_failure_exit' else 0)
if action == 'logout' and provider == 'grok':
    print(json.dumps({'provider':'grok', 'local_logout':True}), flush=True)
    sys.exit(0)
status = {'runtime_pin':'verified','account':{'type':'chatgpt','planType':'plus'},
          'quota':{'primary':{'usedPercent':20},'secondary':{'usedPercent':30}},
          'quota_guard':'available',
          'models':[{'id':'gpt-6-sol','name':'GPT-6 Sol','efforts':['low','medium','high'],'default_effort':'medium'},
                    {'id':'gpt-6-luna','name':'GPT-6 Luna','efforts':['low','medium','high'],'default_effort':'medium'}]}
if provider != 'codex':
    status.update(provider=provider, account={'type':provider,'planType':'free'},
                  models=[{'id':'auto','name':'Automatic','efforts':['none'],'default_effort':'none'}])
if provider == 'grok':
    status['quota'] = {'primary': {'usedPercent': 1.0, 'resetsAt': 2000003600}}
if mode == 'provider_missing_tag': status.pop('provider', None)
if mode == 'provider_wrong_tag': status['provider'] = 'codex'
if mode == 'provider_wrong_account': status['account']['type'] = 'chatgpt'
if mode.startswith('provider_device_login'):
    login = {'provider':provider,'login':'waiting for user','authUrl':'https://accounts.x.ai/oauth2/device','user_code':'ABCD-EFGH','openBrowser':True}
    if mode == 'provider_device_login_native': login['openBrowser'] = False
    if mode == 'provider_device_login_bad_browser': login['openBrowser'] = 'false'
    print(json.dumps(login), flush=True)
if mode == 'status_no_models':
    del status['models']
    status['models_error'] = 'Discovery fixture unavailable'
if mode == 'status_bad_models': status['models'][0]['default_effort'] = 'unsupported'
if mode == 'status_duplicate_models': status['models'][1]['id'] = 'gpt-6-sol'
if mode == 'status_unknown_effort': status['models'][0]['efforts'] = ['unknown']
if mode == 'status_invalid_id': status['models'][0]['id'] = 'invalid model id'
if mode == 'status_allowance':
    status['quota'] = {'primary': {'usedPercent': 25, 'resetsAt': 2000003600, 'windowDurationMins': 300},
                       'secondary': {'usedPercent': 40, 'resetsAt': 2000086400, 'windowDurationMins': 10080}}
if mode == 'status_blocked':
    status['quota']['primary'] = {'usedPercent': 100, 'resetsAt': 2000000060}
    status['quota']['secondary']['resetsAt'] = None
    status['quota_guard'] = 'blocked'
    status['quota_block_reason'] = 'Usage limit reached; no turn permitted'
if mode in ('hold_status', 'provider_hold_status', 'provider_device_login', 'provider_device_login_native'):
    for _ in range(500):
        if (folder / 'release.flag').exists(): break
        time.sleep(.01)
if action == 'ask':
    request = sys.stdin.readline()
    (folder / 'request.txt').write_text(request, encoding='utf-8')
    if mode == 'ask_bounded_output':
        import os
        (folder / 'bounded-pid.txt').write_text(str(os.getpid()), encoding='ascii')
        variant = json.loads(request)['variant']
        print(json.dumps({'question_partial':'Synthetic preview'}), flush=True)
        if variant in ('lf', 'crlf', 'eof', 'oversized'):
            prefix, suffix = '{"padding":"', '","question_answer":"Bounded answer"}'
            frame = prefix + 'x' * (131072 - len(prefix) - len(suffix) + (variant == 'oversized')) + suffix
            ending = {'lf':'\n', 'crlf':'\r\n', 'eof':'', 'oversized':'\n'}[variant]
            sys.stdout.buffer.write((frame + ending).encode('utf-8'))
            sys.stdout.buffer.flush()
            if variant != 'oversized': sys.exit(0)
        elif variant == 'unterminated':
            print(json.dumps({'question_answer':'Must be discarded after protocol failure'}), flush=True)
            sys.stdout.buffer.write(b'x' * 131073)
            sys.stdout.buffer.flush()
        elif variant == 'truncated':
            sys.stdout.write('{"question_answer":')
            sys.stdout.flush()
            sys.exit(0)
        else:
            sys.stdout.write('x' * 8192)
            sys.stdout.flush()
            (folder / 'cancel-ready.flag').write_text('ready', encoding='utf-8')
            sys.stdin.readline()
            sys.exit(0)
        time.sleep(60)
        sys.exit(1)
    if mode == 'ask_valuation':
        variant = json.loads(request)['mode']
        steps = [('valuation', 'candidates', ['inventory','retainers']), ('valuation', 'details', ['retainers','inventory'])]
        if variant == 'third': steps.append(('valuation','details',['retainers','inventory']))
        elif variant == 'scope': steps[1] = ('valuation','details',['retainers'])
        elif variant == 'details_first': steps = [steps[1]]
        elif variant in ('success','ordinary_excess'):
            steps.extend(('quest','ignored',[]) for _ in range(13 if variant == 'ordinary_excess' else 12))
        for seq, (kind, view, categories) in enumerate(steps, 1):
            arguments = {'view':view,'categories':categories,'handles':[] if view=='candidates' else [1]} if kind == 'valuation' else {}
            print(json.dumps({kind+'_query':{'id':seq,'arguments':arguments}}),flush=True)
            reply = sys.stdin.readline()
            if not reply or json.loads(reply) == {'cancel':True}: sys.exit(0)
            assert json.loads(reply)[kind+'_result']['id'] == seq
    if mode == 'ask_parallel':
        import threading
        submitted = json.loads(request)
        owner = submitted['id']
        if submitted.get('lookup'):
            (folder / f'{owner}-ready.flag').write_text('ready', encoding='utf-8')
            for _ in range(1000):
                if (folder / f'{owner}-query.flag').exists(): break
                time.sleep(.01)
            print(json.dumps({'quest_query': {'id': 1, 'arguments': {}}}), flush=True)
            reply = sys.stdin.readline()
            if not reply or json.loads(reply) == {'cancel': True}: sys.exit(0)
            (folder / f'{owner}-result.flag').write_text(reply, encoding='utf-8')
        cancelled = threading.Event()
        def read_cancel():
            line = sys.stdin.readline()
            if not line or json.loads(line) == {'cancel': True}: cancelled.set()
        threading.Thread(target=read_cancel, daemon=True).start()
        print(json.dumps({'question_partial': f'Partial {owner}'}), flush=True)
        (folder / f'{owner}-ready.flag').write_text('ready', encoding='utf-8')
        for _ in range(1000):
            if cancelled.is_set(): sys.exit(0)
            if (folder / f'{owner}-release.flag').exists(): break
            time.sleep(.01)
        if submitted.get('authentication_error'):
            print(json.dumps({'error': 'Authentication fixture failure', 'error_kind': 'authentication'}), flush=True)
            print(json.dumps(status), flush=True)
            print(json.dumps({'question_answer': 'Late rejected answer'}), flush=True)
            sys.exit(1)
        if submitted.get('failure_kind'):
            print(json.dumps({'error': 'Failure fixture', 'error_kind': submitted['failure_kind']}), flush=True)
            sys.exit(1)
        print(json.dumps({'question_answer': f'Answer {owner}', 'question_context': f'Owner {owner}'}), flush=True)
        if submitted.get('hold_exit'):
            for _ in range(1000):
                if cancelled.is_set(): sys.exit(0)
                if (folder / f'{owner}-exit.flag').exists(): break
                time.sleep(.01)
        sys.exit(0)
    if mode.startswith('ask_error_'):
        print(json.dumps({'error':'Sanitized fixture failure','error_kind':mode[len('ask_error_'):],
                          'error_stage':'provider.request'}), flush=True)
    if mode.startswith('ask_lookup_limit_'):
        limit = '12 calls' if mode.endswith('calls') else '120000 bytes'
        print(json.dumps({'error':f'This question reached its data lookup limit ({limit}). Try a narrower question; no automatic retry.',
                          'error_kind':'reader', 'error_stage':'provider.request'}), flush=True)
        print(json.dumps({'question_answer':'Late answer must not be saved'}), flush=True)
        sys.exit(1)
    if mode.startswith('ask_invalid_answer_'):
        reasons = {'missing':'missing response', 'json':'invalid JSON', 'shape':'invalid answer shape',
                   'length':'answer length outside 1-6000 characters'}
        reason = reasons[mode[len('ask_invalid_answer_'):]]
        print(json.dumps({'error':f'Question returned an invalid answer ({reason}); no automatic retry',
                          'error_kind':'provider', 'error_stage':'provider.request'}), flush=True)
        sys.exit(1)
    if mode == 'ask_shop_lookup':
        print(json.dumps({'menus_query': {'id': 1, 'arguments': {'category': 'shops'}}}), flush=True)
        reply = sys.stdin.readline()
        if not reply or json.loads(reply) == {'cancel': True}: sys.exit(1)
        assert json.loads(reply)['menus_result']['id'] == 1
    if mode == 'ask_menu_pages':
        for seq in range(1, 9):
            print(json.dumps({'menus_query': {'id': seq, 'arguments': {'category': 'glamour_plates'}}}), flush=True)
            reply = sys.stdin.readline()
            if not reply or json.loads(reply) == {'cancel': True}: sys.exit(1)
            assert json.loads(reply)['menus_result']['id'] == seq
    if mode in ('ask_history_lookup', 'ask_game_context_lookup', 'ask_currency_lookup', 'ask_additional_lookup'):
        kinds = ('history', 'quest', 'inventory')
        if mode == 'ask_game_context_lookup': kinds = ('game_context',) + kinds
        if mode == 'ask_currency_lookup': kinds = ('currency',) + kinds
        if mode == 'ask_additional_lookup': kinds = ('game_definitions', 'gc_supply', 'npc_target', 'storage', 'collection', 'progression', 'crafting_log', 'menus', 'activities')
        for seq, kind in enumerate(kinds, 1):
            print(json.dumps({kind + '_query': {'id': seq, 'arguments': {}}}), flush=True)
            reply = sys.stdin.readline()
            if not reply: sys.exit(1)
            parsed = json.loads(reply)
            if parsed == {'cancel': True}: sys.exit(0)
            assert parsed[kind + '_result']['id'] == seq
    if mode in ('ask_stream_hold', 'ask_stream_cancel'):
        print(json.dumps({'question_partial': '😀' * 6000}), flush=True)
    if mode in ('ask_hold', 'ask_stream_hold'):
        for _ in range(500):
            if (folder / 'release.flag').exists(): break
            time.sleep(.01)
    if mode in ('ask_mixed_lookup', 'ask_mixed_replay'):
        for kind, seq in [('inventory', 1), ('quest', 1 if mode == 'ask_mixed_replay' else 2)]:
            print(json.dumps({kind + '_query': {'id': seq, 'arguments': {}}}), flush=True)
            reply = sys.stdin.readline()
            if not reply: sys.exit(1)
            parsed = json.loads(reply)
            if parsed == {'cancel': True}: sys.exit(0)
            assert parsed[kind + '_result']['id'] == seq
    if mode in ('ask_cancel', 'ask_stream_cancel'):
        (folder / 'cancel-ready.flag').write_text('ready', encoding='utf-8')
        sys.stdin.readline()
        sys.exit(0)
    if mode in ('ask_lookup', 'ask_lookup_replay'):
        query = {'quest_query': {'id': 1, 'arguments':
                 {'view': 'active', 'query': '', 'ids': [], 'offset': 0, 'limit': 30}}}
        print(json.dumps(query), flush=True)
        reply = sys.stdin.readline()
        if not reply: sys.exit(1)
        (folder / 'quest-reply.txt').write_text(reply, encoding='utf-8')
        if json.loads(reply) == {'cancel': True}: sys.exit(0)
        assert json.loads(reply)['quest_result']['id'] == 1
        if mode == 'ask_lookup_replay':
            print(json.dumps(query), flush=True)
            sys.stdin.readline()
            sys.exit(1)
    answer = {'question_answer':'Answer [guide](https://na.finalfantasyxiv.com/lodestone/playguide/)',
              'question_context':'Scope: synthetic fixture',
              'question_sources':[{'title':'Guide','url':'https://na.finalfantasyxiv.com/lodestone/playguide/'}]}
    if provider == 'grok': answer['quota_before'] = status['quota']
    if mode == 'ask_model':
        selected = json.loads(request)
        answer.update(question_model=selected['model'], question_effort=selected['reasoning_effort'])
    print(json.dumps(answer), flush=True)
    sys.exit(1 if mode == 'ask_fail' else 0)
if action == 'login':
    print(json.dumps({'login':'completed','account':status['account']}), flush=True)
if action == 'logout':
    status['account'] = {'type':None,'planType':None}
    status['quota_guard'] = 'blocked'
    print(json.dumps({'logout':'completed'}), flush=True)
if mode == 'provider_duplicate_tag': print(json.dumps(status)[:-1] + ',"provider":"grok"}', flush=True)
elif mode != 'login_no_status': print(json.dumps(status), flush=True)
""";
}

sealed class FakePluginInterface(string assemblyPath, string configDirectory)
{
    public FileInfo AssemblyLocation { get; } = new(assemblyPath);
    public string GetPluginConfigDirectory() => configDirectory;
}

namespace CompAI
{
    internal static class Plugin
    {
        internal static global::FakePluginInterface PluginInterface { get; set; } = null!;
    }
}

namespace Dalamud.Utility
{
    internal static class Util
    {
        public static readonly List<string> OpenedLinks = new();
        public static void OpenLink(string url) => OpenedLinks.Add(url);
    }
}
