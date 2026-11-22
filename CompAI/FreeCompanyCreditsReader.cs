using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace CompAI;

/// <summary>Framework-thread copy of the displayed FC Topics or item-shop credit balance. Never opens, refreshes or transacts.</summary>
internal sealed unsafe class FreeCompanyCreditsReader(IDataManager data, IGameGui gui)
{
    internal const string AddonName = "FreeCompanyCreditShop";
    internal const int MaximumOffers = 20;
    internal const int MaximumValues = 4096;

    private readonly FreeCompanyTopicsCreditsReader topics = new(gui);

    internal FreeCompanyCreditsObservation? Capture() => topics.Capture() ?? CaptureShop();

    private FreeCompanyCreditsObservation? CaptureShop()
    {
        try
        {
            var before = Observe();
            if (before is null) return null;
            var game = data.GameData;
            var language = data.Language;
            var shops = data.GetExcelSheet<FccShop>();
            var items = data.GetExcelSheet<Item>();
            var ranks = data.GetExcelSheet<FCRank>();
            var offers = shops.SelectMany(shop => shop.ItemData).Where(item => item.Item.RowId > 0)
                .Select(item => (item.Item.RowId, item.Cost)).ToHashSet();
            bool CatalogMatches() => ReferenceEquals(game, data.GameData) && language == data.Language &&
                ReferenceEquals(shops.RawSheet, data.GetExcelSheet<FccShop>().RawSheet) &&
                ReferenceEquals(items.RawSheet, data.GetExcelSheet<Item>().RawSheet) &&
                ReferenceEquals(ranks.RawSheet, data.GetExcelSheet<FCRank>().RawSheet);
            bool OfferExists(FreeCompanyCreditOffer offer)
            {
                if (!offers.Contains((offer.ItemId, offer.Cost)) || !items.TryGetRow(offer.ItemId, out var item)) return false;
                var name = item.Name.ExtractText().Trim();
                return !string.IsNullOrWhiteSpace(name) && name.Length <= 128 && !name.Any(char.IsControl);
            }
            return CaptureStable(Observe, Copy, rank => rank > 0 && ranks.TryGetRow(rank, out _),
                OfferExists, CatalogMatches, before);
        }
        catch (Exception) { return null; }
    }

    private FreeCompanyCreditsOwner? Observe()
    {
        var player = PlayerState.Instance();
        var agents = AgentModule.Instance();
        var addons = RaptureAtkUnitManager.Instance();
        if (!Aligned((nint)player) || !Aligned((nint)agents) || !Aligned((nint)addons) ||
            !player->IsLoaded || player->ContentId == 0) return null;
        var agent = agents->GetAgentByInternalId(AgentId.FreeCompanyCreditShop);
        if (!Aligned((nint)agent) || agent->AddonId is 0 or > ushort.MaxValue ||
            !agent->IsAgentActive() || !agent->IsAddonReady() || !agent->IsAddonShown()) return null;
        var addon = addons->GetAddonById((ushort)agent->AddonId);
        if (!Aligned((nint)addon) || addon->Id != agent->AddonId || addon->NameString != AddonName) return null;
        var handle = gui.GetAddonByName(AddonName);
        if (handle.IsNull || !MatchesAddon((nint)addon, agent->AddonId, addon->NameString,
                handle.Address, handle.Id, handle.IsReady, handle.IsVisible)) return null;
        var values = addon->AtkValues;
        if (!Aligned((nint)values) || addon->AtkValuesCount is < 10 or > MaximumValues ||
            values[0].Type != AtkValueType.UInt || values[3].Type != AtkValueType.UInt ||
            values[9].Type != AtkValueType.UInt) return null;
        var owner = new FreeCompanyCreditsOwner((nint)player, player->ContentId, (nint)agents,
            (nint)addons, (nint)agent, (nint)addon, agent->AddonId, (nint)values, addon->AtkValuesCount, values[9].UInt);
        return ValidOwner(owner) ? owner : null;
    }

