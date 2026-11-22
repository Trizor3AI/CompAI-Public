using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CompAI;

internal static class ShopDiagnosticFormatter
{
    internal static ShopDiagnosticResult WithHighlight(ShopDiagnosticResult shop, ShopHighlightDiagnostic highlight)
    {
        try
        {
            // Both inputs are application-owned projections. Never serialize the native
            // controller, binding or frame; the diagnostic DTO contains no addresses.
            if (shop.Text is { } raw && Encoding.UTF8.GetByteCount(raw) > 16384)
                return new(null, "Shop diagnostic exceeds 16 KB; nothing copied.");
            var output = shop.Text is null
                ? new JsonObject { ["diagnostic"] = "compai_shop_highlight_v1", ["shop_capture_available"] = false }
                : JsonNode.Parse(shop.Text) as JsonObject;
            if (output is null) return new(null, "Shop diagnostic is unavailable; nothing copied.");
            output["highlight"] = JsonSerializer.SerializeToNode(new
            {
                highlight.active, highlight.status, highlight.last_renderer_failure,
                highlight.expected_row, highlight.reported_row, highlight.backing_matches, highlight.component_type, highlight.visible_matches,
            });
            var text = output.ToJsonString();
            return Encoding.UTF8.GetByteCount(text) <= 16384
                ? new(text, "Shop and highlight diagnostic copied. Review it before sharing.")
                : new(null, "Shop diagnostic exceeds 16 KB; nothing copied.");
        }
        catch (Exception) { return new(null, "Shop diagnostic is unavailable; nothing copied."); }
    }
}
