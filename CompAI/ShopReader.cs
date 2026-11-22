using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace CompAI;

/// <summary>Framework-thread only. Reads already open supported shops; never invokes a transaction or opens UI.</summary>
internal sealed unsafe class ShopReader(IDataManager data, IGameGui gui)
{
    internal const string Category = "shops";
    internal const string Source = "Visible ready gil/item/currency/Scrip/FC-item shop; observed offer fields and local Item sheet";
    internal const int MaxOffers = 1024;
    private static readonly string[] AddonNames = ["ShopExchangeItem", "ShopExchangeCurrency"];
    private readonly Dictionary<uint, uint> currencyIcons = new();
    private object? currencyGameData;
    private HashSet<(uint ItemId, uint Cost)>? companyCreditPrices;

    internal MenuSnapshot Capture()
    {
        try
        {
            var before = Observe();
            if (!before.Ready) return Unavailable("Open a gil shop's Buy tab, supported item exchange, Scrip Exchange category or FC credit item shop. GC seal and FC action exchange layouts are not supported yet.");
            var captured = DateTimeOffset.UtcNow;
            var rows = CopyObservedRows(before);
            var language = data.Language;
            var gameData = data.GameData;
            var items = data.GetExcelSheet<Item>();
            string? Name(uint id)
            {
                if (id == 0 || !items.TryGetRow(id, out var item)) return null;
                var name = item.Name.ExtractText().Trim();
                return MenuSnapshot.ValidText(name, 128) ? name : null;
            }
            // Cache only public Item-sheet icon mappings. Duplicate icons stay unresolved.
            if (!ReferenceEquals(currencyGameData, gameData))
            { currencyIcons.Clear(); companyCreditPrices = null; currencyGameData = gameData; }
            if (before.Kind == "currency_exchange")
            {
                var icon = before.CurrencyIcon;
                if (!currencyIcons.TryGetValue(icon, out var currency))
                {
                    var matches = items.Where(item => item.Icon == icon && icon > 0).Take(2).Select(item => item.RowId).ToArray();
                    currencyIcons[icon] = currency = matches.Length == 1 ? matches[0] : 0;
                }
                rows = rows.Select(row => row with { Cost0Id = currency }).ToArray();
            }
            if (before.Kind == "company_credit_items")
            {
                // Bind displayed item/price pairs to the installed FC shop catalogue. A different
                // or changed UI layout must not turn arbitrary numeric fields into an offer.
                companyCreditPrices ??= data.GetExcelSheet<FccShop>().SelectMany(shop => shop.ItemData)
                    .Where(item => item.Item.RowId > 0).Select(item => (item.Item.RowId, item.Cost)).ToHashSet();
                if (rows.Any(row => !companyCreditPrices.Contains((row.ItemId, row.Cost0Amount))))
                    return Unavailable("FC credit shop rows do not match the installed item/price catalogue.");
            }
            var entries = Describe(rows, Name, "Last observed " + before.AddonName + " offers");
            if (before != Observe()) return Unavailable("The shop or character changed while reading. Let the menu settle and ask again.");
            var afterRows = CopyObservedRows(before);
            if (before.Kind == "currency_exchange") afterRows = afterRows.Select(row => row with { Cost0Id = rows[0].Cost0Id }).ToArray();
            if (before != Observe() || !rows.SequenceEqual(afterRows) ||
                language != data.Language || !ReferenceEquals(gameData, data.GameData))
                return Unavailable("The shop, offers or character changed while reading. Wait for the menu to settle and ask again.");
            return new(Category, captured, true, Source, $"visible_ready_{before.Kind}; eligibility_stock_and_conditions_unknown", entries);
        }
        catch (Exception ex) { return Unavailable($"Opened shop read unavailable ({ex.GetType().Name})."); }
    }

