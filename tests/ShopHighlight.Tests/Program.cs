using System.Numerics;
using System.Runtime.Loader;
using CompAI;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Component.GUI;

var libraryPath = Environment.GetEnvironmentVariable("DALAMUD_LIB_PATH") ??
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "addon", "Hooks", "dev");
AssemblyLoadContext.Default.Resolving += (context, name) =>
{ var file = Path.Combine(libraryPath, name.Name + ".dll"); return File.Exists(file) ? context.LoadFromAssemblyPath(file) : null; };
var passed = 0;
void Check(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
void Test(string name, Action action) { action(); ++passed; Console.WriteLine("PASS " + name); }
var now = DateTimeOffset.UtcNow;
var native = new ShopHighlightNativeCapture(new(100, 101, 102, 103, 104, 105, 106, 0x40001, 107, 108),
    Array.AsReadOnly(new[] { new ShopHighlightOffer(0, 1, 200, false, 1, 25), new ShopHighlightOffer(1, 0, 200, true, 1, 40) }));
MenuSnapshot Snapshot(IEnumerable<MenuEntry>? rows = null, DateTimeOffset? timestamp = null, string? source = null) =>
    new("shops", timestamp ?? now, true, source ?? ShopReader.Source, "saved_menu_observation", rows ?? native.Offers.Select(offer =>
        new MenuEntry((uint)offer.Row + 1, offer.ItemId, "Synthetic duplicate item", offer.Quantity, "offer", "Last observed Shop offers",
            null, offer.Row, offer.IsHq, [new("gil", null, "Gil", offer.GilCost)])));
(ShopHighlightState State, ShopHighlightBinding Binding) Bound()
{
    var state = new ShopHighlightState();
    return (state, state.Bind(Snapshot(), native, 100, 7, now) ?? throw new Exception("Fixture failed"));
}

Test("ordinary gil snapshot requires the entire exact returned offer identity", () =>
{
    Check(ShopHighlightState.MatchesSnapshot(Snapshot(), native, now));
    var rows = Snapshot().Entries.ToArray();
    var first = rows[0];
    foreach (var changed in new[] {
        first with { Id = 3 }, first with { ItemId = 201 }, first with { IsHq = null }, first with { IsHq = true },
        first with { Quantity = 2 }, first with { Slot = 1 }, first with { GroupId = 1 },
        first with { Location = "Last observed ShopExchangeItem offers" },
        first with { Costs = [new("gil", null, "Gil", 26)] },
        first with { Costs = [new("item", 1, "Gil", 25)] },
        first with { Costs = [new("unknown", null, null, null, "Unknown")] },
        first with { Costs = [new("gil", null, "Gil", 25), new("item", 2, "Token", 1)] }
    }) Check(!ShopHighlightState.MatchesSnapshot(Snapshot([changed, rows[1]]), native, now));
    Check(!ShopHighlightState.MatchesSnapshot(Snapshot([rows[0]]), native, now));
    Check(!ShopHighlightState.MatchesSnapshot(Snapshot(source: "Different source"), native, now));
    Check(!ShopHighlightState.MatchesSnapshot(Snapshot(timestamp: now.AddMinutes(-5)), native, now));
    Check(!ShopHighlightState.MatchesSnapshot(Snapshot(timestamp: now.AddSeconds(1)), native, now));
    Check(!ShopHighlightState.MatchesSnapshot(Snapshot(), native with { Identity = native.Identity with { EventId = 0 } }, now));
});

Test("same item ID never substitutes quality cost logical row or backing index", () =>
{
    var (state, binding) = Bound();
    Check(state.Start(binding, 1, 100, 7, 15, now));
    Check(state.SelectedRow == 1 && binding.Offers[1].ItemIndex == 0 && binding.Offers[1].IsHq);
    foreach (var offer in new[] { native.Offers[0] with { IsHq = true }, native.Offers[0] with { GilCost = 40 },
        native.Offers[0] with { Row = 1 }, native.Offers[0] with { ItemIndex = 0 } })
    {
        var pair = Bound();
        Check(!pair.State.Observe(native with { Offers = new[] { offer, native.Offers[1] } }, 100, 7, now));
        Check(!pair.State.IsCurrent(pair.Binding, now));
    }
});

Test("native addon proxy handler buy-list and typed shop-event changes revoke candidates", () =>
{
    foreach (var identity in new[] { native.Identity with { Player = 999 }, native.Identity with { Addon = 999 },
        native.Identity with { AddonId = 999 }, native.Identity with { Proxy = 999 }, native.Identity with { Handler = 999 },
        native.Identity with { BuyList = 999 }, native.Identity with { EventId = 999 }, native.Identity with { EventParam = 999 },
        native.Identity with { EventObject = 999 } })
    {
        var (state, binding) = Bound();
        Check(!state.Observe(native with { Identity = identity }, 100, 7, now));
        Check(!state.IsCurrent(binding, now));
    }
});

Test("close reopen same-address and lifecycle invalidation cannot revive an old handle", () =>
{
    var (state, binding) = Bound();
    Check(!state.Observe(null, 100, 7, now));
    var reopened = state.Bind(Snapshot(), native, 100, 7, now)!;
    Check(reopened.OpenGeneration != binding.OpenGeneration && !state.IsCurrent(binding, now));
    Check(state.IsCurrent(reopened, now));
    state.Revoke("Native Shop lifecycle event");
    var reused = state.Bind(Snapshot(), native, 100, 7, now)!;
    Check(reused.OpenGeneration != reopened.OpenGeneration && !state.IsCurrent(reopened, now));
});

Test("owner session permission and five-minute candidate expiry are independent gates", () =>
{
    var first = Bound(); Check(!first.State.Observe(native, 101, 7, now));
    var second = Bound(); Check(!second.State.Observe(native, 100, 8, now));
    var third = Bound(); third.State.Revoke("Permission revoked"); Check(!third.State.IsCurrent(third.Binding, now));
    var fourth = Bound(); Check(!fourth.State.IsCurrent(fourth.Binding, now.AddMinutes(5)));
    var fifth = Bound(); Check(!fifth.State.Start(fifth.Binding, 0, 101, 7, 15, now));
    Check(!fifth.State.Start(fifth.Binding, 0, 100, 8, 15, now));
    Check(!fifth.State.Start(fifth.Binding, 2, 100, 7, 15, now));
    Check(!fifth.State.Start(fifth.Binding, 0, 100, 7, 60, now));
});

Test("manual dismiss retains the valid offer handle but expiry removes the overlay", () =>
{
    var (state, binding) = Bound();
    Check(state.Start(binding, 0, 100, 7, 15, now));
    state.Clear(); Check(!state.Active && state.IsCurrent(binding, now));
    Check(state.Start(binding, 0, 100, 7, 15, now));
    Check(state.CheckActive(now.AddSeconds(14)));
    Check(!state.CheckActive(now.AddSeconds(15)) && state.IsCurrent(binding, now.AddSeconds(15)));
});

Test("idle candidate watches identity while activation still rejects changed prices", () =>
{
    var (state, binding) = Bound();
    Check(state.ObserveIdentity(native.Identity, 100, 7, now));
    Check(state.IsCurrent(binding, now));
    var changed = native with { Offers = new[] { native.Offers[0] with { GilCost = 26 }, native.Offers[1] } };
    Check(!state.Observe(changed, 100, 7, now));
    Check(!state.Start(binding, 0, 100, 7, 15, now));
    var pair = Bound();
    Check(!pair.State.ObserveIdentity(native.Identity with { EventId = 999 }, 100, 7, now));
    Check(!pair.State.IsCurrent(pair.Binding, now));
});

Test("draw frames expire independently and row rectangles permit full-width rows", () =>
{
    var (state, binding) = Bound();
    Check(state.Start(binding, 0, 100, 7, 15, now));
    var row = new ShopHighlightRectangle(new(10, 20), new(510, 50));
    state.Publish(new(now, row, new Vector2(520, 10), "Found"), now);
    Check(state.VisibleFrame(now.AddMilliseconds(249)) is not null);
    Check(state.VisibleFrame(now.AddMilliseconds(251)) is null);
    Check(state.VisibleFrame(now.AddMilliseconds(-1)) is null);
    state.Publish(new(now, row with { Max = new(float.NaN, 50) }, null, "Bad"), now);
    Check(!state.Active && state.Frame is null);
});

Test("dimming covers only the list minus the selected row without overlaps", () =>
{
    var area = new ShopHighlightRectangle(new(10, 20), new(510, 620));
    float Size(ShopHighlightRectangle rectangle) => (rectangle.Max.X - rectangle.Min.X) * (rectangle.Max.Y - rectangle.Min.Y);
    bool Overlaps(ShopHighlightRectangle a, ShopHighlightRectangle b) => a.Min.X < b.Max.X && a.Max.X > b.Min.X &&
        a.Min.Y < b.Max.Y && a.Max.Y > b.Min.Y;
    foreach (var row in new[] {
        new ShopHighlightRectangle(new(30, 220), new(490, 250)),
        new ShopHighlightRectangle(new(10, 20), new(510, 50)),
        new ShopHighlightRectangle(new(10, 590), new(510, 620)) })
    {
        var pieces = new ShopHighlightRectangle[4];
        var count = ShopHighlightDimming.Regions(area, row, pieces);
        Check(count > 0 && count <= 4);
        var total = Size(row);
        for (var index = 0; index < count; ++index)
        {
            var piece = pieces[index];
            Check(Size(piece) > 0 && piece.Min.X >= area.Min.X && piece.Min.Y >= area.Min.Y &&
                piece.Max.X <= area.Max.X && piece.Max.Y <= area.Max.Y && !Overlaps(piece, row));
            for (var other = 0; other < index; ++other) Check(!Overlaps(piece, pieces[other]));
            total += Size(piece);
        }
        Check(total == Size(area));
    }
});

Test("dimming rejects invalid geometry and cannot survive an empty or expired row frame", () =>
{
    var row = new ShopHighlightRectangle(new(20, 30), new(400, 60));
    var area = new ShopHighlightRectangle(new(10, 20), new(510, 620));
    var pieces = new ShopHighlightRectangle[4];
    foreach (var bad in new[] { area with { Max = new(float.NaN, 620) }, area with { Max = new(510, 3000) },
        area with { Min = new(30, 20) }, area with { Max = area.Min } })
        Check(!ShopHighlightDimming.Valid(bad, row) && ShopHighlightDimming.Regions(bad, row, pieces) == 0);
    Check(ShopHighlightDimming.Regions(area, row, new ShopHighlightRectangle[3]) == 0);
    Check(ShopHighlightDimming.Regions(row, row, pieces) == 0);
    var (state, binding) = Bound();
    Check(state.Start(binding, 0, 100, 7, 15, now));
    state.Publish(new(now, row, null, "Found", DimArea: area), now);
    Check(state.VisibleFrame(now.AddMilliseconds(249))?.DimArea == area);
    Check(state.VisibleFrame(now.AddMilliseconds(251)) is null);
    state.Publish(new(now, null, null, "Blocked", ShopHighlightBlockReason.Offscreen), now);
    Check(state.VisibleFrame(now)?.DimArea is null);
    state.Publish(new(now, null, null, "Bad", DimArea: area), now);
    Check(!state.Active && state.Frame is null);
});

Test("offscreen and scrolling frames dim the whole list without retaining a stale row cutout", () =>
{
    var area = new ShopHighlightRectangle(new(10, 20), new(510, 620));
    var row = new ShopHighlightRectangle(new(10, 80), new(510, 110));
    var pieces = new ShopHighlightRectangle[4];
    Check(ShopHighlightDimming.Regions(area, null, pieces) == 1 && pieces[0] == area);
    Check(ShopHighlightDimming.Regions(area with { Max = new(float.PositiveInfinity, 620) }, null, pieces) == 0);
    foreach (var reason in new[] { ShopHighlightBlockReason.Offscreen, ShopHighlightBlockReason.ListUnsettled })
    {
        var (state, binding) = Bound();
        Check(state.Start(binding, 0, 100, 7, 15, now));
        state.Publish(new(now, row, null, "Found", DimArea: area), now);
        state.Publish(new(now, null, new Vector2(510, 10), "Scroll", reason, DimArea: area), now);
        var frame = state.VisibleFrame(now.AddMilliseconds(249));
        Check(state.Active && frame?.Row is null && frame?.DimArea == area && frame.WindowTopRight is not null);
        Check(state.VisibleFrame(now.AddMilliseconds(251)) is null && state.ExpiresAt == now.AddSeconds(15));
        state.Publish(new(now, row, null, "Found", DimArea: area), now);
        Check(state.VisibleFrame(now)?.Row == row && ShopHighlightDimming.Regions(area, row, pieces) == 2);
        state.Publish(new(now, null, null, "Changed", ShopHighlightBlockReason.ShopChanged), now);
        Check(state.VisibleFrame(now)?.DimArea is null);
        state.Clear(); Check(state.VisibleFrame(now) is null);
    }
    foreach (var reason in new[] { ShopHighlightBlockReason.None, ShopHighlightBlockReason.Renderer,
        ShopHighlightBlockReason.LayoutChanged, ShopHighlightBlockReason.ShopChanged })
    {
        var (state, binding) = Bound();
        Check(state.Start(binding, 0, 100, 7, 15, now));
        state.Publish(new(now, null, null, "Invalid mask", reason, DimArea: area), now);
        Check(!state.Active && state.Frame is null);
    }
});

Test("blocked renderer reason and bounded managed diagnostic survive timeout until next attempt or revoke", () =>
{
    var (state, binding) = Bound();
    Check(state.Start(binding, 1, 100, 7, 15, now));
    var result = new ShopRendererBindingResult(ShopRendererBindingFailure.RendererChanged, 1, 2, 1,
        (int)ComponentType.ListItemRenderer, 0);
    state.Publish(new(now, null, null, "Ignored arbitrary frame text", ShopHighlightBlockReason.Renderer, result), now);
    var fixedReason = ShopHighlightMessages.Renderer(ShopRendererBindingFailure.RendererChanged);
    Check(state.Status == fixedReason && !state.Status.Contains("arbitrary"));
    Check(!state.CheckActive(now.AddSeconds(15)) && state.Status.StartsWith(fixedReason, StringComparison.Ordinal));
    Check(state.Status.EndsWith("Highlight expired; click Find to retry.", StringComparison.Ordinal));
    var diagnostic = state.Diagnostic;
    Check(!diagnostic.active && diagnostic.last_renderer_failure == "RendererChanged" && diagnostic.expected_row == 1 &&
        diagnostic.reported_row == 1 && diagnostic.backing_matches == 2 &&
        diagnostic.component_type == (int)ComponentType.ListItemRenderer && diagnostic.visible_matches == 0);
    state.Clear(); Check(state.Diagnostic.last_renderer_failure == "RendererChanged" && state.Diagnostic.visible_matches == 0);
    Check(state.Start(binding, 0, 100, 7, 15, now.AddSeconds(16)));
    Check(state.Diagnostic.last_renderer_failure is null && state.Diagnostic.expected_row == 0 && state.Diagnostic.visible_matches is null);
    state.Publish(new(now.AddSeconds(16), null, null, "ignored", ShopHighlightBlockReason.Renderer, result), now.AddSeconds(16));
    state.Revoke(); Check(state.Diagnostic.last_renderer_failure is null && state.Diagnostic.expected_row is null &&
        state.Diagnostic.visible_matches is null);
});

Test("manual diagnostic projects only fixed status and numeric renderer evidence", () =>
{
    var (state, binding) = Bound();
    Check(state.Start(binding, 0, 100, 7, 15, now));
    var result = new ShopRendererBindingResult(ShopRendererBindingFailure.WrongComponentType, 0, 1, 0, (int)ComponentType.Button);
    state.Publish(new(now, null, null, "ignored", ShopHighlightBlockReason.Renderer, result), now);
    using var document = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(state.Diagnostic));
    Check(document.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet().SetEquals(new[] {
        "active", "status", "last_renderer_failure", "expected_row", "reported_row", "backing_matches", "component_type", "visible_matches" }));
    Check(document.RootElement.GetProperty("visible_matches").ValueKind == System.Text.Json.JsonValueKind.Null);
    foreach (var failure in Enum.GetValues<ShopRendererBindingFailure>())
        Check(ShopHighlightMessages.Renderer(failure).Length < 200);
});

