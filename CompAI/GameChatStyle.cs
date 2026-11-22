using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CompAI;

// Appearance only: invalid settings fall back to the default without authorizing any operation.
internal sealed class GameChatStyleStore
{
    internal const uint DefaultUserColorRgb = 0xFFA65A;
    private const int MaxFileBytes = 16 * 1024;
    private const int MaxPersonalityColors = 256;
    private readonly object gate = new();
    private readonly string path = "";
    private uint userColorRgb = DefaultUserColorRgb;
    private Dictionary<string, uint> personalityColors = new(StringComparer.Ordinal);
    private string? error;

    internal uint UserColorRgb { get { lock (this.gate) return this.userColorRgb; } }
    internal string? Error { get { lock (this.gate) return this.error; } }

    internal GameChatStyleStore(string path)
    {
        try
        {
            this.path = Path.GetFullPath(path);
            var loaded = this.Load();
            this.userColorRgb = loaded.UserColorRgb;
            this.personalityColors = loaded.PersonalityColors;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (IsStorageError(ex))
        {
            this.error = $"Chat colours could not be loaded ({ex.GetType().Name}). Using the default colours; the saved file has not been changed.";
        }
    }

    internal bool SetUserColor(uint rgb)
    {
        lock (this.gate)
            return this.Save(rgb, this.personalityColors);
    }

    internal uint GetPersonalityColor(string id)
    {
        lock (this.gate)
            return this.personalityColors.TryGetValue(id, out var rgb) ? rgb : GameChatStyle.PersonalityColorRgb(id);
    }

    internal bool HasPersonalityColor(string id)
    {
        lock (this.gate)
            return this.personalityColors.ContainsKey(id);
    }

    internal bool SetPersonalityColor(string id, uint? rgb)
    {
        lock (this.gate)
        {
            try
            {
                ValidatePersonalityId(id);
                if (rgb is > 0xFFFFFF) throw new ArgumentOutOfRangeException(nameof(rgb));
                var updated = new Dictionary<string, uint>(this.personalityColors, StringComparer.Ordinal);
                if (rgb is { } value) updated[id] = value;
                else updated.Remove(id);
                return this.Save(this.userColorRgb, updated);
            }
            catch (Exception ex) when (IsStorageError(ex))
            {
                this.SetSaveError(ex);
                return false;
            }
        }
    }

    // Call with the gate held. Publish both settings only after the atomic file replacement succeeds.
    private bool Save(uint rgb, Dictionary<string, uint> colors)
    {
        string? temporaryPath = null;
        try
        {
            if (rgb > 0xFFFFFF) throw new ArgumentOutOfRangeException(nameof(rgb));
            if (colors.Count > MaxPersonalityColors) throw new InvalidDataException("Too many personality colours.");
            if (this.path.Length == 0) throw new IOException("Invalid chat style path.");
            var directory = Path.GetDirectoryName(this.path)!;
            Directory.CreateDirectory(directory);
            RejectLink(this.path);
            temporaryPath = Path.Combine(directory, $".{Path.GetFileName(this.path)}.{Guid.NewGuid():N}.tmp");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 2, userColorRgb = rgb, personalityColors = colors });
            if (bytes.Length > MaxFileBytes) throw new InvalidDataException("Oversized chat style.");
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            RejectLink(this.path);
            if (File.Exists(this.path)) File.Replace(temporaryPath, this.path, null);
            else File.Move(temporaryPath, this.path);
            this.userColorRgb = rgb;
            this.personalityColors = colors;
            this.error = null;
            return true;
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            this.SetSaveError(ex);
            return false;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (Exception ex) when (IsStorageError(ex)) { }
            }
        }
    }

    private void SetSaveError(Exception ex) =>
        this.error = $"Chat colours were not saved ({ex.GetType().Name}). The previous colours remain in effect.";

    private (uint UserColorRgb, Dictionary<string, uint> PersonalityColors) Load()
    {
        RejectLink(this.path);
        using var stream = new FileStream(this.path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxFileBytes) throw new InvalidDataException("Oversized chat style.");
        var bytes = new byte[MaxFileBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = stream.Read(bytes, count, bytes.Length - count);
            if (read == 0) break;
            count += read;
        }
        if (count > MaxFileBytes) throw new InvalidDataException("Oversized chat style.");
        using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 4 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected chat style object.");
        var hasVersion = false;
        var hasColor = false;
        var hasPersonalityColors = false;
        var version = 0;
        uint rgb = 0;
        var colors = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            switch (property.Name)
            {
                case "schemaVersion" when !hasVersion:
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out version) || version is not (1 or 2))
                        throw new InvalidDataException("Unsupported chat style version.");
                    hasVersion = true;
                    break;
                case "userColorRgb" when !hasColor:
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetUInt32(out rgb) || rgb > 0xFFFFFF)
                        throw new InvalidDataException("Invalid chat colour.");
                    hasColor = true;
                    break;
                case "personalityColors" when !hasPersonalityColors:
                    if (property.Value.ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException("Expected personality colour object.");
                    foreach (var color in property.Value.EnumerateObject())
                    {
                        ValidatePersonalityId(color.Name);
                        if (color.Value.ValueKind != JsonValueKind.Number || !color.Value.TryGetUInt32(out var personalityRgb) || personalityRgb > 0xFFFFFF)
                            throw new InvalidDataException("Invalid personality colour.");
                        if (!colors.TryAdd(color.Name, personalityRgb))
                            throw new InvalidDataException("Duplicate personality colour.");
                        if (colors.Count > MaxPersonalityColors)
                            throw new InvalidDataException("Too many personality colours.");
                    }
                    hasPersonalityColors = true;
                    break;
                default: throw new InvalidDataException("Unknown or duplicate chat style setting.");
            }
        }
        if (!hasVersion || !hasColor) throw new InvalidDataException("Missing chat style setting.");
        if (hasPersonalityColors != (version == 2)) throw new InvalidDataException("Personality colours do not match the chat style version.");
        return (rgb, colors);
    }

    private static void ValidatePersonalityId(string id)
    {
        if (id is "builtin:none" or "builtin:armo") return;
        if (id is null || id.Length != 32) throw new InvalidDataException("Invalid personality ID.");
        foreach (var character in id)
            if ((character < '0' || character > '9') && (character < 'a' || character > 'f'))
                throw new InvalidDataException("Invalid personality ID.");
    }

    private static void RejectLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked chat style files are unsupported.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static bool IsStorageError(Exception ex) =>
        ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException;
}

