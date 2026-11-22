using System;
using System.Collections.Generic;
using System.Linq;

namespace CompAI;

internal sealed partial class CompanionConnection
{
    private readonly Dictionary<string, ChatAttachment[]> attachmentDrafts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatAttachment[]> retryAttachments = new(StringComparer.Ordinal);

    internal IReadOnlyList<ChatAttachment> AttachmentDrafts(string chatId)
    { lock (this.gate) return this.attachmentDrafts.GetValueOrDefault(chatId)?.ToArray() ?? Array.Empty<ChatAttachment>(); }

    internal IReadOnlyList<ChatAttachment> RetryAttachments(string chatId)
    { lock (this.gate) return this.retryAttachments.GetValueOrDefault(chatId)?.ToArray() ?? Array.Empty<ChatAttachment>(); }

    internal IReadOnlyList<ChatAttachment> PendingAttachments(string chatId)
    { lock (this.gate) return this.questions.GetValueOrDefault(chatId)?.attachments.ToArray() ?? Array.Empty<ChatAttachment>(); }

    internal string? AddAttachment(string chatId, ChatAttachment attachment)
    {
        lock (this.gate)
        {
            // A picker stays owned by its original chat even if selection changes.
            if (this.disposed || this.questions.GetValueOrDefault(chatId)?.busy == true ||
                !this.chats.View.Any(chat => chat.Id == chatId))
                return "That chat is unavailable or answering. Select the file again when it is ready.";
            var current = this.attachmentDrafts.GetValueOrDefault(chatId) ?? Array.Empty<ChatAttachment>();
            if (current.Length >= ChatAttachment.MaxCount || current.Sum(a => (long)a.ByteCount) + attachment.ByteCount > ChatAttachment.MaxTotalBytes)
                return "Attachment limit reached: 3 files and 4 MiB total. Remove a file first.";
            var buffered = this.attachmentDrafts.Values.SelectMany(a => a).Concat(this.retryAttachments.Values.SelectMany(a => a))
                .Concat(this.questions.Values.SelectMany(operation => operation.attachments)).Distinct().Sum(a => (long)a.ByteCount);
            if (buffered + attachment.ByteCount > ChatAttachment.MaxSessionBytes)
                return "Selected-file limit reached (16 MiB across chats). Remove attachments from another chat before adding this file.";
            this.attachmentDrafts[chatId] = current.Append(attachment).ToArray();
            return null;
        }
    }

    internal void RemoveAttachment(string chatId, int index)
    {
        lock (this.gate)
        {
            if (this.disposed || this.questions.GetValueOrDefault(chatId)?.busy == true ||
                !this.attachmentDrafts.TryGetValue(chatId, out var current) || index < 0 || index >= current.Length) return;
            var removed = current[index];
            this.attachmentDrafts[chatId] = current.Where((_, i) => i != index).ToArray();
            // Removal also revokes this exact frozen selection from an available retry.
            if (this.retryAttachments.TryGetValue(chatId, out var retry))
                this.retryAttachments[chatId] = retry.Where(a => !ReferenceEquals(a, removed)).ToArray();
        }
    }

    private void ClearAttachmentState(string chatId)
    { this.attachmentDrafts.Remove(chatId); this.retryAttachments.Remove(chatId); }

    private void AttachmentsStarted(Operation operation, IReadOnlyList<ChatAttachment>? attachments)
    {
        operation.attachments = ChatAttachment.Freeze(attachments);
        if (operation.chatId is not { } id) return;
        this.retryAttachments.Remove(id);
        if (this.attachmentDrafts.TryGetValue(id, out var draft))
            this.attachmentDrafts[id] = draft.Where(a => !operation.attachments.Contains(a)).ToArray();
    }

    private void AttachmentsFinished(Operation operation, bool saved)
    {
        if (!this.disposed && !saved && operation.chatId is { } id)
        {
            this.retryAttachments[id] = operation.attachments;
            // Restore only into an untouched empty draft, never another selected set.
            if (!this.attachmentDrafts.TryGetValue(id, out var draft) || draft.Length == 0)
                this.attachmentDrafts[id] = operation.attachments;
        }
        operation.attachments = Array.Empty<ChatAttachment>();
    }
}
