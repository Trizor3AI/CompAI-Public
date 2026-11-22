using System;
using System.Collections.Generic;
using System.IO;

namespace CompAI;

public sealed partial class Plugin
{
    private readonly ChatAttachmentLoader attachmentLoader = new();

    internal IReadOnlyList<ChatAttachment> ChatAttachments(string chatId) => this.companion.AttachmentDrafts(chatId);
    internal IReadOnlyList<ChatAttachment> PendingAttachments(string chatId) => this.companion.PendingAttachments(chatId);
    internal IReadOnlyList<ChatAttachment> RetryAttachments(string chatId) => this.companion.RetryAttachments(chatId);
    internal string? BeginAddChatAttachment(string chatId, string path)
    {
        lock (this.stateGate)
            return this.attachmentLoader.Start(chatId, _ => ChatAttachment.ReadSelected(path), this.companion.AddAttachment);
    }
    internal void RemoveChatAttachment(string chatId, int index) => this.companion.RemoveAttachment(chatId, index);
    internal bool AttachmentPending(string chatId) => this.attachmentLoader.Pending(chatId);
    internal string? TakeAttachmentError(string chatId) => this.attachmentLoader.TakeError(chatId);

    internal string? BeginPasteChatImage(string chatId)
    {
        try
        {
            if (ClipboardImage.CaptureRequest() is not { } request) return null;
            lock (this.stateGate)
                return this.attachmentLoader.Start(chatId, token => ClipboardImage.Read(request, token), this.companion.AddAttachment);
        }
        catch (InvalidDataException ex) { return ex.Message; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        { return "CompAI could not read the clipboard. Paste the image again."; }
    }

    internal bool SendAttachedChatQuestion(string question)
    {
        lock (this.stateGate)
        {
            var id = this.companion.ActiveChatId;
            // The worker installs the file before clearing Pending. Check before freezing this draft.
            if (this.AttachmentPending(id))
            {
                this.questionPreviewState = ChatAttachmentLoader.BusyMessage;
                return false;
            }
            return this.SendChatQuestion(question, attachments: this.companion.AttachmentDrafts(id));
        }
    }
    internal string? AttachmentBlockReason(IReadOnlyList<ChatAttachment> attachments)
    {
        foreach (var attachment in attachments)
            if (attachment.IsImage)
            {
                foreach (var model in this.companion.ModelOptions)
                    if (model.Provider == this.ModelSelection.Provider && model.Id == this.ModelSelection.Model && model.SupportsImages) return null;
                return "This model has not advertised image support. Refresh account status, choose an image-capable model, or remove the image.";
            }
        return null;
    }
}
