using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using CabinetRow = Lumina.Excel.Sheets.Cabinet;
using NativeCabinet = FFXIVClientStructs.FFXIV.Client.Game.UI.Cabinet;

namespace CompAI;

/// <summary>Framework-thread only. Reads loaded storage membership and saved plate references; never opens or changes UI.</summary>
internal sealed unsafe class GlamourReader(IDataManager data)
{
    internal const int DresserSlots = 800;
    internal const int PlateCount = 20;
    internal const int PlateSlots = 12;
    internal const int OutfitSlots = 11;
    internal const int MaxCabinetRows = 8192;
    internal const int MaxOutfitRows = 4096;
    private const string ArmoireSource = "Loaded armoire storage membership";
    private const string DresserSource = "Loaded glamour dresser storage slots and outfit-piece membership";
    private const string PlateSource = "Loaded saved glamour plate references";

    internal MenuSnapshot Capture(string category)
    {
        if (category is not ("armoire" or "glamour_dresser" or "glamour_plates"))
            throw new ArgumentException("Unsupported glamour category.", nameof(category));
        try
        {
            var player = PlayerState.Instance();
            if (player == null || !player->IsLoaded || player->ContentId == 0)
                return Unavailable(category, "Current character data is not loaded.");
            var owner = player->ContentId;
            var gameData = data.GameData;
            var language = data.Language.ToString();
            var items = data.GetExcelSheet<Item>();
            string? Name(uint id) => items.TryGetRow(id, out var item) ? item.Name.ExtractText().Trim() : null;
            var captured = DateTimeOffset.UtcNow;
            var result = category switch
            {
                "armoire" => CaptureArmoire(captured, Name),
                "glamour_dresser" => CaptureDresser(captured, Name),
                _ => CapturePlates(captured, Name),
            };
            if (PlayerState.Instance() != player || !player->IsLoaded || player->ContentId != owner ||
                !ReferenceEquals(gameData, data.GameData) || language != data.Language.ToString() ||
                !ReferenceEquals(items.RawSheet, data.GetExcelSheet<Item>().RawSheet))
                return Unavailable(category, "Character or public item definitions changed during reading; try again after loading.");
            return result;
        }
        catch (Exception ex)
        {
            return Unavailable(category, $"Glamour read unavailable ({ex.GetType().Name}).");
        }
    }

    private MenuSnapshot CaptureArmoire(DateTimeOffset captured, Func<uint, string?> name)
    {
        var ui = UIState.Instance();
        var agent = OpenCabinetAgent();
        if (ui == null || agent == null || !CabinetReady(&ui->Cabinet))
            return Unavailable("armoire", "Open the armoire storage or withdrawal window and wait for it to load.");
        var addon = VisibleAddon(agent);
        var sheet = data.GetExcelSheet<CabinetRow>();
        var rows = sheet.Take(MaxCabinetRows + 1).ToArray();
        if (rows.Length is 0 or > MaxCabinetRows || rows.Any(row => row.RowId >= MaxCabinetRows))
            return Unavailable("armoire", "The public armoire catalog exceeds supported bounds.");
        // Cabinet row zero is a real stored-item flag, unlike many other sheets' sentinel row zero.
        var definitions = rows.Where(row => row.Item.RowId != 0)
            .Select(row => new GlamourCabinetDefinition(row.RowId, row.Item.RowId)).ToArray();
        var first = ReadCabinet(&ui->Cabinet, definitions);
        var result = DescribeArmoire(captured, definitions, first, first, name);
        if (UIState.Instance() != ui || !CabinetReady(&ui->Cabinet) || OpenCabinetAgent() != agent ||
            VisibleAddon(agent) != addon || !ReferenceEquals(sheet.RawSheet, data.GetExcelSheet<CabinetRow>().RawSheet) ||
            !first.AsSpan().SequenceEqual(ReadCabinet(&ui->Cabinet, definitions)))
            return Unavailable("armoire", "The armoire menu or public catalog changed during reading.");
        return result;
    }

