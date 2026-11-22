using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;

namespace CompAI;

internal sealed partial class SettingsWindow
{
    private Task<ActivityDiagnosticCapture>? activityDiagnosticTask;
    private string? activityDiagnosticNotice;
    private int activityDiagnosticTarget;

    private void UpdateActivityDiagnostic()
    {
        if (this.activityDiagnosticTask is not { IsCompleted: true } task) return;
        this.activityDiagnosticTask = null;
        if (!task.IsCompletedSuccessfully)
        {
            this.activityDiagnosticNotice = "Diagnostic unavailable; nothing copied.";
            return;
        }
        var capture = task.Result;
        if (!this.plugin.CanCopyActivityDiagnostic(capture))
            this.activityDiagnosticNotice = "Character changed; nothing copied.";
        else
        {
            if (capture.Result.Text is { } text) ImGui.SetClipboardText(text);
            this.activityDiagnosticNotice = capture.Result.Status;
        }
    }

    private void DrawActivityDiagnostic()
    {
        ImGui.TextWrapped("Copy a selected activity or Collection menu for manual troubleshooting.");
        ImGui.BeginDisabled(this.activityDiagnosticTask is not null);
        ImGui.SetNextItemWidth(330);
        // Show the full list instead of the compact
        // combo's scrolling popup, which can hide earlier targets after selection.
        if (ImGui.BeginCombo("Menu##activity-diagnostic", ActivityMenuDiagnostic.Labels[this.activityDiagnosticTarget], ImGuiComboFlags.HeightLarge))
        {
            for (var index = 0; index < ActivityMenuDiagnostic.Labels.Length; ++index)
                if (ImGui.Selectable(ActivityMenuDiagnostic.Labels[index], index == this.activityDiagnosticTarget))
                    this.activityDiagnosticTarget = index;
            ImGui.EndCombo();
        }
        if ((ActivityDiagnosticTarget)this.activityDiagnosticTarget == ActivityDiagnosticTarget.ArchiveItem)
            ImGui.TextWrapped("Archive-item targets the separate ArchiveItem agent. It is not a verified Portable Archive diagnostic; opening Portable Archive may leave this target unavailable.");
        if ((ActivityDiagnosticTarget)this.activityDiagnosticTarget == ActivityDiagnosticTarget.Description)
            ImGui.TextWrapped("Open Portable Archive and select an entry. Includes the reader's acceptance or failure stage; no AI question is needed.");
        if ((ActivityDiagnosticTarget)this.activityDiagnosticTarget == ActivityDiagnosticTarget.GoldSaucer)
            ImGui.TextWrapped("For Mahjong, select its tab and let it load. Includes the reader's acceptance or failure stage; no AI question is needed.");
        if ((ActivityDiagnosticTarget)this.activityDiagnosticTarget == ActivityDiagnosticTarget.CosmicMissions)
            ImGui.TextWrapped("Open Stellar Missions → Mission Log and select the failing job/list. Includes the reader's rejection stage and bounded counts; no AI question is needed.");
        if ((ActivityDiagnosticTarget)this.activityDiagnosticTarget == ActivityDiagnosticTarget.FieldRecord)
            ImGui.TextWrapped("Select a record and include its displayed number and whether it is registered when sharing. Raw page and selection values do not establish registration.");
        if ((ActivityDiagnosticTarget)this.activityDiagnosticTarget == ActivityDiagnosticTarget.FreeCompany)
            ImGui.TextWrapped("Use the main Free Company Topics, Rank, Actions or Info tab. Members, Activity and inspection windows are excluded. Visible company text and names may be included; review before sharing.");
        if (ImGui.Button("Copy selected menu diagnostic"))
        {
            this.activityDiagnosticNotice = null;
            this.activityDiagnosticTask = this.plugin.CaptureActivityDiagnostic((ActivityDiagnosticTarget)this.activityDiagnosticTarget);
        }
        ImGui.SameLine();
        if (ImGui.Button("Copy Field Record diagnostic"))
        {
            this.activityDiagnosticTarget = (int)ActivityDiagnosticTarget.FieldRecord;
            this.activityDiagnosticNotice = null;
            this.activityDiagnosticTask = this.plugin.CaptureActivityDiagnostic(ActivityDiagnosticTarget.FieldRecord);
        }
        ImGui.EndDisabled();
        ShowItemHelp("Open the selected menu first. Copies bounded visible text and node/component IDs to the clipboard. Mogpendium also includes list counts and selection metadata for troubleshooting. Long text or large layouts are explicitly marked as partial; short fields are retained where possible. Visible text can include in-game names or custom names. Review it and choose whether to share it. No AI permission is needed or granted; no file or automatic capture is created.");
        if (this.activityDiagnosticTask is not null) ImGui.TextDisabled("Reading...");
        else if (this.activityDiagnosticNotice is { } notice) ImGui.TextWrapped(notice);
        ImGui.Spacing();
    }
}
