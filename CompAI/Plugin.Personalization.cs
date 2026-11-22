using System.Collections.Generic;
using System.Linq;

namespace CompAI;

public sealed partial class Plugin
{
    private readonly PersonalityPresetStore personalityPresets;

    internal IReadOnlyList<PersonalityPreset> PersonalityPresets => this.personalityPresets.Presets;
    internal PersonalityPreset DefaultPersonality => this.personalityPresets.Default;
    internal string? PersonalityStoreError => this.personalityPresets.Error;
    internal PersonalityPreset ChatPersonality => this.companion.ChatPersonality;
    internal string? ChatPersonalityError => this.companion.ChatPersonalityError;

    internal PersonalityPreset? SavePersonality(string? id, string name, string instructions, PersonalityDetails? details = null)
    {
        lock (this.stateGate)
        {
            if (this.contextDisposed) return null;
            var saved = this.personalityPresets.Save(id, name, instructions, details);
            if (saved is not null) this.RefreshPresetCommands();
            return saved;
        }
    }

    internal bool DeletePersonality(string id)
    {
        lock (this.stateGate)
        {
            if (this.contextDisposed || !this.personalityPresets.Delete(id)) return false;
            this.RefreshPresetCommands();
            return true;
        }
    }

    internal bool SetDefaultPersonality(string id)
    {
        lock (this.stateGate)
            return !this.contextDisposed && this.personalityPresets.SetDefault(id);
    }

    internal bool SetChatPersonality(PersonalityPreset selection)
    {
        lock (this.stateGate)
        {
            if (this.contextDisposed || !this.personalityPresets.Presets.Contains(selection) ||
                !this.companion.SetChatPersonality(selection)) return false;
            this.preparedQuestion = null;
            return true;
        }
    }
}
