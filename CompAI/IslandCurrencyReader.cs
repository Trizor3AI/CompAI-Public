using System;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace CompAI;

/// <summary>Framework-thread observation of the player's visible Island Index or initialized export menu. Never opens or changes either.</summary>
internal sealed unsafe class IslandCurrencyReader(IDataManager data, IGameGui gui)
{
    internal const uint SeafarerId = 37549;
    internal const uint IslanderId = 37550;
    internal const int MaxNameLength = 128;
    internal const string AddonName = "MJIDisposeShop";
    internal const string IndexAddonName = "MJIHud";
    private const int IndexValueLimit = 256;

    internal IslandCurrencyObservation? Capture() => CaptureSource(fromIndex: true) ?? CaptureSource(fromIndex: false);

    private IslandCurrencyObservation? CaptureSource(bool fromIndex)
    {
        try
        {
            var owner = Observe(fromIndex);
            if (owner is null) return null;
            var game = data.GameData;
            var language = data.Language;
            var sheet = data.GetExcelSheet<Item>();
            IslandCurrencyDefinition? Definition(uint id) => sheet.TryGetRow(id, out var row)
                ? new(row.Name.ExtractText(), row.Icon) : null;
            bool CatalogMatches() => ReferenceEquals(game, data.GameData) && language == data.Language &&
                ReferenceEquals(sheet.RawSheet, data.GetExcelSheet<Item>().RawSheet);
            return CaptureStable(() => Observe(fromIndex), Copy, Definition, CatalogMatches, owner);
        }
        catch (Exception) { return null; }
    }

    private IslandCurrencyOwner? Observe(bool fromIndex)
    {
        var player = PlayerState.Instance();
        var module = AgentModule.Instance();
        var manager = RaptureAtkUnitManager.Instance();
        if (player == null || !player->IsLoaded || player->ContentId == 0 || module == null || manager == null)
            return null;
        var agent = module->GetAgentByInternalId(fromIndex ? AgentId.MJIHud : AgentId.MJIDisposeShop);
        if (agent == null || agent->AddonId is 0 or > ushort.MaxValue || !agent->IsAgentActive() ||
            !agent->IsAddonReady() || !agent->IsAddonShown())
            return null;
        var name = fromIndex ? IndexAddonName : AddonName;
        var addon = manager->GetAddonById((ushort)agent->AddonId);
        if (addon == null || addon->NameString != name) return null;
        var handle = gui.GetAddonByName(name);
        if (handle.IsNull || !MatchesAddon((nint)addon, agent->AddonId, addon->NameString,
                handle.Address, handle.Id, handle.IsReady, handle.IsVisible, fromIndex))
            return null;
        nint detailAddress;
        var valueCount = 0;
        if (fromIndex)
        {
            if (addon->AtkValues == null || addon->AtkValuesCount is < 18 or > IndexValueLimit) return null;
            detailAddress = (nint)addon->AtkValues;
            valueCount = addon->AtkValuesCount;
        }
        else
        {
            var detail = ((AgentMJIDisposeShop*)agent)->Data;
            if (detail == null || detail->InitializationState != 3 || !detail->DataInitialized) return null;
            detailAddress = (nint)detail;
        }
        return new((nint)player, player->ContentId, (nint)module, (nint)manager,
            (nint)agent, (nint)addon, agent->AddonId, detailAddress, fromIndex, valueCount);
    }

    internal static bool MatchesAddon(nint addon, uint addonId, string name,
        nint handleAddress, uint handleId, bool ready, bool visible, bool fromIndex = false) =>
        addon != 0 && addonId is > 0 and <= ushort.MaxValue && name == (fromIndex ? IndexAddonName : AddonName) &&
        addon == handleAddress && addonId == handleId && ready && visible;

    // Copy only the source's two currency values. No upkeep/XP/rank, resources,
    // vectors, text buffers, ownership guesses or undocumented dirty flags are read.
    internal static IslandCurrencySample? Copy(IslandCurrencyOwner owner)
    {
        if (!ValidOwner(owner)) return null;
        if (owner.FromIndex) return CopyIndex(owner);
        var agent = (AgentMJIDisposeShop*)owner.Agent;
        if (agent->AddonId != owner.AddonId || (nint)agent->Data != owner.Data) return null;
        var detail = agent->Data;
        var state = detail->InitializationState;
        var initialized = detail->DataInitialized;
        if (state != 3 || !initialized) return null;
        var first = new IslandCurrencySlot(detail->CurrencyItemIds[0], detail->CurrencyIconIds[0],
            detail->CurrencyCounts[0], detail->CurrencyStackSizes[0]);
        var second = new IslandCurrencySlot(detail->CurrencyItemIds[1], detail->CurrencyIconIds[1],
            detail->CurrencyCounts[1], detail->CurrencyStackSizes[1]);
        if (agent->AddonId != owner.AddonId || agent->Data != detail ||
            detail->InitializationState != state || detail->DataInitialized != initialized)
            return null;
        return new(state, initialized, first, second);
    }

