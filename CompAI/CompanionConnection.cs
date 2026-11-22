using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Utility;

namespace CompAI;

internal readonly record struct CompanionView(string State, string Account, string Quota,
    string LoginUrl, string Answer, string QuestionAnswer, bool Busy, bool CanTest, bool IsSignedIn,
    string? SendBlockReason, IReadOnlyList<QuestionSource> QuestionSources, string? SourceOpenError,
    Action<string> OpenQuestionSource, IReadOnlyList<ChatTurn> History, string PendingQuestion,
    string ActiveChatId, IReadOnlyList<ChatSummary> Chats, string? StorageError, string? RetryQuestion,
    string PartialAnswer, string PendingPersonalityName, string PythonPrerequisite,
    bool ActiveChatBusy, IReadOnlyList<string> RunningChatIds)
{
    internal int ActiveRequestCount => this.RunningChatIds.Count;
    internal string PendingAttachmentMetadata { get; init; } = "";
    internal string RequestStatus { get; init; } = "";
    internal string RequestDiagnostics { get; init; } = "";
    internal string RetryState { get; init; } = "";
    internal bool AccountStateIsCurrent { get; init; }
    internal string? ConnectionError { get; init; }
    internal string? PreviousRequestError { get; init; }

    internal string StatusSummary(bool hasMessages) => this.ActiveChatBusy
        ? this.RequestStatus.Length > 0 ? this.RequestStatus : "Working..."
        : !this.IsSignedIn ? "Sign in from Settings"
        : this.ActiveRequestCount >= 2 ? "Two chats are answering; wait or cancel one."
        : this.RetryQuestion is not null
            // Refreshing the account does not retry the old question. Keep its error and
            // retry text, but do not present that old outcome as the current connection.
            ? this.AccountStateIsCurrent
                ? this.CanTest && !hasMessages ? "Ready — retry available" : "Attention — click for details"
                : this.RetryState.Contains("cancelled", StringComparison.OrdinalIgnoreCase)
                    ? "Cancelled — click for details" : "Request failed — click for details"
        : hasMessages ? "Attention — click for details"
        : this.ActiveRequestCount > 0 ? "Another chat is working..." : "Ready";

    internal string PreviousRequestDetail => this.RetryQuestion is not null && this.AccountStateIsCurrent && this.RetryState.Length > 0
        ? "Previous request: " + this.RetryState : "";
}

internal sealed record GameChatMessage(string Text, string Speaker, string? PersonalityId = null);

internal readonly record struct QuestionSource(string Title, string Url, bool IsBundled = false);
internal readonly record struct ChatContextInfo(int IncludedExchanges, int TotalExchanges, int Bytes, string? Warning);

internal sealed partial class CompanionConnection : IDisposable
{
    private sealed class ReaderPageBudgetException : Exception;
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private const int MaxConcurrentQuestions = 2;
    private readonly Dictionary<string, Operation> questions = new(StringComparer.Ordinal);
    private Operation? accountOperation;
    private long operationSerial;
    // Set only around the synchronous callback invocation. It follows that callback's
    // execution context, never the currently selected chat or another request.
    private readonly AsyncLocal<Operation?> callbackOperation = new();
    private readonly Func<string, CancellationToken, Task<string>> preparePython;
    private readonly Func<int> responseLimitMinutes;
    private string pythonPrerequisite = "Embedded Python not checked; refresh account status to check the installation.";
    private bool disposed;
    private bool busy;
    private bool canTest;
    private bool accountRefreshRequired;
    private bool isSignedIn;
    private bool statusObserved;
    private string currentProvider = "codex";
    private string state = "Disconnected; check status to start companion";
    private string? connectionError;
    private string account = "Unknown";
    private string quota = "Unknown";
    private string? quotaGuard;
    private string? quotaBlockReason;
    private DateTimeOffset? quotaRefreshAt;
    private DateTimeOffset? lastAutomaticQuotaRefresh;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly string profileDirectory;
    private string loginUrl = "";
    private string loginUserCode = "";
    private bool loginUrlPendingOpen;
    private string answer = "Not run";
    private string? sourceOpenError;
    private readonly ChatStore chats;
    private readonly Timer draftSaveTimer;
    private long draftEditTimestamp;
    private ChatHistory history => this.chats.ActiveHistory;
    private int chatRevision;
    private bool gameChatReplies;
    private readonly Queue<GameChatMessage> gameChatMessages = new();
    // Retry only retains question text for this session, never a previous request
    // or reader snapshot. Sending again rebuilds context and checks current access.
    private readonly Dictionary<string, string> retryQuestions = new();
    private IReadOnlyList<CompanionModelOption> modelOptions = Array.Empty<CompanionModelOption>();
    private string? modelDiscoveryError;
    private AllowanceWindow[] allowanceWindows = Array.Empty<AllowanceWindow>();
    private DateTimeOffset? allowanceCheckedAt;
    private string? allowanceObservation;

    private sealed class Operation(string action, string? chatId, long serial, string provider, int responseTimeoutSeconds)
    {
        internal readonly string action = action;
        internal readonly string? chatId = chatId;
        internal readonly long serial = serial;
        internal readonly string provider = provider;
        internal readonly int responseTimeoutSeconds = responseTimeoutSeconds;
        internal readonly SemaphoreSlim inputGate = new(1, 1);
        internal Process? process;
        internal CancellationTokenSource? preparationCancellation;
        internal bool busy = true;
        internal bool cancelRequested;
        internal bool initialRequestWritten;
        internal bool questionCompleted;
        internal bool answerSaved;
        internal bool questionInvalidated;
        internal bool questionIncludesInventory;
        internal bool questionIncludesGameContext;
        internal bool questionGameChatReplies;
        internal bool statusObserved;
        internal bool localLogoutCompleted;
        internal bool statusAllowsTest;
        internal bool testPassed;
        internal string? failureKind;
        internal string state = "Question starting";
        private string? errorState;
        internal string? CurrentError => this.errorState == this.state ? this.errorState : null;
        internal void SetError(string message) => this.state = this.errorState = message;
        internal string pendingQuestion = "";
        internal string partialAnswer = "";
        internal string questionAnswer = "No question sent";
        internal QuestionSource[] questionSources = Array.Empty<QuestionSource>();
        internal string? submittedDraft;
        internal string? questionContext;
        internal string questionPersonalityName = "Companion";
        internal string? questionPersonalityId;
        internal long questionStarted;
        internal bool firstAnswerMeasured;
        internal PerformanceDiagnostics.RequestMetrics? metrics;
        internal Guid? inventoryFindKey;
        internal Guid? shopFindKey;
        internal ChatAttachment[] attachments = Array.Empty<ChatAttachment>();
    }

    private Operation? ActiveQuestion => this.questions.GetValueOrDefault(this.chats.ActiveId);
    private bool ActiveChatBusy => this.ActiveQuestion?.busy == true;
    private bool AccountBusy => this.accountOperation?.busy == true;
    private int RunningQuestionCount => this.questions.Values.Count(operation => operation.busy);

