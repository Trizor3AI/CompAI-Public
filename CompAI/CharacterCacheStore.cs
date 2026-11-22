using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace CompAI;

// A single background writer coalesces immutable envelopes by ContentId. The
// caller supplies increasing revisions after loading a character's saved state.
// Clear is an ordinary newer envelope with a null category, never a separate delete.
internal sealed class CharacterCacheStore : IDisposable, IAsyncDisposable
{
    internal const int MaxFileBytes = 16 * 1024 * 1024;
    private const int MaxPendingCharacters = 128;
    private static readonly JsonSerializerOptions DiskJson = new() { MaxDepth = 32 };
    private readonly object gate = new();
    private readonly string directory;
    private readonly Dictionary<ulong, CharacterCacheData> pending = new();
    private readonly Dictionary<ulong, CharacterCacheData> failedWrites = new();
    private readonly Dictionary<ulong, long> revisions = new();
    private readonly HashSet<ulong> awaitingRestore = new();
    private readonly Dictionary<string, string> errors = new(StringComparer.Ordinal);
    private CharacterCacheData? writing;
    private Task worker = Task.CompletedTask;
    private Task? disposeTask;
    private bool draining;
    private bool disposed;
    private ulong? selectedId;
    private long selectionVersion;
    private bool selectionPending;
    private bool selectionFailed;

    internal CharacterCacheStore(string directory) => this.directory = Path.GetFullPath(directory);

    internal string? Error
    {
        get
        {
            lock (this.gate)
                return this.errors.Count == 0 ? null : this.errors.Values.First() +
                    (this.errors.Count > 1 ? $" ({this.errors.Count - 1} other cache issue(s) remain.)" : "");
        }
    }

    internal bool Offer(CharacterCacheData value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (this.gate)
        {
            if (this.disposed || this.awaitingRestore.Contains(value.ContentId)) return false;
            if (value.ContentId == 0 || value.Revision < 0)
            {
                this.errors[$"offer:{value.ContentId}"] = "Character cache update has no valid identity or revision.";
                return false;
            }
            if (this.revisions.TryGetValue(value.ContentId, out var current) && value.Revision <= current)
                return false;
            if (!this.pending.ContainsKey(value.ContentId) && this.pending.Count >= MaxPendingCharacters)
            {
                this.errors[$"offer:{value.ContentId}"] = "Character cache write queue is full; the update was not saved.";
                return false;
            }
            if (!this.revisions.ContainsKey(value.ContentId) && this.revisions.Count >= MaxPendingCharacters)
            {
                this.errors[$"offer:{value.ContentId}"] = "Character cache session limit reached; the update was not saved.";
                return false;
            }
            this.revisions[value.ContentId] = value.Revision;
            this.errors.Remove($"offer:{value.ContentId}");
            this.pending[value.ContentId] = value;
            this.SelectUnderLock(value.ContentId);
            this.StartWriterUnderLock();
            return true;
        }
    }

    // Selecting a character changes only the small offline-view index.
    internal void Select(ulong contentId)
    {
        if (contentId == 0) return;
        lock (this.gate)
        {
            if (this.disposed) return;
            this.SelectUnderLock(contentId);
            this.StartWriterUnderLock();
        }
    }

    internal Task<CharacterCacheLoadResult> LoadAsync(ulong contentId) => contentId == 0
        ? Task.FromResult(new CharacterCacheLoadResult(CharacterCacheLoadStatus.Missing, null))
        : Task.Run(() => this.Load(contentId));

