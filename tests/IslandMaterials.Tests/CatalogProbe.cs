using System.Reflection;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

internal static class CatalogProbe
{
    internal static void Run(string path)
    {
        foreach (var type in new[] { typeof(AgentMJIPouch), typeof(AgentMJIPouch.PouchInventoryData), typeof(AgentMJIPouch.PouchInventoryItem) })
            Console.WriteLine("NATIVE " + type.FullName + " " + string.Join("; ", type.GetFields(BindingFlags.Instance | BindingFlags.Public)
                .Select(field => field.Name + ":" + field.FieldType.Name + "@" + Marshal.OffsetOf(type, field.Name))));
        Console.WriteLine("SCHEMA " + string.Join("; ", typeof(MJIItemPouch).GetProperties().Select(p => p.PropertyType.Name + " " + p.Name)));
        using var game = new Lumina.GameData(path);
        var sheet = game.Excel.GetSheet<MJIItemPouch>();
        Console.WriteLine("MJIItemPouch rows=" + sheet.Count);
        Console.WriteLine("CATEGORY SCHEMA " + string.Join("; ", typeof(MJIItemCategory).GetProperties().Select(p => p.PropertyType.Name + " " + p.Name)));
        foreach (var group in sheet.GroupBy(row => row.Category.RowId))
            Console.WriteLine("CATEGORY " + group.Key + " " + group.First().Category.Value.Plural.ExtractText() +
                " count=" + group.Count() + " example=" + group.First().Item.Value.Name.ExtractText());
        foreach (var row in sheet.Take(4).Concat(sheet.Skip(Math.Max(0, sheet.Count - 4))))
            Console.WriteLine($"ROW {row.RowId} item={row.Item.RowId} category={row.Category.RowId} name={row.Item.Value.Name.ExtractText()} icon={row.Item.Value.Icon} max={row.Item.Value.StackSize}");
        Console.WriteLine("Unique ItemIds=" + sheet.Select(row => row.Item.RowId).Distinct().Count() +
            " emptyItemRows=" + sheet.Count(row => row.Item.RowId == 0) +
            " invalidItemRows=" + sheet.Count(row => !row.Item.IsValid) +
            " caps=" + string.Join(',', sheet.Select(row => row.Item.Value.StackSize).Distinct()));
    }
}