    private MenuSnapshot CaptureDresser(DateTimeOffset captured, Func<uint, string?> name)
    {
        var manager = MirageManager.Instance();
        var agent = AgentMiragePrismPrismBox.Instance();
        if (!DresserReady(manager, agent))
            return Unavailable("glamour_dresser", "Open the glamour dresser and wait for its contents to finish loading.");
        var addon = VisibleAddon(&agent->AgentInterface);
        var source = agent->Data;
        var sheet = data.GetExcelSheet<MirageStoreSetItem>();
        var rows = sheet.Take(MaxOutfitRows + 1).ToArray();
        if (rows.Length is 0 or > MaxOutfitRows)
            return Unavailable("glamour_dresser", "The public outfit catalog exceeds supported bounds.");
        // Installed ClientStructs dd57986b: IsSetSlotUnlocked uses the eleven MirageStoreSetItem columns.
        // Calling this read-only helper avoids guessing the partial-outfit stain-byte encoding.
        var outfits = rows.Where(row => row.RowId != 0).Select(row => new GlamourOutfitDefinition(row.RowId,
            [row.MainHand.RowId, row.OffHand.RowId, row.Head.RowId, row.Body.RowId, row.Hands.RowId,
             row.Legs.RowId, row.Feet.RowId, row.Earrings.RowId, row.Necklace.RowId, row.Bracelets.RowId, row.Ring.RowId]))
            .ToArray();
        var byId = outfits.ToDictionary(row => row.ItemId);
        var first = ReadDresser(manager, byId);
        var result = DescribeDresser(captured, outfits, first, first, name);
        if (MirageManager.Instance() != manager || AgentMiragePrismPrismBox.Instance() != agent ||
            !DresserReady(manager, agent) || agent->Data != source || VisibleAddon(&agent->AgentInterface) != addon ||
            !ReferenceEquals(sheet.RawSheet, data.GetExcelSheet<MirageStoreSetItem>().RawSheet) ||
            !first.AsSpan().SequenceEqual(ReadDresser(manager, byId)))
            return Unavailable("glamour_dresser", "The dresser menu or public outfit catalog changed during reading.");
        return result;
    }

    private static MenuSnapshot CapturePlates(DateTimeOffset captured, Func<uint, string?> name)
    {
        var manager = MirageManager.Instance();
        var agent = AgentMiragePrismMiragePlate.Instance();
        if (!PlatesReady(manager, agent))
            return Unavailable("glamour_plates", "Open the glamour plate editor, wait for it to load, and save or discard pending edits first.");
        var addon = VisibleAddon(&agent->AgentInterface);
        var source = agent->Data;
        var first = ReadPlates(manager);
        var result = DescribePlates(captured, first, first, name);
        if (MirageManager.Instance() != manager || AgentMiragePrismMiragePlate.Instance() != agent ||
            !PlatesReady(manager, agent) || agent->Data != source || VisibleAddon(&agent->AgentInterface) != addon ||
            !first.AsSpan().SequenceEqual(ReadPlates(manager)))
            return Unavailable("glamour_plates", "The saved glamour plates or editor changed during reading.");
        return result;
    }

    private static AtkUnitBase* VisibleAddon(AgentInterface* agent)
    {
        if (agent == null || !agent->IsAgentActive() || agent->AddonId is 0 or > ushort.MaxValue) return null;
        var stage = AtkStage.Instance();
        if (stage == null || stage->RaptureAtkUnitManager == null) return null;
        var addon = stage->RaptureAtkUnitManager->GetAddonById((ushort)agent->AddonId);
        return addon != null && addon->IsReady && addon->IsVisible && addon->Alpha != 0 ? addon : null;
    }

    private static AgentInterface* OpenCabinetAgent()
    {
        var withdraw = AgentCabinetWithdraw.Instance();
        if (withdraw != null && withdraw->Data != null && VisibleAddon(&withdraw->AgentInterface) != null)
            return &withdraw->AgentInterface;
        var store = AgentCabinet.Instance();
        return store != null && !store->PendingUpdate && VisibleAddon(&store->AgentInterface) != null ? &store->AgentInterface : null;
    }

    private static bool CabinetReady(NativeCabinet* cabinet) => cabinet != null && cabinet->IsCabinetLoaded() &&
        cabinet->UnlockedItems.LongCount is > 0 and <= MaxCabinetRows;

    private static bool DresserReady(MirageManager* manager, AgentMiragePrismPrismBox* agent) =>
        manager != null && manager->PrismBoxLoaded && manager->PrismBoxItemIds.Length == DresserSlots &&
        agent != null && VisibleAddon(&agent->AgentInterface) != null && agent->IsDataLoaded && agent->Data != null &&
        agent->Data->IsAddonReady && agent->Data->IsAsyncLoadComplete &&
        agent->Data->IsPopulatingComplete && !agent->Data->IsPopulatingList;