    internal string Status()
    {
        try { return Observe().Ready ? "Open shop ready" : "Open a supported shop's offers tab"; }
        catch (Exception) { return "Shop state unavailable"; }
    }

    /// <summary>Explicit local diagnostic; framework thread only. No saved file or AI request.</summary>
    internal ShopDiagnosticResult CaptureDiagnostic()
    {
        try
        {
            var player = PlayerState.Instance();
            if (player == null || !player->IsLoaded || player->ContentId == 0)
                return new(null, "Log into a character first.");
            var owner = player->ContentId;
            var addon = gui.GetAddonByName<AddonShop>("Shop", 1);
            if (addon == null || !addon->IsVisible)
                return new(null, "Open the affected normal NPC shop's Buy tab, then copy again.");
            var proxy = ShopEventHandler.AgentProxy.Instance();
            var handler = proxy != null && proxy->AddonId == addon->Id ? proxy->Handler : null;
            var before = FormatGilDiagnostic(addon, proxy);
            var current = gui.GetAddonByName<AddonShop>("Shop", 1);
            var currentProxy = ShopEventHandler.AgentProxy.Instance();
            if (current != addon || currentProxy != proxy || PlayerState.Instance() != player ||
                !player->IsLoaded || player->ContentId != owner || !current->IsVisible ||
                (currentProxy != null && currentProxy->AddonId == current->Id ? currentProxy->Handler : null) != handler ||
                before != FormatGilDiagnostic(current, currentProxy))
                return new(null, "Shop changed during the diagnostic; let it settle and copy again.");
            return Encoding.UTF8.GetByteCount(before) <= 16384
                ? new(before, "Shop diagnostic copied. Paste it into the support chat.")
                : new(null, "Shop diagnostic exceeds 16 KB; nothing copied.");
        }
        catch (Exception)
        {
            return new(null, "Shop diagnostic is unavailable; nothing copied.");
        }
    }

    internal static string FormatGilDiagnostic(AddonShop* addon, ShopEventHandler.AgentProxy* proxy)
    {
        if (addon == null) throw new ArgumentException("Shop addon is required.");
        var matching = proxy != null && addon->Id != 0 && proxy->AddonId == addon->Id;
        // Never follow a stale proxy's handler when its addon binding does not match.
        var handler = matching ? proxy->Handler : null;
        var buy = addon->BuyList == null ? null : (AtkResNode*)addon->BuyList->OwnerNode;
        var buyback = addon->BuybackList == null ? null : (AtkResNode*)addon->BuybackList->OwnerNode;
        var rows = new List<object>(60);
        var mappingValid = handler != null && handler->ItemsCount is >= 0 and <= 60 &&
            handler->VisibleItemsCount >= 0 && handler->VisibleItemsCount <= handler->ItemsCount;
        if (mappingValid)
        {
            var seen = new HashSet<int>();
            for (var row = 0; row < handler->VisibleItemsCount; row++)
            {
                var index = handler->VisibleItems[row];
                if (index < 0 || index >= handler->ItemsCount || !seen.Add(index)) { mappingValid = false; break; }
                var item = handler->Items[index];
                rows.Add(new { visible_row = row, item_index = index, item_id = item.ItemId, unit_price = item.PriceBuy, hq = item.IsHQ });
            }
            if (!mappingValid) rows.Clear();
        }
        // Explicit projection only: no character/account IDs, pointers, names, paths, owned
        // counts, balances, shop text or raw UI/SeString payloads can enter the diagnostic.
        return JsonSerializer.Serialize(new
        {
            diagnostic = "compai_shop_v1", addon = "Shop", addon_ready = addon->IsReady, addon_visible = addon->IsVisible,
            addon_id = addon->Id, proxy_present = proxy != null, proxy_addon_id = proxy == null ? (uint?)null : proxy->AddonId,
            proxy_matches_addon = matching, handler_present = handler != null,
            buy_list_present = addon->BuyList != null, buy_owner_present = buy != null,
            buy_visible_through_parents = VisibleThroughParents(buy), buy_ancestors = DiagnosticAncestors(buy),
            buyback_list_present = addon->BuybackList != null, buyback_owner_present = buyback != null,
            buyback_visible_through_parents = VisibleThroughParents(buyback), buyback_ancestors = DiagnosticAncestors(buyback),
            buy_tab_gate = GilBuyListVisible(addon), handler_ready = GilReady(handler),
            item_count = handler == null ? (int?)null : handler->ItemsCount,
            visible_count = handler == null ? (int?)null : handler->VisibleItemsCount,
            mode = handler == null ? (int?)null : handler->CurrentMode,
            buyback_tab_active = handler == null ? (bool?)null : handler->BuybackTabActive,
            sorting = handler == null ? (bool?)null : handler->NeedSorting,
            starting_buy = handler == null ? (bool?)null : handler->StartingBuy,
            starting_sell = handler == null ? (bool?)null : handler->StartingSell,
            transaction_pending = handler == null ? (bool?)null : handler->WaitingForTransactionToFinish,
            visible_mapping_valid = mappingValid, visible_offers = rows,
        });
    }

