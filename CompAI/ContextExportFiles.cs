using System;
using System.IO;
using System.Text;

namespace CompAI;

internal sealed record ContextExportPaths(string Directory, string JsonFile, string TextFile);

internal static class ContextExportFiles
{
    internal static ContextExportPaths Save(string exportsRoot, string json, string text, DateTimeOffset exportedAtUtc)
    {
        var root = Path.GetFullPath(exportsRoot);
        Directory.CreateDirectory(root);
        var name = $"inventory-pedigrees-{exportedAtUtc.UtcDateTime:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}";
        var destination = Path.Combine(root, name);
        var staging = Path.Combine(root, "." + name + ".pending");
        var jsonPath = Path.Combine(staging, "inventory-pedigrees.json");
        var textPath = Path.Combine(staging, "inventory-pedigrees.txt");
        Directory.CreateDirectory(staging);
        try
        {
            WriteNew(jsonPath, json);
            WriteNew(textPath, text);
            // Publish the completed pair together, without replacing any earlier export.
            Directory.Move(staging, destination);
            return new ContextExportPaths(destination, Path.Combine(destination, "inventory-pedigrees.json"),
                Path.Combine(destination, "inventory-pedigrees.txt"));
        }
        catch
        {
            // Only clean the two files owned by this attempted export; never recursively
            // remove directories or touch exports from another attempt.
            try { File.Delete(jsonPath); File.Delete(textPath); Directory.Delete(staging, false); }
            catch { /* Preserve the original write error; a partial hidden folder can remain. */ }
            throw;
        }
    }

    private static void WriteNew(string path, string contents)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false, true));
        writer.Write(contents);
    }
}
