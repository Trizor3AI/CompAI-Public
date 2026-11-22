using System;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;

namespace CompAI;

internal sealed class QuestPanel
{
    private const int PageSize = 50;
    private static readonly string[] Filters = ["Active", "Observed completed", "All known quests"];
    private string search = "";
    private int filter;
    private int page;
    private QuestSnapshot? displayed;
    private QuestObservation[] rows = [];
    private int activeCount;
    private int completedCount;

    internal void Draw(Plugin plugin)
    {
        var view = plugin.QuestView;
        ImGui.TextWrapped("Quest access is on. While answering, the AI can list active or observed completed quests and look up saved records by name or ID. Each reply uses the journal saved when you press Send. Clear removes this character's quest cache and turns quest access off.");
        ImGui.TextWrapped(plugin.ContextCacheStatus);
        ImGui.BeginDisabled(view.Pending || !plugin.ContextCanCapture);
        if (ImGui.Button(view.Snapshot is null ? "Capture quests" : "Refresh now")) plugin.CaptureQuests();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(plugin.ContextCacheLoading);
        if (ImGui.Button("Clear")) plugin.ClearQuests();
        ImGui.EndDisabled();
        view = plugin.QuestView;
        if (view.Pending) ImGui.TextUnformatted(view.Snapshot is null ? "Reading quests..." : "Updating; showing cached quests...");
        if (plugin.QuestAutoUpdating)
        {
            ImGui.TextUnformatted("Auto-update active");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(plugin.QuestLastChecked is { } checkedAt
                    ? $"Last checked {checkedAt:yyyy-MM-dd HH:mm:ss} UTC. Local checks do not use AI tokens."
                    : "Waiting for quest state to become available. Local checks do not use AI tokens.");
        }
        if (view.Snapshot is not { } snapshot)
        {
            ImGui.TextWrapped(view.UnavailableReason ?? "Quests unavailable.");
            this.displayed = null;
            this.rows = [];
            return;
        }

        ImGui.TextWrapped($"Snapshot updated {snapshot.CapturedAtUtc:yyyy-MM-dd HH:mm:ss} UTC.");
        if (view.Stale || view.UnavailableReason is not null)
            ImGui.TextWrapped(view.UnavailableReason ?? "Cached state; waiting for an update.");
        if (!ReferenceEquals(this.displayed, snapshot))
        {
            this.activeCount = 0;
            this.completedCount = 0;
            foreach (var quest in snapshot.Quests)
            {
                if (quest.Accepted) this.activeCount++;
                if (quest.Completed == true) this.completedCount++;
            }
        }
        ImGui.TextUnformatted($"{this.activeCount} active; {this.completedCount} observed completed.");
        ImGui.TextWrapped("A missing completion flag is not proof that a quest was never completed. Sequence is the game's raw progress value; live objective counters are unavailable.");
        if (ImGui.CollapsingHeader("Coverage and diagnostics"))
        {
            ImGui.TextWrapped(snapshot.ReadinessNote);
            ImGui.TextWrapped(snapshot.CoverageNote);
            if (snapshot.CatalogNote is { } catalogNote) ImGui.TextWrapped(catalogNote);
            ImGui.TextUnformatted($"Catalog: {snapshot.CatalogRows} rows; {snapshot.CompletionChecks} completion checks.");
            ImGui.TextWrapped($"Source: {snapshot.SourceBuild}; capture took {snapshot.CaptureDurationMs:F1} ms.");
        }
        ImGui.SetNextItemWidth(Math.Min(220, ImGui.GetContentRegionAvail().X));
        var changed = ImGui.Combo("View", ref this.filter, Filters, Filters.Length);
        var clearSearchWidth = ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(Math.Max(1, ImGui.GetContentRegionAvail().X - clearSearchWidth - ImGui.GetStyle().ItemSpacing.X));
        changed |= ImGui.InputTextWithHint("##quest_search", "Search quests by name or ID", ref this.search, 200);
        ImGui.SameLine();
        ImGui.BeginDisabled(this.search.Length == 0);
        if (ChatTheme.Button("X##clear_quest_search", new Vector2(clearSearchWidth, 0)))
        {
            this.search = string.Empty;
            changed = true;
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("Clear quest search");
        if (changed || !ReferenceEquals(this.displayed, snapshot))
        {
            this.displayed = snapshot;
            if (changed) this.page = 0;
            this.rows = snapshot.Quests.Where(q =>
                    (this.filter == 0 ? q.Accepted : this.filter != 1 || q.Completed == true) &&
                    (this.search.Length == 0 || (q.Name?.Contains(this.search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                     q.NativeId.ToString().Contains(this.search, StringComparison.Ordinal) ||
                     (q.RowId?.ToString().Contains(this.search, StringComparison.Ordinal) ?? false)))
                .OrderBy(q => q.Name ?? "", StringComparer.CurrentCultureIgnoreCase).ThenBy(q => q.NativeId).ToArray();
            this.page = Math.Min(this.page, Math.Max(0, (this.rows.Length - 1) / PageSize));
        }
        if (ImGui.Button("Copy filtered list")) ImGui.SetClipboardText(this.CopyRows(snapshot, view));
        var pages = Math.Max(1, (this.rows.Length + PageSize - 1) / PageSize);
        ImGui.TextUnformatted($"{this.rows.Length} matches; page {this.page + 1}/{pages}");
        if (pages > 1)
        {
            ImGui.BeginDisabled(this.page == 0);
            if (ImGui.Button("Previous")) this.page--;
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(this.page + 1 >= pages);
            if (ImGui.Button("Next")) this.page++;
            ImGui.EndDisabled();
        }
        ImGui.Separator();
        if (this.rows.Length == 0) ImGui.TextWrapped("No matches in this capture and filter; this does not establish full journal coverage.");
        ImGui.BeginChild("##quest_results", new Vector2(0, Math.Max(100, ImGui.GetContentRegionAvail().Y)), false);
        if (ImGui.BeginTable("##quests", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Quest", ImGuiTableColumnFlags.WidthStretch, 3);
            ImGui.TableSetupColumn("Observed state", ImGuiTableColumnFlags.WidthStretch, 2);
            ImGui.TableHeadersRow();
            foreach (var quest in this.rows.Skip(this.page * PageSize).Take(PageSize))
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextWrapped(quest.Name ?? $"Quest {quest.NativeId} (name unavailable)");
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted($"Native ID: {quest.NativeId}; sheet row: {quest.RowId?.ToString() ?? "unknown"}");
                    ImGui.TextUnformatted($"Repeatable: {quest.IsRepeatable?.ToString() ?? "unknown"}");
                    if (quest.UnavailableReason is { } unavailable) ImGui.TextWrapped(unavailable);
                    if (quest.Accepted)
                        ImGui.TextUnformatted($"Raw variables: {string.Join(", ", quest.Variables)}");
                    ImGui.EndTooltip();
                }
                if (quest.JournalText is { } journalText)
                {
                    ImGui.PushID(quest.NativeId);
                    if (ImGui.TreeNode("Journal text"))
                    {
                        ImGui.TextWrapped(journalText);
                        ImGui.TreePop();
                    }
                    ImGui.PopID();
                }
                ImGui.TableSetColumnIndex(1);
                ImGui.TextWrapped(StateLabel(quest));
            }
            ImGui.EndTable();
        }
        ImGui.EndChild();
    }

    private static string StateLabel(QuestObservation quest)
    {
        var completed = quest.Completed switch
        {
            true => "Completion flag set",
            false => "Completion not observed",
            null => "Completion unknown",
        };
        return quest.Accepted ? $"Active; sequence {quest.Sequence?.ToString() ?? "unknown"}\n{completed}" : completed;
    }

    private string CopyRows(QuestSnapshot snapshot, QuestCaptureView view)
    {
        var text = new StringBuilder();
        text.AppendLine($"Quest capture: {snapshot.CapturedAtUtc:O}; filter: {Filters[this.filter]}; search: {this.search}");
        if (view.Stale || view.Pending) text.AppendLine("Cached values; refresh pending/unavailable. " + view.UnavailableReason);
        text.AppendLine(snapshot.ReadinessNote);
        text.AppendLine(snapshot.CoverageNote);
        if (snapshot.CatalogNote is { } catalogNote) text.AppendLine(catalogNote);
        text.AppendLine("Sequence is the game's raw progression value, not a universal step number. Objective text/counters unavailable.");
        foreach (var quest in this.rows)
        {
            text.AppendLine($"{quest.Name ?? "[name unavailable]"} | row {quest.RowId?.ToString() ?? "unknown"} / native {quest.NativeId} | {StateLabel(quest).Replace('\n', ';')}");
            if (quest.JournalText is { } journalText) text.AppendLine("Journal text: " + journalText);
        }
        return text.ToString();
    }
}