    private static object[] DiagnosticAncestors(AtkResNode* node)
    {
        var result = new List<object>(32);
        for (var depth = 0; node != null && depth < 32; depth++, node = node->ParentNode)
            result.Add(new { depth, node_id = node->NodeId, visible_flag = (node->NodeFlags & NodeFlags.Visible) != 0 });
        return result.ToArray();
    }

    private static MenuSnapshot Unavailable(string reason) => MenuSnapshot.Unavailable(Category, Source, reason);

    private ShopMenuObservation Observe()
    {
        var player = PlayerState.Instance();
        if (player == null || !player->IsLoaded || player->ContentId == 0) return default;
        var inclusion = ObserveInclusion(player);
        if (inclusion.Ready) return inclusion;
        var credits = ObserveCompanyCredits(player);
        if (credits.Ready) return credits;
        var gil = ObserveGil(player);
        if (gil.Ready) return gil;
        var agent = AgentShop.Instance();
        if (agent == null || !agent->IsAgentActive() || !agent->IsAddonShown() || !agent->IsAddonReady() || agent->AddonId == 0) return default;
        foreach (var name in AddonNames)
        {
            var addon = gui.GetAddonByName<AtkUnitBase>(name, 1);
            if (addon == null || !addon->IsReady || !addon->IsVisible || addon->Id != agent->AddonId) continue;
            if (!SupportedLayout(agent->ItemReceiveCount, agent->ItemCostCount, agent->ItemReceive != null, agent->ItemCost != null)) return default;
            return new(true, (nint)player, player->ContentId, (nint)agent, (nint)addon, name, agent->AddonId,
                (nint)agent->EventReceiver, (nint)agent->ItemReceive, (nint)agent->ItemCost,
                agent->ItemReceiveCount, agent->ItemCostCount, agent->SelectedItemIndex, agent->SelectedItemStackSize,
                name == "ShopExchangeCurrency" ? "currency_exchange" : "common_exchange",
                name == "ShopExchangeCurrency" && addon->AtkValues != null && addon->AtkValuesCount > 87
                    ? ReadUInt(addon->AtkValues[87]) : 0);
        }
        return default;
    }

    private ShopMenuObservation ObserveGil(PlayerState* player)
    {
        var addon = gui.GetAddonByName<AddonShop>("Shop", 1);
        if (addon == null || !addon->IsReady || !addon->IsVisible || addon->Id == 0 ||
            !GilBuyListVisible(addon)) return default;
        // The dedicated proxy owns the regular gil shop. Do not gate it on common AgentShop
        // receive/cost arrays, which are not this shop's offer source.
        // https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Game/Event/ShopEventHandler.cs
        var proxy = ShopEventHandler.AgentProxy.Instance();
        if (proxy == null || proxy->AddonId != addon->Id || !GilReady(proxy->Handler)) return default;
        var handler = proxy->Handler;
        return new(true, (nint)player, player->ContentId, (nint)proxy, (nint)addon, "Shop", addon->Id,
            (nint)proxy, (nint)handler, 0, handler->VisibleItemsCount, handler->ItemsCount,
            handler->CurrentMode, 0, "gil_shop");
    }