    private static IslandCurrencySample? CopyIndex(IslandCurrencyOwner owner)
    {
        var agent = (AgentInterface*)owner.Agent;
        var addon = (AtkUnitBase*)owner.Addon;
        if (agent->AddonId != owner.AddonId || addon->Id != owner.AddonId ||
            (nint)addon->AtkValues != owner.Data || addon->AtkValuesCount != owner.ValueCount)
            return null;
        var values = addon->AtkValues;
        // ECommons' MJIHud implementation names 14 as Islander's Cowries and
        // 17 as Seafarer's Cowries. Only those slots are copied; upkeep is separate.
        var islander = values[14];
        var seafarer = values[17];
        if (!TryQuantity(islander, out var islanderCount) || !TryQuantity(seafarer, out var seafarerCount)) return null;
        if (agent->AddonId != owner.AddonId || addon->Id != owner.AddonId ||
            addon->AtkValues != values || addon->AtkValuesCount != owner.ValueCount)
            return null;
        return new(0, false,
            new(SeafarerId, null, seafarerCount, null, (int)seafarer.Type),
            new(IslanderId, null, islanderCount, null, (int)islander.Type), FromIndex: true);
    }

    private static bool TryQuantity(AtkValue value, out int quantity)
    {
        quantity = 0;
        if (value.Type == AtkValueType.Int && value.Int >= 0) { quantity = value.Int; return true; }
        if (value.Type == AtkValueType.UInt && value.UInt <= int.MaxValue) { quantity = (int)value.UInt; return true; }
        return false;
    }

    internal static IslandCurrencyObservation? CaptureStable(Func<IslandCurrencyOwner?> observe,
        Func<IslandCurrencyOwner, IslandCurrencySample?> copy,
        Func<uint, IslandCurrencyDefinition?> definition, Func<bool> catalogMatches,
        IslandCurrencyOwner? initialOwner = null)
    {
        var before = initialOwner ?? observe();
        if (before is not { } owner || !ValidOwner(owner)) return null;
        // The initial public sheet load may outlive the menu. Re-resolve the owner
        // before dereferencing any retained native address, and again for each copy.
        if (!catalogMatches() || observe() != before) return null;
        var first = copy(owner);
        if (first is not { } sample || observe() != before || sample.FromIndex != owner.FromIndex || !ValidSample(sample)) return null;
        var seafarerSlot = sample.First.ItemId == SeafarerId ? sample.First : sample.Second;
        var islanderSlot = sample.First.ItemId == IslanderId ? sample.First : sample.Second;
        var seafarer = definition(SeafarerId);
        var islander = definition(IslanderId);
        if (!ValidDefinition(seafarer, seafarerSlot.Icon) || !ValidDefinition(islander, islanderSlot.Icon)) return null;
        if (!catalogMatches() || observe() != before) return null;
        if (copy(owner) != first || observe() != before) return null;
        if (definition(SeafarerId) != seafarer || definition(IslanderId) != islander ||
            !catalogMatches() || observe() != before) return null;
        return new(DateTimeOffset.UtcNow,
            new(SeafarerId, seafarer!.Value.Name.Trim(), seafarerSlot.Quantity),
            new(IslanderId, islander!.Value.Name.Trim(), islanderSlot.Quantity));
    }

    private static bool ValidOwner(IslandCurrencyOwner owner) => owner.Player != 0 && owner.ContentId != 0 &&
        owner.Module != 0 && owner.Manager != 0 && owner.Agent != 0 && owner.Addon != 0 && owner.Data != 0 &&
        owner.AddonId is > 0 and <= ushort.MaxValue &&
        (owner.FromIndex ? owner.ValueCount is >= 18 and <= IndexValueLimit : owner.ValueCount == 0);

    private static bool ValidSample(IslandCurrencySample sample) =>
        (sample.FromIndex ? sample.InitializationState == 0 && !sample.DataInitialized :
            sample.InitializationState == 3 && sample.DataInitialized) &&
        ((sample.First.ItemId == SeafarerId && sample.Second.ItemId == IslanderId) ||
         (sample.First.ItemId == IslanderId && sample.Second.ItemId == SeafarerId)) &&
        ValidSlot(sample.First, sample.FromIndex) && ValidSlot(sample.Second, sample.FromIndex);

    private static bool ValidSlot(IslandCurrencySlot slot, bool fromIndex) => slot.Quantity >= 0 &&
        (fromIndex ? slot.Icon is null && slot.Maximum is null &&
                slot.NativeType is (int)AtkValueType.Int or (int)AtkValueType.UInt :
            slot.Icon > 0 && slot.Maximum > 0 && slot.Quantity <= slot.Maximum && slot.NativeType == 0);

    private static bool ValidDefinition(IslandCurrencyDefinition? definition, uint? icon) =>
        definition is { } item && item.Icon > 0 && (icon is null || item.Icon == icon) && !string.IsNullOrWhiteSpace(item.Name) &&
        item.Name.Length <= MaxNameLength && !item.Name.Any(char.IsControl);
}

internal sealed record IslandCurrencyBalance(uint ItemId, string Name, int Quantity);
internal sealed record IslandCurrencyObservation(DateTimeOffset CapturedAtUtc,
    IslandCurrencyBalance Seafarer, IslandCurrencyBalance Islander);
internal readonly record struct IslandCurrencyDefinition(string Name, uint Icon);
internal readonly record struct IslandCurrencyOwner(nint Player, ulong ContentId, nint Module, nint Manager,
    nint Agent, nint Addon, uint AddonId, nint Data, bool FromIndex = false, int ValueCount = 0);
internal readonly record struct IslandCurrencySlot(uint ItemId, uint? Icon, int Quantity, int? Maximum, int NativeType = 0);
internal readonly record struct IslandCurrencySample(byte InitializationState, bool DataInitialized,
    IslandCurrencySlot First, IslandCurrencySlot Second, bool FromIndex = false);
