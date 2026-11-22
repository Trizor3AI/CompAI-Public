using System;
using Dalamud.Bindings.ImGui;

namespace CompAI;

internal sealed partial class SettingsWindow
{
    private string? storageActionError;

    private void DrawStorage()
    {
        ImGui.TextWrapped("Review local files, sizes and retention. Refresh measures files locally; counts stay cached until the next refresh.");
        var storage = this.plugin.StorageOverview;
        ImGui.BeginDisabled(storage.Busy);
        if (ImGui.Button(storage.Busy ? "Refreshing..." : "Refresh sizes"))
        {
            this.storageActionError = null;
            this.plugin.RefreshStorageOverview();
        }
        ImGui.EndDisabled();
        if (storage.RefreshedAtUtc is { } refreshed)
            ImGui.TextWrapped($"Last measured: {refreshed.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        if (storage.Error is { } error) ImGui.TextWrapped(error);
        if (this.storageActionError is { } actionError) ImGui.TextWrapped(actionError);
        ImGui.Spacing();

        foreach (var category in storage.Categories)
        {
            ImGui.PushID((int)category.Category);
            var size = category.Bytes is { } bytes
                ? (category.Complete ? "" : "at least ") + FormatStorageSize(bytes)
                : "size unknown";
            var open = ImGui.CollapsingHeader($"{category.Name} - {size}###category");
            // Incomplete or failed measurements remain visible with details closed.
            var showWarning = storage.RefreshedAtUtc is not null && !category.Complete &&
                category.Category != StorageCategory.CompanionProfile;
            if (showWarning)
                ImGui.TextWrapped((category.Bytes is null ? "Not measured: " : "Partial measurement: ") + category.Status);
            if (open)
            {
                if (category.FileCount is { } files && category.FolderCount is { } folders)
                    ImGui.TextWrapped($"{(category.Complete ? "" : "At least ")}{files:N0} files, {folders:N0} subfolders");
                if (!showWarning) ImGui.TextWrapped(category.Status);
                ImGui.TextWrapped(category.Retention);
                ImGui.Spacing();
                ImGui.TextWrapped(category.Path);
                if (ImGui.Button("Open folder"))
                    this.storageActionError = this.plugin.OpenStorageFolder(category.Category);
                SameLineIfFits("Copy path");
                if (ImGui.Button("Copy path")) ImGui.SetClipboardText(category.Path);
                ImGui.Spacing();
            }
            ImGui.PopID();
        }

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Measurement details"))
        {
            ImGui.TextWrapped("The provider profile is never scanned. Other categories use bounded metadata checks, not file-content reads. Sizes are file lengths, not allocated disk space. Unknown sizes are not zero; partial counts are lower bounds. Subfolder counts exclude each category's root.");
            ImGui.TextWrapped("This page does not delete data. Open a folder to manage files yourself; exports and recovery copies can outlive the active chat or cache.");
            if (storage.ElapsedMilliseconds is { } elapsed)
                ImGui.TextWrapped($"Last refresh took {elapsed:N0} ms.");
        }
    }

    private static string FormatStorageSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes:N0} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024d:N1} KiB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024):N1} MiB";
        return $"{bytes / (1024d * 1024 * 1024):N1} GiB";
    }
}