    internal static bool GilBuyListVisible(AddonShop* addon) => addon != null &&
        addon->BuyList != null && VisibleThroughParents((AtkResNode*)addon->BuyList->OwnerNode) == true &&
        (addon->BuybackList == null || VisibleThroughParents((AtkResNode*)addon->BuybackList->OwnerNode) == false);

    internal static bool? VisibleThroughParents(AtkResNode* node)
    {
        if (node == null) return null;
        // An individual node's Visible flag does not establish that its containing tab is shown.
        // A hidden parent must not make the inactive Buyback list block the visible Buy list.
        for (var depth = 0; node != null && depth < 32; depth++, node = node->ParentNode)
            if ((node->NodeFlags & NodeFlags.Visible) == 0) return false;
        // Cyclic or over-depth ancestry is unknown, not evidence that Buyback is hidden.
        return node == null ? true : null;
    }

    private ShopMenuObservation ObserveCompanyCredits(PlayerState* player)
    {
        var module = AgentModule.Instance();
        var agent = module == null ? null : module->GetAgentByInternalId(AgentId.FreeCompanyCreditShop);
        if (agent == null || !agent->IsAgentActive() || !agent->IsAddonShown() || !agent->IsAddonReady() || agent->AddonId == 0) return default;
        var addon = gui.GetAddonByName<AtkUnitBase>("FreeCompanyCreditShop", 1);
        if (addon == null || !addon->IsReady || !addon->IsVisible || addon->Id != agent->AddonId ||
            addon->AtkValues == null || addon->AtkValuesCount < 10) return default;
        var count = ReadUInt(addon->AtkValues[9]);
        if (count is 0 or > 20 || addon->AtkValuesCount < 130 + count) return default;
        return new(true, (nint)player, player->ContentId, (nint)agent, (nint)addon, "FreeCompanyCreditShop", agent->AddonId,
            0, (nint)addon->AtkValues, 0, (int)count, addon->AtkValuesCount, 0, 0, "company_credit_items");
    }

    private ShopMenuObservation ObserveInclusion(PlayerState* player)
    {
        var agent = AgentInclusionShop.Instance();
        if (agent == null || !agent->IsAgentActive() || !agent->IsAddonShown() || !agent->IsAddonReady() || agent->AddonId == 0 ||
            agent->Data == null || !agent->Data->IsShopReady || agent->Data->NeedsAddonRefresh) return default;
        var addon = gui.GetAddonByName<AddonInclusionShop>("InclusionShop", 1);
        if (addon == null || !addon->IsReady || !addon->IsVisible || addon->Id != agent->AddonId ||
            addon->AtkValues == null || addon->AtkValuesCount < 299) return default;
        var count = ReadUInt(addon->TypedAtkValues->ItemCount);
        if (count is 0 or > 60 || addon->AtkValuesCount < 299 + count * 18) return default;
        return new(true, (nint)player, player->ContentId, (nint)agent, (nint)addon, "InclusionShop", agent->AddonId,
            (nint)agent->Data, (nint)addon->AtkValues, 0, (int)count, addon->AtkValuesCount,
            agent->Data->SelectedCategoryIndex, agent->Data->SelectedSubCategoryIndex, "inclusion_shop");
    }

