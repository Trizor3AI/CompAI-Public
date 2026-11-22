using System.Linq;

namespace CompAI;

internal static class FieldRecordContract
{
    internal const string Category = "field_record";
    internal const int RecordCount = 50;
    internal const string Registered = "Registered in this observed selection.";
    internal const string Unavailable = "No record available in this observed selection.";
    internal const string Source = "Visible owned MYCWarResultNotebook selected-entry labels and total; localized MYCWarResultNotebook public Number/name definitions";
    internal const string Coverage = "Selected Field Record displayed number, displayed name or ???, displayed registration status and displayed registered total only. English display formatting is currently supported. Public record numbers corroborate titles; native selection indices are not sheet IDs. This is not a complete owned or missing list. Hidden names, lore, rarity and other entries are not copied.";

    internal static bool ValidEntry(ActivityEntry row) => row.State == "observed" && row.ReferenceId is null &&
        row.ReferenceKind is null && row.Slot is null && (row.Id switch
        {
            1 => row.Group == "selection" && row.Name == "Selected record number" && row.Value is >= 1 and <= RecordCount &&
                row.Unit == "record" && row.Required is null && row.Text is null,
            2 => row.Group == "selection" && row.Name == "Displayed record name" && ActivitySnapshot.ValidText(row.Text, 128) &&
                row.Value is null && row.Required is null && row.Unit is null,
            3 => row.Group == "selection" && row.Name == "Displayed registration" && row.Text is Registered or Unavailable &&
                row.Value is null && row.Required is null && row.Unit is null,
            4 => row.Group == "collection" && row.Name == "Displayed registered total" && row.Value is >= 0 and <= RecordCount &&
                row.Required == RecordCount && row.Unit == "records" && row.Text is null,
            _ => false,
        });

    internal static bool ValidShape(ActivityEntry[] rows)
    {
        if (rows.Length != 4 || rows.Any(row => row is null || !ValidEntry(row)) ||
            rows.Select(row => row.Id).Distinct().Count() != 4) return false;
        var name = rows.Single(row => row.Id == 2).Text;
        var registered = rows.Single(row => row.Id == 3).Text == Registered;
        var total = rows.Single(row => row.Id == 4).Value;
        return registered ? name != "???" && total > 0 : name == "???" && total < RecordCount;
    }
}
