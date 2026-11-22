using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace CompAI;

/// <summary>Framework-thread copies of identified resources and Felicitous Tokens in the normally open Isleventory.</summary>
internal sealed unsafe class IslandMaterialsReader(IDataManager data, IGameGui gui)
{
    internal const string AddonName = "MJIPouch";
    internal const int MaxEntries = 512;
    internal const uint FelicitousTokenPouchRowId = 108;
    internal const uint FelicitousTokenItemId = 41668;
    internal const uint FelicitousTokenCategoryId = 10;
    internal const int FelicitousTokenMaximum = 150;
    // The installed public category has no name. This is a CompAI display label.
    internal const string FelicitousTokenCategoryLabel = "Tokens";

    internal IslandMaterialsObservation? Capture()
    {
        try
        {
            var owner = Observe();
            if (owner is null) return null;
            var game = data.GameData;
            var language = data.Language;
            var pouch = data.GetExcelSheet<MJIItemPouch>();
            var items = data.GetExcelSheet<Item>();
            var categories = data.GetExcelSheet<MJIItemCategory>();
            if (pouch.Count is < 1 or > MaxEntries) return null;
            IslandMaterialDefinition? Definition(uint id)
            {
                if (!pouch.TryGetRow(id, out var row) ||
                    !items.TryGetRow(row.Item.RowId, out var item) || !categories.TryGetRow(row.Category.RowId, out var category) ||
                    item.StackSize is 0 or > int.MaxValue) return null;
                var result = new IslandMaterialDefinition(row.RowId, row.Item.RowId, item.Name.ExtractText(), item.Icon, (int)item.StackSize,
                    row.Category.RowId, category.Plural.ExtractText());
                return SupportedDefinition(result) ? result : null;
            }
            bool CatalogMatches() => ReferenceEquals(game, data.GameData) && language == data.Language &&
                ReferenceEquals(pouch.RawSheet, data.GetExcelSheet<MJIItemPouch>().RawSheet) &&
                ReferenceEquals(items.RawSheet, data.GetExcelSheet<Item>().RawSheet) &&
                ReferenceEquals(categories.RawSheet, data.GetExcelSheet<MJIItemCategory>().RawSheet);
            return CaptureStable(Observe, Copy, Definition, CatalogMatches, owner);
        }
        catch (Exception) { return null; }
    }

    internal static bool SupportedCategory(uint category) => category is 1 or 3 or 4 or 5 or 6 or 7;

    private static bool FelicitousTokenIdentity(uint pouchRowId, uint itemId, uint categoryId) =>
        pouchRowId == FelicitousTokenPouchRowId && itemId == FelicitousTokenItemId && categoryId == FelicitousTokenCategoryId;

    private static bool IsFelicitousToken(IslandMaterialDefinition item) =>
        FelicitousTokenIdentity(item.PouchRowId, item.ItemId, item.CategoryId) && item.Maximum == FelicitousTokenMaximum;

    private static bool SupportedDefinition(IslandMaterialDefinition item) => SupportedCategory(item.CategoryId) || IsFelicitousToken(item);

    private IslandMaterialsOwner? Observe()
    {
        var player = PlayerState.Instance();
        var module = AgentModule.Instance();
        var manager = RaptureAtkUnitManager.Instance();
        if (player == null || !player->IsLoaded || player->ContentId == 0 || module == null || manager == null) return null;
        var agent = (AgentMJIPouch*)module->GetAgentByInternalId(AgentId.MJIPouch);
        if (agent == null || agent->AddonId is 0 or > ushort.MaxValue || !agent->IsAgentActive() ||
            !agent->IsAddonReady() || !agent->IsAddonShown()) return null;
        var addon = manager->GetAddonById((ushort)agent->AddonId);
        if (addon == null || addon->NameString != AddonName) return null;
        var handle = gui.GetAddonByName(AddonName);
        if (handle.IsNull || !MatchesAddon((nint)addon, agent->AddonId, addon->NameString,
                handle.Address, handle.Id, handle.IsReady, handle.IsVisible)) return null;
        var detail = agent->InventoryData;
        if (detail == null) return null;
        var vector = detail->Inventory;
        if (!ValidRange((nint)vector.First, (nint)vector.Last, (nint)vector.End, out _)) return null;
        return new((nint)player, player->ContentId, (nint)module, (nint)manager, (nint)agent,
            (nint)addon, agent->AddonId, (nint)detail, (nint)vector.First, (nint)vector.Last, (nint)vector.End);
    }

    internal static bool MatchesAddon(nint addon, uint addonId, string name,
        nint handleAddress, uint handleId, bool ready, bool visible) =>
        addon != 0 && addonId is > 0 and <= ushort.MaxValue && name == AddonName &&
        addon == handleAddress && addonId == handleId && ready && visible;

    internal static bool ValidRange(nint first, nint last, nint end, out int count)
    {
        count = 0;
        if (first <= 0 || last <= first || end < last || first % IntPtr.Size != 0) return false;
        var used = (ulong)(last - first);
        var capacity = (ulong)(end - first);
        var stride = (ulong)sizeof(AgentMJIPouch.PouchInventoryItem);
        if (used % stride != 0 || capacity % stride != 0 || capacity > MaxEntries * stride) return false;
        count = (int)(used / stride);
        return count is >= 1 and <= MaxEntries;
    }