    private static ShopObservedOffer[] CopyObservedRows(ShopMenuObservation observation) => observation.Kind switch
    {
        "gil_shop" => CopyGilRows((ShopEventHandler*)observation.Receive),
        "inclusion_shop" => CopyInclusionRows((AddonInclusionShop*)observation.Addon),
        "currency_exchange" => CopyCurrencyRows((AgentShop*)observation.Agent, (AtkUnitBase*)observation.Addon),
        "company_credit_items" => CopyCompanyCreditRows((AtkUnitBase*)observation.Addon),
        _ => CopyRows((AgentShop*)observation.Agent),
    };

    // CurrentMode may be idle (0) before a transaction. Observe separately verifies the visible
    // Buy list and matching proxy; only buyback (2) or unknown modes are excluded here.
    // NeedSorting is set during sheet loading, not a reliable active-sort/readiness signal.
    // A live ready Buy menu retained it with valid offers. Capture still compares the copied
    // offers/identity twice, and the cache requires two matching observations before saving.
    // https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/FFXIV/Client/Game/Event/ShopEventHandler.cs#L25
    internal static bool GilReady(ShopEventHandler* handler) => handler != null && handler->CurrentMode is 0 or 1 &&
        !handler->BuybackTabActive && !handler->StartingBuy && !handler->StartingSell &&
        !handler->WaitingForTransactionToFinish && handler->ItemsCount is > 0 and <= 60 &&
        handler->VisibleItemsCount > 0 && handler->VisibleItemsCount <= handler->ItemsCount;

    internal static ShopObservedOffer[] CopyGilRows(ShopEventHandler* handler)
    {
        if (!GilReady(handler)) throw new InvalidOperationException("Gil shop is not ready.");
        var rows = new ShopObservedOffer[handler->VisibleItemsCount];
        var seen = new HashSet<int>();
        for (var index = 0; index < rows.Length; index++)
        {
            var itemIndex = handler->VisibleItems[index];
            if (itemIndex < 0 || itemIndex >= handler->ItemsCount || !seen.Add(itemIndex))
                throw new InvalidOperationException("Gil shop visible-row mapping is invalid.");
            var item = handler->Items[itemIndex];
            if (item.ItemId == 0 || item.PriceBuy < 0) throw new InvalidOperationException("Gil shop item is not loaded.");
            // StackSize is the item's stack capacity, NOT the number bought for PriceBuy.
            rows[index] = new(item.ItemId, 1, 1, (uint)item.PriceBuy, 0, 0, 0, 0, item.IsHQ, true);
        }
        return rows;
    }

    internal static uint ReadUInt(AtkValue value) => value.Type switch
    {
        AtkValueType.UInt => value.UInt,
        AtkValueType.Int when value.Int >= 0 => (uint)value.Int,
        _ => throw new InvalidOperationException("Shop value is not a nonnegative integer."),
    };

    internal static ShopObservedOffer[] CopyInclusionRows(AddonInclusionShop* addon)
    {
        if (addon == null || addon->AtkValues == null || addon->AtkValuesCount < 299)
            throw new InvalidOperationException("Inclusion shop values are unavailable.");
        var values = addon->TypedAtkValues;
        var count = ReadUInt(values->ItemCount);
        if (count is 0 or > 60 || addon->AtkValuesCount < 299 + count * 18)
            throw new InvalidOperationException("Inclusion shop row bound is invalid.");
        var rows = new ShopObservedOffer[count];
        for (var index = 0; index < rows.Length; index++)
        {
            var item = values->Items[index];
            var costs = ReadUInt(item.GiveCount);
            if (costs > 3) throw new InvalidOperationException("Inclusion shop cost bound is invalid.");
            // The typed addon contains the already-resolved item IDs shown by the UI, rather
            // than AgentInclusionShop's raw special-currency/tomestone indices.
            uint Id(int slot) => slot < costs ? ReadUInt(item.GiveItemId[slot]) : 0;
            uint Amount(int slot) => slot < costs ? ReadUInt(item.GiveAmount[slot]) : 0;
            rows[index] = new(ReadUInt(item.ItemId), ReadUInt(item.Stacksize),
                Id(0), Amount(0), Id(1), Amount(1), Id(2), Amount(2));
        }
        return rows;
    }