    private static bool PlatesReady(MirageManager* manager, AgentMiragePrismMiragePlate* agent) =>
        manager != null && manager->GlamourPlatesLoaded && !manager->IsApplyingGlamourPlate &&
        manager->GlamourPlates.Length == PlateCount && agent != null &&
        VisibleAddon(&agent->AgentInterface) != null && agent->Data != null && !agent->Data->HasChanges;

    private static bool[] ReadCabinet(NativeCabinet* cabinet, GlamourCabinetDefinition[] definitions) =>
        definitions.Select(row => cabinet->IsItemInCabinet(row.CabinetId)).ToArray();

    private static GlamourStorageSlot[] ReadDresser(MirageManager* manager, IReadOnlyDictionary<uint, GlamourOutfitDefinition> outfits)
    {
        var rows = new GlamourStorageSlot[DresserSlots];
        for (var slot = 0; slot < rows.Length; slot++)
        {
            var rawId = manager->PrismBoxItemIds[slot];
            ushort mask = 0;
            if (outfits.TryGetValue(BaseItemId(rawId), out var outfit))
                for (var column = 0; column < OutfitSlots; column++)
                    if (outfit.Pieces[column] != 0 && manager->IsSetSlotUnlocked((uint)slot, column))
                        mask |= (ushort)(1 << column);
            rows[slot] = new(rawId, mask);
        }
        return rows;
    }

    private static uint[] ReadPlates(MirageManager* manager)
    {
        var rows = new uint[PlateCount * PlateSlots];
        for (var plate = 0; plate < PlateCount; plate++)
        {
            if (manager->GlamourPlates[plate].ItemIds.Length != PlateSlots)
                throw new InvalidOperationException("Unsupported glamour plate bounds.");
            manager->GlamourPlates[plate].ItemIds.CopyTo(rows.AsSpan(plate * PlateSlots, PlateSlots));
        }
        return rows;
    }

    internal static MenuSnapshot DescribeArmoire(DateTimeOffset captured, GlamourCabinetDefinition[] definitions,
        ReadOnlySpan<bool> first, ReadOnlySpan<bool> second, Func<uint, string?> name, bool ready = true)
    {
        if (!ready || definitions.Length is 0 or > MaxCabinetRows || first.Length != definitions.Length ||
            second.Length != definitions.Length || !first.SequenceEqual(second) ||
            definitions.Any(row => row.CabinetId >= MaxCabinetRows || !ValidItemId(row.ItemId)) ||
            definitions.Select(row => row.CabinetId).Distinct().Count() != definitions.Length)
            return Unavailable("armoire", "Armoire readiness, catalog bounds or stored membership changed during reading.");
        var entries = new List<MenuEntry>();
        for (var index = 0; index < definitions.Length; index++)
        {
            if (!first[index]) continue;
            var row = definitions[index];
            var label = name(row.ItemId);
            if (!MenuSnapshot.ValidText(label, 128) || entries.Count >= MenuSnapshot.MaxEntries)
                return Unavailable("armoire", "An observed armoire item's public name or capture bounds are unsupported.");
            entries.Add(new((uint)entries.Count + 1, row.ItemId, label!, null, "stored", "Armoire storage membership",
                null, (int)row.CabinetId, null, []));
        }
        return new("armoire", captured, true, ArmoireSource,
            "open_loaded_positive_membership_only; unobserved items and unlock eligibility remain unknown", entries);
    }

