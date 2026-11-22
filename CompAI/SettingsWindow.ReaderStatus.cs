using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;

namespace CompAI;

internal sealed partial class SettingsWindow
{
    private static void ReaderStatusDetail(IReadOnlyDictionary<string, ReaderStatusRow> statuses, string key, string label)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("  " + label);
        ImGui.TableSetColumnIndex(1);
        statuses.TryGetValue(key, out var row);
        var state = row?.State ?? "On";
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(state).X));
        ImGui.TextDisabled(state);
        ShowItemHelp(row?.Details ?? "Status will update after settings are saved.");
    }

    private static bool BeginReaderOptions(string id)
    {
        if (!ImGui.BeginTable(id, 2, ImGuiTableFlags.SizingStretchProp |
                ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.NoPadOuterX)) return false;
        ImGui.TableSetupColumn("Reader", ImGuiTableColumnFlags.WidthStretch);
        // Use the same font-relative width for every category, independent of its current states.
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("Open to refresh").X);
        return true;
    }

    private static bool ReaderAccessOption(IReadOnlyDictionary<string, ReaderStatusRow> statuses,
        string key, string label, ref bool enabled, string summary, string? details = null)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.PushID(key);
        var changed = false;
        var checkboxWidth = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X;
        if (checkboxWidth + ImGui.CalcTextSize(label).X <= ImGui.GetContentRegionAvail().X)
            changed = ImGui.Checkbox(label, ref enabled);
        else
        {
            // ImGui checkbox labels do not wrap. Keep the checkbox on the first line and make the
            // wrapped label clickable as well; the group retains the checkbox's complete hit area.
            ImGui.BeginGroup();
            changed = ImGui.Checkbox("##access", ref enabled);
            ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
            ImGui.AlignTextToFramePadding();
            ImGui.PushTextWrapPos(0);
            ImGui.TextUnformatted(label);
            ImGui.PopTextWrapPos();
            if (ImGui.IsItemClicked()) { enabled = !enabled; changed = true; }
            ImGui.EndGroup();
        }
        // This must run before drawing the status, which replaces ImGui's last item.
        ShowItemHelp(details is null ? summary : summary + "\n\n" + details);
        ImGui.PopID();
        ImGui.TableSetColumnIndex(1);
        var row = enabled && statuses.TryGetValue(key, out var status) ? status : null;
        var state = !enabled ? "Off" : row?.State ?? "On";
        var help = !enabled ? "Access is off. This reader is not used for replies." : row?.Details ??
            "Access changed; status will update after the setting is saved.";
        ImGui.AlignTextToFramePadding();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(state).X));
        ImGui.TextDisabled(state);
        ShowItemHelp(help);
        return changed;
    }
}
