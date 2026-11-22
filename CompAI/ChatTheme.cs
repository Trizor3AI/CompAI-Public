using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace CompAI;

internal sealed class ConnectionErrorClipboard
{
    private string? copiedError;
    private long showUntil;

    internal void Draw(string? error, string label = "Copy error")
    {
        if (error is null) return;
        if (ImGui.SmallButton(label))
        {
            ImGui.SetClipboardText(error);
            this.copiedError = error;
            this.showUntil = Environment.TickCount64 + 2000;
        }
        if (this.copiedError == error && Environment.TickCount64 < this.showUntil)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("Copied");
        }
    }
}

// Vanilla-inspired charcoal, grey controls and warm metal accents.
// Push before Window.Begin so the title bar and outer frame share the palette.
internal readonly struct ChatTheme : IDisposable
{
    internal static readonly Vector4 Gold = new(0.81f, 0.75f, 0.58f, 1f);
    internal static readonly Vector4 MutedGold = new(0.68f, 0.65f, 0.55f, 1f);

    internal static ChatTheme Push()
    {
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.115f, 0.115f, 0.11f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, new Vector4(0.14f, 0.14f, 0.13f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.82f, 0.82f, 0.79f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, new Vector4(0.49f, 0.49f, 0.46f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.56f, 0.49f, 0.33f, 0.9f));
        ImGui.PushStyleColor(ImGuiCol.BorderShadow, new Vector4(0.025f, 0.025f, 0.02f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TitleBg, new Vector4(0.14f, 0.14f, 0.13f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, new Vector4(0.20f, 0.20f, 0.18f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgCollapsed, new Vector4(0.14f, 0.14f, 0.13f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.23f, 0.23f, 0.22f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0.30f, 0.29f, 0.26f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, new Vector4(0.28f, 0.27f, 0.23f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.27f, 0.27f, 0.25f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.50f, 0.46f, 0.33f, 0.45f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.12f, 0.11f, 0.08f, 0.65f));
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.34f, 0.32f, 0.27f, 1f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.41f, 0.38f, 0.31f, 1f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(0.46f, 0.41f, 0.31f, 1f));
        ImGui.PushStyleColor(ImGuiCol.CheckMark, Gold);
        ImGui.PushStyleColor(ImGuiCol.Separator, new Vector4(0.37f, 0.35f, 0.29f, 0.55f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, new Vector4(0.10f, 0.10f, 0.09f, 0.5f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, new Vector4(0.40f, 0.40f, 0.37f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, new Vector4(0.52f, 0.51f, 0.45f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, MutedGold);
        ImGui.PushStyleColor(ImGuiCol.TextSelectedBg, new Vector4(0.56f, 0.49f, 0.30f, 0.4f));
        ImGui.PushStyleColor(ImGuiCol.ResizeGrip, new Vector4(0.56f, 0.49f, 0.33f, 0.18f));
        ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered, MutedGold);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripActive, Gold);
        ImGui.PushStyleColor(ImGuiCol.Tab, new Vector4(0.21f, 0.21f, 0.20f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TabHovered, new Vector4(0.39f, 0.37f, 0.31f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TabActive, new Vector4(0.34f, 0.32f, 0.26f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TabUnfocused, new Vector4(0.18f, 0.18f, 0.17f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TabUnfocusedActive, new Vector4(0.28f, 0.27f, 0.23f, 1f));
        ImGui.PushStyleColor(ImGuiCol.NavHighlight, Gold);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 3f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 5f);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 5f);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 11f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 7f));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(9f, 4f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12f, 10f));
        return new ChatTheme();
    }

    internal static void SectionHeading(string text)
    {
        var start = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetTextLineHeight() + 6f);
        ImGui.GetWindowDrawList().AddRectFilled(start, start + size,
            ImGui.GetColorU32(new Vector4(0.31f, 0.30f, 0.26f, 1f)), 3f);
        ImGui.GetWindowDrawList().AddText(start + new Vector2(7f, 3f),
            ImGui.GetColorU32(new Vector4(0.90f, 0.87f, 0.79f, 1f)), text);
        ImGui.Dummy(size);
    }

    // Keep the standard ImGui input behavior; draw only its quiet bevel beneath it.
    internal static bool Button(string label, Vector2 size = default)
    {
        var start = ImGui.GetCursorScreenPos();
        var padding = ImGui.GetStyle().FramePadding;
        var measured = ImGui.CalcTextSize(label, true) + padding * 2;
        var width = size.X < 0 ? Math.Max(1, ImGui.GetContentRegionAvail().X + size.X)
            : size.X == 0 ? measured.X : size.X;
        var height = size.Y == 0 ? measured.Y : size.Y;
        var end = start + new Vector2(width, height);
        var draw = ImGui.GetWindowDrawList();
        var top = ImGui.GetColorU32(new Vector4(0.34f, 0.34f, 0.32f, 1f));
        var bottom = ImGui.GetColorU32(new Vector4(0.15f, 0.15f, 0.14f, 1f));
        draw.AddRectFilledMultiColor(start + Vector2.One, end - Vector2.One, top, top, bottom, bottom);
        draw.AddRect(start, end, ImGui.GetColorU32(new Vector4(0.42f, 0.41f, 0.36f, 0.85f)), 3f);
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        var clicked = ImGui.Button(label, new Vector2(width, height));
        ImGui.PopStyleColor();
        return clicked;
    }

    public void Dispose()
    {
        ImGui.PopStyleVar(11);
        ImGui.PopStyleColor(35);
    }
}
