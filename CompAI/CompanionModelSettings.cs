using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CompAI;

internal sealed record CompanionModelOption(string Id, string Name, IReadOnlyList<string> Efforts, string DefaultEffort,
    bool SupportsImages = false, string Provider = "codex");

internal sealed record CompanionModelSettings(string Model = "gpt-6-sol", string Effort = "medium", string Provider = "codex");

internal static class CompanionModelSelection
{
    internal static CompanionModelOption? Find(IReadOnlyList<CompanionModelOption> options, string model, string provider = "codex") =>
        options.FirstOrDefault(option => string.Equals(option.Id, model, StringComparison.Ordinal) &&
            string.Equals(option.Provider, provider, StringComparison.Ordinal));

    internal static CompanionModelOption? Find(IReadOnlyList<CompanionModelOption> options, CompanionModelSettings value) =>
        Find(options, value.Model, value.Provider);

    internal static bool IsSupported(IReadOnlyList<CompanionModelOption> options, CompanionModelSettings value) =>
        IsValid(value) && Find(options, value)?.Efforts.Contains(value.Effort, StringComparer.Ordinal) == true;

    // Called only after an explicit model choice. Preserve the user's effort when
    // supported; otherwise selecting the model also chooses its advertised default.
    internal static CompanionModelSettings ForModel(CompanionModelOption option, string currentEffort) =>
        new(option.Id, option.Efforts.Contains(currentEffort, StringComparer.Ordinal) ? currentEffort : option.DefaultEffort,
            option.Provider);

    // Provider selection is explicit; discovery and status refreshes never call this.
    // Auto is a provider-owned routing choice, not a fallback to another provider.
    internal static CompanionModelSettings ForProvider(string provider) => provider switch
    {
        "codex" => new(),
        "copilot" or "grok" => new("auto", "none", provider),
        _ => throw new ArgumentException("Unsupported provider.", nameof(provider)),
    };

    internal static bool IsValid(CompanionModelSettings value) =>
        IsProvider(value.Provider) && IsIdentifier(value.Model, 128, "-_.:/", false) &&
        IsIdentifier(value.Effort, 32, "-_", true);

    internal static bool IsProvider(string? value) => value is "codex" or "copilot" or "grok";

    private static bool IsIdentifier(string? value, int max, string punctuation, bool lowercaseOnly) =>
        !string.IsNullOrEmpty(value) && value.Length <= max && char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character => (char.IsAsciiLetterOrDigit(character) || punctuation.Contains(character)) &&
            (!lowercaseOnly || !char.IsAsciiLetterUpper(character)));
}

internal sealed class CompanionModelSettingsStore
{
    private const int MaxFileBytes = 4096;
    private readonly object gate = new();
    private readonly string path;
    private CompanionModelSettings settings = new();
    private string? error;
    private bool isLoaded;

    internal CompanionModelSettings Settings { get { lock (this.gate) return this.settings; } }
    internal string? Error { get { lock (this.gate) return this.error; } }
    // A bad file must not silently select the default model. A failed later save
    // retains the last valid selection; Error explains that the change was not saved.
    internal bool IsLoaded { get { lock (this.gate) return this.isLoaded; } }

    internal CompanionModelSettingsStore(string path)
    {
        this.path = string.Empty;
        try
        {
            this.path = Path.GetFullPath(path);
            this.settings = this.Load();
            this.isLoaded = true;
        }
        catch (FileNotFoundException) { this.isLoaded = true; }
        catch (DirectoryNotFoundException) { this.isLoaded = true; }
        catch (Exception ex) when (IsStorageError(ex))
        {
            this.error = $"Model settings could not be loaded ({ex.GetType().Name}). Select a model and reasoning level to save valid settings before sending.";
        }
    }

    internal bool Save(CompanionModelSettings value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (this.gate)
        {
            string? temporaryPath = null;
            try
            {
                if (!CompanionModelSelection.IsValid(value)) throw new InvalidDataException("Invalid model selection.");
                if (this.path.Length == 0) throw new IOException("Invalid settings path.");
                var directory = Path.GetDirectoryName(this.path)!;
                Directory.CreateDirectory(directory);
                RejectLink(this.path);
                temporaryPath = Path.Combine(directory, $".{Path.GetFileName(this.path)}.{Guid.NewGuid():N}.tmp");
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new
                    { schemaVersion = 2, model = value.Model, effort = value.Effort, provider = value.Provider });
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                if (File.Exists(this.path)) File.Replace(temporaryPath, this.path, null);
                else File.Move(temporaryPath, this.path);
                this.settings = value;
                this.isLoaded = true;
                this.error = null;
                return true;
            }
            catch (Exception ex) when (IsStorageError(ex))
            {
                this.error = $"Model settings were not saved ({ex.GetType().Name}). The previous selection remains in effect.";
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

    internal bool SelectProvider(string provider)
    {
        lock (this.gate)
        {
            if (!CompanionModelSelection.IsProvider(provider)) return false;
            // Reselecting the current provider must keep a manually chosen default.
            // An invalid settings file still requires an explicit valid model save.
            if (this.settings.Provider == provider) return this.isLoaded;
            return this.Save(CompanionModelSelection.ForProvider(provider));
        }
    }

    private CompanionModelSettings Load()
    {
        RejectLink(this.path);
        using var stream = new FileStream(this.path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxFileBytes) throw new InvalidDataException("Oversized model settings.");
        var bytes = new byte[MaxFileBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = stream.Read(bytes, count, bytes.Length - count);
            if (read == 0) break;
            count += read;
        }
        if (count > MaxFileBytes) throw new InvalidDataException("Oversized model settings.");
        using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 4 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected settings object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var version = 0;
        string? model = null;
        string? effort = null;
        string? provider = null;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new InvalidDataException("Duplicate model setting.");
            switch (property.Name)
            {
                case "schemaVersion":
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out version) || version is not (1 or 2))
                        throw new InvalidDataException("Unsupported settings version.");
                    break;
                case "model": model = ReadString(property.Value); break;
                case "effort": effort = ReadString(property.Value); break;
                case "provider": provider = ReadString(property.Value); break;
                default: throw new InvalidDataException("Unknown model setting.");
            }
        }
        if (model is null || effort is null ||
            !(version == 1 && seen.Count == 3 && provider is null || version == 2 && seen.Count == 4 && provider is not null))
            throw new InvalidDataException("Missing or unsupported model setting.");
        var value = new CompanionModelSettings(model, effort, provider ?? "codex");
        if (!CompanionModelSelection.IsValid(value)) throw new InvalidDataException("Invalid model setting.");
        return value;
    }

    private static string ReadString(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw new InvalidDataException("Expected string model setting.");

    private static void RejectLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked model settings are unsupported.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static bool IsStorageError(Exception ex) =>
        ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException;
}
