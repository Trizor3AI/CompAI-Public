using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;

namespace CompAI;

internal sealed partial class SettingsWindow
{
    private Task<ChallengeDiagnosticCapture>? challengeDiagnosticTask;
    private string? challengeDiagnosticNotice;

    private void DrawChallengeDiagnostic()
    {
        this.UpdateShopDiagnostic();
        this.UpdateActivityDiagnostic();
        if (this.challengeDiagnosticTask is { IsCompleted: true } task)
        {
            this.challengeDiagnosticTask = null;
            if (task.IsCompletedSuccessfully)
            {
                var capture = task.Result;
                if (!this.plugin.CanCopyChallengeDiagnostic(capture))
                    this.challengeDiagnosticNotice = "Character or permission changed; nothing copied.";
                else
                {
                    if (capture.Result.Text is { } text) ImGui.SetClipboardText(text);
                    this.challengeDiagnosticNotice = capture.Result.Status;
                }
            }
            else this.challengeDiagnosticNotice = "Diagnostic unavailable; nothing copied.";
        }
        if (!ImGui.CollapsingHeader("Reader diagnostics")) return;
        ImGui.TextWrapped("Copies the selected open menu to your clipboard for troubleshooting. No AI request or saved file.");
        ImGui.BeginDisabled(!this.plugin.Access.ChallengeLog || this.challengeDiagnosticTask is not null);
        if (ImGui.Button("Copy Challenge Log diagnostic"))
        {
            this.challengeDiagnosticNotice = null;
            this.challengeDiagnosticTask = this.plugin.CaptureChallengeDiagnostic();
        }
        ImGui.EndDisabled();
        ShowItemHelp("Open the affected Challenge Log tab first. Includes displayed challenge IDs/statuses and visible text/node IDs only. Requires Challenge Log access.");
        if (this.challengeDiagnosticTask is not null) ImGui.TextDisabled("Reading...");
        else if (this.challengeDiagnosticNotice is { } notice) ImGui.TextWrapped(notice);
        ImGui.Spacing();
        this.DrawShopDiagnostic();
        this.DrawActivityDiagnostic();
    }
}