    internal static MenuSnapshot DescribeDresser(DateTimeOffset captured, GlamourOutfitDefinition[] outfits,
        ReadOnlySpan<GlamourStorageSlot> first, ReadOnlySpan<GlamourStorageSlot> second, Func<uint, string?> name, bool ready = true)
    {
        if (!ready || first.Length != DresserSlots || second.Length != DresserSlots || !first.SequenceEqual(second) ||
            outfits.Length > MaxOutfitRows || outfits.Any(row => !ValidItemId(row.ItemId) || row.Pieces.Length != OutfitSlots ||
                row.Pieces.Any(id => id != 0 && !ValidItemId(id))) || outfits.Select(row => row.ItemId).Distinct().Count() != outfits.Length)
            return Unavailable("glamour_dresser", "Dresser readiness, outfit catalog or storage observations changed during reading.");
        var byId = outfits.ToDictionary(row => row.ItemId);
        var observed = new HashSet<uint>();
        var entries = new List<MenuEntry>();
        for (var slot = 0; slot < first.Length; slot++)
        {
            var row = first[slot];
            if (row.RawItemId == 0)
            {
                if (row.OutfitMask != 0) return Unavailable("glamour_dresser", "An empty dresser slot has unsupported outfit state.");
                continue;
            }
            var itemId = BaseItemId(row.RawItemId);
            if (!ValidItemId(itemId) || row.RawItemId >= 2_000_000 || row.OutfitMask >= (1 << OutfitSlots))
                return Unavailable("glamour_dresser", "A dresser item or outfit state is unsupported.");
            if (byId.TryGetValue(itemId, out var outfit))
            {
                if (!outfit.Pieces.Any(id => id != 0))
                    return Unavailable("glamour_dresser", "A stored outfit has no readable public piece mapping.");
                for (var column = 0; column < OutfitSlots; column++)
                {
                    if ((row.OutfitMask & (1 << column)) == 0) continue;
                    var piece = outfit.Pieces[column];
                    if (piece == 0 || !AddStored(piece, $"Glamour dresser; observed outfit piece in storage slot {slot + 1}"))
                        return Unavailable("glamour_dresser", "A stored outfit piece's public name or mapping is unsupported.");
                }
            }
            else if (row.OutfitMask != 0 || !AddStored(itemId, $"Glamour dresser; observed storage slot {slot + 1}"))
                return Unavailable("glamour_dresser", "A stored dresser item's public name or mapping is unsupported.");
        }
        return new("glamour_dresser", captured, true, DresserSource,
            "open_loaded_all_800_storage_slots; positive outfit pieces only; duplicate appearance membership collapsed; absent pieces unknown", entries);

        bool AddStored(uint id, string location)
        {
            if (observed.Contains(id)) return true;
            var label = name(id);
            if (!MenuSnapshot.ValidText(label, 128) || entries.Count >= MenuSnapshot.MaxEntries) return false;
            observed.Add(id);
            entries.Add(new((uint)entries.Count + 1, id, label!, null, "stored", location, null, null, null, []));
            return true;
        }
    }

    internal static MenuSnapshot DescribePlates(DateTimeOffset captured, ReadOnlySpan<uint> first,
        ReadOnlySpan<uint> second, Func<uint, string?> name, bool ready = true)
    {
        if (!ready || first.Length != PlateCount * PlateSlots || second.Length != first.Length || !first.SequenceEqual(second))
            return Unavailable("glamour_plates", "Saved plate readiness, bounds or references changed during reading.");
        var entries = new List<MenuEntry>();
        for (var index = 0; index < first.Length; index++)
        {
            if (first[index] == 0) continue;
            var itemId = BaseItemId(first[index]);
            var label = name(itemId);
            if (first[index] >= 2_000_000 || !ValidItemId(itemId) || !MenuSnapshot.ValidText(label, 128))
                return Unavailable("glamour_plates", "A saved plate reference has no supported public item mapping.");
            var plate = index / PlateSlots;
            var slot = index % PlateSlots;
            entries.Add(new((uint)entries.Count + 1, itemId, label!, null, "appearance_reference",
                $"Saved glamour plate {plate + 1}, appearance slot {slot + 1}", plate, slot, null, [],
                MenuSnapshot.PlateReferenceNotice));
        }
        return new("glamour_plates", captured, true, PlateSource,
            "open_loaded_saved_references_only; no pending edits; empty slots do not establish missing items", entries);
    }

    private static uint BaseItemId(uint id) => id >= 1_000_000 ? id - 1_000_000 : id;
    private static bool ValidItemId(uint id) => id is > 0 and < 1_000_000;
    private static MenuSnapshot Unavailable(string category, string reason) => MenuSnapshot.Unavailable(category,
        category == "armoire" ? ArmoireSource : category == "glamour_dresser" ? DresserSource : PlateSource, reason);
}

internal sealed record GlamourCabinetDefinition(uint CabinetId, uint ItemId);
internal sealed record GlamourOutfitDefinition(uint ItemId, uint[] Pieces);
internal readonly record struct GlamourStorageSlot(uint RawItemId, ushort OutfitMask);
