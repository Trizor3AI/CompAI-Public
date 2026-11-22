using CompAI;

internal static class AccessSaveRetryChecks
{
    private const int UnableToRemoveReplacedFile = unchecked((int)0x80070497);

    internal static void TransientReplacement()
    {
        foreach (var failureCount in new[] { 1, 2 })
        foreach (var granting in new[] { false, true })
        {
            using var folder = new TestFolder();
            var previous = new CompanionAccessSettings(Inventory: !granting, WebSearch: !granting,
                IslandProgress: !granting, CosmicResearch: granting, IslandMaterials: !granting, CosmicMissions: granting);
            var requested = new CompanionAccessSettings(Inventory: granting, WebSearch: granting,
                IslandProgress: granting, CosmicResearch: !granting, IslandMaterials: granting, CosmicMissions: !granting);
            Seed(folder, previous);
            var previousBytes = File.ReadAllBytes(folder.File);
            var attempts = 0;
            var delays = new List<int>();
            var nativeFailures = new List<int>();
            string? originalTemporary = null;
            byte[]? replacementBytes = null;
            CompanionAccessSettingsStore? store = null;
            store = new CompanionAccessSettingsStore(folder.File, (temporary, destination) =>
            {
                attempts++;
                originalTemporary ??= temporary;
                replacementBytes ??= File.ReadAllBytes(temporary);
                Check(temporary == originalTemporary && destination == folder.File,
                    "every retry must use the original source and destination paths");
                Check(previousBytes.SequenceEqual(File.ReadAllBytes(destination)),
                    "old durable choices must remain intact before successful replacement");
                Check(replacementBytes.SequenceEqual(File.ReadAllBytes(temporary)),
                    "retry must keep the same fully written replacement bytes");
                Check(new CompanionAccessSettingsStore(temporary).Settings == requested,
                    "temporary file must contain the requested settings");
                Check(store!.Settings == previous, "requested choices must not take effect before replacement succeeds");
                if (attempts <= failureCount) throw Failure(UnableToRemoveReplacedFile);
                try { File.Replace(temporary, destination, null); }
                catch (IOException ex)
                {
                    nativeFailures.Add(ex.HResult);
                    throw;
                }
            }, milliseconds => delays.Add(milliseconds));

            Check(store.Save(requested),
                $"bounded retry did not recover after {attempts} attempts, {failureCount} injected failures " +
                $"and native errors [{string.Join(", ", nativeFailures.Select(value => $"0x{value:X8}"))}]: {store.Error}");
            Check(attempts == failureCount + nativeFailures.Count + 1 && attempts <= 3 &&
                nativeFailures.All(value => value == UnableToRemoveReplacedFile) &&
                delays.SequenceEqual(Enumerable.Repeat(10, attempts - 1)),
                $"retry must stop at first success within three attempts; actual attempts={attempts}, delays={string.Join(",", delays)}");
            if (nativeFailures.Count > 0)
                Console.WriteLine($"INFO replacement retry also recovered {nativeFailures.Count} real native 1175 failure.");
            Check(store.Settings == requested && store.Error is null, "successful replacement must publish requested access");
            Check(new CompanionAccessSettingsStore(folder.File).Settings == requested, "recovered save must survive reload");
            CheckNoTemporaryFiles(folder);
        }
    }

    internal static void ExhaustionAndExplicitRecovery()
    {
        using var folder = new TestFolder();
        var previous = new CompanionAccessSettings(Inventory: true, WebSearch: true);
        Seed(folder, previous);
        var previousBytes = File.ReadAllBytes(folder.File);
        var attempts = 0;
        var delays = new List<int>();
        var fail = true;
        var store = new CompanionAccessSettingsStore(folder.File, (temporary, destination) =>
        {
            attempts++;
            if (fail) throw Failure(UnableToRemoveReplacedFile);
            File.Replace(temporary, destination, null);
        }, milliseconds => delays.Add(milliseconds));
        var plugin = new Plugin(store);
        plugin.SetBackgroundQuestion(true);
        plugin.SetAccess(false, false, false);
        Check(attempts == 3 && delays.SequenceEqual(new[] { 10, 10 }),
            "persistent 1175 must stop after three attempts and 20 ms deliberate waiting");
        Check(store.Settings == new CompanionAccessSettings() && store.Error is not null,
            "exhausted replacement must fail closed");
        Check(plugin.CancelCount == 1 && !plugin.AnyPendingQuestion,
            "failed revocation must cancel both active and background requests");
        Check(previousBytes.SequenceEqual(File.ReadAllBytes(folder.File)),
            "retry exhaustion must preserve exact prior durable bytes");
        Check(new CompanionAccessSettingsStore(folder.File).Settings == previous,
            "the preserved prior file must still be readable");
        CheckNoTemporaryFiles(folder);

        fail = false;
        Check(store.Save(new()), $"later explicit save must remain available: {store.Error}");
        Check(attempts == 4 && delays.Count == 2 && store.Error is null,
            "explicit successful save must not inherit the exhausted retry state");
        Check(new CompanionAccessSettingsStore(folder.File).Settings == new CompanionAccessSettings(),
            "later explicit revocation must survive reload");
        CheckNoTemporaryFiles(folder);
    }

