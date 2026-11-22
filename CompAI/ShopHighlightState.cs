using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;

namespace CompAI;

// Local-only identities. None of these addresses or action handles enter a reader payload or saved chat.
internal readonly record struct ShopHighlightNativeIdentity(ulong OwnerId, nint Player, nint Addon,
    uint AddonId, nint Proxy, nint Handler, nint BuyList, uint EventId, uint EventParam, nint EventObject);
internal readonly record struct ShopHighlightOffer(int Row, int ItemIndex, uint ItemId, bool IsHq,
    uint Quantity, uint GilCost);
internal sealed record ShopHighlightNativeCapture(ShopHighlightNativeIdentity Identity,
    IReadOnlyList<ShopHighlightOffer> Offers);
internal sealed record ShopHighlightBinding(ulong OwnerId, int SessionGeneration, long OpenGeneration,
    DateTimeOffset ExpiresAt, ShopHighlightNativeIdentity Identity, IReadOnlyList<ShopHighlightOffer> Offers);
internal readonly record struct ShopHighlightRectangle(Vector2 Min, Vector2 Max)
{
    internal bool Valid => float.IsFinite(Min.X) && float.IsFinite(Min.Y) && float.IsFinite(Max.X) &&
        float.IsFinite(Max.Y) && Max.X - Min.X is >= 8 and <= 2048 && Max.Y - Min.Y is >= 8 and <= 256;
}
internal sealed record ShopHighlightFrame(DateTimeOffset CapturedAt, ShopHighlightRectangle? Row,
    Vector2? WindowTopRight, string Status, ShopHighlightBlockReason BlockedReason = ShopHighlightBlockReason.None,
    ShopRendererBindingResult? RendererResult = null, ShopHighlightRectangle? DimArea = null);

internal static class ShopHighlightDimming
{
    internal static bool ValidArea(ShopHighlightRectangle area) =>
        float.IsFinite(area.Min.X) && float.IsFinite(area.Min.Y) && float.IsFinite(area.Max.X) && float.IsFinite(area.Max.Y) &&
        area.Max.X - area.Min.X is >= 8 and <= 2048 && area.Max.Y - area.Min.Y is >= 8 and <= 2048;

    internal static bool Valid(ShopHighlightRectangle area, ShopHighlightRectangle row) => row.Valid && ValidArea(area) &&
        row.Min.X >= area.Min.X && row.Min.Y >= area.Min.Y && row.Max.X <= area.Max.X && row.Max.Y <= area.Max.Y;

    // Without a visible row, fill the list. Otherwise leave that row untouched.
    internal static int Regions(ShopHighlightRectangle area, ShopHighlightRectangle? selected, Span<ShopHighlightRectangle> output)
    {
        if (output.Length < 4 || !ValidArea(area)) return 0;
        if (selected is not { } row) { output[0] = area; return 1; }
        if (!Valid(area, row)) return 0;
        var count = 0;
        if (row.Min.Y > area.Min.Y) output[count++] = new(area.Min, new(area.Max.X, row.Min.Y));
        if (row.Max.Y < area.Max.Y) output[count++] = new(new(area.Min.X, row.Max.Y), area.Max);
        if (row.Min.X > area.Min.X) output[count++] = new(new(area.Min.X, row.Min.Y), new(row.Min.X, row.Max.Y));
        if (row.Max.X < area.Max.X) output[count++] = new(new(row.Max.X, row.Min.Y), new(area.Max.X, row.Max.Y));
        return count;
    }
}

internal enum ShopRendererBindingFailure
{
    None, ListMissing, ListOwnerMissing, ListOwnerMismatch, InvalidListHeader, RowOutOfRange, RowNotVisible, GetterMissing,
    MissingBacking, DuplicateVisibleRenderer, RendererChanged, RowIndexMismatch, RendererOwnerMissing, RendererOwnerMismatch,
    OutsideListAncestry, HiddenRenderer, WrongComponentType,
}
internal readonly record struct ShopRendererBindingResult(ShopRendererBindingFailure Failure, int ExpectedRow,
    int? BackingMatches = null, int? RendererIndex = null, int? ComponentType = null, int? VisibleMatches = null)
{
    internal bool Success => Failure == ShopRendererBindingFailure.None;
}
internal enum ShopHighlightBlockReason
{ None, RowUnavailable, ShopChanged, ListUnsettled, Offscreen, Renderer, Bounds, FocusOrOverlap, LayoutChanged }
internal sealed record ShopHighlightDiagnostic(bool active, string status, string? last_renderer_failure,
    int? expected_row, int? reported_row, int? backing_matches, int? component_type, int? visible_matches = null);

