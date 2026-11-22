using System;
using System.Diagnostics;

namespace CompAI;

public sealed partial class Plugin
{
    private StorageOverviewService? storageOverview;

    internal StorageOverviewView StorageOverview
    {
        get
        {
            lock (this.stateGate)
                return (this.storageOverview ??= new StorageOverviewService(this.storage)).View;
        }
    }

    internal bool RefreshStorageOverview()
    {
        lock (this.stateGate)
            return !this.contextDisposed && (this.storageOverview ??= new StorageOverviewService(this.storage)).Refresh();
    }

    internal string? OpenStorageFolder(StorageCategory category)
    {
        string? path = null;
        lock (this.stateGate)
        {
            if (this.contextDisposed) return "CompAI is shutting down.";
            foreach (var entry in this.StorageOverview.Categories)
                if (entry.Category == category) { path = entry.Path; break; }
        }
        if (path is null) return "Unknown storage category.";
        try
        {
            if (!StorageOverviewScanner.CheckDirectoryPath(path)) return "This storage folder has not been created yet.";
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) when (StorageOverviewScanner.IsReadError(ex) || ex is System.ComponentModel.Win32Exception)
        {
            return $"Could not open this storage folder; it may be linked, missing or inaccessible ({ex.GetType().Name}).";
        }
    }

    private void DisposeStorageOverview()
    {
        lock (this.stateGate) this.storageOverview?.Dispose();
    }
}
