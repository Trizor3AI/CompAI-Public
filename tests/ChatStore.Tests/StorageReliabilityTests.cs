using CompAI;
using System.Text.Json;

internal static class StorageReliabilityTests
{
    internal static void ExportNoticeScope()
    {
        using var folder = new TempFolder();
        var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        var store = new ChatStore(folder.Path, () => now);
        var first = store.ActiveId;
        store.SetDraft("Preserve this unsent draft in the export.");
        var exported = store.ExportActiveChat();
        Check(exported is not null && File.Exists(exported), "manual export is written");
        Check(store.StorageInfo.ExportNotice?.Contains(exported!) == true && store.StorageInfo.ExportPath == exported &&
            store.StorageInfo.RecoveryNotice is null,
            "success is a chat confirmation, not a recovery warning");
        using (var json = JsonDocument.Parse(File.ReadAllBytes(exported!)))
            Check(json.RootElement.GetProperty("Draft").GetString() == store.ActiveDraft, "export includes the current unsent draft");
        store.NewChat();
        Check(store.StorageInfo.ExportNotice is null && store.StorageInfo.RecoveryNotice is null,
            "another chat does not inherit the export confirmation");
        Check(store.Select(first) && store.StorageInfo.ExportNotice is not null, "recent confirmation belongs to the exported chat");
        now += ChatStore.ExportNoticeLifetime;
        Check(store.StorageInfo.ExportNotice is null && store.StorageInfo.ExportPath is null &&
            File.Exists(exported), "confirmation and copy target expire without removing the export");
        Check(new ChatStore(folder.Path, () => now).StorageInfo.ExportNotice is null, "success notices do not persist across reload");

        // A later successful manual export must not erase a real recovery warning.
        var path = Path.Combine(folder.Path, store.ActiveId + ".json");
        store.SetDraft("unsaved after storage failure");
        using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Check(!store.SaveActive(), "simulate chat-save failure");
        var warning = store.StorageInfo.RecoveryNotice;
        Check(warning is not null && store.ExportActiveChat() is not null && store.StorageInfo.RecoveryNotice == warning &&
            store.StorageInfo.UnsavedChats == 1, "manual export preserves the real unsaved/recovery warning");
    }

    internal static void DraftDebounce()
    {
        using var folder = new TempFolder();
        var now = new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        var store = new ChatStore(folder.Path, () => now);
        store.ActiveHistory.Add("fixture", new string('x', 65536), [], null, false);
        Check(store.SaveActive(), "save synthetic transcript");
        var baseline = store.TranscriptSerializations;
        var path = Path.Combine(folder.Path, store.ActiveId + ".json");
        var before = File.ReadAllBytes(path);
        for (var i = 0; i < 200; i++)
        {
            store.SetDraft("Draft Ș " + i);
            _ = store.StorageInfo;
        }
        Check(store.TranscriptSerializations == baseline && store.StorageInfo.UnsavedChats == 0 &&
            File.ReadAllBytes(path).SequenceEqual(before), "typing and repeated status reads neither serialize transcripts nor warn about ordinary pending drafts");
        now += TimeSpan.FromMilliseconds(700);
        Check(store.FlushDraftIfDue() && store.TranscriptSerializations == baseline, "debounce keeps edits in memory before its deadline");
        now += TimeSpan.FromMilliseconds(100);
        Check(store.FlushDraftIfDue() && store.TranscriptSerializations == baseline + 1 &&
            new ChatStore(folder.Path, () => now).ActiveDraft == "Draft Ș 199", "one full save persists all edits after the debounce");
        store.SetDraft("final flush");
        store.FlushAll();
        Check(new ChatStore(folder.Path, () => now).ActiveDraft == "final flush", "final flush persists a draft before its deadline");
    }

