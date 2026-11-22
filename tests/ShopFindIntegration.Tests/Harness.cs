using System.Collections.Immutable;
using System.Text.Json;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;

namespace Dalamud.Game.Addon.Lifecycle { internal enum AddonEvent { PostSetup, PreFinalize, PostHide, PostClose } }
namespace Dalamud.Game.Addon.Lifecycle.AddonArgTypes { internal sealed class AddonArgs { } }

namespace CompAI
{
    public sealed partial class Plugin
    {
        private readonly object stateGate = new();
        private static readonly object GameGui = new();
        private static readonly FixtureLifecycle AddonLifecycle = new();
        internal static readonly FixtureFramework Framework = new();
        private static readonly FixtureClient ClientState = new();
        private readonly FixtureBetaStore betaSettings = new();
        internal readonly FixtureCompanion companion = new();
        private bool contextDisposed, contextCacheLoading;
        private ulong activeContentId = 9, cacheOwnerContentId = 9;
        private int contextLoadEpoch = 2, currencyAreaEpoch = 3;
        private long valuationAccessEpoch = 4;
        private object? preparedQuestion;
        private bool CanReadCurrentCharacter => !contextDisposed && !contextCacheLoading && activeContentId == 9;
        private bool InventoryHighlightEnabled => false;
        private FixtureAccess Access { get; set; } = new(true, false);
        private static bool QuestLoading() => false;
        private static ulong ReadLoadedCharacterId() => 9;
        private void RevokeInventoryFindUnderLock(string _) { }
        private void PersistContextUnderLock() { }
        internal ShopHighlightController Native => this.shopHighlight;
        internal Plugin(MenuSnapshot snapshot)
        {
            Framework.Timeout = false;
            this.shopReader = new(); this.glamourReader = new();
            this.observedMenus = [MenuObservationCache.From(snapshot)];
            this.InitializeShopHighlight();
        }
        internal Func<JsonElement, string> Query() => this.CreateMenuQueryUnderLock()!;
        internal void Lifecycle() => this.OnShopHighlightLifecycle(AddonEvent.PreFinalize, new());
        internal void ChangeSession() => ++this.contextLoadEpoch;
        internal void RevokeAccess() { this.Access = this.Access with { Shops = false }; ++this.valuationAccessEpoch; }
        internal void ChangeArea() { ++this.currencyAreaEpoch; this.ResetMenuCaptureUnderLock(); }
        internal void TickHighlight() => this.UpdateShopHighlight();
        internal void ExpireNotice() => this.shopFindNoticeUntil = DateTimeOffset.UtcNow.AddSeconds(-1);
    }
    internal sealed record FixtureAccess(bool Shops, bool Glamour);
    internal sealed record FixtureBeta(bool InventoryHighlight = false, bool ShopHighlight = true);
    internal sealed class FixtureBetaStore
    {
        internal FixtureBeta Settings { get; private set; } = new();
        internal bool Save(FixtureBeta value) { Settings = value; return true; }
    }
    internal sealed class FixtureClient { internal bool IsLoggedIn => true; }
    internal sealed class FixtureLifecycle
    {
        internal void RegisterListener(AddonEvent type, string name, Action<AddonEvent, AddonArgs> callback) { }
    }
    internal sealed class FixtureFramework
    {
        internal bool Timeout;
        internal int LastThread;
        internal Task<T> RunOnFrameworkThread<T>(Func<T> action) => Timeout ? new TaskCompletionSource<T>().Task :
            Task.Run(() => { LastThread = Environment.CurrentManagedThreadId; return action(); });
    }
    internal sealed class FixtureCompanion
    {
        internal bool Continues = true;
        internal Guid? Key;
        internal int AttachThread;
        internal Func<bool> CaptureQuestionContinuation() => () => Continues;
        internal void AttachShopFindKey(Guid key) { Key = key; AttachThread = Environment.CurrentManagedThreadId; }
    }
    internal sealed class ShopReader
    {
        internal const string Source = "Synthetic shop source";
        internal MenuSnapshot Capture() => MenuSnapshot.Unavailable("shops", Source, "Synthetic unavailable");
    }
    internal sealed class GlamourReader
    { internal MenuSnapshot Capture(string category) => MenuSnapshot.Unavailable(category, "Synthetic", "Synthetic unavailable"); }
    internal sealed class PerformanceDiagnostics : IDisposable
    { internal static PerformanceDiagnostics Measure(string _) => new(); public void Dispose() { } }
    // Only the native boundary is fake. The query coordinator, catalog and lifetime checks are production source above.
    internal sealed class ShopHighlightController(object gui)
    {
        internal int Captures, Validates;
        internal bool Missing, Throws, Current = true;
        internal ShopHighlightBinding? Binding;
        internal bool Active { get; private set; }
        internal bool StopOnUpdate;
        internal string Status { get; private set; } = "Synthetic native boundary";
        internal ShopHighlightBinding? CaptureBinding(MenuSnapshot frozen, ulong owner, int session, out string reason)
        {
            Captures++; reason = "Synthetic";
            if (Throws) throw new InvalidOperationException("Synthetic native failure");
            if (Missing) return null;
            var offers = frozen.Entries.Select(row => new ShopHighlightOffer(row.Slot!.Value, row.Slot.Value,
                row.ItemId, row.IsHq!.Value, row.Quantity!.Value, row.Costs[0].Amount!.Value)).ToArray();
            return Binding = new(owner, session, 1, DateTimeOffset.UtcNow.AddMinutes(5),
                new(owner, 1, 2, 3, 4, 5, 6, 7, 8, 9), offers);
        }
        internal bool IsCurrent(ShopHighlightBinding value) => Current && ReferenceEquals(Binding, value);
        internal bool ValidateBinding(ShopHighlightBinding value) { Validates++; return IsCurrent(value); }
        internal bool Start(ShopHighlightBinding value, int row, ulong owner, int session, int seconds)
        { Active = IsCurrent(value); return Active; }
        internal void Update(bool allowed, ulong owner, int session, bool loading)
        {
            if (StopOnUpdate) { Active = false; Status = "Synthetic renderer unavailable; highlight expired."; }
        }
        internal void Draw() { }
        internal void Clear(string reason = "") { Active = false; Status = reason; }
        internal void Revoke(string reason = "") { Current = false; Active = false; Status = reason; }
    }
}