    internal static bool Aligned(nint address) => address > 0 && address % IntPtr.Size == 0;
    internal static bool MatchesAddon(nint addon, uint addonId, string name, nint handle,
        uint handleId, bool ready, bool visible) => Aligned(addon) && addonId is > 0 and <= ushort.MaxValue &&
        name == AddonName && addon == handle && addonId == handleId && ready && visible;
    internal static bool ValidOwner(FreeCompanyCreditsOwner owner) => Aligned(owner.Player) && owner.ContentId != 0 &&
        Aligned(owner.Agents) && Aligned(owner.Addons) && Aligned(owner.Agent) && Aligned(owner.Addon) &&
        owner.AddonId is > 0 and <= ushort.MaxValue && Aligned(owner.Values) &&
        owner.OfferCount is >= 1 and <= MaximumOffers && owner.ValueCount >= 130 + owner.OfferCount &&
        owner.ValueCount <= MaximumValues;

    private static bool NativeMatches(FreeCompanyCreditsOwner owner)
    {
        var player = (PlayerState*)owner.Player;
        var agent = (AgentInterface*)owner.Agent;
        var addon = (AtkUnitBase*)owner.Addon;
        return player->IsLoaded && player->ContentId == owner.ContentId && agent->AddonId == owner.AddonId &&
            addon->Id == owner.AddonId && (nint)addon->AtkValues == owner.Values &&
            addon->AtkValuesCount == owner.ValueCount && addon->AtkValues[9].Type == AtkValueType.UInt &&
            addon->AtkValues[9].UInt == owner.OfferCount;
    }

    internal static FreeCompanyCreditsSample? Copy(FreeCompanyCreditsOwner owner)
    {
        if (!ValidOwner(owner) || !NativeMatches(owner)) return null;
        var values = (AtkValue*)owner.Values;
        // Documented fields only. Inventory quantities and transaction controls are deliberately not read.
        if (values[0].Type != AtkValueType.UInt || values[3].Type != AtkValueType.UInt) return null;
        var rank = values[0].UInt;
        var credits = values[3].UInt;
        var rows = new FreeCompanyCreditOffer[owner.OfferCount];
        for (var index = 0; index < rows.Length; index++)
        {
            if (values[30 + index].Type != AtkValueType.UInt || values[130 + index].Type != AtkValueType.UInt) return null;
            rows[index] = new(values[30 + index].UInt, values[130 + index].UInt);
        }
        return NativeMatches(owner) ? new(rank, credits, rows) : null;
    }

    internal static FreeCompanyCreditsObservation? CaptureStable(Func<FreeCompanyCreditsOwner?> observe,
        Func<FreeCompanyCreditsOwner, FreeCompanyCreditsSample?> copy, Func<uint, bool> rankExists,
        Func<FreeCompanyCreditOffer, bool> offerExists, Func<bool> catalogMatches,
        FreeCompanyCreditsOwner? initialOwner = null)
    {
        var before = initialOwner ?? observe();
        if (before is not { } owner || !ValidOwner(owner) || !catalogMatches() || observe() != before) return null;
        var first = copy(owner);
        if (!ValidSample(first, owner.OfferCount) || observe() != before || !rankExists(first!.Rank) ||
            first.Offers.Any(offer => !offerExists(offer))) return null;
        if (!catalogMatches() || observe() != before || !SameSample(first, copy(owner), owner.OfferCount) || observe() != before) return null;
        if (!rankExists(first.Rank) || first.Offers.Any(offer => !offerExists(offer)) || !catalogMatches() || observe() != before) return null;
        return new(DateTimeOffset.UtcNow, first.Credits);
    }

    private static bool ValidSample(FreeCompanyCreditsSample? sample, uint count) => sample is not null &&
        sample.Rank > 0 && sample.Offers is not null && sample.Offers.Length == count && sample.Offers.All(row => row.ItemId > 0);
    private static bool SameSample(FreeCompanyCreditsSample first, FreeCompanyCreditsSample? second, uint count) =>
        ValidSample(second, count) && first.Rank == second!.Rank && first.Credits == second.Credits && first.Offers.SequenceEqual(second.Offers);
}

