using System;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using NativePlayerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState;

namespace CompAI;

/// <summary>
/// Reads only the local character and the currently visible Talk addon. Capture must run
/// on the framework thread; the caller must discard results after logout, permission
/// changes or a session/capture epoch change. Native pointers never leave this reader.
/// This reader caches only its current bounded Talk decode; it does not keep history,
/// enumerate actors, read player chat, or operate UI.
/// </summary>
internal sealed unsafe class GameContextReader(IClientState clientState, IObjectTable objects,
    IDataManager data, IGameGui gui, IPlayerState playerState)
{
    internal const string CharacterSource =
        "Dalamud IPlayerState; PlayerState EXP requirement; local ClassJob sheet.";
    internal const string LocationSource = "Dalamud IClientState and IPlayerState; local TerritoryType and World sheets.";
    internal const string DialogueSource = "Currently visible Talk addon: speaker and current sentence.";
    internal const string DialogueCoverage =
        "Talk window only; no player chat, speech bubbles, choice lists, battle dialogue or separate cutscene subtitles. " +
        "The current sentence can include letters still being revealed by the game's text animation.";

    private const int MaxNameCharacters = 128;
    private const int MaxLabelCharacters = 128;
    private const int MaxAbbreviationCharacters = 32;
    private readonly NpcDialogueTextCache dialogueText = new();

    internal void ClearDialogueCache() => this.dialogueText.Clear();

    internal GameContextReading Capture(bool characterAllowed, bool dialogueAllowed, bool locationAllowed = false)
    {
        try
        {
            return this.CaptureCore(characterAllowed, dialogueAllowed, locationAllowed);
        }
        catch (Exception ex)
        {
            if (dialogueAllowed) this.dialogueText.Clear();
            // Readiness properties can fail during unload too. Never throw out of a framework callback.
            var reason = $"Game context read unavailable ({ex.GetType().Name}).";
            return new(DateTimeOffset.UtcNow, null, null,
                characterAllowed ? reason : "Character info access is off.",
                dialogueAllowed ? reason : "NPC dialogue access is off.", null,
                locationAllowed ? reason : "Location access is off.");
        }
    }

    private GameContextReading CaptureCore(bool characterAllowed, bool dialogueAllowed, bool locationAllowed)
    {
        var capturedAtUtc = DateTimeOffset.UtcNow;
        GameCharacterContext? character = null;
        NpcDialogueContext? dialogue = null;
        GameLocationContext? location = null;
        string? characterUnavailable = characterAllowed ? null : "Character info access is off.";
        string? dialogueUnavailable = dialogueAllowed ? null : "NPC dialogue access is off.";
        string? locationUnavailable = locationAllowed ? null : "Location access is off.";

        // Disabled categories perform no game reads, including readiness checks.
        if (!characterAllowed && !dialogueAllowed && !locationAllowed)
            return new(capturedAtUtc, null, null, characterUnavailable, dialogueUnavailable, null, locationUnavailable);

        if (!clientState.IsLoggedIn || !playerState.IsLoaded)
        {
            this.dialogueText.Clear();
            if (characterAllowed)
                characterUnavailable = "Character data is not loaded.";
            if (dialogueAllowed)
                dialogueUnavailable = "Character data is not loaded; NPC dialogue is unavailable.";
            if (locationAllowed)
                locationUnavailable = "Location data is not loaded.";
            return new(capturedAtUtc, null, null, characterUnavailable, dialogueUnavailable, null, locationUnavailable);
        }

        if (characterAllowed)
        {
            try
            {
                character = this.ReadCharacter();
            }
            catch (Exception ex)
            {
                // No exception contents, machine paths, pointers or arbitrary game text in status.
                characterUnavailable = $"Character info read unavailable ({ex.GetType().Name}).";
            }
        }

        if (locationAllowed)
        {
            try { location = this.ReadLocation(); }
            catch (Exception ex)
            {
                locationUnavailable = $"Location read unavailable ({ex.GetType().Name}).";
            }
        }

        if (dialogueAllowed)
        {
            try
            {
                dialogue = this.ReadDialogue(out dialogueUnavailable);
            }
            catch (Exception ex)
            {
                dialogueUnavailable = $"NPC dialogue read unavailable ({ex.GetType().Name}).";
                this.dialogueText.Clear();
            }
        }

        // Each branch also verifies its own scalars; this last check covers the whole capture.
        if (!clientState.IsLoggedIn || !playerState.IsLoaded)
        {
            this.dialogueText.Clear();
            character = null;
            dialogue = null;
            location = null;
            if (characterAllowed)
                characterUnavailable = "Character data changed during reading; waiting for it to load.";
            if (dialogueAllowed)
                dialogueUnavailable = "Character data changed during reading; NPC dialogue is unavailable.";
            if (locationAllowed)
                locationUnavailable = "Location changed during reading; waiting for it to load.";
        }

        return new(capturedAtUtc, character, dialogue, characterUnavailable, dialogueUnavailable, location, locationUnavailable);
    }

    private GameCharacterContext ReadCharacter()
    {
        var localPlayer = objects.LocalPlayer;
        if (localPlayer is null || localPlayer.Address == nint.Zero)
            throw new InvalidOperationException("Local player is unavailable.");

        var address = localPlayer.Address;
        var entityId = playerState.EntityId; // Compared only locally; never copied into the result.
        var name = RequireText(playerState.CharacterName, MaxNameCharacters);
        var classJobId = playerState.ClassJob.RowId;
        var level = (int)playerState.Level; // IPlayerState.Level is the actual, unsynced job level.
        if (entityId == 0 || localPlayer.EntityId != entityId || classJobId == 0 ||
            localPlayer.ClassJob.RowId != classJobId || level is < 1 or > 200)
            throw new InvalidOperationException("Character state is not ready.");

        var classJobName = "[class/job name unavailable]";
        var abbreviation = "[abbreviation unavailable]";
        int? experience = null;
        int? nextLevelExperience = null;
        if (data.GetExcelSheet<ClassJob>().TryGetRow(classJobId, out var classJob))
        {
            classJobName = LabelOrUnavailable(classJob.Name.ExtractText(), classJobName);
            abbreviation = LabelOrUnavailable(classJob.Abbreviation.ExtractText(), abbreviation,
                MaxAbbreviationCharacters);
            if (classJob.ExpArrayIndex >= 0)
            {
                try
                {
                    var currentExperience = playerState.GetClassJobExperience(classJob);
                    if (currentExperience >= 0)
                        experience = currentExperience;
                }
                catch
                {
                    // An unavailable EXP field must not erase the other supported character fields.
                    experience = null;
                }
                nextLevelExperience = ReadNextLevelExperience(entityId, classJobId, level);
            }
        }

        var after = objects.LocalPlayer;
        if (!clientState.IsLoggedIn || !playerState.IsLoaded || after is null ||
            after.Address != address || after.EntityId != entityId || playerState.EntityId != entityId ||
            playerState.ClassJob.RowId != classJobId || playerState.Level != level ||
            !string.Equals(playerState.CharacterName, name, StringComparison.Ordinal))
            throw new InvalidOperationException("Character state changed during reading.");

        return new(name, classJobId, classJobName, abbreviation, level, experience,
            nextLevelExperience);
    }

    private GameLocationContext ReadLocation()
    {
        // This branch never reads the character's name, job or experience.
        var territoryId = (uint)clientState.TerritoryType;
        if (territoryId == 0) throw new InvalidOperationException("Location is not ready.");
        // IPlayerState exposes current and home World rows separately, including while
        // visiting another world. IDs are used only for stable reads and local sheet lookup.
        var currentWorldId = playerState.CurrentWorld.RowId;
        var homeWorldId = playerState.HomeWorld.RowId;
        var locationName = "[location name unavailable]";
        if (data.GetExcelSheet<TerritoryType>().TryGetRow(territoryId, out var territory) && territory.PlaceName.IsValid)
            locationName = LabelOrUnavailable(territory.PlaceName.Value.Name.ExtractText(), locationName);
        var currentWorldName = this.ReadWorldName(currentWorldId);
        var homeWorldName = this.ReadWorldName(homeWorldId);
        if (!clientState.IsLoggedIn || !playerState.IsLoaded || clientState.TerritoryType != territoryId ||
            playerState.CurrentWorld.RowId != currentWorldId || playerState.HomeWorld.RowId != homeWorldId)
            throw new InvalidOperationException("Location changed during reading.");
        return new(territoryId, locationName, currentWorldName, homeWorldName);
    }

    private string? ReadWorldName(uint worldId)
    {
        if (worldId == 0) return null;
        try
        {
            if (!data.GetExcelSheet<World>().TryGetRow(worldId, out var world)) return null;
            var name = world.Name.ExtractText();
            return string.IsNullOrWhiteSpace(name) || name.Length > MaxLabelCharacters || name.Contains('\0')
                ? null : name.Trim();
        }
        catch
        {
            // A missing world name must not erase a supported zone or substitute the other world.
            return null;
        }
    }

    private static int? ReadNextLevelExperience(uint entityId, uint classJobId, int level)
    {
        try
        {
            var player = NativePlayerState.Instance();
            if (player == null || !player->IsLoaded || player->EntityId != entityId ||
                player->CurrentClassJobId != classJobId)
                return null;

            // The native current-job queries account for limited jobs and the character's cap.
            // A generic ParamGrow row alone cannot establish whether another level is available.
            var maxLevel = player->GetCurrentClassJobMaxLevel();
            if (maxLevel == 0 || level >= maxLevel)
                return null;
            var needed = player->GetCurrentClassJobNeededExp();
            if (needed == 0 || needed > int.MaxValue || NativePlayerState.Instance() != player ||
                !player->IsLoaded || player->EntityId != entityId || player->CurrentClassJobId != classJobId)
                return null;
            return (int)needed;
        }
        catch
        {
            // Keep the supported service fields when this version's optional native query is unavailable.
            return null;
        }
    }

    private NpcDialogueContext? ReadDialogue(out string? unavailable)
    {
        unavailable = "No visible Talk window.";
        var addon = gui.GetAddonByName<AddonTalk>("Talk", 1);
        if (addon == null || !addon->IsReady || !addon->IsVisible)
        {
            this.dialogueText.Clear();
            return null;
        }

        // These are the installed AddonTalk's speaker node and current-sentence buffer.
        // Read exactly these two fields: no node traversal or arbitrary addon text fallback.
        var speakerNode = addon->AtkTextNode220;
        if (speakerNode == null || speakerNode->Type != NodeType.Text)
        {
            this.dialogueText.Clear();
            unavailable = "The Talk speaker node is not ready.";
            return null;
        }

        var speakerVisible = (speakerNode->NodeFlags & NodeFlags.Visible) != 0;
        var speakerBytes = speakerVisible
            ? TextBytes(&speakerNode->NodeText, NpcDialogueTextCache.MaxSpeakerBytes)
            : ReadOnlySpan<byte>.Empty;
        var sentenceBytes = TextBytes(&addon->String268, NpcDialogueTextCache.MaxDialogueBytes);
        // Measure changed-page decoding, not the cheap unchanged-page checks.
        using PerformanceDiagnostics.TimingScope? timing = this.dialogueText.Matches(speakerBytes, sentenceBytes)
            ? null : PerformanceDiagnostics.Measure("reader.npc");
        var line = this.dialogueText.Read(speakerBytes, sentenceBytes,
            static bytes => SeString.Parse(bytes.ToArray()).TextValue);
        if (line.Text.Length == 0)
        {
            unavailable = "The visible Talk window has no readable sentence.";
            return null;
        }

        if (gui.GetAddonByName<AddonTalk>("Talk", 1) != addon || !addon->IsReady || !addon->IsVisible ||
            addon->AtkTextNode220 != speakerNode ||
            ((speakerNode->NodeFlags & NodeFlags.Visible) != 0) != speakerVisible ||
            !this.dialogueText.Matches(speakerVisible
                    ? TextBytes(&speakerNode->NodeText, NpcDialogueTextCache.MaxSpeakerBytes)
                    : ReadOnlySpan<byte>.Empty,
                TextBytes(&addon->String268, NpcDialogueTextCache.MaxDialogueBytes)))
        {
            this.dialogueText.Clear();
            unavailable = "The Talk line changed during reading; waiting for it to settle.";
            return null;
        }

        // An empty speaker remains empty: do not infer an NPC identity or invent a narrator.
        unavailable = null;
        return line;
    }

    private static ReadOnlySpan<byte> TextBytes(Utf8String* source, int maxBytes)
    {
        if (source == null || source->BufUsed < 0 || source->BufUsed > maxBytes + 1 ||
            source->BufSize < source->BufUsed || source->StringLength < 0 ||
            source->StringLength > maxBytes || source->StringLength > source->BufUsed)
            throw new InvalidOperationException("Dialogue text exceeds the reader bounds.");
        if (source->BufUsed <= 1)
            return ReadOnlySpan<byte>.Empty;
        if ((byte*)source->StringPtr == null)
            throw new InvalidOperationException("Dialogue text is unavailable.");

        // BufUsed is checked before AsSpan, whose length is derived from BufUsed - 1.
        return source->AsSpan();
    }

    private static string RequireText(string value, int maxCharacters)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxCharacters)
            throw new InvalidOperationException("Character text is unavailable.");
        return value;
    }

    private static string LabelOrUnavailable(string value, string unavailable,
        int maxCharacters = MaxLabelCharacters) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maxCharacters ? unavailable : value.Trim();
}