    internal async Task<CharacterCacheLoadResult?> RestoreForSessionAsync(ulong contentId, Func<bool> sessionActive)
    {
        while (sessionActive())
        {
            var result = contentId == 0 ? await this.LoadLastAsync().ConfigureAwait(false)
                : await this.LoadAsync(contentId).ConfigureAwait(false);
            if (result.Status != CharacterCacheLoadStatus.TemporarilyUnavailable) return result;
            // A sharing failure leaves the baseline unknown. Retry away from the game thread.
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        return null;
    }

    internal async Task<CharacterCacheLoadResult> LoadLastAsync()
    {
        ulong? selected;
        long version;
        lock (this.gate) { selected = this.selectedId; version = this.selectionVersion; }
        if (!selected.HasValue)
        {
            var index = await Task.Run(this.ReadSelection).ConfigureAwait(false);
            selected = index.Value?.ContentId;
            lock (this.gate)
            {
                if (this.selectionVersion != version) selected = this.selectedId;
            }
            if (!selected.HasValue) return new(index.Status, null);
        }
        return await this.LoadAsync(selected.Value).ConfigureAwait(false);
    }

    // Drain accepted updates, then retry each retained failure once. A persistent
    // storage error returns normally with Error set; it never starts a retry loop.
    internal async Task FlushAsync()
    {
        Task initial;
        lock (this.gate) initial = this.worker;
        await initial.ConfigureAwait(false);
        Task retry;
        lock (this.gate)
        {
            foreach (var (id, failed) in this.failedWrites)
                if (this.revisions.GetValueOrDefault(id, -1) == failed.Revision &&
                    !this.pending.ContainsKey(id) && this.writing?.ContentId != id)
                    this.pending[id] = failed;
            if (this.selectionFailed && this.selectedId.HasValue) this.selectionPending = true;
            this.StartWriterUnderLock();
            retry = this.worker;
        }
        await retry.ConfigureAwait(false);
    }

    public void Dispose() => this.DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        lock (this.gate)
        {
            if (this.disposeTask is not null) return new ValueTask(this.disposeTask);
            this.disposed = true;
            this.disposeTask = this.FlushAsync();
            return new ValueTask(this.disposeTask);
        }
    }

    private void SelectUnderLock(ulong contentId)
    {
        if (this.selectedId == contentId) return;
        this.selectedId = contentId;
        ++this.selectionVersion;
        this.selectionPending = true;
    }

    private void StartWriterUnderLock()
    {
        if (this.draining || this.pending.Count == 0 && !this.selectionPending) return;
        this.draining = true;
        this.worker = Task.Run(this.Drain);
    }

    private void Drain()
    {
        while (true)
        {
            CharacterCacheData? value;
            ulong? selection;
            long version;
            lock (this.gate)
            {
                value = this.pending.Values.FirstOrDefault();
                if (value is not null) this.pending.Remove(value.ContentId);
                this.writing = value;
                selection = value is null && this.selectionPending ? this.selectedId : null;
                version = this.selectionVersion;
                if (selection.HasValue) this.selectionPending = false;
                if (value is null && !selection.HasValue)
                {
                    this.draining = false;
                    return;
                }
            }
            if (value is not null)
                this.Write(value);
            else if (selection.HasValue)
                this.WriteSelection(selection.Value, version);
            lock (this.gate) this.writing = null;
        }
    }

    private CharacterCacheLoadResult Load(ulong contentId)
    {
        while (true)
        {
            long before;
            lock (this.gate)
            {
                if (this.LatestUnderLock(contentId) is { } queued) return new(CharacterCacheLoadStatus.Loaded, queued);
                before = this.revisions.GetValueOrDefault(contentId, -1);
            }
            var saved = this.Read<CharacterCacheData>(this.CharacterPath(contentId), MaxFileBytes,
                value => { Validate(value); if (value.ContentId != contentId) throw new InvalidDataException("Character identity mismatch"); });
            lock (this.gate)
            {
                if (this.LatestUnderLock(contentId) is { } queued) return new(CharacterCacheLoadStatus.Loaded, queued);
                var latest = this.revisions.GetValueOrDefault(contentId, -1);
                // A rename can complete while a reader still has the previous file
                // open. Retry rather than publishing a pre-clear snapshot.
                if (latest != before) continue;
                if (saved.Status == CharacterCacheLoadStatus.TemporarilyUnavailable)
                    this.awaitingRestore.Add(contentId);
                else
                    this.awaitingRestore.Remove(contentId);
                if (saved.Value is not null && (this.revisions.ContainsKey(contentId) || this.revisions.Count < MaxPendingCharacters))
                    this.revisions[contentId] = Math.Max(latest, saved.Value.Revision);
                return new(saved.Status, saved.Value);
            }
        }
    }

