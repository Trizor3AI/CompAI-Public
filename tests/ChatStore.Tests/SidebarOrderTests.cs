using CompAI;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class SidebarOrderTests
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private static string IndexPath(TempFolder folder) => Path.Combine(folder.Path, "sidebar-order.json");
    private static string[] Order(ChatStore store) => store.View.Select(chat => chat.Id).ToArray();

    internal static void CreationAndManualOrder()
    {
        using var folder = new TempFolder();
        var now = DateTimeOffset.UtcNow;
        var store = new ChatStore(folder.Path, () => now);
        var first = store.ActiveId;
        var second = store.NewChat();
        var third = store.NewChat();
        Check(store.AutoSortChats && !store.MoveChatBefore(third, first), "existing default is automatic and rejects manual moves");
        Check(store.SetAutoSortChats(false) && Order(store).SequenceEqual([first, second, third]),
            "first disable follows actual creation even when timestamps tie");
        Check(store.MoveChatBefore(third, first) && Order(store).SequenceEqual([third, first, second]), "drag moves before target");
        Check(store.MoveChatBefore(third, null) && Order(store).SequenceEqual([first, second, third]), "null target moves to end");
        Check(store.MoveChatBefore(second, first), "save a distinct manual order");
        now += TimeSpan.FromMinutes(1);
        Check(store.Select(first), "select oldest chat");
        store.SetDraft("new activity in first chat");
        Check(store.SaveActive(), "save activity");
        Check(Order(store).SequenceEqual([second, first, third]), "activity never changes manual order");
        Check(store.SetAutoSortChats(true) && Order(store)[0] == first, "automatic mode resumes activity sorting");
        now += TimeSpan.FromMinutes(1);
        var fourth = store.NewChat();
        var reloaded = new ChatStore(folder.Path, () => now);
        Check(reloaded.AutoSortChats && Order(reloaded)[0] == fourth, "automatic preference survives reload");
        Check(reloaded.SetAutoSortChats(false) && Order(reloaded).SequenceEqual([second, first, third, fourth]),
            "manual order survives toggles and new chats append even while automatic mode was on");
        var manualReload = new ChatStore(folder.Path, () => now);
        Check(!manualReload.AutoSortChats && Order(manualReload).SequenceEqual(Order(reloaded)), "manual preference and order reload");
        Check(!manualReload.MoveChatBefore("../outside", first) && !manualReload.MoveChatBefore(first, new string('a', 32)),
            "invalid and absent targets are rejected");
    }

    internal static void LegacyMigration()
    {
        using var folder = new TempFolder();
        var now = DateTimeOffset.UtcNow;
        var store = new ChatStore(folder.Path, () => now);
        var first = store.ActiveId;
        now += TimeSpan.FromMinutes(1);
        var second = store.NewChat();
        now += TimeSpan.FromMinutes(1);
        Check(store.Select(first), "select legacy chat");
        store.SetDraft("updated after second chat");
        Check(store.SaveActive(), "save later legacy activity");
        File.Delete(IndexPath(folder));
        var firstPath = Path.Combine(folder.Path, first + ".json");
        var secondPath = Path.Combine(folder.Path, second + ".json");
        var beforeFirst = File.ReadAllBytes(firstPath);
        var beforeSecond = File.ReadAllBytes(secondPath);
        var migrated = new ChatStore(folder.Path, () => now);
        Check(migrated.AutoSortChats && migrated.SetAutoSortChats(false) && Order(migrated).SequenceEqual([second, first]),
            "legacy creation is unknown: initial fallback is oldest saved activity, not guessed GUID creation");
        Check(File.ReadAllBytes(firstPath).SequenceEqual(beforeFirst) && File.ReadAllBytes(secondPath).SequenceEqual(beforeSecond),
            "legacy migration and first mode change do not rewrite either transcript");
        now += TimeSpan.FromMinutes(1);
        Check(migrated.Select(second), "select older fallback entry");
        migrated.SetDraft("activity after migration");
        Check(migrated.SaveActive(), "save post migration activity");
        Check(Order(new ChatStore(folder.Path, () => now)).SequenceEqual([second, first]), "fallback order freezes after first migration");
    }

    internal static void SnapshotsAndContent()
    {
        using var folder = new TempFolder();
        var store = new ChatStore(folder.Path);
        var first = store.ActiveId;
        store.ActiveHistory.Add("question", "answer", [], null, false);
        Check(store.SetActiveNotes("notes") && store.SetActivePersonality(PersonalityPresets.Armo), "save chat content and personality");
        store.SetDraft("draft");
        var second = store.NewChat();
        Check(store.SetAutoSortChats(false), "disable automatic sorting");
        var snapshot = store.View;
        var firstPath = Path.Combine(folder.Path, first + ".json");
        var firstBytes = File.ReadAllBytes(firstPath);
        var secondBytes = File.ReadAllBytes(Path.Combine(folder.Path, second + ".json"));
        var serializations = store.TranscriptSerializations;
        Check(store.MoveChatBefore(second, first), "reorder chats");
        Check(!ReferenceEquals(snapshot, store.View) && snapshot.Select(chat => chat.Id).SequenceEqual([first, second]) &&
              Order(store).SequenceEqual([second, first]), "old immutable view remains valid after reorder");
        var reordered = store.View;
        Check(store.MoveChatBefore(second, first) && ReferenceEquals(reordered, store.View), "no-op reorder keeps cached view");
        Check(store.SetAutoSortChats(false) && ReferenceEquals(reordered, store.View), "no-op mode change keeps cached view");
        Check(store.TranscriptSerializations == serializations && File.ReadAllBytes(firstPath).SequenceEqual(firstBytes) &&
              File.ReadAllBytes(Path.Combine(folder.Path, second + ".json")).SequenceEqual(secondBytes),
            "reorder serializes and writes no transcripts");
        Check(store.Select(first) && store.ActiveHistory.View.Single().Question == "question" &&
              store.ActiveNotes == "notes" && store.ActiveDraft == "draft" && store.ActivePersonality == PersonalityPresets.Armo,
            "per chat content and snapshots remain intact");
        Check(File.ReadAllBytes(IndexPath(folder)).Length < 1024 && Directory.GetFiles(folder.Path, "*.tmp").Length == 0,
            "small order file is committed without leftover temporary files");
    }

    internal static void ExpiryAndDeletion()
    {
        using var folder = new TempFolder();
        var now = DateTimeOffset.UtcNow.AddDays(-29);
        var store = new ChatStore(folder.Path, () => now);
        var expired = store.ActiveId;
        now += TimeSpan.FromDays(28);
        var second = store.NewChat();
        var third = store.NewChat();
        Check(store.SetAutoSortChats(false) && store.MoveChatBefore(third, expired), "set manual order around old chat");
        now += TimeSpan.FromDays(3);
        var reloaded = new ChatStore(folder.Path, () => now);
        Check(Order(reloaded).SequenceEqual([third, second]) && !File.Exists(Path.Combine(folder.Path, expired + ".json")),
            "sidebar movements do not refresh activity or prevent existing expiry");
        Check(reloaded.Delete(second) && Order(reloaded).SequenceEqual([third]), "inactive delete removes one order entry");
        Check(reloaded.Delete(third) && reloaded.View.Count == 1 && reloaded.ActiveId != third, "last delete creates one ordered replacement");
        Check(Order(new ChatStore(folder.Path, () => now)).SequenceEqual(Order(reloaded)), "deletion and replacement order reload");
    }

    internal static void InvalidMetadata()
    {
        foreach (var invalid in new[]
        {
            "{",
            "{\"SchemaVersion\":2,\"AutoSortChats\":false,\"OrderedChatIds\":[]}",
            "{\"SchemaVersion\":1,\"AutoSortChats\":false,\"OrderedChatIds\":[],\"Future\":true}",
            "{\"SchemaVersion\":1,\"SchemaVersion\":1,\"AutoSortChats\":false,\"OrderedChatIds\":[]}",
            "{\"SchemaVersion\":1,\"AutoSortChats\":false,\"OrderedChatIds\":[\"../outside\"]}",
            "{\"SchemaVersion\":1,\"AutoSortChats\":false,\"OrderedChatIds\":[\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"]}",
            "{\"SchemaVersion\":\"1\",\"AutoSortChats\":false,\"OrderedChatIds\":[]}",
            new string('x', ChatStore.MaxSidebarOrderBytes + 1),
        })
        {
            using var folder = new TempFolder();
            var store = new ChatStore(folder.Path);
            var bytes = Encoding.UTF8.GetBytes(invalid);
            File.WriteAllBytes(IndexPath(folder), bytes);
            var reloaded = new ChatStore(folder.Path);
            Check(reloaded.ChatOrderError is not null && reloaded.AutoSortChats && !reloaded.SetAutoSortChats(false),
                "invalid metadata keeps safe default and reports why preferences cannot save");
            reloaded.NewChat();
            Check(File.ReadAllBytes(IndexPath(folder)).SequenceEqual(bytes), "loading, toggling and new chats preserve invalid metadata bytes");
            Check(!store.SetAutoSortChats(false) && File.ReadAllBytes(IndexPath(folder)).SequenceEqual(bytes),
                "metadata corrupted after construction also remains protected");
        }
        using var badUtf8 = new TempFolder();
        var utf8Store = new ChatStore(badUtf8.Path);
        File.WriteAllBytes(IndexPath(badUtf8), [0xff, 0xfe]);
        Check(!utf8Store.SetAutoSortChats(false) && File.ReadAllBytes(IndexPath(badUtf8)).SequenceEqual(new byte[] { 0xff, 0xfe }),
            "invalid UTF8 remains intact");
        using var tooMany = new TempFolder();
        var manyStore = new ChatStore(tooMany.Path);
        var manyBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = 1, AutoSortChats = false,
            OrderedChatIds = Enumerable.Range(0, ChatStore.MaxSidebarOrderEntries + 1).Select(value => value.ToString("x32")).ToArray(),
        });
        File.WriteAllBytes(IndexPath(tooMany), manyBytes);
        Check(!manyStore.SetAutoSortChats(false) && File.ReadAllBytes(IndexPath(tooMany)).SequenceEqual(manyBytes), "entry count bound is enforced");
    }

    internal static void WriteFailure()
    {
        using var folder = new TempFolder();
        var store = new ChatStore(folder.Path);
        var first = store.ActiveId;
        var second = store.NewChat();
        Check(store.SetAutoSortChats(false), "precondition: manual order is persisted");
        var snapshot = store.View;
        var bytes = File.ReadAllBytes(IndexPath(folder));
        File.SetAttributes(IndexPath(folder), FileAttributes.ReadOnly);
        try
        {
            Check(!store.MoveChatBefore(second, first) && !store.SetAutoSortChats(true) && !store.AutoSortChats &&
                  ReferenceEquals(snapshot, store.View) && File.ReadAllBytes(IndexPath(folder)).SequenceEqual(bytes),
                "failed atomic replacement keeps mode, cached order and previous disk bytes");
            Check(store.ChatOrderError is not null && Directory.GetFiles(folder.Path, "*.tmp").Length == 0, "failure is visible and temporary files are cleaned");
        }
        finally { File.SetAttributes(IndexPath(folder), FileAttributes.Normal); }
        Check(store.MoveChatBefore(second, first) && store.ChatOrderError is null, "retry succeeds after storage is repaired");

        using var blockedFolder = new TempFolder();
        Directory.CreateDirectory(IndexPath(blockedFolder));
        var blocked = new ChatStore(blockedFolder.Path);
        Check(blocked.ChatOrderError is not null && !blocked.SetAutoSortChats(false) && Directory.Exists(IndexPath(blockedFolder)),
            "a directory at the index path is preserved");
    }
}
