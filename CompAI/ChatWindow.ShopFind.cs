using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace CompAI;

internal sealed partial class ChatWindow
{
    private string shopFindFilter = string.Empty;

    private void DrawShopFind(ChatTurn turn)
    {
        if (!this.plugin.ShopHighlightEnabled || turn.ShopFindKey is not { } key) return;
        AnswerTextRenderer.ContinueActionRow("Find in current shop");
        if (ImGui.SmallButton("Find in current shop"))
        {
            this.shopFindFilter = string.Empty;
            this.sendError = null;
            var group = this.plugin.ShopFindItems(key);
            if (group is { Items.Count: 1 }) this.sendError = this.plugin.FindShopItem(key, group.Items[0].Id);
            else ImGui.OpenPopup("Find shop offer");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Beta: highlight an exact offer returned for this answer in the same currently open gil shop. Scroll to the offer manually. The highlight lasts 15 seconds; no menu is opened and nothing is bought.");
        if (this.plugin.ShopHighlightActive)
        {
            AnswerTextRenderer.ContinueActionRow("Clear shop highlight");
            if (ImGui.SmallButton("Clear shop highlight")) this.plugin.ClearShopHighlight();
        }
        var popupWidth = Math.Max(1, Math.Min(560, ImGui.GetMainViewport().WorkSize.X - 30));
        ImGui.SetNextWindowSizeConstraints(new Vector2(popupWidth, 0), new Vector2(popupWidth, float.MaxValue));
        if (!ImGui.BeginPopup("Find shop offer")) return;
        var current = this.plugin.ShopFindItems(key);
        if (current is null)
            ImGui.TextWrapped("These shop actions have expired or the shop changed. Keep the shop open and ask for a fresh lookup.");
        else
        {
            ImGui.TextWrapped("Offers read for this answer, matched to the current shop. Choose one to highlight its visible row for 15 seconds.");
            if (current.Items.Count > 8)
                ImGui.InputTextWithHint("##shop_find_filter", "Filter by item name", ref this.shopFindFilter, 100);
            var visible = current.Items.Where(item => this.shopFindFilter.Length == 0 ||
                item.Name.Contains(this.shopFindFilter, StringComparison.OrdinalIgnoreCase)).ToArray();
            ImGui.BeginChild("##shop_find_rows", new Vector2(0,
                Math.Min(300, Math.Max(1, visible.Length) * ImGui.GetFrameHeightWithSpacing() + 8)), true);
            if (visible.Length == 0) ImGui.TextUnformatted("No matching offers.");
            foreach (var item in visible)
            {
                ImGui.PushID(checked((int)item.Id));
                if (ImGui.SmallButton("Find"))
                {
                    this.sendError = this.plugin.FindShopItem(key, item.Id);
                }
                ImGui.SameLine();
                ImGui.TextWrapped($"{item.Name}{(item.IsHq ? " (HQ)" : "")} · {item.GilCost:N0} gil · Row {item.Id}");
                ImGui.PopID();
            }
            ImGui.EndChild();
        }
        if (this.plugin.ShopFindFeedback(key) is { } status)
        {
            ImGui.Separator();
            ImGui.TextWrapped(status);
            if (ImGui.SmallButton("Copy status")) ImGui.SetClipboardText(status);
            if (this.plugin.ShopHighlightActive)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Clear highlight")) this.plugin.ClearShopHighlight();
            }
        }
        if (this.sendError is { } error) ImGui.TextWrapped(error);
        if (ImGui.SmallButton("Close")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }
}
