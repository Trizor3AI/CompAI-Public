using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CompAI;

// One background read at a time bounds the memory used for file and clipboard conversions.
internal sealed class ChatAttachmentLoader : IDisposable
{
    internal const string BusyMessage = "A file is being added. Wait for it to finish.";
    private readonly object gate = new();
    private readonly CancellationTokenSource cancellation = new();
    private string? pendingChat;
    private readonly Dictionary<string, string> errors = new(StringComparer.Ordinal);
    private bool disposed;

    internal bool Pending(string chatId)
    {
        lock (this.gate) return this.pendingChat == chatId;
    }

    internal string? TakeError(string chatId)
    {
        lock (this.gate) return this.errors.Remove(chatId, out var error) ? error : null;
    }

    internal string? Start(string chatId, Func<CancellationToken, ChatAttachment> read,
        Func<string, ChatAttachment, string?> addToDraft)
    {
        lock (this.gate)
        {
            if (this.disposed) return "CompAI is closing.";
            if (this.pendingChat is not null) return BusyMessage;
            this.pendingChat = chatId;
            this.errors.Remove(chatId);
            var token = this.cancellation.Token;
            _ = Task.Run(() => this.Load(chatId, read, addToDraft, token));
            return null;
        }
    }

    private void Load(string chatId, Func<CancellationToken, ChatAttachment> read,
        Func<string, ChatAttachment, string?> addToDraft, CancellationToken token)
    {
        string? error = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var attachment = read(token);
            lock (this.gate)
            {
                if (!this.disposed) error = addToDraft(chatId, attachment);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (InvalidDataException ex) { error = ex.Message; }
        catch (Exception) { error = "CompAI could not read the attachment. Select the file or paste the image again."; }
        finally
        {
            lock (this.gate)
            {
                if (!this.disposed && error is not null) this.errors[chatId] = error;
                this.pendingChat = null;
            }
        }
    }

    public void Dispose()
    {
        lock (this.gate)
        {
            if (this.disposed) return;
            this.disposed = true;
            this.cancellation.Cancel();
            this.cancellation.Dispose();
            this.errors.Clear();
        }
    }
}