// The editor keeps appearance changes pending until its single Save action succeeds.
internal sealed class PersonalityReplyColorDraft
{
    private string? presetId;
    private uint? savedOverride;
    internal uint? Override { get; private set; }
    internal uint DisplayRgb => this.Override ?? GameChatStyle.PersonalityColorRgb(this.presetId ?? "builtin:none");
    internal bool HasEdits => this.Override != this.savedOverride;

    internal void Load(string? id, uint? rgb)
    {
        this.presetId = id;
        this.savedOverride = this.Override = rgb;
    }

    internal void Select(uint rgb)
    {
        if (rgb > 0xFFFFFF) throw new ArgumentOutOfRangeException(nameof(rgb));
        this.Override = rgb;
    }

    internal void UseAutomatic() => this.Override = null;

    internal void Randomize()
    {
        // A fresh bright colour per press, saved as an ordinary preset override.
        // It never changes between replies or reloads.
        var previous = this.DisplayRgb;
        uint next;
        do
        {
            next = (uint)(Random.Shared.Next(144, 256) << 16 |
                Random.Shared.Next(144, 256) << 8 | Random.Shared.Next(144, 256));
        } while (next == previous);
        this.Select(next);
    }

    internal void Clone()
    {
        var colour = this.DisplayRgb;
        this.Load(null, null);
        this.Select(colour);
    }

    // Assigning the freshly saved personality's ID must not discard an unsaved colour.
    internal void SetPresetId(string id) => this.presetId = id;
    internal void MarkSaved() => this.savedOverride = this.Override;
}

internal static class GameChatStyle
{
    // Keep this palette and its order stable: each preset ID retains its colour across reloads and renames.
    // These bright colours are intended for the game's dark chat background; IDs can share a colour.
    private static readonly uint[] PersonalityPalette =
    [
        0x8FD8FF, // Sky blue.
        0x9FE3B1, // Mint green.
        0xD9B3FF, // Lilac.
        0xFFADC9, // Rose pink.
        0xF4E49D, // Soft gold.
        0x9FE7E5, // Aqua.
        0xBFC6FF, // Periwinkle.
        0xCBE795, // Lime green.
    ];

    internal static uint PersonalityColorRgb(string presetId)
    {
        ArgumentNullException.ThrowIfNull(presetId);
        if (presetId == "builtin:armo") return 0xFFB8D0;
        // FNV-1a over the ID's UTF-16 code units, independent of randomized string.GetHashCode().
        var hash = 2166136261u;
        foreach (var character in presetId)
            hash = unchecked((hash ^ character) * 16777619u);
        return PersonalityPalette[hash % (uint)PersonalityPalette.Length];
    }
}
