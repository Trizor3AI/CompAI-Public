using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CompAI;

internal enum StorageCategory { Settings, Chats, CharacterCache, Exports, CompanionProfile }

// Paths belong only in the local settings UI. Never attach this view to a question,
// export, log or the copyable numeric diagnostics report.
internal sealed record StorageCategoryView(StorageCategory Category, string Name, string Path,
    long? FileCount, long? FolderCount, long? Bytes, bool Complete, string Status, string Retention);

internal sealed record StorageOverviewView(bool Busy, DateTimeOffset? RefreshedAtUtc,
    double? ElapsedMilliseconds, IReadOnlyList<StorageCategoryView> Categories, string? Error = null);

internal sealed record StorageScanLimits(int MaximumEntries = 10_000, int MaximumDepth = 3,
    int MaximumMilliseconds = 2_000);

internal static class StorageOverviewScanner
{
    private static readonly string[] SettingsFiles =
    [
        "companion-access.json", "companion-model.json", "companion-personalities.json", "game-chat-style.json", "companion-beta.json",
        ".legacy-inspector-profile",
    ];

    internal static StorageOverviewView Empty(PluginStoragePaths paths) => new(false, null, null,
        Array.AsReadOnly(new[]
        {
            Category(StorageCategory.Settings, "Settings", paths.Configuration,
                "Known preferences, presets and migration marker; kept until changed or removed."),
            Category(StorageCategory.Chats, "Saved chats", Path.Combine(paths.Configuration, "chats"),
                "Unpinned chats expire after 30 inactive days; unreadable sidebar metadata suspends expiry. Exports are separate."),
            Category(StorageCategory.CharacterCache, "Character caches", Path.Combine(paths.Configuration, "character-cache"),
                "Includes active caches, last-character selection, .bak and .quarantine recovery files. Clearing active data leaves recovery copies."),
            Category(StorageCategory.Exports, "Exports and chat recovery copies", Path.Combine(paths.Configuration, "exports"),
                "Kept until manually removed. Chat deletion, inactivity expiry and cache clearing do not remove exports."),
            Category(StorageCategory.CompanionProfile, "Private provider profile", paths.CompanionProfile,
                "Provider-managed credentials, databases and logs. Kept separately from chats and caches; never measured here.")
                with { Status = "Not scanned; size and counts are unknown." },
        }));

    private static StorageCategoryView Category(StorageCategory category, string name, string path, string retention) =>
        new(category, name, Path.GetFullPath(path), null, null, null, false, "Not refreshed yet.", retention);

    internal static StorageOverviewView Scan(PluginStoragePaths paths, CancellationToken cancellation,
        StorageScanLimits? limits = null)
    {
        limits ??= new();
        if (limits.MaximumEntries < 1 || limits.MaximumDepth < 0 || limits.MaximumMilliseconds < 1)
            throw new ArgumentOutOfRangeException(nameof(limits));
        var started = Stopwatch.GetTimestamp();
        var budget = new ScanBudget(limits, started, cancellation);
        var categories = new List<StorageCategoryView>();
        foreach (var category in Empty(paths).Categories)
        {
            cancellation.ThrowIfCancellationRequested();
            if (category.Category == StorageCategory.CompanionProfile) { categories.Add(category); continue; }
            categories.Add(MeasureCategory(category, budget));
        }
        return new(false, DateTimeOffset.UtcNow, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            categories.AsReadOnly());
    }

    private static StorageCategoryView MeasureCategory(StorageCategoryView category, ScanBudget budget)
    {
        var count = new Counts();
        try
        {
            budget.Check();
            if (!CheckDirectoryPath(category.Path))
                return category with { FileCount = 0, FolderCount = 0, Bytes = 0, Complete = true, Status = "Folder not created yet." };
            count.Known = true;
            if (category.Category == StorageCategory.Settings)
            {
                foreach (var name in SettingsFiles)
                {
                    budget.Visit();
                    var path = Path.Combine(category.Path, name);
                    try
                    {
                        var info = new FileInfo(path);
                        var attributes = info.Attributes;
                        if ((int)attributes == -1) continue;
                        CountFile(info, attributes, count);
                    }
                    catch (FileNotFoundException) { }
                    catch (DirectoryNotFoundException) { }
                    catch (Exception ex) when (IsReadError(ex)) { count.Warn(ReadError(ex)); }
                }
            }
            else
                Walk(category.Path, 0, category.Category == StorageCategory.Exports, budget, count);
        }
        catch (ScanLimitException ex) { count.Warn(ex.Message); }
        catch (Exception ex) when (IsReadError(ex)) { count.Warn(ReadError(ex)); }
        var status = count.Status;
        if (status.Length == 0)
            status = category.Category switch
            {
                StorageCategory.Settings => "Known settings files only; other configuration files are excluded.",
                StorageCategory.Exports => "Measured files and subfolders. Sizes may change while the plugin writes.",
                _ => "Top-level files only, including recovery and temporary files. Sizes may change while the plugin writes.",
            };
        return category with
        {
            FileCount = count.Known ? count.Files : null,
            FolderCount = count.Known ? count.Folders : null,
            Bytes = count.Known ? count.Bytes : null,
            Complete = count.Known && count.Status.Length == 0,
            Status = status,
        };
    }