    // Field facts: https://github.com/NightmareXIV/ECommons/blob/3ebb7c7911c667b89da450d527423f938f15466c/ECommons/UIHelpers/AddonMasterImplementations/ShopExchangeCurrency.cs
    // Validation reference: https://github.com/McVaxius/ADS/blob/1d5ad22a9c09fcccd628e1044560c371df6e1f67/ADS/Services/ShopPurchaseRuntime.cs
    // Cross-check against independent AgentShop receive IDs/bundles; never read empty cost pointers.
    // Only the basic one-currency layout is accepted. Combined/certificate and other variants fail closed.
    internal static ShopObservedOffer[] CopyCurrencyRows(AgentShop* agent, AtkUnitBase* addon)
    {
        if (agent == null || addon == null || addon->AtkValues == null || addon->AtkValuesCount <= 87 ||
            agent->ItemReceive == null) throw new InvalidOperationException("Currency exchange is not ready.");
        var values = addon->AtkValues;
        var count = ReadUInt(values[4]);
        if (count is 0 or > 122 || count != agent->ItemReceiveCount || addon->AtkValuesCount < 1310 + count)
            throw new InvalidOperationException("Unsupported currency exchange row layout.");
        var rows = new ShopObservedOffer[count];
        var indices = new HashSet<uint>();
        for (var index = 0; index < count; index++)
        {
            var callback = ReadUInt(values[1310 + index]);
            var received = agent->ItemReceive[index];
            if (callback >= count || !indices.Add(callback) || ReadUInt(values[1066 + index]) != received.ItemId ||
                received.ItemId == 0 || received.ItemCount == 0)
                throw new InvalidOperationException("Currency exchange item mapping is inconsistent.");
            rows[index] = new(received.ItemId, received.ItemCount, 0, ReadUInt(values[456 + index]), 0, 0, 0, 0);
        }
        return rows;
    }

    // Field facts: https://github.com/NightmareXIV/ECommons/blob/3ebb7c7911c667b89da450d527423f938f15466c/ECommons/UIHelpers/AddonMasterImplementations/FreeCompanyCreditShop.cs
    // Costs are per single item. Actions use another addon and a non-Item identity; not read here.
    internal static ShopObservedOffer[] CopyCompanyCreditRows(AtkUnitBase* addon)
    {
        if (addon == null || addon->AtkValues == null || addon->AtkValuesCount < 10)
            throw new InvalidOperationException("FC credit item shop is not ready.");
        var values = addon->AtkValues;
        var count = ReadUInt(values[9]);
        if (count is 0 or > 20 || addon->AtkValuesCount < 130 + count)
            throw new InvalidOperationException("FC credit shop row bound is invalid.");
        var rows = new ShopObservedOffer[count];
        for (var index = 0; index < count; index++)
        {
            var item = ReadUInt(values[30 + index]);
            if (item == 0) throw new InvalidOperationException("FC credit shop item identity is missing.");
            rows[index] = new(item, 1, 0, ReadUInt(values[130 + index]), 0, 0, 0, 0,
                NamedCurrency: "Company credits");
        }
        return rows;
    }

    internal static bool SupportedLayout(int receiveCount, int costCount, bool receivePresent, bool costPresent) =>
        receivePresent && receiveCount is > 0 and <= MaxOffers &&
        (costCount == 0 || costPresent && costCount == receiveCount * 3);

