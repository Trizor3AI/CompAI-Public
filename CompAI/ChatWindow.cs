using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.ImGuiFileDialog;

namespace CompAI;

internal sealed partial class ChatWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly ConnectionErrorClipboard errorClipboard = new();
    private readonly FileDialogManager attachmentPicker = new();
    private readonly ChatFont font = new();
    private readonly Dictionary<int, AnswerTextRenderer> renderers = new();
    private readonly Dictionary<int, AnswerTextRenderer> questionRenderers = new();
    private readonly AnswerTextRenderer pendingQuestionRenderer = new();
    private readonly AnswerTextRenderer pendingAnswerRenderer = new();
    private readonly Dictionary<int, TranscriptMessageLayout> messageLayouts = new();
    private bool lastInventoryHighlightEnabled;
    private readonly ChatScrollState transcriptScroll = new();
    private string? sendError;
    private string lastChatId = "";
    private string[]? pendingDeleteIds;
    private bool deletePopupOpened;
    private string? deleteError;
    private string chatSearch = "";
    private bool manageChats;
    private bool supportWebsiteOpenFailed;
    private readonly HashSet<string> selectedChats = new(StringComparer.Ordinal);
    private string? pendingRenameId;
    private string renameDraft = "";
    private bool renamePopupOpened;
    private string? renameError;
    private ChatTheme? windowTheme;
    private Vector2 contentFramePadding;
    private Vector2 contentInnerSpacing;
    private string notesDraft = "";
    private string notesChatId = "";
    private bool focusComposer;
    private int composerInputRevision;
    private string? lastComposerDraft;
    private bool composerWasActive;
    private readonly ChatInputHistory inputHistory = new();
    private readonly ImGui.ImGuiInputTextCallbackDelegate inputCallback;
    private int composerCursor;
    private int composerSelectionStart;
    private int composerSelectionEnd;
    private bool composerCaretValid;

    internal ChatWindow(Plugin plugin) : base("CompAI##ffchat")
    {
        this.plugin = plugin;
        this.inputCallback = this.ObserveComposerCaret;
        this.Size = new Vector2(900, 610);
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(760, 600),
            MaximumSize = new Vector2(4096, 4096),
        };
    }

    public override void PreDraw()
    {
        this.windowTheme = ChatTheme.Push();
        this.contentFramePadding = ImGui.GetStyle().FramePadding;
        this.contentInnerSpacing = ImGui.GetStyle().ItemInnerSpacing;
        // Installed ImGui 1.88 spaces close/collapse by font size alone.
        // Dalamud adds ItemInnerSpacing twice before its menu button. Equal hit-box
        // pitch still leaves unequal visible gaps: the 24px-title-font screenshot
        // has 7px before collapse and 9px before close. Offset the menu ink 2px left
        // (one extra inner-spacing unit), scaled with the title font.
        // Retain this through the host's title-bar draw, including collapsed.
        const float titlePaddingX = 4f;
        var menuInkSpacing = ImGui.GetFontSize() / 24f;
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(titlePaddingX, this.contentFramePadding.Y));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing,
            new Vector2(titlePaddingX / 2f + menuInkSpacing, this.contentInnerSpacing.Y));
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(2);
        this.windowTheme?.Dispose();
        this.windowTheme = null;
    }

    public override void Draw()
    {
        // Title-bar spacing does not change the chat controls.
        using var contentStyle = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, this.contentFramePadding)
            .Push(ImGuiStyleVar.ItemInnerSpacing, this.contentInnerSpacing);
        this.plugin.OpenPendingLoginUrl();
        using (this.font.Push())
        {
            var companion = this.plugin.CompanionView;
            var width = Math.Min(210f, Math.Max(155f, ImGui.GetContentRegionAvail().X * 0.25f));
            var contentStart = ImGui.GetCursorScreenPos();
            // Keep breathing room on both sides, including with larger chat fonts.
            var columnGap = Math.Max(24f, ImGui.GetFontSize() * 1.5f);
            var dividerX = contentStart.X + width + columnGap / 2;
            var windowPosition = ImGui.GetWindowPos();
            var border = ImGui.GetStyle().WindowBorderSize;
            var dividerTop = contentStart.Y - ImGui.GetStyle().WindowPadding.Y;
            var dividerBottom = windowPosition.Y + ImGui.GetWindowSize().Y - border;
            var draw = ImGui.GetWindowDrawList();
            // The normal content clip excludes padding; extend this decoration to
            // the title bar and bottom frame while staying inside this window.
            draw.PushClipRect(new Vector2(windowPosition.X + border, dividerTop),
                new Vector2(windowPosition.X + ImGui.GetWindowSize().X - border, dividerBottom), false);
            draw.AddLine(new Vector2(dividerX, dividerTop), new Vector2(dividerX, dividerBottom),
                ImGui.GetColorU32(ChatTheme.MutedGold), 1f);
            draw.PopClipRect();
            ImGui.BeginChild("##chat_sidebar", new Vector2(width, 0), false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            this.DrawSidebar(ref companion);
            ImGui.EndChild();
            ImGui.SameLine(0, columnGap);
            ImGui.BeginChild("##chat_main", new Vector2(0, 0), false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            // Only the transcript scrolls. Clear an offset retained by earlier
            // layouts so the header and composer never move out of view.
            ImGui.SetScrollY(0);
            this.DrawMain(companion);
            ImGui.EndChild();
        }
        // Match the settings dialogs: create the modal with Dalamud's default
        // font/atlas, outside the custom transcript font's lifetime.
        this.DrawDeleteConfirmation();
        this.DrawRenameConfirmation();
        this.attachmentPicker.Draw();
    }

    private void DrawSidebar(ref CompanionView companion)
    {
        ImGui.SetScrollY(0);
        if (ChatTheme.Button("New chat", new Vector2(-1, 0)) && this.plugin.NewChat())
        {
            this.ResetViewForChat();
            companion = this.plugin.CompanionView;
        }
        var clearSearchWidth = ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(Math.Max(1, ImGui.GetContentRegionAvail().X - clearSearchWidth - ImGui.GetStyle().ItemSpacing.X));
        ImGui.InputTextWithHint("##chat_search", "Search chats", ref this.chatSearch, ChatStore.MaxSearchQueryChars);
        ImGui.SameLine();
        ImGui.BeginDisabled(this.chatSearch.Length == 0);
        if (ChatTheme.Button("X##clear_chat_search", new Vector2(clearSearchWidth, 0))) this.chatSearch = string.Empty;
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("Clear chat search");
        ImGui.Separator();
        var showSupport = !this.plugin.HideKofi;
        var supportSize = showSupport ? SupportButton.GetSize(ImGui.GetContentRegionAvail().X) : Vector2.Zero;
        var footerHeight = ImGui.GetFrameHeightWithSpacing() * 2 + ImGui.GetStyle().ItemSpacing.Y * 2 + 2;
        ImGui.BeginChild("##conversation_list", new Vector2(0, Math.Max(1, ImGui.GetContentRegionAvail().Y - footerHeight)), false);
        var autoSort = this.plugin.AutoSortChats;
        var search = this.plugin.SearchChats(this.chatSearch);
        var runningIds = companion.RunningChatIds;
        if (this.manageChats)
        {
            if (ImGui.SmallButton("Select results"))
                foreach (var chat in search.Chats.Where(c => !c.HasUnsavedChanges && !runningIds.Contains(c.Id)))
                    this.selectedChats.Add(chat.Id);
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear")) this.selectedChats.Clear();
            ImGui.BeginDisabled(this.selectedChats.Count == 0);
            if (ChatTheme.Button($"Delete selected ({this.selectedChats.Count})", new Vector2(-1, 0)))
            {
                this.pendingDeleteIds = this.selectedChats.ToArray();
                this.deleteError = null;
            }
            ImGui.EndDisabled();
        }
        if (!string.IsNullOrWhiteSpace(this.chatSearch))
        {
            ImGui.TextColored(ChatTheme.MutedGold, $"Matches ({search.Chats.Count})");
            foreach (var chat in search.Chats) this.DrawChatRow(chat, ref companion);
        }
        else
        {
            var pinned = search.Chats.Where(c => c.IsPinned).ToArray();
            var allowDrag = !this.manageChats && (!autoSort || pinned.Length > 0);
            if (pinned.Length > 0)
                this.DrawChatGroupHeading("Pinned", search.Chats, allowDrag, pinned: true);
            foreach (var chat in pinned) this.DrawChatRow(chat, ref companion, allowDrag);
            this.DrawChatGroupHeading(autoSort ? "Recent" : "Your order", search.Chats, allowDrag, pinned: false);
            foreach (var chat in search.Chats.Where(c => !c.IsPinned && !c.IsArchived))
                this.DrawChatRow(chat, ref companion, allowDrag);
            var older = search.Chats.Where(c => c.IsArchived).ToArray();
            if (older.Length > 0)
            {
                var expanded = ImGui.CollapsingHeader($"Older ({older.Length})");
                if (allowDrag) this.AcceptChatDrop(search.Chats, null, headerPinned: false);
                if (expanded)
                    foreach (var chat in older) this.DrawChatRow(chat, ref companion, allowDrag);
            }
            if (allowDrag)
            {
                ImGui.Dummy(new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetFrameHeight()));
                this.AcceptChatDrop(search.Chats, null);
            }
        }
        if (search.IsTruncated) ImGui.TextWrapped("Search limit reached. Try a more specific phrase.");
        if (search.Error is { } searchError) ImGui.TextWrapped(searchError);
        if (this.plugin.ChatOrderError is { } orderError) ImGui.TextWrapped(orderError);
        if (companion.StorageError is { } storageError)
            ImGui.TextWrapped("Chat storage: " + storageError);
        var storage = this.plugin.ChatStorageInfo;
        if (storage.CapacityWarning is { } capacityWarning) ImGui.TextWrapped(capacityWarning);
        if (storage.UnsavedChats > 0)
            ImGui.TextWrapped($"{storage.UnsavedChats} chat(s) have unsaved changes. Keep the plugin open until saved or exported.");
        if (storage.RecoveryNotice is { } recoveryNotice) ImGui.TextWrapped(recoveryNotice);
        if (storage.CapacityWarning is not null || companion.StorageError is not null || storage.UnsavedChats > 0)
        {
            ImGui.BeginDisabled(companion.ActiveChatBusy);
            if (ChatTheme.Button("Export this chat")) this.plugin.ExportActiveChat();
            ImGui.EndDisabled();
        }
        ImGui.EndChild();
        ImGui.Separator();
        var sortRowStart = ImGui.GetCursorPos();
        var sortRowWidth = ImGui.GetContentRegionAvail().X;
        var sortLabel = "Auto-sort chats";
        var availableLabelWidth = sortRowWidth - ImGui.GetFrameHeight() - ImGui.GetStyle().ItemInnerSpacing.X
            - (showSupport ? supportSize.X + ImGui.GetStyle().ItemSpacing.X : 0);
        if (ImGui.CalcTextSize(sortLabel).X > availableLabelWidth) sortLabel = "Auto-sort";
        if (ImGui.CalcTextSize(sortLabel).X > availableLabelWidth) sortLabel = string.Empty;
        if (ImGui.Checkbox(sortLabel + "###auto_sort_chats", ref autoSort)) this.plugin.SetAutoSortChats(autoSort);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Auto-sort chats. On: most recently used first. Off: creation order, then your saved drag-and-drop order. Older chats use their saved activity date because their creation date was not recorded.");
        if (showSupport)
        {
            ImGui.SameLine(sortRowStart.X + sortRowWidth - supportSize.X);
            ImGui.SetCursorPosY(sortRowStart.Y);
            SupportButton.Draw(supportSize, ref this.supportWebsiteOpenFailed);
        }
        var halfWidth = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2;
        if (ChatTheme.Button("Settings", new Vector2(halfWidth, 0))) this.plugin.OpenSettings();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Account, personalization and access settings");
        ImGui.SameLine();
        if (ChatTheme.Button(this.manageChats ? "Done" : "Manage", new Vector2(-1, 0)))
        {
            this.manageChats = !this.manageChats;
            this.selectedChats.Clear();
        }
    }

    private void DrawChatGroupHeading(string label, IReadOnlyList<ChatSummary> chats, bool allowDrag, bool pinned)
    {
        var start = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetTextLineHeight()));
        ImGui.GetWindowDrawList().AddText(start, ImGui.GetColorU32(ChatTheme.MutedGold), label);
        if (allowDrag) this.AcceptChatDrop(chats, null, headerPinned: pinned);
    }

    private unsafe void DrawChatRow(ChatSummary chat, ref CompanionView companion, bool allowDrag = false)
    {
        ImGui.PushID(chat.Id);
        var running = companion.RunningChatIds.Contains(chat.Id);
        if (this.manageChats)
        {
            var selected = this.selectedChats.Contains(chat.Id);
            ImGui.BeginDisabled(running || chat.HasUnsavedChanges);
            if (ImGui.Checkbox("##select_chat", ref selected))
            {
                if (selected) this.selectedChats.Add(chat.Id);
                else this.selectedChats.Remove(chat.Id);
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
        }
        var title = chat.Title.Length > 28 ? chat.Title[..25] + "..." : chat.Title;
        if (chat.HasUnsavedChanges) title = "! " + title;
        if (running) title = "… " + title;
        var dragging = allowDrag && ImGui.GetDragDropPayload().Handle != null;
        if (dragging)
        {
            // The insertion line, not a hovered row fill, identifies the drop position.
            var colour = chat.Id == companion.ActiveChatId ? ImGui.GetStyle().Colors[(int)ImGuiCol.Header] : Vector4.Zero;
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, colour);
            ImGui.PushStyleColor(ImGuiCol.HeaderActive, colour);
        }
        if (ImGui.Selectable(title.Replace("##", "# #", StringComparison.Ordinal),
                chat.Id == companion.ActiveChatId) && this.plugin.SelectChat(chat.Id))
        {
            this.ResetViewForChat();
            companion = this.plugin.CompanionView;
        }
        if (dragging) ImGui.PopStyleColor(2);
        if (!dragging && ImGui.IsItemHovered()) ImGui.SetTooltip(chat.Title + (chat.HasUnsavedChanges
            ? "\nNot saved to its chat file. Select it and export before unloading the plugin." : ""));
        if (allowDrag)
        {
            if (ImGui.BeginDragDropSource())
            {
                ImGui.SetDragDropPayload("COMPAI_CHAT_ORDER", Encoding.UTF8.GetBytes(chat.Id), ImGuiCond.Once);
                ImGui.TextUnformatted(chat.Title);
                ImGui.EndDragDropSource();
            }
            this.AcceptChatDrop(companion.Chats, chat);
        }
        if (ImGui.BeginPopupContextItem("##chat_actions"))
        {
            if (ImGui.MenuItem("Rename"))
            {
                this.pendingRenameId = chat.Id;
                this.renameDraft = chat.Title;
                this.renameError = null;
            }
            if (ImGui.MenuItem(chat.IsPinned ? "Unpin" : "Pin")) this.plugin.SetChatPinned(chat.Id, !chat.IsPinned);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Pinned chats do not expire. Unpinned chats inactive for 30 days may be removed on plugin load.");
            if (chat.Id == companion.ActiveChatId && ImGui.MenuItem("Export chat", "", false, !running))
                this.plugin.ExportActiveChat();
            if (ImGui.MenuItem("Delete chat", "", false, !running && !chat.HasUnsavedChanges))
            {
                this.pendingDeleteIds = new[] { chat.Id };
                this.deleteError = null;
            }
            ImGui.EndPopup();
        }
        ImGui.PopID();
    }

    private unsafe void AcceptChatDrop(IReadOnlyList<ChatSummary> chats, ChatSummary? destination, bool? headerPinned = null)
    {
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        if (!ImGui.BeginDragDropTarget()) return;
        try
        {
            var payload = ImGui.AcceptDragDropPayload("COMPAI_CHAT_ORDER",
                ImGuiDragDropFlags.AcceptBeforeDelivery | ImGuiDragDropFlags.AcceptNoDrawDefaultRect);
            if (payload.Handle == null || payload.DataSize is <= 0 or > 64) return;
            var id = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(payload.Data, payload.DataSize));
            var source = chats.FirstOrDefault(c => c.Id == id);
            if (source is null) return;
            var targetPinned = headerPinned ?? destination?.IsPinned ?? false;
            if (targetPinned != source.IsPinned)
            {
                ImGui.GetWindowDrawList().AddRect(min, max, ImGui.GetColorU32(ChatTheme.Gold), 2f);
                ImGui.SetTooltip(targetPinned ? "Drop to pin" :
                    "Drop to unpin and return to the normal sidebar order. Unpinned chats may expire on reload after 30 inactive days.");
                if (payload.IsDelivery()) this.plugin.SetChatPinned(id, targetPinned);
                return;
            }
            if (headerPinned.HasValue || this.plugin.AutoSortChats) return;
            var below = destination is not null && ImGui.GetIO().MousePos.Y >= (min.Y + max.Y) / 2;
            var y = below ? max.Y : min.Y;
            ImGui.GetWindowDrawList().AddLine(new Vector2(min.X, y), new Vector2(max.X, y),
                ImGui.GetColorU32(ChatTheme.Gold), 2f);
            if (payload.IsDelivery())
            {
                var beforeId = below && destination is not null
                    ? chats.SkipWhile(c => c.Id != destination.Id).Skip(1).FirstOrDefault(c => c.IsPinned == destination.IsPinned)?.Id
                    : destination?.Id;
                this.plugin.MoveChatBefore(id, beforeId);
            }
        }
        finally { ImGui.EndDragDropTarget(); }
    }

    private void DrawMain(CompanionView companion)
    {
        this.DrawStatusHeader(companion);
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var toolbar = this.GetComposerToolbarLayout(availableWidth);
        var spacing = ImGui.GetStyle().ItemSpacing.Y;
        // A fixed single-row header keeps completion from moving the transcript.
        // Detailed notices live in a popup, never in extra space below the input.
        var composerReserve = 70f + toolbar.Height + ImGui.GetFrameHeightWithSpacing() + spacing * 3 + 2f;
        var transcriptHeight = Math.Max(1f, ImGui.GetContentRegionAvail().Y - composerReserve);
        // Leave room between wrapped messages and the transcript's scrollbar.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,
            new Vector2(Math.Max(12f, ImGui.GetFontSize() * 0.75f), 0));
        ImGui.BeginChild("##chat_transcript", new Vector2(0, transcriptHeight), false,
            ImGuiWindowFlags.AlwaysUseWindowPadding);
        ImGui.PopStyleVar();
        this.DrawTranscript(companion);
        ImGui.EndChild();
        ImGui.Separator();
        this.DrawComposer(companion);
    }

    private void DrawTranscript(CompanionView companion)
    {
        if (this.lastChatId != companion.ActiveChatId) this.ResetViewForChat();
        var scrollY = ImGui.GetScrollY();
        var scrollMax = ImGui.GetScrollMaxY();
        var viewport = ImGui.GetWindowSize();
        var io = ImGui.GetIO();
        var hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        var scrollbarLeft = ImGui.GetWindowPos().X + viewport.X - ImGui.GetStyle().ScrollbarSize;
        var draggingScrollbar = hovered && ImGui.IsMouseDown(ImGuiMouseButton.Left) &&
            io.MousePos.X >= scrollbarLeft;
        var atBottom = scrollMax - scrollY <= 1f;
        var userScrolledAway = hovered && io.MouseWheel > 0 || draggingScrollbar && !atBottom;
        var userScrolledTowardBottom = hovered && io.MouseWheel < 0 || draggingScrollbar && atBottom;
        var style = ImGui.GetStyle();
        var layoutKey = new TranscriptLayoutKey(ImGui.GetContentRegionAvail().X,
            ImGui.GetTextLineHeight(), ImGui.GetFont().GetHashCode(), style.ItemSpacing,
            style.FramePadding, style.ItemInnerSpacing, style.IndentSpacing);
        var highlightEnabled = this.plugin.InventoryHighlightEnabled;
        if (highlightEnabled != this.lastInventoryHighlightEnabled)
        {
            this.messageLayouts.Clear();
            this.lastInventoryHighlightEnabled = highlightEnabled;
        }
        var viewportTop = ImGui.GetWindowPos().Y;
        var viewportBottom = viewportTop + viewport.Y;
        if (companion.History.Count == 0 && string.IsNullOrEmpty(companion.PendingQuestion))
            ImGui.TextWrapped(companion.IsSignedIn
                ? "Start a chat. Your saved conversations are in the sidebar."
                : "Sign in from Settings to send. Saved chats remain in the sidebar.");
        for (var i = 0; i < companion.History.Count; i++)
        {
            var turn = companion.History[i];
            if (!this.messageLayouts.TryGetValue(i, out var measured))
                this.messageLayouts[i] = measured = new TranscriptMessageLayout();
            var start = ImGui.GetCursorPosY();
            if (measured.CanSkip(turn, layoutKey, ImGui.GetCursorScreenPos().Y, viewportTop, viewportBottom))
            {
                ImGui.Dummy(new Vector2(0, measured.PlaceholderHeight(style.ItemSpacing.Y)));
                continue;
            }
            ImGui.PushID(i);
            ImGui.Spacing();
            ImGui.TextColored(ChatTheme.MutedGold, "You");
            if (!this.questionRenderers.TryGetValue(i, out var questionRenderer))
                this.questionRenderers[i] = questionRenderer = new AnswerTextRenderer();
            ImGui.PushID("question");
            questionRenderer.DrawQuestion(turn.Question, companion.OpenQuestionSource);
            ImGui.PopID();
            if (turn.ContextLabel?.Split('\n').FirstOrDefault(line => line.StartsWith("Explicit attachments: ", StringComparison.Ordinal)) is { } attached)
            {
                ImGui.TextColored(ChatTheme.MutedGold, "Attached files");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(attached + "\nFile contents are not saved in chat history.");
            }
            ImGui.Spacing();
            ImGui.TextColored(ChatTheme.Gold, turn.PersonalityName ?? "Companion");
            if (!this.renderers.TryGetValue(i, out var renderer))
            {
                renderer = new AnswerTextRenderer();
                this.renderers[i] = renderer;
            }
            ImGui.SameLine();
            renderer.DrawCopyActions(turn.Answer);
            this.DrawInventoryFind(turn);
            this.DrawShopFind(turn);
            renderer.Draw(turn.Answer, turn.Sources, companion.OpenQuestionSource, this.plugin.OpenChatMap);
            if (turn.ContextLabel?.Split('\n').FirstOrDefault(line => line.StartsWith("Market estimate: ", StringComparison.Ordinal)) is { } marketNotice)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ChatTheme.MutedGold);
                ImGui.TextWrapped(marketNotice);
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Prices refresh when requested; public quotes may be reused for 60 seconds.\nPossibly outdated means the upload was over an hour old or its time was unknown at lookup.\nA recent upload cannot guarantee current listings across every world. Saved answers do not refresh themselves.");
            }
            if (!string.IsNullOrEmpty(turn.ContextLabel) && ImGui.TreeNode("Context and sources"))
            {
                ImGui.TextWrapped(turn.ContextLabel);
                ImGui.TreePop();
            }
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.PopID();
            // Expanded context rows are part of the real measured height. A click
            // updates it in this frame; reopening a skipped row keeps ImGui state.
            measured.Remember(turn, layoutKey, ImGui.GetCursorPosY() - start);
        }
        if (!string.IsNullOrEmpty(companion.PendingQuestion))
        {
            var speaker = companion.PendingPersonalityName ?? "Companion";
            ImGui.Spacing();
            ImGui.TextColored(ChatTheme.MutedGold, "You");
            ImGui.PushID("pending_question");
            this.pendingQuestionRenderer.DrawQuestion(companion.PendingQuestion, companion.OpenQuestionSource);
            ImGui.PopID();
            if (companion.PendingAttachmentMetadata.Length > 0)
            {
                ImGui.TextColored(ChatTheme.MutedGold, "Files supplied with this question (contents not saved)");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(companion.PendingAttachmentMetadata);
            }
            if (companion.PartialAnswer.Length == 0) ImGui.TextUnformatted($"{speaker} is working...");
            else
            {
                ImGui.Spacing();
                ImGui.TextColored(ChatTheme.Gold, $"{speaker} · receiving...");
                // Use the final text layout while streaming too, so completion
                // does not replace wrapped raw Markdown with a shorter layout.
                // Sources and copy controls still belong to completed answers.
                this.pendingAnswerRenderer.Draw(companion.PartialAnswer, Array.Empty<QuestionSource>(), companion.OpenQuestionSource);
            }
        }
        // An explicit bottom anchor avoids using the last answer's tall item as
        // the scroll target. Reapply while the child's new bounds settle.
        ImGui.Dummy(Vector2.Zero);
        if (this.transcriptScroll.Update(companion.History.Count, companion.PendingQuestion,
                viewport, ImGui.GetCursorPosY(), scrollY, scrollMax, userScrolledAway, userScrolledTowardBottom))
            ImGui.SetScrollHereY(1f);
        this.lastChatId = companion.ActiveChatId;
    }

    private void DrawComposer(CompanionView companion)
    {
        if (this.plugin.TakeAttachmentError(companion.ActiveChatId) is { } attachmentError) this.sendError = attachmentError;
        this.DrawAttachments(companion);
        var storedDraft = this.plugin.ChatDraft;
        var draft = storedDraft;
        if (this.lastComposerDraft is not null && draft != this.lastComposerDraft)
        {
            // A send, recovery, or chat switch can replace the stored draft while the
            // native text field still owns an old editing buffer. Start a fresh widget
            // before drawing, so that buffer cannot overwrite the restored text.
            ++this.composerInputRevision;
            this.focusComposer |= this.composerWasActive;
            this.inputHistory.Reset();
            this.composerCaretValid = false;
        }
        var priorCaretValid = this.composerWasActive && this.composerCaretValid;
        var priorCursor = this.composerCursor;
        var priorSelectionStart = this.composerSelectionStart;
        var priorSelectionEnd = this.composerSelectionEnd;
        ImGui.PushID(this.composerInputRevision);
        if (this.focusComposer)
        {
            ImGui.SetKeyboardFocusHere();
            this.focusComposer = false;
        }
        // ImGui's native multiline handler owns validation and cursor editing. Select its
        // newline mode from Shift, rather than sending on a window-wide Enter key press.
        var inputFlags = ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CallbackAlways;
        var io = ImGui.GetIO();
        if (io.KeyShift == io.KeyCtrl) inputFlags |= ImGuiInputTextFlags.CtrlEnterForNewLine;
        var submitted = ImGui.InputTextMultiline("##chat_draft", ref draft, 2000, new Vector2(-1, 70), inputFlags, this.inputCallback);
        this.composerWasActive = ImGui.IsItemActive();
        if (this.composerWasActive && !companion.ActiveChatBusy && io.KeyCtrl && !io.KeyAlt &&
            ImGui.IsKeyPressed(ImGuiKey.V, false) && this.plugin.BeginPasteChatImage(companion.ActiveChatId) is { } pasteError)
            this.sendError = pasteError;
        if (draft != storedDraft) this.inputHistory.Reset();
        if (!submitted && this.composerWasActive && priorCaretValid && draft == storedDraft && !io.KeyShift && !io.KeyCtrl)
        {
            var older = ImGui.IsKeyPressed(ImGuiKey.UpArrow, false);
            var newer = ImGui.IsKeyPressed(ImGuiKey.DownArrow, false);
            if ((older || newer) && (io.KeyAlt || ChatInputHistory.AtBoundary(draft,
                    priorCursor, priorSelectionStart, priorSelectionEnd, older)) &&
                this.inputHistory.Move(older, draft, RecentQuestions(companion)) is { } recalled)
            {
                draft = recalled;
                ++this.composerInputRevision;
                this.focusComposer = true;
                this.composerCaretValid = false;
            }
        }
        this.lastComposerDraft = draft;
        ImGui.PopID();
        // Compare against the value read before drawing; an asynchronous restoration
        // arriving during this frame must survive when the user made no edit.
        if (draft != storedDraft)
        {
            this.plugin.ChatDraft = draft;
            this.sendError = null;
        }
        if (submitted) this.focusComposer = true;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Enter to send · Shift+Enter for a new line · Ctrl+V to paste text or a screenshot\nUp/Down at the first/last line recalls earlier questions; Alt+Up/Down works anywhere.\nDown past the newest restores your unsent draft.");
        var toolbarStartX = ImGui.GetCursorPosX();
        var toolbarWidth = ImGui.GetContentRegionAvail().X;
        var toolbar = this.GetComposerToolbarLayout(toolbarWidth);
        var attachmentBlock = this.plugin.AttachmentPending(companion.ActiveChatId) ? "Adding attachment..." :
            this.plugin.AttachmentBlockReason(this.plugin.ChatAttachments(companion.ActiveChatId));
        var canSend = this.plugin.CanSendChatQuestion(draft) && attachmentBlock is null;
        var sendEnabled = !companion.ActiveChatBusy && companion.CanTest && canSend;
        ImGui.BeginGroup();
        ImGui.BeginDisabled(!companion.ActiveChatBusy && !sendEnabled);
        var actionWidth = Math.Max(ImGui.CalcTextSize("Send").X, ImGui.CalcTextSize("Cancel").X) +
            ImGui.GetStyle().FramePadding.X * 2;
        var clickedSend = ChatTheme.Button(companion.ActiveChatBusy ? "Cancel" : "Send", new Vector2(actionWidth, 0));
        if (clickedSend && companion.ActiveChatBusy) this.plugin.CancelCompanion();
        if ((clickedSend || submitted) && sendEnabled)
        {
            if (this.plugin.SendAttachedChatQuestion(draft))
            {
                draft = this.plugin.ChatDraft;
                this.sendError = null;
                this.inputHistory.Reset();
            }
            else this.sendError = this.plugin.QuestionPreviewState;
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(companion.ActiveChatBusy
            ? "Cancel the current request."
            : !sendEnabled ? attachmentBlock ?? companion.SendBlockReason ?? this.plugin.GetChatQuestionBlockReason(draft) ?? "Enter a question to send."
            : "Uses Codex/Work allowance from your ChatGPT subscription and access enabled in Settings.");
        if (companion.RetryQuestion is { } retryQuestion)
        {
            ImGui.SameLine();
            var retryBlock = this.plugin.AttachmentBlockReason(this.plugin.RetryAttachments(companion.ActiveChatId));
            var retryEnabled = !companion.ActiveChatBusy && companion.CanTest && this.plugin.CanSendChatQuestion(retryQuestion) && retryBlock is null;
            ImGui.BeginDisabled(!retryEnabled);
            if (ChatTheme.Button("Retry") && retryEnabled)
            {
                if (this.plugin.RetryChatQuestion(companion.ActiveChatId, retryQuestion)) this.sendError = null;
                else this.sendError = this.plugin.QuestionPreviewState;
            }
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                var block = retryEnabled ? "" : "\n" + (retryBlock ?? companion.SendBlockReason ??
                    this.plugin.GetChatQuestionBlockReason(retryQuestion) ?? "Wait until the companion is ready.");
                ImGui.BeginTooltip();
                ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28f);
                ImGui.TextUnformatted("Resend this chat's last failed or cancelled question and its frozen attachments. Remove a restored attachment to exclude it from Retry. Uses subscription allowance; other draft text is preserved.\n" + retryQuestion + block);
                ImGui.PopTextWrapPos();
                ImGui.EndTooltip();
            }
        }
        ImGui.SameLine();
        this.DrawContextButton(companion);
        ImGui.SameLine();
        ImGui.BeginChild("##composer_allowance", new Vector2(toolbar.UsageWidth, ImGui.GetFrameHeight()), false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(this.plugin.AllowanceSummary
            .Replace("Primary", "P", StringComparison.Ordinal).Replace("Secondary", "S", StringComparison.Ordinal));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Percentages show allowance remaining. P/S mean primary/secondary when the window length is unknown.\n" + this.plugin.AllowanceDetails);
        ImGui.EndChild();
        ImGui.EndGroup();
        if (!toolbar.WrapPicker) ImGui.SameLine();
        ImGui.SetCursorPosX(toolbarStartX + Math.Max(0, toolbarWidth - toolbar.PickerSize.X));
        ModelPicker.DrawCompact(this.plugin, toolbar.PickerSize.X);
    }

    private void DrawAttachments(CompanionView companion)
    {
        var attachments = this.plugin.ChatAttachments(companion.ActiveChatId);
        ImGui.BeginDisabled(companion.ActiveChatBusy);
        if (ChatTheme.Button($"Files ({attachments.Count})")) ImGui.OpenPopup("chat_attachments");
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(ChatAttachment.Limits);
        ImGui.SameLine();
        var summary = this.plugin.AttachmentPending(companion.ActiveChatId) ? "Adding attachment..." : this.plugin.AttachmentBlockReason(attachments) ??
            (attachments.Count == 0 ? "Optional screenshots or text" : string.Join(", ", attachments.Select(a => a.Name)));
        ImGui.BeginChild("##attachment_summary", new Vector2(0, ImGui.GetFrameHeight()), false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(summary);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(summary);
        ImGui.EndChild();
        if (!ImGui.BeginPopup("chat_attachments")) return;
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30f);
        ImGui.TextWrapped(ChatAttachment.Limits);
        ImGui.Separator();
        for (var i = 0; i < attachments.Count; ++i)
        {
            ImGui.PushID(i);
            if (ImGui.SmallButton("Remove")) this.plugin.RemoveChatAttachment(companion.ActiveChatId, i);
            ImGui.SameLine();
            ImGui.TextUnformatted(attachments[i].Label);
            ImGui.PopID();
        }
        ImGui.BeginDisabled(companion.ActiveChatBusy || attachments.Count >= ChatAttachment.MaxCount);
        if (ChatTheme.Button("Choose file..."))
        {
            var chatId = companion.ActiveChatId;
            this.attachmentPicker.OpenFileDialog("Attach a screenshot or text file",
                "Supported files{.png,.jpg,.jpeg,.txt,.md,.log,.json}", (ok, path) =>
                {
                    if (ok) this.sendError = this.plugin.BeginAddChatAttachment(chatId, path);
                });
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();
        ImGui.PopTextWrapPos();
        ImGui.EndPopup();
    }

    private void DrawStatusHeader(CompanionView companion)
    {
        var draft = this.plugin.ChatDraft;
        var messages = this.ComposerMessages(companion, draft, this.plugin.CanSendChatQuestion(draft)).ToList();
        if (this.plugin.CoreCommandWarning is { } commandWarning) messages.Add(commandWarning);
        if (this.font.Status is { } fontStatus) messages.Add(fontStatus);
        if (this.plugin.ModelSettingsError is { } modelError) messages.Add(modelError);
        if (this.plugin.ModelDiscoveryError is { } discoveryError) messages.Add(discoveryError);
        var summary = companion.StatusSummary(messages.Count > 0);
        var height = ImGui.GetFrameHeight();
        ImGui.BeginChild("##chat_status_header", new Vector2(0, height), false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.AlignTextToFramePadding();
        // Keep the header to one clipped row even at the narrowest window width.
        // The tooltip retains every observation when trailing counts do not fit.
        ImGui.PushTextWrapPos(-1f);
        ImGui.TextUnformatted(summary);
        ImGui.PopTextWrapPos();
        var clicked = ImGui.IsItemClicked();
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30f);
            ImGui.TextUnformatted(companion.State);
            if (companion.PreviousRequestDetail.Length > 0) ImGui.TextUnformatted(companion.PreviousRequestDetail);
            if (companion.RequestDiagnostics.Length > 0) ImGui.TextUnformatted(companion.RequestDiagnostics);
            foreach (var message in messages.Distinct()) ImGui.TextUnformatted(message);
            ImGui.TextUnformatted("Click for account and model details.");
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
        ImGui.EndChild();
        if (clicked) ImGui.OpenPopup("##chat_status_details");
        var popupWidth = Math.Min(ImGui.GetFontSize() * 30f, ImGui.GetIO().DisplaySize.X - 40f);
        ImGui.SetNextWindowSizeConstraints(new Vector2(popupWidth, 0), new Vector2(popupWidth, float.MaxValue));
        if (ImGui.BeginPopup("##chat_status_details"))
        {
            ImGui.PushTextWrapPos(ImGui.GetWindowContentRegionMax().X);
            this.DrawStatus(companion);
            if (companion.PreviousRequestDetail.Length > 0) ImGui.TextWrapped(companion.PreviousRequestDetail);
            this.errorClipboard.Draw(companion.PreviousRequestError, "Copy previous error");
            foreach (var message in messages.Distinct()) ImGui.TextWrapped(message);
            ImGui.PopTextWrapPos();
            ImGui.EndPopup();
        }
    }

    private void DrawStatus(CompanionView companion)
    {
        // Ready is still useful information. Previously every branch was optional,
        // so clicking a healthy connection opened an empty auto-sized popup.
        ImGui.TextUnformatted("Connection");
        ImGui.TextWrapped(companion.State);
        this.errorClipboard.Draw(companion.ConnectionError);
        ImGui.TextWrapped("Account: " + companion.Account);
        ImGui.TextWrapped($"Questions in progress: {companion.ActiveRequestCount}/2");
        if (!companion.Busy && !companion.CanTest)
        {
            if (ChatTheme.Button("Refresh account")) this.plugin.CompanionAction("status");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Check sign-in and allowance. Does not resend your question.");
        }
        if (this.plugin.CoreCommandWarning is { } commandWarning) ImGui.TextWrapped(commandWarning);
        ModelPicker.DrawStatus(this.plugin);
        if (this.font.Status is { } fontStatus) ImGui.TextWrapped(fontStatus);
        if (!companion.IsSignedIn && !companion.Busy)
            ImGui.TextWrapped("Sign in to send; saved chats remain available.");
    }

    private int ObserveComposerCaret(ref ImGuiInputTextCallbackData data)
    {
        this.composerCursor = data.CursorPos;
        this.composerSelectionStart = data.SelectionStart;
        this.composerSelectionEnd = data.SelectionEnd;
        this.composerCaretValid = true;
        return 0;
    }

    private static IEnumerable<string> RecentQuestions(CompanionView companion)
    {
        string? previous = null;
        if (!string.IsNullOrWhiteSpace(companion.PendingQuestion))
            yield return previous = companion.PendingQuestion;
        else if (companion.RetryQuestion is { } retry)
            yield return previous = retry;
        for (var i = companion.History.Count - 1; i >= 0; --i)
        {
            var question = companion.History[i].Question;
            if (question == previous) continue;
            yield return previous = question;
        }
    }

    private string NextMessageSummary()
    {
        var info = this.plugin.ChatContextInfo;
        var notes = string.IsNullOrWhiteSpace(this.plugin.ChatNotes) ? "no notes" : "saved notes";
        var lookup = this.plugin.Access.ChatHistory ? "older lookup on" : "older lookup off";
        return $"Next message: {info.IncludedExchanges} recent exchanges + {notes}; {lookup}";
    }

    private void DrawContextButton(CompanionView companion)
    {
        if (ChatTheme.Button("Context"))
        {
            this.notesDraft = this.plugin.ChatNotes;
            this.notesChatId = companion.ActiveChatId;
            ImGui.OpenPopup("Next message context");
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(this.NextMessageSummary() + "\nOpen to view context and edit this chat's notes.");

        ImGui.SetNextWindowSize(new Vector2(Math.Min(520f, ImGui.GetWindowSize().X - 20f), 0), ImGuiCond.Always);
        if (!ImGui.BeginPopup("Next message context")) return;
        if (this.notesChatId != companion.ActiveChatId)
        {
            // Never carry an unsaved note into a different chat.
            this.notesDraft = this.plugin.ChatNotes;
            this.notesChatId = companion.ActiveChatId;
        }
        var info = this.plugin.ChatContextInfo;
        ImGui.TextUnformatted($"Recent context: {info.IncludedExchanges} of {info.TotalExchanges} exchanges");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Recent messages fit within a size budget. Older messages remain saved in the transcript.");
        if (info.Warning is { } warning) ImGui.TextWrapped(warning);
        ImGui.TextWrapped(this.plugin.Access.ChatHistory
            ? "Older messages: available on request"
            : "Older messages: lookup off (Settings > Access)");
        var storage = this.plugin.ChatStorageInfo;
        if (storage.CapacityWarning is { } capacityWarning) ImGui.TextWrapped(capacityWarning);
        if (ImGui.CollapsingHeader("Saved chat and exports"))
        {
            ImGui.TextWrapped($"{storage.Turns:N0} saved exchanges · approximately {storage.Bytes / (1024d * 1024):0.0} MiB");
            ImGui.BeginDisabled(companion.ActiveChatBusy);
            if (ChatTheme.Button("Export this chat")) this.plugin.ExportActiveChat();
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ChatTheme.Button("Open exports")) this.sendError = this.plugin.OpenChatExportFolder();
            ImGui.TextWrapped("Local exports include messages, draft, notes and personality. A new chat keeps this conversation in the sidebar but does not inherit its context.");
        }
        if (storage.ExportNotice is { } exportNotice)
        {
            ImGui.TextWrapped(exportNotice);
            if (storage.ExportPath is { } exportPath)
            {
                if (ImGui.SmallButton("Copy##export_path")) ImGui.SetClipboardText(exportPath);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Copy the exported file's path. This notice clears after 10 seconds.");
            }
        }
        if (storage.RecoveryNotice is { } recoveryNotice) ImGui.TextWrapped(recoveryNotice);
        ImGui.Separator();
        PersonalizationPanel.DrawChatPicker(this.plugin);
        ImGui.Separator();
        ImGui.TextUnformatted("Goal and notes for this chat");
        ImGui.TextWrapped("Included with each question. Add lasting goals or preferences; use Personalization for the companion’s voice.");
        ImGui.BeginDisabled(companion.ActiveChatBusy);
        ImGui.InputTextMultiline("##chat_notes", ref this.notesDraft, 2000, new Vector2(-1, 110));
        if (ChatTheme.Button("Save notes") && this.plugin.SaveChatNotes(this.notesDraft))
            ImGui.CloseCurrentPopup();
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ChatTheme.Button("Close")) ImGui.CloseCurrentPopup();
        if (companion.ActiveChatBusy) ImGui.TextWrapped("Notes can be edited after the current reply finishes.");
        if (this.plugin.ChatNotesError is { } error) ImGui.TextWrapped(error);
        ImGui.EndPopup();
    }

    private IEnumerable<string> ComposerMessages(CompanionView companion, string draft, bool canSend)
    {
        if (this.plugin.ChatCommandNotice is { } commandNotice) yield return commandNotice;
        var questionBlock = !canSend && !string.IsNullOrWhiteSpace(draft)
            ? this.plugin.GetChatQuestionBlockReason(draft) ?? "Question cannot be sent yet."
            : null;
        if (questionBlock is not null) yield return questionBlock;
        if (companion.SendBlockReason is { } sendBlock && !companion.ActiveChatBusy && sendBlock != questionBlock)
            yield return sendBlock;
        if (companion.SourceOpenError is { } sourceError) yield return sourceError;
        if (this.plugin.InventoryFindNotice is { } findNotice) yield return findNotice;
        if (this.plugin.ShopFindNotice is { } shopNotice) yield return shopNotice;
        if (this.sendError is { } error && error != questionBlock && error != companion.SendBlockReason)
            yield return error;
    }

    private ComposerToolbarLayout GetComposerToolbarLayout(float width)
    {
        var style = ImGui.GetStyle();
        var picker = ModelPicker.GetCompactSize(this.plugin, width);
        var actionsWidth = Math.Max(ImGui.CalcTextSize("Send").X, ImGui.CalcTextSize("Cancel").X) +
            ImGui.CalcTextSize("Context").X +
            style.FramePadding.X * 4 + style.ItemSpacing.X;
        // Keep the same wrapping when a completed/cancelled request adds Retry.
        actionsWidth += ImGui.CalcTextSize("Retry").X + style.FramePadding.X * 2 + style.ItemSpacing.X;
        // Reserve a stable allowance width even before status is known or a digit
        // changes, so completion cannot change wrapping and shift the transcript.
        var usageWidth = Math.Min(ImGui.CalcTextSize("Left: 5h 99.9%* · 7d 99.9%*").X + 2,
            Math.Max(1, width - actionsWidth - style.ItemSpacing.X));
        actionsWidth += usageWidth + style.ItemSpacing.X;
        var leftHeight = ImGui.GetFrameHeight();
        var preferredPickerWidth = ModelPicker.GetCompactSize(this.plugin, float.MaxValue).X;
        var wrapPicker = actionsWidth + style.ItemSpacing.X + preferredPickerWidth > width;
        var height = wrapPicker ? leftHeight + style.ItemSpacing.Y + picker.Y
            : Math.Max(leftHeight, picker.Y);
        return new(picker, wrapPicker, height, usageWidth);
    }

    private readonly record struct ComposerToolbarLayout(Vector2 PickerSize, bool WrapPicker, float Height, float UsageWidth);

    internal void FollowLatestMessage() => this.transcriptScroll.RequestFollow();

    private void ResetViewForChat()
    {
        this.sendError = null;
        this.renderers.Clear();
        this.questionRenderers.Clear();
        this.messageLayouts.Clear();
        this.lastChatId = "";
        this.transcriptScroll.Reset();
        this.notesDraft = "";
        this.notesChatId = "";
        this.focusComposer = false;
        this.lastComposerDraft = null;
        this.composerWasActive = false;
        this.composerCaretValid = false;
        this.inputHistory.Reset();
        ++this.composerInputRevision;
    }

    private void DrawDeleteConfirmation()
    {
        const string popupName = "Delete chat?###compai_delete_chat";
        if (this.pendingDeleteIds is null) return;
        if (!this.deletePopupOpened)
        {
            ImGui.OpenPopup(popupName);
            this.deletePopupOpened = true;
        }
        // The host supports multiple viewports; keep the modal on its parent's
        // surface so another viewport's foreground dim layer cannot cover it.
        ImGui.SetNextWindowViewport(ImGui.GetWindowViewport().ID);
        if (!ImGui.BeginPopupModal(popupName, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            this.pendingDeleteIds = null;
            this.deletePopupOpened = false;
            return;
        }
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 380f * ImGui.GetFontSize() / 17f);
        ImGui.TextWrapped($"Delete {this.pendingDeleteIds.Length} selected chat(s) and their messages? This cannot be undone. Exported copies are kept.");
        var summaries = this.plugin.CompanionView.Chats;
        foreach (var id in this.pendingDeleteIds.Take(5))
        {
            var chat = summaries.FirstOrDefault(c => c.Id == id);
            if (chat is not null) ImGui.TextWrapped("• " + chat.Title + (chat.IsPinned ? " (pinned)" : ""));
        }
        if (this.pendingDeleteIds.Length > 5) ImGui.TextUnformatted($"… and {this.pendingDeleteIds.Length - 5} more.");
        ImGui.TextWrapped("Chats with an active request or unsaved changes are protected.");
        if (ImGui.Button("Delete"))
        {
            var previousChatId = this.plugin.CompanionView.ActiveChatId;
            var result = this.plugin.DeleteSelectedChats(this.pendingDeleteIds);
            foreach (var id in result.DeletedIds) this.selectedChats.Remove(id);
            if (previousChatId != this.plugin.CompanionView.ActiveChatId) this.ResetViewForChat();
            if (result.Failures.Count == 0)
            {
                this.pendingDeleteIds = null;
                this.deletePopupOpened = false;
                ImGui.CloseCurrentPopup();
            }
            else
            {
                this.pendingDeleteIds = result.Failures.Select(f => f.Id).ToArray();
                this.deleteError = string.Join("\n", result.Failures.Select(f => f.Reason).Distinct());
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            this.pendingDeleteIds = null;
            this.deletePopupOpened = false;
            ImGui.CloseCurrentPopup();
        }
        if (this.deleteError is { } error) ImGui.TextWrapped(error);
        ImGui.PopTextWrapPos();
        ImGui.EndPopup();
    }

    private void DrawRenameConfirmation()
    {
        if (this.pendingRenameId is null) return;
        const string popup = "Rename chat###compai_rename_chat";
        if (!this.renamePopupOpened) { ImGui.OpenPopup(popup); this.renamePopupOpened = true; }
        ImGui.SetNextWindowViewport(ImGui.GetWindowViewport().ID);
        if (!ImGui.BeginPopupModal(popup, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            this.pendingRenameId = null;
            this.renamePopupOpened = false;
            return;
        }
        ImGui.SetNextItemWidth(320);
        var save = ImGui.InputText("##chat_name", ref this.renameDraft, ChatStore.MaxCustomTitleChars, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.TextUnformatted("Leave blank to restore the automatic title.");
        if (ImGui.Button("Save") || save)
        {
            if (this.plugin.RenameChat(this.pendingRenameId, this.renameDraft))
            {
                this.pendingRenameId = null;
                this.renamePopupOpened = false;
                ImGui.CloseCurrentPopup();
            }
            else this.renameError = this.plugin.ChatOrderError ?? "The chat name could not be saved.";
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            this.pendingRenameId = null;
            this.renamePopupOpened = false;
            ImGui.CloseCurrentPopup();
        }
        if (this.renameError is { } error) ImGui.TextWrapped(error);
        ImGui.EndPopup();
    }

    public override void OnClose() => this.plugin.FlushChatDraft();

    public void Dispose() { this.attachmentPicker.Reset(); this.font.Dispose(); }
}
