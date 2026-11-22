using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Game.Inventory;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;

namespace CompAI;

/// <summary>
/// Copies the currently visible carried-item tooltip. Call only on the framework thread,
/// while the user has explicitly armed scanning. This does not open or modify game UI.
/// </summary>
internal sealed unsafe class PedigreeTooltipReader(IGameGui gui, IGameInventory inventory, IDataManager data)
{
    private const int MaxNodes = 256;
    private const int MaxDepth = 8;
    private const int MaxAncestors = 32;
    private const int MaxStringBytes = 2048;
    private const int MaxTotalBytes = 16384;

    internal bool TryRead(out HoveredChocoboTooltip? sample, out string reason)
    {
        sample = null;
        reason = "Hover a chocobo card in one of the four carried bags.";
        try
        {
            if (!this.TryReadIdentity(out var identity))
                return false;

            var addon = gui.GetAddonByName<AtkUnitBase>("ItemDetail", 1);
            if (addon == null || !addon->IsVisible)
            {
                reason = "Waiting for the item tooltip to become visible.";
                return false;
            }

            var itemSheet = data.GetExcelSheet<Item>();
            if (!itemSheet.TryGetRow(identity.BaseItemId, out var itemRow))
            {
                reason = "The local item name is unavailable; no tooltip was saved.";
                return false;
            }

            var itemName = itemRow.Name.ExtractText().Trim();
            if (itemName.Length == 0)
                return false;

            var addonSheet = data.GetExcelSheet<Addon>();
            var labels = new string[5];
            for (uint i = 0; i < labels.Length; i++)
            {
                if (!addonSheet.TryGetRow(9378 + i, out var row) ||
                    string.IsNullOrWhiteSpace(labels[i] = row.Text.ExtractText().Trim()))
                {
                    reason = "Local chocobo stat labels are unavailable; no tooltip was saved.";
                    return false;
                }
            }

            if (!TryCopyVisibleText(&addon->UldManager, out var nodes))
            {
                reason = "The tooltip exceeded the reader's bounds or contained unreadable text.";
                return false;
            }

            if (!nodes.Any(node => string.Equals(node.Text.Trim(), itemName, StringComparison.Ordinal)))
            {
                reason = "Waiting for the tooltip name to match the hovered card.";
                return false;
            }

            // Re-read all identity scalars after the bounded copy; never associate a previous
            // tooltip with a different carried slot while the mouse or inventory is changing.
            if (!this.TryReadIdentity(out var after) || identity != after ||
                gui.GetAddonByName<AtkUnitBase>("ItemDetail", 1) != addon || !addon->IsVisible)
            {
                reason = "The hovered card changed during reading; hover it again.";
                return false;
            }

            sample = new HoveredChocoboTooltip(identity.Bag.ToString(), identity.Slot, identity.ItemId,
                identity.BaseItemId, itemName, checked((int)identity.Condition), DateTimeOffset.UtcNow,
                nodes, labels);
            reason = "Visible tooltip copied locally; parentage still requires decoding and review.";
            return true;
        }
        catch (Exception ex)
        {
            // Do not expose pointers, arbitrary UI text or exception contents in the status.
            reason = $"Tooltip read unavailable ({ex.GetType().Name}); no card was saved.";
            return false;
        }
    }

    private bool TryReadIdentity(out Identity identity)
    {
        identity = default;
        var agent = AgentItemDetail.Instance();
        if (agent == null || agent->DetailKind != DetailKind.InventoryItem ||
            agent->TypeOrId is < 48 or > 51 || agent->Index >= 35 ||
            agent->ItemId is < 9561 or > 9614 || gui.HoveredItem != agent->ItemId)
            return false;

        var type = agent->TypeOrId;
        var slot = agent->Index;
        var itemId = agent->ItemId;
        var bag = type switch
        {
            48 => GameInventoryType.Inventory1,
            49 => GameInventoryType.Inventory2,
            50 => GameInventoryType.Inventory3,
            _ => GameInventoryType.Inventory4,
        };

        var items = inventory.GetInventoryItems(bag);
        if (items.Length != 35)
            return false;
        foreach (var item in items)
        {
            if (item.InventorySlot != slot)
                continue;
            if (item.IsEmpty || item.ItemId != itemId || item.BaseItemId != itemId || item.Quantity != 1)
                return false;

            identity = new Identity(bag, slot, item.ItemId, item.BaseItemId, item.Condition);
            return agent->TypeOrId == type && agent->Index == slot && agent->ItemId == itemId &&
                   agent->DetailKind == DetailKind.InventoryItem && gui.HoveredItem == itemId;
        }

        return false;
    }

