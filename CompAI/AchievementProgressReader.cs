using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using AchievementSheet = Lumina.Excel.Sheets.Achievement;
using NativeAchievement = FFXIVClientStructs.FFXIV.Client.Game.UI.Achievement;

namespace CompAI;

/// <summary>Framework-thread only. Copies the latest loaded progress record without requesting data.</summary>
internal sealed unsafe class AchievementProgressReader(IDataManager data)
{
    internal const string Category = "achievement_progress";

    internal ProgressionSnapshot Capture()
    {
        try
        {
            var before = Observe();
            if (!before.IsReady)
                return Unavailable();

            // This is one public definition lookup, never a catalog scan or a
            // progress request. The record's own key is an Achievement RowId.
            AchievementProgressDefinition? definition = null;
            if (data.GetExcelSheet<AchievementSheet>().TryGetRow(before.AchievementId, out var row))
                definition = new(row.RowId, row.Name.ExtractText().Trim());

            return Describe(DateTimeOffset.UtcNow, before, Observe(), definition);
        }
        catch (Exception ex)
        {
            return ProgressionSnapshot.Unavailable(Category,
                $"Achievement progress read unavailable ({ex.GetType().Name}).");
        }
    }

    private static AchievementProgressObservation Observe()
    {
        var player = PlayerState.Instance();
        var achievement = NativeAchievement.Instance();
        var agent = AgentAchievement.Instance();
        if (player == null || achievement == null || agent == null ||
            !player->IsLoaded || player->ContentId == 0 ||
            !agent->IsAgentActive() || !agent->IsAddonShown() || !agent->IsAddonReady())
            return default;

        // AgentAchievement exposes no selected achievement ID. In particular,
        // ContextMenuSelectedItemId is not a selection binding for this record.
        // Menu readiness merely limits when this cached observation is exposed.
        var state = achievement->ProgressRequestState;
        var observation = new AchievementProgressObservation((nint)player, (nint)achievement, (nint)agent,
            player->ContentId, player->IsLoaded, true, agent->AddonId, state,
            achievement->ProgressAchievementId, achievement->ProgressCurrent, achievement->ProgressMax);
        return achievement->ProgressRequestState == state ? observation : default;
    }

    internal static ProgressionSnapshot Describe(DateTimeOffset capturedAt,
        AchievementProgressObservation before, AchievementProgressObservation after,
        AchievementProgressDefinition? definition)
    {
        if (!before.IsReady || !after.IsReady || before != after)
            return Unavailable();
        if (definition is null || definition.Id != before.AchievementId ||
            !ProgressionSnapshot.ValidText(definition.Name, ProgressionSnapshot.MaxNameLength))
            return ProgressionSnapshot.Unavailable(Category,
                "The loaded achievement progress record has no supported named Achievement definition.");

        // uint counters are widened losslessly. Do not clamp a value above its
        // target or infer completion: the completion bitmap is a separate source.
        return new(Category, capturedAt, true, "loaded_progress_record",
            [new(definition.Id, definition.Name, "observed",
                Progress: (long)before.Current, Required: (long)before.Maximum)]);
    }

    private static ProgressionSnapshot Unavailable() => ProgressionSnapshot.Unavailable(Category,
        "No stable loaded achievement progress record is available in the open Achievements menu. " +
        "Open an achievement yourself, wait for its progress to load, then ask again.");
}

internal sealed record AchievementProgressDefinition(uint Id, string Name);

internal readonly record struct AchievementProgressObservation(nint PlayerAddress, nint AchievementAddress,
    nint AgentAddress, ulong ContentId, bool PlayerLoaded, bool MenuReady, uint AddonId,
    NativeAchievement.AchievementState State, uint AchievementId, uint Current, uint Maximum)
{
    internal bool IsReady => PlayerAddress != 0 && AchievementAddress != 0 && AgentAddress != 0 &&
        ContentId != 0 && PlayerLoaded && MenuReady && AddonId != 0 &&
        State == NativeAchievement.AchievementState.Loaded && AchievementId != 0 && Maximum != 0;
}