    internal static ShopObservedOffer[] CopyRows(AgentShop* agent)
    {
        if (agent == null || !SupportedLayout(agent->ItemReceiveCount, agent->ItemCostCount, agent->ItemReceive != null, agent->ItemCost != null))
            throw new InvalidOperationException("Shop array layout is not ready or supported.");
        var rows = new ShopObservedOffer[agent->ItemReceiveCount];
        for (var index = 0; index < rows.Length; index++)
        {
            var received = agent->ItemReceive + index;
            // Official current AgentShop schema documents three cost slots for every offer:
            // https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/UI/Agent/AgentShop.cs
            var costs = agent->ItemCostCount == 0 ? null : agent->ItemCost + index * 3;
            rows[index] = new(received->ItemId, received->ItemCount,
                costs == null ? 0 : costs[0].ItemId, costs == null ? 0 : costs[0].ItemCount,
                costs == null ? 0 : costs[1].ItemId, costs == null ? 0 : costs[1].ItemCount,
                costs == null ? 0 : costs[2].ItemId, costs == null ? 0 : costs[2].ItemCount);
        }
        return rows;
    }

    internal static MenuEntry[] Describe(IEnumerable<ShopObservedOffer> observed, Func<uint, string?> itemName, string location)
    {
        var rows = observed.Take(MaxOffers + 1).ToArray();
        if (rows.Length is 0 or > MaxOffers || !MenuSnapshot.ValidText(location, 192))
            throw new InvalidOperationException("Shop offer bound exceeded or not loaded.");
        var entries = new MenuEntry[rows.Length];
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            var name = row.ItemId > 0 ? itemName(row.ItemId) : null;
            if (!MenuSnapshot.ValidText(name, 128)) throw new InvalidOperationException("Shop item identity is unavailable.");
            var costs = new List<MenuCost>(3);
            if (row.NamedCurrency is not null)
                costs.Add(new("unknown", null, row.NamedCurrency, row.Cost0Amount == 0 ? null : row.Cost0Amount,
                    "Named non-item currency; inventory balance comparison is unsupported. Zero cost does not establish a free offer."));
            foreach (var (id, amount) in new[] { (row.Cost0Id, row.Cost0Amount), (row.Cost1Id, row.Cost1Amount), (row.Cost2Id, row.Cost2Amount) })
            {
                if (row.NamedCurrency is not null) break;
                if (id == 0 && amount == 0) continue;
                var costName = id > 0 ? itemName(id) : null;
                if (id > 0 && MenuSnapshot.ValidText(costName, 128))
                    costs.Add(new(row.GilCost && id == 1 ? "gil" : "item", row.GilCost && id == 1 ? null : id, costName, amount == 0 ? null : amount,
                        amount == 0 ? "The loaded cost amount is zero; a payable amount or free offer is not established." : null));
                else costs.Add(new("unknown", null, null, amount == 0 ? null : amount,
                    "The currency/item identifier does not resolve to a supported Item row; currency identity and requirements are unknown."));
            }
            if (costs.Count == 0) costs.Add(new("unknown", null, null, null,
                "No supported cost was observed; this does not establish a free offer."));
            entries[index] = new((uint)index + 1, row.ItemId, name!, row.Quantity == 0 ? null : row.Quantity,
                "offer", location, null, index, row.IsHq, costs.ToArray(),
                "Stock, conditions, purchase eligibility, affordability and item quality are not verified.");
        }
        return entries;
    }
}

internal readonly record struct ShopMenuObservation(bool Ready, nint Player, ulong Owner, nint Agent, nint Addon,
    string? AddonName, uint AddonId, nint EventReceiver, nint Receive, nint Cost, int ReceiveCount, int CostCount,
    int SelectedIndex, int SelectedStackSize, string Kind = "common_exchange", uint CurrencyIcon = 0);
internal readonly record struct ShopObservedOffer(uint ItemId, uint Quantity, uint Cost0Id, uint Cost0Amount,
    uint Cost1Id, uint Cost1Amount, uint Cost2Id, uint Cost2Amount, bool? IsHq = null, bool GilCost = false,
    string? NamedCurrency = null);
internal sealed record ShopDiagnosticResult(string? Text, string Status);
