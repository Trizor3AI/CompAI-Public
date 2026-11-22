using System;
using System.Linq;

namespace CompAI;

internal sealed record NpcTargetPlayer(uint? HomeWorldId, string? HomeWorldName, uint? ClassJobId,
    string? ClassJobName, int? Level, string? LodestoneSearchUrl);
internal sealed record NpcTargetEntry(string Name, uint? DataId, string Kind, NpcTargetPlayer? Player = null);

internal sealed class NpcTargetSnapshot
{
    internal const int MaxNameLength = 128;
    internal static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);
    internal const string Coverage = "Only the currently selected target, within the separately permitted categories. " +
        "NPC access permits battle/event NPCs; Target Info permits players and other supported objects. Aetherytes are excluded. " +
        "Player details are visible name, home world, class/job and level; unavailable fields remain unknown. " +
        "The Lodestone URL is an offline name/home-world search link, not a verified profile. " +
        "No actor enumeration, inspection request, coordinates, equipment, account/owner IDs, shop inventory, quests or interaction is included.";
    internal DateTimeOffset CapturedAtUtc { get; }
    internal bool Available => Target is not null;
    internal string? UnavailableReason { get; }
    internal NpcTargetEntry? Target { get; }
    internal bool AllowNpcs { get; }
    internal bool AllowPlayersAndObjects { get; }
    internal ulong OwnerContentId { get; }
    internal ulong TargetObjectId { get; }
    internal nint TargetAddress { get; }

    internal NpcTargetSnapshot(DateTimeOffset capturedAtUtc, NpcTargetEntry? target, string? unavailableReason = null,
        ulong ownerContentId = 0, ulong targetObjectId = 0, nint targetAddress = 0,
        bool allowNpcs = true, bool allowPlayersAndObjects = false)
    {
        if (target is not null && (!ValidText(target.Name) || target.DataId == 0 ||
            !AllowedKind(target.Kind, allowNpcs, allowPlayersAndObjects) || unavailableReason is not null ||
            (target.Kind == "player" ? target.DataId is not null || !ValidPlayer(target.Name, target.Player) : target.Player is not null)) ||
            unavailableReason is { Length: 0 or > 256 })
            throw new ArgumentException("Invalid selected-target observation.", nameof(target));
        CapturedAtUtc = capturedAtUtc.ToUniversalTime();
        Target = target;
        AllowNpcs = allowNpcs;
        AllowPlayersAndObjects = allowPlayersAndObjects;
        UnavailableReason = target is null ? unavailableReason ?? "No permitted supported target is selected." : null;
        OwnerContentId = ownerContentId;
        TargetObjectId = targetObjectId;
        TargetAddress = targetAddress;
    }

    internal bool IsFresh(DateTimeOffset now) => now >= CapturedAtUtc - TimeSpan.FromSeconds(2) && now - CapturedAtUtc <= MaxAge;
    internal static bool AllowedKind(string kind, bool allowNpcs, bool allowPlayersAndObjects) => kind switch
    {
        "battle_npc" or "event_npc" => allowNpcs,
        "player" or "treasure" or "gathering_point" or "event_object" or "mount" or "companion" or "retainer" or
        "area_object" or "housing_event_object" or "cutscene" or "reaction_event_object" or "ornament" or
        "card_stand" or "follow_mount" => allowPlayersAndObjects,
        _ => false,
    };
    internal static bool ValidText(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= MaxNameLength &&
        !value.Any(char.IsControl) && !value.Any(char.IsSurrogate);
    internal static string? LodestoneSearch(string name, string? homeWorld) => homeWorld is null ? null :
        "https://na.finalfantasyxiv.com/lodestone/character/?q=" + Uri.EscapeDataString(name) +
        "&worldname=" + Uri.EscapeDataString(homeWorld);
    private static bool ValidPlayer(string name, NpcTargetPlayer? player) => player is not null &&
        player.HomeWorldId != 0 && player.ClassJobId != 0 && player.Level is not (< 1 or > 255) &&
        (player.HomeWorldName is null || player.HomeWorldId is not null && ValidText(player.HomeWorldName)) &&
        (player.ClassJobName is null || player.ClassJobId is not null && ValidText(player.ClassJobName)) &&
        player.LodestoneSearchUrl == LodestoneSearch(name, player.HomeWorldName);
    internal static NpcTargetSnapshot Unavailable(string reason, bool allowNpcs = true, bool allowPlayersAndObjects = false) =>
        new(DateTimeOffset.UtcNow, null, reason, allowNpcs: allowNpcs, allowPlayersAndObjects: allowPlayersAndObjects);
}
