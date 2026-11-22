namespace CompAI;

public sealed partial class Plugin
{
    private readonly MapLocationsReader mapLocations;

    // Called from a chat link click, never from a model tool or background reader.
    internal async void OpenChatMap(string url)
    {
        if (this.contextDisposed) return;
        if (!ClientState.IsLoggedIn)
        {
            this.chatCommandNotice = "Log into a character to open the game map.";
            return;
        }
        if (!MapLocationLink.TryParse(url, out var link))
        {
            this.chatCommandNotice = "This map link is invalid.";
            return;
        }
        var epoch = this.contextLoadEpoch;
        try
        {
            await Framework.RunOnFrameworkThread(() =>
            {
                // ImGui can draw on a different thread. The click opens native UI only here.
                if (this.contextDisposed || epoch != this.contextLoadEpoch || !ClientState.IsLoggedIn) return;
                if (!this.mapLocations.TryOpen(GameGui, link, out var reason)) this.chatCommandNotice = reason;
            });
        }
        catch (System.Exception)
        {
            if (!this.contextDisposed) this.chatCommandNotice = "The game could not open this map right now.";
        }
    }
}