    internal static IslandMaterialSample[]? Copy(IslandMaterialsOwner owner)
    {
        if (!ValidOwner(owner) || !ValidRange(owner.First, owner.Last, owner.End, out var count)) return null;
        var agent = (AgentMJIPouch*)owner.Agent;
        var addon = (AtkUnitBase*)owner.Addon;
        if (agent->AddonId != owner.AddonId || addon->Id != owner.AddonId || (nint)agent->InventoryData != owner.Data) return null;
        var detail = agent->InventoryData;
        var vector = detail->Inventory;
        if ((nint)vector.First != owner.First || (nint)vector.Last != owner.Last || (nint)vector.End != owner.End) return null;
        var result = new IslandMaterialSample[count];
        // Use the agent-owned contiguous vector only. Never follow category-list
        // pointers or read native names and allocation records. Unsupported rows
        // are excluded from the observation after this bounded primitive copy.
        for (var index = 0; index < count; index++)
        {
            ref var row = ref vector.First[index];
            result[index] = new(row.RowId, row.ItemId, row.IconId, row.StackSize, row.MaxStackSize,
                row.InventoryIndex, row.ItemCategory, row.IsUndiscovered);
        }
        if (agent->AddonId != owner.AddonId || addon->Id != owner.AddonId || agent->InventoryData != detail ||
            detail->Inventory.First != vector.First || detail->Inventory.Last != vector.Last || detail->Inventory.End != vector.End) return null;
        return result;
    }

    internal static IslandMaterialsObservation? CaptureStable(Func<IslandMaterialsOwner?> observe,
        Func<IslandMaterialsOwner, IslandMaterialSample[]?> copy, Func<uint, IslandMaterialDefinition?> definition,
        Func<bool> catalogMatches, IslandMaterialsOwner? initialOwner = null)
    {
        var before = initialOwner ?? observe();
        if (before is not { } owner || !ValidOwner(owner) || !ValidRange(owner.First, owner.Last, owner.End, out var count)) return null;
        if (!catalogMatches() || observe() != before) return null;
        var first = copy(owner);
        if (first is null || first.Length != count || observe() != before) return null;
        var observed = new List<(IslandMaterialSample Sample, IslandMaterialDefinition Definition)>();
        var seen = new HashSet<uint>();
        foreach (var row in first)
        {
            if (row.IsUndiscovered || row.InventoryIndex == 3 &&
                !FelicitousTokenIdentity(row.PouchRowId, row.ItemId, row.CategoryId)) continue;
            var resolved = definition(row.PouchRowId);
            // MJIKeyItem and unknown rows share this vector but not this grant.
            // Their row IDs may overlap MJIItemPouch, so both IDs must agree.
            if (resolved is not { } item || row.ItemId != item.ItemId || !SupportedDefinition(item)) continue;
            if (!ValidItem(row, item) || !seen.Add(item.ItemId)) return null;
            observed.Add((row, item));
        }
        // A populated source may contain no supported discovered rows. That is an
        // empty observed subset, not evidence that the island inventory is empty.
        if (!catalogMatches() || observe() != before) return null;
        var second = copy(owner);
        if (second is null || !first.SequenceEqual(second) || observe() != before) return null;
        foreach (var entry in observed)
            if (definition(entry.Sample.PouchRowId) != entry.Definition) return null;
        if (!catalogMatches() || observe() != before) return null;
        var entries = observed.OrderBy(entry => entry.Definition.PouchRowId).Select(entry => new IslandMaterialBalance(
            entry.Definition.ItemId, entry.Definition.Name.Trim(), entry.Sample.Quantity, entry.Definition.PouchRowId,
            entry.Definition.CategoryId, IsFelicitousToken(entry.Definition) && entry.Definition.CategoryName.Length == 0
                ? FelicitousTokenCategoryLabel : entry.Definition.CategoryName.Trim())).ToArray();
        return new(DateTimeOffset.UtcNow, Array.AsReadOnly(entries));
    }

    private static bool ValidOwner(IslandMaterialsOwner owner) => owner.Player != 0 && owner.ContentId != 0 &&
        owner.Module != 0 && owner.Manager != 0 && owner.Agent != 0 && owner.Addon != 0 && owner.Data != 0 &&
        owner.AddonId is > 0 and <= ushort.MaxValue;

    private static bool ValidText(string value, int maximum = 128) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);
    private static bool ValidItem(IslandMaterialSample row, IslandMaterialDefinition item) =>
        row.PouchRowId == item.PouchRowId && item.PouchRowId < MaxEntries && item.ItemId > 0 && item.Icon > 0 && row.Icon == item.Icon &&
        item.Maximum is > 0 and <= 999 && row.Maximum == item.Maximum && row.Quantity >= 0 && row.Quantity <= row.Maximum &&
        // InventoryIndex groups menu tabs; it is not the token's public identity.
        // Only the exact token may bypass that grouping restriction. Its native
        // IDs, icon, category, cap and quantity still must match public metadata.
        (row.InventoryIndex < 3 || IsFelicitousToken(item)) && row.CategoryId == item.CategoryId && ValidText(item.Name) &&
        (ValidText(item.CategoryName, 256) || IsFelicitousToken(item) && item.CategoryName.Length == 0);
}

internal sealed record IslandMaterialBalance(uint ItemId, string Name, int Quantity, uint PouchRowId,
    uint CategoryId, string CategoryName);
internal sealed record IslandMaterialsObservation(DateTimeOffset CapturedAtUtc, IReadOnlyList<IslandMaterialBalance> Items);
internal readonly record struct IslandMaterialDefinition(uint PouchRowId, uint ItemId, string Name, uint Icon,
    int Maximum, uint CategoryId, string CategoryName);
internal readonly record struct IslandMaterialsOwner(nint Player, ulong ContentId, nint Module, nint Manager,
    nint Agent, nint Addon, uint AddonId, nint Data, nint First, nint Last, nint End);
internal readonly record struct IslandMaterialSample(uint PouchRowId, uint ItemId, uint Icon, int Quantity, int Maximum,
    byte InventoryIndex, byte CategoryId, bool IsUndiscovered);
