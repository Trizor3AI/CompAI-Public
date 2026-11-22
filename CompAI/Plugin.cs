using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Game.Inventory;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace CompAI;

public sealed partial class Plugin : IDalamudPlugin
{
    private const string ChatCommandName = "/compai";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager Commands { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IGameInventory Inventory { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IObjectTable Objects { get; private set; } = null!;
    [PluginService] internal static ITargetManager Targets { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerInfo { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;

    private readonly WindowSystem windows = new("CompAI");
    private readonly SettingsWindow window;
    private readonly ChatWindow chatWindow;
    private readonly CompanionConnection companion;
    private readonly PluginStoragePaths storage;
    private readonly IGameInventory.InventoryChangelogDelegate onInventoryChanged;
    private readonly IClientState.LogoutDelegate onLogout;
    private readonly PedigreeTooltipReader pedigreeReader;
    private readonly QuestReader questReader;
    private readonly QuestCaptureStore quests = new();
    private readonly PedigreeScanStore pedigrees = new();
    private int pedigreeEpoch;
    private DateTimeOffset? scanUntil;
    private DateTimeOffset nextScan;
    private string pedigreeStatus = "Start a scan, then hover each chocobo card in your carried bags.";
    private PedigreeCard[]? pedigreeCardsView;
    private SnapshotData? pedigreeAttachmentSnapshot;
    private PedigreeCard[]? pedigreeAttachmentCards;
    private PedigreeAttachmentStatus? pedigreeAttachmentStatus;
    private string? pedigreeAttachmentUnavailableReason;
    private bool exportBusy;
    private string exportStatus = "Export the saved inventory and available pedigree readings to local files.";
    private ContextExportPaths? lastExport;

    private readonly object stateGate = new();
    private SnapshotData? snapshot;
    private int captureEpoch;
    private bool changedSinceCapture;
    private bool capturePending;
    private string? unavailableReason;
    private PreparedQuestion? preparedQuestion;
    private string questionPreviewState = "Preview a saved capture, or turn off inventory to send a standalone question.";

    internal string QuestionPreviewState
    {
        get { lock (this.stateGate) return this.questionPreviewState; }
    }
    internal bool CanSendQuestion(string question, bool includeInventory)
    {
        lock (this.stateGate)
            return this.QuestionValidationError(question, includeInventory) is null;
    }

    internal string? GetQuestionBlockReason(string question, bool includeInventory)
    {
        lock (this.stateGate) return this.QuestionValidationError(question, includeInventory);
    }

    internal bool CanSendStandaloneQuestion(string question) => this.QuestionValidationError(question, false) is null;

    internal void DiscardQuestionPreview()
    {
        lock (this.stateGate)
        {
            this.preparedQuestion = null;
            this.questionPreviewState = "Inventory option changed; preview the attachment before sending.";
        }
    }

    internal SnapshotView View
    {
        get
        {
            lock (this.stateGate)
                return new SnapshotView(this.snapshot, this.capturePending,
                    this.changedSinceCapture, this.unavailableReason);
        }
    }

    internal CompanionView CompanionView => this.companion.View;
    internal string ChatDraft
    {
        get => this.companion.Draft;
        set { lock (this.stateGate) { this.companion.Draft = value; this.chatCommandNotice = null; } }
    }
    internal ChatContextInfo ChatContextInfo => this.companion.ContextInfo;
    internal ChatStorageInfo ChatStorageInfo => this.companion.StorageInfo;
    internal string? ExportActiveChat() => this.companion.ExportActiveChat();
    internal void FlushChatDraft() => this.companion.FlushDraft();
    private readonly CompanionUiSettingsStore uiSettings;
    internal bool HideKofi => this.uiSettings.HideKofi;
    internal string? UiSettingsError => this.uiSettings.Error;
    internal bool SetHideKofi(bool hidden) => this.uiSettings.SetHideKofi(hidden);
    internal bool AutoSortChats => this.companion.AutoSortChats;
    internal string? ChatOrderError => this.companion.ChatOrderError;
    internal bool SetAutoSortChats(bool enabled) => this.companion.SetAutoSortChats(enabled);
    internal bool MoveChatBefore(string id, string? beforeId) => this.companion.MoveChatBefore(id, beforeId);
    internal ChatSearchResult SearchChats(string query) => this.companion.SearchChats(query);
    internal bool RenameChat(string id, string? title) => this.companion.RenameChat(id, title);
    internal bool SetChatPinned(string id, bool pinned) => this.companion.SetChatPinned(id, pinned);
    internal ChatDeleteResult DeleteSelectedChats(IEnumerable<string> ids)
    {
        lock (this.stateGate)
        {
            var result = this.companion.DeleteSelectedChats(ids);
            this.preparedQuestion = null;
            return result;
        }
    }
    internal string? OpenChatExportFolder()
    {
        var path = Path.Combine(this.storage.Configuration, "exports", "chats");
        try
        {
            if (!Directory.Exists(path)) return "Export a chat first to create its folder.";
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return $"Could not open the chat export folder ({ex.GetType().Name}).";
        }
    }
    internal string ChatNotes => this.companion.ChatNotes;
    internal string? ChatNotesError => this.companion.ChatNotesError;
    internal string AllowanceSummary => this.companion.AllowanceSummary;
    internal string AllowanceDetails => this.companion.AllowanceDetails;
    internal string? SyntheticTestBlockReason => this.companion.SyntheticTestBlockReason;
    internal bool SaveChatNotes(string notes)
    {
        lock (this.stateGate)
        {
            if (this.contextDisposed || !this.companion.SaveChatNotes(notes)) return false;
            this.preparedQuestion = null;
            return true;
        }
    }
    internal void OpenSettings() => this.window.IsOpen = true;
    internal void OpenChat() => this.chatWindow.IsOpen = true;
    internal void OpenPedigrees() => this.window.OpenPedigrees();
    internal QuestCaptureView QuestView => this.quests.View;
    internal void ClearQuests() => this.ClearQuestCache();
    internal void CompanionAction(string action) => this.companion.Start(action);
    internal void CancelCompanion() => this.companion.Cancel();
    internal void OpenPendingLoginUrl() => this.companion.OpenPendingLoginUrl();
    internal bool NewChat()
    {
        lock (this.stateGate)
        {
            if (!this.companion.NewChat()) return false;
            this.chatCommandNotice = null;
            this.preparedQuestion = null;
            this.questionPreviewState = "";
            return true;
        }
    }

    internal bool SelectChat(string id)
    {
        lock (this.stateGate)
        {
            if (!this.companion.SelectChat(id)) return false;
            this.chatCommandNotice = null;
            this.preparedQuestion = null;
            return true;
        }
    }

    internal bool DeleteChat(string id)
    {
        lock (this.stateGate)
        {
            if (!this.companion.DeleteChat(id)) return false;
            this.preparedQuestion = null;
            return true;
        }
    }

    internal void PrepareQuestion(string question, bool includeInventory, bool useWebSearch = false, bool includeQuests = false,
        IReadOnlyList<ChatAttachment>? attachments = null)
    {
        lock (this.stateGate)
        {
            this.preparedQuestion = null;
            var error = this.QuestionValidationError(question, includeInventory);
            if (error is not null) { this.questionPreviewState = error; return; }
            ChatAttachment[] selectedAttachments;
            try { selectedAttachments = ChatAttachment.Freeze(attachments); }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
            { this.questionPreviewState = "Selected attachments exceed the allowed limits."; return; }
            if (this.AttachmentBlockReason(selectedAttachments) is { } attachmentError)
            { this.questionPreviewState = attachmentError; return; }
            var attachmentWire = selectedAttachments.Select(attachment => attachment.Wire).ToArray();
            var attachmentBytes = JsonSerializer.SerializeToUtf8Bytes(attachmentWire).Length;
            var data = this.snapshot!;
            var historyContext = this.companion.HistoryContext;
            var optionalPersonality = new PersonalityDetailsLookup(historyContext.Personality.Details);
            var personalityDetailsLookup = historyContext.Personality.Id == CompAI.PersonalityPresets.None.Id ||
                !optionalPersonality.HasSections ? null : optionalPersonality;
            var history = historyContext.Exchanges.ToList();
            var questView = this.quests.View;
            // Freeze existing observations. Character details are captured once on demand,
            // through a guarded framework-thread callback; tools only receive managed data.
            var questSnapshot = includeQuests && this.Access.Quests && this.CanReadCurrentCharacter
                ? questView.Snapshot : null;
            var questStale = questView.Stale || questView.Pending;
            var gameContextLookup = this.FreezeGameContextUnderLock();
            var npcDialogueLookup = this.Access.NpcDialogue && this.CanReadCurrentCharacter
                ? new NpcDialogueLookup(this.npcConversation.CaptureSnapshot(DateTimeOffset.UtcNow)) : null;
            var characterDetailsQuery = this.TrackReader("character_details", this.CreateCharacterDetailsQueryUnderLock());
            var currencyQuery = this.TrackReader("currencies", this.CreateCurrencyQueryUnderLock());
            var dutyQuery = this.TrackReader("duties", this.CreateDutyQueryUnderLock());
            var collectionQuery = this.TrackReader("collections", this.CreateCollectionQueryUnderLock());
            var progressionQuery = this.TrackReader("progression", this.CreateProgressionQueryUnderLock());
            var craftingLogQuery = this.TrackReader("crafting_log", this.CreateCraftingLogQueryUnderLock());
            var menuQuery = this.TrackReader("menus", this.CreateMenuQueryUnderLock());
            var activityQuery = this.TrackReader("activities", this.CreateActivityQueryUnderLock());
            var definitionsQuery = this.CreateGameDefinitionsQueryUnderLock();
            var gcSupplyQuery = this.TrackReader("gc_supply", this.CreateGcSupplyQueryUnderLock());
            var storageLookup = this.FreezeStorageUnderLock();
            var npcTargetQuery = this.TrackReader("npc_target", this.CreateNpcTargetQueryUnderLock());
            var inventoryLookup = includeInventory ? new InventoryLookup(data.CapturedAtUtc,
                this.changedSinceCapture || this.capturePending,
                data.Containers.Select(c => new InventoryLookupBag(c.Type.ToString(), c.SlotCount,
                    c.UnavailableReason, c.Items.Count)),
                InventoryRows(data)) : null;
            var valuationLookup = this.FreezeValuationUnderLock(includeInventory, useWebSearch);
            var olderTurns = Array.Empty<ChatTurn>();
            object? historySummary = null;
            string EncodeRequest()
            {
                // Complete recent pairs need no lookup. A shortened latest pair still
                // exposes its full saved turn through the same permission-gated tool.
                olderTurns = this.Access.ChatHistory
                    ? historyContext.Turns.Take(historyContext.Turns.Length - history.Count(x => !x.IsExcerpt)).ToArray()
                    : Array.Empty<ChatTurn>();
                historySummary = olderTurns.Length > 0 ? ChatHistoryLookup.CreateSummary(olderTurns) : null;
                return JsonSerializer.Serialize(new { request = new
                {
                    version = 43,
                    provider = this.ModelSelection.Provider,
                    model = this.ModelSelection.Model,
                    reasoning_effort = this.ModelSelection.Effort,
                    question = question.Trim(),
                    web_search = useWebSearch && this.Access.WebSearch,
                    spoilers_allowed = this.Access.SpoilersAllowed,
                    history,
                    chat_notes = historyContext.Notes,
                    personality = historyContext.Personality.Id == CompAI.PersonalityPresets.None.Id
                        ? (object?)null : new { name = historyContext.Personality.Name,
                            instructions = historyContext.Personality.Instructions },
                    personality_details_lookup = personalityDetailsLookup?.Summary,
                    history_lookup = historySummary,
                    quest_lookup = questSnapshot is not null ? QuestLookup.CreateSummary(questSnapshot, questStale) : null,
                    inventory_lookup = inventoryLookup?.Summary,
                    game_context_lookup = gameContextLookup?.Summary,
                    npc_dialogue_lookup = npcDialogueLookup?.Summary,
                    character_details_lookup = characterDetailsQuery is null ? null : new { scope = "local_character_details" },
                    currency_lookup = currencyQuery is null ? null : new { scope = CurrencyLookup.Scope },
                    duty_lookup = dutyQuery is null ? null : new { scope = DutyLookup.Scope },
                    collection_lookup = collectionQuery is null ? null :
                        new CollectionLookupSummary(CollectionLookup.Scope, CollectionAccess.Categories(this.Access)),
                    progression_lookup = progressionQuery is null ? null :
                        new ProgressionLookupSummary(ProgressionLookup.Scope, this.ProgressionQueryCategoriesUnderLock()),
                    crafting_log_lookup = craftingLogQuery is null ? null : new { scope = CraftingLogLookup.Scope },
                    menu_lookup = menuQuery is null ? null : new { scope = MenuLookup.Scope, categories = this.MenuCategoriesUnderLock() },
                    activity_lookup = activityQuery is null ? null : new { scope = ActivityLookup.Scope, categories = this.ActivityCategoriesUnderLock() },
                    game_definitions_lookup = new { scope = GameDefinitionsLookup.Scope },
                    gc_supply_lookup = gcSupplyQuery is null ? null : new { scope = GcSupplyLookup.Scope },
                    storage_lookup = storageLookup?.Summary,
                    valuation_lookup = valuationLookup?.Summary,
                    npc_target_lookup = npcTargetQuery is null ? null :
                        new NpcTargetLookupSummary(NpcTargetLookup.Scope, this.Access.NpcDialogue, this.Access.TargetInfo),
                    attachments = attachmentWire,
                    snapshot = (object?)null,
                }});
            }
            var json = EncodeRequest();
            var bytes = Encoding.UTF8.GetByteCount(json);
            // Keep complete recent exchanges within the same bounded transport as snapshots.
            while (bytes - attachmentBytes + 2 > 65536 && history.Count > 0)
            {
                history.RemoveAt(0);
                json = EncodeRequest();
                bytes = Encoding.UTF8.GetByteCount(json);
            }
            if (bytes - attachmentBytes + 2 > 65536 || bytes > ChatAttachment.MaxRequestBytes)
            {
                this.questionPreviewState = "Request exceeds the context or attachment limit. Shorten the question or remove an attachment.";
                return;
            }
            this.preparedQuestion = new PreparedQuestion(json, this.captureEpoch, question, includeInventory,
                historyContext.Revision, this.changedSinceCapture, questSnapshot is null ? null :
                    arguments => JsonSerializer.Serialize(QuestLookup.Query(questSnapshot, questStale, arguments)),
                inventoryLookup is null ? null : this.CreateInventoryQueryUnderLock(inventoryLookup),
                historySummary is null ? null : arguments => JsonSerializer.Serialize(ChatHistoryLookup.Query(olderTurns, arguments)),
                gameContextLookup is null ? null : arguments => JsonSerializer.Serialize(gameContextLookup.Query(arguments)),
                npcDialogueLookup is null ? null : arguments => JsonSerializer.Serialize(npcDialogueLookup.Query(arguments)),
                characterDetailsQuery,
                personalityDetailsLookup is null ? null : arguments => JsonSerializer.Serialize(personalityDetailsLookup.Query(arguments)),
                currencyQuery, dutyQuery, definitionsQuery, gcSupplyQuery, npcTargetQuery, historyContext.ChatId, selectedAttachments,
                storageLookup is null ? null : this.CreateStorageQueryUnderLock(storageLookup),
                valuationLookup is null ? null : this.CreateValuationQueryUnderLock(valuationLookup, includeInventory),
                collectionQuery, progressionQuery, craftingLogQuery, menuQuery, activityQuery);
            this.questionPreviewState = includeInventory
                ? $"Ready to send ({bytes:N0} bytes). Inventory lookup available from {data.CapturedAtUtc:yyyy-MM-dd HH:mm:ss} UTC; only requested item rows will be shared."
                : $"Ready to send question ({bytes:N0} bytes); inventory lookup unavailable.";
            this.questionPreviewState += $" Includes {history.Count} recent completed exchange(s).";
        }
    }

    internal bool SendQuestion(string question, bool includeInventory, bool useWebSearch = false, bool includeQuests = false,
        bool replyInGameChat = false, IReadOnlyList<ChatAttachment>? attachments = null)
    {
        lock (this.stateGate)
        {
            // Send explicitly approves the selected attachment and constructs its payload
            // atomically here. Inspecting bag details is optional, never another send gate.
            this.PrepareQuestion(question, includeInventory, useWebSearch, includeQuests, attachments);
            var prepared = this.preparedQuestion;
            var error = this.QuestionValidationError(question, includeInventory);
            if (prepared is null || error is not null || prepared.IncludeInventory != includeInventory ||
                (includeInventory && prepared.Epoch != this.captureEpoch) || prepared.Question != question)
            {
                this.preparedQuestion = null;
                this.questionPreviewState = error ?? "Preview expired or question changed; preview again.";
                return false;
            }
            if (prepared.HistoryRevision != this.companion.HistoryRevision)
            {
                this.preparedQuestion = null;
                this.questionPreviewState = "Chat context changed; preview again.";
                return false;
            }
            // The reviewed bag contents are immutable. A game change only updates the
            // historical-status label; it must not throw away approval of those contents.
            if (includeInventory && prepared.Changed != this.changedSinceCapture)
            {
                this.PrepareQuestion(question, true, useWebSearch, includeQuests, attachments);
                prepared = this.preparedQuestion;
                if (prepared is null) return false;
            }
            if (!this.companion.StartAsk(prepared.Json, includeInventory, question.Trim(), includeQuests,
                    prepared.QuestQuery, inventoryQuery: prepared.InventoryQuery,
                    includeHistoryLookup: this.Access.ChatHistory && prepared.HistoryQuery is not null,
                    historyQuery: prepared.HistoryQuery, includeGameContext: prepared.GameContextQuery is not null,
                    gameContextQuery: prepared.GameContextQuery,
                    npcDialogueQuery: prepared.NpcDialogueQuery, characterDetailsQuery: prepared.CharacterDetailsQuery,
                    personalityDetailsQuery: prepared.PersonalityDetailsQuery, replyInGameChat: replyInGameChat,
                    currencyQuery: prepared.CurrencyQuery, dutyQuery: prepared.DutyQuery, expectedChatId: prepared.ChatId,
                    attachments: prepared.Attachments, gameDefinitionsQuery: prepared.GameDefinitionsQuery,
                    gcSupplyQuery: prepared.GcSupplyQuery, npcTargetQuery: prepared.NpcTargetQuery, storageQuery: prepared.StorageQuery,
                    valuationQuery: prepared.ValuationQuery, collectionQuery: prepared.CollectionQuery,
                    progressionQuery: prepared.ProgressionQuery, craftingLogQuery: prepared.CraftingLogQuery, menuQuery: prepared.MenuQuery, activityQuery: prepared.ActivityQuery))
            {
                this.questionPreviewState = this.companion.View.SendBlockReason ?? "Companion unavailable; preview retained.";
                return false;
            }
            this.preparedQuestion = null;
            this.questionPreviewState = "Question sent with available context allowed in Settings.";
            return true;
        }
    }

    // Called only while holding stateGate.
    private string? QuestionValidationError(string question, bool includeInventory)
    {
        if (string.IsNullOrWhiteSpace(question) || question.Length > 2000)
            return "Enter a question of 1 to 2,000 characters.";
        if (this.AttachmentPending(this.companion.ActiveChatId)) return ChatAttachmentLoader.BusyMessage;
        if (this.companion.ChatPersonalityBlockReason is { } personalityError) return personalityError;
        if (this.companion.StorageInfo.SendBlockReason is { } storageError) return storageError;
        if (this.ModelSelectionBlockReason() is { } modelError) return modelError;
        if (!includeInventory)
            return null;
        if (!this.Access.Inventory) return "Inventory access is off in Settings.";
        if (!ClientState.IsLoggedIn)
            return "Character is logged out; saved inventory remains viewable locally. Log in to attach it.";
        if (!this.CanReadCurrentCharacter)
            return "Waiting for this character's saved context to load.";
        if (this.PedigreeAttachmentsUnderLock().BlockReason is { } pedigreeError)
            return pedigreeError;
        if (this.snapshot is null)
            return "Capture inventory and wait for completion before previewing.";
        return null;
    }

    public Plugin()
    {
        using var timing = PerformanceDiagnostics.Measure("plugin.startup");
        this.storage = PluginStoragePaths.Prepare(PluginInterface.GetPluginConfigDirectory());
        this.accessSettings = new CompanionAccessSettingsStore(Path.Combine(this.storage.Configuration, "companion-access.json"));
        this.modelSettings = new CompanionModelSettingsStore(Path.Combine(this.storage.Configuration, "companion-model.json"));
        this.responseSettings = new CompanionResponseSettingsStore(Path.Combine(this.storage.Configuration, "companion-response.json"));
        this.uiSettings = new CompanionUiSettingsStore(Path.Combine(this.storage.Configuration, "companion-ui.json"));
        this.personalityPresets = new PersonalityPresetStore(Path.Combine(this.storage.Configuration, "companion-personalities.json"));
        this.companion = new CompanionConnection(() => this.modelSettings.IsLoaded ? this.modelSettings.Settings : null,
            defaultPersonality: () => this.personalityPresets.Default,
            configurationDirectory: this.storage.Configuration, profileDirectory: this.storage.CompanionProfile,
            responseLimitMinutes: () => this.responseSettings.Settings.Minutes);
        this.companion.SetGameChatReplies(this.Access.GameChatReplies);
        this.gameChatStyle = new GameChatStyleStore(Path.Combine(this.storage.Configuration, "game-chat-style.json"));
        this.characterCache = new CharacterCacheStore(Path.Combine(this.storage.Configuration, "character-cache"));
        this.inventoryStateReader = new InventoryStateReader(Inventory);
        this.pedigreeReader = new PedigreeTooltipReader(GameGui, Inventory, DataManager);
        this.questReader = new QuestReader(DataManager);
        this.gameContextReader = new GameContextReader(ClientState, Objects, DataManager, GameGui, PlayerInfo);
        this.characterDetailsReader = new CharacterDetailsReader(ClientState, Objects, DataManager, PlayerInfo, Inventory);
        this.currencyReader = new CurrencyReader(DataManager);
        this.dutyReader = new DutyReader(DataManager);
        this.collectionReader = new CollectionReader(DataManager);
        this.progressionReader = new ProgressionReader(DataManager, GameGui);
        this.goldSaucerReader = new GoldSaucerReader(DataManager, GameGui);
        this.freeCompanyReader = new FreeCompanyReader(DataManager, GameGui);
        this.pvpSeriesRewardsReader = new PvpSeriesRewardsReader(DataManager, GameGui);
        this.mogpendiumReader = new MogpendiumReader(DataManager, GameGui);
        this.fieldRecordReader = new FieldRecordReader(DataManager, GameGui);
        this.portableArchiveReader = new PortableArchiveReader(DataManager, GameGui);
        this.mahjongReader = new MahjongReader(GameGui);
        this.islandCurrencyReader = new IslandCurrencyReader(DataManager, GameGui);
        this.islandProgressReader = new IslandProgressReader(DataManager, GameGui);
        this.cosmicResearchReader = new CosmicResearchReader(DataManager, GameGui);
        this.islandMaterialsReader = new IslandMaterialsReader(DataManager, GameGui);
        this.cosmicMissionReader = new CosmicMissionReader(DataManager, GameGui);
        this.freeCompanyCreditsReader = new FreeCompanyCreditsReader(DataManager, GameGui);
        this.selectedMacroReader = new SelectedMacroReader(GameGui);
        this.craftingLogReader = new CraftingLogReader(DataManager);
        this.shopReader = new ShopReader(DataManager, GameGui);
        this.glamourReader = new GlamourReader(DataManager);
        this.gameDefinitionsReader = new GameDefinitionsReader(DataManager);
        this.mapLocations = new MapLocationsReader(DataManager);
        this.gcSupplyReader = new GcSupplyReader(DataManager, GameGui);
        this.retainerStorageReader = new RetainerSaddlebagReader(Inventory, DataManager, GameGui);
        this.housingStorageReader = new HousingFcStorageReader(Inventory, DataManager, GameGui);
        this.armouryReader = new ArmouryReader(Inventory, DataManager);
        this.npcTargetReader = new NpcTargetReader(Targets, DataManager);
        this.InitializeInventoryHighlight();
        this.InitializeShopHighlight();
        this.window = new SettingsWindow(this);
        this.chatWindow = new ChatWindow(this);
        this.windows.AddWindow(this.window);
        this.windows.AddWindow(this.chatWindow);
        this.onInventoryChanged = this.OnInventoryChanged;
        this.onLogout = (_, _) => this.OnContextLogout();
        Inventory.InventoryChangedRaw += this.onInventoryChanged;
        ClientState.Logout += this.onLogout;
        ClientState.Login += this.OnContextLogin;
        ClientState.TerritoryChanged += this.OnContextTerritoryChanged;
        Framework.Update += this.OnFrameworkUpdate;
        this.RegisterTalkCapture();
        this.RegisterCoreCommands();
        this.RefreshPresetCommands();
        PluginInterface.UiBuilder.Draw += this.windows.Draw;
        PluginInterface.UiBuilder.Draw += this.DrawInventoryHighlight;
        PluginInterface.UiBuilder.Draw += this.DrawShopHighlight;
        PluginInterface.UiBuilder.OpenMainUi += this.OpenChat;
        PluginInterface.UiBuilder.OpenConfigUi += this.OpenSettings;
        this.InitializeContextCache();
        // One asynchronous account/model/quota read per load; never opens login or sends a question.
        this.companion.Start("status");
    }

    internal void Capture() => this.CaptureInventory(automatic: false);

    private void CaptureInventory(bool automatic)
    {
        int epoch;
        lock (this.stateGate)
        {
            if (!this.Access.Inventory || !this.CanReadCurrentCharacter || automatic && !this.inventoryWatching) return;
            this.inventoryWatching = true;
            epoch = ++this.captureEpoch;
            this.capturePending = true;
            this.changedSinceCapture = false;
            this.unavailableReason = null;
            this.preparedQuestion = null;
        }
        try
        {
            var task = Framework.RunOnFrameworkThread(() => this.CaptureOnFrameworkThread(epoch));
            _ = task.ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        this.CaptureFailed(epoch, t.Exception!);
                    else if (t.IsCanceled)
                        this.CaptureFailed(epoch, new TaskCanceledException());
                },
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            this.CaptureFailed(epoch, ex);
        }
    }

    private void CaptureFailed(int epoch, Exception ex)
    {
        lock (this.stateGate)
        {
            if (epoch == this.captureEpoch)
            {
                var error = ex.GetBaseException();
                this.PauseInventoryUnderLock(error is InvalidOperationException ? error.Message : $"Capture failed: {error.GetType().Name}; showing saved inventory.");
                this.nextInventoryCheck = DateTimeOffset.UtcNow.AddSeconds(3);
            }
        }
    }

    private void CaptureOnFrameworkThread(int epoch)
    {
        int pedigreeRevision;
        ulong characterId;
        lock (this.stateGate)
        {
            if (!this.Access.Inventory || epoch != this.captureEpoch || !this.CanReadCurrentCharacter)
                return;
            characterId = this.activeContentId;
            pedigreeRevision = this.pedigrees.AttachmentRevision;
        }
        using var timing = PerformanceDiagnostics.Measure("reader.inventory");
        if (QuestLoading()) throw new InvalidOperationException("Loading; showing saved inventory until bags are available.");
        var loadedId = ReadLoadedCharacterId();
        if (loadedId == 0) throw new InvalidOperationException("Character is loading; saved inventory retained.");
        if (loadedId != characterId) { this.OnDetectedCharacterMismatch(); return; }
        var before = this.inventoryStateReader.Read(characterId);
        var containers = new List<ContainerSnapshot>();
        var capturedAtUtc = DateTimeOffset.UtcNow;
        ExcelSheet<Item>? itemSheet = null;
        try
        {
            itemSheet = DataManager.GetExcelSheet<Item>();
        }
        catch
        {
            // Inventory remains useful with numeric IDs if the local sheet is unavailable.
        }
        foreach (var type in new[]
                 {
                     GameInventoryType.Inventory1, GameInventoryType.Inventory2,
                     GameInventoryType.Inventory3, GameInventoryType.Inventory4,
                 })
        {
            var container = new ContainerSnapshot(type);
            containers.Add(container);
            if (!ClientState.IsLoggedIn)
            {
                container.UnavailableReason = "Not logged in";
                continue;
            }

            try
            {
                // Copy only scalar fields. The API returns a span backed by current game state.
                var slots = Inventory.GetInventoryItems(type);
                if (slots.Length == 0)
                {
                    container.UnavailableReason = "No slots returned; loading or availability unknown";
                    continue;
                }

                container.SlotCount = slots.Length;
                foreach (var item in slots)
                {
                    if (!item.IsEmpty)
                    {
                        ObservedPedigree? pedigree;
                        lock (this.stateGate)
                            pedigree = pedigreeRevision == this.pedigrees.AttachmentRevision
                                ? this.pedigrees.Attachment(type.ToString(), item.InventorySlot,
                                    item.ItemId, item.BaseItemId, item.Quantity, item.IsHq) : null;
                        container.Items.Add(new ItemSnapshot(item.InventorySlot, item.ItemId,
                            item.BaseItemId, ResolveItemName(itemSheet, item.BaseItemId),
                            item.Quantity, item.IsHq, pedigree));
                    }
                }
            }
            catch (Exception ex)
            {
                container.UnavailableReason = $"Read failed: {ex.GetType().Name}";
                container.SlotCount = null;
                container.Items.Clear();
            }
        }

        var after = this.inventoryStateReader.Read(characterId);
        if (ReadLoadedCharacterId() != characterId) { this.OnDetectedCharacterMismatch(); return; }
        if (QuestLoading() || !before.Equals(after) || containers.Any(bag => bag.UnavailableReason is not null))
            throw new InvalidOperationException("Inventory changed or became unavailable during capture; saved counts retained until refresh.");
        lock (this.stateGate)
        {
            if (epoch == this.captureEpoch && this.CanReadCurrentCharacter && this.activeContentId == characterId)
            {
                if (pedigreeRevision != this.pedigrees.AttachmentRevision)
                    RemovePedigrees(containers);
                this.snapshot = new SnapshotData(capturedAtUtc, containers.ToArray());
                this.capturePending = false;
                this.changedSinceCapture = false;
                this.unavailableReason = null;
                this.inventoryApplied = this.inventoryCandidate = after;
                this.inventoryNeedsRefresh = false;
                this.inventoryLastChecked = DateTimeOffset.UtcNow;
                this.nextInventoryCheck = DateTimeOffset.UtcNow.AddSeconds(15);
                this.PersistContextUnderLock();
            }
            else if (epoch == this.captureEpoch)
            {
                this.PauseInventoryUnderLock("Character not ready; saved inventory retained.");
            }
        }
    }

    private static string? ResolveItemName(ExcelSheet<Item>? sheet, uint baseItemId)
    {
        if (sheet is null || baseItemId == 0)
            return null;

        try
        {
            if (sheet.TryGetRow(baseItemId, out var row))
            {
                var name = row.Name.ToString();
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
        }
        catch
        {
            // A missing or unreadable local row has no name; keep the numeric ID.
        }

        return null;
    }

    private void InvalidateUnderLock(string? reason)
    {
        this.ClearPedigreeScanUnderLock(reason ?? "Pedigree readings cleared.");
        ++this.captureEpoch;
        this.snapshot = null;
        this.preparedQuestion = null;
        this.questionPreviewState = "Snapshot cleared; capture and preview again.";
        this.changedSinceCapture = false;
        this.capturePending = false;
        this.unavailableReason = reason;
    }

    internal void Clear() => this.ClearInventoryCache();

    internal ContextExportView ExportView
    {
        get
        {
            lock (this.stateGate)
                return new ContextExportView(this.exportBusy, this.exportStatus, this.lastExport?.Directory,
                    this.snapshot is not null || (this.pedigreeCardsView ??= this.pedigrees.Cards).Length > 0);
        }
    }

    internal void SaveContextExport()
    {
        ContextExportData data;
        lock (this.stateGate)
        {
            if (!this.TryPrepareExportUnderLock(out data)) return;
            this.exportBusy = true;
            this.exportStatus = "Saving inventory and pedigree lists...";
        }
        string directory;
        try { directory = Path.Combine(this.storage.Configuration, "exports"); }
        catch (Exception ex)
        {
            lock (this.stateGate)
            {
                this.exportBusy = false;
                this.exportStatus = "Export folder unavailable: " + ex.Message;
            }
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                var paths = ContextExportFiles.Save(directory, Export.CreateJson(data), Export.CreateText(data), data.ExportedAtUtc);
                lock (this.stateGate)
                {
                    this.lastExport = paths;
                    this.exportStatus = "Saved inventory-pedigrees.txt and inventory-pedigrees.json.";
                }
            }
            catch (Exception ex)
            {
                lock (this.stateGate) this.exportStatus = "Export failed: " + ex.Message;
            }
            finally
            {
                lock (this.stateGate) this.exportBusy = false;
            }
        });
    }

    internal string? CopyContextExport()
    {
        ContextExportData data;
        lock (this.stateGate)
            if (!this.TryPrepareExportUnderLock(out data)) return null;
        try { return Export.CreateText(data); }
        catch (Exception ex)
        {
            lock (this.stateGate) this.exportStatus = "Could not prepare list: " + ex.Message;
            return null;
        }
    }

    internal void OpenExportFolder()
    {
        string? path;
        lock (this.stateGate) path = this.lastExport?.Directory;
        if (path is null) return;
        try
        {
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException("The export folder was moved or removed.");
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            lock (this.stateGate) this.exportStatus = "Could not open export folder: " + ex.Message;
        }
    }

    private bool TryPrepareExportUnderLock(out ContextExportData data)
    {
        data = null!;
        if (!this.Access.Inventory) { this.exportStatus = "Inventory access is off in Settings."; return false; }
        if (this.exportBusy) return false;
        if (this.capturePending)
        {
            this.exportStatus = "Wait for inventory capture to finish before exporting.";
            return false;
        }
        var cards = this.pedigrees.Cards;
        var extraInventory = this.observedStorage.Where(c => c.Category is "crystals" or "key_items").ToArray();
        if (this.snapshot is null && cards.Length == 0 && extraInventory.Length == 0)
        {
            this.exportStatus = "Capture inventory or scan a pedigree first.";
            return false;
        }
        // Capture all managed export data under one gate. Disk I/O happens later and cannot
        // observe a half-applied state change or re-read changing game memory.
        var saved = this.snapshot is not { } snapshot ? null : new ExportInventorySnapshot(snapshot.CapturedAtUtc,
            snapshot.Containers.Select(bag => new ExportInventoryBag(bag.Type.ToString(), bag.SlotCount,
                bag.UnavailableReason, bag.Items.Select(item => new ExportInventoryItem(item.Slot, item.ItemId,
                    item.BaseItemId, item.Name, item.Quantity, item.IsHq, item.Pedigree is not { } pedigree ? null :
                        new ExportPedigree(pedigree.ObservedAtUtc, pedigree.Kind, pedigree.Grade, pedigree.Sex,
                            ExportStarRow.From(pedigree.FatherStars), ExportStarRow.From(pedigree.MotherStars), pedigree.Status))).ToArray())).ToArray());
        var scan = new ExportPedigreeScan(this.scanUntil is not null,
            this.scanUntil is { } until ? Math.Max(0, (int)Math.Ceiling((until - DateTimeOffset.UtcNow).TotalSeconds)) : 0,
            this.pedigreeStatus, cards.Select(card => new ExportPedigreeScanCard(card.Sample.Bag, card.Sample.Slot,
                card.Sample.ItemId, card.Sample.BaseItemId, card.Sample.ItemName, card.Sample.CapturedAtUtc,
                card.Reading is null ? ExportPedigreeCheckStatus.Unavailable : ExportPedigreeCheckStatus.Decoded,
                card.Reason, card.Reading is not { } reading ? null : new ExportPedigree(card.Sample.CapturedAtUtc,
                    reading.Kind, reading.Grade, reading.Sex,
                    ExportStarRow.From(reading.FatherStars), ExportStarRow.From(reading.MotherStars), "decoded_tooltip"))).ToArray());
        data = new ContextExportData(DateTimeOffset.UtcNow, saved, this.capturePending,
            this.changedSinceCapture, this.unavailableReason, scan) {
            ExtraInventory = extraInventory.Select(c => new ExportExtraInventory(c.Category, c.Container,
                StorageSnapshot.ItemNamespace(c.Category), c.CapturedAtUtc, c.Capacity,
                c.Items.Select(i => new ExportInventoryItem((uint)i.Slot, i.ItemId, i.BaseItemId, i.Name, i.Quantity,
                    i.IsHq, null)).ToArray())).ToArray() };
        return true;
    }

    internal PedigreeScanView PedigreeView
    {
        get
        {
            lock (this.stateGate)
                return new PedigreeScanView(this.scanUntil is not null,
                    this.scanUntil is { } until ? Math.Max(0, (int)Math.Ceiling((until - DateTimeOffset.UtcNow).TotalSeconds)) : 0,
                    this.pedigreeStatus, this.pedigreeCardsView ??= this.pedigrees.Cards);
        }
    }

    internal PedigreeAttachmentStatus PedigreeAttachments
    {
        get { lock (this.stateGate) return this.PedigreeAttachmentsUnderLock(); }
    }

    private PedigreeAttachmentStatus PedigreeAttachmentsUnderLock()
    {
        var cards = this.pedigreeCardsView ??= this.pedigrees.Cards;
        // Chat checks availability every frame. Reuse the comparison until one of
        // its immutable source views or capture status changes.
        if (this.pedigreeAttachmentStatus is null || !ReferenceEquals(this.pedigreeAttachmentSnapshot, this.snapshot) ||
            !ReferenceEquals(this.pedigreeAttachmentCards, cards) ||
            this.pedigreeAttachmentStatus.CapturePending != this.capturePending ||
            this.pedigreeAttachmentUnavailableReason != this.unavailableReason)
        {
            this.pedigreeAttachmentSnapshot = this.snapshot;
            this.pedigreeAttachmentCards = cards;
            this.pedigreeAttachmentUnavailableReason = this.unavailableReason;
            this.pedigreeAttachmentStatus = PedigreeAttachmentStatus.Create(cards,
                this.snapshot is { } saved ? InventoryRows(saved) : Array.Empty<InventoryLookupRow>(),
                this.capturePending, this.unavailableReason);
        }
        return this.pedigreeAttachmentStatus;
    }

    private static IEnumerable<InventoryLookupRow> InventoryRows(SnapshotData data) =>
        data.Containers.SelectMany(bag => bag.Items.Select(item => new InventoryLookupRow(bag.Type.ToString(),
            item.Slot, item.ItemId, item.BaseItemId, item.Name, item.Quantity, item.IsHq, item.Pedigree)));

    internal void StartPedigreeScan()
    {
        lock (this.stateGate)
        {
            if (!this.Access.Inventory || !this.CanReadCurrentCharacter) { this.pedigreeStatus = "Enable inventory access and wait for your character to load before scanning."; return; }
            ++this.pedigreeEpoch;
            this.pedigrees.ResetPending();
            this.scanUntil = DateTimeOffset.UtcNow.AddSeconds(60);
            this.nextScan = DateTimeOffset.MinValue;
            this.pedigreeStatus = "Hover a chocobo card in your carried bags for at least half a second.";
        }
    }

    internal void StopPedigreeScan()
    {
        lock (this.stateGate)
        {
            ++this.pedigreeEpoch;
            this.scanUntil = null;
            this.pedigrees.ResetPending();
            this.pedigreeStatus = "Scan stopped. Collected cards are available below.";
        }
    }

    internal void ClearPedigrees()
    {
        lock (this.stateGate)
        {
            if (this.contextCacheLoading) return;
            this.ClearPedigreeScanUnderLock("Pedigree readings cleared.");
            // Replace the saved objects rather than mutating a snapshot the UI is reading.
            if (this.snapshot is { } saved)
                this.snapshot = saved with { Containers = saved.Containers.Select(bag =>
                {
                    var clean = new ContainerSnapshot(bag.Type) { SlotCount = bag.SlotCount, UnavailableReason = bag.UnavailableReason };
                    clean.Items.AddRange(bag.Items.Select(item => item with { Pedigree = null }));
                    return clean;
                }).ToArray() };
            ++this.captureEpoch;
            this.capturePending = false;
            this.preparedQuestion = null;
            this.PersistContextUnderLock();
        }
        this.companion.InvalidateQuestion();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        this.DrainGameChatReplies();
        this.PollContextCharacter();
        this.PollObservedProgression();
        this.PollObservedMenus();
        this.PollRecentReaders();
        this.PollActivityObservations();
        this.PollGameContext();
        this.PollObservedStorage();
        this.PollInventoryChanges();
        this.PollQuestChanges();
        this.UpdateInventoryHighlight();
        this.UpdateShopHighlight();
        int epoch;
        var now = DateTimeOffset.UtcNow;
        lock (this.stateGate)
        {
            if (this.scanUntil is not { } until) return;
            if (!ClientState.IsLoggedIn)
            {
                this.ClearPedigreeScanUnderLock("Logged out; pedigree readings cleared.");
                return;
            }
            if (now >= until) { this.StopPedigreeScan(); return; }
            if (now < this.nextScan) return;
            this.nextScan = now.AddMilliseconds(250);
            epoch = this.pedigreeEpoch;
        }
        var read = this.pedigreeReader.TryRead(out var sample, out var reason);
        lock (this.stateGate)
        {
            if (epoch != this.pedigreeEpoch || this.scanUntil is null) return;
            if (read && sample is not null)
            {
                var hadReading = this.pedigrees.HasReading(sample.Bag, sample.Slot);
                var previousRevision = this.pedigrees.AttachmentRevision;
                this.pedigreeStatus = this.pedigrees.Observe(sample);
                this.pedigreeCardsView = null;
                if (previousRevision != this.pedigrees.AttachmentRevision)
                {
                    if (hadReading) this.RevokeSavedPedigreeUnderLock(sample.Bag, sample.Slot);
                    // Only new or changed complete evidence schedules a refresh. Repeated
                    // stable hovers and Start/Stop leave the saved capture untouched.
                    if (this.pedigrees.HasReading(sample.Bag, sample.Slot)) this.Capture();
                }
            }
            else
            {
                this.pedigrees.ResetPending();
                this.pedigreeStatus = reason;
            }
        }
    }

    private void ClearPedigreeScanUnderLock(string status)
    {
        ++this.pedigreeEpoch;
        this.scanUntil = null;
        this.pedigrees.Clear();
        this.pedigreeCardsView = null;
        this.pedigreeStatus = status;
    }

    private void RevokeSavedPedigreeUnderLock(string bagName, uint slot)
    {
        // Contradictory tooltip evidence withdraws parentage even from the saved capture.
        // Ordinary inventory-change events still preserve that capture as historical data.
        if (this.snapshot is { } saved)
            this.snapshot = saved with { Containers = saved.Containers.Select(bag =>
            {
                if (bag.Type.ToString() != bagName) return bag;
                var clean = new ContainerSnapshot(bag.Type) { SlotCount = bag.SlotCount, UnavailableReason = bag.UnavailableReason };
                clean.Items.AddRange(bag.Items.Select(item => item.Slot == slot ? item with { Pedigree = null } : item));
                return clean;
            }).ToArray() };
        // Revocation can interrupt a capture requested by a different decoded card.
        // Keep the automatic refresh owed so that card is not stranded unattached.
        if (this.capturePending)
        {
            this.inventoryNeedsRefresh = true;
            this.nextInventoryCheck = DateTimeOffset.MinValue;
        }
        ++this.captureEpoch;
        this.capturePending = false;
        this.preparedQuestion = null;
        this.PersistContextUnderLock();
    }

    private static void RemovePedigrees(IEnumerable<ContainerSnapshot> containers)
    {
        foreach (var bag in containers)
            for (var i = 0; i < bag.Items.Count; i++)
                bag.Items[i] = bag.Items[i] with { Pedigree = null };
    }

    public void Dispose()
    {
        this.attachmentLoader.Dispose();
        this.DisposeContextCache();
        this.DisposeQuestTracking();
        this.DisposeStorageOverview();
        this.ClearInventoryHighlight();
        lock (this.stateGate) this.RevokeShopFindUnderLock("Plugin unloaded; shop actions cleared.");
        this.companion.Dispose();
        lock (this.stateGate) this.RemovePresetCommands();
        PluginInterface.UiBuilder.OpenMainUi -= this.OpenChat;
        PluginInterface.UiBuilder.OpenConfigUi -= this.OpenSettings;
        PluginInterface.UiBuilder.Draw -= this.windows.Draw;
        PluginInterface.UiBuilder.Draw -= this.DrawInventoryHighlight;
        PluginInterface.UiBuilder.Draw -= this.DrawShopHighlight;
        this.RemoveCoreCommands();
        Inventory.InventoryChangedRaw -= this.onInventoryChanged;
        ClientState.Logout -= this.onLogout;
        ClientState.Login -= this.OnContextLogin;
        ClientState.TerritoryChanged -= this.OnContextTerritoryChanged;
        Framework.Update -= this.OnFrameworkUpdate;
        AddonLifecycle.UnregisterListener(this.OnTalkCapture);
        AddonLifecycle.UnregisterListener(this.OnShopHighlightLifecycle);
        this.windows.RemoveAllWindows();
        this.chatWindow.Dispose();
    }
}

internal sealed class ContainerSnapshot
{
    internal ContainerSnapshot(GameInventoryType type) => this.Type = type;
    internal GameInventoryType Type { get; }
    internal int? SlotCount { get; set; }
    internal string? UnavailableReason { get; set; }
    internal List<ItemSnapshot> Items { get; } = new();
}

internal readonly record struct ItemSnapshot(uint Slot, uint ItemId, uint BaseItemId, string? Name, int Quantity, bool IsHq,
    ObservedPedigree? Pedigree = null);

internal sealed record SnapshotData(DateTimeOffset CapturedAtUtc, ContainerSnapshot[] Containers);

internal readonly record struct SnapshotView(SnapshotData? Snapshot, bool Pending, bool Changed, string? UnavailableReason);

internal sealed record PreparedQuestion(string Json, int Epoch, string Question, bool IncludeInventory,
    int HistoryRevision, bool Changed, Func<JsonElement, string>? QuestQuery, Func<JsonElement, string>? InventoryQuery,
    Func<JsonElement, string>? HistoryQuery, Func<JsonElement, string>? GameContextQuery,
    Func<JsonElement, string>? NpcDialogueQuery, Func<JsonElement, string>? CharacterDetailsQuery,
    Func<JsonElement, string>? PersonalityDetailsQuery, Func<JsonElement, string>? CurrencyQuery,
    Func<JsonElement, string>? DutyQuery, Func<JsonElement, string> GameDefinitionsQuery,
    Func<JsonElement, string>? GcSupplyQuery, Func<JsonElement, string>? NpcTargetQuery,
    string ChatId, IReadOnlyList<ChatAttachment> Attachments, Func<JsonElement, string>? StorageQuery,
    Func<JsonElement, string>? ValuationQuery, Func<JsonElement, string>? CollectionQuery,
    Func<JsonElement, string>? ProgressionQuery, Func<JsonElement, string>? CraftingLogQuery, Func<JsonElement, string>? MenuQuery, Func<JsonElement, string>? ActivityQuery);

internal sealed record ContextExportView(bool Busy, string Status, string? LastDirectory, bool HasData);
