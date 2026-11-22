using System.Text.Json;

namespace CompAI;

public sealed partial class Plugin
{
    private static SelectedMacroObservation MacroObservation(DateTimeOffset time, int set = 0, int index = 0)
    {
        var lines = Enumerable.Repeat("", 15).ToArray();
        lines[0] = "/echo Cafe\u0301 and café";
        lines[2] = "/echo Αλφα \"quotes\"";
        lines[3] = "\t/echo preserve tab and trailing spaces  ";
        lines[14] = "/echo final <wait.1>";
        return new(time, set, index, "Synthetic macro", Array.AsReadOnly(lines));
    }

    internal static void ProduceMacroFixtures(string path)
    {
        var time = DateTimeOffset.UtcNow;
        var observation = MacroObservation(time);
        var snapshot = CaptureSelectedMacro(observation);
        var empty = CaptureSelectedMacro(observation with { Name = "", Lines = Array.AsReadOnly(Enumerable.Repeat("", 15).ToArray()) });
        Check(snapshot.Entries.Count == 16 && snapshot.Entries[2].Text == "" && snapshot.Entries[15].Text == "/echo final <wait.1>" &&
            snapshot.Entries[0].Slot == 1 && empty.Entries.All(row => row.Text == ""), "Exact positions, empty title and empty lines preserved");
        foreach (var invalid in new[] { "\u0002payload", "line\nline", "\r", "\u0085", "\ud800", new string('é', 257) })
            Throws<ArgumentException>(() => new ActivitySnapshot("selected_macro", time, true,
                snapshot.Entries.Select(row => row.Id == 2 ? row with { Text = invalid } : row)));
        Throws<ArgumentException>(() => new ActivitySnapshot("selected_macro", time, true, snapshot.Entries.Skip(1)));
        Throws<ArgumentException>(() => new ActivitySnapshot("selected_macro", time, true,
            snapshot.Entries.Select(row => row.Id == 2 ? row with { Group = "shared" } : row)));
        Check(!CaptureSelectedMacro(observation with { Index = 100 }).Available, "Invalid selected slot remains unknown");

        var cache = new ActivityObservationCache();
        cache.Observe(CaptureSelectedMacro(observation with { CapturedAtUtc = time.AddSeconds(-2) }), replaceSelection: true);
        cache.Observe(CaptureSelectedMacro(observation with { CapturedAtUtc = time.AddSeconds(-1) }), replaceSelection: true);
        Check(cache.Freeze("selected_macro", time).Available, "Stable selection retained");
        cache.Observe(ActivitySnapshot.Unavailable("selected_macro", "Closed"));
        Check(cache.Freeze("selected_macro", time).Available, "Closed menu retains last read");
        var editedLines = observation.Lines.ToArray();
        editedLines[0] = "/echo edited";
        cache.Observe(CaptureSelectedMacro(observation with { Lines = Array.AsReadOnly(editedLines) }), replaceSelection: true);
        Check(!cache.Freeze("selected_macro", time).Available, "Same-slot edit invalidates previous text before settlement");
        cache.Observe(CaptureSelectedMacro(observation with { CapturedAtUtc = time.AddSeconds(1), Lines = Array.AsReadOnly(editedLines) }), replaceSelection: true);
        Check(cache.Freeze("selected_macro", time.AddSeconds(1)).Entries[1].Text == "/echo edited", "New text settles");
        cache.Observe(ActivitySnapshot.Unavailable("selected_macro", "Unreadable selected macro"), replaceSelection: true);
        Check(!cache.Freeze("selected_macro", time.AddSeconds(1)).Available, "Unreadable visible selection never falls back");

        var cases = new Dictionary<string, object>();
        void Add(string name, ActivitySnapshot value, string json = "{\"category\":\"selected_macro\"}")
        {
            var arguments = JsonDocument.Parse(json).RootElement.Clone();
            cases[name] = new { arguments, result = new ActivityLookup(value.Available ? value.AsLastObserved() : value).Query(arguments) };
        }
        Add("individual", snapshot);
        Add("shared", CaptureSelectedMacro(MacroObservation(time, 1, 99)));
        Add("empty", empty);
        Add("unavailable", CaptureSelectedMacro(null));
        Add("page", snapshot, "{\"category\":\"selected_macro\",\"limit\":3}");
        Add("query", snapshot, "{\"category\":\"selected_macro\",\"query\":\"final\"}");
        Add("ids", snapshot, "{\"category\":\"selected_macro\",\"ids\":[1,16]}");
        Add("wrong_set", snapshot, "{\"category\":\"selected_macro\",\"group\":\"shared\"}");
        File.WriteAllText(path, JsonSerializer.Serialize(cases));
        Console.WriteLine("PASS selected macro projection, Unicode/blank bounds, selection invalidation and bridge producer");
    }
}
