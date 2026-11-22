using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CompAI;

internal static class AttachmentConnectionChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Until(Func<bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        { if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Attachment fixture timed out"); await Task.Delay(20); }
    }
    internal static async Task Ownership()
    {
        using var fixture = new Fixture();
        using var connection = new CompanionConnection(preparePython: Fixture.PreparePython);
        fixture.Scenario("login_status"); connection.Start("status");
        await Until(() => !connection.View.Busy);
        Check(connection.View.CanTest, "synthetic authorization");
        fixture.Scenario("ask_parallel");
        var directory = Plugin.PluginInterface.GetPluginConfigDirectory();
        var path = Path.Combine(directory, "first.txt");
        File.WriteAllText(path, "PRIVATE_SYNTHETIC_FIRST_BYTES", new UTF8Encoding(false));
        var firstFile = ChatAttachment.ReadSelected(path); File.Delete(path);
        var first = connection.View.ActiveChatId;
        Check(connection.AddAttachment(first, firstFile) is null, "explicit first draft accepted");
        var frozen = connection.AttachmentDrafts(first);
        connection.Draft = "Question firstAttachment";
        Check(connection.StartAsk(JsonSerializer.Serialize(new { id = "firstAttachment", attachments = frozen.Select(a => a.Wire) }),
            false, connection.Draft, expectedChatId: first, attachments: frozen), "first attachment send");
        Check(connection.AttachmentDrafts(first).Count == 0 && connection.PendingAttachments(first).Single() == firstFile,
            "send clears selected draft and owns frozen content");
        await Until(() => connection.View.PartialAnswer == "Partial firstAttachment");
        Check(connection.NewChat(), "second owner selectable");
        var second = connection.View.ActiveChatId;
        path = Path.Combine(directory, "second.txt"); File.WriteAllText(path, "PRIVATE_SYNTHETIC_SECOND_BYTES", new UTF8Encoding(false));
        var secondFile = ChatAttachment.ReadSelected(path); File.Delete(path);
        Check(connection.AddAttachment(second, secondFile) is null && connection.AddAttachment(first, secondFile) is not null,
            "picker ownership cannot transfer to or change a busy chat");
        Check(connection.StartAsk(JsonSerializer.Serialize(new { id = "secondAttachment", attachments = new[] { secondFile.Wire } }),
            false, "Question secondAttachment", expectedChatId: second, attachments: connection.AttachmentDrafts(second)), "second overlap send");
        await Until(() => connection.View.PartialAnswer == "Partial secondAttachment");
        Check(connection.SelectChat(first), "first selectable"); connection.Cancel();
        await Until(() => connection.View.ActiveRequestCount == 1);
        Check(connection.AttachmentDrafts(first).Single() == firstFile && connection.RetryAttachments(first).Single() == firstFile &&
            connection.PendingAttachments(second).Single() == secondFile, "cancel restores owner frozen bytes without affecting sibling");
        connection.RemoveAttachment(first, 0);
        Check(connection.AttachmentDrafts(first).Count == 0 && connection.RetryAttachments(first).Count == 0,
            "explicit removal excludes previous bytes from retry");
        Check(connection.StartAsk("{\"id\":\"retryAttachment\"}", false, "Question firstAttachment", expectedChatId: first,
            attachments: connection.RetryAttachments(first)), "explicit retry after removal");
        await Until(() => connection.View.PartialAnswer == "Partial retryAttachment");
        fixture.ReleaseParallel("retryAttachment"); fixture.ReleaseParallel("secondAttachment");
        await Until(() => !connection.View.Busy);
        Check(connection.RetryAttachments(first).Count == 0 && connection.AttachmentDrafts(second).Count == 0,
            "completed sends do not reselect or retain attachments");
        Check(connection.SelectChat(second) && connection.View.History.Single().ContextLabel!.Contains("second.txt") &&
            !connection.View.History.Single().ContextLabel!.Contains("first.txt"), "saved display metadata belongs to correct owner");
        Check(connection.View.StorageError is null, "attachment metadata storage: " + connection.View.StorageError);
        var saved = File.ReadAllText(Path.Combine(directory, "chats", second + ".json"));
        Check(!saved.Contains("PRIVATE_SYNTHETIC") && !saved.Contains(secondFile.Base64) && !saved.Contains(directory),
            "saved chats never retain file data, base64 or source paths");
        var pastedImage = ChatAttachment.FromClipboardPng(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC"));
        Check(connection.AddAttachment(first, pastedImage) is null && connection.AttachmentDrafts(second).Count == 0 &&
            connection.AttachmentDrafts(first).Single() == pastedImage,
            "pasted image keeps its original chat even when another chat is selected");
        Check(connection.AddAttachment(first, pastedImage) is null && connection.AddAttachment(first, firstFile) is null &&
            connection.AddAttachment(first, pastedImage) is not null,
            "pasted images and selected files share the same attachment-count limit");
        for (var i = 2; i >= 0; i--) connection.RemoveAttachment(first, i);
        Check(connection.AddAttachment(second, secondFile) is null && connection.DeleteChat(second) &&
            connection.AttachmentDrafts(second).Count == 0, "delete removes selected in-memory content");
        Check(connection.AddAttachment(first, firstFile) is null, "new attachment ready before unload");
        connection.Dispose();
        Check(connection.AttachmentDrafts(first).Count == 0 && connection.RetryAttachments(first).Count == 0,
            "unload drops in-memory drafts and retry bytes");
    }
}
