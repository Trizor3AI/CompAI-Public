namespace CompAI;

// Compile the production access transition method with local stand-ins only.
public sealed partial class Plugin
{
    private void ResetMenuCaptureUnderLock() { }
    private void ApplyRecentReaderAccessChangeUnderLock(CompanionAccessSettings previous, CompanionAccessSettings next) { }
    private void ApplyActivityAccessChangeUnderLock(CompanionAccessSettings previous, CompanionAccessSettings next) { this.RecentReaderResetCount++; }
    private void ResetRecentAchievementProgressUnderLock() { }
    private readonly object stateGate = new();
    private readonly Dictionary<string, object> readerObservations = new();
    private readonly AccessTestCompanion companion = new();
    private readonly AccessTestQuests quests = new();
    private readonly AccessTestChatWindow chatWindow = new();
    private readonly AccessTestPedigrees pedigrees = new();
    private bool contextDisposed = false;
    private bool inventoryWatching = false;
    private int valuationAccessEpoch;
    private object? preparedQuestion = null;
    private object? snapshot = null;
    private string? chatCommandNotice = null;
    private bool CanReadCurrentCharacter => true;
    private AccessTestCompanionView CompanionView => this.companion.View;

    internal Plugin(CompanionAccessSettingsStore settings) => this.accessSettings = settings;
    internal int CancelCount => this.companion.CancelCount;
    internal bool AnyPendingQuestion => this.companion.View.PendingQuestion is not null || this.companion.BackgroundPending;
    internal int UnrelatedReaderActions { get; private set; }
    internal int RecentReaderResetCount { get; private set; }
    internal int AccessEpoch => this.valuationAccessEpoch;
    internal bool HasReaderObservation => this.readerObservations.Count != 0;
    internal void SeedReaderObservation(string category) => this.readerObservations[category] = new object();
    internal bool LastReplyInGameChat { get; private set; }
    internal int GameChatInputCount { get; private set; }
    internal void SetPendingQuestion(string? text) => this.companion.View.PendingQuestion = text;
    internal void SetBackgroundQuestion(bool pending) => this.companion.BackgroundPending = pending;

    private void ApplyGameContextAccessChangeUnderLock(CompanionAccessSettings previous, CompanionAccessSettings next)
    {
        if (previous.CharacterContext != next.CharacterContext || previous.Location != next.Location ||
            previous.NpcDialogue != next.NpcDialogue) this.UnrelatedReaderActions++;
    }
    private void ApplyProgressionAccessChangeUnderLock(CompanionAccessSettings previous, CompanionAccessSettings next) { }
    private void PauseInventoryUnderLock(string _) => this.UnrelatedReaderActions++;
    private void ClearPedigreeScanUnderLock(string _) => this.UnrelatedReaderActions++;
    private void PauseQuestsForOffline(string _) => this.UnrelatedReaderActions++;
    private void Capture() => this.UnrelatedReaderActions++;
    private void CaptureQuests() => this.UnrelatedReaderActions++;
    private bool CanSendQuestion(string _, bool inventory) => true;
    private string? GetQuestionBlockReason(string _, bool inventory) => null;
    private string? QuestionValidationError(string _, bool inventory) => null;
    private bool SendQuestion(string _, bool inventory, bool web, bool quests, bool replyInGameChat = false,
        IReadOnlyList<ChatAttachment>? attachments = null)
    {
        this.LastReplyInGameChat = replyInGameChat;
        return true;
    }
    private void PrintGameChatInput(string _) => this.GameChatInputCount++;

    // Read harness fields so build warnings do not hide production diagnostics.
    internal bool HarnessStateIsEmpty => !this.inventoryWatching && this.preparedQuestion is null &&
        this.snapshot is null && this.chatCommandNotice is null;
}

internal sealed class AccessTestCompanion
{
    internal IReadOnlyList<ChatAttachment> RetryAttachments(string _) => Array.Empty<ChatAttachment>();
    internal AccessTestCompanionView View { get; } = new();
    internal int CancelCount { get; private set; }
    internal bool BackgroundPending { get; set; }
    internal void SetGameChatReplies(bool _) { }
    internal void Cancel() { this.CancelCount++; this.View.PendingQuestion = null; }
    internal void CancelAllQuestions()
    {
        if (this.View.PendingQuestion is null && !this.BackgroundPending) return;
        this.CancelCount++;
        this.View.PendingQuestion = null;
        this.BackgroundPending = false;
    }
}

internal sealed class ChatAttachment { }

internal sealed class AccessTestCompanionView
{
    internal string? PendingQuestion { get; set; } = "answer in progress";
    internal string ActiveChatId => "test";
    internal string RetryQuestion => "test";
    internal bool Busy => false;
    internal bool ActiveChatBusy => false;
    internal bool CanTest => true;
}

internal sealed class AccessTestQuests
{
    internal AccessTestQuestView View { get; } = new();
}
internal sealed class AccessTestQuestView { internal object? Snapshot => null; }
internal sealed class AccessTestChatWindow { internal void FollowLatestMessage() { } }
internal sealed class AccessTestPedigrees { internal bool HasReadings => false; }
