using System;
using System.IO;
using System.Linq;
using System.Text;

namespace CompAI;

internal sealed record PluginStoragePaths(string Configuration, string CompanionProfile)
{
    private const string MigrationMarker = ".legacy-inspector-profile";
    private const string MarkerContents = "CompAI uses the existing Inspector companion profile.\n";
    private const string MigrationReceipt = ".compai-migration-complete";
    private const string ReceiptContents = "CompAI legacy import completed; version 1.\n";
    private static readonly string[] SettingsFiles =
    [
        "companion-access.json", "companion-model.json", "companion-personalities.json", "game-chat-style.json",
    ];
    private static readonly string[] DataDirectories = ["chats", "character-cache", "exports"];

    internal static PluginStoragePaths Prepare(string configurationDirectory)
    {
        var configuration = Path.GetFullPath(configurationDirectory);
        var parent = Directory.GetParent(configuration)?.FullName
            ?? throw new IOException("CompAI settings have no parent directory.");
        var legacy = Path.Combine(parent, "Inspector");
        CheckDirectory(parent);
        CheckDirectory(configuration);
        Directory.CreateDirectory(configuration);

        var marker = Path.Combine(configuration, MigrationMarker);
        if (File.Exists(marker))
        {
            CheckNotLinked(marker);
            if (new FileInfo(marker).Length > 256 || File.ReadAllText(marker) != MarkerContents)
                throw new IOException("The CompAI settings migration marker is invalid.");
            CheckDirectory(legacy);
            // Backfill installations migrated before receipts existed. This sibling
            // record survives Dalamud resetting CompAI's own configuration directory.
            EnsureMigrationReceipt(legacy);
            return Paths(configuration, legacy);
        }

        // Existing CompAI settings always win. Only import into a genuinely empty new directory.
        if (Directory.EnumerateFileSystemEntries(configuration).Any() || !Directory.Exists(legacy))
            return Paths(configuration, configuration);
        CheckDirectory(legacy);
        if (HasMigrationReceipt(legacy))
            return Paths(configuration, configuration);
        if (!SettingsFiles.Any(name => File.Exists(Path.Combine(legacy, name))) &&
            !DataDirectories.Any(name => Directory.Exists(Path.Combine(legacy, name))) &&
            !Directory.Exists(Path.Combine(legacy, "companion")))
            return Paths(configuration, configuration);

        var staging = Path.Combine(parent, ".CompAI-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var name in SettingsFiles)
            {
                var source = Path.Combine(legacy, name);
                if (!File.Exists(source)) continue;
                CopyFile(source, Path.Combine(staging, name));
            }
            foreach (var name in DataDirectories)
            {
                var source = Path.Combine(legacy, name);
                if (Directory.Exists(source)) CopyDirectory(source, Path.Combine(staging, name), 0);
            }
            CheckDirectory(Path.Combine(legacy, "companion"));
            File.WriteAllText(Path.Combine(staging, MigrationMarker), MarkerContents);

            // Keep CODEX_HOME at its original path: Windows keyring credentials are scoped to it.
            // Publish only a complete import. Non-recursive deletion fails if another writer appeared.
            CheckDirectory(configuration);
            Directory.Delete(configuration, recursive: false);
            Directory.Move(staging, configuration);
            EnsureMigrationReceipt(legacy);
            return Paths(configuration, legacy);
        }
        catch (Exception ex)
        {
            Directory.CreateDirectory(configuration);
            // Preserve any unfinished staging copy for recovery; never touch the original data.
            throw new IOException("CompAI could not import the existing Inspector settings. " +
                "The original settings are unchanged. Close the old plugin and retry.", ex);
        }
    }

    private static bool HasMigrationReceipt(string legacy)
    {
        var receipt = Path.Combine(legacy, MigrationReceipt);
        if (!File.Exists(receipt) && !Directory.Exists(receipt)) return false;
        CheckNotLinked(receipt);
        if (Directory.Exists(receipt)) throw new IOException("The CompAI migration receipt is not a file.");
        using var stream = new FileStream(receipt, FileMode.Open, FileAccess.Read, FileShare.Read);
        var bytes = new byte[257];
        var count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        if (count > 256 || Encoding.UTF8.GetString(bytes, 0, count) != ReceiptContents)
            throw new IOException("The CompAI migration receipt is invalid; legacy settings were not imported.");
        return true;
    }

    private static void EnsureMigrationReceipt(string legacy)
    {
        if (HasMigrationReceipt(legacy)) return;
        CheckDirectory(legacy);
        Directory.CreateDirectory(legacy);
        var receipt = Path.Combine(legacy, MigrationReceipt);
        var temporary = Path.Combine(legacy, ".CompAI-migration-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Encoding.UTF8.GetBytes(ReceiptContents));
                stream.Flush(flushToDisk: true);
            }
            // Never overwrite a receipt, including one changed concurrently.
            try { File.Move(temporary, receipt); }
            catch (IOException) when (HasMigrationReceipt(legacy)) { }
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static PluginStoragePaths Paths(string configuration, string profileOwner)
    {
        var profile = Path.Combine(profileOwner, "companion");
        CheckDirectory(profile);
        CheckDirectory(Path.Combine(profile, "home"));
        return new PluginStoragePaths(configuration, profile);
    }

    private static void CopyDirectory(string source, string destination, int depth)
    {
        if (depth > 16) throw new IOException("The legacy settings directory is too deeply nested.");
        CheckDirectory(source);
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            CheckNotLinked(entry);
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if (Directory.Exists(entry)) CopyDirectory(entry, target, depth + 1);
            else CopyFile(entry, target);
        }
    }

    private static void CopyFile(string source, string destination)
    {
        CheckNotLinked(source);
        File.Copy(source, destination, overwrite: false);
    }

    private static void CheckDirectory(string path)
    {
        if (Directory.Exists(path) || File.Exists(path))
        {
            CheckNotLinked(path);
            if (!Directory.Exists(path)) throw new IOException("A settings directory is occupied by a file.");
        }
    }

    private static void CheckNotLinked(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("CompAI settings migration does not follow symbolic links or junctions.");
    }
}
