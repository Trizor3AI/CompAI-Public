using System;
using System.Collections.Generic;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling.Payloads;

namespace CompAI;

public sealed partial class Plugin
{
    private readonly GameChatStyleStore gameChatStyle;
    internal uint GameChatUserColor => this.gameChatStyle.UserColorRgb;
    internal string? GameChatStyleError => this.gameChatStyle.Error;
    internal bool SetGameChatUserColor(uint rgb) => this.gameChatStyle.SetUserColor(rgb);
    internal uint GetPersonalityReplyColor(string id) => this.gameChatStyle.GetPersonalityColor(id);
    internal bool HasPersonalityReplyColor(string id) => this.gameChatStyle.HasPersonalityColor(id);
    internal bool SetPersonalityReplyColor(string id, uint? rgb) => this.gameChatStyle.SetPersonalityColor(id, rgb);

    private void DrainGameChatReplies()
    {
        if (this.contextDisposed || !ClientState.IsLoggedIn || !this.Access.GameChatReplies)
        {
            this.companion.DiscardGameChatReplies();
            return;
        }
        while (this.companion.TakeGameChatMessage() is { } message)
            this.PrintGameChatText(message.Text, message.Speaker,
                message.PersonalityId is { } id ? this.GetPersonalityReplyColor(id) : null);
    }

    private void PrintGameChatNotice(string message) => this.PrintGameChatText(message, "CompAI");

    private void PrintGameChatInput(string question)
    {
        // Character names are used only for local output, never added to the AI request.
        if (!this.Access.GameChatReplies || !ClientState.IsLoggedIn) return;
        var name = Objects.LocalPlayer?.Name.TextValue;
        this.PrintGameChatText(question, string.IsNullOrWhiteSpace(name) ? "You" : name,
            this.GameChatUserColor, markdown: false);
        // This local acknowledgement is emitted once, only after a send was accepted.
        var personality = this.companion.ChatPersonality;
        var speaker = personality.Id == CompAI.PersonalityPresets.None.Id ? "Companion" : personality.Name;
        this.PrintGameChatText("Thinking...", speaker, this.GetPersonalityReplyColor(personality.Id), markdown: false);
    }

    private void PrintGameChatText(string message, string speaker, uint? color = null, bool markdown = true)
    {
        if (this.contextDisposed || !this.Access.GameChatReplies || !ClientState.IsLoggedIn) return;
        try
        {
            var payloads = new Dictionary<MapLocationLink, MapLinkPayload>();
            string? ResolveMap(MapLocationLink link)
            {
                if (!this.mapLocations.TryCreatePayload(link, out var payload, out var label, out _) || payload is null) return null;
                payloads[link] = payload;
                return label + " (" + link.CoordinateLabel + ")";
            }
            foreach (var line in GameChatReply.FormatParts(message, speaker, markdown, markdown ? ResolveMap : null))
            {
                ChatGui.Print(new XivChatEntry
                {
                    Type = XivChatType.Debug,
                    Message = GameChatReplyRendering.Build(line, payloads, color),
                    Silent = true,
                });
            }
        }
        catch (Exception ex)
        {
            this.chatCommandNotice = $"Local game-chat output failed ({ex.GetType().Name}); the reply is still available in CompAI.";
        }
    }
}
