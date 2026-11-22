namespace CompAI;

public sealed partial class Plugin
{
    private readonly CompanionResponseSettingsStore responseSettings;

    internal int ResponseLimitMinutes => this.responseSettings.Settings.Minutes;
    internal string? ResponseLimitError => this.responseSettings.Error;

    internal bool SetResponseLimitMinutes(int minutes)
    {
        lock (this.stateGate)
        {
            if (this.contextDisposed) return false;
            return this.responseSettings.Save(new(minutes));
        }
    }
}
