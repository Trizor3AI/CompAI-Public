using System;
using System.Linq;
using System.Text.Json;

namespace CompAI;

// Immutable, permission-filtered observations for one answer. No native game access.
internal sealed class GameContextLookup
{
    internal const string Scope = "local_character_and_npc_dialogue";
    private readonly DateTimeOffset capturedAt;
    private readonly bool characterAllowed;
    private readonly bool dialogueAllowed;
    private readonly bool locationAllowed;
    private readonly GameCharacterContext? character;
    private readonly NpcDialogueContext? dialogue;
    private readonly GameLocationContext? location;
    private readonly DateTimeOffset? dialogueObservedAt;
    private readonly bool dialogueVisible;
    private readonly bool dialogueFromPreviousArea;
    private readonly string characterUnavailable;
    private readonly string dialogueUnavailable;
    private readonly string locationUnavailable;

    internal GameContextLookup(DateTimeOffset capturedAt, bool characterAllowed, bool dialogueAllowed,
        GameCharacterContext? character, NpcDialogueContext? dialogue, DateTimeOffset? dialogueObservedAt,
        bool dialogueVisible, string? characterUnavailable, string? dialogueUnavailable,
        bool locationAllowed = false, GameLocationContext? location = null, string? locationUnavailable = null,
        bool dialogueFromPreviousArea = false)
    {
        this.capturedAt = capturedAt;
        this.characterAllowed = characterAllowed;
        this.dialogueAllowed = dialogueAllowed;
        this.locationAllowed = locationAllowed;
        this.character = characterAllowed ? character : null;
        this.dialogue = dialogueAllowed && dialogueObservedAt.HasValue ? dialogue : null;
        this.location = locationAllowed ? location : null;
        this.dialogueObservedAt = this.dialogue is not null ? dialogueObservedAt : null;
        this.dialogueFromPreviousArea = this.dialogue is not null && dialogueFromPreviousArea;
        this.dialogueVisible = this.dialogue is not null && dialogueVisible && !dialogueFromPreviousArea;
        this.characterUnavailable = characterUnavailable ?? "Character details have not been observed yet.";
        this.dialogueUnavailable = dialogueUnavailable ?? "No recent standard NPC dialogue has been observed.";
        this.locationUnavailable = locationUnavailable ?? "Location has not been observed yet.";
    }

    internal object Summary => new
    {
        scope = Scope, captured_at_utc = this.capturedAt,
        character_allowed = this.characterAllowed, dialogue_allowed = this.dialogueAllowed,
        character_available = this.character is not null, dialogue_available = this.dialogue is not null,
        location_allowed = this.locationAllowed, location_available = this.location is not null,
    };

    internal object Query(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Count() != 1 ||
            !arguments.TryGetProperty("view", out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Expected one game-context view.");
        var view = value.GetString();
        if (view is not ("character" or "dialogue" or "location") ||
            view == "character" && !this.characterAllowed || view == "dialogue" && !this.dialogueAllowed ||
            view == "location" && !this.locationAllowed)
            throw new InvalidOperationException("This game-context category is not permitted.");
        object? data = null;
        if (view == "character" && this.character is { } c)
            data = new { name = c.Name, class_job_id = c.ClassJobId, class_job_name = c.ClassJobName,
                abbreviation = c.Abbreviation, level = c.Level, experience = c.Experience,
                next_level_experience = c.NextLevelExperience };
        else if (view == "location" && this.location is { } l)
            data = new { territory_id = l.TerritoryId, location_name = l.LocationName,
                current_world_name = l.CurrentWorldName, home_world_name = l.HomeWorldName };
        else if (view == "dialogue" && this.dialogue is { } d)
            data = new { speaker = d.Speaker, text = d.Text, observed_at_utc = this.dialogueObservedAt,
                is_visible = this.dialogueVisible, from_previous_area = this.dialogueFromPreviousArea };
        return new { scope = Scope, captured_at_utc = this.capturedAt, view, available = data is not null,
            unavailable_reason = data is not null ? null : view == "character"
                ? this.characterUnavailable : view == "location" ? this.locationUnavailable : this.dialogueUnavailable, data };
    }
}

// Only the latest Talk line is kept, briefly and in memory. A closed dialogue is never
// labelled visible. Area changes preserve its age and explicitly mark it as historical;
// logout, character changes and dialogue permission changes clear it entirely.
internal sealed class RecentNpcDialogue
{
    internal static readonly TimeSpan Retention = TimeSpan.FromMinutes(5);
    internal NpcDialogueContext? Line { get; private set; }
    internal DateTimeOffset? ObservedAt { get; private set; }
    internal bool Visible { get; private set; }
    internal bool FromPreviousArea { get; private set; }
    private bool waitingForPreviousTalkToClose;

    internal void Observe(NpcDialogueContext? line, DateTimeOffset now)
    {
        // The old Talk addon can remain visible for part of a zone transition. Re-reading
        // that exact retained line is not evidence of fresh dialogue in the new area.
        if (line is null) this.waitingForPreviousTalkToClose = false;
        if (this.waitingForPreviousTalkToClose && line == this.Line)
        {
            this.Visible = false;
            this.Expire(now);
            return;
        }
        this.Visible = line is not null;
        if (line is not null)
        {
            this.Line = line;
            this.ObservedAt = now;
            this.FromPreviousArea = false;
            this.waitingForPreviousTalkToClose = false;
        }
        this.Expire(now);
    }

    internal void Expire(DateTimeOffset now)
    {
        if (this.ObservedAt is { } observed && (now - observed >= Retention || now < observed)) this.Clear();
    }

    internal void OnAreaChanged(DateTimeOffset now)
    {
        var previousTalkMayBeVisible = this.Visible || this.waitingForPreviousTalkToClose;
        this.Visible = false;
        this.Expire(now);
        this.FromPreviousArea = this.Line is not null;
        this.waitingForPreviousTalkToClose = previousTalkMayBeVisible && this.Line is not null;
    }

    internal void Clear()
    {
        this.Line = null;
        this.ObservedAt = null;
        this.Visible = false;
        this.FromPreviousArea = false;
        this.waitingForPreviousTalkToClose = false;
    }
}
