using System;
using Dalamud.Bindings.ImGui;

namespace CompAI;

internal sealed partial class SettingsWindow
{
    private int? highlightSecondsDraft;

    private void DrawBeta()
    {
        ImGui.TextWrapped("Optional features still being tested. Each feature is off by default.");
        ImGui.Spacing();
        var enabled = this.plugin.InventoryHighlightEnabled;
        if (ImGui.Checkbox("Inventory highlighting", ref enabled))
        {
            this.plugin.SetInventoryHighlight(enabled);
            this.highlightSecondsDraft = null;
        }
        ImGui.TextWrapped("Click Find in bags on a supported item in a reply to open inventory and highlight its slot. Other visible bag slots are dimmed temporarily. No items are moved or used.");
        if (enabled)
        {
            var seconds = this.highlightSecondsDraft ?? this.plugin.InventoryHighlightSeconds;
            ImGui.SetNextItemWidth(220);
            if (ImGui.SliderInt("Duration (seconds)", ref seconds, 5, 60))
                this.highlightSecondsDraft = seconds;
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                this.plugin.SetInventoryHighlightSeconds(Math.Clamp(seconds, 5, 60));
                this.highlightSecondsDraft = null;
            }
            ImGui.TextWrapped("The highlight stops when you view that item's tooltip. Use the X beside inventory to stop early.");
        }
        if (!this.plugin.Access.Inventory)
        {
            ImGui.TextWrapped("Inventory access is required to offer Find in bags.");
            if (ImGui.Button("Open access settings")) this.selectAccess = true;
        }
        ImGui.Spacing();
        ImGui.TextWrapped(this.plugin.InventoryHighlightStatus);
        if (this.plugin.InventoryHighlightActive && ImGui.Button("Clear highlight"))
            this.plugin.ClearInventoryHighlight();
        ImGui.Spacing();
        ImGui.Separator();
        var shopEnabled = this.plugin.ShopHighlightEnabled;
        if (ImGui.Checkbox("Shop highlighting", ref shopEnabled)) this.plugin.SetShopHighlight(shopEnabled);
        ImGui.TextWrapped("Click Find in current shop on a reply to highlight an offer in the same open gil shop for 15 seconds. Scroll to the row manually. No shop is opened and nothing is bought.");
        if (!this.plugin.Access.Shops)
        {
            ImGui.TextWrapped("Shop access is required to offer Find in current shop.");
            if (ImGui.Button("Open shop access settings")) this.selectAccess = true;
        }
        ImGui.TextWrapped(this.plugin.ShopHighlightStatus);
        if (this.plugin.ShopHighlightActive && ImGui.Button("Clear shop highlight")) this.plugin.ClearShopHighlight();
        ImGui.Spacing();
        if (this.plugin.InventoryHighlightSaveError is { } error)
        {
            ImGui.TextWrapped("Beta settings: " + error);
            if (ImGui.Button("Retry saving beta settings"))
                this.plugin.SetInventoryHighlight(this.plugin.InventoryHighlightEnabled);
        }
        if (ImGui.CollapsingHeader("Highlight details"))
        {
            ImGui.TextWrapped("Only an explicit Find in bags click can open inventory. The selected bag slot is rechecked against current inventory before highlighting. Unsupported layouts or changed items may prevent the highlight.");
            ImGui.TextWrapped("Find selects the item's inventory page once. Keep inventory focused. Other tooltips keep the highlight visible; viewing the selected item's tooltip clears it. Overlapping game windows still pause it.");
            ImGui.TextWrapped("The timeout defaults to 15 seconds and can be set from 5 to 60 seconds. Turning this feature off clears the highlight.");
        }
    }
}