internal enum FreeCompanyCreditsSource { ItemShop, Topics }
internal sealed record FreeCompanyCreditsObservation(DateTimeOffset CapturedAtUtc, uint Credits,
    FreeCompanyCreditsSource Source = FreeCompanyCreditsSource.ItemShop);
internal readonly record struct FreeCompanyCreditsOwner(nint Player, ulong ContentId, nint Agents, nint Addons,
    nint Agent, nint Addon, uint AddonId, nint Values, ushort ValueCount, uint OfferCount);
internal readonly record struct FreeCompanyCreditOffer(uint ItemId, uint Cost);
internal sealed record FreeCompanyCreditsSample(uint Rank, uint Credits, FreeCompanyCreditOffer[] Offers);

/// <summary>Only the confirmed root Topics text node, never a recursive numeric-text search.</summary>
internal sealed unsafe class FreeCompanyTopicsCreditsReader(IGameGui gui)
{
    internal const string AddonName = "FreeCompany";
    internal const int MaximumNodes = 512;
    internal const int MaximumAncestors = 32;
    internal const int MaximumTextBytes = 13; // 4,294,967,295: UInt32 with grouping.

    internal FreeCompanyCreditsObservation? Capture()
    {
        try { return CaptureStable(Observe, Copy); }
        catch (Exception) { return null; }
    }

    private FreeCompanyTopicsCreditsOwner? Observe()
    {
        var player = PlayerState.Instance();
        var info = InfoModule.Instance();
        var agents = AgentModule.Instance();
        var addons = RaptureAtkUnitManager.Instance();
        if (!Aligned((nint)player) || !Aligned((nint)info) || !Aligned((nint)agents) || !Aligned((nint)addons) ||
            !player->IsLoaded || player->ContentId == 0 || info->LocalContentId != player->ContentId) return null;
        var agent = (AgentFreeCompany*)agents->GetAgentByInternalId(AgentId.FreeCompany);
        if (!Aligned((nint)agent) || agent != AgentFreeCompany.Instance() || agent->AddonId is 0 or > ushort.MaxValue ||
            !agent->IsAgentActive() || !agent->IsAddonReady() || !agent->IsAddonShown()) return null;
        var inspect = AgentInspect.Instance();
        var inspectedCompany = AgentFreeCompanyProfile.Instance();
        if (inspect != null && (!Aligned((nint)inspect) || inspect->IsAgentActive()) ||
            inspectedCompany != null && (!Aligned((nint)inspectedCompany) || inspectedCompany->IsAgentActive())) return null;
        var profile = agent->InfoProxyFreeCompany;
        var members = agent->InfoProxyFreeCompanyMember;
        // Same registered-proxy identity gates as the main FC reader/diagnostic. No roster is read.
        if (!Aligned((nint)profile) || !Aligned((nint)members) || profile->Id == 0 ||
            profile->Id != members->FreeCompanyId || profile->ActiveListItemNum != 0 ||
            (nint)profile != (nint)info->GetInfoProxyById(InfoProxyId.FreeCompany) ||
            (nint)members != (nint)info->GetInfoProxyById(InfoProxyId.FreeCompanyMember)) return null;
        var addon = addons->GetAddonById((ushort)agent->AddonId);
        if (!Aligned((nint)addon) || addon->Id != agent->AddonId || addon->NameString != AddonName) return null;
        var handle = gui.GetAddonByName(AddonName);
        if (handle.IsNull || !MatchesAddon((nint)addon, agent->AddonId, addon->NameString,
                handle.Address, handle.Id, handle.IsReady, handle.IsVisible)) return null;
        var owner = new FreeCompanyTopicsCreditsOwner((nint)player, player->ContentId, (nint)info, (nint)agents,
            (nint)addons, (nint)agent, (nint)profile, (nint)members, profile->Id, profile->ActiveListItemNum,
            (nint)addon, agent->AddonId, (nint)addon->RootNode, (nint)addon->UldManager.NodeList, addon->UldManager.NodeListCount);
        return ValidOwner(owner) && NativeMatches(owner) ? owner : null;
    }

