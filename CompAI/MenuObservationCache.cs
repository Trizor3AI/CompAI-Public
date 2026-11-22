using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;

namespace CompAI;

// Managed observations only. The surrounding character cache supplies the owner.
internal sealed record CachedMenuObservation(string Category, DateTimeOffset CapturedAtUtc,
    string Source, ImmutableArray<MenuEntry> Entries);

internal static class MenuObservationCache
{
    internal static readonly TimeSpan ShopLifetime = TimeSpan.FromMinutes(5);

    internal static void Validate(ImmutableArray<CachedMenuObservation> observations)
    {
        if (observations.IsDefault || observations.Length > 4 || observations.Any(value => value is null) ||
            observations.Select(value => value.Category).Distinct().Count() != observations.Length)
            throw new InvalidDataException("Invalid saved menu observations.");
        foreach (var value in observations)
        {
            if (value.CapturedAtUtc == default || value.Entries.IsDefault)
                throw new InvalidDataException("Invalid saved menu timestamp or entries.");
            _ = new MenuSnapshot(value.Category, value.CapturedAtUtc, true, value.Source,
                "saved_menu_observation", value.Entries);
        }
    }

    internal static bool IsUsable(CachedMenuObservation value, DateTimeOffset now) =>
        value.CapturedAtUtc <= now && (value.Category != "shops" || now - value.CapturedAtUtc < ShopLifetime);

    internal static CachedMenuObservation From(MenuSnapshot snapshot)
    {
        if (!snapshot.Available) throw new ArgumentException("Only available observations can be saved.");
        return new(snapshot.Category, snapshot.CapturedAtUtc, snapshot.Source, snapshot.Entries.ToImmutableArray());
    }

    internal static bool SameContent(CachedMenuObservation first, CachedMenuObservation second)
    {
        if (first.Category != second.Category || first.Source != second.Source || first.Entries.IsDefault ||
            second.Entries.IsDefault || first.Entries.Length != second.Entries.Length) return false;
        for (var i = 0; i < first.Entries.Length; i++)
        {
            var a = first.Entries[i];
            var b = second.Entries[i];
            if (a is null || b is null || a.Id != b.Id || a.ItemId != b.ItemId || a.Name != b.Name ||
                a.Quantity != b.Quantity || a.Role != b.Role || a.Location != b.Location || a.GroupId != b.GroupId ||
                a.Slot != b.Slot || a.IsHq != b.IsHq || a.UnknownReason != b.UnknownReason ||
                a.Costs is null || b.Costs is null || !a.Costs.AsSpan().SequenceEqual(b.Costs)) return false;
        }
        return true;
    }

    internal static ImmutableArray<CachedMenuObservation> Merge(ImmutableArray<CachedMenuObservation> observations,
        CachedMenuObservation capture)
    {
        var old = observations.FirstOrDefault(value => value.Category == capture.Category);
        // A repeat of the validated saved rows needs no new snapshot or deep copies.
        if (old is not null && capture.CapturedAtUtc != default && SameContent(old, capture))
        {
            if (capture.CapturedAtUtc - old.CapturedAtUtc < TimeSpan.FromMinutes(1)) return observations;
        }
        else
        {
            Validate([capture]);
            if (old is not null && capture.CapturedAtUtc <= old.CapturedAtUtc) return observations;
        }
        return observations.Where(value => value.Category != capture.Category).Append(capture)
            .OrderBy(value => value.Category, StringComparer.Ordinal).ToImmutableArray();
    }

    internal static MenuSnapshot Freeze(ImmutableArray<CachedMenuObservation> observations, string category, DateTimeOffset now,
        string? unavailableReason = null)
    {
        var value = observations.FirstOrDefault(value => value.Category == category && IsUsable(value, now));
        return value is null
            ? MenuSnapshot.Unavailable(category, "Last observed menus for this character",
                MenuSnapshot.ValidText(unavailableReason, 256) ? unavailableReason! :
                category == "shops" ? "No recent shop observation. It may never have been captured, or its five-minute retention has elapsed. Open a supported shop to refresh."
                    : "No saved observation. Open the relevant menu with access enabled and let it load once to save its contents.")
            : new(category, value.CapturedAtUtc, true, value.Source,
                category == "shops" ? "saved_menu_observation; last_shop_only; expires_after_five_minutes"
                    : "saved_menu_observation; last_known_contents; open_to_refresh", value.Entries);
    }
}
