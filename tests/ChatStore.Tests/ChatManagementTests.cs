using CompAI;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class ChatManagementTests
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private static string ChatPath(TempFolder folder, string id) => Path.Combine(folder.Path, id + ".json");
    private static string IndexPath(TempFolder folder) => Path.Combine(folder.Path, "sidebar-order.json");

    internal static void TitlesAndPins()
    {
        using var folder = new TempFolder();
        var now = DateTimeOffset.UtcNow;
        var store = new ChatStore(folder.Path, () => now);
        var first = store.ActiveId;
        store.ActiveHistory.Add("Original question", "Saved answer", [], null, false);
        Check(store.SaveActive(), "save original content");
        var bytes = File.ReadAllBytes(ChatPath(folder, first));
        var updated = store.View.Single().UpdatedAtUtc;
        var second = store.NewChat();
        var snapshot = store.View;
        var serializations = store.TranscriptSerializations;
        now += TimeSpan.FromDays(1);
        Check(store.RenameChat(first, "  Știință and crafting  ") && store.SetPinned(first, true), "save title and pin");
        var summary = store.View.First();
        Check(summary.Id == first && summary.Title == "Știință and crafting" && summary.IsPinned &&
            summary.HasCustomTitle && !summary.IsArchived && summary.UpdatedAtUtc == updated, "pin and title keep original activity");
        Check(snapshot.All(chat => !chat.IsPinned && !chat.HasCustomTitle) &&
            File.ReadAllBytes(ChatPath(folder, first)).SequenceEqual(bytes) && store.TranscriptSerializations == serializations,
            "metadata edits preserve immutable snapshots and never serialize or rewrite transcripts");
        var loaded = new ChatStore(folder.Path, () => now);
        Check(loaded.View.First().Id == first && loaded.View.First().Title == summary.Title && loaded.View.First().IsPinned,
            "title and pin survive reload");
        Check(loaded.RenameChat(first, "") && loaded.View.First().Title == "Original question" && !loaded.View.First().HasCustomTitle,
            "blank name restores the automatic title");
        Check(!loaded.RenameChat(first, "line\nbreak") && !loaded.RenameChat(first, new string('x', 57)) &&
            !loaded.RenameChat("../outside", "title"), "unsafe, oversized and non-chat title targets rejected");
        Check(loaded.SetAutoSortChats(false) && loaded.MoveChatBefore(second, first) && loaded.View.First().Id == first &&
            loaded.SetPinned(first, false) && loaded.View.First().Id == second, "pins overlay rather than destroy manual order");
        for (var i = 0; i < 10; ++i) loaded.NewChat();
        Check(loaded.SetPinned(first, true) && loaded.View.Count(chat => chat.IsArchived) == 1,
            "the pinned chat does not consume one of the ten ordinary recent positions");
    }

    internal static void PinRetentionAndMigration()
    {
        using var folder = new TempFolder();
        var now = DateTimeOffset.UtcNow.AddDays(-31);
        var store = new ChatStore(folder.Path, () => now);
        var pinned = store.ActiveId;
        var ordinary = store.NewChat();
        Check(store.SetPinned(pinned, true), "pin old chat");
        now += TimeSpan.FromDays(31);
        var loaded = new ChatStore(folder.Path, () => now);
        Check(loaded.View.Any(chat => chat.Id == pinned) && !File.Exists(ChatPath(folder, ordinary)),
            "pins are read before expiry and ordinary stale chats still expire");
        Check(loaded.SetPinned(pinned, false) && File.Exists(ChatPath(folder, pinned)) && loaded.View.Any(chat => chat.Id == pinned),
            "unpin retains old chat until a later load");
        var expired = new ChatStore(folder.Path, () => now);
        Check(!expired.View.Any(chat => chat.Id == pinned) && !File.Exists(ChatPath(folder, pinned)), "unpin permits next-load expiry");

        using var legacyFolder = new TempFolder();
        var legacy = new ChatStore(legacyFolder.Path, () => now);
        var id = legacy.ActiveId;
        var bytes = File.ReadAllBytes(ChatPath(legacyFolder, id));
        File.WriteAllText(IndexPath(legacyFolder), JsonSerializer.Serialize(new
            { SchemaVersion = 1, AutoSortChats = false, OrderedChatIds = new[] { id } }));
        var migrated = new ChatStore(legacyFolder.Path, () => now);
        using var json = JsonDocument.Parse(File.ReadAllText(IndexPath(legacyFolder)));
        Check(!migrated.AutoSortChats && json.RootElement.GetProperty("SchemaVersion").GetInt32() == 2 &&
            File.ReadAllBytes(ChatPath(legacyFolder, id)).SequenceEqual(bytes), "schema one migrates metadata only");
    }

    internal static void RecoverPinMetadata()
    {
        using var folder = new TempFolder();
        var now = DateTimeOffset.UtcNow.AddDays(-45);
        var store = new ChatStore(folder.Path, () => now);
        var id = store.ActiveId;
        Check(store.SetPinned(id, true) && store.RenameChat(id, "keep me"), "pin and title saved");
        var path = ChatPath(folder, id);
        now = now.AddDays(45);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var partial = new ChatStore(folder.Path, () => now);
            var first = partial.ActiveId;
            var second = partial.NewChat();
            Check(partial.SetAutoSortChats(false) && partial.MoveChatBefore(second, first), "reordered readable chats");
        }
        var recovered = new ChatStore(folder.Path, () => now);
        Check(recovered.View.Any(chat => chat.Id == id && chat.IsPinned && chat.Title == "keep me"),
            "a transient read failure and subsequent sidebar edits preserve the pin and title");

        File.WriteAllText(IndexPath(folder), "{");
        var damaged = new ChatStore(folder.Path, () => now);
        File.Move(IndexPath(folder), IndexPath(folder) + ".old");
        Check(!damaged.RenameChat(id, "renamed") && !File.Exists(IndexPath(folder)),
            "moving the broken index cannot save an empty pin set in the same session");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var missing = new ChatStore(folder.Path, () => now);
            Check(missing.StorageInfo.RecoveryNotice?.Contains("pinned") == true, "recovery pinning is explained");
        }
        var rebuilt = new ChatStore(folder.Path, () => now);
        Check(rebuilt.View.Any(chat => chat.Id == id && chat.IsPinned) && rebuilt.SetPinned(id, false),
            "missing metadata protects even a chat that was temporarily unreadable");
        var expired = new ChatStore(folder.Path, () => now);
        Check(!File.Exists(path) && expired.View.All(chat => chat.Id != id), "explicit unpin restores normal expiry");
    }

    internal static void LongSourceTitles()
    {
        using var folder = new TempFolder();
        var store = new ChatStore(folder.Path);
        var id = store.ActiveId;
        var uri = new Uri($"https://{new string('a', 56)}.{new string('b', 56)}.example/ref");
        var title = ChatHistory.ModelSourceTitle(uri);
        Check(title.Length <= 120 && title.Contains("unverified"), "new long-host titles fit the saved format");
        store.ActiveHistory.Add("Where?", "A reference", [new QuestionSource(title, uri.AbsoluteUri)], null, false);
        Check(store.SaveActive(), "save new source");
        var path = ChatPath(folder, id);
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        json["Turns"]![0]!["Sources"]![0]!["Title"] = "Model-provided, unverified: " + uri.Host;
        File.WriteAllText(path, json.ToJsonString());
        var loaded = new ChatStore(folder.Path);
        var source = loaded.ActiveHistory.View.Single().Sources.Single();
        Check(loaded.ActiveId == id && source.Title == title && source.Url == uri.AbsoluteUri,
            "existing long generated labels recover without changing the link");
        json["Turns"]![0]!["Sources"]![0]!["Title"] = new string('x', 121);
        File.WriteAllText(path, json.ToJsonString());
        Check(new ChatStore(folder.Path).View.All(chat => chat.Id != id), "unrelated invalid titles are still rejected");
    }

    internal static void MetadataFailure()
    {
        using var folder = new TempFolder();
        var now = DateTimeOffset.UtcNow.AddDays(-31);
        var store = new ChatStore(folder.Path, () => now);
        var id = store.ActiveId;
        Check(store.SetPinned(id, true) && store.RenameChat(id, "keep"), "metadata precondition");
        var bytes = File.ReadAllBytes(IndexPath(folder));
        var view = store.View;
        File.SetAttributes(IndexPath(folder), FileAttributes.ReadOnly);
        try
        {
            Check(!store.SetPinned(id, false) && !store.RenameChat(id, "replace") && ReferenceEquals(view, store.View) &&
                File.ReadAllBytes(IndexPath(folder)).SequenceEqual(bytes), "failed metadata saves preserve pin, title and disk bytes");
        }
        finally { File.SetAttributes(IndexPath(folder), FileAttributes.Normal); }
        foreach (var malformed in new[] { "{", "{\"SchemaVersion\":3,\"Future\":true}",
            "{\"SchemaVersion\":2,\"AutoSortChats\":true,\"OrderedChatIds\":[],\"ChatProperties\":[null]}" })
        {
            File.WriteAllText(IndexPath(folder), malformed);
            var loaded = new ChatStore(folder.Path, () => now.AddDays(31));
            Check(loaded.View.Any(chat => chat.Id == id) && File.Exists(ChatPath(folder, id)) && loaded.ChatOrderError is not null &&
                !loaded.SetPinned(id, true) && !loaded.RenameChat(id, "replace") && File.ReadAllText(IndexPath(folder)) == malformed,
                "unreadable metadata preserves unknown pin retention and blocks replacement");
        }
        File.WriteAllBytes(IndexPath(folder), bytes);
        var node = JsonNode.Parse(bytes)!.AsObject();
        var properties = node["ChatProperties"]!.AsArray();
        properties.Add(properties[0]!.DeepClone());
        var duplicate = node.ToJsonString();
        File.WriteAllText(IndexPath(folder), duplicate);
        var duplicateStore = new ChatStore(folder.Path, () => now.AddDays(31));
        Check(duplicateStore.ChatOrderError is not null && !duplicateStore.SetPinned(id, false) &&
            File.ReadAllText(IndexPath(folder)) == duplicate, "duplicate per-chat properties are preserved as invalid metadata");
    }

    internal static void Search()
    {
        using var folder = new TempFolder();
        var store = new ChatStore(folder.Path);
        var first = store.ActiveId;
        store.ActiveHistory.Add("A FISHING question", "A hidden crafting answer", [], null, false);
        store.SetActiveNotes("private-note-only");
        store.SetDraft("draft-only");
        var second = store.NewChat();
        Check(store.RenameChat(second, "Romanian Știință"), "rename fixture");
        Check(store.SearchChats("fishing").Chats.Single().Id == first && store.SearchChats("CRAFTING").Chats.Single().Id == first &&
            store.SearchChats("știință").Chats.Single().Id == second, "title, question and answer use local case-insensitive matching");
        Check(store.SearchChats("private-note-only").Chats.Count == 0 && store.SearchChats("draft-only").Chats.Count == 0,
            "notes and drafts are outside completed message search");
        var result = store.SearchChats("crafting");
        var scans = store.SearchScans;
        for (var i = 0; i < 100; i++) Check(ReferenceEquals(result, store.SearchChats("crafting")), "unchanged search returns immutable cached result");
        Check(store.SearchScans == scans && !result.IsTruncated && store.SearchChats(null).Chats.Count == 2, "repeated frames do not rescan messages");
        Check(store.SearchChats(new string('x', 121)).Error is not null, "oversized query is rejected");
        Check(store.SearchChats("new token").Chats.Count == 0, "initial missing phrase");
        store.ActiveHistory.Add("new token", "fresh answer", [], null, false);
        Check(store.SearchChats("new token").Chats.Single().Id == second, "history revision invalidates results before the next save");
        var oldResult = store.SearchChats("crafting");
        Check(store.RenameChat(second, "crafting title") && store.SearchChats("crafting").Chats.Count == 2 && oldResult.Chats.Count == 1,
            "rename refreshes results without mutating an earlier result snapshot");
    }

    internal static void SearchBudget()
    {
        using var folder = new TempFolder();
        var store = new ChatStore(folder.Path);
        var titleMatch = store.ActiveId;
        Check(store.RenameChat(titleMatch, "needle title"), "title fixture");
        store.NewChat();
        Check(store.SetPinned(store.ActiveId, true), "large transcript is first");
        for (var i = 0; i < 129; i++) store.ActiveHistory.Add("question", new string('x', 65536), [], null, false);
        store.ActiveHistory.Add("needle after budget", "late", [], null, false);
        var result = store.SearchChats("needle");
        Check(result.IsTruncated && result.Chats.Single().Id == titleMatch,
            "budget limit is explicit and all titles stay searchable after message scanning stops");
    }

    internal static void BulkDelete()
    {
        using var folder = new TempFolder();
        var store = new ChatStore(folder.Path);
        var good = store.ActiveId;
        var lockedId = store.NewChat();
        var dirty = store.NewChat();
        store.SetDraft("unsaved draft survives deletion");
        using var dirtyLock = new FileStream(ChatPath(folder, dirty), FileMode.Open, FileAccess.Read, FileShare.Read);
        Check(!store.SaveActive(), "failed-save precondition");
        var busy = store.NewChat();
        var active = store.NewChat();
        var export = store.ExportActiveChat();
        var sideFile = Path.Combine(folder.Path, "provider-settings.json");
        File.WriteAllText(sideFile, "keep settings");
        using (var fileLock = new FileStream(ChatPath(folder, lockedId), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = store.DeleteSelected([good, good, lockedId, dirty, busy, "../outside", "*"],
                new HashSet<string>([busy], StringComparer.Ordinal));
            Check(result.DeletedIds.SequenceEqual([good]) && result.Failures.Count == 5 && store.ActiveId == active,
                "only explicitly selected successful IDs are removed; duplicate IDs are one deletion");
            Check(store.View.Any(chat => chat.Id == lockedId) && store.View.Any(chat => chat.Id == dirty && chat.HasUnsavedChanges) &&
                store.View.Any(chat => chat.Id == busy), "locked, failed-save and in-flight chats remain reachable");
            Check(File.Exists(export) && File.ReadAllText(sideFile) == "keep settings" && File.Exists(ChatPath(folder, dirty)),
                "exports, settings, failed saves and unselected data are untouched");
        }
        Check(store.DeleteSelected([lockedId]).DeletedIds.SequenceEqual([lockedId]), "partial failure can be retried after file unlock");

        using var allFolder = new TempFolder();
        var allStore = new ChatStore(allFolder.Path);
        allStore.NewChat();
        allStore.NewChat();
        var selected = allStore.View.Select(chat => chat.Id).ToArray();
        var cleared = allStore.DeleteSelected(selected);
        Check(cleared.Failures.Count == 0 && cleared.DeletedIds.Count == 3 && allStore.View.Count == 1 &&
            !selected.Contains(allStore.ActiveId) && new ChatStore(allFolder.Path).View.Single().Id == allStore.ActiveId,
            "deleting every selected chat creates and persists exactly one fresh replacement");
        var before = allStore.View;
        Check(allStore.DeleteSelected(Enumerable.Repeat(allStore.ActiveId, ChatStore.MaxSidebarOrderEntries + 1)).DeletedIds.Count == 0 &&
            ReferenceEquals(before, allStore.View), "oversized selection is rejected before any mutation");
    }

    internal static void OwnerPersistence()
    {
        using var folder = new TempFolder();
        var store = new ChatStore(folder.Path);
        var owner = store.ActiveId;
        var selected = store.NewChat();
        store.SetDraft("selected chat draft");
        using (var fileLock = new FileStream(ChatPath(folder, owner), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Check(store.AppendAnswer(owner, "owner question", "owner answer", [], null, false, "Armo") && store.ActiveId == selected &&
                store.ActiveHistory.View.Count == 0 && store.View.Single(chat => chat.Id == owner).HasUnsavedChanges,
                "background completion remains owned and accepted in memory despite save failure");
            Check(store.RestoreDraftIfEmpty(owner, "owner cancelled draft") && store.RestoreDraftIfEmpty(owner, "do not replace") &&
                store.ActiveDraft == "selected chat draft", "cancellation cannot replace another chat draft or an existing owner draft");
        }
        store.FlushAll();
        var loaded = new ChatStore(folder.Path);
        Check(loaded.Select(owner) && loaded.ActiveHistory.View.Single().Answer == "owner answer" && loaded.ActiveDraft == "owner cancelled draft",
            "owner answer and draft persist after storage recovery");
        Check(!store.AppendAnswer(new string('a', 32), "missing", "missing", [], null, false, null) &&
            !store.RestoreDraftIfEmpty("../outside", "missing") && store.ActiveHistory.View.Count == 0,
            "absent owners never fall back to the selected chat");
    }
}