    private static bool Aligned(nint address) => FreeCompanyCreditsReader.Aligned(address);
    internal static bool MatchesAddon(nint addon, uint addonId, string name, nint handle,
        uint handleId, bool ready, bool visible) => Aligned(addon) && addonId is > 0 and <= ushort.MaxValue &&
        name == AddonName && addon == handle && addonId == handleId && ready && visible;

    internal static bool ValidOwner(FreeCompanyTopicsCreditsOwner owner) =>
        Aligned(owner.Player) && owner.ContentId != 0 && Aligned(owner.Info) && Aligned(owner.Agents) &&
        Aligned(owner.Addons) && Aligned(owner.Agent) && Aligned(owner.Profile) && Aligned(owner.Members) &&
        owner.CompanyId != 0 && owner.SelectedTab == 0 && Aligned(owner.Addon) && owner.AddonId is > 0 and <= ushort.MaxValue &&
        Aligned(owner.Root) && Aligned(owner.NodeList) && owner.NodeCount is >= 1 and <= MaximumNodes;

    private static bool NativeMatches(FreeCompanyTopicsCreditsOwner owner)
    {
        var player = (PlayerState*)owner.Player;
        var info = (InfoModule*)owner.Info;
        var agent = (AgentFreeCompany*)owner.Agent;
        var profile = (InfoProxyFreeCompany*)owner.Profile;
        var members = (InfoProxyFreeCompanyMember*)owner.Members;
        var addon = (AtkUnitBase*)owner.Addon;
        return player->IsLoaded && player->ContentId == owner.ContentId && info->LocalContentId == owner.ContentId &&
            agent->AddonId == owner.AddonId && (nint)agent->InfoProxyFreeCompany == owner.Profile &&
            (nint)agent->InfoProxyFreeCompanyMember == owner.Members && profile->Id == owner.CompanyId &&
            members->FreeCompanyId == owner.CompanyId && profile->ActiveListItemNum == owner.SelectedTab &&
            addon->Id == owner.AddonId && addon->NameString == AddonName && addon->IsReady && addon->IsVisible && addon->Alpha > 0 &&
            (nint)addon->RootNode == owner.Root && (nint)addon->UldManager.NodeList == owner.NodeList &&
            addon->UldManager.NodeListCount == owner.NodeCount;
    }

    internal static FreeCompanyTopicsCreditsSample? Copy(FreeCompanyTopicsCreditsOwner owner)
    {
        if (!ValidOwner(owner) || !NativeMatches(owner)) return null;
        var list = (AtkResNode**)owner.NodeList;
        var addresses = new nint[owner.NodeCount];
        var owned = new HashSet<nint>();
        AtkTextNode* field = null;
        for (var index = 0; index < addresses.Length; ++index)
        {
            var address = addresses[index] = (nint)list[index];
            if (address == 0) continue;
            if (!Aligned(address) || !owned.Add(address)) return null;
            var node = (AtkResNode*)address;
            if (node->NodeId != 17) continue;
            if (field != null || node->Type != NodeType.Text) return null;
            field = (AtkTextNode*)node;
        }
        if (field == null || (nint)field == owner.Root || !owned.Contains(owner.Root) || !owned.Contains((nint)field->ParentNode) ||
            field->ParentNode->NodeId != 16) return null;
        var ancestors = new List<FreeCompanyTopicsNode>();
        var seen = new HashSet<nint>();
        var current = (AtkResNode*)field;
        for (var depth = 0; depth < MaximumAncestors && current != null; ++depth)
        {
            if (!owned.Contains((nint)current) || !seen.Add((nint)current)) return null;
            var node = Node(current);
            if ((node.Flags & NodeFlags.Visible) == 0 || node.Alpha == 0 || node.DrawDisabled) return null;
            ancestors.Add(node);
            if ((nint)current == owner.Root) break;
            current = current->ParentNode;
        }
        if (ancestors.Count == 0 || ancestors[^1].Address != owner.Root) return null;
        var buffer = (byte*)field->NodeText.StringPtr;
        var used = field->NodeText.BufUsed;
        var size = field->NodeText.BufSize;
        // BufUsed includes NUL. Byte text pointers need not be word-aligned (inline Utf8String storage).
        if ((nint)buffer < 65536 || used is < 2 or > MaximumTextBytes + 1 || size < used) return null;
        var bytes = new ReadOnlySpan<byte>(buffer, (int)used).ToArray();
        if (bytes[^1] != 0 || !TryParseBalance(bytes.AsSpan(0, bytes.Length - 1), out var credits)) return null;
        if (!NativeMatches(owner) || (byte*)field->NodeText.StringPtr != buffer || field->NodeText.BufUsed != used ||
            field->NodeText.BufSize != size || !new ReadOnlySpan<byte>(buffer, (int)used).SequenceEqual(bytes)) return null;
        for (var index = 0; index < addresses.Length; ++index)
            if ((nint)list[index] != addresses[index]) return null;
        foreach (var node in ancestors)
            if (Node((AtkResNode*)node.Address) != node) return null;
        return new(credits, (nint)buffer, used, size, Convert.ToHexString(bytes), addresses, ancestors.ToArray());
    }

