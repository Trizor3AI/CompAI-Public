using System.Collections.Generic;

namespace CompAI;

public sealed partial class Plugin
{
    private readonly CompanionModelSettingsStore modelSettings;

    // The fallback is for the picker display only. Sending requires a saved chat selection.
    internal CompanionModelSettings ModelSelection => this.companion.ChatModelSelection ?? this.DefaultModelSelection;
    internal CompanionModelSettings DefaultModelSelection => this.modelSettings.Settings;
    internal IReadOnlyList<CompanionModelOption> ModelOptions => this.companion.ModelOptions;
    internal string? ModelSettingsError => this.companion.ChatModelError;
    internal string? DefaultModelSettingsError => this.modelSettings.Error;
    internal string? ModelDiscoveryError => this.companion.ModelDiscoveryError;
    internal string CurrentProvider => this.companion.CurrentProvider;
    internal string LoginUserCode => this.companion.LoginUserCode;

    internal bool SetProvider(string provider)
    {
        lock (this.stateGate)
        {
            if (this.contextDisposed || !this.companion.SetProvider(provider)) return false;
            this.preparedQuestion = null;
            return this.modelSettings.SelectProvider(provider);
        }
    }

    internal bool SetModelSelection(string model, string effort, string provider = "codex")
    {
        lock (this.stateGate)
        {
            if (this.contextDisposed || this.companion.View.ActiveChatBusy) return false;
            if (!this.companion.SetChatModelSelection(new(model, effort, provider))) return false;
            this.preparedQuestion = null;
            return true;
        }
    }

    internal bool SetDefaultModelSelection(string model, string effort, string provider = "codex")
    {
        lock (this.stateGate)
        {
            if (this.contextDisposed || this.companion.View.Busy) return false;
            var selection = new CompanionModelSettings(model, effort, provider);
            if (!CompanionModelSelection.IsSupported(this.ModelOptions, selection)) return false;
            return this.modelSettings.Save(selection);
        }
    }

    private string? ModelSelectionBlockReason()
    {
        var selection = this.companion.ChatModelSelection;
        if (selection is null)
            return "This chat has no valid saved model. Choose a model and reasoning level below the input.";
        if (selection.Provider != this.CurrentProvider)
            return $"This chat uses {CompanionConnection.ProviderLabel(selection.Provider)}. Switch provider in Settings or select a model from the current provider.";
        if (this.ModelOptions.Count == 0)
            return this.ModelDiscoveryError ?? "Sign in or refresh account status to load the model choices.";
        foreach (var model in this.ModelOptions)
            if (model.Provider == selection.Provider && model.Id == selection.Model)
                foreach (var effort in model.Efforts)
                    if (effort == selection.Effort) return null;
        return "This chat's model or reasoning level is unavailable. Choose an available option below the input.";
    }
}
