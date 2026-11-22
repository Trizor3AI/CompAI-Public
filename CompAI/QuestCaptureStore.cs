using System;

namespace CompAI;

internal sealed record QuestCaptureView(QuestSnapshot? Snapshot, bool Pending, string? UnavailableReason, bool Stale = false);

// Only managed snapshots cross between the framework callback and the UI.
internal sealed class QuestCaptureStore : IDisposable
{
    private readonly object gate = new();
    private int epoch;
    private bool disposed;
    private QuestCaptureView view = new(null, false, "Not captured yet.");

    internal QuestCaptureView View { get { lock (this.gate) return this.view; } }

    internal int? Begin()
    {
        lock (this.gate)
        {
            if (this.disposed || this.view.Pending) return null;
            this.view = new(this.view.Snapshot, true, null, this.view.Stale);
            return ++this.epoch;
        }
    }

    internal bool IsCurrent(int candidate)
    {
        lock (this.gate)
            return !this.disposed && candidate == this.epoch && this.view.Pending;
    }

    internal void Complete(int candidate, QuestSnapshot snapshot)
    {
        lock (this.gate)
            if (this.IsCurrent(candidate)) this.view = new(snapshot, false, null);
    }

    internal void Fail(int candidate, string reason)
    {
        lock (this.gate)
            if (this.IsCurrent(candidate)) this.view = new(this.view.Snapshot, false, reason, true);
    }

    internal void Suspend(string reason)
    {
        lock (this.gate)
        {
            if (this.disposed) return;
            ++this.epoch;
            this.view = new(this.view.Snapshot, false, reason, true);
        }
    }

    internal void MarkCurrent()
    {
        lock (this.gate)
        {
            if (this.disposed || this.view.Pending || this.view.Snapshot is null ||
                !this.view.Stale && this.view.UnavailableReason is null) return;
            this.view = this.view with { Stale = false, UnavailableReason = null };
        }
    }

    internal void Clear(string reason = "Quest capture cleared.")
    {
        lock (this.gate)
        {
            ++this.epoch;
            this.view = new(null, false, reason);
        }
    }

    public void Dispose()
    {
        lock (this.gate)
        {
            this.disposed = true;
            this.Clear("Reader unloaded.");
        }
    }
}
