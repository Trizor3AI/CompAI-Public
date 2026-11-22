using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CompAI;

internal sealed record ChatSummary(string Id, string Title, DateTimeOffset UpdatedAtUtc, bool IsArchived,
    bool HasUnsavedChanges = false, bool IsPinned = false, bool HasCustomTitle = false);
internal sealed record ChatSearchResult(IReadOnlyList<ChatSummary> Chats, bool IsTruncated = false, string? Error = null);
internal sealed record ChatDeleteFailure(string Id, string Reason);
internal sealed record ChatDeleteResult(IReadOnlyList<string> DeletedIds, IReadOnlyList<ChatDeleteFailure> Failures);
internal sealed record ChatStorageInfo(long Bytes, int Turns, string? CapacityWarning,
    string? SendBlockReason, int UnsavedChats, string? RecoveryNotice, string? ExportNotice = null,
    string? ExportPath = null);

// One UTF-8 JSON file per chat, including its model choice, personality snapshot, notes, turns and draft.
// Callers serialize access through CompanionConnection's gate.
internal sealed class ChatStore
{
    internal const int MaxFileBytes = 16 * 1024 * 1024;
    internal const int SendLimitBytes = 14 * 1024 * 1024;
    internal static readonly TimeSpan DraftDebounce = TimeSpan.FromMilliseconds(750);
    internal static readonly TimeSpan ExportNoticeLifetime = TimeSpan.FromSeconds(10);
    private const int MaxDraftChars = 2000;
    internal const int MaxNotesChars = 2000;
    private const int RecentCount = 10;
    internal const int MaxSidebarOrderBytes = 512 * 1024;
    internal const int MaxSidebarOrderEntries = 8192;
    internal const int MaxCustomTitleChars = 56;
    internal const int MaxSearchQueryChars = 120;
    internal const int MaxSearchCharacters = 8 * 1024 * 1024;
    internal const int MaxSearchResults = 500;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions DiskJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private readonly string directory;
    private readonly string exportDirectory;
    private readonly Func<DateTimeOffset> clock;
    private readonly Func<CompanionModelSettings?> defaultModelSelection;
    private readonly Func<PersonalityPreset> defaultPersonality;
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly List<string> chatOrder = new();
    private readonly Dictionary<string, ChatProperties> chatProperties = new(StringComparer.Ordinal);
    private IReadOnlyList<ChatSummary>? cachedView;
    private IReadOnlyList<ChatSummary>? searchSource;
    private readonly Dictionary<string, int> searchRevisions = new(StringComparer.Ordinal);
    private string? searchQuery;
    private ChatSearchResult? searchResult;
    private bool storageAvailable = true;
    private bool chatOrderReadable = true;
    private Entry active = null!;

    internal string ActiveId => this.active.Id;
    internal ChatHistory ActiveHistory => this.active.History;
    internal string ActiveDraft => this.active.Draft;
    internal string ActiveNotes => this.active.Notes;
    internal string? ActiveNotesError => this.active.NotesError;
    internal CompanionModelSettings? ActiveModelSelection => this.active.ModelSelection;
    internal string? ActiveModelError => this.active.ModelError;
    internal PersonalityPreset ActivePersonality => this.active.Personality;
    internal bool ActivePersonalityIsValid => this.active.PersonalityIsValid;
    internal string? ActivePersonalityError => this.active.PersonalityError;
    internal string? Error { get; private set; }
    internal bool AutoSortChats { get; private set; } = true;
    internal string? ChatOrderError { get; private set; }
    private string? errorOwner;
    private string? recoveryNotice;
    private string? exportNotice;
    private string? exportPath;
    private string? exportNoticeOwner;
    private DateTimeOffset exportNoticeUntil;
    internal int TranscriptSerializations { get; private set; }
    internal int SearchScans { get; private set; }
    internal ChatStorageInfo StorageInfo
    {
        get
        {
            this.Measure(this.active);
            var bytes = this.active.MeasuredBytes;
            var turns = this.active.History.View.Count;
            var blocked = turns >= ChatHistory.MaxStoredTurns
                ? "This chat has reached 1,000 exchanges. Export it if needed, then start another chat to continue."
                : bytes >= SendLimitBytes
                    ? "This chat is near its 16 MiB file limit. Export it if needed, then start another chat so the next reply has room to save."
                    : null;
            var warning = blocked ?? (turns >= 900 || bytes >= 12 * 1024 * 1024
                ? "This chat is approaching its storage limit. Export it and start another chat soon." : null);
            var recentExport = this.exportNoticeOwner == this.active.Id && this.Now() < this.exportNoticeUntil
                ? this.exportNotice : null;
            return new(bytes, turns, warning, blocked, this.entries.Values.Count(entry => entry.SaveFailed && IsDirty(entry)),
                this.recoveryNotice, recentExport, recentExport is null ? null : this.exportPath);
        }
    }
    internal IReadOnlyList<ChatSummary> View
    {
        get
        {
            if (this.cachedView is not null) return this.cachedView;
            var ordinaryIndex = 0;
            this.cachedView = Array.AsReadOnly(this.OrderedEntries().OrderByDescending(x => this.IsPinned(x.Id))
                .Select(x => new ChatSummary(x.Id, this.DisplayTitle(x), x.UpdatedAtUtc,
                    !this.IsPinned(x.Id) && ordinaryIndex++ >= RecentCount, x.SaveFailed && IsDirty(x),
                    this.IsPinned(x.Id), this.chatProperties.GetValueOrDefault(x.Id)?.CustomTitle is not null)).ToArray());
            return this.cachedView;
        }
    }

    private bool IsPinned(string id) => this.chatProperties.GetValueOrDefault(id)?.IsPinned == true;
    private string DisplayTitle(Entry entry) => this.chatProperties.GetValueOrDefault(entry.Id)?.CustomTitle ?? this.Title(entry);

    private IEnumerable<Entry> OrderedEntries() => this.AutoSortChats
        ? this.entries.Values.OrderByDescending(x => x.UpdatedAtUtc).ThenByDescending(x => x.Id, StringComparer.Ordinal)
        : this.chatOrder.Where(this.entries.ContainsKey).Select(id => this.entries[id]);

