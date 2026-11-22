using System.Text.Json;
using CompAI;

void Check(bool value, string message) { if (!value) throw new Exception(message); }
var snapshot = new MenuSnapshot("shops", DateTimeOffset.UtcNow, true, ShopReader.Source, "saved_menu_observation",
    Enumerable.Range(0, 2).Select(index => new MenuEntry((uint)index + 1, (uint)index + 100, "Synthetic offer " + index,
        1, "offer", "Last observed Shop offers", null, index, false, [new("gil", null, "gil", 20, null)])));
JsonElement Page(int offset = 0) => JsonSerializer.SerializeToElement(new { category = "shops", offset, limit = 1 });
string Expected(int offset = 0) => JsonSerializer.Serialize(new MenuLookup(new MenuSnapshot("shops", snapshot.CapturedAtUtc,
    true, snapshot.Source, "saved_menu_observation; last_shop_only; expires_after_five_minutes", snapshot.Entries)).Query(Page(offset)));

foreach (var failure in new[] { "missing", "throws", "timeout" })
{
    var plugin = new Plugin(snapshot);
    plugin.Native.Missing = failure == "missing"; plugin.Native.Throws = failure == "throws";
    Plugin.Framework.Timeout = failure == "timeout";
    Check(plugin.Query()(Page()) == Expected(), failure + " changed the historical tool reply");
    Check(plugin.companion.Key is null, failure + " created an action");
}
Console.WriteLine("PASS optional missing, failed and timed-out native binding preserves exact historical JSON");

{
    var plugin = new Plugin(snapshot); var query = plugin.Query();
    var callbackThread = Environment.CurrentManagedThreadId;
    Check(query(Page()) == Expected(), "first page changed");
    var key = plugin.companion.Key!.Value;
    var first = plugin.ShopFindItems(key)!;
    Check(first.Items.Count == 1 && first.Items[0].Id == 1, "unreturned offers became actions");
    Check(plugin.companion.AttachThread == callbackThread && Plugin.Framework.LastThread != callbackThread,
        "action key was attached on the framework callback instead of originating callback");
    Check(query(Page(1)) == Expected(1), "second page changed");
    var second = plugin.ShopFindItems(key)!;
    Check(second.Items.Count == 2 && ReferenceEquals(first.Binding, second.Binding) && plugin.Native.Captures == 1 &&
        plugin.Native.Validates == 1, "pagination changed native binding or discarded returned rows");
    plugin.Native.Current = false;
    Check(query(Page()) == Expected() && plugin.ShopFindItems(key) is null && plugin.Native.Captures == 1,
        "stale binding rebound or changed historical data");
}
Console.WriteLine("PASS returned rows only, callback-thread attachment and immutable first binding across pages");

foreach (var change in new[] { "lifecycle", "area", "access", "session", "cancel" })
{
    var plugin = new Plugin(snapshot); var query = plugin.Query(); _ = query(Page());
    var key = plugin.companion.Key!.Value;
    switch (change)
    {
        case "lifecycle": plugin.Lifecycle(); break;
        case "area": plugin.ChangeArea(); break;
        case "access": plugin.RevokeAccess(); break;
        case "session": plugin.ChangeSession(); break;
        case "cancel": plugin.companion.Continues = false; break;
    }
    if (change is "lifecycle" or "area") Check(query(Page(1)) == Expected(1), "local action revocation broke historical reply");
    else
    {
        var denied = false; try { _ = query(Page(1)); } catch (OperationCanceledException) { denied = true; }
        Check(denied, change + " did not deny the continued query");
    }
    if (change != "cancel") Check(plugin.ShopFindItems(key) is null, change + " retained local action");
    Check(plugin.Native.Captures == 1, change + " rebound native identity");
}
Console.WriteLine("PASS lifecycle, area, access, session and cancellation gates");

{
    var plugin = new Plugin(snapshot); _ = plugin.Query()(Page());
    var key = plugin.companion.Key!.Value;
    Check(plugin.FindShopItem(key, 1) is null, "current item did not queue");
    Check(plugin.ShopFindFeedback(key)!.Contains("Checking") && plugin.ShopFindFeedback(Guid.NewGuid()) is null,
        "manual feedback lost reply ownership");
    plugin.TickHighlight();
    plugin.Native.StopOnUpdate = true;
    plugin.TickHighlight();
    Check(!plugin.ShopHighlightActive && plugin.ShopFindNotice!.Contains("renderer unavailable"),
        "automatic failure silently lost its reason");
    plugin.ExpireNotice();
    Check(plugin.ShopFindNotice is null && plugin.ShopFindFeedback(key)!.Contains("renderer unavailable"),
        "compact notice must expire while explicit per-reply feedback remains reviewable");
    plugin.ClearShopHighlight();
    Check(plugin.ShopFindNotice is null && plugin.ShopFindFeedback(key) is null, "manual dismissal retained feedback");
}
{
    var plugin = new Plugin(snapshot); _ = plugin.Query()(Page());
    var key = plugin.companion.Key!.Value;
    Check(plugin.FindShopItem(key, 1) is null, "item did not queue before lifecycle change");
    plugin.Lifecycle();
    Check(plugin.ShopFindNotice!.Contains("closed or reopened") && plugin.ShopFindItems(key) is null,
        "lifecycle cancellation lost feedback or retained the action");
}
Console.WriteLine("PASS asynchronous cancellation preserves per-reply feedback with a bounded composer notice and manual dismissal");