internal static class ShopHighlightMessages
{
    internal static string Blocked(ShopHighlightBlockReason reason, ShopRendererBindingFailure rendererFailure = ShopRendererBindingFailure.None) => reason switch
    {
        ShopHighlightBlockReason.RowUnavailable => "Selected shop row is unavailable.",
        ShopHighlightBlockReason.ShopChanged => "The shop changed; no highlight drawn.",
        ShopHighlightBlockReason.ListUnsettled => "Waiting for the shop list to settle.",
        ShopHighlightBlockReason.Offscreen => "Scroll to the selected item manually to show its highlight.",
        ShopHighlightBlockReason.Renderer => Renderer(rendererFailure),
        ShopHighlightBlockReason.Bounds => "The selected shop row is clipped or has unsupported bounds.",
        ShopHighlightBlockReason.FocusOrOverlap => "Focus the shop and clear overlapping game windows to show the highlight.",
        ShopHighlightBlockReason.LayoutChanged => "Shop layout changed; waiting for it to settle.",
        _ => "Shop highlight unavailable.",
    };
    internal static string Renderer(ShopRendererBindingFailure failure) => failure switch
    {
        ShopRendererBindingFailure.ListMissing => "Shop row not highlighted: the Buy list is missing.",
        ShopRendererBindingFailure.ListOwnerMissing => "Shop row not highlighted: the Buy list has no owner node.",
        ShopRendererBindingFailure.ListOwnerMismatch => "Shop row not highlighted: the Buy list owner points to another component.",
        ShopRendererBindingFailure.InvalidListHeader => "Shop row not highlighted: the Buy list counts or backing array are invalid.",
        ShopRendererBindingFailure.RowOutOfRange => "Shop row not highlighted: the selected row is outside the Buy list.",
        ShopRendererBindingFailure.RowNotVisible => "Shop row not highlighted: scroll to the selected item first.",
        ShopRendererBindingFailure.GetterMissing => "Shop row not highlighted: the list returned no renderer for this row.",
        ShopRendererBindingFailure.MissingBacking => "Shop row not highlighted: its renderer has no matching current list backing entry.",
        ShopRendererBindingFailure.DuplicateVisibleRenderer => "Shop row not highlighted: multiple visible rows reference the same renderer.",
        ShopRendererBindingFailure.RendererChanged => "Shop row not highlighted: its visible renderer association changed.",
        ShopRendererBindingFailure.RowIndexMismatch => "Shop row not highlighted: the renderer reports a different row index.",
        ShopRendererBindingFailure.RendererOwnerMissing => "Shop row not highlighted: the renderer has no owner node.",
        ShopRendererBindingFailure.RendererOwnerMismatch => "Shop row not highlighted: the renderer owner points to another component.",
        ShopRendererBindingFailure.OutsideListAncestry => "Shop row not highlighted: the renderer is outside the Buy list's node ancestry.",
        ShopRendererBindingFailure.HiddenRenderer => "Shop row not highlighted: the renderer or a parent node is hidden or draw-disabled.",
        ShopRendererBindingFailure.WrongComponentType => "Shop row not highlighted: the component is not a list-item renderer.",
        _ => "Selected shop row renderer verified.",
    };
}

