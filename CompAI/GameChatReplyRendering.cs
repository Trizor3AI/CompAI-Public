using System;
using System.Collections.Generic;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;

namespace CompAI;

/// <summary>Encodes already sanitized, bounded reply parts; never resolves a map or invokes game UI.</summary>
internal static class GameChatReplyRendering
{
    internal static SeString Build(GameChatReplyLine line, IReadOnlyDictionary<MapLocationLink, MapLinkPayload> payloads, uint? color)
    {
        SeString ColoredText(string text)
        {
            var builder = new Lumina.Text.SeStringBuilder();
            if (color is { } rgb) builder.PushColorRgba((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);
            builder.Append(text);
            if (color is not null) builder.PopColor();
            return SeString.Parse(builder.ToArray());
        }
        var builder = new SeStringBuilder();
        foreach (var part in line.Parts)
        {
            if (part.Map is { } map && payloads.TryGetValue(map, out var payload))
            {
                // Map payload and terminator are atomic application-owned bytes; model text remains literal.
                builder.AddUiForeground(549).Add(payload).AddText(part.Text)
                    .Add(RawPayload.LinkTerminator).AddUiForegroundOff();
            }
            else builder.Append(ColoredText(part.Text));
        }
        var output = builder.Build();
        // Preserve the byte bound even if a future API grows its payload encoding.
        if (output.Encode().Length <= GameChatReply.MaxLineBytes) return output;
        var plain = ColoredText(line.Text);
        if (plain.Encode().Length > GameChatReply.MaxLineBytes)
            throw new InvalidOperationException("Game-chat output exceeds its encoded byte bound.");
        return plain;
    }
}
