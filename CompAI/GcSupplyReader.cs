using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace CompAI;

/// <summary>Framework-thread only. Observes an already open menu; never opens UI or sends packets.</summary>
internal sealed unsafe class GcSupplyReader(IDataManager data, IGameGui gui)
{
    internal GcSupplySnapshot Capture()
    {
        try
        {
            var player = PlayerState.Instance();
            if (player == null || !player->IsLoaded || player->ContentId == 0)
                return GcSupplySnapshot.Unavailable("Character data is not loaded.");
            var owner = player->ContentId;
            var addon = gui.GetAddonByName<AtkUnitBase>("GrandCompanySupplyList", 1);
            var agent = AgentGrandCompanySupply.Instance();
            if (addon == null || !addon->IsVisible)
                return GcSupplySnapshot.Unavailable("Open the Grand Company Supply or Provisioning delivery window to observe today's requests.");
            if (!addon->IsReady || agent == null || !agent->IsAgentActive())
                return GcSupplySnapshot.Unavailable("The Grand Company delivery window is visible but still loading; wait briefly and try again.");
            if (agent->SelectedTab is not (0 or 1))
                return GcSupplySnapshot.Unavailable("Select Supply or Provisioning; Expert Delivery is not a daily-request list.");
            if (!HasRequestData(agent))
                return GcSupplySnapshot.Unavailable("The Grand Company delivery window is visible, but its daily request data is unavailable; close and reopen it to refresh.");
            var captured = DateTimeOffset.UtcNow;
            var selectedTab = agent->SelectedTab;
            var sourceAddress = (nint)agent->SupplyProvisioningData;
            var itemAddress = (nint)agent->ItemArray;
            var count = agent->NumItems;
            var observed = CopyRows(agent);
            var sheet = data.GetExcelSheet<Item>();
            var entries = new List<GcSupplyEntry>();
            foreach (var row in observed)
            {
                if (!sheet.TryGetRow(row.ItemId, out var item))
                    return GcSupplySnapshot.Unavailable("A requested item's local name is unavailable.");
                var name = item.Name.ExtractText().Trim();
                entries.Add(new(row.Position, GcSupplySnapshot.JobId(row.Position), GcSupplySnapshot.JobAbbreviation(row.Position),
                    GcSupplySnapshot.Category(row.Position), row.ItemId, name, row.QuantityRequested,
                    row.ExperienceReward, row.SealReward, row.TurnInAvailable, row.BonusReward));
            }
            if (PlayerState.Instance() != player || !player->IsLoaded || player->ContentId != owner ||
                gui.GetAddonByName<AtkUnitBase>("GrandCompanySupplyList", 1) != addon ||
                AgentGrandCompanySupply.Instance() != agent || !Ready(addon, agent) ||
                agent->SelectedTab != selectedTab || (nint)agent->SupplyProvisioningData != sourceAddress ||
                (nint)agent->ItemArray != itemAddress || agent->NumItems != count || !observed.SequenceEqual(CopyRows(agent)))
                return GcSupplySnapshot.Unavailable("The Grand Company menu changed during reading; try again when it is ready.");
            if (entries.Count == 0)
                return GcSupplySnapshot.Unavailable("The visible Grand Company menu has no readable daily requests yet.");
            return new(captured, true, entries, ownerContentId: owner, menuAddress: (nint)addon,
                agentAddress: (nint)agent, selectedTab: selectedTab);
        }
        catch (Exception ex)
        {
            return GcSupplySnapshot.Unavailable($"Grand Company read unavailable ({ex.GetType().Name}).");
        }
    }

    internal bool IsCurrent(GcSupplySnapshot snapshot)
    {
        if (!snapshot.Available) return true;
        if (!snapshot.IsFresh(DateTimeOffset.UtcNow)) return false;
        var current = Capture();
        return current.Available && current.OwnerContentId == snapshot.OwnerContentId &&
            current.MenuAddress == snapshot.MenuAddress && current.AgentAddress == snapshot.AgentAddress &&
            current.SelectedTab == snapshot.SelectedTab && current.Entries.SequenceEqual(snapshot.Entries);
    }

    private static bool Ready(AtkUnitBase* addon, AgentGrandCompanySupply* agent) =>
        addon != null && addon->IsReady && addon->IsVisible && agent != null && agent->IsAgentActive() &&
        agent->SelectedTab is 0 or 1 && HasRequestData(agent);

    // Daily requirements live in fixed arrays separately from the filtered
    // delivery list. An empty list must not hide valid requirements. Its rows
    // are optional evidence for eligibility, never evidence of completion.
    internal static bool HasRequestData(AgentGrandCompanySupply* agent) =>
        agent != null && agent->SupplyProvisioningData != null && agent->NumItems is >= 0 and <= 10000 &&
        (agent->NumItems == 0 || agent->ItemArray != null);

    internal static GcSupplyObservedRow[] CopyRows(AgentGrandCompanySupply* agent)
    {
        var rows = new List<GcSupplyObservedRow>(GcSupplySnapshot.MaxEntries);
        // The fixed arrays, not NumItems, bound daily data. Expert delivery entries are never traversed.
        for (var position = 0; position < GcSupplySnapshot.MaxEntries; position++)
        {
            var source = position < 8 ? agent->SupplyProvisioningData->SupplyData[position] :
                agent->SupplyProvisioningData->ProvisioningData[position - 8];
            if (source.ItemId == 0) continue;
            if (source.NumRequested == 0 || source.ExpReward < 0 || source.SealReward < 0)
                throw new InvalidOperationException("Daily request is not ready.");
            bool? available = null;
            var matches = 0;
            for (var index = 0; index < Math.Min(agent->NumItems, GcSupplySnapshot.MaxEntries); index++)
            {
                var item = agent->ItemArray[index];
                if (item.Position != position || item.ItemId != source.ItemId || item.NumRequested != source.NumRequested) continue;
                matches++;
                available = TurnInFlag(item.TurnInAvailable);
            }
            if (matches > 1) throw new InvalidOperationException("Daily request identity is ambiguous.");
            // The installed/upstream IsBonusReward only tests Bonus != 0. Its
            // relationship to visible daily stars, especially after delivery,
            // is unverified and produced a reported mismatch. Do not read or
            // interpret that byte as either starred or unstarred.
            rows.Add(new(position, source.ItemId, source.NumRequested, source.ExpReward, source.SealReward, available, null));
        }
        return rows.ToArray();
    }

    // Native IsTurnInAvailable is inverted: zero means eligible. Other raw values
    // remain unknown until their meaning is validated, rather than implying completion.
    internal static bool? TurnInFlag(byte value) => value switch { 0 => true, 1 => false, _ => null };
}

internal readonly record struct GcSupplyObservedRow(int Position, uint ItemId, int QuantityRequested,
    int ExperienceReward, int SealReward, bool? TurnInAvailable, bool? BonusReward);
