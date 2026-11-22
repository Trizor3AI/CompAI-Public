using System;

namespace CompAI;

public sealed partial class Plugin
{
    private readonly GameContextReader gameContextReader;
    private readonly RecentNpcDialogue recentNpcDialogue = new();
    private readonly NpcConversationStore npcConversation = new();
    private GameCharacterContext? currentCharacterContext;
    private GameLocationContext? currentLocationContext;
    private DateTimeOffset characterContextCheckedAt;
    private DateTimeOffset locationContextCheckedAt;
    private string? characterContextUnavailable;
    private string? locationContextUnavailable;
    private string? npcDialogueUnavailable;
    private DateTimeOffset nextCharacterContextCheck;
    private DateTimeOffset nextLocationContextCheck;
    private DateTimeOffset nextNpcDialogueCheck;

    // Called by Framework.Update only. A cheap deadline check avoids per-frame native reads.
    private void PollGameContext()
    {
        lock (this.stateGate)
        {
            var access = this.Access;
            var now = DateTimeOffset.UtcNow;
            this.recentNpcDialogue.Expire(now);
            this.npcConversation.Expire(now);
            if (!this.CanReadCurrentCharacter)
            {
                this.ClearCharacterContextUnderLock();
                this.ClearLocationContextUnderLock();
                this.recentNpcDialogue.Observe(null, now);
                return;
            }
            var character = access.CharacterContext && now >= this.nextCharacterContextCheck;
            var location = access.Location && now >= this.nextLocationContextCheck;
            var dialogue = access.NpcDialogue && now >= this.nextNpcDialogueCheck;
            if (!character && !dialogue && !location) return;
            var loadedId = ReadLoadedCharacterId();
            if (loadedId == 0)
            {
                // An area load can temporarily unload PlayerState. Only a confirmed different
                // nonzero identity is a character change; recent Talk remains historical here.
                this.ClearCharacterContextUnderLock();
                this.ClearLocationContextUnderLock();
                this.recentNpcDialogue.Observe(null, now);
                this.nextCharacterContextCheck = this.nextLocationContextCheck = this.nextNpcDialogueCheck = now.AddMilliseconds(250);
                return;
            }
            if (loadedId != this.activeContentId)
            {
                this.OnDetectedCharacterMismatch();
                return;
            }
            if (character) this.nextCharacterContextCheck = now.AddSeconds(1);
            if (location) this.nextLocationContextCheck = now.AddSeconds(1);
            if (dialogue) this.nextNpcDialogueCheck = now.AddMilliseconds(250);
            var reading = this.gameContextReader.Capture(character, dialogue, location);
            if (character)
            {
                this.currentCharacterContext = reading.Character;
                this.characterContextCheckedAt = reading.CapturedAtUtc;
                this.characterContextUnavailable = reading.CharacterUnavailable;
            }
            if (location)
            {
                this.currentLocationContext = reading.Location;
                this.locationContextCheckedAt = reading.CapturedAtUtc;
                this.locationContextUnavailable = reading.LocationUnavailable;
            }
            if (dialogue)
            {
                this.ObserveNpcReadingUnderLock(reading);
            }
        }
    }

    private void ObserveNpcReadingUnderLock(GameContextReading reading)
    {
        this.recentNpcDialogue.Observe(reading.Dialogue, reading.CapturedAtUtc);
        this.npcDialogueUnavailable = reading.DialogueUnavailable;
        // A transient failure must not split one page into duplicate observations.
        if (reading.Dialogue is not null || reading.DialogueUnavailable == "No visible Talk window.")
            this.npcConversation.Observe(reading.Dialogue, reading.CapturedAtUtc);
        else this.npcConversation.Expire(reading.CapturedAtUtc);
    }

    // Caller holds stateGate. Disabled categories never enter a request or its lookup closure.
    private GameContextLookup? FreezeGameContextUnderLock()
    {
        var access = this.Access;
        if (!access.CharacterContext && !access.NpcDialogue && !access.Location) return null;
        var now = DateTimeOffset.UtcNow;
        this.recentNpcDialogue.Expire(now);
        var ready = this.CanReadCurrentCharacter;
        var characterFresh = now >= this.characterContextCheckedAt &&
            now - this.characterContextCheckedAt < TimeSpan.FromSeconds(3);
        var locationFresh = now >= this.locationContextCheckedAt &&
            now - this.locationContextCheckedAt < TimeSpan.FromSeconds(3);
        return new GameContextLookup(now, access.CharacterContext, access.NpcDialogue,
            ready && characterFresh ? this.currentCharacterContext : null,
            ready ? this.recentNpcDialogue.Line : null, this.recentNpcDialogue.ObservedAt,
            this.recentNpcDialogue.Visible,
            !ready ? "Character is logged out or still loading." : !characterFresh
                ? "Character details are waiting for a fresh local reading." : this.characterContextUnavailable,
            !ready ? "Character is logged out or still loading." : this.npcDialogueUnavailable,
            access.Location, ready && locationFresh ? this.currentLocationContext : null,
            !ready ? "Character is logged out or still loading." : !locationFresh
                ? "Location is waiting for a fresh local reading." : this.locationContextUnavailable,
            this.recentNpcDialogue.FromPreviousArea);
    }

    private void ClearGameContextUnderLock()
    {
        this.ClearCharacterContextUnderLock();
        this.ClearLocationContextUnderLock();
        this.ClearNpcDialogueUnderLock();
    }

    private void ApplyGameContextAccessChangeUnderLock(CompanionAccessSettings previous, CompanionAccessSettings next)
    {
        if (previous.CharacterContext != next.CharacterContext) this.ClearCharacterContextUnderLock();
        if (previous.Location != next.Location) this.ClearLocationContextUnderLock();
        if (previous.NpcDialogue != next.NpcDialogue) this.ClearNpcDialogueUnderLock();
    }

    private void ClearCharacterContextUnderLock()
    {
        this.currentCharacterContext = null;
        this.characterContextCheckedAt = default;
        this.characterContextUnavailable = null;
        this.nextCharacterContextCheck = default;
    }

    private void ClearLocationContextUnderLock()
    {
        this.currentLocationContext = null;
        this.locationContextCheckedAt = default;
        this.locationContextUnavailable = null;
        this.nextLocationContextCheck = default;
    }

    private void ClearNpcDialogueUnderLock()
    {
        this.gameContextReader.ClearDialogueCache();
        this.recentNpcDialogue.Clear();
        this.npcConversation.Clear();
        this.npcDialogueUnavailable = null;
        this.nextNpcDialogueCheck = default;
    }

    private void OnGameContextAreaChangedUnderLock()
    {
        this.gameContextReader.ClearDialogueCache();
        this.ClearCharacterContextUnderLock();
        this.ClearLocationContextUnderLock();
        this.recentNpcDialogue.OnAreaChanged(DateTimeOffset.UtcNow);
        this.npcConversation.OnAreaChanged(DateTimeOffset.UtcNow);
        this.nextNpcDialogueCheck = default;
    }
}
