using Lumina.Excel.Sheets;

internal static class CatalogProbe
{
    internal static void Run(string path)
    {
        var properties = typeof(MJIRank).GetProperties();
        Console.WriteLine("MJIRank schema: " + string.Join("; ", properties.Select(p => p.PropertyType.Name + " " + p.Name)));
        using var game = new Lumina.GameData(path);
        var sheet = game.Excel.GetSheet<MJIRank>();
        foreach (var row in sheet)
            Console.WriteLine("MJIRank " + row.RowId + ": " + string.Join("; ", properties.Where(p => p.Name != "RowId").Select(p => p.Name + "=" + p.GetValue(row))));
    }
}
