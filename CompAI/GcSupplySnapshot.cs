using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace CompAI;

internal sealed record GcSupplyEntry(int Position, uint ClassJobId, string ClassJobAbbreviation,
    string Category, uint ItemId, string Name, int QuantityRequested, int ExperienceReward,
    int SealReward, bool? TurnInAvailable, bool? BonusReward);

/// <summary>A bounded copy of the visible GC delivery menu; never a daily completion ledger.</summary>
internal sealed class GcSupplySnapshot
{
    internal const int MaxEntries = 11;
    internal const int MaxNameLength = 128;
    internal static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);
    internal const string Coverage = "Listed requests observed in the Grand Company delivery menu at the capture time. " +
        "Positions map to the eight crafting and three gathering classes. Missing classes are unknown. " +
        "Listed requests may include items already delivered today; their presence does not mean they remain outstanding. " +
        "turn_in_available is the menu's eligibility flag, never completion; false does not mean already delivered. " +
        "bonus_reward is always null: the native byte's relationship to displayed bonus stars is unverified. " +
        "Rewards are observed menu values, not a promised payout or evidence of a bonus. " +
        "No inventory counts, completion history or server/reset freshness is inferred.";
    private static readonly string[] Jobs = ["CRP", "BSM", "ARM", "GSM", "LTW", "WVR", "ALC", "CUL", "MIN", "BTN", "FSH"];

    internal DateTimeOffset CapturedAtUtc { get; }
    internal bool Available { get; }
    internal bool LastObserved { get; }
    internal string? UnavailableReason { get; }
    internal ReadOnlyCollection<GcSupplyEntry> Entries { get; }
    // These identities stay local; lookup DTOs never serialize them.
    internal ulong OwnerContentId { get; }
    internal nint MenuAddress { get; }
    internal nint AgentAddress { get; }
    internal short SelectedTab { get; }

    internal GcSupplySnapshot(DateTimeOffset capturedAtUtc, bool available, IEnumerable<GcSupplyEntry> entries,
        string? unavailableReason = null, ulong ownerContentId = 0, nint menuAddress = 0,
        nint agentAddress = 0, short selectedTab = 0, bool lastObserved = false)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var copied = entries.Take(MaxEntries + 1).ToArray();
        if (copied.Length > MaxEntries || copied.Any(entry => entry is null || entry.Position is < 0 or >= MaxEntries ||
            entry.ClassJobId != JobId(entry.Position) || entry.ClassJobAbbreviation != JobAbbreviation(entry.Position) ||
            entry.Category != Category(entry.Position) || entry.ItemId == 0 ||
            string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > MaxNameLength || entry.Name.Any(char.IsControl) ||
            entry.QuantityRequested is < 1 or > 255 || entry.ExperienceReward < 0 || entry.SealReward < 0 ||
            entry.BonusReward is not null) ||
            copied.Select(entry => entry.Position).Distinct().Count() != copied.Length ||
            (available ? copied.Length == 0 || unavailableReason is not null : copied.Length != 0) ||
            lastObserved && (!available || ownerContentId != 0 || menuAddress != 0 || agentAddress != 0 ||
                copied.Any(entry => entry.TurnInAvailable is not null)) ||
            unavailableReason is { Length: 0 or > 256 })
            throw new ArgumentException("Invalid Grand Company observation.", nameof(entries));
        CapturedAtUtc = capturedAtUtc.ToUniversalTime();
        Available = available;
        LastObserved = lastObserved;
        UnavailableReason = available ? null : unavailableReason ?? "Grand Company requests are unavailable.";
        Entries = Array.AsReadOnly(copied.OrderBy(entry => entry.Position).ToArray());
        OwnerContentId = ownerContentId;
        MenuAddress = menuAddress;
        AgentAddress = agentAddress;
        SelectedTab = selectedTab;
    }

    internal bool IsFresh(DateTimeOffset now) => now >= CapturedAtUtc - TimeSpan.FromSeconds(2) &&
        (this.LastObserved ? now - CapturedAtUtc < TimeSpan.FromMinutes(15) : now - CapturedAtUtc <= MaxAge);
    internal static uint JobId(int position) => checked((uint)(position + 8));
    internal static string JobAbbreviation(int position) => Jobs[position];
    internal static string Category(int position) => position < 8 ? "supply" : "provisioning";
    internal static GcSupplySnapshot Unavailable(string reason) => new(DateTimeOffset.UtcNow, false, [], reason);
}
