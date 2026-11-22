using System;
using System.Threading;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;

namespace CompAI;

public sealed partial class Plugin
{
    private ShopHighlightController shopHighlight = null!;
    private readonly ShopFindCatalog shopFind = new();
    private long shopHighlightChanges;
    private bool shopHighlightWasAllowed;
    private (Guid Key, uint Id)? pendingShopHighlight;
    private string? shopFindNotice;
    private DateTimeOffset shopFindNoticeUntil;
    private Guid? shopFindFeedbackKey;

    private void InitializeShopHighlight()
    {
        this.shopHighlight = new(GameGui);
        foreach (var type in new[] { AddonEvent.PostSetup, AddonEvent.PreFinalize, AddonEvent.PostHide, AddonEvent.PostClose })
            AddonLifecycle.RegisterListener(type, "Shop", this.OnShopHighlightLifecycle);
    }

    // No native reads: lifecycle edges revoke even when a window reuses its address between updates.
    private void OnShopHighlightLifecycle(AddonEvent _, AddonArgs __)
    {
        lock (this.stateGate)
            this.RevokeShopFindUnderLock("The shop was closed or reopened. Ask for a fresh shop lookup.");
    }

    internal bool ShopHighlightEnabled { get { lock (this.stateGate) return this.betaSettings.Settings.ShopHighlight; } }
    internal bool ShopHighlightActive { get { lock (this.stateGate) return this.pendingShopHighlight is not null || this.shopHighlight.Active; } }
    internal string ShopHighlightStatus { get { lock (this.stateGate) return this.pendingShopHighlight is not null
        ? "Checking the selected shop offer…" : this.shopFindNotice ?? this.shopHighlight.Status; } }
    internal string? ShopFindNotice { get { lock (this.stateGate) return this.ShopHighlightActive
        ? this.ShopHighlightStatus : DateTimeOffset.UtcNow < this.shopFindNoticeUntil ? this.shopFindNotice : null; } }

    internal string? ShopFindFeedback(Guid key)
    {
        lock (this.stateGate) return this.shopFindFeedbackKey == key ? this.ShopHighlightStatus : null;
    }

    private void SetShopFindNoticeUnderLock(string message)
    {
        this.shopFindNotice = message;
        this.shopFindNoticeUntil = DateTimeOffset.UtcNow.AddSeconds(10);
    }

    internal void SetShopHighlight(bool enabled)
    {
        lock (this.stateGate)
        {
            this.betaSettings.Save(this.betaSettings.Settings with { ShopHighlight = enabled });
            this.RevokeShopFindUnderLock(this.ShopHighlightEnabled
                ? "Find in current shop is ready for a fresh shop lookup." : "Shop highlighting is off.");
            if (!this.InventoryHighlightEnabled) this.RevokeInventoryFindUnderLock("Inventory highlighting is off.");
        }
    }

    internal void ClearShopHighlight()
    {
        lock (this.stateGate)
        {
            this.pendingShopHighlight = null;
            this.shopFindNotice = null;
            this.shopFindFeedbackKey = null;
            this.shopHighlight.Clear();
        }
    }

    private ShopFindStamp ShopFindStampUnderLock() =>
        new(this.activeContentId, this.contextLoadEpoch, this.currencyAreaEpoch, this.valuationAccessEpoch, this.shopHighlightChanges);

    private bool ShopFindCurrentUnderLock(ShopFindStamp stamp) =>
        this.ShopHighlightEnabled && this.Access.Shops && this.CanReadCurrentCharacter &&
        stamp == this.ShopFindStampUnderLock();

