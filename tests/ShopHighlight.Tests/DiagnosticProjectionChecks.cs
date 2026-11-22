using System.Text;
using System.Text.Json;
using CompAI;

internal static class DiagnosticProjectionChecks
{
    internal static void Run()
    {
        var evidence = new ShopHighlightDiagnostic(false, "Fixed failed renderer check", "DuplicateVisibleRenderer", 2, 2, 3, 14, 2);
        var combined = ShopDiagnosticFormatter.WithHighlight(new("{\"diagnostic\":\"compai_shop_v1\",\"visible_offers\":[{\"item_id\":100}]}", "copied"), evidence);
        using (var doc = Parse(combined))
        {
            var root = doc.RootElement;
            Check(root.GetProperty("visible_offers")[0].GetProperty("item_id").GetUInt32() == 100);
            var row = root.GetProperty("highlight");
            Check(row.EnumerateObject().Select(property => property.Name).SequenceEqual(
                ["active", "status", "last_renderer_failure", "expected_row", "reported_row", "backing_matches", "component_type", "visible_matches"]));
            Check(row.GetProperty("last_renderer_failure").GetString() == "DuplicateVisibleRenderer" && row.GetProperty("expected_row").GetInt32() == 2);
            Check(row.GetProperty("backing_matches").GetInt32() == 3 && row.GetProperty("visible_matches").GetInt32() == 2);
        }
        using (var doc = Parse(ShopDiagnosticFormatter.WithHighlight(new(null, "Closed shop"), evidence)))
        {
            Check(!doc.RootElement.GetProperty("shop_capture_available").GetBoolean());
            Check(doc.RootElement.GetProperty("highlight").GetProperty("reported_row").GetInt32() == 2);
            Check(doc.RootElement.GetProperty("highlight").GetProperty("visible_matches").GetInt32() == 2);
        }
        using (var doc = Parse(ShopDiagnosticFormatter.WithHighlight(new("{}", "valid"), evidence with { visible_matches = null })))
            Check(doc.RootElement.GetProperty("highlight").GetProperty("visible_matches").ValueKind == JsonValueKind.Null);
        Check(ShopDiagnosticFormatter.WithHighlight(new("[]", "invalid"), evidence).Text is null);
        var dense = JsonSerializer.Serialize(new { text = new string('界', 3000) });
        Check(ShopDiagnosticFormatter.WithHighlight(new(dense, "large"), evidence).Text is null);
        Check(ShopDiagnosticFormatter.WithHighlight(new("{}", "valid"), evidence with { status = new string('x', 16384) }).Text is null);
        Console.WriteLine("PASS manual highlight diagnostic preserves offers, projects only safe fields, survives closed shops and respects the shared16KB cap");
    }
    private static JsonDocument Parse(ShopDiagnosticResult result)
    {
        Check(result.Text is not null && Encoding.UTF8.GetByteCount(result.Text) <= 16384);
        return JsonDocument.Parse(result.Text!);
    }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Shop diagnostic projection failed."); }
}