Test("virtual list bounds reject unknown backing and keep offscreen rows unavailable", () =>
{
    var header = new ShopHighlightReader.ListHeader(60, 20, 10, 60, 500, false, false);
    Check(ShopHighlightReader.ValidHeader(header, 60));
    Check(ShopHighlightReader.VisibleRow(header, 20) && ShopHighlightReader.VisibleRow(header, 29));
    Check(!ShopHighlightReader.VisibleRow(header, 19) && !ShopHighlightReader.VisibleRow(header, 30));
    foreach (var changed in new[] { header with { Length = 59 }, header with { First = -1 }, header with { First = 60 },
        header with { Visible = 0 }, header with { Visible = 61 }, header with { Allocated = 0 },
        header with { Allocated = 129 }, header with { Array = 0 } }) Check(!ShopHighlightReader.ValidHeader(changed, 60));
});

Test("backing aliases are allowed only with one exact visible getter binding and typed owned renderer", NativeRenderer);
static unsafe void NativeRenderer()
{
    void Require(bool value) { if (!value) throw new Exception("Renderer assertion failed"); }
    var listNode = new AtkComponentNode(); listNode.NodeFlags = NodeFlags.Visible;
    var rowNode = new AtkComponentNode(); rowNode.NodeFlags = NodeFlags.Visible;
    var renderer = new AtkComponentListItemRenderer { ListItemIndex = 1, OwnerNode = &rowNode };
    rowNode.Component = (AtkComponentBase*)&renderer;
    rowNode.ParentNode = (AtkResNode*)&listNode;
    var backing = stackalloc AtkComponentList.ListItem[2];
    backing[0] = default; backing[1] = default;
    backing[1].AtkComponentListItemRenderer = &renderer;
    var list = new AtkComponentList { OwnerNode = &listNode, ListLength = 2, FirstVisibleItemIndex = 0, NumVisibleItems = 2,
        AllocatedItemRendererListLength = 2, ItemRendererList = backing };
    listNode.Component = (AtkComponentBase*)&list;
    var listPointer = &list; var rendererPointer = &renderer;
    nint Getter(int index) => index == 1 ? (nint)rendererPointer : 0;
    ShopRendererBindingResult Result(Func<int, nint>? getter = null) => ShopHighlightReader.CheckRendererBinding(
        listPointer, rendererPointer, 1, _ => ComponentType.ListItemRenderer, getter ?? Getter);
    void Failure(ShopRendererBindingFailure expected, Func<int, nint>? getter = null)
    {
        Require(Result(getter).Failure == expected && !ShopHighlightReader.RendererBound(
            listPointer, rendererPointer, 1, _ => ComponentType.ListItemRenderer, getter ?? Getter));
    }
    Require(Result().Success && Result().ComponentType == (int)ComponentType.ListItemRenderer && Result().VisibleMatches == 1);
    Require(ShopHighlightReader.RendererBound(listPointer, rendererPointer, 1, _ => ComponentType.ListItemRenderer, Getter));
    Require(ShopHighlightReader.CheckRendererBinding(null, rendererPointer, 1).Failure == ShopRendererBindingFailure.ListMissing);
    list.OwnerNode = null; Failure(ShopRendererBindingFailure.ListOwnerMissing); list.OwnerNode = &listNode;
    listNode.Component = null; Failure(ShopRendererBindingFailure.ListOwnerMismatch); listNode.Component = (AtkComponentBase*)&list;
    Require(ShopHighlightReader.CheckRendererBinding(listPointer, rendererPointer, 2).Failure == ShopRendererBindingFailure.RowOutOfRange);
    Require(ShopHighlightReader.CheckRendererBinding(listPointer, null, 1).Failure == ShopRendererBindingFailure.GetterMissing);
    backing[0].AtkComponentListItemRenderer = &renderer;
    Require(Result().Success && Result().BackingMatches == 2 && Result().RendererIndex == 1 && Result().VisibleMatches == 1);
    Require(ShopHighlightReader.RendererBound(listPointer, rendererPointer, 1, _ => ComponentType.ListItemRenderer, Getter));
    nint DuplicateGetter(int _) => (nint)rendererPointer;
    Failure(ShopRendererBindingFailure.DuplicateVisibleRenderer, DuplicateGetter);
    Require(Result(DuplicateGetter).BackingMatches == 2 && Result(DuplicateGetter).VisibleMatches == 2);
    nint WrongRowGetter(int index) => index == 0 ? (nint)rendererPointer : 0;
    Failure(ShopRendererBindingFailure.RendererChanged, WrongRowGetter);
    Require(Result(WrongRowGetter).VisibleMatches == 1);
    Failure(ShopRendererBindingFailure.RendererChanged, _ => 0);
    Require(Result(_ => 0).VisibleMatches == 0);
    renderer.ListItemIndex = 0; Failure(ShopRendererBindingFailure.RowIndexMismatch);
    Require(Result().RendererIndex == 0 && Result().ExpectedRow == 1 && Result().BackingMatches == 2);
    renderer.ListItemIndex = 1;
    var replacement = new AtkComponentListItemRenderer();
    var currentGetterResult = (nint)rendererPointer;
    nint ChangingGetter(int index) => index == 1 ? currentGetterResult : 0;
    Require(Result(ChangingGetter).Success);
    currentGetterResult = (nint)(&replacement);
    Failure(ShopRendererBindingFailure.RendererChanged, ChangingGetter);
    Require(Result(ChangingGetter).VisibleMatches == 0);
    backing[0] = default; backing[1] = default; Failure(ShopRendererBindingFailure.MissingBacking);
    Require(Result().BackingMatches == 0 && Result().RendererIndex is null);
    backing[1].AtkComponentListItemRenderer = &renderer;
    renderer.ListItemIndex = 0; Failure(ShopRendererBindingFailure.RowIndexMismatch);
    Require(Result().RendererIndex == 0 && Result().ExpectedRow == 1); renderer.ListItemIndex = 1;
    renderer.OwnerNode = null; Failure(ShopRendererBindingFailure.RendererOwnerMissing); renderer.OwnerNode = &rowNode;
    rowNode.Component = null; Failure(ShopRendererBindingFailure.RendererOwnerMismatch); rowNode.Component = (AtkComponentBase*)&renderer;
    rowNode.ParentNode = null; Failure(ShopRendererBindingFailure.OutsideListAncestry); rowNode.ParentNode = (AtkResNode*)&listNode;
    rowNode.NodeFlags = 0; Failure(ShopRendererBindingFailure.HiddenRenderer); rowNode.NodeFlags = NodeFlags.Visible;
    listNode.NodeFlags = 0; Failure(ShopRendererBindingFailure.HiddenRenderer); listNode.NodeFlags = NodeFlags.Visible;
    var wrongType = ShopHighlightReader.CheckRendererBinding(&list, &renderer, 1, _ => ComponentType.Button, Getter);
    Require(wrongType.Failure == ShopRendererBindingFailure.WrongComponentType && wrongType.ComponentType == (int)ComponentType.Button);
    list.AllocatedItemRendererListLength = 129; Failure(ShopRendererBindingFailure.InvalidListHeader);
}
Test("visible getter checks follow scrolled logical rows clamp the tail and reject unsettled or offscreen rows", NativeVisibleRendererRange);
static unsafe void NativeVisibleRendererRange()
{
    void Require(bool value) { if (!value) throw new Exception("Visible renderer range assertion failed"); }
    var listNode = new AtkComponentNode(); listNode.NodeFlags = NodeFlags.Visible;
    var rowNode = new AtkComponentNode(); rowNode.NodeFlags = NodeFlags.Visible;
    var renderer = new AtkComponentListItemRenderer { ListItemIndex = 25, OwnerNode = &rowNode };
    rowNode.Component = (AtkComponentBase*)&renderer;
    rowNode.ParentNode = (AtkResNode*)&listNode;
    var backing = stackalloc AtkComponentList.ListItem[3];
    for (var index = 0; index < 3; ++index)
    { backing[index] = default; backing[index].AtkComponentListItemRenderer = &renderer; }
    var list = new AtkComponentList { OwnerNode = &listNode, ListLength = 60, FirstVisibleItemIndex = 20, NumVisibleItems = 10,
        AllocatedItemRendererListLength = 3, ItemRendererList = backing };
    listNode.Component = (AtkComponentBase*)&list;
    var listPointer = &list; var rendererPointer = &renderer;
    var requested = new List<int>();
    var selectedRow = 25;
    nint Getter(int index)
    {
        Require(index >= listPointer->FirstVisibleItemIndex && index < listPointer->ListLength &&
            index < listPointer->FirstVisibleItemIndex + listPointer->NumVisibleItems);
        requested.Add(index);
        return index == selectedRow ? (nint)rendererPointer : 0;
    }
    ShopRendererBindingResult Result(int row) => ShopHighlightReader.CheckRendererBinding(
        listPointer, rendererPointer, row, _ => ComponentType.ListItemRenderer, Getter);
    var middle = Result(selectedRow);
    Require(middle.Success && middle.BackingMatches == 3 && middle.VisibleMatches == 1);
    Require(requested.SequenceEqual(Enumerable.Range(20, 10).Append(selectedRow)));
    foreach (var offscreen in new[] { 19, 30 })
    {
        requested.Clear();
        Require(Result(offscreen).Failure == ShopRendererBindingFailure.RowNotVisible && requested.Count == 0);
    }
    list.IsUpdatePending = true;
    requested.Clear();
    Require(Result(selectedRow).Failure == ShopRendererBindingFailure.InvalidListHeader && requested.Count == 0);
    list.IsUpdatePending = false;
    list.IsScrollRefreshPending = true;
    Require(Result(selectedRow).Failure == ShopRendererBindingFailure.InvalidListHeader && requested.Count == 0);
    list.IsScrollRefreshPending = false;
    list.FirstVisibleItemIndex = 57;
    selectedRow = 58; renderer.ListItemIndex = selectedRow;
    requested.Clear();
    Require(Result(selectedRow).Success && requested.SequenceEqual(new[] { 57, 58, 59, 58 }));

    ShopRendererBindingResult Recheck(Func<int, nint> getter) => ShopHighlightReader.CheckRendererBinding(
        listPointer, rendererPointer, selectedRow, _ => ComponentType.ListItemRenderer, getter);
    var selectedCalls = 0;
    var changedGetter = Recheck(index => index == selectedRow && ++selectedCalls == 1 ? (nint)rendererPointer : 0);
    Require(changedGetter.Failure == ShopRendererBindingFailure.RendererChanged && changedGetter.VisibleMatches == 1);
    for (var mutation = 0; mutation < 3; ++mutation)
    {
        var change = mutation;
        selectedCalls = 0;
        var changedDuringRecheck = Recheck(index =>
        {
            if (index != selectedRow) return 0;
            if (++selectedCalls == 2)
            {
                if (change == 0) listPointer->NumVisibleItems = 9;
                else if (change == 1) rendererPointer->ListItemIndex = 59;
                else rendererPointer->OwnerNode = null;
            }
            return (nint)rendererPointer;
        });
        Require(changedDuringRecheck.Failure == ShopRendererBindingFailure.RendererChanged && changedDuringRecheck.VisibleMatches == 1);
        list.NumVisibleItems = 10; renderer.ListItemIndex = selectedRow; renderer.OwnerNode = &rowNode;
    }
}
Test("native gil copy preserves exact visible backing index including duplicate item IDs", NativeRows);
static unsafe void NativeRows()
{
    var handler = new ShopEventHandler { ItemsCount = 2, VisibleItemsCount = 2, CurrentMode = 1 };
    handler.Items[0].ItemId = 200; handler.Items[0].PriceBuy = 40; handler.Items[0].IsHQ = true;
    handler.Items[1].ItemId = 200; handler.Items[1].PriceBuy = 25;
    handler.VisibleItems[0] = 1; handler.VisibleItems[1] = 0;
    var rows = ShopHighlightReader.CopyOffers(&handler);
    if (rows[0] != new ShopHighlightOffer(0, 1, 200, false, 1, 25) || rows[1] != new ShopHighlightOffer(1, 0, 200, true, 1, 40))
        throw new Exception("Visible-to-backing row mapping was lost");
}
ShopFindChecks.Run();
DiagnosticProjectionChecks.Run();
Console.WriteLine($"Passed {passed} shop-highlight groups plus shop finder contract checks.");
