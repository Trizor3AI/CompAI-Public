using System;
using System.IO;
using System.Text.Json;

namespace CompAI;

// Appearance only; separate from access permissions, account state and personality prompts.
internal sealed class CompanionUiSettingsStore
{
    private readonly string path;
    internal bool HideKofi { get; private set; }
    internal string? Error { get; private set; }

    internal CompanionUiSettingsStore(string path)
    {
        this.path = path;
        try
        {
            RejectLink(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bytes = new byte[1025];
            var length = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            if (length > 1024) throw new InvalidDataException("Oversized interface settings.");
            using var document = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid interface settings.");
            var versionSeen = false;
            var hideSeen = false;
            var hide = false;
            foreach (var field in root.EnumerateObject())
            {
                if (field.Name == "schemaVersion" && !versionSeen && field.Value.ValueKind == JsonValueKind.Number &&
                    field.Value.TryGetInt32(out var version) && version == 1) versionSeen = true;
                else if (field.Name == "hideKofi" && !hideSeen && field.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                { hideSeen = true; hide = field.Value.GetBoolean(); }
                else throw new InvalidDataException("Invalid interface setting.");
            }
            if (!versionSeen || !hideSeen) throw new InvalidDataException("Incomplete interface settings.");
            this.HideKofi = hide;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (StorageError(ex))
        { this.Error = $"Interface preferences could not be loaded ({ex.GetType().Name}). Defaults are in use."; }
    }

    internal bool SetHideKofi(bool hidden)
    {
        string? temporary = null;
        try
        {
            RejectLink(this.path);
            var directory = Path.GetDirectoryName(this.path)!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, $".{Path.GetFileName(this.path)}.{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new { schemaVersion = 1, hideKofi = hidden });
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(this.path)) File.Replace(temporary, this.path, null);
            else File.Move(temporary, this.path);
            this.HideKofi = hidden;
            this.Error = null;
            return true;
        }
        catch (Exception ex) when (StorageError(ex))
        { this.Error = $"Interface preferences were not saved ({ex.GetType().Name}). The previous choice remains in effect."; return false; }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (Exception ex) when (StorageError(ex)) { }
        }
    }

    private static void RejectLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked interface settings are unsupported.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static bool StorageError(Exception ex) => ex is IOException or UnauthorizedAccessException or
        InvalidDataException or ArgumentException or NotSupportedException or JsonException;
}