    internal static void CapacityAndOverflow()
    {
        using var folder = new TempFolder();
        var store = new ChatStore(folder.Path);
        var answer = new string('x', 65536);
        for (var i = 0; i < 192; i++) store.ActiveHistory.Add("q" + i, answer, [], null, false);
        Check(store.SaveActive(), "12 MiB fixture saves within limit");
        Check(store.StorageInfo.CapacityWarning is not null && store.StorageInfo.SendBlockReason is null,
            "near-limit warning appears before sends are blocked");
        for (var i = 192; i < 224; i++) store.ActiveHistory.Add("q" + i, answer, [], null, false);
        var info = store.StorageInfo;
        Check(info.Bytes >= ChatStore.SendLimitBytes && info.SendBlockReason is not null, "reply headroom blocks sends before 16 MiB");
        var measured = store.TranscriptSerializations;
        for (var i = 0; i < 100; i++) _ = store.StorageInfo;
        Check(store.TranscriptSerializations == measured, "unchanged frame status reuses the byte measurement");
        for (var i = 224; i < 257; i++) store.ActiveHistory.Add("q" + i, answer, [], null, false);
        var id = store.ActiveId;
        var path = Path.Combine(folder.Path, id + ".json");
        var saved = File.ReadAllBytes(path);
        Check(!store.SaveActive() && store.StorageInfo.UnsavedChats == 1 && File.ReadAllBytes(path).SequenceEqual(saved),
            "overflow preserves the original file and records unsaved work");
        var exported = store.ExportActiveChat();
        Check(exported is not null && new FileInfo(exported).Length > ChatStore.MaxFileBytes, "external export preserves content beyond normal chat-file limit");
        using (var json = JsonDocument.Parse(File.ReadAllBytes(exported!)))
            Check(json.RootElement.GetProperty("Turns").GetArrayLength() == 257 &&
                json.RootElement.GetProperty("Turns")[256].GetProperty("Answer").GetString() == answer, "overflow export retains the complete final answer");
        var next = store.NewChat();
        Check(next != id && store.StorageInfo.UnsavedChats == 1 && store.Select(id) && store.ActiveHistory.View.Count == 257,
            "failed oversized save does not trap navigation or lose its in-memory transcript");
        Check(new ChatStore(folder.Path).StorageInfo.RecoveryNotice is not null, "reload makes existing external recovery exports visible");

        var turnsStore = new ChatStore(Path.Combine(folder.Path, "turn-limit"));
        for (var i = 0; i < 900; i++) turnsStore.ActiveHistory.Add("q" + i, "answer", [], null, false);
        Check(turnsStore.StorageInfo.CapacityWarning is not null && turnsStore.StorageInfo.SendBlockReason is null, "turn warning precedes the cap");
        for (var i = 900; i < 1001; i++) turnsStore.ActiveHistory.Add("q" + i, "answer", [], null, false);
        Check(turnsStore.StorageInfo.SendBlockReason is not null && !turnsStore.SaveActive() &&
            turnsStore.ActiveHistory.View.Count == 1001 && turnsStore.ActiveHistory.View[0].Question == "q0", "turn overflow is preserved rather than silently evicted");
    }

    internal static void FailedSaveNavigation()
    {
        using var folder = new TempFolder();
        var exports = Path.Combine(folder.Path, "exports");
        var store = new ChatStore(Path.Combine(folder.Path, "chats"), exportDirectory: exports);
        var first = store.ActiveId;
        var path = Path.Combine(folder.Path, "chats", first + ".json");
        store.SetDraft("keep draft during failed save");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var second = store.NewChat();
            Check(second != first && store.StorageInfo.UnsavedChats == 1 && store.Select(first) &&
                store.ActiveDraft == "keep draft during failed save", "navigation keeps failed-save contents reachable");
            var recovery = Path.Combine(exports, first + "-recovery.json");
            using var json = JsonDocument.Parse(File.ReadAllBytes(recovery));
            Check(json.RootElement.GetProperty("Draft").GetString() == store.ActiveDraft &&
                store.StorageInfo.RecoveryNotice!.Contains("not automatically restored"), "recovery export is complete and clearly external-only");
            Check(store.Select(second), "leave the failed chat selected elsewhere before final flush");
        }
        store.FlushAll();
        Check(store.StorageInfo.UnsavedChats == 0 && new ChatStore(Path.Combine(folder.Path, "chats"), exportDirectory: exports).Select(first),
            "final flush retries inactive chats after storage recovers");
        using var recovered = JsonDocument.Parse(File.ReadAllBytes(path));
        Check(recovered.RootElement.GetProperty("Draft").GetString() == "keep draft during failed save", "retried save contains original pending draft");
    }

    internal static void RecoveryFailure()
    {
        using var folder = new TempFolder();
        var exports = Path.Combine(folder.Path, "blocked-exports");
        File.WriteAllText(exports, "keep");
        var store = new ChatStore(Path.Combine(folder.Path, "chats"), exportDirectory: exports);
        var first = store.ActiveId;
        var path = Path.Combine(folder.Path, "chats", first + ".json");
        store.SetDraft("memory only");
        using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Check(!store.SaveActive() && store.StorageInfo.RecoveryNotice!.Contains("memory only") &&
            store.StorageInfo.RecoveryNotice.Contains("lost when CompAI unloads"), "failed preservation clearly labels the remaining lifetime risk");
        Check(store.NewChat() != first && store.Select(first) && store.ActiveDraft == "memory only" &&
            File.ReadAllText(exports) == "keep", "even a failed recovery export allows navigation without destroying memory or blocked path");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
