using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;

namespace CompAI;

internal sealed class PersonalizationPanel
{
    private const int NameLimit = 40;
    private const int InstructionsLimit = 2000;
    // ImGui sizes these buffers in UTF-8 bytes, while saved-field limits use C# characters.
    private const int NameInputCapacity = NameLimit * 4 + 1;
    private const int InstructionsInputCapacity = InstructionsLimit * 4 + 1;
    private bool initialized;
    private string? selectedId;
    private string name = "";
    private string instructions = "";
    private string savedName = "";
    private string savedInstructions = "";
    private string preferences = "";
    private string likesDislikes = "";
    private string opinions = "";
    private string background = "";
    private PersonalityDetails savedDetails = PersonalityDetails.Empty;
    private string? message;
    private bool messageIsError;
    private bool openDiscardConfirmation;
    private PersonalityPreset? pendingSelection;
    private string? deleteId;
    private string deleteName = "";
    private readonly PersonalityReplyColorDraft replyColor = new();

    private bool IsBuiltIn => this.selectedId is { } id && PersonalityPresets.IsBuiltIn(id);
    private PersonalityDetails DraftDetails => new(this.preferences, this.likesDislikes, this.opinions, this.background);
    private bool HasPersonalityEdits => this.name != this.savedName || this.instructions != this.savedInstructions ||
        this.DraftDetails != this.savedDetails;
    private bool HasEdits => this.HasPersonalityEdits || this.replyColor.HasEdits;

