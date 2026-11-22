using System.Linq;

namespace CompAI;

internal static class PortableArchiveContract
{
    internal const string Category = "portable_archive";
    internal const string Source = "Visible Description-agent-owned DescriptionYTC selected entry; local DescriptionSection 5 and DescriptionPage/DescriptionString definitions";
    internal const string Coverage = "Selected Portable Archive title and public page/subrow references only. The full visible title uniquely matches a local public definition; body text is not read or supplied. English display formatting is currently supported. Identifying a selected title does not establish its registration or unlock state. This is not a complete owned or missing list; other entries, unread markers and completion are unknown.";

    internal static bool ValidPage(uint? page, long? subrow) => page switch
    {
        12 or 22 => subrow is >= 0 and < 11,
        37 => subrow is >= 0 and < 6,
        _ => false,
    };

    internal static bool ValidEntry(ActivityEntry row) => row.State == "observed" && row.Group == "selection" &&
        row.Required is null && row.Slot is null && (row.Id switch
        {
            1 => row.Name == "Selected archive entry" && ActivitySnapshot.ValidText(row.Text, 128) &&
                row.Value is null && row.Unit is null && row.ReferenceId is >= 1 and <= ushort.MaxValue &&
                row.ReferenceKind == "description_string",
            2 => row.Name == "Selected archive page" && row.Text is null && row.Unit == "subrow" &&
                row.ReferenceKind == "description_page" && ValidPage(row.ReferenceId, row.Value),
            _ => false,
        });

    internal static bool ValidShape(ActivityEntry[] rows) => rows.Length == 2 &&
        rows.All(row => row is not null && ValidEntry(row)) && rows.Select(row => row.Id).Distinct().Count() == 2;
}
