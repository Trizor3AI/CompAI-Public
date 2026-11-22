using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace CompAI;

internal sealed partial class ChatWindow
{
    private string inventoryFindFilter = string.Empty;
    private HashSet<int> inventoryFindMentioned = new();
    private bool inventoryFindMentionedOnly;
    private bool inventoryFindHasSuggested;

    private void DrawInventoryFind(ChatTurn turn)
    {
        if (!this.plugin.InventoryHighlightEnabled || turn.InventoryFindKey is not { } key) return;
        AnswerTextRenderer.ContinueActionRow("Find in bags");
        if (ImGui.SmallButton("Find in bags"))
        {
            this.inventoryFindFilter = string.Empty;
            this.sendError = null;
            var items = this.plugin.InventoryFindItems(key);
            this.inventoryFindHasSuggested = items?.SuggestedLocations.Count > 0;
            this.inventoryFindMentioned = items is null ? new() : this.inventoryFindHasSuggested
                ? Enumerable.Range(0, items.Items.Count).Where(index => items.SuggestedLocations.Contains(
                    (items.Items[index].Bag, items.Items[index].Slot))).ToHashSet()
                : InventoryFindCatalog.MentionedItems(items, turn.Answer);
            this.inventoryFindMentionedOnly = this.inventoryFindMentioned.Count > 0;
            if (items is { Items.Count: 1 })
                this.sendError = this.plugin.FindInventoryItem(key, 0);
            else ImGui.OpenPopup("Find inventory item");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Choose an exact bag slot from this reply’s inventory lookup. Beta: opens inventory and highlights only a current, supported slot.");
        var popupWidth = Math.Max(1, Math.Min(560, ImGui.GetMainViewport().WorkSize.X - 30));
        // Wrapped text has no intrinsic width for an auto-sized popup. Keep its width
        // bounded even after its item list expires while it is already open.
        ImGui.SetNextWindowSizeConstraints(new Vector2(popupWidth, 0), new Vector2(popupWidth, float.MaxValue));
        if (!ImGui.BeginPopup("Find inventory item")) return;
        var group = this.plugin.InventoryFindItems(key);
        if (group is null)
            ImGui.TextWrapped("These item locations have expired. Ask for a fresh inventory lookup.");
        else
        {
            if (this.inventoryFindMentioned.Count > 0 && this.inventoryFindMentioned.Count < group.Items.Count)
            {
                ImGui.Checkbox(this.inventoryFindHasSuggested ? "Only selected locations" : "Only item names mentioned in this reply",
                    ref this.inventoryFindMentionedOnly);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(this.inventoryFindHasSuggested
                    ? "Exact bag/slot locations selected by the companion. Uncheck to see all items read for this answer, with selected locations first."
                    : "Matches item names, not a recommendation or a specific card. Uncheck to see all items read for this answer.");
            }
            else ImGui.TextWrapped(this.inventoryFindHasSuggested ? "Locations selected for this answer." :
                "Items read for this answer; these are not necessarily recommendations.");
            if (group.Items.Count > 8)
                ImGui.InputTextWithHint("##find_filter", "Filter by item name", ref this.inventoryFindFilter, 100);
            bool Visible(int index) => (!this.inventoryFindMentionedOnly || this.inventoryFindMentioned.Contains(index)) &&
                (this.inventoryFindFilter.Length == 0 || group.Items[index].Name.Contains(this.inventoryFindFilter, StringComparison.OrdinalIgnoreCase));
            var visibleCount = 0;
            for (var i = 0; i < group.Items.Count; i++) if (Visible(i)) ++visibleCount;
            ImGui.BeginChild("##find_slots", new Vector2(0, Math.Min(300, Math.Max(1, visibleCount) * ImGui.GetFrameHeightWithSpacing() + 8)), true);
            if (visibleCount == 0) ImGui.TextUnformatted("No matching items.");
            foreach (var i in Enumerable.Range(0, group.Items.Count).OrderByDescending(index =>
                this.inventoryFindHasSuggested && this.inventoryFindMentioned.Contains(index)))
            {
                var item = group.Items[i];
                if (!Visible(i)) continue;
                ImGui.PushID(i);
                if (ImGui.SmallButton("Find"))
                {
                    this.sendError = this.plugin.FindInventoryItem(key, i);
                    if (this.sendError is null) ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                ImGui.TextWrapped($"{item.Name}{(item.IsHq ? " (HQ)" : "")} · Bag {item.Bag[^1]}, slot {item.Slot + 1} · ×{item.Quantity}");
                ImGui.PopID();
            }
            ImGui.EndChild();
        }
        if (this.sendError is { } error) ImGui.TextWrapped(error);
        if (ImGui.SmallButton("Close")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }
}
