using System;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace CompAI;

/// <summary>Framework-thread only. Reads ITargetManager.Target only, with no actor enumeration or actions.</summary>
internal sealed unsafe class NpcTargetReader(ITargetManager targets, IDataManager data)
{
    internal NpcTargetSnapshot Capture(bool allowNpcs = true, bool allowPlayersAndObjects = false)
    {
        NpcTargetSnapshot Unavailable(string reason) => NpcTargetSnapshot.Unavailable(reason, allowNpcs, allowPlayersAndObjects);
        if (!allowNpcs && !allowPlayersAndObjects) return Unavailable("Selected-target access is off.");
        try
        {
            var player = PlayerState.Instance();
            if (player == null || !player->IsLoaded || player->ContentId == 0)
                return Unavailable("Character data is not loaded.");
            var owner = player->ContentId;
            var target = targets.Target;
            // Check category permission before reading identifying text or details.
            if (target is null)
                return Unavailable("Select a permitted target to identify it.");
            var kind = target.ObjectKind;
            if (!SupportedKind((byte)kind, allowNpcs, allowPlayersAndObjects))
                return Unavailable(kind == ObjectKind.Aetheryte ? "Aetheryte targets are excluded." : "This target category is not permitted or supported.");
            var objectId = target.GameObjectId;
            var address = target.Address;
            var entry = ReadTarget(target, kind);
            var current = targets.Target;
            if (PlayerState.Instance() != player || !player->IsLoaded || player->ContentId != owner || current is null ||
                current.ObjectKind != kind || current.GameObjectId != objectId || current.Address != address ||
                ReadTarget(current, kind) != entry)
                return Unavailable("The selected target changed during reading; try again.");
            return new(DateTimeOffset.UtcNow, entry, ownerContentId: owner, targetObjectId: objectId, targetAddress: address,
                allowNpcs: allowNpcs, allowPlayersAndObjects: allowPlayersAndObjects);
        }
        catch (Exception ex)
        {
            return Unavailable($"Selected-target read unavailable ({ex.GetType().Name}).");
        }
    }

    internal bool IsCurrent(NpcTargetSnapshot snapshot)
    {
        if (!snapshot.Available) return true;
        if (!snapshot.IsFresh(DateTimeOffset.UtcNow)) return false;
        var current = Capture(snapshot.AllowNpcs, snapshot.AllowPlayersAndObjects);
        return current.Available && current.OwnerContentId == snapshot.OwnerContentId &&
            current.TargetObjectId == snapshot.TargetObjectId && current.TargetAddress == snapshot.TargetAddress &&
            current.Target == snapshot.Target;
    }

    private NpcTargetEntry ReadTarget(IGameObject target, ObjectKind kind)
    {
        var name = target.Name.TextValue.Trim();
        if (kind != ObjectKind.Pc)
            return new(name, target.BaseId == 0 ? null : target.BaseId, KindName((byte)kind)!);
        if (target is not IPlayerCharacter player) throw new InvalidOperationException("Player target details are unavailable.");
        var homeWorld = player.HomeWorld.RowId;
        var job = player.ClassJob.RowId;
        var level = player.Level;
        string? worldName = null, jobName = null;
        if (homeWorld != 0 && data.GetExcelSheet<World>().TryGetRow(homeWorld, out var world))
        {
            var text = world.Name.ExtractText().Trim();
            if (NpcTargetSnapshot.ValidText(text)) worldName = text;
        }
        if (job != 0 && data.GetExcelSheet<ClassJob>().TryGetRow(job, out var classJob))
        {
            var text = classJob.Name.ExtractText().Trim();
            if (NpcTargetSnapshot.ValidText(text)) jobName = text;
        }
        return new(name, null, "player", new(homeWorld == 0 ? null : homeWorld, worldName,
            job == 0 ? null : job, jobName, level == 0 ? null : level, NpcTargetSnapshot.LodestoneSearch(name, worldName)));
    }

    internal static bool SupportedKind(byte kind, bool allowNpcs = true, bool allowPlayersAndObjects = false) =>
        KindName(kind) is { } name && NpcTargetSnapshot.AllowedKind(name, allowNpcs, allowPlayersAndObjects);
    internal static string? KindName(byte kind) => (ObjectKind)kind switch
    {
        ObjectKind.Pc => "player", ObjectKind.BattleNpc => "battle_npc", ObjectKind.EventNpc => "event_npc",
        ObjectKind.Treasure => "treasure", ObjectKind.GatheringPoint => "gathering_point", ObjectKind.EventObj => "event_object",
        ObjectKind.Mount => "mount", ObjectKind.Companion => "companion", ObjectKind.Retainer => "retainer",
        ObjectKind.AreaObject => "area_object", ObjectKind.HousingEventObject => "housing_event_object",
        ObjectKind.Cutscene => "cutscene", ObjectKind.ReactionEventObject => "reaction_event_object",
        ObjectKind.Ornament => "ornament", ObjectKind.CardStand => "card_stand", ObjectKind.FollowMount => "follow_mount",
        _ => null, // None, aetherytes and unknown future kinds remain unsupported.
    };
}
