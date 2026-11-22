using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CompAI;

internal sealed record CompanionResponseSettings(int Minutes = 0);

internal static class CompanionResponseTimeout
{
    internal const int MinSeconds = 60;
    internal const int MaxSeconds = 1800;
    internal const int OuterGraceSeconds = 60;

    internal static int ResolveSeconds(int minutes, string? effort)
    {
        if (minutes is < 0 or > 30) throw new ArgumentOutOfRangeException(nameof(minutes));
        if (minutes is >= 1 and <= 30) return minutes * 60;
        return effort is "high" or "xhigh" or "max" or "ultra" ? 600 : 300;
    }
}

/// <summary>A local timing preference; it never changes reader or provider permissions.</summary>
internal sealed class CompanionResponseSettingsStore
{
    private const int MaxFileBytes = 1024;
    private readonly object gate = new();
    private readonly string path = string.Empty;
    private CompanionResponseSettings settings = new();
    private string? error;

    internal CompanionResponseSettings Settings { get { lock (this.gate) return this.settings; } }
    internal string? Error { get { lock (this.gate) return this.error; } }

    internal CompanionResponseSettingsStore(string path)
    {
        try
        {
            this.path = Path.GetFullPath(path);
            this.settings = this.Load();
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (IsStorageError(ex))
        {
            this.error = $"Response limit could not be loaded ({ex.GetType().Name}). Automatic is in effect; select a limit to save valid settings.";
        }
    }

    internal bool Save(CompanionResponseSettings value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (this.gate)
        {
            string? temporaryPath = null;
            try
            {
                if (value.Minutes is < 0 or > 30) throw new InvalidDataException("Invalid response limit.");
                if (this.path.Length == 0) throw new IOException("Invalid settings path.");
                var directory = Path.GetDirectoryName(this.path)!;
                Directory.CreateDirectory(directory);
                RejectLink(this.path);
                temporaryPath = Path.Combine(directory, $".{Path.GetFileName(this.path)}.{Guid.NewGuid():N}.tmp");
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, minutes = value.Minutes });
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                if (File.Exists(this.path)) File.Replace(temporaryPath, this.path, null);
                else File.Move(temporaryPath, this.path);
                this.settings = value;
                this.error = null;
                return true;
            }
            catch (Exception ex) when (IsStorageError(ex))
            {
                this.error = $"Response limit was not saved ({ex.GetType().Name}). The previous limit remains in effect.";
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
    }

    private CompanionResponseSettings Load()
    {
        RejectLink(this.path);
        using var stream = new FileStream(this.path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxFileBytes) throw new InvalidDataException("Oversized response settings.");
        var bytes = new byte[MaxFileBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = stream.Read(bytes, count, bytes.Length - count);
            if (read == 0) break;
            count += read;
        }
        if (count > MaxFileBytes) throw new InvalidDataException("Oversized response settings.");
        using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 4 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected response settings object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var minutes = 0;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new InvalidDataException("Duplicate response setting.");
            switch (property.Name)
            {
                case "schemaVersion":
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var version) || version != 1)
                        throw new InvalidDataException("Unsupported response settings version.");
                    break;
                case "minutes":
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out minutes) || minutes is < 0 or > 30)
                        throw new InvalidDataException("Invalid response limit.");
                    break;
                default: throw new InvalidDataException("Unknown response setting.");
            }
        }
        if (seen.Count != 2) throw new InvalidDataException("Missing response setting.");
        return new(minutes);
    }

    private static void RejectLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked response settings are unsupported.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static bool IsStorageError(Exception ex) =>
        ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException;
}
