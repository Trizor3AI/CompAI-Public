using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace CompAI;

internal sealed record DutyEntry(uint DutyId, uint? InstanceContentId, string Name,
    bool? Unlocked, bool? Completed, string? UnknownReason);

/// <summary>Managed, bounded observations. Native false is unknown until negative-state readiness is proven.</summary>
internal sealed class DutySnapshot
{
    internal const int MaxEntries = 4096;
    internal const int MaxNameLength = 128;
    internal const string Coverage = "Local ContentFinderCondition names with validated InstanceContent links. " +
        "True means the game's read-only instance-content API observed that flag. Null means unknown: " +
        "negative-state readiness is unverified or this content type is unsupported. Unknown never means locked " +
        "or not completed. Unlock and completion are independent; no queue eligibility, rewards or server freshness is implied.";

    internal DateTimeOffset CapturedAtUtc { get; }
    internal bool Available { get; }
    internal string? UnavailableReason { get; }
    internal ReadOnlyCollection<DutyEntry> Entries { get; }

    internal DutySnapshot(DateTimeOffset capturedAtUtc, bool available,
        IEnumerable<DutyEntry> entries, string? unavailableReason = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var copied = entries.Take(MaxEntries + 1).ToArray();
        if (copied.Length > MaxEntries || copied.Any(entry => entry is null || entry.DutyId == 0 ||
            entry.InstanceContentId == 0 || string.IsNullOrWhiteSpace(entry.Name) ||
            entry.Name.Length > MaxNameLength || entry.Name.Any(char.IsControl) ||
            entry.Unlocked == false || entry.Completed == false ||
            (entry.InstanceContentId is null && (entry.Unlocked is not null || entry.Completed is not null)) ||
            ((entry.Unlocked is null || entry.Completed is null) != (entry.UnknownReason is not null)) ||
            entry.UnknownReason is { Length: 0 or > 256 }) ||
            copied.Select(entry => entry.DutyId).Distinct().Count() != copied.Length ||
            (!available && copied.Length != 0))
            throw new ArgumentException("Duty observation is invalid or ambiguous.", nameof(entries));
        if (unavailableReason is { Length: > 256 } || (available && unavailableReason is not null))
            throw new ArgumentException("Invalid duty availability reason.", nameof(unavailableReason));
        CapturedAtUtc = capturedAtUtc.ToUniversalTime();
        Available = available;
        UnavailableReason = available ? null : unavailableReason ?? "Duty state is unavailable.";
        Entries = Array.AsReadOnly(copied);
    }

    internal static DutySnapshot Unavailable(string reason) => new(DateTimeOffset.UtcNow, false, [], reason);
}
