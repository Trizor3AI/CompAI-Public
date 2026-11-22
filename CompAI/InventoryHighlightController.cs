using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace CompAI;

/// <summary>Framework update captures; Draw uses managed rectangles only. Start is an explicit local click.</summary>
internal sealed class InventoryHighlightController(IGameGui gui, IGameInventory inventory)
{
    private readonly object gate = new();
    private readonly InventoryHighlightState state = new();
    private readonly InventoryHighlightReader reader = new(gui, inventory);
    private bool selectPagePending;
    internal bool Active { get { lock (gate) return state.Active; } }
    internal bool Visible { get { lock (gate) return state.IsVisible(DateTimeOffset.UtcNow); } }
    internal string Status { get { lock (gate) return state.Status; } }

    internal unsafe bool Start(InventoryHighlightSelection selection, ulong ownerId, int sessionGeneration, int timeoutSeconds = 15)
    {
        lock (gate)
        {
            try
            {
                var before = reader.ReadState(ownerId);
                var selected = reader.ReadSelected(selection);
                var after = reader.ReadState(ownerId);
                if (before != after || !state.Start(selection, ownerId, sessionGeneration, timeoutSeconds, after, selected, DateTimeOffset.UtcNow))
                { state.Clear("Inventory changed or the item location is stale; request a fresh lookup."); return false; }
                // A manual Find click may open inventory and select the item's visual page.
                // Page selection waits for a ready addon below; it is never repeated after success.
                // No item interaction, native styling, or inventory mutation is performed.
                var ui = UIModule.Instance();
                if (ui == null) { state.Clear("Inventory UI is unavailable; open inventory and retry."); return false; }
                // UIModuleInterface.OpenInventory documents0=Inventory,1=KeyItems. Unlike
                // AgentInventory.Show, this is the UI module's configured inventory entry point.
                if (!ui->IsInventoryOpen()) ui->OpenInventory(0);
                selectPagePending = true;
                return true;
            }
            catch (Exception ex)
            { state.Clear($"Inventory highlight unavailable ({ex.GetType().Name}); open inventory and retry."); return false; }
        }
    }

    internal void Update(bool enabled, bool inventoryAllowed, ulong ownerId, int sessionGeneration, bool loading)
    {
        lock (gate)
        {
            if (!state.Check(enabled, inventoryAllowed, ownerId, sessionGeneration, loading, DateTimeOffset.UtcNow)) return;
            try
            {
                var before = reader.ReadState(ownerId);
                if (before != state.Baseline) { state.Clear("Inventory changed; select a fresh result before highlighting again."); return; }
                if (selectPagePending)
                {
                    var ready = reader.TrySelectPage(state.Selection!, ownerId, out var switched);
                    if (ready) selectPagePending = false;
                    if (!ready || switched)
                    {
                        state.Publish(before, reader.ReadState(ownerId), new(DateTimeOffset.UtcNow, [], null,
                            ready ? "Inventory page selected; waiting for it to settle." : "Waiting for inventory and its sort order to load."));
                        return;
                    }
                }
                var frame = reader.Capture(state.Selection!, ownerId);
                var after = reader.ReadState(ownerId);
                state.Publish(before, after, frame);
            }
            catch (Exception ex) { state.Clear($"Inventory highlight unavailable ({ex.GetType().Name})."); }
        }
    }

    internal void NotifyInventoryChanged() => Clear("Inventory changed; select a fresh result before highlighting again.");
    internal void Clear(string reason = "Inventory highlight cleared.") { lock (gate) state.Clear(reason); }

    internal void Draw()
    {
        InventoryHighlightFrame? frame;
        DateTimeOffset expires;
        lock (gate) { frame = state.VisibleFrame(DateTimeOffset.UtcNow); expires = state.ExpiresAt; }
        if (frame is null) return;
        var origin = ImGui.GetMainViewport().Pos;
        // Paint above the game but below plugin windows, so chat and the clear control remain readable.
        var draw = ImGui.GetBackgroundDrawList();
        foreach (var slot in frame.Slots)
        {
            if (slot.Selected) draw.AddRect(origin + slot.Min, origin + slot.Max, 0xFF33E6FF, 3f, ImDrawFlags.None, 3f);
            else draw.AddRectFilled(origin + slot.Min, origin + slot.Max, 0xB8000000, 2f);
        }
        lock (gate) state.MarkDrawn(frame, DateTimeOffset.UtcNow);
        if (frame.WindowTopRight is not { } corner) return;
        ImGui.SetNextWindowPos(origin + corner + new Vector2(-4, 4), ImGuiCond.Always, new Vector2(1, 0));
        ImGui.SetNextWindowBgAlpha(0.9f);
        var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove;
        if (ImGui.Begin("###CompAIInventoryHighlightClear", flags))
        {
            ImGui.TextUnformatted($"Find in bags · {Math.Max(0, (int)Math.Ceiling((expires - DateTimeOffset.UtcNow).TotalSeconds))}s");
            ImGui.SameLine();
            if (ImGui.SmallButton("X###ClearInventoryHighlight")) Clear();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Clear inventory highlighting");
        }
        ImGui.End();
    }
}