    internal ChatStore(string directory, Func<DateTimeOffset>? clock = null,
        Func<CompanionModelSettings?>? defaultModelSelection = null, Func<PersonalityPreset>? defaultPersonality = null,
        string? exportDirectory = null)
    {
        using var timing = PerformanceDiagnostics.Measure("storage.load");
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.defaultModelSelection = defaultModelSelection ?? (() => new CompanionModelSettings());
        this.defaultPersonality = defaultPersonality ?? (() => PersonalityPresets.None);
        this.exportDirectory = exportDirectory ?? Path.Combine(directory, "exports");
        try
        {
            this.directory = Path.GetFullPath(directory);
            Directory.CreateDirectory(this.directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            this.directory = "";
            this.storageAvailable = false;
            this.NoteError($"Chat storage unavailable; this session is in memory only ({ex.GetType().Name})");
            this.active = this.CreateEntry();
            this.active.SaveFailed = true;
            this.entries.Add(this.active.Id, this.active);
            this.chatOrder.Add(this.active.Id);
            return;
        }
        var now = this.Now();
        // A missing/corrupt/newer pin index must never cause possibly pinned chats
        // to expire. Leave all transcripts intact until the metadata is readable.
        this.chatOrderReadable = this.ReadChatOrder(out var sidebar);
        var indexedIds = sidebar?.OrderedChatIds.ToHashSet(StringComparer.Ordinal);
        var unreadableIds = new HashSet<string>(StringComparer.Ordinal);
        if (sidebar is not null)
            foreach (var properties in sidebar.ChatProperties) this.chatProperties.Add(properties.Id, properties);
        string[] paths;
        try { paths = Directory.GetFiles(this.directory, "*.json", SearchOption.TopDirectoryOnly); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            this.storageAvailable = false;
            this.NoteError($"Chat storage unavailable; this session is in memory only ({ex.GetType().Name})");
            this.active = this.CreateEntry();
            this.active.SaveFailed = true;
            this.entries.Add(this.active.Id, this.active);
            this.chatOrder.Add(this.active.Id);
            return;
        }
        // Every legacy chat receives the same startup default. Later changes only affect new chats.
        var migrationModel = this.ReadDefaultModel();
        foreach (var path in paths)
        {
            var id = Path.GetFileNameWithoutExtension(path);
            if (!ValidId(id)) continue;
            // An existing transcript without its index entry may have lost its pin.
            // Keep it protected on subsequent loads too, until the user unpins it.
            if (this.chatOrderReadable && sidebar?.SchemaVersion != 1 && indexedIds?.Contains(id) != true)
            {
                this.chatProperties[id] = new ChatProperties(id, null, true);
                this.recoveryNotice = "Some chats had missing sidebar information. They have been pinned to protect them from automatic deletion. Unpin any you want to expire normally.";
            }
            try
            {
                if (IsLink(path) || new FileInfo(path).Length > MaxFileBytes)
                    throw new InvalidDataException("Linked or oversized chat file");
                var saved = JsonSerializer.Deserialize<StoredChat>(File.ReadAllText(path, StrictUtf8));
                if (saved is null || saved.SchemaVersion is not (1 or 2 or 3 or 4) || saved.Id != id ||
                    saved.UpdatedAtUtc == default || saved.UpdatedAtUtc.Offset != TimeSpan.Zero ||
                    saved.UpdatedAtUtc > now.AddDays(1) || saved.Draft is null ||
                    saved.Draft.Length > MaxDraftChars || saved.Title is null ||
                    saved.Title.Length is < 1 or > 56 || saved.Turns is null ||
                    saved.SchemaVersion >= 3 && (saved.Notes is null || saved.Notes.Length > MaxNotesChars || saved.Notes.Contains('\0')))
                    throw new InvalidDataException("Invalid chat metadata");
                var history = new ChatHistory();
                history.Restore(saved.Turns);
                if (this.chatOrderReadable && !this.IsPinned(id) && now - saved.UpdatedAtUtc > TimeSpan.FromDays(30))
                {
                    File.Delete(path);
                    continue;
                }
                var model = saved.SchemaVersion == 1 ? migrationModel : ReadModelSelection(saved.ModelSelection);
                // Existing chats deliberately migrate to None, regardless of today's library default.
                var personality = saved.SchemaVersion < 4 ? PersonalityPresets.None : PersonalityPresets.ReadSnapshot(saved.Personality);
                var entry = new Entry(id, saved.Title, saved.UpdatedAtUtc, saved.Draft, history, model, personality)
                    { Notes = saved.SchemaVersion >= 3 ? saved.Notes! : "", Persisted = true };
                if (saved.SchemaVersion == 4 && saved.Personality.ValueKind != JsonValueKind.Undefined)
                    entry.StoredPersonality = saved.Personality;
                if (saved.SchemaVersion >= 2)
                {
                    // Preserve malformed model data while retaining readable history. An explicit
                    // valid model choice repairs it; a routine draft save must not silently do so.
                    if (saved.ModelSelection.ValueKind != JsonValueKind.Undefined)
                        entry.StoredModelSelection = saved.ModelSelection;
                    if (model is null)
                        entry.ModelError = "This chat has a missing or invalid saved model. Select a model and reasoning level before sending.";
                }
                this.entries.Add(id, entry);
                if (saved.SchemaVersion < 4 && !this.WriteChat(entry, saved.UpdatedAtUtc))
                {
                    entry.PersonalityError = "This chat's None personality could not be saved during migration. Select a personality to retry.";
                    if (saved.SchemaVersion == 1)
                    {
                        entry.ModelSelection = null;
                        entry.StoredModelSelection = JsonSerializer.SerializeToElement<CompanionModelSettings?>(null);
                        entry.ModelError = "This chat's migrated model could not be saved. Select a model and reasoning level to retry before sending.";
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                InvalidDataException or ArgumentException or DecoderFallbackException)
            {
                unreadableIds.Add(id);
                this.NoteError($"Could not load chat {id}: {ex.GetType().Name}");
            }
        }
        var selected = this.ReadActiveId();
        this.active = selected is not null && this.entries.TryGetValue(selected, out var found)
            ? found : this.entries.Values.OrderByDescending(x => x.UpdatedAtUtc).FirstOrDefault()!;
        if (this.active is null)
        {
            this.active = this.CreateEntry();
            this.entries.Add(this.active.Id, this.active);
            if (!this.WriteChat(this.active, this.active.UpdatedAtUtc))
            {
                this.active.SaveFailed = true;
                this.active.ModelSelection = null;
                this.active.StoredModelSelection = JsonSerializer.SerializeToElement<CompanionModelSettings?>(null);
                this.active.ModelError = "This chat's model could not be saved. Select a model and reasoning level to retry before sending.";
                this.active.PersonalityError = "This chat's personality could not be saved. Select a personality to retry.";
            }
        }
        this.InitializeChatOrder(sidebar, unreadableIds);
        // Selection does not update the chat's activity time or rewrite its file.
        try
        {
            if (Directory.Exists(this.exportDirectory) && Directory.EnumerateFiles(this.exportDirectory, "*-recovery.json").Any())
                this.recoveryNotice = (this.recoveryNotice is null ? "" : this.recoveryNotice + "\n") +
                    "Recovery exports from an earlier save failure are available in the chat export folder. They are complete external copies, not automatically restored chats.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal bool SetAutoSortChats(bool enabled)
    {
        if (enabled == this.AutoSortChats) return true;
        if (!this.WriteChatOrder(enabled, this.chatOrder)) return false;
        this.AutoSortChats = enabled;
        this.cachedView = null;
        return true;
    }

    internal bool MoveChatBefore(string id, string? beforeId)
    {
        if (this.AutoSortChats || !ValidId(id) || !this.entries.ContainsKey(id) ||
            beforeId is not null && (!ValidId(beforeId) || !this.entries.ContainsKey(beforeId))) return false;
        if (id == beforeId) return true;
        var proposed = this.chatOrder.Where(chatId => chatId != id).ToList();
        var index = beforeId is null ? proposed.Count : proposed.IndexOf(beforeId);
        proposed.Insert(index, id);
        if (proposed.SequenceEqual(this.chatOrder)) return true;
        if (!this.WriteChatOrder(this.AutoSortChats, proposed)) return false;
        this.chatOrder.Clear();
        this.chatOrder.AddRange(proposed);
        this.cachedView = null;
        return true;
    }

    // Metadata edits are local-only and do not count as transcript activity.
    // A blank name restores the original automatic title.
    internal bool RenameChat(string id, string? title)
    {
        if (!ValidId(id) || !this.entries.ContainsKey(id)) return false;
        title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        if (title is not null && !ValidCustomTitle(title))
        {
            this.ChatOrderError = $"A chat title must contain at most {MaxCustomTitleChars} characters and no control characters.";
            return false;
        }
        return this.UpdateChatProperties(id, title, this.IsPinned(id));
    }

    internal bool SetPinned(string id, bool pinned)
    {
        if (!ValidId(id) || !this.entries.ContainsKey(id)) return false;
        // Unpinning does not delete anything now. Ordinary age-based expiry occurs
        // only on the next load, so the caller can warn before unpinning old chats.
        return this.UpdateChatProperties(id, this.chatProperties.GetValueOrDefault(id)?.CustomTitle, pinned);
    }

    private static bool ValidCustomTitle(string title) => title.Length is > 0 and <= MaxCustomTitleChars &&
        title == title.Trim() && !title.Any(char.IsControl);

    private bool UpdateChatProperties(string id, string? title, bool pinned)
    {
        var proposed = new Dictionary<string, ChatProperties>(this.chatProperties, StringComparer.Ordinal);
        if (title is null && !pinned) proposed.Remove(id);
        else proposed[id] = new ChatProperties(id, title, pinned);
        if (!this.WriteChatOrder(this.AutoSortChats, this.chatOrder, proposed)) return false;
        this.chatProperties.Clear();
        foreach (var pair in proposed) this.chatProperties.Add(pair.Key, pair.Value);
        this.cachedView = null;
        return true;
    }

    internal ChatSearchResult SearchChats(string? query)
    {
        query = query?.Trim() ?? "";
        var source = this.View;
        // Repeated UI reads are bounded to cheap history revision checks; message
        // text is scanned only when the query or actual chat content changes.
        if (this.searchResult is not null && this.searchQuery == query && ReferenceEquals(this.searchSource, source) &&
            this.entries.Count == this.searchRevisions.Count && this.entries.Values.All(entry =>
                this.searchRevisions.TryGetValue(entry.Id, out var revision) && revision == entry.History.Revision))
            return this.searchResult;
        this.searchSource = source;
        this.searchQuery = query;
        this.searchRevisions.Clear();
        foreach (var entry in this.entries.Values) this.searchRevisions.Add(entry.Id, entry.History.Revision);
        if (query.Length == 0) return this.searchResult = new ChatSearchResult(source);
        if (query.Length > MaxSearchQueryChars || query.Any(char.IsControl))
            return this.searchResult = new ChatSearchResult(Array.Empty<ChatSummary>(), Error:
                $"Search must contain at most {MaxSearchQueryChars} characters and no control characters.");
        ++this.SearchScans;
        var matches = new HashSet<string>(StringComparer.Ordinal);
        // Titles remain searchable even when a very large transcript exhausts the
        // message budget. Notes, drafts and sources are deliberately not searched.
        foreach (var summary in source)
            if (summary.Title.Contains(query, StringComparison.OrdinalIgnoreCase)) matches.Add(summary.Id);
        var remaining = MaxSearchCharacters;
        var truncated = false;
        foreach (var summary in source)
        {
            if (matches.Contains(summary.Id)) continue;
            foreach (var turn in this.entries[summary.Id].History.View)
            {
                if (remaining == 0) { truncated = true; break; }
                if (Matches(turn.Question) || Matches(turn.Answer))
                {
                    matches.Add(summary.Id);
                    break;
                }
            }
        }
        var result = source.Where(summary => matches.Contains(summary.Id)).Take(MaxSearchResults).ToArray();
        return this.searchResult = new ChatSearchResult(Array.AsReadOnly(result), truncated || matches.Count > MaxSearchResults);

        bool Matches(string text)
        {
            var length = Math.Min(remaining, text.Length);
            remaining -= length;
            if (length < text.Length) truncated = true;
            return length >= query.Length && text.AsSpan(0, length).Contains(query.AsSpan(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private void InitializeChatOrder(SidebarOrder? saved, HashSet<string> unreadableIds)
    {
        if (saved is not null)
        {
            this.AutoSortChats = saved.AutoSortChats;
            this.chatOrder.AddRange(saved.OrderedChatIds.Where(id => this.entries.ContainsKey(id) || unreadableIds.Contains(id)));
        }
        // Legacy transcripts have no creation date. The first migration uses their
        // saved activity time, then an ID tie-break; new chats append at creation.
        // Persisting this independent index makes that initial order stable thereafter.
        var known = this.chatOrder.ToHashSet(StringComparer.Ordinal);
        this.chatOrder.AddRange(this.entries.Values.Where(entry => !known.Contains(entry.Id))
            .OrderBy(entry => entry.UpdatedAtUtc).ThenBy(entry => entry.Id, StringComparer.Ordinal).Select(entry => entry.Id));
        this.chatOrder.AddRange(unreadableIds.Where(id => !known.Contains(id)).Order(StringComparer.Ordinal));
        foreach (var id in this.chatProperties.Keys.Where(id => !this.entries.ContainsKey(id) && !unreadableIds.Contains(id)).ToArray())
            this.chatProperties.Remove(id);
        if (this.chatOrderReadable && (saved is null || saved.SchemaVersion != 2 || !saved.OrderedChatIds.SequenceEqual(this.chatOrder)))
            this.WriteChatOrder(this.AutoSortChats, this.chatOrder);
    }

    private void AddChatToOrder(string id)
    {
        this.chatOrder.Add(id);
        this.WriteChatOrder(this.AutoSortChats, this.chatOrder);
    }

    private void RemoveChatFromOrder(string id)
    {
        this.chatOrder.Remove(id);
        this.WriteChatOrder(this.AutoSortChats, this.chatOrder);
    }

    private bool ReadChatOrder(out SidebarOrder? saved)
    {
        saved = null;
        var path = Path.Combine(this.directory, "sidebar-order.json");
        try
        {
            if (Directory.Exists(path)) throw new InvalidDataException("Directory at sidebar order path");
            if (!File.Exists(path)) return true;
            if (IsLink(path)) throw new InvalidDataException("Linked sidebar order file");
            using var stream = File.OpenRead(path);
            if (stream.Length > MaxSidebarOrderBytes) throw new InvalidDataException("Oversized sidebar order file");
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw new InvalidDataException("Sidebar order file changed during read");
            using var document = JsonDocument.Parse(StrictUtf8.GetString(bytes));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid sidebar order");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name) || property.Name is not ("SchemaVersion" or "AutoSortChats" or "OrderedChatIds" or "ChatProperties"))
                    throw new InvalidDataException("Unsupported sidebar order fields");
            if (!root.TryGetProperty("SchemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out var version) || version is not (1 or 2) || names.Count != (version == 1 ? 3 : 4) ||
                !names.Contains("AutoSortChats") || !names.Contains("OrderedChatIds") ||
                version == 1 && names.Contains("ChatProperties") || version == 2 && !names.Contains("ChatProperties") ||
                root.GetProperty("AutoSortChats").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                root.GetProperty("OrderedChatIds").ValueKind != JsonValueKind.Array ||
                root.GetProperty("OrderedChatIds").GetArrayLength() > MaxSidebarOrderEntries)
                throw new InvalidDataException("Unsupported sidebar order schema");
            var ids = new List<string>();
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in root.GetProperty("OrderedChatIds").EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String || !ValidId(value.GetString()) || !unique.Add(value.GetString()!))
                    throw new InvalidDataException("Invalid sidebar chat ID");
                ids.Add(value.GetString()!);
            }
            var properties = new List<ChatProperties>();
            if (version == 2)
            {
                var values = root.GetProperty("ChatProperties");
                if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > MaxSidebarOrderEntries)
                    throw new InvalidDataException("Invalid chat properties");
                var propertyIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var value in values.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid chat properties");
                    var fields = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var field in value.EnumerateObject())
                        if (!fields.Add(field.Name) || field.Name is not ("Id" or "CustomTitle" or "IsPinned"))
                            throw new InvalidDataException("Unsupported chat properties");
                    if (fields.Count != 3 || value.GetProperty("Id").ValueKind != JsonValueKind.String ||
                        !ValidId(value.GetProperty("Id").GetString()) ||
                        !propertyIds.Add(value.GetProperty("Id").GetString()!) ||
                        !unique.Contains(value.GetProperty("Id").GetString()!) ||
                        value.GetProperty("CustomTitle").ValueKind is not (JsonValueKind.String or JsonValueKind.Null) ||
                        value.GetProperty("IsPinned").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new InvalidDataException("Invalid chat properties");
                    var title = value.GetProperty("CustomTitle").GetString();
                    if (title is not null && !ValidCustomTitle(title)) throw new InvalidDataException("Invalid custom title");
                    properties.Add(new ChatProperties(value.GetProperty("Id").GetString()!, title,
                        value.GetProperty("IsPinned").GetBoolean()));
                }
            }
            saved = new SidebarOrder(version, root.GetProperty("AutoSortChats").GetBoolean(), ids.ToArray(), properties.ToArray());
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            InvalidDataException or DecoderFallbackException or InvalidOperationException)
        {
            this.ChatOrderError = $"Chat titles, pins and ordering could not be loaded ({ex.GetType().Name}). Automatic expiry is paused. Restore sidebar-order.json and reload the plugin to recover the original settings, or move the file aside and reload to protect existing chats with new pins.";
            return false;
        }
    }

    private bool WriteChatOrder(bool autoSort, IReadOnlyList<string> order, IReadOnlyDictionary<string, ChatProperties>? properties = null)
    {
        if (!this.storageAvailable)
        {
            this.ChatOrderError = "Chat ordering could not be saved because chat storage is unavailable.";
            return false;
        }
        // Revalidate before every replacement so an unreadable or newer file is
        // preserved even if it changed after this store was constructed.
        if (!this.chatOrderReadable || !this.ReadChatOrder(out _)) return false;
        if (order.Count > MaxSidebarOrderEntries)
        {
            this.ChatOrderError = "Chat ordering could not be saved because the sidebar index has reached its size limit.";
            return false;
        }
        var values = properties ?? this.chatProperties;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new SidebarOrder(2, autoSort, order.ToArray(),
            order.Where(values.ContainsKey).Select(id => values[id]).ToArray()), DiskJson);
        if (bytes.Length > MaxSidebarOrderBytes)
        {
            this.ChatOrderError = "Chat ordering could not be saved because the sidebar index has reached its size limit.";
            return false;
        }
        var path = Path.Combine(this.directory, "sidebar-order.json");
        var temporary = Path.Combine(this.directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
            this.ChatOrderError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            this.ChatOrderError = $"Chat ordering could not be saved ({ex.GetType().Name}). Retry after fixing storage.";
            return false;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal void SetDraft(string draft)
    {
        if (draft is null || draft.Length > MaxDraftChars) throw new ArgumentOutOfRangeException(nameof(draft));
        if (draft == this.active.Draft) return;
        this.active.Draft = draft;
        this.active.DraftDirty = true;
        this.active.DraftSaveAt = this.Now() + DraftDebounce;
        if (this.active.MeasuredRevision == this.active.History.Revision)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(draft, DiskJson).Length;
            this.active.MeasuredBytes += bytes - this.active.MeasuredDraftBytes;
            this.active.MeasuredDraftBytes = bytes;
        }
    }

    internal bool FlushDraftIfDue() => this.active.DraftSaveAt is not { } due || this.Now() < due || this.SaveActive();

    internal void FlushAll()
    {
        foreach (var entry in this.entries.Values.Where(IsDirty)) this.Save(entry);
    }

    internal string? ExportActiveChat()
    {
        this.exportNotice = null;
        var path = Path.Combine(this.exportDirectory, $"{this.active.Id}-{Guid.NewGuid():N}.json");
        return this.Export(this.active, path, recovery: false) ? path : null;
    }

    internal bool SetActiveModelSelection(CompanionModelSettings value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!CompanionModelSelection.IsValid(value))
        {
            this.active.ModelError = "Invalid chat model or reasoning level. The previous selection remains in effect.";
            return false;
        }
        if (!this.storageAvailable)
        {
            this.active.ModelError = "Chat model was not saved because chat storage is unavailable. The previous selection remains in effect.";
            return false;
        }
        var contentChanged = this.active.History.Revision != this.active.SavedRevision || this.active.DraftDirty;
        var activityChanged = contentChanged || value != this.active.ModelSelection;
        var updatedAt = activityChanged ? this.Now() : this.active.UpdatedAtUtc;
        var savedModel = JsonSerializer.SerializeToElement(value);
        if (!this.WriteChat(this.active, updatedAt, savedModel))
        {
            this.active.ModelError = "Chat model was not saved. The previous selection remains in effect. Select it again to retry.";
            return false;
        }
        this.active.ModelSelection = value;
        this.active.StoredModelSelection = savedModel;
        this.active.ModelError = null;
        this.active.UpdatedAtUtc = updatedAt;
        this.active.SavedRevision = this.active.History.Revision;
        this.active.DraftDirty = false;
        this.ClearSaveFailure(this.active);
        if (activityChanged) this.cachedView = null;
        return true;
    }

    internal bool SetActiveNotes(string notes)
    {
        if (notes is null || notes.Length > MaxNotesChars || notes.Contains('\0'))
        {
            this.active.NotesError = "Chat notes must contain at most 2,000 characters. The previous notes remain in effect.";
            return false;
        }
        if (!this.storageAvailable)
        {
            this.active.NotesError = "Chat notes were not saved because chat storage is unavailable. The previous notes remain in effect.";
            return false;
        }
        var changed = notes != this.active.Notes || this.active.DraftDirty ||
            this.active.History.Revision != this.active.SavedRevision;
        var updatedAt = changed ? this.Now() : this.active.UpdatedAtUtc;
        if (!this.WriteChat(this.active, updatedAt, notes: notes))
        {
            this.active.NotesError = "Chat notes were not saved. The previous notes remain in effect. Retry after storage is available.";
            return false;
        }
        this.active.Notes = notes;
        this.active.NotesError = null;
        this.active.UpdatedAtUtc = updatedAt;
        this.active.SavedRevision = this.active.History.Revision;
        this.active.DraftDirty = false;
        this.ClearSaveFailure(this.active);
        if (changed) this.cachedView = null;
        return true;
    }

    internal bool SetActivePersonality(PersonalityPreset value)
    {
        if (!PersonalityPresets.IsValid(value))
        {
            this.active.PersonalityError = "Invalid chat personality. The previous personality remains in effect.";
            return false;
        }
        if (!this.storageAvailable)
        {
            this.active.PersonalityError = "Chat personality was not saved because chat storage is unavailable. The previous personality remains in effect.";
            return false;
        }
        var changed = value != this.active.Personality || this.active.DraftDirty ||
            this.active.History.Revision != this.active.SavedRevision;
        var updatedAt = changed ? this.Now() : this.active.UpdatedAtUtc;
        var savedPersonality = JsonSerializer.SerializeToElement(value);
        if (!this.WriteChat(this.active, updatedAt, personality: savedPersonality))
        {
            this.active.PersonalityError = "Chat personality was not saved. The previous personality remains in effect. Select it again to retry.";
            return false;
        }
        this.active.Personality = value;
        this.active.PersonalityIsValid = true;
        this.active.StoredPersonality = savedPersonality;
        this.active.PersonalityError = null;
        this.active.UpdatedAtUtc = updatedAt;
        this.active.SavedRevision = this.active.History.Revision;
        this.active.DraftDirty = false;
        this.ClearSaveFailure(this.active);
        if (changed) this.cachedView = null;
        return true;
    }

    internal bool SaveActive()
    {
        return this.Save(this.active);
    }

    // Completion/cancellation must address the originating chat even after the UI
    // changes selection. A failed save retains the appended answer in that entry;
    // true means accepted in memory and must not trigger a duplicate model retry.
    internal bool AppendAnswer(string id, string question, string answer, IReadOnlyList<QuestionSource> sources,
        string? context, bool includesInventory, string? personalityName, Guid? inventoryFindKey = null,
        Guid? shopFindKey = null)
    {
        if (!ValidId(id) || !this.entries.TryGetValue(id, out var entry)) return false;
        entry.History.Add(question, answer, sources, context, includesInventory, personalityName, inventoryFindKey, shopFindKey);
        this.Save(entry);
        return true;
    }

    internal bool RestoreDraftIfEmpty(string id, string draft)
    {
        if (!ValidId(id) || !this.entries.TryGetValue(id, out var entry)) return false;
        if (draft is null || draft.Length > MaxDraftChars) throw new ArgumentOutOfRangeException(nameof(draft));
        if (entry.Draft.Length != 0 || draft.Length == 0) return true;
        entry.Draft = draft;
        entry.DraftDirty = true;
        entry.MeasuredRevision = -1;
        this.Save(entry);
        return true;
    }

    private static bool IsDirty(Entry entry) => !entry.Persisted || entry.History.Revision != entry.SavedRevision || entry.DraftDirty;

    private bool Save(Entry entry)
    {
        if (!IsDirty(entry)) return true;
        using var timing = PerformanceDiagnostics.Measure("storage.save");
        var now = this.Now();
        entry.DraftSaveAt = null; // A failure is retried explicitly or at the final flush, not every frame.
        if (!this.storageAvailable || !this.WriteChat(entry, now))
        {
            entry.SaveFailed = true;
            this.cachedView = null;
            var recoveryPath = Path.Combine(this.exportDirectory, entry.Id + "-recovery.json");
            this.Export(entry, recoveryPath, recovery: true);
            this.errorOwner = entry.Id;
            return false;
        }
        entry.UpdatedAtUtc = now;
        entry.SavedRevision = entry.History.Revision;
        entry.DraftDirty = false;
        this.ClearSaveFailure(entry);
        this.cachedView = null;
        return true;
    }

    private void ClearSaveFailure(Entry entry)
    {
        if (!entry.SaveFailed) return;
        this.recoveryNotice = "This chat was saved successfully. Previous recovery exports remain in the chat export folder; any other unsaved chats are still marked.";
        if (this.errorOwner == entry.Id)
        {
            this.Error = null;
            this.errorOwner = null;
        }
        entry.SaveFailed = false;
        this.cachedView = null;
    }

    internal string NewChat()
    {
        this.SaveActive(); // The previous entry remains reachable even if it only exists in memory.
        var entry = this.CreateEntry();
        this.entries.Add(entry.Id, entry);
        this.Save(entry);
        this.AddChatToOrder(entry.Id);
        if (this.storageAvailable) this.WriteActiveId(entry.Id);
        this.cachedView = null;
        this.active = entry;
        return entry.Id;
    }

    internal bool Select(string id)
    {
        if (!ValidId(id) || !this.entries.TryGetValue(id, out var chosen)) return false;
        if (this.active.Id == id) return true;
        this.SaveActive();
        if (this.storageAvailable) this.WriteActiveId(id);
        this.active = chosen;
        return true;
    }

    internal bool ContinuePersonalityChat(PersonalityPreset preset)
    {
        if (!PersonalityPresets.IsValid(preset))
        {
            this.NoteError("Could not continue chat: the requested personality is invalid.");
            return false;
        }
        this.SaveActive();
        // Search the complete local store, including archives. A matching ID alone does
        // not establish the same personality after a library edit or template update.
        var chosen = this.entries.Values.Where(x => x.PersonalityIsValid && x.Personality == preset)
            .OrderByDescending(x => x.UpdatedAtUtc)
            .ThenByDescending(x => x.Id == this.active.Id)
            .ThenByDescending(x => x.Id, StringComparer.Ordinal).FirstOrDefault();
        var created = chosen is null;
        chosen ??= this.CreateEntry(preset);
        if (created)
        {
            this.entries.Add(chosen.Id, chosen);
            this.Save(chosen);
            this.AddChatToOrder(chosen.Id);
            this.cachedView = null;
        }
        if (this.storageAvailable) this.WriteActiveId(chosen.Id);
        this.active = chosen;
        return true;
    }

    internal ChatDeleteResult DeleteSelected(IEnumerable<string> selectedIds, IReadOnlySet<string>? protectedIds = null)
    {
        ArgumentNullException.ThrowIfNull(selectedIds);
        var selected = selectedIds.Take(MaxSidebarOrderEntries + 1).ToArray();
        if (selected.Length > MaxSidebarOrderEntries)
            return new ChatDeleteResult(Array.Empty<string>(), Array.AsReadOnly(new[]
                { new ChatDeleteFailure("", "Too many selected chats; no chats were deleted.") }));
        var deleted = new List<string>();
        var failures = new List<ChatDeleteFailure>();
        // Delete the active chat last so its replacement is outside the successful
        // selection, or is one fresh blank chat after a complete clear.
        foreach (var id in selected.Distinct(StringComparer.Ordinal).OrderBy(id => id == this.active.Id))
        {
            string? reason = null;
            if (!ValidId(id) || !this.entries.TryGetValue(id, out var entry)) reason = "This chat no longer exists.";
            else if (protectedIds?.Contains(id) == true) reason = "This chat has an in-flight request. Wait or cancel it first.";
            else if (entry.SaveFailed && IsDirty(entry)) reason = "This chat has unsaved changes after a save failure. Save it successfully before deleting it.";
            else if (!this.Delete(id)) reason = this.Error ?? "The chat could not be deleted. Its saved file and in-memory entry were retained.";
            if (reason is null) deleted.Add(id);
            else failures.Add(new ChatDeleteFailure(id ?? "", reason));
        }
        return new ChatDeleteResult(Array.AsReadOnly(deleted.ToArray()), Array.AsReadOnly(failures.ToArray()));
    }

    internal bool Delete(string id)
    {
        if (!ValidId(id) || !this.entries.ContainsKey(id)) return false;
        if (this.active.Id != id)
        {
            if (this.storageAvailable && !this.DeleteChatFile(id)) return false;
            this.entries.Remove(id);
            this.chatProperties.Remove(id);
            this.RemoveChatFromOrder(id);
            this.cachedView = null;
            return true;
        }
        var replacement = this.entries.Values.Where(x => x.Id != id)
            .OrderByDescending(x => x.UpdatedAtUtc).FirstOrDefault();
        var created = replacement is null;
        replacement ??= this.CreateEntry();
        if (this.storageAvailable)
        {
            var replacementPath = this.ChatPath(replacement.Id);
            if (created || !File.Exists(replacementPath))
            {
                if (!this.WriteChat(replacement, replacement.UpdatedAtUtc))
                {
                    if (created) this.RemoveIncompleteChat(replacement.Id);
                    return false;
                }
            }
            else if (IsLink(replacementPath))
            {
                this.NoteError("Replacement chat is a linked file; deletion was not started");
                return false;
            }
            if (!this.WriteActiveId(replacement.Id))
            {
                if (created) this.RemoveIncompleteChat(replacement.Id);
                return false;
            }
            if (!this.DeleteChatFile(id))
            {
                if (!this.WriteActiveId(id))
                    this.NoteError("Active chat index could not be restored after failed deletion");
                if (created) this.RemoveIncompleteChat(replacement.Id);
                return false;
            }
        }
        this.entries.Remove(id);
        this.chatProperties.Remove(id);
        if (created) this.entries.Add(replacement.Id, replacement);
        this.chatOrder.Remove(id);
        if (created) this.chatOrder.Add(replacement.Id);
        this.WriteChatOrder(this.AutoSortChats, this.chatOrder);
        this.cachedView = null;
        this.active = replacement;
        return true;
    }

    private bool DeleteChatFile(string id)
    {
        var path = this.ChatPath(id);
        try
        {
            if (File.Exists(path) && IsLink(path)) throw new InvalidDataException("Linked chat file");
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            this.NoteError($"Could not delete chat {id}: {ex.GetType().Name}");
            return false;
        }
    }

    private void RemoveIncompleteChat(string id)
    {
        if (!this.DeleteChatFile(id))
            this.NoteError($"Incomplete chat {id} remains on disk; it may appear after reload");
    }

    private Entry CreateEntry(PersonalityPreset? personality = null) => new(Guid.NewGuid().ToString("N"), "New chat", this.Now(), "",
        new ChatHistory(), this.ReadDefaultModel(), personality ?? this.ReadDefaultPersonality());

    private PersonalityPreset? ReadDefaultPersonality()
    {
        var value = this.defaultPersonality();
        return PersonalityPresets.IsValid(value) ? value : null;
    }

    private CompanionModelSettings? ReadDefaultModel()
    {
        var value = this.defaultModelSelection();
        return value is not null && CompanionModelSelection.IsValid(value) ? value : null;
    }

    private static CompanionModelSettings? ReadModelSelection(JsonElement saved)
    {
        if (saved.ValueKind != JsonValueKind.Object) return null;
        string? model = null;
        string? effort = null;
        string? provider = null;
        foreach (var property in saved.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String) return null;
            switch (property.Name)
            {
                case "Model" when model is null: model = property.Value.GetString(); break;
                case "Effort" when effort is null: effort = property.Value.GetString(); break;
                case "Provider" when provider is null: provider = property.Value.GetString(); break;
                default: return null;
            }
        }
        if (model is null || effort is null) return null;
        var value = new CompanionModelSettings(model, effort, provider ?? "codex");
        return CompanionModelSelection.IsValid(value) ? value : null;
    }

    private bool WriteChat(Entry entry, DateTimeOffset updatedAt, JsonElement? modelSelection = null, string? notes = null,
        JsonElement? personality = null)
    {
        var bytes = this.SerializeChat(entry, updatedAt, modelSelection, notes, personality);
        // The measurement is exact for ordinary writes. Failed proposed metadata changes
        // must not change the measurement of the still-active snapshot.
        if (modelSelection is null && notes is null && personality is null) this.RecordMeasurement(entry, bytes.Length);
        if (entry.History.View.Count > ChatHistory.MaxStoredTurns || bytes.Length > MaxFileBytes)
        {
            this.NoteError("Chat exceeds the 16 MiB or 1,000-exchange storage limit. Full content remains in memory; export it and start another chat.");
            return false;
        }
        if (!this.WriteBytes(this.ChatPath(entry.Id), bytes)) return false;
        entry.Persisted = true;
        entry.MeasuredRevision = -1;
        if (modelSelection is null && notes is null && personality is null) this.RecordMeasurement(entry, bytes.Length);
        var title = this.Title(entry);
        if (entry.Title != title)
        {
            entry.Title = title;
            this.cachedView = null;
        }
        return true;
    }

    private string Title(Entry entry) => entry.Title == "New chat" && entry.History.View.Count > 0
        ? ShortTitle(entry.History.View[0].Question) : entry.Title;

    private byte[] SerializeChat(Entry entry, DateTimeOffset updatedAt, JsonElement? modelSelection = null,
        string? notes = null, JsonElement? personality = null)
    {
        var turns = entry.History.Export();
        var saved = new StoredChat(4, entry.Id, this.Title(entry), updatedAt, entry.Draft, turns,
            modelSelection ?? entry.StoredModelSelection, notes ?? entry.Notes, personality ?? entry.StoredPersonality);
        ++this.TranscriptSerializations;
        return JsonSerializer.SerializeToUtf8Bytes(saved, DiskJson);
    }

    private void RecordMeasurement(Entry entry, int bytes)
    {
        entry.MeasuredBytes = bytes;
        entry.MeasuredRevision = entry.History.Revision;
        entry.MeasuredDraftBytes = JsonSerializer.SerializeToUtf8Bytes(entry.Draft, DiskJson).Length;
    }

    private void Measure(Entry entry)
    {
        if (entry.MeasuredRevision == entry.History.Revision) return;
        this.RecordMeasurement(entry, this.SerializeChat(entry, entry.UpdatedAtUtc).Length);
    }

    private bool Export(Entry entry, string path, bool recovery)
    {
        try
        {
            Directory.CreateDirectory(this.exportDirectory);
            // Exports deliberately have no chat-file limit: they preserve the complete
            // in-memory transcript for external reading, including exceptional overflow.
            var bytes = this.SerializeChat(entry, IsDirty(entry) ? this.Now() : entry.UpdatedAtUtc);
            if (!this.WriteBytes(path, bytes)) throw new IOException("Export could not be saved");
            if (recovery)
                this.recoveryNotice = $"A complete recovery export was saved to {path}. The chat still has unsaved changes; the export is an external copy and is not automatically restored.";
            else
            {
                // A successful manual export is a transient confirmation for its chat,
                // never a global recovery warning that follows the user between chats.
                this.exportNotice = $"Chat exported to {path}.";
                this.exportPath = path;
                this.exportNoticeOwner = entry.Id;
                this.exportNoticeUntil = this.Now() + ExportNoticeLifetime;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            this.recoveryNotice = $"Chat export failed ({ex.GetType().Name}). Unsaved work remains in memory only and will be lost when CompAI unloads. Keep this session open and retry export after fixing storage.";
            this.NoteError(this.recoveryNotice);
            return false;
        }
    }

    private string? ReadActiveId()
    {
        var path = Path.Combine(this.directory, "active.json");
        if (!File.Exists(path)) return null;
        try
        {
            if (IsLink(path) || new FileInfo(path).Length > 1024) throw new InvalidDataException();
            var index = JsonSerializer.Deserialize<ActiveIndex>(File.ReadAllText(path, StrictUtf8));
            if (index is null || index.SchemaVersion != 1 || !ValidId(index.ActiveId))
                throw new InvalidDataException();
            return index.ActiveId;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            InvalidDataException or DecoderFallbackException)
        {
            this.NoteError($"Could not load active chat index: {ex.GetType().Name}");
            return null;
        }
    }

    private bool WriteActiveId(string id) => this.WriteJson(Path.Combine(this.directory, "active.json"), new ActiveIndex(1, id));

    private bool WriteJson<T>(string path, T value)
    {
        return this.WriteBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, DiskJson));
    }

    private bool WriteBytes(string path, byte[] bytes)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            if (File.Exists(path) && IsLink(path)) throw new InvalidDataException("Linked destination file");
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            this.NoteError($"Could not save chat: {ex.GetType().Name}");
            return false;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private string ChatPath(string id)
    {
        if (!ValidId(id)) throw new ArgumentException("Invalid chat ID", nameof(id));
        return Path.Combine(this.directory, id + ".json");
    }

    private static bool ValidId(string? id) => id is { Length: 32 } &&
        id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static string ShortTitle(string question)
    {
        var oneLine = string.Join(" ", question.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (oneLine.Length == 0) return "New chat";
        return oneLine.Length <= 56 ? oneLine : oneLine[..56];
    }
    private DateTimeOffset Now() => this.clock().ToUniversalTime();
    private void NoteError(string message)
    {
        this.Error = message;
        this.errorOwner = null;
    }

    private sealed class Entry(string id, string title, DateTimeOffset updatedAtUtc, string draft,
        ChatHistory history, CompanionModelSettings? modelSelection, PersonalityPreset? personality)
    {
        internal string Id { get; } = id;
        internal string Title { get; set; } = title;
        internal DateTimeOffset UpdatedAtUtc { get; set; } = updatedAtUtc;
        internal string Draft { get; set; } = draft;
        internal string Notes { get; set; } = "";
        internal string? NotesError { get; set; }
        internal ChatHistory History { get; } = history;
        internal int SavedRevision { get; set; } = history.Revision;
        internal bool DraftDirty { get; set; }
        internal DateTimeOffset? DraftSaveAt { get; set; }
        internal bool Persisted { get; set; }
        internal bool SaveFailed { get; set; }
        internal int MeasuredRevision { get; set; } = -1;
        internal long MeasuredBytes { get; set; }
        internal int MeasuredDraftBytes { get; set; }
        internal CompanionModelSettings? ModelSelection { get; set; } = modelSelection;
        internal JsonElement StoredModelSelection { get; set; } = JsonSerializer.SerializeToElement(modelSelection);
        internal string? ModelError { get; set; } = modelSelection is null
            ? "This chat has no valid default model. Select a model and reasoning level before sending." : null;
        internal PersonalityPreset Personality { get; set; } = personality ?? PersonalityPresets.None;
        // The neutral display fallback must not turn an invalid saved/default snapshot into a valid selection.
        internal bool PersonalityIsValid { get; set; } = personality is not null;
        internal JsonElement StoredPersonality { get; set; } = JsonSerializer.SerializeToElement(personality);
        internal string? PersonalityError { get; set; } = personality is null
            ? "This chat has a missing or invalid saved personality. Select a personality before sending." : null;
    }

    private sealed record StoredChat(int SchemaVersion, string Id, string Title,
        DateTimeOffset UpdatedAtUtc, string Draft, ChatTurn[] Turns, JsonElement ModelSelection = default, string? Notes = null,
        JsonElement Personality = default);
    private sealed record ActiveIndex(int SchemaVersion, string ActiveId);
    private sealed record ChatProperties(string Id, string? CustomTitle, bool IsPinned);
    private sealed record SidebarOrder(int SchemaVersion, bool AutoSortChats, string[] OrderedChatIds, ChatProperties[] ChatProperties);
}
