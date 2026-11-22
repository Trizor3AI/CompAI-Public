using CompAI;
using System.Reflection;

internal static class ConnectionStorageChecks
{
    internal static async Task Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CompAIStorageConnection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var chats = Path.Combine(directory, "chats");
            using (var connection = new CompanionConnection(configurationDirectory: directory))
            {
                var path = Path.Combine(chats, connection.View.ActiveChatId + ".json");
                var before = File.ReadAllBytes(path);
                connection.Draft = "timed draft";
                Check(File.ReadAllBytes(path).SequenceEqual(before), "editing does not immediately write a transcript");
                await Task.Delay(1100);
                Check(new ChatStore(chats).ActiveDraft == "timed draft", "debounce persists even without reading View or opening a window");
                connection.Draft = "window close";
                connection.FlushDraft();
                Check(new ChatStore(chats).ActiveDraft == "window close", "window close flushes immediately");
                connection.Draft = "unload flush";
            }
            Check(new ChatStore(chats).ActiveDraft == "unload flush", "Dispose persists edits before timer expiry");

            var seed = new ChatStore(chats);
            for (var i = 0; i < ChatHistory.MaxStoredTurns; i++) seed.ActiveHistory.Add("q" + i, "answer", [], null, false);
            Check(seed.SaveActive(), "seed the exact turn cap");
            using var blocked = new CompanionConnection(configurationDirectory: directory);
            typeof(CompanionConnection).GetField("canTest", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(blocked, true);
            Check(blocked.StorageInfo.SendBlockReason is not null && !blocked.StartAsk("{}", false, "overflow"),
                "connection refuses a capacity-blocked request before starting any companion process");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