    internal static void OtherErrorsAreNotRetried()
    {
        var failures = new Exception[]
        {
            Failure(unchecked((int)0x80070498)), // 1176: original can be gone.
            Failure(unchecked((int)0x80070499)), // 1177: original can have moved.
            Failure(unchecked((int)0x80070020)), // Sharing violation is outside this recovery.
            Failure(unchecked((int)0x80070021)), // Lock violation is outside this recovery.
            Failure(unchecked((int)0x80070005)), // Access denied.
            Failure(1175), // A bare error number is not the documented HRESULT.
            Failure(unchecked((int)0x80130497)), // Matching low bits alone are insufficient.
            new IOException("Synthetic unrelated storage failure."),
            new UnauthorizedAccessException("Synthetic denied replacement."),
        };
        foreach (var failure in failures)
        {
            using var folder = new TestFolder();
            var previous = new CompanionAccessSettings(Inventory: true);
            Seed(folder, previous);
            var previousBytes = File.ReadAllBytes(folder.File);
            var attempts = 0;
            var delays = 0;
            var store = new CompanionAccessSettingsStore(folder.File, (_, _) =>
            {
                attempts++;
                throw failure;
            }, _ => delays++);
            Check(!store.Save(new(WebSearch: true)), $"unrelated error 0x{failure.HResult:X8} must fail");
            Check(attempts == 1 && delays == 0, $"unrelated error 0x{failure.HResult:X8} must not retry");
            Check(store.Settings == new CompanionAccessSettings() && store.Error is not null, "failed save must disable access");
            Check(previousBytes.SequenceEqual(File.ReadAllBytes(folder.File)), "failed save must preserve previous bytes");
            CheckNoTemporaryFiles(folder);
        }
    }

    internal static void MissingInputsAreNotRetried()
    {
        foreach (var missingSource in new[] { false, true })
        foreach (var removeDuringDelay in new[] { false, true })
        {
            using var folder = new TestFolder();
            Seed(folder, new(Inventory: true));
            var previousBytes = File.ReadAllBytes(folder.File);
            var preservedDestination = Path.Combine(folder.Path, "preserved.json");
            var attempts = 0;
            var delays = 0;
            string? replacementSource = null;
            void RemoveInput()
            {
                if (missingSource) File.Delete(replacementSource!);
                else File.Move(folder.File, preservedDestination);
            }
            var store = new CompanionAccessSettingsStore(folder.File, (temporary, destination) =>
            {
                attempts++;
                replacementSource = temporary;
                if (!removeDuringDelay) RemoveInput();
                throw Failure(UnableToRemoveReplacedFile);
            }, _ =>
            {
                delays++;
                if (removeDuringDelay) RemoveInput();
            });
            Check(!store.Save(new(WebSearch: true)), "missing replacement input must fail closed");
            Check(attempts == 1 && delays == (removeDuringDelay ? 1 : 0),
                "a source or destination removed before or during the retry wait must prevent another replacement attempt");
            Check(store.Settings == new CompanionAccessSettings() && store.Error is not null, "missing input must disable access");
            Check(previousBytes.SequenceEqual(File.ReadAllBytes(missingSource ? folder.File : preservedDestination)),
                "save must not change the surviving old file");
            CheckNoTemporaryFiles(folder);
        }
    }

    internal static void ReadOnlyTargetIsNotRetried()
    {
        using var folder = new TestFolder();
        Seed(folder, new(Inventory: true));
        var previousBytes = File.ReadAllBytes(folder.File);
        var attempts = 0;
        var delays = 0;
        var store = new CompanionAccessSettingsStore(folder.File, (temporary, destination) =>
        {
            attempts++;
            File.Replace(temporary, destination, null);
        }, _ => delays++);
        File.SetAttributes(folder.File, FileAttributes.ReadOnly);
        try
        {
            Check(!store.Save(new(WebSearch: true)), "read-only target must reject replacement");
            Check(attempts == 1 && delays == 0, "read-only denial must not be retried");
            Check(store.Settings == new CompanionAccessSettings() && store.Error is not null, "read-only target must fail closed");
            Check(previousBytes.SequenceEqual(File.ReadAllBytes(folder.File)), "read-only rejection must preserve prior bytes");
            CheckNoTemporaryFiles(folder);
        }
        finally { File.SetAttributes(folder.File, FileAttributes.Normal); }
    }

    private static IOException Failure(int hresult) => new("Synthetic replacement failure.", hresult);

    private static void Seed(TestFolder folder, CompanionAccessSettings previous)
    {
        var store = new CompanionAccessSettingsStore(folder.File);
        Check(store.Save(previous), $"initial synthetic settings write failed: {store.Error}");
    }

    private static void CheckNoTemporaryFiles(TestFolder folder) =>
        Check(Directory.GetFiles(folder.Path, "*.tmp").Length == 0, "save must clean its temporary file");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