    // This optional local action does not change the five-minute historical tool result.
    // The first returned page binds a matching CURRENT shop, never the historical NPC.
    private Action<MenuLookupResult, Func<bool>>? CreateShopFindQueryUnderLock(MenuSnapshot frozen)
    {
        if (!this.ShopHighlightEnabled || !this.Access.Shops || !this.CanReadCurrentCharacter || !frozen.Available)
            return null;
        var key = Guid.NewGuid();
        var stamp = this.ShopFindStampUnderLock();
        var expires = frozen.CapturedAtUtc.Add(MenuObservationCache.ShopLifetime);
        ShopHighlightBinding? binding = null;
        var attempted = false;
        return (result, continues) =>
        {
            if (result.category != "shops" || !result.available || result.entries.Length == 0) return;
            lock (this.stateGate)
                if (!this.ShopFindCurrentUnderLock(stamp) || !continues() || DateTimeOffset.UtcNow >= expires) return;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var token = deadline.Token;
            bool added;
            try
            {
                added = Framework.RunOnFrameworkThread(() =>
                {
                    lock (this.stateGate)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!this.ShopFindCurrentUnderLock(stamp) || !continues() || QuestLoading() ||
                            ReadLoadedCharacterId() != stamp.Owner || DateTimeOffset.UtcNow >= expires) return false;
                        if (!attempted)
                        {
                            attempted = true;
                            binding = this.shopHighlight.CaptureBinding(frozen, stamp.Owner, stamp.Session, out _);
                        }
                        else if (binding is not null && !this.shopHighlight.ValidateBinding(binding))
                        {
                            this.shopFind.Remove(key);
                            return false;
                        }
                        token.ThrowIfCancellationRequested();
                        if (binding is null || !this.ShopFindCurrentUnderLock(stamp) || !continues() ||
                            ReadLoadedCharacterId() != stamp.Owner || !this.shopHighlight.IsCurrent(binding)) return false;
                        return this.shopFind.Add(key, stamp, binding, expires < binding.ExpiresAt ? expires : binding.ExpiresAt,
                            result.entries, DateTimeOffset.UtcNow);
                    }
                }).WaitAsync(token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { return; }
            catch (Exception) { return; } // Highlight failure never suppresses a valid historical shop reply.
            lock (this.stateGate)
            {
                if (added && this.ShopFindCurrentUnderLock(stamp) && continues() && binding is not null &&
                    this.shopHighlight.IsCurrent(binding)) this.companion.AttachShopFindKey(key);
            }
        };
    }

    internal ShopFindGroup? ShopFindItems(Guid key)
    {
        lock (this.stateGate)
        {
            var group = this.shopFind.Get(key, DateTimeOffset.UtcNow);
            return group is not null && this.ShopFindCurrentUnderLock(group.Stamp) &&
                this.shopHighlight.IsCurrent(group.Binding) ? group : null;
        }
    }

    internal string? FindShopItem(Guid key, uint id)
    {
        lock (this.stateGate)
        {
            var group = this.ShopFindItems(key);
            if (group is null) return "This shop offer is no longer current. Keep the shop open and ask for a fresh lookup.";
            if (!System.Linq.Enumerable.Any(group.Items, item => item.Id == id)) return "This shop offer is unavailable.";
            this.shopFindNotice = null;
            this.shopFindFeedbackKey = key;
            this.pendingShopHighlight = (key, id);
            return null;
        }
    }

    private void RevokeShopFindUnderLock(string reason)
    {
        var wasActive = this.pendingShopHighlight is not null || this.shopHighlight?.Active == true;
        ++this.shopHighlightChanges;
        this.pendingShopHighlight = null;
        if (wasActive) this.SetShopFindNoticeUnderLock(reason);
        else
        {
            this.shopFindNotice = null;
            this.shopFindFeedbackKey = null;
        }
        this.shopFind.Clear();
        this.shopHighlight?.Revoke(reason);
    }

    private void UpdateShopHighlight()
    {
        lock (this.stateGate)
        {
            var allowed = this.ShopHighlightEnabled && this.Access.Shops && this.CanReadCurrentCharacter;
            if (!allowed && this.shopHighlightWasAllowed)
                this.RevokeShopFindUnderLock("Shop highlighting is unavailable for this session.");
            this.shopHighlightWasAllowed = allowed;
            var loading = QuestLoading();
            var wasActive = this.shopHighlight.Active;
            this.shopHighlight.Update(allowed, this.activeContentId, this.contextLoadEpoch, loading);
            if (wasActive && !this.shopHighlight.Active)
                this.SetShopFindNoticeUnderLock(this.shopHighlight.Status);
            if (this.pendingShopHighlight is not { } pending) return;
            this.pendingShopHighlight = null;
            var group = this.ShopFindItems(pending.Key);
            if (!allowed || loading || group is null || ReadLoadedCharacterId() != group.Stamp.Owner ||
                !this.shopHighlight.Start(group.Binding, checked((int)pending.Id - 1), group.Stamp.Owner, group.Stamp.Session, 15))
                this.SetShopFindNoticeUnderLock(group is null || loading
                    ? "The shop changed or is loading. Ask for a fresh shop lookup." : this.shopHighlight.Status);
        }
    }

    private void DrawShopHighlight()
    {
        lock (this.stateGate)
            if (!this.contextDisposed && this.ShopHighlightEnabled && this.Access.Shops && this.CanReadCurrentCharacter)
                this.shopHighlight.Draw();
    }
}