    private static void Walk(string directory, int depth, bool recurse, ScanBudget budget, Counts count)
    {
        // Recheck each directory immediately before enumeration; never follow a
        // detected reparse point, including links on the way to the category root.
        budget.Check();
        if (!CheckDirectoryPath(directory)) { count.Warn("A folder disappeared during refresh."); return; }
        foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", new EnumerationOptions
        {
            RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        }))
        {
            budget.Visit();
            try
            {
                var attributes = info.Attributes;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    count.Warn("Symbolic links or junctions were skipped.");
                    continue;
                }
                if ((attributes & FileAttributes.Directory) == 0)
                    CountFile((FileInfo)info, attributes, count);
                else
                {
                    count.Folders++;
                    if (!recurse) count.Warn("Subfolder contents are excluded from this category.");
                    else if (depth >= budget.Limits.MaximumDepth) count.Warn("The subfolder depth limit was reached.");
                    else Walk(info.FullName, depth + 1, true, budget, count);
                }
            }
            catch (Exception ex) when (IsReadError(ex)) { count.Warn(ReadError(ex)); }
        }
    }

    private static void CountFile(FileInfo info, FileAttributes attributes, Counts count)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            count.Warn("Symbolic links or junctions were skipped.");
            return;
        }
        if ((attributes & FileAttributes.Directory) != 0)
        {
            count.Warn("A settings file was replaced by a folder.");
            return;
        }
        var length = info.Length;
        count.Bytes = checked(count.Bytes + length);
        count.Files++;
    }

    // Used again at the point of an explicit folder-open action. Inspect metadata
    // only, including every existing ancestor; no file contents are opened.
    internal static bool CheckDirectoryPath(string path)
    {
        var chain = new Stack<string>();
        for (var part = new DirectoryInfo(Path.GetFullPath(path)); part is not null; part = part.Parent)
            chain.Push(part.FullName);
        while (chain.Count > 0)
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(chain.Pop()); }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked storage paths are excluded.");
            if ((attributes & FileAttributes.Directory) == 0)
                throw new IOException("A storage folder was replaced by a file.");
        }
        return true;
    }

    internal static bool IsReadError(Exception ex) => ex is IOException or UnauthorizedAccessException
        or System.Security.SecurityException or OverflowException;

    private static string ReadError(Exception ex) => ex switch
    {
        UnauthorizedAccessException or System.Security.SecurityException => "Some storage metadata could not be read (access denied).",
        OverflowException => "The measured byte total exceeded the supported range.",
        _ => "Some storage metadata could not be read; a path may be linked, missing or in use.",
    };

    private sealed class Counts
    {
        internal bool Known;
        internal long Files, Folders, Bytes;
        private readonly List<string> warnings = new();
        internal string Status => string.Join(" ", this.warnings);
        internal void Warn(string warning) { if (!this.warnings.Contains(warning)) this.warnings.Add(warning); }
    }

    private sealed class ScanBudget(StorageScanLimits limits, long started, CancellationToken cancellation)
    {
        private int entries;
        internal StorageScanLimits Limits => limits;
        internal void Check()
        {
            cancellation.ThrowIfCancellationRequested();
            if (this.entries >= limits.MaximumEntries) throw new ScanLimitException("The refresh entry limit was reached; counts are incomplete.");
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= limits.MaximumMilliseconds)
                throw new ScanLimitException("The refresh time limit was reached; counts are incomplete.");
        }
        internal void Visit() { this.Check(); this.entries++; }
    }

    private sealed class ScanLimitException(string message) : Exception(message);
}

// One background refresh at a time; reads reuse the same immutable snapshot.
// Cancellation bounds further metadata operations without waiting on IO at unload.
internal sealed class StorageOverviewService : IDisposable
{
    private readonly object gate = new();
    private readonly Func<CancellationToken, StorageOverviewView> scan;
    private readonly CancellationTokenSource cancellation = new();
    private StorageOverviewView view;
    private bool disposed;
    private bool running;

    internal StorageOverviewService(PluginStoragePaths paths, Func<CancellationToken, StorageOverviewView>? scan = null)
    {
        this.view = StorageOverviewScanner.Empty(paths);
        this.scan = scan ?? (token => StorageOverviewScanner.Scan(paths, token));
    }

    internal StorageOverviewView View { get { lock (this.gate) return this.view; } }

    internal bool Refresh()
    {
        lock (this.gate)
        {
            if (this.disposed || this.view.Busy) return false;
            this.view = this.view with { Busy = true, Error = null };
            this.running = true;
            _ = Task.Run(this.RefreshWorker);
            return true;
        }
    }

    private void RefreshWorker()
    {
        StorageOverviewView? result = null;
        string? error = null;
        try
        {
            result = this.scan(this.cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            error = "Storage refresh was cancelled. Previous results may be stale.";
        }
        catch (Exception ex)
        {
            error = $"Storage refresh failed ({ex.GetType().Name}). Previous results may be stale.";
        }
        finally
        {
            lock (this.gate)
            {
                this.running = false;
                if (this.disposed) this.cancellation.Dispose();
                else if (result is not null)
                {
                    this.view = result with { Busy = false };
                    if (result.ElapsedMilliseconds is { } elapsed) PerformanceDiagnostics.Record("storage.overview", elapsed);
                }
                else this.view = this.view with { Busy = false, Error = error };
            }
        }
    }

    public void Dispose()
    {
        lock (this.gate)
        {
            if (this.disposed) return;
            this.disposed = true;
            this.view = this.view with { Busy = false };
            this.cancellation.Cancel();
            if (!this.running) this.cancellation.Dispose();
        }
    }
}
