using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace CompAI;

internal sealed record CurrencyEntry(uint ItemId, string Name, int Quantity);

/// <summary>A bounded, managed observation; no identity or native pointers leave the reader.</summary>
internal sealed class CurrencySnapshot
{
    internal const int MaxEntries = 200;
    internal const int MaxNameLength = 128;
    internal const string Coverage = "Observed entries in the loaded Currency inventory container and supported " +
        "common CurrencyManager entries confirmed present by HasItem. Missing currencies are unknown, not zero. " +
        "Cosmic Exploration adds Cosmocredits and the current planet's credits only from a " +
        "ready HUD in the matching loaded zone (Lunar, Phaenna, Oizys or Auxesia). Other planets, weekly caps and other region/menu-specific balances " +
        "are not covered. Cosmic balances are not retained after leaving the zone.";

    internal DateTimeOffset CapturedAtUtc { get; }
    internal bool Available { get; }
    internal string? UnavailableReason { get; }
    internal ReadOnlyCollection<CurrencyEntry> Entries { get; }

    internal CurrencySnapshot(DateTimeOffset capturedAtUtc, bool available,
        IEnumerable<CurrencyEntry> entries, string? unavailableReason = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var copied = entries.Take(MaxEntries + 1).ToArray();
        if (copied.Length > MaxEntries || copied.Any(entry => entry is null || entry.ItemId == 0 ||
            entry.Quantity < 0 || string.IsNullOrWhiteSpace(entry.Name) ||
            entry.Name.Length > MaxNameLength || entry.Name.Any(char.IsControl)) ||
            copied.Select(entry => entry.ItemId).Distinct().Count() != copied.Length ||
            (!available && copied.Length != 0))
            throw new ArgumentException("Currency observation is invalid or ambiguous.", nameof(entries));
        if (unavailableReason is { Length: > 256 } || (available && unavailableReason is not null))
            throw new ArgumentException("Invalid currency availability reason.", nameof(unavailableReason));
        this.CapturedAtUtc = capturedAtUtc.ToUniversalTime();
        this.Available = available;
        this.UnavailableReason = available ? null : unavailableReason ?? "Currency container is unavailable.";
        this.Entries = Array.AsReadOnly(copied);
    }

    internal static CurrencySnapshot Unavailable(string reason) =>
        new(DateTimeOffset.UtcNow, false, Array.Empty<CurrencyEntry>(), reason);
}
