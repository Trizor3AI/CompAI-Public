using System;
using System.Linq;

namespace CompAI;

/// <summary>Detached wire contract; no game dependencies or inferred personal completion.</summary>
internal static class MogpendiumContract
{
    internal static readonly string[] Categories =
        ["mogpendium_standard", "mogpendium_weekly", "mogpendium_minimog", "mogpendium_ultimog"];
    internal const string Unknowns = "Completion, reward claims, selected week and event timing are unknown. Rewards are displayed previews, not ownership.";
    internal const string Definition = "Public objective definition; completion and claims are unknown.";
    private const string CommonCoverage = " Selected event and category are corroborated by the visible heading, currency labels and category description. Completion, reward claims, selected week and event timing are unknown. Stale or off-screen list widgets are excluded.";

    internal static bool IsCategory(string? category) => category is
        "mogpendium_standard" or "mogpendium_weekly" or "mogpendium_minimog" or "mogpendium_ultimog";
    internal static string Source(string category) => category switch
    {
        "mogpendium_weekly" or "mogpendium_minimog" => "Owned visible MoogleCollection active backing list or independently supported detail pane; localized CSBonusSeason, CSBonusMission, CSBonusContent and related public definitions",
        "mogpendium_standard" or "mogpendium_ultimog" => "Owned visible MoogleCollection detail pane; localized CSBonusSeason, CSBonusMission, CSBonusContent and related public definitions",
        _ => throw new ArgumentException("Unsupported Mogpendium category."),
    };
    internal static string Coverage(string category) => (category switch
    {
        "mogpendium_standard" => "Full public Standard objective catalog for the selected event, including off-screen definitions, plus the selected objective and displayed rewards. Catalog rewards are shown only when the public definition has one fixed amount. Definitions do not establish personal completion or ownership.",
        "mogpendium_weekly" => "Complete observed Weekly list when its backing structure is supported, with displayed week labels and uniquely matched public duties. Independently supported selected objective detail and displayed rewards may also be included. At least one of the list or selected detail is present. Other event views, completion and claims are not covered.",
        "mogpendium_minimog" => "Complete observed Minimog choice list when its backing structure is supported, with displayed week labels and choice positions. Generic choice labels may not identify a unique public objective. Independently supported selected objective detail and displayed rewards may also be included. At least one of the list or selected detail is present. Other event views, completion and claims are not covered.",
        "mogpendium_ultimog" => "Only the selected Ultimog challenge detail and displayed rewards are observed. Other objectives and event periods are not covered.",
        _ => throw new ArgumentException("Unsupported Mogpendium category."),
    }) + CommonCoverage;