/// <summary>Managed session, offer and lifetime gates; never reassociates a stale result by item ID.</summary>
internal sealed class ShopHighlightState
{
    internal static readonly TimeSpan BindingLifetime = TimeSpan.FromMinutes(5);
    private ShopHighlightNativeCapture? baseline;
    private int session;
    private long generation;
    private DateTimeOffset watchUntil;
    private ShopHighlightBinding? selectedBinding;
    private ShopRendererBindingResult? lastRendererResult;
    private ShopHighlightBlockReason lastBlockedReason;
    private ShopRendererBindingFailure lastBlockedRendererFailure;
    private int? lastExpectedRow;
    internal int SelectedRow { get; private set; } = -1;
    internal bool Active => selectedBinding is not null;
    internal DateTimeOffset ExpiresAt { get; private set; }
    internal string Status { get; private set; } = "Choose Find in current shop for a current shop result.";
    internal ShopHighlightFrame? Frame { get; private set; }
    internal ShopHighlightDiagnostic Diagnostic => new(Active, Status, lastRendererResult?.Failure.ToString(),
        lastRendererResult?.ExpectedRow ?? lastExpectedRow, lastRendererResult?.RendererIndex,
        lastRendererResult?.BackingMatches, lastRendererResult?.ComponentType, lastRendererResult?.VisibleMatches);
    internal bool Watching(DateTimeOffset now) => baseline is not null && now < watchUntil;

    internal static bool MatchesSnapshot(MenuSnapshot snapshot, ShopHighlightNativeCapture capture, DateTimeOffset now)
    {
        if (!snapshot.Available || snapshot.Category != "shops" || snapshot.Source != ShopReader.Source ||
            snapshot.CapturedAtUtc > now || now - snapshot.CapturedAtUtc >= BindingLifetime ||
            capture.Identity.OwnerId == 0 || capture.Identity.EventId == 0 || capture.Offers.Count is < 1 or > 60)
            return false;
        var rows = snapshot.Entries;
        if (rows.Count != capture.Offers.Count) return false;
        var indices = new HashSet<int>();
        for (var index = 0; index < rows.Count; ++index)
        {
            var row = rows[index];
            var offer = capture.Offers[index];
            if (offer.Row != index || offer.ItemIndex is < 0 or >= 60 || !indices.Add(offer.ItemIndex) ||
                offer.ItemId == 0 || offer.Quantity != 1 || offer.GilCost == 0 || row.Id != index + 1 || row.Slot != index ||
                row.GroupId is not null || row.Role != "offer" || row.Location != "Last observed Shop offers" ||
                row.ItemId != offer.ItemId || row.IsHq != offer.IsHq || row.Quantity != offer.Quantity ||
                row.Costs.Length != 1 || row.Costs[0].Kind != "gil" || row.Costs[0].ItemId is not null ||
                row.Costs[0].Amount != offer.GilCost || row.Costs[0].UnknownReason is not null)
                return false;
        }
        return true;
    }

    internal ShopHighlightBinding? Bind(MenuSnapshot snapshot, ShopHighlightNativeCapture capture,
        ulong owner, int sessionGeneration, DateTimeOffset now)
    {
        if (capture.Identity.OwnerId != owner || !MatchesSnapshot(snapshot, capture, now)) return null;
        if (!Watching(now) || session != sessionGeneration || !Same(baseline, capture))
        {
            Revoke("The open shop changed; choose a fresh shop result.");
            baseline = Copy(capture);
            session = sessionGeneration;
        }
        watchUntil = now.Add(BindingLifetime);
        return new(owner, sessionGeneration, generation, watchUntil, capture.Identity,
            new ReadOnlyCollection<ShopHighlightOffer>(capture.Offers.ToArray()));
    }

    internal bool IsCurrent(ShopHighlightBinding binding, DateTimeOffset now) => Watching(now) &&
        now < binding.ExpiresAt && binding.OwnerId != 0 && binding.OwnerId == baseline!.Identity.OwnerId &&
        binding.SessionGeneration == session && binding.OpenGeneration == generation &&
        binding.Identity == baseline.Identity && binding.Offers.SequenceEqual(baseline.Offers);

    internal bool Observe(ShopHighlightNativeCapture? capture, ulong owner, int sessionGeneration, DateTimeOffset now)
    {
        if (baseline is null) return false;
        if (!Watching(now) || owner == 0 || owner != baseline.Identity.OwnerId || session != sessionGeneration || !Same(baseline, capture))
        {
            Revoke("Shop, offers or character changed; ask for a fresh shop lookup.");
            return false;
        }
        return true;
    }

    // Waiting reply buttons need a cheap identity/lifecycle watch, not repeated offer-array copies.
    // ValidateBinding and active overlays still compare all offers against the immutable binding.
    internal bool ObserveIdentity(ShopHighlightNativeIdentity? identity, ulong owner, int sessionGeneration, DateTimeOffset now)
    {
        if (baseline is null) return false;
        if (!Watching(now) || owner == 0 || owner != baseline.Identity.OwnerId || session != sessionGeneration ||
            identity is null || identity.Value != baseline.Identity)
        {
            Revoke("Shop or character changed; ask for a fresh shop lookup.");
            return false;
        }
        return true;
    }

