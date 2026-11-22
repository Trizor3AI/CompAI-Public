using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;

namespace CompAI;

internal sealed partial class SettingsWindow
{
    private Task<ShopDiagnosticCapture>? shopDiagnosticTask;
    private string? shopDiagnosticNotice;

    private void UpdateShopDiagnostic()
    {
        if (this.shopDiagnosticTask is not { IsCompleted: true } task) return;
        this.shopDiagnosticTask = null;
        if (!task.IsCompletedSuccessfully)
        {
            this.shopDiagnosticNotice = "Diagnostic unavailable; nothing copied.";
            return;
        }
        var capture = task.Result;
        if (!this.plugin.CanCopyShopDiagnostic(capture))
            this.shopDiagnosticNotice = "Character or permission changed; nothing copied.";
        else
        {
            if (capture.Result.Text is { } text) ImGui.SetClipboardText(text);
            this.shopDiagnosticNotice = capture.Result.Status;
        }
    }

    private void DrawShopDiagnostic()
    {
        ImGui.BeginDisabled(!this.plugin.Access.Shops || this.shopDiagnosticTask is not null);
        if (ImGui.Button("Copy shop diagnostic"))
        {
            this.shopDiagnosticNotice = null;
            this.shopDiagnosticTask = this.plugin.CaptureShopDiagnostic();
        }
        ImGui.EndDisabled();
        ShowItemHelp("Keep the affected shop open. Copies bounded shop readiness flags and public offer details; when shop highlighting is enabled it also includes the last renderer check, even after the highlight expires. No character/account identifiers or addresses. Requires shop access.");
        if (this.shopDiagnosticTask is not null) ImGui.TextDisabled("Reading...");
        else if (this.shopDiagnosticNotice is { } notice) ImGui.TextWrapped(notice);
        ImGui.Spacing();
    }
}
