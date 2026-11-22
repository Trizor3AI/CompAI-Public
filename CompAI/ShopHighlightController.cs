using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace CompAI;

/// <summary>Manual local overlay. Native work stays on framework; drawing consumes managed bounds only.</summary>
internal sealed class ShopHighlightController(IGameGui gui)
{
    private readonly object gate = new();
    private readonly ShopHighlightReader reader = new(gui);
    private readonly ShopHighlightState state = new();
    internal bool Active { get { lock (gate) return state.Active; } }
    internal string Status { get { lock (gate) return state.Status; } }
    internal ShopHighlightDiagnostic Diagnostic { get { lock (gate) return state.Diagnostic; } }

    internal ShopHighlightBinding? CaptureBinding(MenuSnapshot frozen, ulong owner, int session, out string reason)
    {
        lock (gate)
        {
            var capture = reader.Capture(owner, out reason);
            if (capture is null) { state.Revoke(reason); return null; }
            var binding = state.Bind(frozen, capture, owner, session, DateTimeOffset.UtcNow);
            if (binding is null) reason = "This reply's saved offers do not match the current ordinary gil shop. Ask for a fresh shop lookup.";
            return binding;
        }
    }

    internal bool IsCurrent(ShopHighlightBinding binding)
    { lock (gate) return state.IsCurrent(binding, DateTimeOffset.UtcNow); }

    internal bool ValidateBinding(ShopHighlightBinding binding)
    {
        lock (gate)
        {
            if (!state.IsCurrent(binding, DateTimeOffset.UtcNow)) return false;
            var capture = reader.Capture(binding.OwnerId, out _);
            return state.Observe(capture, binding.OwnerId, binding.SessionGeneration, DateTimeOffset.UtcNow) &&
                state.IsCurrent(binding, DateTimeOffset.UtcNow);
        }
    }

    internal bool Start(ShopHighlightBinding binding, int row, ulong owner, int session, int seconds = 15)
    {
        lock (gate)
        {
            state.BeginAttempt(row);
            if (!ValidateBinding(binding))
            { state.Clear("This shop result is no longer current. Ask for a fresh shop lookup."); return false; }
            return state.Start(binding, row, owner, session, seconds, DateTimeOffset.UtcNow);
        }
    }

    internal void Update(bool allowed, ulong owner, int session, bool loading)
    {
        lock (gate)
        {
            if (!allowed || loading) { state.Revoke("Shop highlighting is unavailable for this session."); return; }
            var now = DateTimeOffset.UtcNow;
            if (!state.Watching(now)) { if (state.Active) state.Clear("Shop result expired."); return; }
            if (!state.CheckActive(now))
            {
                state.ObserveIdentity(reader.CaptureIdentity(owner), owner, session, now);
                return;
            }
            var capture = reader.Capture(owner, out _);
            if (!state.Observe(capture, owner, session, now) || !state.CheckActive(now)) return;
            try { state.Publish(reader.CaptureFrame(capture!, state.SelectedRow), DateTimeOffset.UtcNow); }
            catch (Exception) { state.Revoke("Shop highlight unavailable; ask for a fresh shop lookup."); }
        }
    }

    internal void Clear(string reason = "Shop highlight cleared.") { lock (gate) state.Clear(reason); }
    internal void Revoke(string reason = "Shop changed; ask for a fresh shop lookup.") { lock (gate) state.Revoke(reason); }
    internal void InvalidateNativeLifecycle() => Revoke("Shop closed or reopened; ask for a fresh shop lookup.");

    internal void Draw()
    {
        ShopHighlightFrame? frame;
        DateTimeOffset expires;
        lock (gate) { frame = state.VisibleFrame(DateTimeOffset.UtcNow); expires = state.ExpiresAt; }
        if (frame is null) return;
        var origin = ImGui.GetMainViewport().Pos;
        var draw = ImGui.GetBackgroundDrawList();
        if (frame.DimArea is { } area)
        {
            Span<ShopHighlightRectangle> regions = stackalloc ShopHighlightRectangle[4];
            var count = ShopHighlightDimming.Regions(area, frame.Row, regions);
            for (var index = 0; index < count; ++index)
                draw.AddRectFilled(origin + regions[index].Min, origin + regions[index].Max, 0xB8000000);
        }
        if (frame.Row is { } row)
            draw.AddRect(origin + row.Min, origin + row.Max, 0xFF33E6FF, 3f, ImDrawFlags.None, 3f);
        if (frame.WindowTopRight is not { } corner) return;
        ImGui.SetNextWindowPos(origin + corner + new Vector2(-4, 4), ImGuiCond.Always, new Vector2(1, 0));
        ImGui.SetNextWindowBgAlpha(0.9f);
        var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove;
        if (ImGui.Begin("###CompAIShopHighlightClear", flags))
        {
            ImGui.TextUnformatted($"Find in current shop · {Math.Max(0, (int)Math.Ceiling((expires - DateTimeOffset.UtcNow).TotalSeconds))}s");
            ImGui.SameLine();
            if (ImGui.SmallButton("X###ClearShopHighlight")) Clear();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Clear shop highlighting");
        }
        ImGui.End();
    }
}
