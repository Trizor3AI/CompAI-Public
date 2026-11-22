namespace CompAI;

// Compile production polling/freezing/clearing with synthetic game services only.
internal sealed class GameContextReader
{
    internal readonly List<(bool Character, bool Dialogue, bool Location)> Calls = [];
    internal required GameContextReading Reading { get; set; }
    internal int DialogueCacheClears { get; private set; }
    internal void ClearDialogueCache() => this.DialogueCacheClears++;
    internal GameContextReading Capture(bool character, bool dialogue, bool location = false)
    {
        this.Calls.Add((character, dialogue, location));
        return this.Reading with { CapturedAtUtc = DateTimeOffset.UtcNow };
    }
}

public sealed partial class Plugin
{
    private readonly object stateGate = new();
    private ulong activeContentId = 101;
    private bool CanReadCurrentCharacter { get; set; } = true;
    internal CompanionAccessSettings Access { get; private set; }
    internal static ulong LoadedId { get; set; } = 101;
    internal int MismatchCount { get; private set; }
    internal Plugin(GameContextReader reader, CompanionAccessSettings access)
    {
        this.gameContextReader = reader;
        this.Access = access;
        LoadedId = 101;
    }

    private static ulong ReadLoadedCharacterId() => LoadedId;
    private void OnDetectedCharacterMismatch()
    {
        this.MismatchCount++;
        this.ClearGameContextUnderLock();
    }

    internal void Poll() => this.PollGameContext();
    internal void MakePollDue()
    {
        this.nextCharacterContextCheck = this.nextLocationContextCheck = this.nextNpcDialogueCheck = default;
    }
    internal void AreaChanged() => this.OnGameContextAreaChangedUnderLock();
    internal void SetReady(bool ready) => this.CanReadCurrentCharacter = ready;
    internal GameContextLookup? Freeze() => this.FreezeGameContextUnderLock();
    internal NpcConversationSnapshot DialogueHistory() => this.npcConversation.CaptureSnapshot(DateTimeOffset.UtcNow);
    internal void ChangeAccess(CompanionAccessSettings next)
    {
        this.ApplyGameContextAccessChangeUnderLock(this.Access, next);
        this.Access = next;
    }
    internal void EndSession() => this.ClearGameContextUnderLock();
}