    // Separate from game identity lookup so traversal can be checked against owned, synthetic
    // native structures offline. The caller must keep the supplied manager alive for this call.
    internal static bool TryCopyVisibleText(AtkUldManager* manager, out TooltipText[] texts)
    {
        texts = [];
        var copied = new List<TooltipText>();
        var visited = new HashSet<nint>();
        var totalBytes = 0;
        var examinedEntries = 0;
        if (!CopyManager(manager, 0, copied, visited, ref totalBytes, ref examinedEntries))
            return false;
        texts = copied.ToArray();
        return true;
    }

    private static bool CopyManager(AtkUldManager* manager, int depth, List<TooltipText> texts,
        HashSet<nint> visited, ref int totalBytes, ref int examinedEntries)
    {
        if (manager == null || depth > MaxDepth || manager->NodeListCount > MaxNodes)
            return false;
        var count = manager->NodeListCount;
        var list = manager->NodeList;
        if (count > 0 && list == null)
            return false;

        for (var i = 0; i < count; i++)
        {
            // Component lists may overlap their parent list. Bound visits as well as unique nodes.
            if (++examinedEntries > MaxNodes * 4)
                return false;
            var node = list[i];
            if (node == null || !visited.Add((nint)node))
                continue;
            if (visited.Count > MaxNodes)
                return false;
            if (!VisibleThroughParents(node))
                continue;

            if (node->Type == NodeType.Text)
            {
                var text = (AtkTextNode*)node;
                var length = text->NodeText.StringLength;
                if (length < 0 || length > MaxStringBytes || text->NodeText.BufUsed < 0 ||
                    text->NodeText.BufUsed > MaxStringBytes + 1 || text->NodeText.BufUsed < length ||
                    text->NodeText.BufSize < text->NodeText.BufUsed)
                    return false;
                if (text->NodeText.BufUsed <= 1)
                    continue;
                if ((byte*)text->NodeText.StringPtr == null)
                    return false;
                var span = text->NodeText.AsSpan();
                if (span.Length > MaxStringBytes || totalBytes + span.Length > MaxTotalBytes)
                    return false;
                if (span.Length == 0)
                    continue;
                var encoded = span.ToArray();
                var parsed = SeString.Parse(encoded);
                totalBytes += encoded.Length;
                // Screen coordinates share one origin even when text lives inside components.
                if (!float.IsFinite(node->ScreenX) || !float.IsFinite(node->ScreenY))
                    return false;
                texts.Add(new TooltipText(node->NodeId, node->ScreenX, node->ScreenY,
                    parsed.TextValue, encoded, CopyRuns(encoded)));
            }
            // Raw component types are their ULD IDs (>=1000). The enum's Component
            // value (10000) belongs to GetNodeType()'s normalized return value.
            else if ((ushort)node->Type >= 1000)
            {
                var component = ((AtkComponentNode*)node)->Component;
                if (component == null || !CopyManager(&component->UldManager, depth + 1, texts,
                        visited, ref totalBytes, ref examinedEntries))
                    return false;
            }
        }

        return manager->NodeList == list && manager->NodeListCount == count;
    }

    private static bool VisibleThroughParents(AtkResNode* node)
    {
        // Bounded parent traversal also rejects cyclic/corrupt chains. Check bits directly;
        // no generated native UI methods are called while walking the tooltip.
        for (var depth = 0; node != null && depth < MaxAncestors; depth++, node = node->ParentNode)
            if ((node->NodeFlags & NodeFlags.Visible) == 0 || node->Color.A == 0)
                return false;
        return node == null;
    }

    private static TooltipRun[] CopyRuns(byte[] encoded)
    {
        var runs = new List<TooltipRun>();
        ushort color = 0;
        foreach (var payload in new ReadOnlySeString(encoded))
        {
            if (payload.Type == ReadOnlySePayloadType.Text)
            {
                var text = Encoding.UTF8.GetString(payload.Body.Span);
                if (text.Length > 0)
                    runs.Add(new TooltipRun(text, color));
                continue;
            }

            switch (payload.MacroCode)
            {
                case MacroCode.ColorType:
                    color = payload.TryGetExpression(out var expression) &&
                            expression.TryGetUInt(out var value) && value < ushort.MaxValue
                        ? (ushort)value : ushort.MaxValue;
                    break;
                case MacroCode.EdgeColor:
                case MacroCode.EdgeColorType:
                case MacroCode.ShadowColor:
                case MacroCode.Italic:
                    break;
                default:
                    // Unknown payloads, including direct Color expressions, must not silently
                    // inherit a recognized star color from an earlier ColorType payload.
                    color = ushort.MaxValue;
                    break;
            }
        }

        return runs.ToArray();
    }

    private readonly record struct Identity(GameInventoryType Bag, uint Slot, uint ItemId, uint BaseItemId,
        uint Condition);
}