    internal bool Start(ShopHighlightBinding binding, int row, ulong owner, int sessionGeneration,
        int seconds, DateTimeOffset now)
    {
        BeginAttempt(row);
        Clear("This shop result is no longer current. Ask for a fresh shop lookup.");
        if (owner != binding.OwnerId || sessionGeneration != binding.SessionGeneration || !IsCurrent(binding, now) ||
            row < 0 || row >= binding.Offers.Count || binding.Offers[row].Row != row || seconds is < 5 or > 15) return false;
        selectedBinding = binding;
        SelectedRow = row;
        ExpiresAt = now.AddSeconds(seconds);
        Status = "Locating the selected visible shop row.";
        return true;
    }

    internal bool CheckActive(DateTimeOffset now)
    {
        if (selectedBinding is null) return false;
        if (!IsCurrent(selectedBinding, now)) Clear("The shop result expired; ask for a fresh lookup.");
        else if (now >= ExpiresAt) Clear(lastBlockedReason == ShopHighlightBlockReason.None ? "Shop highlight expired." :
            ShopHighlightMessages.Blocked(lastBlockedReason, lastBlockedRendererFailure) + " Highlight expired; click Find to retry.");
        return Active;
    }

    internal void Publish(ShopHighlightFrame frame, DateTimeOffset now)
    {
        if (!CheckActive(now)) return;
        if (frame.Row is { Valid: false }) { Clear("Unsupported shop row bounds; no highlight drawn."); return; }
        if (frame.DimArea is { } area && (frame.Row is { } selected ? !ShopHighlightDimming.Valid(area, selected) :
                !ShopHighlightDimming.ValidArea(area) || frame.BlockedReason is not (ShopHighlightBlockReason.Offscreen or ShopHighlightBlockReason.ListUnsettled)))
        { Clear("Unsupported shop dimming bounds; no highlight drawn."); return; }
        Frame = frame;
        // Derive the published diagnostic from fixed local outcomes, not arbitrary frame text.
        Status = frame.Row is not null ? "Selected shop row outlined." :
            ShopHighlightMessages.Blocked(frame.BlockedReason, frame.RendererResult?.Failure ?? ShopRendererBindingFailure.None);
        if (frame.RendererResult is { } renderer) lastRendererResult = renderer;
        lastBlockedReason = frame.Row is null ? frame.BlockedReason : ShopHighlightBlockReason.None;
        lastBlockedRendererFailure = frame.RendererResult?.Failure ?? ShopRendererBindingFailure.None;
    }

    internal ShopHighlightFrame? VisibleFrame(DateTimeOffset now) => Active && now < ExpiresAt &&
        selectedBinding is not null && IsCurrent(selectedBinding, now) && Frame is { } frame && now >= frame.CapturedAt &&
        now - frame.CapturedAt <= TimeSpan.FromMilliseconds(250) ? frame : null;

    internal void Clear(string reason = "Shop highlight cleared.")
    { selectedBinding = null; SelectedRow = -1; Frame = null; ExpiresAt = default; Status = reason; }

    internal void BeginAttempt(int row)
    {
        lastRendererResult = null; lastBlockedReason = ShopHighlightBlockReason.None;
        lastBlockedRendererFailure = ShopRendererBindingFailure.None;
        lastExpectedRow = row is >= 0 and < 60 ? row : null;
    }

    internal void Revoke(string reason = "Shop context changed; choose a fresh result.")
    { ++generation; baseline = null; session = 0; watchUntil = default; BeginAttempt(-1); Clear(reason); }

    internal static bool Same(ShopHighlightNativeCapture? first, ShopHighlightNativeCapture? second) =>
        first is not null && second is not null && first.Identity == second.Identity && first.Offers.SequenceEqual(second.Offers);
    private static ShopHighlightNativeCapture Copy(ShopHighlightNativeCapture source) =>
        new(source.Identity, new ReadOnlyCollection<ShopHighlightOffer>(source.Offers.ToArray()));
}