    internal static bool TryParseBalance(ReadOnlySpan<byte> text, out uint balance)
    {
        balance = 0;
        if (text.Length is < 1 or > MaximumTextBytes || text.Length > 1 && text[0] == (byte)'0') return false;
        var grouped = text.Contains((byte)',');
        var digits = 0;
        var groups = 0;
        uint result = 0;
        foreach (var character in text)
        {
            if (character == (byte)',' && grouped)
            {
                if (groups == 0 ? digits is < 1 or > 3 : digits != 3) return false;
                ++groups; digits = 0; continue;
            }
            if (character is < (byte)'0' or > (byte)'9') return false;
            var digit = (uint)(character - (byte)'0');
            if (result > (uint.MaxValue - digit) / 10) return false;
            result = result * 10 + digit; ++digits;
        }
        if (grouped && digits != 3) return false;
        balance = result;
        return true;
    }

    private static FreeCompanyTopicsNode Node(AtkResNode* node) => new((nint)node, node->NodeId, (nint)node->ParentNode,
        node->Type, node->NodeFlags, node->Color.A, node->IsDrawDisabled);

    internal static FreeCompanyCreditsObservation? CaptureStable(Func<FreeCompanyTopicsCreditsOwner?> observe,
        Func<FreeCompanyTopicsCreditsOwner, FreeCompanyTopicsCreditsSample?> copy)
    {
        var before = observe();
        if (before is not { } owner || !ValidOwner(owner) || observe() != before) return null;
        var first = copy(owner);
        if (first is null || observe() != before) return null;
        var second = copy(owner);
        if (second is null || observe() != before || first.Credits != second.Credits || first.Buffer != second.Buffer ||
            first.Used != second.Used || first.Size != second.Size || first.Bytes != second.Bytes ||
            !first.NodeList.SequenceEqual(second.NodeList) || !first.Ancestors.SequenceEqual(second.Ancestors)) return null;
        return new(DateTimeOffset.UtcNow, first.Credits, FreeCompanyCreditsSource.Topics);
    }
}

internal readonly record struct FreeCompanyTopicsCreditsOwner(nint Player, ulong ContentId, nint Info, nint Agents,
    nint Addons, nint Agent, nint Profile, nint Members, ulong CompanyId, byte SelectedTab, nint Addon,
    uint AddonId, nint Root, nint NodeList, ushort NodeCount);
internal readonly record struct FreeCompanyTopicsNode(nint Address, uint Id, nint Parent, NodeType Type,
    NodeFlags Flags, byte Alpha, bool DrawDisabled);
internal sealed record FreeCompanyTopicsCreditsSample(uint Credits, nint Buffer, long Used, long Size, string Bytes,
    nint[] NodeList, FreeCompanyTopicsNode[] Ancestors);
