using System.Linq;

namespace CompAI;

/// <summary>Six displayed player-statistic fields; no inferred rank progress or NPC counters.</summary>
internal static class MahjongContract
{
    internal const string Category = "mahjong";
    internal const int MaxTextLength = 64;
    internal const string Source = "Visible GoldSaucerInfo Mahjong tab and its uniquely owned GSInfoEmj panel; typed AddonGSInfoEmj statistic text fields";
    internal const string Coverage = "Displayed player rank, points, next-rank text, matches played, current rating and highest rating. Values retain the game's localized display text. Next-rank text is not a verified progress denominator or points-remaining calculation. NPC table wins, yakuman history, settings, competitive rankings and reward claims are excluded. Missing fields are unknown, never zero.";

    internal static string? Name(uint id) => id switch
    {
        1 => "Rank",
        2 => "Points (displayed)",
        3 => "Next rank (displayed)",
        4 => "Matches played",
        5 => "Current rating",
        6 => "Highest rating",
        _ => null,
    };

    internal static bool ValidEntry(ActivityEntry? row) => row is not null && row.Id is >= 1 and <= 6 &&
        row.Group == "statistics" && row.Name == Name(row.Id) && row.State == "observed" &&
        ActivitySnapshot.ValidText(row.Text, MaxTextLength) && row.Value is null && row.Required is null &&
        row.Unit is null && row.ReferenceId is null && row.ReferenceKind is null && row.Slot is null;

    internal static bool ValidShape(ActivityEntry[] rows) => rows is not null && rows.Length == 6 &&
        rows.All(ValidEntry) && rows.Select(row => row.Id).Distinct().Count() == 6;
}
