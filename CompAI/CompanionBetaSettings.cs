using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CompAI;

internal sealed record CompanionBetaSettings(bool InventoryHighlight = false, int InventoryHighlightSeconds = 15,
    bool ShopHighlight = false);

/// <summary>Local experimental UI preferences. Failed persistence disables the feature.</summary>
internal sealed class CompanionBetaSettingsStore
{
    private readonly string path = string.Empty;
    internal CompanionBetaSettings Settings { get; private set; } = new();
    internal string? Error { get; private set; }

    internal CompanionBetaSettingsStore(string path)
    {
        try { this.path = Path.GetFullPath(path); this.Settings = this.Load(); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (StorageError(ex)) { this.Error = $"Beta settings could not be loaded; experiments are off ({ex.GetType().Name})."; }
    }

    internal bool Save(CompanionBetaSettings value)
    {
        string? temporary = null;
        try
        {
            if (value.InventoryHighlightSeconds is < 5 or > 60) throw new ArgumentException("Invalid highlight duration.");
            if (this.path.Length == 0) throw new IOException("Invalid settings path.");
            RejectLink(this.path);
            var directory = Path.GetDirectoryName(this.path)!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, $".{Path.GetFileName(this.path)}.{Guid.NewGuid():N}.tmp");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 2,
                inventoryHighlight = value.InventoryHighlight, inventoryHighlightSeconds = value.InventoryHighlightSeconds,
                shopHighlight = value.ShopHighlight });
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            if (File.Exists(this.path)) File.Replace(temporary, this.path, null);
            else File.Move(temporary, this.path);
            this.Settings = value;
            this.Error = null;
            return true;
        }
        catch (Exception ex) when (StorageError(ex))
        {
            this.Settings = new();
            this.Error = $"Beta settings were not saved; experiments are off for this session ({ex.GetType().Name}).";
            return false;
        }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (Exception ex) when (StorageError(ex)) { }
        }
    }

    private CompanionBetaSettings Load()
    {
        RejectLink(this.path);
        using var stream = new FileStream(this.path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 2048) throw new InvalidDataException("Oversized beta settings.");
        var bytes = new byte[2049];
        var count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        if (count > 2048) throw new InvalidDataException("Oversized beta settings.");
        using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 3 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected beta settings.");
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in document.RootElement.EnumerateObject())
            if (!fields.Add(field.Name)) throw new InvalidDataException("Duplicate beta setting.");
        var root = document.RootElement;
        if (!root.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out var version) || version is not (1 or 2) ||
            !fields.SetEquals(version == 1 ? ["schemaVersion", "inventoryHighlight", "inventoryHighlightSeconds"] :
                ["schemaVersion", "inventoryHighlight", "inventoryHighlightSeconds", "shopHighlight"]))
            throw new InvalidDataException("Unknown or missing beta setting.");
        if (
            root.GetProperty("inventoryHighlight").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !root.GetProperty("inventoryHighlightSeconds").TryGetInt32(out var seconds) || seconds is < 5 or > 60 ||
            version == 2 && root.GetProperty("shopHighlight").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("Unsupported beta settings.");
        return new(root.GetProperty("inventoryHighlight").GetBoolean(), seconds,
            version == 2 && root.GetProperty("shopHighlight").GetBoolean());
    }

    private static void RejectLink(string path)
    {
        try { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked settings unsupported."); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
    private static bool StorageError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or
        ArgumentException or NotSupportedException or JsonException or InvalidOperationException;
}