    internal CompanionConnection(Func<CompanionModelSettings?>? defaultModel = null,
        Func<DateTimeOffset>? utcNow = null, Func<PersonalityPreset>? defaultPersonality = null,
        string? configurationDirectory = null, string? profileDirectory = null,
        Func<string, CancellationToken, Task<string>>? preparePython = null, Func<int>? responseLimitMinutes = null)
    {
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.preparePython = preparePython ?? CompanionPythonRuntime.PrepareAsync;
        this.responseLimitMinutes = responseLimitMinutes ?? (() => 0);
        configurationDirectory ??= Plugin.PluginInterface.GetPluginConfigDirectory();
        this.profileDirectory = profileDirectory ?? Path.Combine(configurationDirectory, "companion");
        var preferredProvider = defaultModel?.Invoke()?.Provider;
        if (CompanionModelSelection.IsProvider(preferredProvider)) this.currentProvider = preferredProvider!;
        this.chats = new ChatStore(Path.Combine(configurationDirectory, "chats"),
            clock: this.utcNow, defaultModelSelection: defaultModel, defaultPersonality: defaultPersonality,
            exportDirectory: Path.Combine(configurationDirectory, "exports", "chats"));
        this.draftSaveTimer = new Timer(_ =>
        {
            lock (this.gate)
            {
                if (this.disposed) return;
                var remaining = ChatStore.DraftDebounce - Stopwatch.GetElapsedTime(this.draftEditTimestamp);
                if (remaining > TimeSpan.Zero)
                    this.draftSaveTimer!.Change(remaining, Timeout.InfiniteTimeSpan);
                else this.chats.SaveActive();
            }
        }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    internal IReadOnlyList<CompanionModelOption> ModelOptions { get { lock (this.gate) return this.modelOptions; } }
    internal string? ModelDiscoveryError { get { lock (this.gate) return this.modelDiscoveryError; } }
    internal CompanionModelSettings? ChatModelSelection { get { lock (this.gate) return this.chats.ActiveModelSelection; } }
    internal string? ChatModelError { get { lock (this.gate) return this.chats.ActiveModelError; } }
    internal string CurrentProvider { get { lock (this.gate) return this.currentProvider; } }
    internal string ActiveChatId { get { lock (this.gate) return this.chats.ActiveId; } }
    internal string LoginUserCode { get { lock (this.gate) return this.loginUserCode; } }
    internal static string ProviderLabel(string provider) => provider switch
        { "codex" => "ChatGPT / Codex", "copilot" => "GitHub Copilot", "grok" => "Grok (Beta)", _ => "Unknown provider" };
    internal static string? ProviderUnavailableReason(string provider) => provider == "copilot"
        ? $"{ProviderLabel(provider)} is awaiting authenticated free-account validation. Its integration remains disabled."
        : null;

    internal bool SetProvider(string provider)
    {
        lock (this.gate)
        {
            if (this.disposed || this.busy || this.AccountBusy || this.RunningQuestionCount != 0 ||
                !CompanionModelSelection.IsProvider(provider) || ProviderUnavailableReason(provider) is not null) return false;
            if (provider == this.currentProvider) return true;
            this.currentProvider = provider;
            this.MarkAccountUnverified();
            this.ClearLoginLink();
            this.accountRefreshRequired = false;
            this.statusObserved = false;
            this.lastAutomaticQuotaRefresh = null;
            this.accountOperation = null;
            this.state = "Provider changed; checking account status";
            ++this.chatRevision;
            return this.StartCore("status", null);
        }
    }

    internal bool SetChatModelSelection(CompanionModelSettings selection)
    {
        lock (this.gate)
        {
            if (this.disposed || this.ActiveChatBusy || this.AccountBusy || selection.Provider != this.currentProvider ||
                !CompanionModelSelection.IsSupported(this.modelOptions, selection)) return false;
            if (!this.chats.SetActiveModelSelection(selection)) return false;
            ++this.chatRevision;
            return true;
        }
    }

    internal PersonalityPreset ChatPersonality { get { lock (this.gate) return this.chats.ActivePersonality; } }
    internal string? ChatPersonalityError { get { lock (this.gate) return this.chats.ActivePersonalityError; } }
    internal string? ChatPersonalityBlockReason
    {
        get { lock (this.gate) return this.GetPersonalityBlockReason(); }
    }

    // A failed replacement retains a valid prior selection and must not become a send block.
    private string? GetPersonalityBlockReason() => this.chats.ActivePersonalityIsValid ? null :
        "This chat has no valid saved personality. Choose a personality from Context before sending.";

    internal bool SetChatPersonality(PersonalityPreset selection)
    {
        lock (this.gate)
        {
            if (this.disposed || this.ActiveChatBusy || this.AccountBusy || !this.chats.SetActivePersonality(selection)) return false;
            ++this.chatRevision;
            return true;
        }
    }

    internal (int Revision, ChatExchange[] Exchanges, string Notes, ChatTurn[] Turns, PersonalityPreset Personality, string ChatId) HistoryContext
    {
        get { lock (this.gate) return (this.chatRevision, this.history.Context(), this.chats.ActiveNotes,
            this.history.Export(), this.chats.ActivePersonality, this.chats.ActiveId); }
    }

    internal ChatContextInfo ContextInfo
    {
        get
        {
            lock (this.gate)
            {
                var info = this.history.ContextStats;
                var warning = info.OmissionReason switch
                {
                    "latest_exchange_excerpt" => "The latest reply is included as a labelled excerpt; text between its beginning and ending is omitted. The full saved reply remains available in portions through older-message lookup when enabled, within the usual tool limits.",
                    "history_byte_budget" => "Older exchanges are still saved, but are outside the recent-history size budget.",
                    _ => null,
                };
                return new(info.IncludedCount, info.TotalCount, info.Bytes, warning);
            }
        }
    }

    internal string ChatNotes { get { lock (this.gate) return this.chats.ActiveNotes; } }
    internal string? ChatNotesError { get { lock (this.gate) return this.chats.ActiveNotesError; } }
    internal bool SaveChatNotes(string notes)
    {
        lock (this.gate)
        {
            if (this.disposed || this.ActiveChatBusy || this.AccountBusy || !this.chats.SetActiveNotes(notes)) return false;
            ++this.chatRevision;
            return true;
        }
    }

    internal string AllowanceSummary { get { lock (this.gate) return this.FormatAllowance(false); } }
    internal string AllowanceDetails { get { lock (this.gate) return this.FormatAllowance(true); } }

    internal int HistoryRevision
    {
        get { lock (this.gate) return this.chatRevision; }
    }

    internal string Draft
    {
        get { lock (this.gate) return this.chats.ActiveDraft; }
        set
        {
            lock (this.gate)
            {
                if (this.disposed || value == this.chats.ActiveDraft) return;
                this.chats.SetDraft(value);
                this.draftEditTimestamp = Stopwatch.GetTimestamp();
                this.draftSaveTimer.Change(ChatStore.DraftDebounce, Timeout.InfiniteTimeSpan);
            }
        }
    }

    internal ChatStorageInfo StorageInfo { get { lock (this.gate) return this.chats.StorageInfo; } }
    internal bool AutoSortChats { get { lock (this.gate) return this.chats.AutoSortChats; } }
    internal string? ChatOrderError { get { lock (this.gate) return this.chats.ChatOrderError; } }

    internal bool SetAutoSortChats(bool enabled)
    {
        lock (this.gate) return !this.disposed && this.chats.SetAutoSortChats(enabled);
    }

    internal bool MoveChatBefore(string id, string? beforeId)
    {
        lock (this.gate) return !this.disposed && this.chats.MoveChatBefore(id, beforeId);
    }

    internal bool RenameChat(string id, string? title)
    {
        lock (this.gate) return !this.disposed && this.chats.RenameChat(id, title);
    }

    internal bool SetChatPinned(string id, bool pinned)
    {
        lock (this.gate) return !this.disposed && this.chats.SetPinned(id, pinned);
    }

    internal ChatSearchResult SearchChats(string? query)
    {
        lock (this.gate) return this.chats.SearchChats(query);
    }

    internal ChatDeleteResult DeleteSelectedChats(IEnumerable<string> ids)
    {
        lock (this.gate)
        {
            var selected = ids.Distinct(StringComparer.Ordinal).ToArray();
            if (this.disposed || this.AccountBusy)
                return new(Array.Empty<string>(), selected.Select(id => new ChatDeleteFailure(id, "Account action in progress; try again when it finishes.")).ToArray());
            var result = this.chats.DeleteSelected(selected, this.questions.Where(pair => pair.Value.busy).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal));
            foreach (var id in result.DeletedIds)
            {
                this.ClearAttachmentState(id);
                this.retryQuestions.Remove(id);
                this.questions.Remove(id);
            }
            if (result.DeletedIds.Count > 0) this.OnChatSelected();
            return result;
        }
    }

    internal string? ExportActiveChat()
    {
        lock (this.gate) return this.disposed ? null : this.chats.ExportActiveChat();
    }

    internal void FlushDraft()
    {
        lock (this.gate)
        {
            if (this.disposed) return;
            this.draftSaveTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            this.chats.SaveActive();
        }
    }

    internal void SetGameChatReplies(bool enabled)
    {
        lock (this.gate)
        {
            this.gameChatReplies = enabled;
            if (!enabled) this.DiscardGameChatReplies();
        }
    }

    // The framework drains these messages even when both plugin windows are closed.
    internal GameChatMessage? TakeGameChatMessage()
    {
        lock (this.gate)
            return !this.disposed && this.gameChatReplies && this.gameChatMessages.TryDequeue(out var message) ? message : null;
    }

    internal void DiscardGameChatReplies()
    {
        lock (this.gate)
        {
            this.gameChatMessages.Clear();
            foreach (var operation in this.questions.Values) operation.questionGameChatReplies = false;
        }
    }

    private void QueueGameChatMessage(Operation operation, string message, bool answer = false)
    {
        if (!this.gameChatReplies || !operation.questionGameChatReplies || this.disposed) return;
        if (this.gameChatMessages.Count >= 8) this.gameChatMessages.Dequeue();
        this.gameChatMessages.Enqueue(new(message, answer ? operation.questionPersonalityName : "CompAI",
            answer ? operation.questionPersonalityId : null));
    }

    internal CompanionView View
    {
        get
        {
            lock (this.gate)
            {
                // A reset is a reason to refresh, never proof that quota is available.
                if (!this.disposed && !this.busy && this.quotaRefreshAt is { } due && this.utcNow() >= due)
                {
                    this.lastAutomaticQuotaRefresh = this.utcNow();
                    this.StartCore("status", null);
                }
                var operation = this.ActiveQuestion;
                var showAccountState = this.AccountBusy || this.accountOperation is { } account &&
                    account.serial > (operation?.serial ?? 0);
                var displayed = showAccountState ? this.accountOperation : operation;
                var retry = this.retryQuestions.GetValueOrDefault(this.chats.ActiveId);
                return new(displayed?.state ?? this.state,
                    this.account, this.quota, this.loginUrl, this.answer,
                    operation?.questionAnswer ?? this.history.View.LastOrDefault()?.Answer ?? "No question sent",
                    this.busy, this.canTest && !this.accountRefreshRequired && !this.AccountBusy && !this.ActiveChatBusy && this.RunningQuestionCount < MaxConcurrentQuestions,
                    this.isSignedIn, this.GetSendBlockReason(),
                    Array.AsReadOnly((QuestionSource[])(operation?.questionSources.Clone() ?? Array.Empty<QuestionSource>())), this.sourceOpenError,
                    this.OpenQuestionSource, this.history.View, operation?.pendingQuestion ?? "",
                    this.chats.ActiveId, this.chats.View, this.chats.Error,
                    retry, operation?.partialAnswer ?? "",
                    operation?.questionPersonalityName ?? "Companion", this.pythonPrerequisite,
                    this.ActiveChatBusy, Array.AsReadOnly(this.questions.Where(pair => pair.Value.busy).Select(pair => pair.Key).ToArray()))
                {
                    PendingAttachmentMetadata = ChatAttachment.Metadata(operation?.attachments ?? Array.Empty<ChatAttachment>()),
                    RequestStatus = operation?.metrics?.Status ?? "",
                    RequestDiagnostics = operation?.metrics?.Report ?? "",
                    RetryState = operation?.state ?? "",
                    AccountStateIsCurrent = showAccountState,
                    ConnectionError = displayed is null ? (this.state == this.connectionError ? this.connectionError : null) : displayed.CurrentError,
                    PreviousRequestError = showAccountState && retry is not null ? operation?.CurrentError : null,
                };
            }
        }
    }

    // Called only while holding gate.
    private string? GetSendBlockReason() => this.AccountBusy ? "Account action in progress; wait or cancel it before sending." :
        this.ActiveChatBusy ? "This chat is answering; wait or cancel its request before sending." :
        this.RunningQuestionCount >= MaxConcurrentQuestions ? "Two chats are answering; wait or cancel one before sending." :
        this.GetProviderBlockReason() ?? this.GetAccountBlockReason() ?? this.GetPersonalityBlockReason() ?? this.chats.StorageInfo.SendBlockReason;

    internal string? SyntheticTestBlockReason { get { lock (this.gate) return this.currentProvider != "codex"
        ? "The bundled synthetic connection test is available for Codex only."
        : this.busy ? "Companion action in progress; wait or cancel it first." : this.GetAccountBlockReason(); } }

    private string? GetProviderBlockReason() => this.chats.ActiveModelSelection is { } selected && selected.Provider != this.currentProvider
        ? $"This chat uses {ProviderLabel(selected.Provider)}. Switch back to that provider, or choose a {ProviderLabel(this.currentProvider)} model beside Send to change this chat."
        : null;

    private string? GetAccountBlockReason()
    {
        if (ProviderUnavailableReason(this.currentProvider) is { } unavailable) return unavailable;
        if (this.accountRefreshRequired) return "A companion request failed validation; refresh account status before sending.";
        if (this.canTest) return null;
        if (this.quotaGuard == "blocked")
            return this.quotaBlockReason ?? (this.allowanceWindows.Length > 0
                ? "Companion quota guard blocked model requests; refresh status for details."
                : "Companion quota is unavailable or blocked; refresh account status.");
        if (this.statusObserved && !this.isSignedIn) return $"Sign in with {ProviderLabel(this.currentProvider)} before sending.";
        if (!this.isSignedIn) return "Refresh account status or sign in before sending.";
        return "Quota status is unavailable; refresh account status before sending.";
    }

    internal void Start(string action)
    {
        if (action is not ("status" or "login" or "logout" or "test"))
            throw new ArgumentOutOfRangeException(nameof(action));
        this.StartCore(action, null);
    }

    internal bool StartAsk(string requestJson, bool includeInventory, string question, bool includeQuests = false,
        Func<JsonElement, string>? questQuery = null, Func<JsonElement, string>? inventoryQuery = null,
        bool includeHistoryLookup = false, Func<JsonElement, string>? historyQuery = null,
        bool includeGameContext = false, Func<JsonElement, string>? gameContextQuery = null,
        Func<JsonElement, string>? npcDialogueQuery = null, Func<JsonElement, string>? characterDetailsQuery = null,
        Func<JsonElement, string>? personalityDetailsQuery = null, bool replyInGameChat = false,
        Func<JsonElement, string>? currencyQuery = null, Func<JsonElement, string>? dutyQuery = null,
        string? expectedChatId = null, IReadOnlyList<ChatAttachment>? attachments = null,
        Func<JsonElement, string>? gameDefinitionsQuery = null, Func<JsonElement, string>? gcSupplyQuery = null,
        Func<JsonElement, string>? npcTargetQuery = null, Func<JsonElement, string>? storageQuery = null,
        Func<JsonElement, string>? valuationQuery = null, Func<JsonElement, string>? collectionQuery = null,
        Func<JsonElement, string>? progressionQuery = null, Func<JsonElement, string>? craftingLogQuery = null, Func<JsonElement, string>? menuQuery = null, Func<JsonElement, string>? activityQuery = null) =>
        this.StartCore("ask", requestJson, includeInventory, question, includeQuests, questQuery, inventoryQuery,
            includeHistoryLookup, historyQuery, includeGameContext, gameContextQuery, npcDialogueQuery, characterDetailsQuery,
            personalityDetailsQuery, replyInGameChat, currencyQuery, dutyQuery, expectedChatId, attachments,
            gameDefinitionsQuery, gcSupplyQuery, npcTargetQuery, storageQuery, valuationQuery, collectionQuery, progressionQuery, craftingLogQuery, menuQuery, activityQuery);

    // Capture on the background tool callback, then check again immediately before
    // queued framework reads. A later question must not reactivate old work.
    internal Func<bool> CaptureQuestionContinuation()
    {
        Operation? operation;
        lock (this.gate) operation = this.callbackOperation.Value ?? this.ActiveQuestion;
        return () =>
        {
            lock (this.gate)
                return operation is { busy: true, cancelRequested: false, questionInvalidated: false } &&
                    operation.chatId is { } id && ReferenceEquals(this.questions.GetValueOrDefault(id), operation) && !this.disposed;
        };
    }

    private bool StartCore(string action, string? requestJson, bool includeInventory = false, string question = "", bool includeQuests = false,
        Func<JsonElement, string>? questQuery = null, Func<JsonElement, string>? inventoryQuery = null,
        bool includeHistoryLookup = false, Func<JsonElement, string>? historyQuery = null,
        bool includeGameContext = false, Func<JsonElement, string>? gameContextQuery = null,
        Func<JsonElement, string>? npcDialogueQuery = null, Func<JsonElement, string>? characterDetailsQuery = null,
        Func<JsonElement, string>? personalityDetailsQuery = null, bool replyInGameChat = false,
        Func<JsonElement, string>? currencyQuery = null, Func<JsonElement, string>? dutyQuery = null,
        string? expectedChatId = null, IReadOnlyList<ChatAttachment>? attachments = null,
        Func<JsonElement, string>? gameDefinitionsQuery = null, Func<JsonElement, string>? gcSupplyQuery = null,
        Func<JsonElement, string>? npcTargetQuery = null, Func<JsonElement, string>? storageQuery = null,
        Func<JsonElement, string>? valuationQuery = null, Func<JsonElement, string>? collectionQuery = null,
        Func<JsonElement, string>? progressionQuery = null, Func<JsonElement, string>? craftingLogQuery = null, Func<JsonElement, string>? menuQuery = null, Func<JsonElement, string>? activityQuery = null)
    {
        Operation operation;
        lock (this.gate)
        {
            if (ProviderUnavailableReason(this.currentProvider) is { } unavailable)
            {
                this.state = this.connectionError = unavailable;
                this.canTest = false;
                return false;
            }
            if (this.disposed || (action == "ask" && expectedChatId is not null && expectedChatId != this.chats.ActiveId) ||
                (action == "ask" ? this.GetSendBlockReason() is not null : this.busy) ||
                (action is "test" or "ask" && (!this.canTest || this.accountRefreshRequired)) ||
                (action == "test" && this.currentProvider != "codex") ||
                (action == "ask" && !RequestMatchesProvider(requestJson, this.currentProvider)) ||
                (action == "logout" && this.currentProvider == "codex" && !this.isSignedIn))
                return false;
            var responseSeconds = 300;
            if (action == "ask" && !this.TryResolveResponseTimeout(requestJson, out responseSeconds))
            {
                this.state = this.connectionError = "Question timing could not be prepared; check the response limit and try again.";
                return false;
            }
            operation = new Operation(action, action == "ask" ? this.chats.ActiveId : null, ++this.operationSerial,
                this.currentProvider, responseSeconds);
            if (operation.chatId is { } chatId) this.questions[chatId] = operation;
            else this.accountOperation = operation;
            this.busy = true;
            if (action == "ask")
            {
                operation.metrics = PerformanceDiagnostics.BeginQuestion();
                operation.questionStarted = Stopwatch.GetTimestamp();
                operation.firstAnswerMeasured = false;
            }
            if (action != "ask") this.canTest = false;
            if (action is "status" or "login" or "logout")
            {
                this.statusObserved = false;
                this.quotaGuard = null;
                this.quotaBlockReason = null;
                this.quotaRefreshAt = null;
                this.modelOptions = Array.Empty<CompanionModelOption>();
                this.modelDiscoveryError = null;
                this.isSignedIn = false;
                this.account = "Checking";
                this.quota = "Checking";
                this.ClearAllowance();
            }
            operation.state = action switch
            {
                "test" => "Synthetic test starting",
                "ask" => "Question starting",
                "logout" => this.currentProvider == "codex" ? "Signing out of companion" : "Clearing saved sign-in",
                _ => "Companion connecting",
            };
            this.ClearLoginLink();
            if (action == "test")
                this.answer = "Pending";
            if (action == "ask")
            {
                // Freeze delivery when sending; opening the plugin later must not redirect a reply.
                operation.questionGameChatReplies = this.gameChatReplies && replyInGameChat;
                var personality = this.chats.ActivePersonality;
                operation.questionPersonalityId = personality.Id;
                operation.questionPersonalityName = personality.Id == PersonalityPresets.None.Id ? "Companion" : personality.Name;
                this.AttachmentsStarted(operation, attachments);
                this.retryQuestions.Remove(this.chats.ActiveId);
                operation.questionIncludesInventory = includeInventory;
                operation.questionIncludesGameContext = includeInventory || includeQuests || includeGameContext ||
                    npcDialogueQuery is not null || characterDetailsQuery is not null || currencyQuery is not null || dutyQuery is not null ||
                    gcSupplyQuery is not null || npcTargetQuery is not null || storageQuery is not null || valuationQuery is not null ||
                    collectionQuery is not null || progressionQuery is not null || craftingLogQuery is not null || menuQuery is not null || activityQuery is not null;
                operation.pendingQuestion = question;
                operation.submittedDraft = this.chats.ActiveDraft.Trim() == question ? this.chats.ActiveDraft : null;
                if (operation.submittedDraft is not null) this.chats.SetDraft("");
                operation.questionContext = null;
                operation.questionAnswer = "Pending";
                operation.questionSources = Array.Empty<QuestionSource>();
                this.sourceOpenError = null;
            }
            if (action == "logout")
            {
                this.answer = "Not run";
                this.sourceOpenError = null;
                this.chats.SaveActive();
            }
        }
        _ = Task.Run(() => this.RunAsync(operation, requestJson, includeQuests ? questQuery : null,
            includeInventory ? inventoryQuery : null, includeHistoryLookup ? historyQuery : null,
            includeGameContext ? gameContextQuery : null, npcDialogueQuery, characterDetailsQuery, personalityDetailsQuery, currencyQuery, dutyQuery,
            gameDefinitionsQuery, gcSupplyQuery, npcTargetQuery, storageQuery, valuationQuery, collectionQuery, progressionQuery, craftingLogQuery, menuQuery, activityQuery));
        return true;
    }

    private bool TryResolveResponseTimeout(string? requestJson, out int seconds)
    {
        seconds = 0;
        if (requestJson is null) return false;
        try
        {
            using var document = JsonDocument.Parse(requestJson);
            var request = document.RootElement;
            if (request.ValueKind != JsonValueKind.Object) return false;
            var wrappers = request.EnumerateObject().Where(property => property.Name == "request").ToArray();
            if (wrappers.Length > 1) return false;
            if (wrappers.Length == 1) request = wrappers[0].Value;
            if (request.ValueKind != JsonValueKind.Object) return false;
            var efforts = request.EnumerateObject().Where(property => property.Name == "reasoning_effort").ToArray();
            if (efforts.Length > 1 || efforts.Length == 1 && efforts[0].Value.ValueKind != JsonValueKind.String)
                return false;
            // Resolve once from the submitted request, before background preparation starts.
            seconds = CompanionResponseTimeout.ResolveSeconds(this.responseLimitMinutes(),
                efforts.Length == 1 ? efforts[0].Value.GetString() : null);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentOutOfRangeException) { return false; }
    }

    internal static TimeSpan ProcessTimeout(string action, int responseSeconds) => action switch
    {
        "ask" => TimeSpan.FromSeconds(responseSeconds + CompanionResponseTimeout.OuterGraceSeconds),
        "login" => TimeSpan.FromMinutes(6),
        "status" or "logout" => TimeSpan.FromSeconds(30),
        _ => TimeSpan.FromMinutes(3),
    };

    private static bool RequestMatchesProvider(string? requestJson, string provider)
    {
        if (requestJson is null) return false;
        try
        {
            using var document = JsonDocument.Parse(requestJson);
            var request = document.RootElement;
            if (request.ValueKind != JsonValueKind.Object) return false;
            if (request.TryGetProperty("request", out var inner)) request = inner;
            if (request.ValueKind != JsonValueKind.Object) return false;
            var fields = request.EnumerateObject().Where(property => property.Name == "provider").ToArray();
            if (fields.Length == 1)
                return fields[0].Value.ValueKind == JsonValueKind.String && fields[0].Value.GetString() == provider;
            return fields.Length == 0 && provider == "codex" &&
                (!request.TryGetProperty("version", out var version) || version.ValueKind == JsonValueKind.Number &&
                    version.TryGetInt32(out var number) && number < 21);
        }
        catch (JsonException) { return false; }
    }

    private async Task RunAsync(Operation operation, string? requestJson, Func<JsonElement, string>? questQuery,
        Func<JsonElement, string>? inventoryQuery, Func<JsonElement, string>? historyQuery,
        Func<JsonElement, string>? gameContextQuery, Func<JsonElement, string>? npcDialogueQuery,
        Func<JsonElement, string>? characterDetailsQuery, Func<JsonElement, string>? personalityDetailsQuery,
        Func<JsonElement, string>? currencyQuery, Func<JsonElement, string>? dutyQuery,
        Func<JsonElement, string>? gameDefinitionsQuery, Func<JsonElement, string>? gcSupplyQuery,
        Func<JsonElement, string>? npcTargetQuery, Func<JsonElement, string>? storageQuery, Func<JsonElement, string>? valuationQuery,
        Func<JsonElement, string>? collectionQuery, Func<JsonElement, string>? progressionQuery, Func<JsonElement, string>? craftingLogQuery, Func<JsonElement, string>? menuQuery, Func<JsonElement, string>? activityQuery)
    {
        var action = operation.action;
        using var operationTiming = action == "ask" ? (PerformanceDiagnostics.TimingScope?)null : PerformanceDiagnostics.Measure(action switch
        {
            "ask" => "client.question", "test" => "client.synthetic", _ => "client.account",
        });
        Process? local = null;
        CancellationTokenSource? preparation = null;
        var stage = "installation";
        var answerSaved = false;
        try
        {
            // Assembly.Location may be empty when Dalamud loads the plugin from memory.
            var assemblyPath = Plugin.PluginInterface.AssemblyLocation.FullName;
            var folder = Path.GetDirectoryName(assemblyPath);
            if (string.IsNullOrWhiteSpace(folder))
                throw CompanionPythonRuntime.Failure("installation.path");
            var script = Path.Combine(folder, "companion", "companion.py");
            if (!File.Exists(script))
                throw CompanionPythonRuntime.Failure("installation");
            lock (this.gate)
            {
                if (this.disposed || operation.cancelRequested) throw new OperationCanceledException();
                preparation = CancellationTokenSource.CreateLinkedTokenSource(this.lifetime.Token);
                operation.preparationCancellation = preparation;
                this.pythonPrerequisite = "Checking bundled Python integrity and standard library...";
            }
            stage = "python.prerequisite";
            var executable = await this.preparePython(Path.GetDirectoryName(script)!, preparation.Token);
            lock (this.gate)
            {
                if (this.disposed || operation.cancelRequested) throw new OperationCanceledException();
                this.pythonPrerequisite = CompanionPythonRuntime.Ready;
                operation.preparationCancellation = null;
            }
            stage = "profile.storage";
            var profileDir = this.profileDirectory;
            Directory.CreateDirectory(profileDir);
            lock (this.gate)
                if (operation.cancelRequested)
                    throw new OperationCanceledException();
            stage = "bridge.start";
            var start = CompanionPythonRuntime.CreateStartInfo(executable, Path.GetDirectoryName(script)!);
            start.ArgumentList.Add(script);
            start.ArgumentList.Add("--profile-dir");
            start.ArgumentList.Add(profileDir);
            start.ArgumentList.Add("--provider");
            start.ArgumentList.Add(operation.provider);
            if (action == "ask")
            {
                start.ArgumentList.Add("--response-timeout-seconds");
                start.ArgumentList.Add(operation.responseTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            start.ArgumentList.Add("bridge");
            start.ArgumentList.Add(action);
            local = new Process { StartInfo = start };
            if (!local.Start())
                throw CompanionPythonRuntime.Failure(stage);
            _ = CompanionPythonRuntime.DiscardErrorAsync(local.StandardError);
            stage = "bridge.protocol";
            lock (this.gate)
            {
                if (this.disposed || operation.cancelRequested)
                    throw new OperationCanceledException();
                operation.process = local;
            }
            if (action == "ask")
            {
                if (requestJson is null)
                    throw new InvalidOperationException("Question request missing");
                lock (this.gate)
                    if (operation.cancelRequested)
                        throw new OperationCanceledException();
                await this.WriteInputAsync(operation, local, requestJson, this.lifetime.Token);
                lock (this.gate)
                {
                    operation.initialRequestWritten = true;
                    if (operation.cancelRequested)
                        throw new OperationCanceledException();
                }
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(this.lifetime.Token);
            timeout.CancelAfter(ProcessTimeout(action, operation.responseTimeoutSeconds));
            var queryCount = 0;
            var ordinaryQueryCount = 0;
            var valuationQueryCount = 0;
            var resultBytes = 0;
            var valuationBytes = 0;
            string? valuationCategories = null;
            var output = new CompanionLineReader(local.StandardOutput);
            while (true)
            {
                var line = await output.ReadLineAsync(timeout.Token);
                if (line is null)
                    break;
                using (var message = JsonDocument.Parse(line))
                {
                    var root = message.RootElement;
                    if (root.ValueKind == JsonValueKind.Object &&
                        (root.TryGetProperty("quest_query", out _) || root.TryGetProperty("inventory_query", out _) ||
                         root.TryGetProperty("history_query", out _) || root.TryGetProperty("game_context_query", out _) ||
                         root.TryGetProperty("npc_dialogue_query", out _) || root.TryGetProperty("character_details_query", out _) ||
                         root.TryGetProperty("personality_details_query", out _) || root.TryGetProperty("currency_query", out _) ||
                         root.TryGetProperty("duty_query", out _) || root.TryGetProperty("collection_query", out _) ||
                         root.TryGetProperty("progression_query", out _) || root.TryGetProperty("crafting_log_query", out _) || root.TryGetProperty("menus_query", out _) || root.TryGetProperty("activities_query", out _) ||
                         root.TryGetProperty("game_definitions_query", out _) ||
                         root.TryGetProperty("gc_supply_query", out _) || root.TryGetProperty("npc_target_query", out _) ||
                         root.TryGetProperty("storage_query", out _) || root.TryGetProperty("valuation_query", out _)))
                    {
                        var kind = root.TryGetProperty("inventory_query", out _) ? "inventory" :
                            root.TryGetProperty("history_query", out _) ? "history" :
                            root.TryGetProperty("game_context_query", out _) ? "game_context" :
                            root.TryGetProperty("npc_dialogue_query", out _) ? "npc_dialogue" :
                            root.TryGetProperty("character_details_query", out _) ? "character_details" :
                            root.TryGetProperty("personality_details_query", out _) ? "personality_details" :
                            root.TryGetProperty("currency_query", out _) ? "currency" :
                            root.TryGetProperty("duty_query", out _) ? "duty" :
                            root.TryGetProperty("collection_query", out _) ? "collection" :
                            root.TryGetProperty("progression_query", out _) ? "progression" :
                            root.TryGetProperty("crafting_log_query", out _) ? "crafting_log" :
                            root.TryGetProperty("menus_query", out _) ? "menus" :
                            root.TryGetProperty("activities_query", out _) ? "activities" :
                            root.TryGetProperty("game_definitions_query", out _) ? "game_definitions" :
                            root.TryGetProperty("gc_supply_query", out _) ? "gc_supply" :
                            root.TryGetProperty("npc_target_query", out _) ? "npc_target" :
                            root.TryGetProperty("storage_query", out _) ? "storage" :
                            root.TryGetProperty("valuation_query", out _) ? "valuation" : "quest";
                        var query = root.GetProperty(kind + "_query");
                        var lookup = kind switch { "inventory" => inventoryQuery, "history" => historyQuery,
                            "game_context" => gameContextQuery, "npc_dialogue" => npcDialogueQuery,
                            "character_details" => characterDetailsQuery, "personality_details" => personalityDetailsQuery,
                            "currency" => currencyQuery, "duty" => dutyQuery, "collection" => collectionQuery, "progression" => progressionQuery, "crafting_log" => craftingLogQuery, "menus" => menuQuery, "activities" => activityQuery,
                            "game_definitions" => gameDefinitionsQuery,
                            "gc_supply" => gcSupplyQuery, "npc_target" => npcTargetQuery, "storage" => storageQuery,
                            "valuation" => valuationQuery, _ => questQuery };
                        lock (this.gate)
                            if (this.disposed || operation.cancelRequested || operation.questionInvalidated)
                                throw new OperationCanceledException();
                        if (action != "ask" || lookup is null || root.EnumerateObject().Count() != 1 ||
                            query.ValueKind != JsonValueKind.Object || query.EnumerateObject().Count() != 2 ||
                            !query.TryGetProperty("id", out var id) || !id.TryGetInt32(out var queryId) ||
                            queryId != queryCount + 1 ||
                            !query.TryGetProperty("arguments", out var arguments))
                            throw new InvalidDataException("Unexpected or excessive context lookup");
                        ++queryCount;
                        var valuation = kind == "valuation";
                        var maxResultBytes = 20000;
                        if (valuation)
                        {
                            // Only this private channel transports a compact complete local
                            // snapshot. It never extends the ordinary reader/model budgets.
                            if (++valuationQueryCount > 2 || arguments.ValueKind != JsonValueKind.Object ||
                                !arguments.TryGetProperty("view", out var view) || view.ValueKind != JsonValueKind.String ||
                                view.GetString() != (valuationQueryCount == 1 ? "candidates" : "details") ||
                                !arguments.TryGetProperty("categories", out var categories) || categories.ValueKind != JsonValueKind.Array)
                                throw new InvalidDataException("Unexpected valuation lookup sequence");
                            var categoryKey = JsonSerializer.Serialize(categories.EnumerateArray().Select(c => c.GetString())
                                .Order(StringComparer.Ordinal).ToArray());
                            if (valuationQueryCount == 1) valuationCategories = categoryKey;
                            else if (valuationCategories != categoryKey) throw new InvalidDataException("Valuation scope changed");
                            maxResultBytes = valuationQueryCount == 1 ? ValuationLookup.MaxCandidateBytes : ValuationLookup.MaxDetailBytes;
                        }
                        else if (++ordinaryQueryCount > 12)
                            throw new InvalidDataException("Unexpected or excessive context lookup");
                        string result;
                        try
                        {
                            this.callbackOperation.Value = operation;
                            result = lookup(arguments);
                        }
                        catch (OperationCanceledException)
                        {
                            lock (this.gate) operation.failureKind = "permission";
                            throw;
                        }
                        catch (Exception ex) when (ex is TimeoutException or IOException)
                        {
                            // Only ordinary reader failures become unavailable data.
                            // Cancellation, permission failures and invalid results stop the request.
                            result = "{\"reader_unavailable\":true}";
                        }
                        finally { this.callbackOperation.Value = null; }
                        var bytes = Encoding.UTF8.GetByteCount(result);
                        if (valuation) valuationBytes += bytes;
                        else resultBytes += bytes;
                        if (bytes > maxResultBytes || (valuation && valuationBytes > ValuationLookup.MaxCombinedBytes))
                            throw new InvalidDataException("Context lookup exceeded response budget");
                        using var resultDocument = JsonDocument.Parse(result);
                        // Valid bounded pages can exhaust the cumulative data allowance.
                        // That is an ordinary request limit, not corrupt transport/authentication.
                        // Never send the over-budget page; malformed/oversized pages still fail closed above.
                        if (!valuation && resultBytes > 120000)
                            throw new ReaderPageBudgetException();
                        var reply = JsonSerializer.Serialize(new Dictionary<string, object>
                        {
                            [kind + "_result"] = new { id = queryId, result = resultDocument.RootElement },
                        });
                        await this.WriteInputAsync(operation, local, reply, timeout.Token);
                        continue;
                    }
                }
                this.ApplyMessage(operation, line);
            }
            await local.WaitForExitAsync(timeout.Token);
            lock (this.gate)
            {
                if (this.disposed)
                    return;
                if (operation.cancelRequested)
                {
                    if (action == "test")
                        this.MarkSyntheticTestCancelled(operation);
                    else if (action == "ask")
                        this.MarkQuestionCancelled(operation);
                    else
                    {
                        operation.state = "Companion cancelled; refresh account status";
                        this.MarkAccountUnverified(operation);
                    }
                }
                else if (local.ExitCode != 0 || operation.failureKind is not null)
                {
                    if (!operation.state.StartsWith("Companion refused:", StringComparison.Ordinal))
                        operation.SetError(CompanionPythonRuntime.Failure("bridge.protocol").SafeMessage);
                    this.MarkOperationFailed(operation, operation.failureKind ?? "protocol");
                    if (action == "test")
                        this.answer = "No answer";
                    if (action == "ask")
                        operation.questionAnswer = "No answer";
                }
                else if (action == "test")
                {
                    this.canTest = operation.testPassed && this.isSignedIn;
                    if (!operation.testPassed)
                        operation.SetError("Synthetic test returned no verified result");
                }
                else if (action == "ask")
                {
                    if (!operation.questionCompleted)
                    {
                        operation.SetError("Companion returned no completed question answer");
                        operation.questionAnswer = "No answer";
                    }
                    else if (!operation.questionInvalidated)
                    {
                        answerSaved = this.chats.AppendAnswer(operation.chatId!, operation.pendingQuestion, operation.questionAnswer,
                            operation.questionSources, string.Join("\n", new[] { operation.questionContext, ChatAttachment.Metadata(operation.attachments) }
                                .Where(value => !string.IsNullOrEmpty(value))), operation.questionIncludesInventory, operation.questionPersonalityName,
                            operation.inventoryFindKey, operation.shopFindKey);
                        ++this.chatRevision;
                        if (answerSaved)
                        {
                            operation.answerSaved = true;
                            operation.submittedDraft = null;
                            this.QueueGameChatMessage(operation, operation.questionAnswer, answer: true);
                        }
                    }
                    this.canTest = this.isSignedIn && this.quotaGuard != "blocked";
                }
                else if (operation.localLogoutCompleted)
                {
                    // A local removal is not a verified runtime/account status.
                    this.MarkAccountUnverified(operation);
                    this.statusObserved = false;
                    this.accountRefreshRequired = true;
                    this.account = "No saved Grok sign-in";
                    this.quota = "Unavailable until signed in";
                    operation.state = "Saved Grok sign-in cleared locally; sign in to reconnect.";
                }
                else if (operation.statusObserved)
                {
                    this.canTest = operation.statusAllowsTest;
                    this.accountRefreshRequired = false;
                }
                else
                {
                    operation.SetError("Companion returned no account status");
                    this.MarkAccountUnverified(operation);
                }
            }
        }
        catch (OperationCanceledException)
        {
            lock (this.gate)
                if (!this.disposed)
                {
                    if (stage == "python.prerequisite")
                        this.pythonPrerequisite = "Python prerequisite check cancelled; refresh account status to check again.";
                    if (action == "test" && operation.cancelRequested)
                        this.MarkSyntheticTestCancelled(operation);
                    else if (action == "ask" && operation.cancelRequested)
                        this.MarkQuestionCancelled(operation);
                    else
                    {
                        operation.SetError("Companion cancelled or timed out; no automatic retry");
                        this.MarkOperationFailed(operation, operation.failureKind ?? "provider");
                        if (action == "test")
                            this.answer = "No answer";
                        if (action == "ask")
                            operation.questionAnswer = "No answer";
                    }
                }
        }
        catch (ReaderPageBudgetException)
        {
            lock (this.gate)
                if (!this.disposed)
                {
                    operation.SetError("This question reached its data lookup limit. Try fewer items or plates; no automatic retry.");
                    operation.failureKind = "reader";
                    this.MarkOperationFailed(operation, "reader");
                    if (action == "ask") operation.questionAnswer = "No answer";
                }
        }
        catch (Exception ex)
        {
            lock (this.gate)
                if (!this.disposed)
                {
                    var failure = ex as CompanionSetupException ?? CompanionPythonRuntime.Failure(stage);
                    operation.SetError(failure.SafeMessage);
                    if (failure.Stage.StartsWith("python.", StringComparison.Ordinal))
                        this.pythonPrerequisite = failure.SafeMessage;
                    this.MarkOperationFailed(operation, ex is UnauthorizedAccessException ? "permission" : "protocol");
                    if (action == "test")
                        this.answer = "No answer";
                    if (action == "ask")
                        operation.questionAnswer = "No answer";
                }
        }
        finally
        {
            if (local is not null)
            {
                try { if (!local.HasExited) local.Kill(entireProcessTree: true); }
                catch (Exception) { }
                local.Dispose();
            }
            lock (this.gate)
            {
                if (ReferenceEquals(operation.preparationCancellation, preparation))
                    operation.preparationCancellation = null;
                if (action == "ask")
                {
                    if (!answerSaved)
                        this.QueueGameChatMessage(operation, operation.state + ". Open /compai for details.");
                    if (!this.disposed && !answerSaved && operation.pendingQuestion.Length > 0)
                        this.retryQuestions[operation.chatId!] = operation.pendingQuestion;
                    // Restore a failed/cancelled send only if the user has not started another draft.
                    if (!this.disposed && !answerSaved && operation.submittedDraft is { } unsent)
                        this.chats.RestoreDraftIfEmpty(operation.chatId!, unsent);
                    operation.submittedDraft = null;
                    this.AttachmentsFinished(operation, answerSaved);
                }
                if (ReferenceEquals(operation.process, local))
                    operation.process = null;
                if (action == "login")
                    this.ClearLoginLink();
                operation.metrics?.Complete(answerSaved ? "Completed" : operation.cancelRequested ? "Cancelled" : "Failed");
                operation.metrics = null;
                operation.busy = false;
                this.busy = this.AccountBusy || this.RunningQuestionCount > 0;
                if (action != "ask") this.state = operation.state;
                operation.pendingQuestion = "";
                operation.partialAnswer = "";
                operation.questionGameChatReplies = false;
            }
            preparation?.Dispose();
        }
    }

    private async Task WriteInputAsync(Operation operation, Process target, string line, CancellationToken token, bool allowCancelled = false)
    {
        await operation.inputGate.WaitAsync(token);
        try
        {
            Task write;
            lock (this.gate)
            {
                if (!allowCancelled && (this.disposed || operation.cancelRequested || operation.questionInvalidated || !ReferenceEquals(operation.process, target)))
                    throw new OperationCanceledException();
                // Start the write before releasing the cancellation gate; later writes cannot overtake Cancel.
                write = target.StandardInput.WriteLineAsync(line.AsMemory(), token);
            }
            await write;
            await target.StandardInput.FlushAsync(token);
        }
        finally { operation.inputGate.Release(); }
    }

    private void ApplyMessage(Operation operation, string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Invalid companion message");
        lock (this.gate)
        {
            if (this.disposed)
                return;
            if (operation.provider != this.currentProvider || root.EnumerateObject().Count(property => property.Name == "provider") > 1 ||
                root.TryGetProperty("provider", out var messageProvider) &&
                (messageProvider.ValueKind != JsonValueKind.String || messageProvider.GetString() != operation.provider))
                throw new InvalidDataException("Companion provider does not match the operation");
            if (root.TryGetProperty("performance", out var performance) && root.EnumerateObject().Count() == 1)
            {
                // Final cancellation telemetry may arrive while its owning operation is still
                // cleaning up, but replaced/completed operations cannot contaminate another request.
                if (operation.busy && operation.action == "ask" && operation.chatId is { } owner &&
                    ReferenceEquals(this.questions.GetValueOrDefault(owner), operation))
                    operation.metrics?.Accept(performance);
                return;
            }
            if (operation.cancelRequested) return;
            if (operation.failureKind == "authentication") return;
            if (root.TryGetProperty("local_logout", out var localLogout))
            {
                if (operation.action != "logout" || operation.provider != "grok" ||
                    root.EnumerateObject().Count() != 2 ||
                    !root.TryGetProperty("provider", out var localProvider) ||
                    localProvider.ValueKind != JsonValueKind.String || localProvider.GetString() != "grok" ||
                    localLogout.ValueKind != JsonValueKind.True || operation.localLogoutCompleted ||
                    operation.failureKind is not null || operation.statusObserved)
                    throw new InvalidDataException("Invalid local sign-out result");
                operation.localLogoutCompleted = true;
                return;
            }
            if (operation.localLogoutCompleted)
                throw new InvalidDataException("Unexpected output after local sign-out");
            if (operation.action == "logout" && operation.provider == "grok" && !root.TryGetProperty("error", out _))
                throw new InvalidDataException("Missing local sign-out result");
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Invalid companion failure");
                var kind = root.TryGetProperty("error_kind", out var errorKind) && errorKind.ValueKind == JsonValueKind.String
                    ? errorKind.GetString() : "protocol";
                if (kind is not ("authentication" or "quota" or "provider" or "reader" or "protocol" or "permission"))
                    throw new InvalidDataException("Invalid companion failure category");
                // Keep the first failure, and never allow later output to save an answer.
                operation.failureKind ??= kind;
                if (kind == "authentication") operation.failureKind = kind;
                if (operation.action is "ask" or "test" && kind is "protocol" or "permission")
                    this.accountRefreshRequired = true;
                operation.questionCompleted = false;
                operation.partialAnswer = "";
                var hasStage = root.TryGetProperty("error_stage", out var suppliedStage);
                if (hasStage && suppliedStage.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Invalid companion failure stage");
                var errorStage = hasStage ? suppliedStage.GetString() : null;
                if (errorStage is not (null or "runtime.integrity" or "runtime.containment" or "provider.start" or
                    "provider.authentication" or "provider.quota" or "provider.request"))
                    throw new InvalidDataException("Invalid companion failure stage");
                operation.SetError("Companion refused: " + (errorStage is null ? "" : $"[{errorStage}] ") + error.GetString());
                if (operation.action == "ask") operation.questionAnswer = "No answer";
                else if (operation.action == "test") this.answer = "No answer";
                if (operation.action == "ask") operation.questionSources = Array.Empty<QuestionSource>();
                this.ClearLoginLink();
                if (kind == "authentication") this.MarkAccountUnverified(operation);
                return;
            }
            if (operation.failureKind is not null &&
                (root.TryGetProperty("question_answer", out _) || root.TryGetProperty("question_partial", out _) ||
                 root.TryGetProperty("question_state", out _)))
                return;
            if (operation.action == "ask" && root.TryGetProperty("question_partial", out var partial))
            {
                if (partial.ValueKind != JsonValueKind.String || root.EnumerateObject().Count() != 1)
                    throw new InvalidDataException("Invalid partial answer");
                var text = partial.GetString()!;
                if (text.Length > 12000 || text.EnumerateRunes().Count() > 6000 || text.Contains('\0'))
                    throw new InvalidDataException("Partial answer exceeded bounds");
                if (!operation.questionCompleted && !operation.questionInvalidated)
                {
                    if (!string.IsNullOrWhiteSpace(text)) this.MeasureFirstAnswer(operation);
                    operation.partialAnswer = text;
                }
                return;
            }
            if (operation.action == "ask" && root.TryGetProperty("question_state", out var questionState))
            {
                operation.state = "Question: " + questionState.GetString();
                return;
            }
            if (operation.action == "ask" && root.TryGetProperty("question_answer", out var questionAnswer))
            {
                var answerText = questionAnswer.ValueKind == JsonValueKind.String ? questionAnswer.GetString() : null;
                var hasAnswer = !string.IsNullOrWhiteSpace(answerText);
                // Keep the pending row at its final height until the owner history is
                // appended on successful process exit; never flash a blank working row.
                operation.partialAnswer = hasAnswer ? answerText! : "";
                if (hasAnswer) this.MeasureFirstAnswer(operation);
                operation.questionAnswer = hasAnswer ? answerText! : "No answer";
                operation.questionContext = root.TryGetProperty("question_context", out var context) &&
                    context.ValueKind == JsonValueKind.String ? context.GetString() : null;
                if (root.TryGetProperty("question_model", out var usedModel) && usedModel.ValueKind == JsonValueKind.String &&
                    root.TryGetProperty("question_effort", out var usedEffort) && usedEffort.ValueKind == JsonValueKind.String &&
                    usedModel.GetString() is { Length: > 0 and <= 128 } modelName && !modelName.Contains('\0') &&
                    usedEffort.GetString() is { Length: > 0 and <= 32 } effortName && !effortName.Contains('\0'))
                    operation.questionContext = (string.IsNullOrEmpty(operation.questionContext) ? "" : operation.questionContext + "\n") +
                        $"Model: {modelName}; reasoning: {effortName}.";
                operation.questionSources = hasAnswer ? ReadQuestionSources(root, answerText!) : Array.Empty<QuestionSource>();
                operation.questionCompleted = hasAnswer;
                operation.state = "Question answered";
                if (root.TryGetProperty("quota_before", out var questionQuota))
                {
                    this.quota = FormatQuota(questionQuota);
                    this.ReadAllowance(questionQuota, "Before the last reply; that reply may have used more allowance.");
                }
                return;
            }
            if (root.TryGetProperty("runtime_pin", out var pin) && pin.GetString() == "verified")
            {
                if (operation.provider != "codex" && !root.TryGetProperty("provider", out _))
                    throw new InvalidDataException("Missing companion provider identity");
                operation.statusObserved = true;
                this.statusObserved = true;
                operation.state = "Runtime pin verified";
                if (root.TryGetProperty("account", out var accountValue))
                {
                    this.account = FormatAccount(accountValue, operation.provider);
                    this.quota = "Unavailable until signed in";
                    this.ClearAllowance();
                    this.isSignedIn = accountValue.TryGetProperty("type", out var reportedType) &&
                        reportedType.ValueKind == JsonValueKind.String &&
                        reportedType.GetString() == (operation.provider == "codex" ? "chatgpt" : operation.provider);
                    if (!this.isSignedIn)
                        foreach (var other in this.questions.Values.Where(other => other.busy && !ReferenceEquals(other, operation)))
                            this.CancelOperation(other);
                    operation.state = this.isSignedIn ? "Connected (runtime pin verified)" : "Signed out (runtime pin verified)";
                    if (this.isSignedIn)
                        this.ClearLoginLink();
                }
                if (root.TryGetProperty("quota", out var quotaValue))
                {
                    this.quota = FormatQuota(quotaValue);
                    if (this.isSignedIn) this.ReadAllowance(quotaValue, "Account status snapshot; other chats may use the same allowance.");
                }
                if (root.TryGetProperty("quota_error", out var quotaError))
                    this.quota = quotaError.GetString() ?? "Unavailable";
                this.quotaGuard = root.TryGetProperty("quota_guard", out var guard) ? guard.GetString() : null;
                this.quotaBlockReason = root.TryGetProperty("quota_block_reason", out var blockReason) &&
                    blockReason.ValueKind == JsonValueKind.String ? blockReason.GetString() : null;
                this.ScheduleQuotaRefresh(root);
                if (this.quotaGuard == "blocked")
                    this.quota += "; model requests blocked";
                operation.statusAllowsTest = this.isSignedIn &&
                    root.TryGetProperty("quota_guard", out var quotaGuard) && quotaGuard.GetString() == "available";
                this.ReadModels(root, operation.provider);
                return;
            }
            if (root.TryGetProperty("login", out var login))
            {
                var loginState = login.GetString();
                operation.state = "Login: " + loginState;
                if (loginState is "completed" or "already connected")
                    this.ClearLoginLink();
                if (loginState == "waiting for user" && root.TryGetProperty("authUrl", out var url))
                {
                    var candidate = url.GetString() ?? "";
                    if (!IsAllowedLoginUrl(candidate, operation.provider))
                        throw new InvalidDataException("Unexpected provider login URL");
                    var openBrowser = true;
                    if (root.TryGetProperty("openBrowser", out var browserChoice))
                    {
                        if (root.EnumerateObject().Count(property => property.Name == "openBrowser") != 1 ||
                            browserChoice.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            throw new InvalidDataException("Invalid provider browser choice");
                        openBrowser = browserChoice.GetBoolean();
                    }
                    this.loginUrl = candidate;
                    // Some native providers open the browser themselves. Keep the
                    // manual link available without launching the same page twice.
                    this.loginUrlPendingOpen = openBrowser;
                    if (root.TryGetProperty("user_code", out var code))
                    {
                        var value = code.ValueKind == JsonValueKind.String ? code.GetString() : null;
                        if (value is not { Length: > 0 and <= 32 } || !value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
                            throw new InvalidDataException("Invalid provider login code");
                        this.loginUserCode = value;
                    }
                }
                if (root.TryGetProperty("account", out var loginAccount))
                    this.account = FormatAccount(loginAccount, operation.provider);
            }
            if (root.TryGetProperty("browser", out var browser))
            {
                operation.state = browser.GetString() ?? "Browser did not open";
                if (operation.state is "Browser did not open" or "Browser did not open; use the official URL shown above")
                    operation.SetError(operation.state);
            }
            if (root.TryGetProperty("logout", out var logout))
                operation.state = "Companion logout: " + logout.GetString();
            if (root.TryGetProperty("proof", out var proof))
                operation.state = "Synthetic test: " + proof.GetString();
            if (root.TryGetProperty("passed", out var passed))
            {
                var ok = passed.ValueKind == JsonValueKind.True;
                operation.testPassed = ok;
                if (ok) operation.state = "Synthetic test passed";
                else operation.SetError("Synthetic test failed");
                this.answer = root.TryGetProperty("answer", out var value) ? value.GetRawText() : "No answer";
                if (root.TryGetProperty("quota_before", out var before))
                {
                    this.quota = FormatQuota(before);
                    this.ReadAllowance(before, "Before the synthetic test; later usage is not included.");
                }
            }
        }
    }

    // Called only while holding gate.
    private void MeasureFirstAnswer(Operation operation)
    {
        if (operation.firstAnswerMeasured || operation.questionStarted == 0) return;
        operation.firstAnswerMeasured = true;
        operation.metrics?.Record("client.first_answer", Stopwatch.GetElapsedTime(operation.questionStarted).TotalMilliseconds);
        operation.metrics?.SetActivity("answer");
    }

    private void ClearLoginLink()
    {
        this.loginUrl = "";
        this.loginUserCode = "";
        this.loginUrlPendingOpen = false;
    }

    // Called only while holding gate. Discovery errors must not invalidate a verified account.
    private void ReadModels(JsonElement root, string provider)
    {
        this.modelOptions = Array.Empty<CompanionModelOption>();
        this.modelDiscoveryError = null;
        if (!this.isSignedIn) return;
        if (!root.TryGetProperty("models", out var models))
        {
            this.modelDiscoveryError = root.TryGetProperty("models_error", out var error) && error.ValueKind == JsonValueKind.String
                ? error.GetString() : "Model choices unavailable; refresh account status.";
            return;
        }
        try
        {
            if (models.ValueKind != JsonValueKind.Array || models.GetArrayLength() > 64)
                throw new InvalidDataException();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var parsed = new List<CompanionModelOption>();
            foreach (var value in models.EnumerateArray())
            {
                var id = value.GetProperty("id").GetString();
                var name = value.GetProperty("name").GetString();
                var preferred = value.GetProperty("default_effort").GetString();
                if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Contains('\0') || !seen.Add(id) ||
                    string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Contains('\0'))
                    throw new InvalidDataException();
                var options = value.GetProperty("efforts");
                if (options.ValueKind != JsonValueKind.Array || options.GetArrayLength() is < 1 or > 8)
                    throw new InvalidDataException();
                var efforts = new List<string>();
                foreach (var option in options.EnumerateArray())
                {
                    var effort = option.GetString();
                    if (!(effort is "none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max" or "ultra" ||
                          provider != "codex" && effort == "default") ||
                        efforts.Contains(effort)) throw new InvalidDataException();
                    efforts.Add(effort);
                }
                if (preferred is null || !efforts.Contains(preferred) ||
                    !CompanionModelSelection.IsValid(new(id, preferred, provider))) throw new InvalidDataException();
                var supportsImages = value.TryGetProperty("supports_images", out var imageSupport) && imageSupport.ValueKind == JsonValueKind.True;
                parsed.Add(new(id, name, efforts.AsReadOnly(), preferred, supportsImages, provider));
            }
            this.modelOptions = parsed.AsReadOnly();
            if (parsed.Count == 0) this.modelDiscoveryError = "No compatible model choices were reported; refresh account status.";
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            this.modelDiscoveryError = "The companion returned invalid model choices; refresh account status.";
        }
    }

    // Called only while holding gate. Request failure does not prove sign-out.
    // Every explicit new request still performs a fresh account/quota preflight.
    private void MarkOperationFailed(Operation operation, string kind)
    {
        operation.partialAnswer = "";
        operation.questionSources = Array.Empty<QuestionSource>();
        if (operation.action is "ask" or "test" && kind is "protocol" or "permission")
            this.accountRefreshRequired = true;
        if (operation.action is not ("ask" or "test") || kind == "authentication")
        {
            this.MarkAccountUnverified(operation);
            return;
        }
        if (kind == "quota")
        {
            this.canTest = operation.statusObserved && operation.statusAllowsTest;
            if (!operation.statusObserved)
            {
                this.quotaGuard = "blocked";
                this.quotaBlockReason = "Companion allowance is unavailable or exhausted; refresh status before sending.";
                this.quota = "Unavailable; refresh status";
                this.ClearAllowance();
                // A failed follow-up status read has no reset deadline. Retry only
                // account status after a bounded delay, never the failed question.
                this.quotaRefreshAt = this.utcNow().AddMinutes(5);
                if (this.lastAutomaticQuotaRefresh?.AddMinutes(5) is { } floor && floor > this.quotaRefreshAt)
                    this.quotaRefreshAt = floor;
            }
            return;
        }
        this.canTest = this.isSignedIn && kind is ("reader" or "provider");
        if (!operation.statusObserved)
        {
            this.quota = "Unknown after request failure; checked before next request";
            this.ClearAllowance();
        }
    }

    // A failed or cancelled auth operation may have changed the stored session.
    private void MarkAccountUnverified(Operation? owner = null)
    {
        foreach (var other in this.questions.Values.Where(other => other.busy && !ReferenceEquals(other, owner)))
            this.CancelOperation(other);
        this.account = "Unknown; refresh status";
        this.quota = "Unknown; refresh status";
        this.ClearAllowance();
        this.isSignedIn = false;
        this.canTest = false;
        this.modelOptions = Array.Empty<CompanionModelOption>();
        this.modelDiscoveryError = null;
        this.quotaGuard = null;
        this.quotaBlockReason = null;
        this.quotaRefreshAt = null;
    }

    // Shared allowance windows are refreshed once after reset, with a five-minute retry floor
    // for stale deadlines. This never sends or retries a model question.
    private void ScheduleQuotaRefresh(JsonElement root)
    {
        this.quotaRefreshAt = null;
        if (!this.isSignedIn || this.quotaGuard != "blocked") return;
        var now = this.utcNow();
        var deadlines = new List<(DateTimeOffset Reset, bool Exhausted)>();
        if (root.TryGetProperty("quota", out var windows) && windows.ValueKind == JsonValueKind.Object)
            foreach (var name in new[] { "primary", "secondary" })
            {
                if (!windows.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object ||
                    !window.TryGetProperty("resetsAt", out var reset) || reset.ValueKind != JsonValueKind.Number ||
                    !reset.TryGetInt64(out var seconds) || seconds < 0 || seconds > 253402300794) continue;
                var exhausted = window.TryGetProperty("usedPercent", out var used) &&
                    used.ValueKind == JsonValueKind.Number && used.TryGetDouble(out var percent) && percent >= 100;
                deadlines.Add((DateTimeOffset.FromUnixTimeSeconds(seconds), exhausted));
            }
        var reached = deadlines.Where(value => value.Exhausted).ToArray();
        var candidate = reached.Length > 0 ? reached.Max(value => value.Reset).AddSeconds(5) :
            deadlines.Count > 0 ? deadlines.Min(value => value.Reset).AddSeconds(5) : now.AddMinutes(5);
        var floor = this.lastAutomaticQuotaRefresh?.AddMinutes(5) ?? now.AddSeconds(5);
        this.quotaRefreshAt = candidate > floor ? candidate : floor;
    }

    // Called only while holding gate. A fresh preflight guards the next test request.
    private void MarkSyntheticTestCancelled(Operation operation)
    {
        operation.state = "Synthetic test cancelled; next run rechecks login and quota";
        this.answer = "No answer";
        this.quota = "Unknown after cancellation; checked before next test";
        this.canTest = this.isSignedIn;
    }

    // Called only while holding gate.
    private void MarkQuestionCancelled(Operation operation)
    {
        operation.partialAnswer = "";
        operation.state = "Question cancelled; next request rechecks login and quota";
        operation.questionAnswer = "No answer";
        operation.questionSources = Array.Empty<QuestionSource>();
        this.quota = "Unknown after cancellation; checked before next request";
        this.canTest = this.isSignedIn && this.quotaGuard != "blocked";
    }

    internal void InvalidateQuestion()
    {
        lock (this.gate)
        {
            // Completed messages are historical records. Clear only the live attachment,
            // and cancel a currently running request that used it; never erase a chat.
            foreach (var operation in this.questions.Values.Where(operation => operation.busy && operation.questionIncludesGameContext))
            {
                operation.questionInvalidated = true;
                this.CancelOperation(operation);
            }
        }
    }

    internal bool NewChat()
    {
        lock (this.gate)
        {
            if (this.AccountBusy || this.disposed) return false;
            var previousId = this.chats.ActiveId;
            if (this.chats.NewChat() == previousId) return false;
            this.OnChatSelected();
            return true;
        }
    }

    internal bool SelectChat(string id)
    {
        lock (this.gate)
        {
            if (this.AccountBusy || this.disposed || !this.chats.Select(id)) return false;
            this.OnChatSelected();
            return true;
        }
    }

    internal bool ContinuePersonalityChat(PersonalityPreset preset)
    {
        lock (this.gate)
        {
            if (this.AccountBusy || this.disposed || !this.chats.ContinuePersonalityChat(preset)) return false;
            this.OnChatSelected();
            return true;
        }
    }

    internal bool DeleteChat(string id)
    {
        lock (this.gate)
        {
            if (this.AccountBusy || this.disposed || this.questions.GetValueOrDefault(id)?.busy == true || !this.chats.Delete(id)) return false;
            this.ClearAttachmentState(id);
            this.retryQuestions.Remove(id);
            this.questions.Remove(id);
            this.OnChatSelected();
            return true;
        }
    }

    private void OnChatSelected()
    {
        ++this.chatRevision;
        this.sourceOpenError = null;
    }

    private static QuestionSource[] ReadQuestionSources(JsonElement root, string answerText)
    {
        var accepted = new List<QuestionSource>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("question_sources", out var sources) && sources.ValueKind == JsonValueKind.Array &&
            sources.GetArrayLength() <= 10)
        {
            foreach (var source in sources.EnumerateArray())
            {
                if (source.ValueKind != JsonValueKind.Object ||
                    !source.TryGetProperty("title", out var titleValue) || titleValue.ValueKind != JsonValueKind.String ||
                    !source.TryGetProperty("url", out var urlValue) || urlValue.ValueKind != JsonValueKind.String)
                    continue;
                var title = titleValue.GetString();
                var url = urlValue.GetString();
                if (string.IsNullOrWhiteSpace(title) || title.Length > 120 ||
                    string.IsNullOrWhiteSpace(url) || !IsAllowedBundledSourceUrl(url, out var uri) ||
                    !seen.Add(uri.AbsoluteUri))
                    continue;
                accepted.Add(new QuestionSource(title, uri.AbsoluteUri, IsBundled: true));
            }
        }
        foreach (var uri in AnswerLinks.Extract(answerText))
        {
            if (accepted.Count >= 20) break;
            if (seen.Add(uri.AbsoluteUri))
                accepted.Add(new QuestionSource(ChatHistory.ModelSourceTitle(uri), uri.AbsoluteUri));
        }
        return accepted.ToArray();
    }

    private static bool IsAllowedBundledSourceUrl(string url, out Uri uri) =>
        AnswerLinks.TryCanonicalize(url, out uri) && uri.IsDefaultPort &&
        uri.Host is "na.finalfantasyxiv.com" or "ffxiv.consolegameswiki.com";

    internal void OpenQuestionSource(string url)
    {
        lock (this.gate)
        {
            if (this.disposed || !AnswerLinks.TryCanonicalize(url, out var uri) ||
                (Array.FindIndex(this.ActiveQuestion?.questionSources ?? Array.Empty<QuestionSource>(), source => source.Url == uri.AbsoluteUri) < 0 &&
                 !this.history.ContainsSource(uri.AbsoluteUri) &&
                 !AnswerLinks.Extract(this.ActiveQuestion?.pendingQuestion ?? "").Any(link => link.AbsoluteUri == uri.AbsoluteUri)))
            {
                this.sourceOpenError = "Source link is no longer available.";
                return;
            }
        }
        try
        {
            Util.OpenLink(url);
            lock (this.gate) this.sourceOpenError = null;
        }
        catch (Exception)
        {
            lock (this.gate) this.sourceOpenError = "Could not open the source link in a browser.";
        }
    }

    private static string FormatAccount(JsonElement value, string provider)
    {
        if (!value.TryGetProperty("type", out var type) || type.ValueKind == JsonValueKind.Null)
            return "Not signed in";
        var plan = value.TryGetProperty("planType", out var planValue) ? planValue.GetString() : null;
        return type.GetString() == (provider == "codex" ? "chatgpt" : provider)
            ? $"{ProviderLabel(provider)} ({plan ?? "plan unknown"})" : "Unsupported account type";
    }

    private static string FormatQuota(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return "Unavailable";
        string Window(string name)
        {
            if (!value.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object ||
                !window.TryGetProperty("usedPercent", out var used) || used.ValueKind != JsonValueKind.Number ||
                !used.TryGetDouble(out var percent) || !double.IsFinite(percent) || percent < 0 || percent > 100)
                return name + ": unavailable";
            return name + ": " + (100 - percent).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "% remaining";
        }
        return Window("primary") + "; " + Window("secondary");
    }

    private sealed record AllowanceWindow(string Label, double Used, DateTimeOffset? Reset);

    private void ClearAllowance()
    {
        this.allowanceWindows = Array.Empty<AllowanceWindow>();
        this.allowanceCheckedAt = null;
        this.allowanceObservation = null;
    }

    private void ReadAllowance(JsonElement value, string observation)
    {
        this.ClearAllowance();
        if (value.ValueKind != JsonValueKind.Object) return;
        var windows = new List<AllowanceWindow>();
        foreach (var name in new[] { "primary", "secondary" })
        {
            if (!value.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object ||
                !window.TryGetProperty("usedPercent", out var used) || used.ValueKind != JsonValueKind.Number ||
                !used.TryGetDouble(out var percent) || !double.IsFinite(percent) || percent < 0 || percent > 100) continue;
            DateTimeOffset? resetAt = null;
            if (window.TryGetProperty("resetsAt", out var reset) && reset.ValueKind == JsonValueKind.Number &&
                reset.TryGetInt64(out var timestamp) && timestamp is >= 0 and <= 253402300799)
                resetAt = DateTimeOffset.FromUnixTimeSeconds(timestamp);
            var label = name == "primary" ? "Primary" : "Secondary";
            if (window.TryGetProperty("windowDurationMins", out var duration) && duration.ValueKind == JsonValueKind.Number &&
                duration.TryGetInt32(out var minutes) && minutes > 0)
                label = minutes % 1440 == 0 ? $"{minutes / 1440}d" : minutes % 60 == 0 ? $"{minutes / 60}h" : $"{minutes}m";
            windows.Add(new(label, percent, resetAt));
        }
        this.allowanceWindows = windows.ToArray();
        if (windows.Count == 0) return;
        this.allowanceCheckedAt = this.utcNow();
        this.allowanceObservation = observation;
    }

    private string FormatAllowance(bool details)
    {
        if (this.allowanceWindows.Length == 0)
            return details ? "Allowance unavailable; refresh account status in Settings." : "Left: —";
        var now = this.utcNow();
        if (!details)
            return "Left: " + string.Join(" · ", this.allowanceWindows.Select(window =>
                $"{window.Label} {(100 - window.Used).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}%" +
                (window.Reset is { } reset && reset <= now ? "*" : "")));
        string FormatWindow(AllowanceWindow window)
        {
            var remaining = Math.Clamp(100 - window.Used, 0, 100).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            if (window.Reset is not { } reset) return $"{window.Label}: {remaining}% remaining; reset unknown";
            var until = reset - now;
            var resetText = until <= TimeSpan.Zero ? "reset due; refresh status" : until.TotalDays >= 1
                ? $"resets in {(int)until.TotalDays}d {until.Hours}h" : until.TotalHours >= 1
                ? $"resets in {(int)until.TotalHours}h {until.Minutes}m" : $"resets in {Math.Max(1, (int)Math.Ceiling(until.TotalMinutes))}m";
            return $"{window.Label}: {remaining}% remaining; {resetText} ({reset:yyyy-MM-dd HH:mm} UTC)";
        }
        var text = "Allowance (last reported): " + string.Join("\n", this.allowanceWindows.Select(FormatWindow));
        text += $"\nReceived {this.allowanceCheckedAt:yyyy-MM-dd HH:mm:ss} UTC. {this.allowanceObservation}" +
            "\nThis is shared subscription allowance, not an exact token count. A reset time alone does not confirm renewed availability.";
        if (this.allowanceWindows.Any(window => window.Reset is { } reset && reset <= now))
            text += "\n* The reported reset time has passed. This percentage is from before that reset; refresh account status for current usage.";
        return text;
    }

    internal void OpenPendingLoginUrl()
    {
        string url;
        string provider;
        lock (this.gate)
        {
            if (!this.loginUrlPendingOpen || this.disposed)
                return;
            this.loginUrlPendingOpen = false;
            url = this.loginUrl;
            provider = this.currentProvider;
        }
        if (!IsAllowedLoginUrl(url, provider))
        {
            lock (this.gate)
            {
                this.state = this.connectionError = "Rejected unexpected login URL";
                this.accountOperation?.SetError(this.state);
            }
            return;
        }
        try
        {
            Util.OpenLink(url);
        }
        catch (Exception)
        {
            lock (this.gate)
            {
                this.state = this.connectionError = "Browser could not open; copy the official login link below";
                this.accountOperation?.SetError(this.state);
            }
        }
    }

    internal static bool IsAllowedLoginUrl(string url, string provider = "codex")
    {
        if (url.Length > 8192 || url.Any(character => character <= 0x20 || character == 0x7F || character == '\\') ||
            !Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps ||
            parsed.UserInfo.Length != 0 || parsed.Port != 443 || !CompanionModelSelection.IsProvider(provider))
            return false;
        var allowed = provider switch
        {
            "codex" => parsed.Host is "auth.openai.com" or "auth.chatgpt.com" or "chatgpt.com",
            "copilot" => parsed.Host == "github.com" && parsed.AbsolutePath is "/login/device" or "/login/oauth" or "/login/oauth/authorize",
            "grok" => parsed.Host == "accounts.x.ai" && parsed.AbsolutePath == "/oauth2/device",
            _ => false,
        };
        if (!allowed) return false;
        // Uri.UserInfo is also empty for an explicitly empty username (https://@host).
        var start = url.IndexOf("://", StringComparison.Ordinal) + 3;
        var end = url.IndexOfAny(['/', '?', '#'], start);
        return !url.AsSpan(start, (end < 0 ? url.Length : end) - start).Contains('@');
    }

    // Cancels the visible chat, or the exclusive account/test operation.
    internal void Cancel()
    {
        lock (this.gate)
        {
            var operation = this.AccountBusy ? this.accountOperation : this.ActiveQuestion;
            if (operation is not null) this.CancelOperation(operation);
        }
    }

    internal void CancelAllQuestions()
    {
        lock (this.gate)
            foreach (var operation in this.questions.Values.Where(operation => operation.busy))
            {
                operation.questionInvalidated = true;
                this.CancelOperation(operation);
            }
    }

    // Called under gate. Cancellation marks its owner before any later callback or write.
    private void CancelOperation(Operation operation)
    {
        if (!operation.busy || operation.cancelRequested || operation.answerSaved) return;
        operation.cancelRequested = true;
        operation.partialAnswer = "";
        var current = operation.process;
        var preparation = operation.preparationCancellation;
        var killInstead = operation.action == "ask" && !operation.initialRequestWritten;
        operation.state = "Cancelling companion";
        operation.metrics?.SetActivity("cancelling");
        if (operation.action is "test" or "ask")
            this.quota = "Unknown after cancellation; checked before next request";
        else
            this.MarkAccountUnverified(operation);
        try { preparation?.Cancel(); } catch (ObjectDisposedException) { }
        if (current is null) return;
        if (killInstead)
        {
            _ = Task.Run(() => { try { if (!current.HasExited) current.Kill(entireProcessTree: true); } catch (Exception) { } });
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await this.WriteInputAsync(operation, current, "{\"cancel\":true}", grace.Token, allowCancelled: true);
                await current.WaitForExitAsync(grace.Token);
            }
            catch (Exception)
            {
                try { if (!current.HasExited) current.Kill(entireProcessTree: true); }
                catch (Exception) { }
            }
        });
    }

    public void Dispose()
    {
        Process[] processes;
        lock (this.gate)
        {
            if (this.disposed) return;
            this.disposed = true;
            this.attachmentDrafts.Clear();
            this.retryAttachments.Clear();
            this.DiscardGameChatReplies();
            foreach (var operation in this.questions.Values)
            {
                operation.cancelRequested = true;
                operation.partialAnswer = "";
                if (operation.busy && operation.submittedDraft is { } unsent)
                    this.chats.RestoreDraftIfEmpty(operation.chatId!, unsent);
            }
            this.draftSaveTimer.Dispose();
            this.chats.FlushAll();
            processes = this.questions.Values.Append(this.accountOperation).Where(operation => operation?.process is not null)
                .Select(operation => operation!.process!).ToArray();
        }
        this.lifetime.Cancel();
        foreach (var current in processes)
        {
            try { if (!current.HasExited) current.Kill(entireProcessTree: true); }
            catch (Exception) { }
        }
        this.lifetime.Dispose();
    }
}
