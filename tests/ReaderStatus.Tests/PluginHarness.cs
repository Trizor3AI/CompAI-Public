using System.Text.Json;

namespace CompAI;

// Managed status metadata only. The production status builder is linked unchanged; this fixture
// intentionally has no game services, native readers, filesystem stores or network connection.
public sealed partial class Plugin
{
    private readonly object stateGate = new();
    private ulong activeContentId = 123;
    private int contextLoadEpoch = 1;
    private int currencyAreaEpoch = 1;
    private int valuationAccessEpoch = 1;
    private bool contextDisposed = false;
    internal CompanionAccessSettings Access { get; set; } = new();
    private bool CanUseSavedProgressionUnderLock => this.SavedOwner;
    private bool CanReadCurrentCharacter => this.Ready;
    internal bool Ready { get; set; } = true;
    internal bool SavedOwner { get; set; } = true;
    internal object? snapshot = null;
    internal QuestHarness quests = new();
    internal readonly List<CategoryHarness> observedStorage = new();
    internal readonly List<CategoryHarness> observedProgression = new();
    internal readonly HashSet<string> Recent = new(StringComparer.Ordinal);
    internal readonly HashSet<string> SavedMenus = new(StringComparer.Ordinal);
    private bool HasRecentReaderUnderLock(string category) => this.Recent.Contains(category);
    private bool HasRecentActivityUnderLock(string category) => this.Recent.Contains(category);
    private bool HasSavedMenuUnderLock(string category) => this.SavedMenus.Contains(category);

    internal void Observe(string key, bool available)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { category = key }));
        this.TrackReader(key, _ => JsonSerializer.Serialize(new
        {
            available,
            coverage = "Synthetic observed positive entry.",
            unavailable_reason = "Synthetic menu unavailable.",
        }))!(document.RootElement);
    }

    internal void ChangeIdentity() => this.activeContentId++;
    internal void ChangeSession() => this.contextLoadEpoch++;
    internal void ChangeArea() => this.currencyAreaEpoch++;
}

internal sealed class QuestHarness { internal QuestViewHarness View { get; } = new(); }
internal sealed class QuestViewHarness { internal object? Snapshot { get; set; } }
internal sealed record CategoryHarness(string Category, bool Usable = true);
internal static class ProgressionMenuCache
{
    internal static bool IsUsable(CategoryHarness value, DateTimeOffset _) => value.Usable;
}
