using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CompAI;

// The package is the only Python source. Never resolve an interpreter from PATH,
// app execution aliases, the working directory, or a user's Python environment.
internal static class CompanionPythonRuntime
{
    internal const string Version = "3.14.8";
    internal const string Ready = "Embedded Python " + Version + ": package verified; standard library ready.";
    private const string Marker = "COMPAI_PYTHON_OK|" + Version;
    private const string Probe = "import sys, ssl, ctypes, hashlib, json, pathlib, subprocess, threading, queue, socket, urllib.request, argparse, webbrowser; " +
        "assert sys.version_info[:3] == (3,14,8); assert sys.flags.isolated and sys.flags.no_site; " +
        "assert ssl.create_default_context().get_ca_certs(); print('" + Marker + "')";
    private const int MaximumManifestBytes = 65536;

    internal static async Task<string> PrepareAsync(string companionDirectory, CancellationToken token)
    {
        var executable = await ValidateBundleAsync(companionDirectory, token);
        await ProbeAsync(executable, token);
        return executable;
    }

    internal static async Task<string> ValidateBundleAsync(string companionDirectory, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var manifestFile = new FileInfo(Path.Combine(companionDirectory, "python-runtime.json"));
            var directory = Path.Combine(companionDirectory, "python");
            if (!manifestFile.Exists || !Directory.Exists(directory))
                throw Failure("python.missing");
            if (manifestFile.Length > MaximumManifestBytes ||
                (manifestFile.Attributes & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw Failure("python.integrity");
            byte[] manifest;
            await using (var stream = manifestFile.Open(FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > MaximumManifestBytes) throw Failure("python.integrity");
                manifest = new byte[checked((int)stream.Length)];
                await stream.ReadExactlyAsync(manifest, token);
            }
            using var document = JsonDocument.Parse(manifest, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Select(entry => entry.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count() ||
                root.GetProperty("schema").GetInt32() != 1 || root.GetProperty("version").GetString() != Version ||
                root.GetProperty("arch").GetString() != "win-x64" || root.GetProperty("executable").GetString() != "python.exe" ||
                root.GetProperty("files").ValueKind != JsonValueKind.Object)
                throw Failure("python.integrity");
            var files = root.GetProperty("files").EnumerateObject().ToArray();
            if (files.Length is < 5 or > 128) throw Failure("python.integrity");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in files)
            {
                if (entry.Name.Length is < 1 or > 100 || entry.Name is "." or ".." ||
                    entry.Name.StartsWith('.') || entry.Name.EndsWith('.') ||
                    entry.Name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')) ||
                    !names.Add(entry.Name) || entry.Value.ValueKind != JsonValueKind.String ||
                    entry.Value.GetString() is not { Length: 64 } digest || !digest.All(char.IsAsciiHexDigit))
                    throw Failure("python.integrity");
                var file = new FileInfo(Path.Combine(directory, entry.Name));
                if (!file.Exists || file.Length > 134217728 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw Failure("python.integrity");
                await using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
                if (!actual.Equals(digest, StringComparison.OrdinalIgnoreCase)) throw Failure("python.integrity");
            }
            if (!new[] { "python.exe", "python3.dll", "python314.dll", "python314.zip", "python314._pth" }.All(names.Contains))
                throw Failure("python.integrity");
            // Additional .pth, DLL or Python files can change the interpreter's behavior.
            // A partial overwrite of an old release therefore requires a clean reinstall.
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                    !names.Remove(Path.GetFileName(path))) throw Failure("python.integrity");
            if (names.Count != 0 || await File.ReadAllTextAsync(Path.Combine(directory, "python314._pth"), token) != "python314.zip\n.\n..\n")
                throw Failure("python.integrity");
            return Path.GetFullPath(Path.Combine(directory, "python.exe"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
            InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException)
        {
            throw Failure("python.integrity");
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string executable, string workingDirectory)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in new[] { "-I", "-S", "-B", "-X", "utf8", "-u" }) start.ArgumentList.Add(argument);
        return start;
    }

    internal static async Task ProbeAsync(string executable, CancellationToken token, TimeSpan? limit = null,
        string? fixtureProbe = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(limit ?? TimeSpan.FromSeconds(5));
        using var process = new Process { StartInfo = CreateStartInfo(executable, Path.GetDirectoryName(executable)!) };
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(fixtureProbe ?? Probe);
        Task<string>? output = null;
        Task<string>? error = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!process.Start()) throw Failure("python.start");
            output = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            error = ReadBoundedAsync(process.StandardError, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var streams = await Task.WhenAll(output, error);
            if (process.ExitCode != 0 || streams[0].Trim() != Marker) throw Failure("python.prerequisite");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw Failure("python.timeout");
        }
        catch (Win32Exception) { throw Failure("python.start"); }
        catch (IOException) { throw Failure("python.prerequisite"); }
        finally
        {
            timeout.Cancel();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            if (output is not null && error is not null)
            {
                try { await Task.WhenAll(output, error); }
                catch (Exception exception) when (exception is OperationCanceledException or IOException or CompanionSetupException) { }
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[512];
        int count;
        while ((count = await reader.ReadAsync(buffer, token)) > 0)
        {
            if (result.Length + count > 4096) throw Failure("python.prerequisite");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }

    // Stderr can contain user paths or loader details. Drain it to avoid blocking
    // the bridge, without storing, logging or displaying its content.
    internal static async Task DiscardErrorAsync(StreamReader reader)
    {
        try
        {
            var buffer = new char[1024];
            while (await reader.ReadAsync(buffer) > 0) { }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
    }

    internal static CompanionSetupException Failure(string stage) => new(stage, stage switch
    {
        "installation" => "companion/companion.py is missing beside the loaded DLL. Replace any old Inspector development entry with CompAI.dll from the complete plugin folder; copying only the DLL is not enough.",
        "python.missing" => "The bundled Python files are missing. Reinstall the complete CompAI folder, including companion/python and companion/python-runtime.json; a system Python installation is not used.",
        "python.integrity" => "The bundled Python files are incomplete, changed, or mixed with another release. Reinstall CompAI into a clean folder; do not replace individual Python files.",
        "python.start" => "Windows could not start the bundled Python. Check whether security software blocked the package, then reinstall the complete Windows x64 CompAI release.",
        "python.timeout" => "The bundled Python prerequisite check timed out. Cancelled startup made no provider request. Check whether security software blocked Python, then refresh account status to try again.",
        "python.prerequisite" => "The bundled Python could not load its required standard library or Windows trust store. Reinstall the complete CompAI folder and check Windows security settings.",
        "profile.storage" => "The private companion profile could not be opened for writing. Check access to the plugin configuration folder and available disk space; preserve its saved sign-in data.",
        "bridge.start" => "The companion bridge could not start. Reinstall the complete CompAI folder and check whether Windows security software blocked it.",
        "bridge.protocol" => "The companion bridge failed or returned an invalid response. Reinstall the complete CompAI folder if this repeats; no automatic retry was sent.",
        _ => "The plugin installation could not be opened. Reinstall the complete CompAI folder and reload CompAI.dll.",
    });
}

internal sealed class CompanionSetupException(string stage, string remedy) : Exception(remedy)
{
    internal string Stage { get; } = stage;
    internal string SafeMessage => $"Companion setup [{this.Stage}]: {this.Message}";
}
