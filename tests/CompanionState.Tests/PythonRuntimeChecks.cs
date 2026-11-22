using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CompAI;

internal static class PythonRuntimeChecks
{
    internal static async Task ActualBundle()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var source = Path.Combine(repository, "bridge", "runtime", "python-win-x64");
        Check(Directory.Exists(source), "actual embedded Python fixture requires the pinned runtime payload");
        var root = Path.Combine(Path.GetTempPath(), "CompAIPythonRelocation-" + Guid.NewGuid().ToString("N"));
        var oldHome = Environment.GetEnvironmentVariable("PYTHONHOME");
        var oldPath = Environment.GetEnvironmentVariable("PYTHONPATH");
        try
        {
            var directory = Path.Combine(root, "python");
            Directory.CreateDirectory(directory);
            foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
            File.Copy(Path.Combine(repository, "bridge", "python-runtime.json"), Path.Combine(root, "python-runtime.json"));
            var foreign = Path.Combine(root, "foreign");
            Directory.CreateDirectory(foreign);
            File.WriteAllText(Path.Combine(foreign, "json.py"), "raise RuntimeError('foreign environment was imported')");
            Environment.SetEnvironmentVariable("PYTHONHOME", Path.Combine(foreign, "not-an-installation"));
            Environment.SetEnvironmentVariable("PYTHONPATH", foreign);
            var executable = await CompanionPythonRuntime.PrepareAsync(root, default);
            Check(executable == Path.Combine(directory, "python.exe") &&
                !Directory.EnumerateDirectories(root, "__pycache__", SearchOption.AllDirectories).Any(),
                "relocated bundled interpreter ignores foreign environment and writes no bytecode");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PYTHONHOME", oldHome);
            Environment.SetEnvironmentVariable("PYTHONPATH", oldPath);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    internal static async Task Run()
    {
        using (var bundle = new Bundle())
        {
            Check(await CompanionPythonRuntime.ValidateBundleAsync(bundle.Root, default) == Path.Combine(bundle.Root, "python", "python.exe"),
                "valid fixture resolves only the adjacent executable");
            var original = bundle.Manifest.DeepClone();
            foreach (var mutate in new Action<JsonObject>[] {
                json => json["files"]!["../escape.exe"] = new string('a', 64),
                json => json["files"]!["C:\\private-user\\python.exe"] = new string('a', 64),
                json => json["files"]!["PYTHON.exe"] = new string('a', 64),
                json => json["files"]!["python.exe"] = "not-a-digest",
                json => json["files"]!.AsObject().Remove("python314.dll"),
                json => json["version"] = "3.14.7",
                json => json["arch"] = "win-arm64",
                json => json["executable"] = "pythonw.exe",
            })
            {
                bundle.Manifest = (JsonObject)original.DeepClone();
                mutate(bundle.Manifest);
                bundle.Save();
                await Rejected(() => CompanionPythonRuntime.ValidateBundleAsync(bundle.Root, default), "python.integrity", bundle.Root);
            }
            bundle.Manifest = (JsonObject)original.DeepClone();
            bundle.Save();
            var unexpected = Path.Combine(bundle.Root, "python", "python._pth");
            File.WriteAllText(unexpected, "untrusted-folder");
            await Rejected(() => CompanionPythonRuntime.ValidateBundleAsync(bundle.Root, default), "python.integrity", bundle.Root);
            File.Delete(unexpected);
            var executable = Path.Combine(bundle.Root, "python", "python.exe");
            File.AppendAllText(executable, "changed");
            await Rejected(() => CompanionPythonRuntime.ValidateBundleAsync(bundle.Root, default), "python.integrity", bundle.Root);
            File.Delete(Path.Combine(bundle.Root, "python-runtime.json"));
            await Rejected(() => CompanionPythonRuntime.ValidateBundleAsync(bundle.Root, default), "python.missing", bundle.Root);
        }

        var start = CompanionPythonRuntime.CreateStartInfo("C:\\trusted-fixture\\python.exe", "C:\\trusted-fixture");
        Check(start.FileName == "C:\\trusted-fixture\\python.exe" &&
            start.ArgumentList.SequenceEqual(new[] { "-I", "-S", "-B", "-X", "utf8", "-u" }) &&
            !start.UseShellExecute && start.CreateNoWindow && start.RedirectStandardError,
            "launch keeps the pinned executable and isolated, no-site, no-bytecode, UTF8 flags");
        await Rejected(() => CompanionPythonRuntime.ProbeAsync("C:\\missing-private-user\\python.exe", default),
            "python.start", "missing-private-user");

        // Test process lifecycle with an explicitly supplied synthetic command.
        // No provider, credentials, real question, or game data participates.
        await CompanionPythonRuntime.ProbeAsync("python", default, fixtureProbe: "print('COMPAI_PYTHON_OK|3.14.8')");
        await Rejected(() => CompanionPythonRuntime.ProbeAsync("python", default,
            fixtureProbe: "import sys; print('private-path', file=sys.stderr); sys.exit(1)"), "python.prerequisite", "private-path");
        await Rejected(() => CompanionPythonRuntime.ProbeAsync("python", default,
            fixtureProbe: "print('x' * 5000)"), "python.prerequisite", "private-path");
        var clock = Stopwatch.StartNew();
        await Rejected(() => CompanionPythonRuntime.ProbeAsync("python", default, TimeSpan.FromMilliseconds(200),
            "import time; time.sleep(60)"), "python.timeout", "private-path");
        Check(clock.Elapsed < TimeSpan.FromSeconds(3), "probe timeout is bounded");
        using (var cancellation = new CancellationTokenSource(200))
        {
            try
            {
                await CompanionPythonRuntime.ProbeAsync("python", cancellation.Token,
                    fixtureProbe: "import time; time.sleep(60)");
                throw new Exception("cancelled probe was accepted");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }

        using (var fixture = new Fixture())
        using (var connection = new CompanionConnection())
        {
            connection.Start("status");
            await Idle(connection);
            Check(connection.View.State.Contains("[python.missing]") && !connection.View.CanTest &&
                !connection.View.State.Contains(Path.GetTempPath()) && !File.Exists(fixture.RequestFile),
                "missing runtime explains clean install, hides paths and starts no bridge");
        }
        using (var fixture = new Fixture())
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var connection = new CompanionConnection(preparePython: async (_, token) =>
            {
                entered.SetResult();
                await Task.Delay(60000, token);
                return "python";
            });
            connection.Start("status");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            connection.Cancel();
            await Idle(connection);
            Check(connection.View.PythonPrerequisite.Contains("cancelled") && !File.Exists(fixture.RequestFile),
                "user cancellation interrupts prerequisite check before bridge startup");
        }
        using (var fixture = new Fixture())
        {
            var profile = Path.Combine(Path.GetDirectoryName(Plugin.PluginInterface.AssemblyLocation.FullName)!, "profile-file");
            File.WriteAllText(profile, "synthetic profile obstacle");
            using var connection = new CompanionConnection(profileDirectory: profile, preparePython: Fixture.PreparePython);
            connection.Start("status");
            await Idle(connection);
            Check(connection.View.State.Contains("[profile.storage]") && !connection.View.State.Contains(profile) &&
                connection.View.PythonPrerequisite == CompanionPythonRuntime.Ready,
                "profile failure is distinguished from a passed Python prerequisite");
        }
        using (var fixture = new Fixture())
        using (var connection = new CompanionConnection(preparePython: Fixture.PreparePython))
        {
            var apply = typeof(CompanionConnection).GetMethod("ApplyMessage",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            apply.Invoke(connection, new object[] { "{\"error\":\"Safe fixture remedy\",\"error_kind\":\"provider\",\"error_stage\":\"runtime.integrity\"}" });
            Check(connection.View.State.Contains("[runtime.integrity] Safe fixture remedy"), "known provider stages render their safe remedy");
            foreach (var stage in new[] { "123", "\"C:\\\\private-user\\\\secret\"" })
            {
                try
                {
                    apply.Invoke(connection, new object[] { "{\"error\":\"Safe fixture remedy\",\"error_kind\":\"provider\",\"error_stage\":" + stage + "}" });
                    throw new Exception("unrecognized stage was accepted");
                }
                catch (System.Reflection.TargetInvocationException exception) when (exception.InnerException is InvalidDataException) { }
                Check(!connection.View.State.Contains("private-user"), "invalid provider stage never appears in user diagnostics");
            }
        }
    }

    private static async Task Rejected(Func<Task> operation, string stage, string secret)
    {
        try { await operation(); throw new Exception("invalid prerequisite was accepted: " + stage); }
        catch (CompanionSetupException exception)
        {
            Check(exception.Stage == stage && exception.SafeMessage.Contains("[" + stage + "]") &&
                !exception.SafeMessage.Contains(secret), "failure uses a fixed stage and sanitized remedy");
        }
    }

    private static async Task Idle(CompanionConnection connection)
    {
        var clock = Stopwatch.StartNew();
        while (connection.View.Busy)
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(3)) throw new Exception("startup did not stop within its bound");
            await Task.Delay(10);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class Bundle : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "CompAIPythonFixture-" + Guid.NewGuid().ToString("N"));
        internal JsonObject Manifest { get; set; }
        internal Bundle()
        {
            var directory = Path.Combine(this.Root, "python");
            Directory.CreateDirectory(directory);
            var files = new JsonObject();
            foreach (var name in new[] { "python.exe", "python3.dll", "python314.dll", "python314.zip", "python314._pth" })
            {
                var path = Path.Combine(directory, name);
                File.WriteAllText(path, name.EndsWith("._pth") ? "python314.zip\n.\n..\n" : "synthetic " + name);
                files[name] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            }
            this.Manifest = new JsonObject { ["schema"] = 1, ["version"] = "3.14.8", ["arch"] = "win-x64",
                ["executable"] = "python.exe", ["files"] = files };
            this.Save();
        }
        internal void Save() => File.WriteAllText(Path.Combine(this.Root, "python-runtime.json"), this.Manifest.ToJsonString());
        public void Dispose() => Directory.Delete(this.Root, recursive: true);
    }
}
