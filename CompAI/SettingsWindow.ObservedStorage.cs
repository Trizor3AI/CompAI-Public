using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace CompAI;

internal sealed partial class SettingsWindow
{
    private string storageSearch = "";
    private readonly HashSet<(string Category, string SourceKey)> storageOpenSources = new();
    private bool storageShowEmpty;
    private StorageClearRequest? storageClearRequest;
    private string storageClearLabel = "";
    private bool openStorageClear;
    private string? storageClearNotice;

    private void DrawObservedStorage()
    {
        var access = this.plugin.Access;
        if (!access.Inventory && !access.Retainers && !access.Saddlebag && !access.Housing && !access.FcChest && !access.Armoury)
        { this.DrawAccessDisabled("Other storage"); return; }
        ImGui.TextWrapped("Last-observed contents, saved for this character. Open a source below to browse its items.");
        if (ImGui.CollapsingHeader("How to refresh"))
        {
            ImGui.TextWrapped("Personal crystals and key items update while their game containers are loaded. If missing, open the corresponding inventory tab. Retainers: open their inventory (including crystals) and sale list. Saddlebags: open each available tab. Housing: open furnishing placement and storeroom menus in each property. FC: open the FC member list once, then chest pages you can access.");
            ImGui.TextWrapped("Key items use the game's EventItem definitions; they are not market items and are excluded from pricing.");
            ImGui.TextWrapped("Armoury Chest updates from loaded containers. If missing, open the Armoury Chest. Physical slots may differ from sorted screen positions. Only owned slots are counted; equipped items and saved gearset plans are separate. Materia slots and grades are zero-based in lookups; unknown materia names remain unknown. Armoury gear is excluded from sale-value rankings.");
            ImGui.TextWrapped("Unopened storage remains unknown. Shared storage can change while closed. Asking prices are not completed sales; placed furnishings are separate from stored items. Retainer page numbers follow saved containers and may differ from the game's sorted layout.");
        }
        if (this.plugin.StorageReadError is { } error) ImGui.TextWrapped(error);
        if (this.storageClearNotice is { } notice) ImGui.TextWrapped(notice);
        var captures = this.plugin.ObservedStorageView;
        this.storageOpenSources.RemoveWhere(source => !captures.Any(c => c.Category == source.Category && c.SourceKey == source.SourceKey));
        ImGui.TextDisabled($"{captures.GroupBy(c => (c.Category, c.SourceKey)).Count()} sources · {captures.Sum(c => c.Items.Length)} occupied slots");
        var clearSearchWidth = ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(Math.Max(1, ImGui.GetContentRegionAvail().X - clearSearchWidth - ImGui.GetStyle().ItemSpacing.X));
        ImGui.InputTextWithHint("##storage_search", "Search items, source names or item IDs", ref this.storageSearch, 128);
        ImGui.SameLine();
        ImGui.BeginDisabled(this.storageSearch.Length == 0);
        if (ChatTheme.Button("X##clear_storage_search", new Vector2(clearSearchWidth, 0))) this.storageSearch = string.Empty;
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("Clear storage search");
        ImGui.Checkbox("Show empty containers", ref this.storageShowEmpty);
        ImGui.SameLine();
        var collapse = ImGui.SmallButton("Collapse all");
        if (collapse)
        {
            this.storageSearch = "";
            // Sources may be hidden by a closed category or the search filter.
            this.storageOpenSources.Clear();
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(this.plugin.ContextCacheLoading || captures.Length == 0);
        if (ImGui.SmallButton("Clear all saved storage")) this.RequestStorageClear("all saved storage");
        ImGui.EndDisabled();
        ImGui.Separator();

        var query = this.storageSearch.Trim();
        var matched = captures.Where(c => StorageBrowser.Matches(c, query)).ToArray();
        if (captures.Length == 0) ImGui.TextWrapped("No saved storage yet. Open a storage menu in game to record it.");
        else if (matched.Length == 0) ImGui.TextUnformatted("No matching items or sources.");
        foreach (var category in StorageSnapshot.Categories)
        {
            var categoryCaptures = matched.Where(c => c.Category == category).ToArray();
            if (categoryCaptures.Length == 0) continue;
            ImGui.PushID(category);
            var sources = categoryCaptures.GroupBy(c => c.SourceKey).OrderBy(g =>
                g.OrderByDescending(c => c.CapturedAtUtc).First().SourceLabel, StringComparer.OrdinalIgnoreCase).ToArray();
            if (collapse) ImGui.SetNextItemOpen(false);
            else if (query.Length > 0) ImGui.SetNextItemOpen(true);
            if (ImGui.CollapsingHeader($"{StorageBrowser.CategoryLabel(category)} ({sources.Length})###category"))
            {
                ImGui.Indent();
                ImGui.BeginDisabled(this.plugin.ContextCacheLoading);
                if (ImGui.SmallButton("Clear this category"))
                    this.RequestStorageClear(StorageBrowser.CategoryLabel(category).ToLowerInvariant(), category);
                ImGui.EndDisabled();
                foreach (var source in sources)
                {
                    var allSource = captures.Where(c => c.Category == category && c.SourceKey == source.Key).ToArray();
                    var newest = allSource.OrderByDescending(c => c.CapturedAtUtc).First();
                    ImGui.PushID(source.Key);
                    var sourceId = (category, source.Key);
                    ImGui.SetNextItemOpen(query.Length > 0 || this.storageOpenSources.Contains(sourceId));
                    var sourceOpen = ImGui.CollapsingHeader($"{newest.SourceLabel} · {allSource.Sum(c => c.Items.Length)} occupied slots###source");
                    if (sourceOpen) this.storageOpenSources.Add(sourceId);
                    else this.storageOpenSources.Remove(sourceId);
                    if (sourceOpen)
                    {
                        ImGui.Indent();
                        ImGui.TextWrapped($"{allSource.Length} recorded containers · last observed {newest.CapturedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
                        ImGui.BeginDisabled(this.plugin.ContextCacheLoading);
                        if (ImGui.SmallButton("Clear this source")) this.RequestStorageClear(newest.SourceLabel, category, source.Key);
                        ImGui.EndDisabled();
                        this.DrawStorageItems(source.ToArray(), query);
                        ImGui.Unindent();
                    }
                    ImGui.PopID();
                }
                ImGui.Unindent();
            }
            ImGui.PopID();
        }

        // Draw after group ID stacks unwind so every clear button uses the same popup.
        if (this.openStorageClear)
        {
            ImGui.OpenPopup("clear_other_storage");
            this.openStorageClear = false;
        }
        ImGui.SetNextWindowSizeConstraints(new Vector2(330, 0), new Vector2(550, 900));
        if (ImGui.BeginPopup("clear_other_storage"))
        {
            ImGui.TextWrapped($"Clear saved observations for {this.storageClearLabel}?");
            ImGui.TextWrapped("Only CompAI's saved records are removed. Enabled readers can record loaded containers again. Turn off the corresponding reader first if you want the records to stay cleared.");
            if (ImGui.Button("Clear saved records"))
            {
                this.storageClearNotice = this.storageClearRequest is { } request && this.plugin.ClearObservedStorage(request)
                    ? "Saved observations cleared." : "Character context changed. Select the source again to clear its records.";
                this.storageClearRequest = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) { this.storageClearRequest = null; ImGui.CloseCurrentPopup(); }
            ImGui.EndPopup();
        }
        else this.storageClearRequest = null;
    }

    private void RequestStorageClear(string label, string? category = null, string? sourceKey = null)
    {
        this.storageClearRequest = this.plugin.PrepareStorageClear(category, sourceKey);
        this.storageClearLabel = label;
        this.openStorageClear = this.storageClearRequest is not null;
    }

    private void DrawStorageItems(StorageContainerCapture[] captures, string query)
    {
        var rows = captures.OrderBy(c => c.Container, StringComparer.Ordinal).SelectMany(c =>
            c.Items.Where(item => StorageBrowser.ContainerMatches(c, query) || StorageBrowser.Matches(item.Name, query) ||
                item.BaseItemId.ToString(CultureInfo.InvariantCulture) == query).Select(item => (Container: c, Item: item))).ToArray();
        var showPrices = captures.Any(c => c.Container == "Current market listings");
        if (rows.Length > 0 && ImGui.BeginTable("##storage_items", showPrices ? 4 : 3,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 3);
            ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 55);
            ImGui.TableSetupColumn("Location", ImGuiTableColumnFlags.WidthStretch, 2);
            if (showPrices) ImGui.TableSetupColumn("Asking price", ImGuiTableColumnFlags.WidthFixed, 95);
            ImGui.TableHeadersRow();
            foreach (var row in rows)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); ImGui.TextWrapped(row.Item.Name + (row.Item.IsHq ? " HQ" : ""));
                if (row.Item.Materia is { } materia && ImGui.IsItemHovered())
                {
                    var attached = materia.Where(m => !m.IsEmpty).ToArray();
                    ImGui.SetTooltip(attached.Length == 0 ? "No attached materia." :
                        string.Join("\n", attached.Select(m => $"Materia slot {m.Slot + 1}: " +
                            (m.Name ?? $"Unknown materia #{m.MateriaId}, grade index {m.Grade}"))));
                }
                ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(row.Item.Quantity.ToString("N0"));
                ImGui.TableSetColumnIndex(2); ImGui.TextWrapped($"{StorageBrowser.ContainerLabel(row.Container.Container)} · slot {row.Item.Slot + 1}");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Observed {row.Container.CapturedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
                if (showPrices)
                {
                    ImGui.TableSetColumnIndex(3);
                    ImGui.TextUnformatted(row.Item.UnitPriceGil is { } price ? $"{price:N0} gil each" : "—");
                }
            }
            ImGui.EndTable();
        }
        var empty = captures.Where(c => c.Items.Length == 0).ToArray();
        if (this.storageShowEmpty)
            foreach (var container in empty) ImGui.TextDisabled($"{StorageBrowser.ContainerLabel(container.Container)} · empty when observed");
        else if (empty.Length > 0) ImGui.TextDisabled($"{empty.Length} empty containers hidden");
    }
}
