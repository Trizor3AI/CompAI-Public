using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace CompAI;

internal static class SupportButton
{
    private const string SupportUrl = "https://ko-fi.com/trizor3";
    internal const string OpenFailedMessage = "Could not open the browser. Visit ko-fi.com/trizor3 manually.";

    internal static Vector2 GetSize(float maximumWidth, bool wordmark = false)
    {
        var aspectRatio = wordmark ? 160f / 46f : 321f / 258f;
        // The staging header displays the wordmark at five times the base font size.
        if (wordmark) maximumWidth = Math.Min(maximumWidth, 5f * ImGui.GetFontSize());
        var height = Math.Min(ImGui.GetFrameHeight(), Math.Max(1f, maximumWidth) / aspectRatio);
        return new Vector2(height * aspectRatio, height);
    }

    internal static void Draw(Vector2 size, ref bool websiteOpenFailed, bool wordmark = false)
    {
        var sharedTexture = Plugin.TextureProvider.GetFromManifestResource(
            typeof(SupportButton).Assembly, wordmark ? "CompAI.Assets.kofi_wordmark.png" : "CompAI.Assets.kofi_symbol.png");
        bool clicked;
        if (sharedTexture.TryGetWrap(out var texture, out _))
        {
            // This shared wrap is valid only for this frame; Dalamud owns its lifetime.
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);
            ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
            ImGui.PushID("ko_fi_support");
            try { clicked = ImGui.ImageButton(texture.Handle, size, 0); }
            finally
            {
                ImGui.PopID();
                ImGui.PopStyleColor();
                ImGui.PopStyleVar();
            }
        }
        else
        {
            clicked = ImGui.Button(wordmark ? "Ko-fi" : "K", size);
        }

        if (clicked)
        {
            try
            {
                Dalamud.Utility.Util.OpenLink(SupportUrl);
                websiteOpenFailed = false;
            }
            catch { websiteOpenFailed = true; }
        }
        if (!ImGui.IsItemHovered()) return;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(420f);
        ImGui.TextUnformatted(websiteOpenFailed ? OpenFailedMessage
            : "Support me on Ko-fi\nOptional support for CompAI's development. Opens ko-fi.com/trizor3 in your browser.");
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }
}
