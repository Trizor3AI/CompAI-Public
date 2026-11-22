using System.Linq;
using System.Text.Json;
using Dalamud.Bindings.ImGui;

namespace CompAI;

internal static class PedigreePanel
{
    private static readonly string[] StatNames = { "Maximum speed", "Acceleration", "Endurance", "Stamina", "Cunning" };

    internal static void Draw(Plugin plugin)
    {
        ImGui.TextWrapped("Experimental pedigree reader. Start a scan, then hover each covering permission, retired registration or fledgling in your four carried bags for at least half a second.");
        ImGui.TextWrapped("Complete parent-star readings join your saved inventory automatically. They are available to questions while Inventory access is enabled. Scanning itself sends nothing.");
        var view = plugin.PedigreeView;
        ImGui.BeginDisabled(!plugin.ContextCanCapture);
        if (ImGui.Button(view.Scanning ? "Restart 60-second scan" : "Start 60-second scan")) plugin.StartPedigreeScan();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!view.Scanning);
        if (ImGui.Button("Stop")) plugin.StopPedigreeScan();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(plugin.ContextCacheLoading);
        if (ImGui.Button("Clear readings")) plugin.ClearPedigrees();
        ImGui.EndDisabled();
        if (view.Scanning) ImGui.TextUnformatted($"Scanning: {view.SecondsLeft}s remaining");
        ImGui.TextWrapped(view.Status);
        ImGui.TextUnformatted($"{view.Cards.Count(card => card.Reading is not null)} decoded, {view.Cards.Length} observed cards");
        var attachments = plugin.PedigreeAttachments;
        if (attachments.DecodedCount > 0)
        {
            ImGui.TextWrapped($"{attachments.SavedCount} of {attachments.DecodedCount} decoded readings attached to the saved inventory for the next question. Values describe that capture, not live inventory.");
            if (attachments.BlockReason is { } blockReason) ImGui.TextWrapped(blockReason);
            if (attachments.RefreshFailure is { } failure)
                ImGui.TextWrapped("Inventory refresh did not complete: " + failure + " The previous saved capture is retained.");
            if (attachments.MissingCount > 0)
            {
                ImGui.BeginDisabled(!plugin.ContextCanCapture || attachments.CapturePending);
                if (ImGui.Button("Retry inventory refresh")) plugin.Capture();
                ImGui.EndDisabled();
            }
        }
        ImGui.TextWrapped("Inventory changes and logout clear current readings. Saved pedigrees remain historical until inventory refresh replaces them; re-scan cards after login or reload. Bag position and item ID cannot identify an individual card reliably.");
        ImGui.BeginDisabled(view.Cards.Length == 0);
        var collapseAll = ImGui.Button("Collapse all");
        ImGui.EndDisabled();
        ImGui.Separator();
        foreach (var card in view.Cards)
        {
            ImGui.PushID(card.Key);
            var state = card.Reading is not null ? "decoded" : "unavailable";
            if (collapseAll) ImGui.SetNextItemOpen(false, ImGuiCond.Always);
            if (ImGui.TreeNode($"{card.Sample.Bag} / slot {card.Sample.Slot} - {card.Sample.ItemName} ({state})"))
            {
                ImGui.TextUnformatted($"Observed {card.Sample.CapturedAtUtc:HH:mm:ss} UTC; slot numbers start at 0.");
                if (card.Reading is { } reading)
                {
                    ImGui.TextUnformatted($"Grade {reading.Grade}, {reading.Sex}, {reading.Kind.Replace('_', ' ')}");
                    ImGui.TextWrapped("Parentage stars only. These are not the bird's own racing stats.");
                    if (ImGui.BeginTable("##parent_stars", 3, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg))
                    {
                        ImGui.TableSetupColumn("Stat");
                        ImGui.TableSetupColumn("Father (blue)");
                        ImGui.TableSetupColumn("Mother (red)");
                        ImGui.TableHeadersRow();
                        for (var stat = 0; stat < 5; stat++)
                        {
                            ImGui.TableNextRow(); ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(StatNames[stat]);
                            ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted($"{reading.FatherStars[stat]} / 4");
                            ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted($"{reading.MotherStars[stat]} / 4");
                        }
                        ImGui.EndTable();
                    }
                }
                else ImGui.TextWrapped(card.Reason);
                if (ImGui.Button("Copy local diagnostic"))
                {
                    // Only this card's managed tooltip copy, never pointers, credentials or other UI.
                    ImGui.SetClipboardText(JsonSerializer.Serialize(new
                    {
                        card.Sample.Bag, card.Sample.Slot, card.Sample.ItemId, card.Sample.CapturedAtUtc,
                        card.Sample.StatLabels, card.Reason,
                        nodes = card.Sample.Nodes.Select(node => new
                        {
                            node.NodeId, node.X, node.Y, node.Text, node.Runs,
                        }),
                    }, new JsonSerializerOptions { WriteIndented = true }));
                }
                ImGui.TreePop();
            }
            ImGui.PopID();
        }
        ImGui.Separator();
        ImGui.TextWrapped("Not read: abilities, colour, breeding capacity, own racing stats, the registered racing bird, or cards you have not hovered. Proof of Covering is not a pedigree card.");
    }
}