    private CharacterCacheData? LatestUnderLock(ulong id) => this.pending.GetValueOrDefault(id) ??
        (this.writing?.ContentId == id ? this.writing : null) ?? this.failedWrites.GetValueOrDefault(id);

    private ReadResult<SelectionIndex> ReadSelection() => this.Read<SelectionIndex>(Path.Combine(this.directory, "last-character.json"),
        1024, value =>
        {
            if (value.SchemaVersion != 1 || value.ContentId == 0) throw new InvalidDataException("Invalid character index");
        });

    private ReadResult<T> Read<T>(string path, int limit, Action<T> validate) where T : class
    {
        var inspected = false;
        try
        {
            this.CheckDirectory();
            if (IsLink(path)) throw new InvalidDataException("Linked cache file");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > limit) throw new InvalidDataException("Oversized cache file");
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw new InvalidDataException("Cache file grew during read");
            inspected = true;
            // Inspect the envelope before deserializing fields whose shape may
            // have changed in a newer version.
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = DiskJson.MaxDepth });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("SchemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.Number)
                throw new InvalidDataException("Missing cache schema");
            if (!schema.TryGetInt32(out var version) || version != 1 && !(typeof(T) == typeof(CharacterCacheData) && version is 2 or 3 or 4))
            {
                var versionLabel = schema.TryGetInt32(out version) ? version.ToString(CultureInfo.InvariantCulture) : "outside the supported range";
                this.NoteError("read:" + path, $"Character cache uses unsupported schema {versionLabel}; the file is protected from replacement.");
                return new(CharacterCacheLoadStatus.UnsupportedSchema, null, false);
            }
            var value = JsonSerializer.Deserialize<T>(bytes, DiskJson) ?? throw new InvalidDataException("Empty cache file");
            validate(value);
            this.ClearError("read:" + path);
            return new(CharacterCacheLoadStatus.Loaded, value, true);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            this.ClearError("read:" + path);
            return new(CharacterCacheLoadStatus.Missing, null, true);
        }
        catch (Exception ex) when (StorageFailure(ex))
        {
            this.NoteError("read:" + path, $"Could not load character cache ({ex.GetType().Name}); saved data was left untouched.");
            if (ex is IOException or UnauthorizedAccessException)
                return new(CharacterCacheLoadStatus.TemporarilyUnavailable, null, false);
            return new(CharacterCacheLoadStatus.Unreadable, null, inspected);
        }
    }

    private void Write(CharacterCacheData value)
    {
        var valid = false;
        var saved = this.WriteAtomic(this.CharacterPath(value.ContentId), value, () =>
            this.revisions.GetValueOrDefault(value.ContentId, -1) == value.Revision,
            () => { Validate(value); valid = true; }, existing =>
            {
                Validate(existing);
                if (existing.ContentId != value.ContentId) throw new InvalidDataException("Character identity mismatch");
            });
        lock (this.gate)
        {
            if (!saved && valid && this.revisions.GetValueOrDefault(value.ContentId, -1) == value.Revision)
                this.failedWrites[value.ContentId] = value;
            else if (saved && this.failedWrites.TryGetValue(value.ContentId, out var failed) && failed.Revision <= value.Revision)
                this.failedWrites.Remove(value.ContentId);
        }
    }

    private void WriteSelection(ulong contentId, long version)
    {
        var saved = this.WriteAtomic(Path.Combine(this.directory, "last-character.json"), new SelectionIndex(1, contentId),
            () => this.selectionVersion == version, () => { }, existing =>
            {
                if (existing.ContentId == 0) throw new InvalidDataException("Invalid character index");
            }, 1024);
        lock (this.gate)
            if (this.selectionVersion == version) this.selectionFailed = !saved;
    }

    private bool WriteAtomic<T>(string path, T value, Func<bool> isCurrent, Action validate,
        Action<T> validateExisting, int limit = MaxFileBytes) where T : class
    {
        var temporary = Path.Combine(this.directory, Guid.NewGuid().ToString("N") + ".tmp");
        var created = false;
        try
        {
            validate();
            Directory.CreateDirectory(this.directory);
            this.CheckDirectory();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, DiskJson);
            if (bytes.Length > limit) throw new InvalidDataException("Cache exceeds storage limit");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            // Parse and validate on the writer thread without holding the lock
            // used by offers from the framework thread.
            var existing = this.Read(path, limit, validateExisting);
            lock (this.gate)
            {
                if (!isCurrent()) return true;
                if (!existing.CanReplace)
                {
                    this.NoteError("write:" + path, "Character cache could not be safely inspected; replacement is blocked and latest changes are only held in memory.");
                    return false;
                }
                if (existing.Status != CharacterCacheLoadStatus.Missing)
                {
                    var backup = path + (existing.Status == CharacterCacheLoadStatus.Loaded ? ".bak" : ".quarantine");
                    try
                    {
                        if (IsLink(backup)) throw new InvalidDataException("Linked cache backup");
                    }
                    catch (FileNotFoundException) { }
                    // Keep one last-good and one quarantined copy. Corruption
                    // must not erase a last-good backup. Neither is auto-restored:
                    // an explicit Clear stays final.
                    File.Replace(temporary, path, backup);
                }
                else File.Move(temporary, path);
                this.ClearError("read:" + path);
                this.ClearError("write:" + path);
            }
            return true;
        }
        catch (Exception ex) when (StorageFailure(ex))
        {
            this.NoteError("write:" + path, $"Could not save character cache ({ex.GetType().Name}); latest changes may be unavailable after reload.");
            return false;
        }
        finally
        {
            if (created)
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { this.NoteError("cleanup:" + temporary, $"Could not remove temporary cache file ({ex.GetType().Name})."); }
            }
        }
    }

    private static void Validate(CharacterCacheData value)
    {
        if (value.SchemaVersion is not (1 or 2 or 3 or 4) || value.ContentId == 0 || value.Revision < 0 || value.SavedAtUtc == default ||
            !ValidText(value.CharacterLabel, 256)) throw new InvalidDataException("Invalid cache metadata");
        if (value.Storage.IsDefault || value.Storage.Length > StorageSnapshot.MaxContainers ||
            value.SchemaVersion == 1 && value.Storage.Length != 0 || value.Storage.Any(item => !StorageSnapshot.Valid(item)) ||
            value.Storage.Sum(item => item.Items.Length) > StorageSnapshot.MaxRows ||
            value.Storage.Select(item => (item.Category, item.SourceKey, item.Container)).Distinct().Count() != value.Storage.Length)
            throw new InvalidDataException("Invalid storage observations");
        if (value.SchemaVersion < 3 && !value.Progression.IsDefaultOrEmpty)
            throw new InvalidDataException("Progression observations require cache schema 3.");
        ProgressionMenuCache.Validate(value.Progression);
        if (value.SchemaVersion < 4 && (!value.Menus.IsDefaultOrEmpty ||
            value.Progression.Any(page => page.Category is "hunting_log" or "challenge_log")))
            throw new InvalidDataException("New menu observations require cache schema 4.");
        MenuObservationCache.Validate(value.Menus);
        if (value.Inventory is { } inventory)
        {
            if (inventory.CapturedAtUtc == default || !ValidArray(inventory.Bags, 4) || inventory.Bags.Length != 4)
                throw new InvalidDataException("Invalid inventory snapshot");
            var seenBags = new HashSet<string>(StringComparer.Ordinal);
            foreach (var bag in inventory.Bags)
            {
                if (bag is null || bag.Bag is not ("Inventory1" or "Inventory2" or "Inventory3" or "Inventory4") ||
                    !seenBags.Add(bag.Bag) || bag.SlotCount is not (null or 35) ||
                    !ValidOptionalText(bag.UnavailableReason) || !ValidArray(bag.Items, 35) ||
                    (bag.SlotCount is null && (string.IsNullOrWhiteSpace(bag.UnavailableReason) || bag.Items.Length != 0)))
                    throw new InvalidDataException("Invalid inventory bag");
                var seenSlots = new HashSet<uint>();
                foreach (var item in bag.Items)
                {
                    if (item is null || item.Slot >= 35 || !seenSlots.Add(item.Slot) || item.ItemId == 0 ||
                        item.BaseItemId == 0 || item.Quantity <= 0 || !ValidOptionalText(item.Name))
                        throw new InvalidDataException("Invalid inventory item");
                    if (item.Pedigree is { } pedigree && (!ValidText(pedigree.Source) || !ValidText(pedigree.Status) ||
                        pedigree.ObservedAtUtc == default || !ValidText(pedigree.Kind) || !ValidText(pedigree.Sex) ||
                        !ValidArray(pedigree.FatherStars, 5) || !ValidArray(pedigree.MotherStars, 5) ||
                        pedigree.FatherStars.Length != 5 || pedigree.MotherStars.Length != 5 ||
                        pedigree.FatherStars.Any(stars => stars is < 1 or > 4) || pedigree.MotherStars.Any(stars => stars is < 1 or > 4)))
                        throw new InvalidDataException("Invalid pedigree observation");
                }
            }
        }
        if (value.Quests is { } quests)
        {
            if (quests.CapturedAtUtc == default || !double.IsFinite(quests.CaptureDurationMs) || quests.CaptureDurationMs < 0 ||
                !ValidText(quests.SourceBuild) || !ValidText(quests.ReadinessNote) || !ValidText(quests.CoverageNote) ||
                !ValidOptionalText(quests.CatalogNote) || quests.CatalogRows is < 0 or > 65536 ||
                quests.CompletionChecks is < 0 or > 65536 || quests.ActiveSlotCount is < 0 or > 10000 ||
                !ValidArray(quests.Quests, 65536)) throw new InvalidDataException("Invalid quest snapshot");
            foreach (var quest in quests.Quests)
                if (quest is null || !ValidOptionalText(quest.Name) || !ValidArray(quest.Variables, 256) ||
                    !ValidOptionalText(quest.UnavailableReason) || !ValidOptionalText(quest.JournalText, 32768))
                    throw new InvalidDataException("Invalid quest observation");
        }
    }

    private static bool ValidArray<T>(ImmutableArray<T> values, int max) => !values.IsDefault && values.Length <= max;
    private static bool ValidText(string? text, int max = 8192) => text is not null && text.Length <= max;
    private static bool ValidOptionalText(string? text, int max = 8192) => text is null || text.Length <= max;
    private string CharacterPath(ulong id) => Path.Combine(this.directory, id.ToString("X16", CultureInfo.InvariantCulture) + ".json");
    private void CheckDirectory()
    {
        var attributes = File.GetAttributes(this.directory);
        if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
            throw new InvalidDataException("Invalid character cache directory");
    }
    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static bool StorageFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or
        JsonException or InvalidDataException or ArgumentException or NotSupportedException or OverflowException;
    private void NoteError(string key, string message) { lock (this.gate) this.errors[key] = message; }
    private void ClearError(string key) { lock (this.gate) this.errors.Remove(key); }
    private sealed record ReadResult<T>(CharacterCacheLoadStatus Status, T? Value, bool CanReplace) where T : class;
    private sealed record SelectionIndex(int SchemaVersion, ulong ContentId);
}
