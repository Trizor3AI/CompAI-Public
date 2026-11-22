using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace CompAI;

internal sealed record MenuCost(string Kind, uint? ItemId, string? Name, uint? Amount, string? UnknownReason = null);
internal sealed record MenuEntry(uint Id, uint ItemId, string Name, uint? Quantity, string Role, string Location,
    int? GroupId, int? Slot, bool? IsHq, MenuCost[] Costs, string? UnknownReason = null);

/// <summary>Detached observations for one explicitly permitted menu category and one answer.</summary>
internal sealed class MenuSnapshot
{
    // The loaded dresser may expose 800 outfit entries with up to 11 referenced pieces each.
    internal const int MaxEntries = 10000;
    internal const int MaxCosts = 8;
    internal const string PlateReferenceNotice = "A plate reference does not establish item ownership or storage membership.";
    internal string Category { get; }
    internal DateTimeOffset CapturedAtUtc { get; }
    internal bool Available { get; }
    internal string Source { get; }
    internal string Readiness { get; }
    internal string? UnavailableReason { get; }
    private readonly MenuEntry[] entries;
    // Deep copies keep both the snapshot and separate query pages isolated from mutable cost arrays.
    internal ReadOnlyCollection<MenuEntry> Entries => Array.AsReadOnly(entries.Select(Clone).ToArray());

    internal MenuSnapshot(string category, DateTimeOffset capturedAtUtc, bool available, string source,
        string readiness, IEnumerable<MenuEntry> entries, string? unavailableReason = null)
    {
        if (!IsCategory(category) || !ValidText(source, 256) || !ValidText(readiness, 256) ||
            (available ? unavailableReason is not null : !ValidText(unavailableReason, 256)))
            throw new ArgumentException("Invalid menu snapshot metadata.");
        var rows = entries.Take(MaxEntries + 1).ToArray();
        if (rows.Length > MaxEntries || (!available && rows.Length != 0) || rows.Any(row => !ValidEntry(category, row)) ||
            rows.Select(row => row.Id).Distinct().Count() != rows.Length)
            throw new ArgumentException("Invalid menu snapshot rows.");
        Category = category;
        CapturedAtUtc = capturedAtUtc.ToUniversalTime();
        Available = available;
        Source = source;
        Readiness = readiness;
        UnavailableReason = unavailableReason;
        this.entries = rows.OrderBy(row => row.Id).Select(Clone).ToArray();
    }

    internal static MenuSnapshot Unavailable(string category, string source, string reason) =>
        new(category, DateTimeOffset.UtcNow, false, source, "unavailable", [], reason);
    internal static bool IsCategory(string? category) => category is "shops" or "armoire" or "glamour_dresser" or "glamour_plates";
    internal static bool ValidText(string? text, int maximum) =>
        !string.IsNullOrWhiteSpace(text) && text.Length <= maximum && !text.Any(char.IsControl);
    internal static string Coverage(string category) => category switch
    {
        "shops" => "Only offers from the last observed supported shop/exchange. Saved offers last five minutes; they are not proof of the currently targeted NPC or open shop. " +
            "Quantity is an offer bundle, not owned inventory. " +
            "Costs describe the observed offer; absent or unknown costs never mean free. Stock, hidden tabs, requirements, affordability and purchase eligibility are unknown. " +
            "No purchase, target interaction or menu opening is performed. Open the shop to refresh. This observation may become stale after capture.",
        "armoire" => "Observed armoire storage membership only. Stored appearances are not additional physical inventory copies; null quantity is not zero. " +
            "No ownership total, withdrawal availability or complete wardrobe is inferred. This observation may become stale after capture.",
        "glamour_dresser" => "Observed loaded glamour dresser appearance entries only. These are stored appearances, not additional physical inventory copies or carried quantities. " +
            "No complete wardrobe or withdrawal eligibility is inferred. This observation may become stale after capture.",
        "glamour_plates" => "Observed loaded glamour plate references only. A referenced item is not proof of a stored or owned item, and repeated references are not duplicate physical ownership. " +
            "No equip, apply, restore or plate modification is performed. This observation may become stale after capture.",
        _ => throw new ArgumentException("Invalid menu category."),
    };

    private static MenuEntry Clone(MenuEntry entry) => entry with { Costs = entry.Costs.ToArray() };
    internal static bool ValidEntry(string category, MenuEntry? row) => row is not null && row.Id is > 0 and <= MaxEntries &&
        row.ItemId > 0 && ValidText(row.Name, 128) && ValidText(row.Location, 192) && row.Quantity is not 0 &&
        (row.GroupId is null or >= 0 and <= 65535) && (row.Slot is null or >= 0 and <= 65535) &&
        (row.UnknownReason is null || ValidText(row.UnknownReason, 256)) &&
        row.Role == (category == "shops" ? "offer" : category == "glamour_plates" ? "appearance_reference" : "stored") &&
        (row.Role != "appearance_reference" || row.Quantity is null) &&
        row.Costs is not null && row.Costs.Length <= MaxCosts && (category == "shops" || row.Costs.Length == 0) &&
        row.Costs.All(ValidCost);
    internal static bool ValidCost(MenuCost? cost) => cost is not null && cost.Kind is "gil" or "item" or "unknown" &&
        (cost.Name is null || ValidText(cost.Name, 128)) && cost.Amount is not 0 &&
        (cost.UnknownReason is null || ValidText(cost.UnknownReason, 256)) &&
        (cost.Kind == "item" ? cost.ItemId is > 0 : cost.ItemId is null) &&
        (cost.Kind != "unknown" && cost.Amount.HasValue || ValidText(cost.UnknownReason, 256));
}