    internal static bool ValidEntry(string category, ActivityEntry row) => IsCategory(category) && row is not null &&
        row.Required is null && (row.Id is >= 100 and <= 131 && category is "mogpendium_weekly" or "mogpendium_minimog"
            ? row.Group == "observed_list" && ActivitySnapshot.ValidText(row.Name, 128) && ActivitySnapshot.ValidText(row.Text, 128) &&
                row.Value is null && row.Unit is null && (category == "mogpendium_weekly" ? row.Slot == 1 : row.Slot is 1 or 2) &&
                (row.ReferenceId is > 0 and <= ushort.MaxValue && row.ReferenceKind == "cs_bonus_content" ||
                    category == "mogpendium_minimog" && row.ReferenceId is null && row.ReferenceKind is null)
            : row.Slot is null && (row.Id switch
        {
            1 => row.Group == "event" && row.Name == "Selected event" && ActivitySnapshot.ValidText(row.Text, 256) &&
                row.Value is >= 1 and <= ushort.MaxValue && row.Unit == "season" && row.ReferenceId is null,
            2 => row.Group == "event" && row.Name == "Selected category" && ActivitySnapshot.ValidText(row.Text, 128) &&
                row.Value is null && row.Unit is null && row.ReferenceId is null,
            3 => row.Group == "coverage" && row.Name == "Unknown state" && row.Text == Unknowns &&
                row.Value is null && row.Unit is null && row.ReferenceId is null,
            4 or 5 => row.Group == "event" && row.Name == (row.Id == 4 ? "Reward currency" : "Secondary reward currency") &&
                ActivitySnapshot.ValidText(row.Text, 128) && row.Value is null && row.Unit is null &&
                row.ReferenceId is > 0 and <= 1000000 && row.ReferenceKind == "item",
            10 => row.Group == "selection" && ActivitySnapshot.ValidText(row.Name, 128) && ActivitySnapshot.ValidText(row.Text, 256) &&
                row.Value is null && row.Unit is null && row.ReferenceId is > 0 and <= ushort.MaxValue && row.ReferenceKind == "cs_bonus_content",
            11 or 12 => row.Group == "selection" && row.Name == (row.Id == 11 ? "Displayed reward" : "Displayed secondary reward") &&
                row.Text is null && row.Value is >= 1 and <= ushort.MaxValue && row.Unit == "items" &&
                row.ReferenceId is > 0 and <= 1000000 && row.ReferenceKind == "item",
            >= 100 and <= 299 => category == "mogpendium_standard" && row.Group == "catalog" &&
                ActivitySnapshot.ValidText(row.Name, 128) && row.Text == Definition &&
                (row.Value is null ? row.Unit is null : row.Value is >= 1 and <= byte.MaxValue && row.Unit == "items") &&
                row.ReferenceId is > 0 and <= ushort.MaxValue && row.ReferenceKind == "cs_bonus_content",
            _ => false,
        }));

    internal static bool ValidShape(string category, ActivityEntry[] rows)
    {
        if (!IsCategory(category) || rows is null || rows.Length is < 5 or > 208 ||
            rows.Any(row => row is null || !ValidEntry(category, row)) || rows.Select(row => row.Id).Distinct().Count() != rows.Length ||
            new uint[] { 1, 2, 3, 4 }.Any(id => rows.Count(row => row.Id == id) != 1)) return false;
        var selection = rows.SingleOrDefault(row => row.Id == 10);
        var reward = rows.SingleOrDefault(row => row.Id == 11);
        if ((selection is null) != (reward is null) || category is "mogpendium_standard" or "mogpendium_ultimog" && selection is null) return false;
        var currency = rows.Single(row => row.Id == 4);
        if (reward is not null && reward.ReferenceId != currency.ReferenceId) return false;
        var secondary = rows.SingleOrDefault(row => row.Id == 5);
        var secondaryReward = rows.SingleOrDefault(row => row.Id == 12);
        if (secondaryReward is not null && (selection is null || secondary is null || secondary.ReferenceId != secondaryReward.ReferenceId)) return false;
        var catalog = rows.Where(row => row.Id >= 100).OrderBy(row => row.Id).ToArray();
        if (!catalog.Select(row => row.Id).SequenceEqual(Enumerable.Range(100, catalog.Length).Select(id => (uint)id))) return false;
        if (category is "mogpendium_weekly" or "mogpendium_minimog" && catalog.Length == 0) return selection is not null;
        return category switch
        {
            "mogpendium_standard" => catalog.Length is >= 1 and <= 200 && catalog.Any(row => row.ReferenceId == selection!.ReferenceId),
            "mogpendium_weekly" => catalog.Length is >= 1 and <= 16 && catalog.Select(row => row.Text).Distinct(StringComparer.Ordinal).Count() == catalog.Length,
            "mogpendium_minimog" => catalog.Length is >= 2 and <= 32 && catalog.Length % 2 == 0 &&
                catalog.Where((row, index) => row.Slot != index % 2 + 1 || row.Text != catalog[index / 2 * 2].Text).Count() == 0 &&
                catalog.Where((_, index) => index % 2 == 0).Select(row => row.Text).Distinct(StringComparer.Ordinal).Count() == catalog.Length / 2,
            "mogpendium_ultimog" => catalog.Length == 0,
            _ => false,
        };
    }
}
