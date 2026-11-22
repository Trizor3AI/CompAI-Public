using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace CompAI;

public sealed partial class Plugin
{
    private readonly ShopReader shopReader;
    private readonly GlamourReader glamourReader;
    private ImmutableArray<CachedMenuObservation> observedMenus = ImmutableArray<CachedMenuObservation>.Empty;
    private ImmutableArray<CachedMenuObservation> persistedMenus = ImmutableArray<CachedMenuObservation>.Empty;
    private readonly Dictionary<string, CachedMenuObservation> menuCandidates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> menuUnavailableReasons = new(StringComparer.Ordinal);
    private DateTimeOffset nextMenuCheck;

    private bool CanUseSavedMenusUnderLock => !this.contextDisposed && !this.contextCacheLoading &&
        this.cacheOwnerContentId != 0 && (!ClientState.IsLoggedIn || this.activeContentId == this.cacheOwnerContentId);

    private bool HasSavedMenuUnderLock(string category) => this.CanUseSavedMenusUnderLock &&
        this.observedMenus.Any(value => value.Category == category && MenuObservationCache.IsUsable(value, DateTimeOffset.UtcNow));

    private void ResetMenuCaptureUnderLock()
    {
        this.RevokeShopFindUnderLock("Shop context changed. Ask for a fresh shop lookup.");
        this.menuCandidates.Clear();
        this.menuUnavailableReasons.Clear();
        this.nextMenuCheck = default;
    }

    private void PollObservedMenus()
    {
        lock (this.stateGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (now < this.nextMenuCheck) return;
            this.nextMenuCheck = now.AddSeconds(1);
            if (!this.CanReadCurrentCharacter || QuestLoading()) { this.menuCandidates.Clear(); return; }
            var owner = this.activeContentId;
            if (ReadLoadedCharacterId() != owner) { this.menuCandidates.Clear(); return; }
            var changed = false;
            foreach (var category in this.MenuCategoriesUnderLock())
            {
                try
                {
                    using var timing = PerformanceDiagnostics.Measure("reader.menus.observe");
                    var reading = category == "shops" ? this.shopReader.Capture() : this.glamourReader.Capture(category);
                    if (ReadLoadedCharacterId() != owner || !this.CanReadCurrentCharacter || QuestLoading())
                    { this.menuCandidates.Clear(); return; }
                    if (!reading.Available)
                    {
                        this.menuCandidates.Remove(category);
                        if (MenuSnapshot.ValidText(reading.UnavailableReason, 256))
                            this.menuUnavailableReasons[category] = reading.UnavailableReason!;
                        continue;
                    }
                    this.menuUnavailableReasons.Remove(category);
                    var capture = MenuObservationCache.From(reading);
                    if (!this.menuCandidates.TryGetValue(category, out var before) ||
                        !MenuObservationCache.SameContent(before, capture) || before.CapturedAtUtc > capture.CapturedAtUtc)
                    { this.menuCandidates[category] = capture; continue; }
                    if (capture.CapturedAtUtc - before.CapturedAtUtc < TimeSpan.FromMilliseconds(250)) continue;
                    var merged = MenuObservationCache.Merge(this.observedMenus, capture);
                    changed |= merged != this.observedMenus;
                    this.observedMenus = merged;
                }
                catch (Exception)
                {
                    // Bad/transitional native observations never erase a previously valid copy.
                    this.menuCandidates.Remove(category);
                    this.menuUnavailableReasons[category] = "The observed menu could not be read safely. Close and reopen it to refresh.";
                }
            }
            if (changed)
            {
                this.preparedQuestion = null;
                this.PersistContextUnderLock();
            }
        }
    }

    private string[] MenuCategoriesUnderLock()
    {
        var categories = new List<string>(4);
        if (this.Access.Shops) categories.Add("shops");
        if (this.Access.Glamour) categories.AddRange(["armoire", "glamour_dresser", "glamour_plates"]);
        return categories.ToArray();
    }

    private Func<JsonElement, string>? CreateMenuQueryUnderLock()
    {
        if (!this.CanUseSavedMenusUnderLock) return null;
        var allowed = this.MenuCategoriesUnderLock();
        if (allowed.Length == 0) return null;
        var owner = this.cacheOwnerContentId;
        var session = this.contextLoadEpoch;
        var accessEpoch = this.valuationAccessEpoch;
        bool Permitted(string category) => allowed.Contains(category) && this.MenuCategoriesUnderLock().Contains(category) &&
            this.CanUseSavedMenusUnderLock && owner == this.cacheOwnerContentId &&
            session == this.contextLoadEpoch && accessEpoch == this.valuationAccessEpoch;
        // Freeze all permitted observations when the question starts, including absence.
        // Later menu openings cannot mix different observation revisions into one answer.
        var frozenAt = DateTimeOffset.UtcNow;
        var shop = this.observedMenus.FirstOrDefault(value => value.Category == "shops" && MenuObservationCache.IsUsable(value, frozenAt));
        var shopExpires = shop is null ? DateTimeOffset.MaxValue : shop.CapturedAtUtc.Add(MenuObservationCache.ShopLifetime);
        var captured = allowed.ToDictionary(category => category, category =>
            new MenuLookup(MenuObservationCache.Freeze(this.observedMenus, category, frozenAt,
                this.menuUnavailableReasons.GetValueOrDefault(category))), StringComparer.Ordinal);
        var findShop = this.ShopHighlightEnabled && this.CanReadCurrentCharacter && allowed.Contains("shops") ? this.CreateShopFindQueryUnderLock(
            MenuObservationCache.Freeze(this.observedMenus, "shops", frozenAt,
                this.menuUnavailableReasons.GetValueOrDefault("shops"))) : null;
        var queryGate = new object();
        return arguments =>
        {
            MenuLookup.ValidateArguments(arguments);
            var category = arguments.GetProperty("category").GetString()!;
            var continues = this.companion.CaptureQuestionContinuation();
            lock (queryGate)
            {
                lock (this.stateGate)
                {
                    if (!Permitted(category) || !continues()) throw new OperationCanceledException();
                    if (category == "shops" && DateTimeOffset.UtcNow >= shopExpires)
                        throw new IOException("The saved shop observation expired; open the shop to refresh.");
                }
                // Compare names on the detached snapshot without holding the game state lock.
                // Permissions, session and expiry are checked again before returning anything.
                var result = captured[category].Query(arguments);
                if (category == "shops") findShop?.Invoke(result, continues);
                lock (this.stateGate)
                {
                    if (!Permitted(category) || !continues()) throw new OperationCanceledException();
                    if (category == "shops" && DateTimeOffset.UtcNow >= shopExpires)
                        throw new IOException("The saved shop observation expired; open the shop to refresh.");
                    return JsonSerializer.Serialize(result);
                }
            }
        };
    }
}
