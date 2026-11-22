using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace CompAI;

internal static class ModelPicker
{
    internal static Vector2 GetCompactSize(Plugin plugin, float availableWidth) =>
        GetLayout(plugin, false, availableWidth).Size;

    internal static void DrawCompact(Plugin plugin, float availableWidth = -1f, bool defaults = false)
    {
        if (availableWidth < 0) availableWidth = ImGui.GetContentRegionAvail().X;
        var layout = GetLayout(plugin, defaults, availableWidth);
        var startX = ImGui.GetCursorPosX();
        var options = plugin.ModelOptions;
        var selection = GetSelection(plugin, defaults);
        ImGui.PushID(defaults ? "default_model_selection" : "chat_model_selection");
        ImGui.BeginGroup();
        ImGui.BeginDisabled((defaults ? plugin.CompanionView.Busy : plugin.CompanionView.ActiveChatBusy) || options.Count == 0);
        if (layout.Stacked) ImGui.SetCursorPosX(startX + layout.Size.X - layout.ModelWidth);
        ImGui.SetNextItemWidth(layout.ModelWidth);
        var preview = ModelPreview(plugin, selection);
        if (ImGui.BeginCombo("##companion_model", FitPreview(preview, layout.ModelWidth)))
        {
            foreach (var option in options)
            {
                ImGui.PushID(option.Id);
                var selected = option.Id == selection.Model && option.Provider == selection.Provider;
                if (ImGui.Selectable(SafeLabel(option.Name), selected))
                {
                    var next = CompanionModelSelection.ForModel(option, selection.Effort);
                    SetSelection(plugin, defaults, next);
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(option.Id);
                if (selected) ImGui.SetItemDefaultFocus();
                ImGui.PopID();
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(preview + (defaults
            ? "\nDefault model for new chats. Existing chats keep their own selection."
            : "\nModel for future replies in this chat.") +
            "\nChoices come from the companion runtime; a listed model may still be unavailable to your account.");
        if (layout.Stacked) ImGui.SetCursorPosX(startX + layout.Size.X - layout.EffortWidth);
        else ImGui.SameLine();
        // Reread after a model change so reasoning choices belong to the new model.
        selection = GetSelection(plugin, defaults);
        var selectedModel = CompanionModelSelection.Find(options, selection);
        ImGui.BeginDisabled(selectedModel is null);
        ImGui.SetNextItemWidth(layout.EffortWidth);
        var effortPreview = EffortPreview(plugin, selection);
        if (ImGui.BeginCombo("##companion_effort", FitPreview(effortPreview, layout.EffortWidth)))
        {
            if (selectedModel is not null)
            {
                foreach (var effort in selectedModel.Efforts)
                {
                    var selected = effort == selection.Effort;
                    if (ImGui.Selectable(EffortLabel(effort, selection.Provider), selected)) SetSelection(plugin, defaults, new(selection.Model, effort, selection.Provider));
                    if (selected) ImGui.SetItemDefaultFocus();
                }
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(effortPreview + (selection.Provider == "codex" || selection.Effort is not ("none" or "default")
            ? "\nReasoning effort. Lower levels generally spend less time thinking; allowance savings are not guaranteed."
            : "\nThe provider controls reasoning. This connection does not override it."));
        ImGui.EndDisabled();
        ImGui.EndDisabled();
        ImGui.EndGroup();
        ImGui.PopID();
    }

    internal static void DrawStatus(Plugin plugin, bool defaults = false)
    {
        var selection = GetSelection(plugin, defaults);
        var settingsError = defaults ? plugin.DefaultModelSettingsError : plugin.ModelSettingsError;
        if (settingsError is not null) ImGui.TextWrapped(settingsError);
        if (selection.Provider != plugin.CurrentProvider)
            ImGui.TextWrapped($"Saved provider: {CompanionConnection.ProviderLabel(selection.Provider)}. Choose a model from {CompanionConnection.ProviderLabel(plugin.CurrentProvider)} to change it.");
        if (plugin.ModelDiscoveryError is { } discoveryError) ImGui.TextWrapped("Model discovery: " + discoveryError);
        var options = plugin.ModelOptions;
        if (CompanionConnection.ProviderUnavailableReason(plugin.CurrentProvider) is { } unavailable)
            ImGui.TextWrapped(unavailable);
        else if (options.Count == 0)
            ImGui.TextWrapped("Refresh account status in Settings to load model choices.");
        else if (selection.Provider == plugin.CurrentProvider && !CompanionModelSelection.IsSupported(options, selection))
            ImGui.TextWrapped(defaults
                ? "The default model or reasoning level is unavailable. Select a supported combination for new chats."
                : "This chat's saved model or reasoning level is unavailable. Select a supported combination below before sending.");
        if (settingsError is not null)
        {
            ImGui.PushID(defaults ? "default_model_repair" : "chat_model_repair");
            ImGui.BeginDisabled((defaults ? plugin.CompanionView.Busy : plugin.CompanionView.ActiveChatBusy) || !CompanionModelSelection.IsSupported(options, selection));
            if (ImGui.Button("Save displayed selection")) SetSelection(plugin, defaults, selection);
            ImGui.EndDisabled();
            ImGui.PopID();
        }
    }

    internal static void DrawSettings(Plugin plugin)
    {
        ChatTheme.SectionHeading("Default for new chats");
        ImGui.TextWrapped("Provider: " + CompanionConnection.ProviderLabel(plugin.CurrentProvider));
        DrawCompact(plugin, defaults: true);
        ImGui.TextWrapped("These defaults apply only to chats created after this change and are saved between sessions. Existing chats keep their own model and reasoning choices. Choices come from this runtime's model catalog; listing does not confirm account entitlement.");
        DrawStatus(plugin, defaults: true);
        ImGui.Separator();
    }

    private static CompanionModelSettings GetSelection(Plugin plugin, bool defaults) =>
        defaults ? plugin.DefaultModelSelection : plugin.ModelSelection;

    private static void SetSelection(Plugin plugin, bool defaults, CompanionModelSettings selection)
    {
        if (defaults) plugin.SetDefaultModelSelection(selection.Model, selection.Effort, selection.Provider);
        else plugin.SetModelSelection(selection.Model, selection.Effort, selection.Provider);
    }

    private static string ModelPreview(Plugin plugin, CompanionModelSettings selection)
    {
        var model = CompanionModelSelection.Find(plugin.ModelOptions, selection);
        var preview = model?.Name ?? selection.Model;
        if (plugin.ModelOptions.Count > 0 && model is null) preview += " (unavailable)";
        return SafeLabel(preview);
    }

    private static string EffortPreview(Plugin plugin, CompanionModelSettings selection)
    {
        var preview = EffortLabel(selection.Effort, selection.Provider);
        if (CompanionModelSelection.Find(plugin.ModelOptions, selection) is not null &&
            !CompanionModelSelection.IsSupported(plugin.ModelOptions, selection)) preview += " (unavailable)";
        return preview;
    }

    private static CompactLayout GetLayout(Plugin plugin, bool defaults, float availableWidth)
    {
        var selection = GetSelection(plugin, defaults);
        var style = ImGui.GetStyle();
        var frameHeight = ImGui.GetFrameHeight();
        var chromeWidth = frameHeight + style.FramePadding.X * 2;
        // Use the active font, label and combo arrow rather than a percentage of
        // the window. Unusually long runtime labels remain available on hover.
        var modelWidth = MathF.Ceiling(Math.Min(ImGui.CalcTextSize(ModelPreview(plugin, selection)).X,
            ImGui.GetFontSize() * 16f) + chromeWidth + 1f);
        var effortWidth = MathF.Ceiling(Math.Min(ImGui.CalcTextSize(EffortPreview(plugin, selection)).X,
            ImGui.GetFontSize() * 12f) + chromeWidth + 1f);
        availableWidth = Math.Max(1f, availableWidth);
        var stacked = modelWidth + style.ItemSpacing.X + effortWidth > availableWidth;
        modelWidth = Math.Min(modelWidth, availableWidth);
        effortWidth = Math.Min(effortWidth, availableWidth);
        return new(modelWidth, effortWidth, stacked, new Vector2(
            stacked ? Math.Max(modelWidth, effortWidth) : modelWidth + style.ItemSpacing.X + effortWidth,
            stacked ? frameHeight * 2 + style.ItemSpacing.Y : frameHeight));
    }

    private static string FitPreview(string label, float width)
    {
        var textWidth = Math.Max(0, width - ImGui.GetFrameHeight() - ImGui.GetStyle().FramePadding.X * 2);
        if (ImGui.CalcTextSize(label).X <= textWidth) return label;
        const string suffix = "...";
        if (ImGui.CalcTextSize(suffix).X > textWidth) return "";
        var length = label.Length;
        while (length > 0 && ImGui.CalcTextSize(label[..length] + suffix).X > textWidth) length--;
        return label[..length] + suffix;
    }

    private readonly record struct CompactLayout(float ModelWidth, float EffortWidth, bool Stacked, Vector2 Size);

    private static string SafeLabel(string label) => label.Replace("##", "# #", StringComparison.Ordinal);

    private static string EffortLabel(string effort, string provider) => provider != "codex" && effort == "none" ? "Default" : effort switch
    {
        "default" => "Default", "none" => "None", "minimal" => "Minimal", "low" => "Low", "medium" => "Medium", "high" => "High",
        "xhigh" => "Extra high", "max" => "Max", "ultra" => "Ultra", _ => SafeLabel(effort),
    };
}