    internal void Draw(Plugin plugin)
    {
        if (!this.initialized)
        {
            this.Load(plugin, plugin.DefaultPersonality);
            this.initialized = true;
        }

        ImGui.PushID("personalization_settings");
        ImGui.TextUnformatted("Personality for new chats");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##edit_personality", this.selectedId is null ? "New personality (unsaved)" :
            SafeLabel(this.selectedId == PersonalityPresets.None.Id ? "Default" : this.savedName)))
        {
            foreach (var preset in plugin.PersonalityPresets)
            {
                ImGui.PushID(preset.Id);
                var selected = this.selectedId == preset.Id;
                if (ImGui.Selectable(SafeLabel(PersonalityPresets.DisplayName(preset)), selected) && !selected)
                    this.RequestLoad(plugin, preset);
                if (selected) ImGui.SetItemDefaultFocus();
                ImGui.PopID();
            }
            ImGui.EndCombo();
        }
        ImGui.TextWrapped("Existing chats keep their personality. Use a chat's Context menu to change it there.");

        if (ImGui.Button("New")) this.RequestLoad(plugin, null);
        if (this.IsBuiltIn)
        {
            ImGui.TextWrapped(this.selectedId == PersonalityPresets.Armo.Id
                ? "Armo is CompAI's built-in companion. Choose New to create a personality of your own."
                : "Default gives you a neutral assistant. Choose New to create a personality of your own.");
        }
        else
        {
            this.DrawEditor(plugin);
        }
        if (this.message is { } message) ImGui.TextWrapped(message);
        if (plugin.PersonalityStoreError is { } error && error != this.message)
            ImGui.TextWrapped("Personality storage: " + error);

        ImGui.Separator();
        var commandsEnabled = plugin.PresetCommandsEnabled;
        if (ImGui.Checkbox("Enable preset commands", ref commandsEnabled))
        {
            var saved = plugin.SetPresetCommandsEnabled(commandsEnabled);
            this.SetMessage(saved ? (commandsEnabled ? "Preset commands enabled." : "Preset commands disabled.") :
                plugin.PersonalityStoreError ?? "Could not save the preset command setting.", !saved);
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(!plugin.PresetCommandsEnabled);
        if (ImGui.Button("Refresh shortcuts")) plugin.RefreshPresetCommands();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Retry unavailable shortcuts after another plugin releases a command.");
        ImGui.TextWrapped("Shortcuts such as /armo question immediately send using the latest matching chat. With Debug replies enabled, input and replies appear locally without opening the window; nothing is posted to other players.");
        if (this.selectedId is { } savedPresetId)
            ImGui.TextWrapped(plugin.GetPresetCommandStatus(savedPresetId));

        // Open outside the combo so the modal belongs to this settings panel.
        if (this.openDiscardConfirmation)
        {
            ImGui.OpenPopup("Discard unsaved personality edits?");
            this.openDiscardConfirmation = false;
        }
        this.DrawDiscardConfirmation(plugin);
        this.DrawDeleteConfirmation(plugin);
        ImGui.PopID();
    }

    private void DrawEditor(Plugin plugin)
    {
        ImGui.SameLine();
        var validationError = this.Validate();
        ImGui.BeginDisabled(validationError is not null ||
            (this.selectedId == plugin.DefaultPersonality.Id && !this.HasEdits && plugin.GameChatStyleError is null &&
                plugin.PersonalityStoreError is null));
        if (ImGui.Button("Save")) this.Save(plugin);
        ImGui.EndDisabled();
        if (this.selectedId is not null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Clone")) this.Clone();
            // Clone becomes a new, unsaved draft immediately.
            if (this.selectedId is not null)
            {
                ImGui.SameLine();
                if (ImGui.Button("Delete"))
                {
                    this.deleteId = this.selectedId;
                    this.deleteName = this.savedName;
                    ImGui.OpenPopup("Delete personality?");
                }
            }
        }
        if (this.HasEdits || this.selectedId is null) ImGui.TextUnformatted("Unsaved changes");
        ImGui.TextUnformatted($"Name ({this.name.Length}/{NameLimit})");
        ImGui.SetNextItemWidth(-1);
        var edited = ImGui.InputText("##personality_name", ref this.name, NameInputCapacity);
        ImGui.TextUnformatted($"Core instructions ({this.instructions.Length}/{InstructionsLimit})");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Core instructions are included with every question. Keep essential voice and behaviour here. Unused character allowance costs no context.");
        edited |= ImGui.InputTextMultiline("##personality_instructions", ref this.instructions,
            InstructionsInputCapacity, new Vector2(-1, ImGui.GetTextLineHeight() * 7));
        ImGui.TextWrapped("Core instructions are sent with every question. Keep the companion's essential voice here; access permissions still apply.");
        if (ImGui.CollapsingHeader("Optional details · requested when relevant"))
        {
            ImGui.TextWrapped("These describe the companion character, not an automatically learned profile of you. Only section names are sent initially; the AI can request their text when useful. Retrieval adds a tool exchange, so token savings are not guaranteed.");
            edited |= DrawDetail("Preferences", "preferences", ref this.preferences, ImGuiInputTextFlags.None);
            edited |= DrawDetail("Likes and dislikes", "likes_dislikes", ref this.likesDislikes, ImGuiInputTextFlags.None);
            edited |= DrawDetail("Opinions", "opinions", ref this.opinions, ImGuiInputTextFlags.None);
            edited |= DrawDetail("Background", "background", ref this.background, ImGuiInputTextFlags.None);
        }
        if (edited) this.message = null;
        validationError = this.Validate();
        if (validationError is not null) ImGui.TextWrapped(validationError);
        this.DrawReplyColor(plugin);
    }

    internal static void DrawChatPicker(Plugin plugin)
    {
        ImGui.PushID("chat_personality");
        ImGui.TextUnformatted("Personality");
        var snapshot = plugin.ChatPersonality;
        ImGui.BeginDisabled(plugin.CompanionView.ActiveChatBusy);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##personality", SafeLabel(PersonalityPresets.DisplayName(snapshot))))
        {
            foreach (var preset in plugin.PersonalityPresets)
            {
                ImGui.PushID(preset.Id);
                var selected = preset == snapshot;
                // Choosing the same ID explicitly adopts the latest saved version.
                if (ImGui.Selectable(SafeLabel(PersonalityPresets.DisplayName(preset)), selected)) plugin.SetChatPersonality(preset);
                if (selected) ImGui.SetItemDefaultFocus();
                ImGui.PopID();
            }
            ImGui.EndCombo();
        }
        ImGui.EndDisabled();
        snapshot = plugin.ChatPersonality;
        PersonalityPreset? latest = null;
        foreach (var preset in plugin.PersonalityPresets)
            if (preset.Id == snapshot.Id)
            {
                latest = preset;
                break;
            }
        if (latest is not null && latest != snapshot)
        {
            ImGui.BeginDisabled(plugin.CompanionView.ActiveChatBusy);
            if (ImGui.SmallButton("Apply update")) plugin.SetChatPersonality(latest);
            ImGui.EndDisabled();
            snapshot = plugin.ChatPersonality;
        }
        ImGui.TextWrapped(latest == snapshot
            ? "Applies to future replies in this chat. Manage personalities in Settings."
            : latest is not null
                ? "This chat keeps its saved personality. Apply update to use the latest saved version for future replies."
                : "This preset is no longer available. The saved chat personality is retained. Choose another preset above to change future replies.");
        if (plugin.ChatPersonalityError is { } error) ImGui.TextWrapped("Personality: " + error);
        ImGui.PopID();
    }

    private void DrawReplyColor(Plugin plugin)
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Debug chat reply colour");
        var rgb = this.replyColor.DisplayRgb;
        var colour = new Vector3((rgb >> 16) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);
        if (ImGui.ColorEdit3("##reply_colour", ref colour))
        {
            this.replyColor.Select(((uint)Math.Clamp((int)MathF.Round(colour.X * 255), 0, 255) << 16) |
                ((uint)Math.Clamp((int)MathF.Round(colour.Y * 255), 0, 255) << 8) |
                (uint)Math.Clamp((int)MathF.Round(colour.Z * 255), 0, 255));
            this.message = null;
        }
        if (ImGuiComponents.IconButton("##random_reply_colour", FontAwesomeIcon.Dice))
        {
            this.replyColor.Randomize();
            this.message = null;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Random colour: choose a new shade each press. Use Save to keep it for this personality.");
        rgb = this.replyColor.DisplayRgb;
        ImGui.TextColored(new Vector4((rgb >> 16) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1),
            $"[{SafeLabel(string.IsNullOrWhiteSpace(this.name) ? "New personality" : this.name)}]: Reply preview");
        if (this.selectedId is null && this.replyColor.Override is null)
            ImGui.TextWrapped("Automatic colour is assigned when you save. Choose a colour above to keep an exact shade.");
        ImGui.TextWrapped("Use Save above to save all changes. Colour changes apply to future local Debug replies and Thinking notices in all chats using this preset; no AI tokens are used.");
        if (!plugin.Access.GameChatReplies) ImGui.TextWrapped("Debug replies are currently off. Enable them in Access to see these colours in game chat.");
        if (plugin.GameChatStyleError is { } error) ImGui.TextWrapped(error);
    }

    private void Save(Plugin plugin)
    {
        if (this.IsBuiltIn) return;
        var personalitySaved = false;
        if (!this.IsBuiltIn && (this.selectedId is null || this.HasPersonalityEdits || plugin.PersonalityStoreError is not null))
        {
            var saved = plugin.SavePersonality(this.selectedId, this.name, this.instructions, this.DraftDetails);
            if (saved is null)
            {
                this.SetMessage(plugin.PersonalityStoreError ?? "Could not save the personality. Your changes are still here.", true);
                return;
            }
            // Keep the new ID and the colour draft if its separate storage write fails.
            this.selectedId = saved.Id;
            this.savedName = this.name = saved.Name;
            this.savedInstructions = this.instructions = saved.Instructions;
            this.savedDetails = saved.Details;
            this.replyColor.SetPresetId(saved.Id);
            personalitySaved = true;
        }
        if (this.selectedId is not { } id) return;
        if (this.replyColor.HasEdits || plugin.GameChatStyleError is not null)
        {
            if (!plugin.SetPersonalityReplyColor(id, this.replyColor.Override))
            {
                this.SetMessage((personalitySaved ? "Personality saved, but the reply colour was not saved. " : "Reply colour was not saved. ") +
                    "Your colour choice is still here; press Save to retry. " + plugin.GameChatStyleError, true);
                return;
            }
            this.replyColor.MarkSaved();
        }
        if (plugin.DefaultPersonality.Id != id && !plugin.SetDefaultPersonality(id))
        {
            this.SetMessage("Personality saved, but it could not be selected for new chats. Press Save to retry. " +
                plugin.PersonalityStoreError, true);
            return;
        }
        this.SetMessage("Personality and reply colour saved. Selected for new chats; existing chats keep their saved personality.", false);
    }

    private void RequestLoad(Plugin plugin, PersonalityPreset? preset)
    {
        if (this.HasEdits)
        {
            this.pendingSelection = preset;
            this.openDiscardConfirmation = true;
        }
        else this.Select(plugin, preset);
    }

    private void Select(Plugin plugin, PersonalityPreset? preset)
    {
        if (preset is not null && !plugin.SetDefaultPersonality(preset.Id))
        {
            this.SetMessage(plugin.PersonalityStoreError ?? "Could not save the personality selection. Your edits are still here.", true);
            return;
        }
        this.Load(plugin, preset);
    }

    private void Load(Plugin plugin, PersonalityPreset? preset)
    {
        this.replyColor.Load(preset?.Id, preset is not null && plugin.HasPersonalityReplyColor(preset.Id)
            ? plugin.GetPersonalityReplyColor(preset.Id) : null);
        this.selectedId = preset?.Id;
        this.name = preset?.Name ?? "";
        this.instructions = preset?.Instructions ?? "";
        this.savedName = this.name;
        this.savedInstructions = this.instructions;
        var details = preset?.Details ?? PersonalityDetails.Empty;
        this.preferences = details.Preferences;
        this.likesDislikes = details.LikesDislikes;
        this.opinions = details.Opinions;
        this.background = details.Background;
        this.savedDetails = details;
        this.message = null;
    }

    private void Clone()
    {
        if (this.IsBuiltIn || this.selectedId is null) return;
        var baseName = string.IsNullOrWhiteSpace(this.name) ? "New personality" : this.name.Trim();
        if (baseName.Length > NameLimit - 5)
        {
            var length = NameLimit - 5;
            if (char.IsHighSurrogate(baseName[length - 1])) length--;
            baseName = baseName[..length];
        }
        this.replyColor.Clone();
        this.selectedId = null;
        this.name = baseName + " copy";
        this.savedName = "";
        this.savedInstructions = "";
        this.savedDetails = PersonalityDetails.Empty;
        this.message = null;
    }

    private string? Validate()
    {
        if (string.IsNullOrWhiteSpace(this.name)) return "Enter a name before saving.";
        if (this.name.Length > NameLimit) return $"Shorten the name to {NameLimit} characters before saving.";
        if (this.instructions.Length > InstructionsLimit) return $"Shorten the instructions to {InstructionsLimit} characters before saving.";
        if (!PersonalityPresets.ValidDetails(this.DraftDetails))
            return "Each optional section allows up to 2,000 characters without NUL.";
        return null;
    }

    private static bool DrawDetail(string label, string id, ref string value, ImGuiInputTextFlags flags)
    {
        if (!ImGui.CollapsingHeader($"{label} ({value.Length}/{PersonalityPresets.MaxDetailChars})###personality_detail_{id}")) return false;
        return ImGui.InputTextMultiline("##detail_" + id, ref value,
            PersonalityPresets.MaxDetailChars * 4 + 1, new Vector2(-1, ImGui.GetTextLineHeight() * 5), flags);
    }

    private void SetMessage(string text, bool isError)
    {
        this.message = text;
        this.messageIsError = isError;
    }

    private void DrawDiscardConfirmation(Plugin plugin)
    {
        if (!ImGui.BeginPopupModal("Discard unsaved personality edits?", ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextUnformatted("Your unsaved changes will be lost.");
        if (ImGui.Button("Discard changes"))
        {
            this.Select(plugin, this.pendingSelection);
            this.pendingSelection = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Keep editing"))
        {
            this.pendingSelection = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void DrawDeleteConfirmation(Plugin plugin)
    {
        if (!ImGui.BeginPopupModal("Delete personality?", ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 380);
        ImGui.TextWrapped($"Delete \"{this.deleteName}\"? Existing chats keep their saved personality.");
        if (this.deleteId == plugin.DefaultPersonality.Id)
            ImGui.TextWrapped("New chats will use the neutral Default personality.");
        if (ImGui.Button("Delete personality") && this.deleteId is { } id)
        {
            if (plugin.DeletePersonality(id))
            {
                this.deleteId = null;
                this.Load(plugin, plugin.DefaultPersonality);
                this.SetMessage("Personality deleted. Existing chats keep their saved version.", false);
                ImGui.CloseCurrentPopup();
            }
            else this.SetMessage(plugin.PersonalityStoreError ?? "Could not delete the personality.", true);
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            this.deleteId = null;
            ImGui.CloseCurrentPopup();
        }
        if (this.messageIsError && this.message is { } error) ImGui.TextWrapped(error);
        ImGui.PopTextWrapPos();
        ImGui.EndPopup();
    }

    private static string SafeLabel(string label) => label.Replace("##", "# #", StringComparison.Ordinal);
}
