using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Dalamud.Plugin.Services;
using CompAI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

var libraryPath = Environment.GetEnvironmentVariable("DALAMUD_LIB_PATH") ??
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "XIVLauncher", "addon", "Hooks", "dev");
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var path = Path.Combine(libraryPath, name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
Tests.Run();
LookupTests.Run();
CostTests.Run();

internal static class Tests
{
    private static int passed;

    internal static void Run()
    {
        Test("logged-out capture stops at readiness and marks every section unavailable", () =>
        {
            var client = DispatchProxy.Create<IClientState, ReadinessProxy>();
            var proxy = (ReadinessProxy)(object)client;
            var reader = new CharacterDetailsReader(client, null!, null!, null!, null!);
            var reading = reader.Capture(new(CharacterDetailsSection.Jobs), reader.CreateMetadataCache());
            Check(proxy.Calls.SequenceEqual(new[] { "get_IsLoggedIn" }), "only login readiness inspected");
            Check(reading.Jobs is null && reading.Equipped is null && reading.Gearsets is null, "no false empty results");
            Check(reading.JobsUnavailable is not null && reading.EquippedUnavailable is not null &&
                  reading.GearsetsUnavailable is not null, "unavailable for all sections");
        });
        Test("materia keeps gaps, zero-based grades, and copied scalar values", () =>
        {
            ushort[] ids = [0, 27, 0, 28, 0];
            byte[] grades = [0, 9, 0, 11, 0];
            var calls = new List<(uint, int)>();
            var values = CharacterDetailsReader.CopyMateria(ids, grades, (id, grade) =>
            {
                calls.Add((id, grade));
                return (id + 100, $"Materia {id}/{grade}");
            });
            ids[1] = 99;
            grades[1] = 0;
            Check(values.Length == 5 && values[0].IsEmpty && values[2].IsEmpty, "empty positions retained");
            Check(values[1] == new CharacterMateriaContext(1, 27, 9, 127, "Materia 27/9", false), "frozen source values");
            Check(calls.SequenceEqual(new[] { (27u, 9), (28u, 11) }), "empty slots never resolved");
        });
        Test("unknown materia keeps identity without an invented name", () =>
        {
            var values = CharacterDetailsReader.CopyMateria([999, 0, 0, 0, 0], [255, 0, 0, 0, 0],
                (_, _) => (null, null));
            Check(values[0].MateriaId == 999 && values[0].Grade == 255 && !values[0].IsEmpty, "unknown raw values");
            Check(values[0].ItemId is null && values[0].Name is null, "no invented mapping");
        });
        Test("layout drift fails before any materia lookup", () =>
        {
            var calls = 0;
            Throws(() => CharacterDetailsReader.CopyMateria(new ushort[6], new byte[6], (_, _) =>
            { calls++; return (null, null); }));
            Throws(() => CharacterDetailsReader.CopyMateria(new ushort[5], new byte[4], (_, _) =>
            { calls++; return (null, null); }));
            Check(calls == 0, "no lookup outside fixed bounds");
        });
        Test("empty equipment has explicit slots and rejects out-of-bounds slots", () =>
        {
            for (var slot = 0; slot < 14; slot++)
            {
                var empty = CharacterDetailsReader.EmptyEquipment(slot);
                Check(empty.IsEmpty && empty.ItemId == 0 && empty.BaseItemId == 0 && !empty.IsHq, "canonical empty item");
                Check(empty.Materia.Length == 5 && empty.Materia.All(x => x.IsEmpty), "explicit empty materia");
                Check(empty.Slot == slot && empty.SlotName.Length > 0, "named position");
            }
            Throws(() => CharacterDetailsReader.EmptyEquipment(-1));
            Throws(() => CharacterDetailsReader.EmptyEquipment(14));
            Check(CharacterDetailsReader.EmptyEquipment(5).SlotName.Contains("unused"), "legacy belt is visible");
        });
        Test("gearset names are bounded and require termination", () =>
        {
            var bytes = new byte[48];
            Encoding.UTF8.GetBytes("Crafting α").CopyTo(bytes, 0);
            Check(CharacterDetailsReader.ReadGearsetName(bytes) == "Crafting α", "UTF-8 preserved");
            Array.Fill(bytes, (byte)'A');
            Check(CharacterDetailsReader.ReadGearsetName(bytes) == "[gearset name unavailable]", "no unbounded string scan");
            Throws(() => CharacterDetailsReader.ReadGearsetName(new byte[49]));
        });
        Test("installed saved-gearset layout agrees with bounded reader", () =>
        {
            var module = new RaptureGearsetModule();
            var entry = new RaptureGearsetModule.GearsetEntry();
            var item = new RaptureGearsetModule.GearsetItem();
            Check(module.Entries.Length == 100 && entry.Items.Length == 14 && entry.Name.Length == 48, "installed collection layout");
            Check(item.Materia.Length == 5 && item.MateriaGrades.Length == 5, "installed materia layout");
            Check((int)RaptureGearsetModule.GearsetItemIndex.Belt == 5 &&
                  (int)RaptureGearsetModule.GearsetItemIndex.SoulStone == 13, "installed slot order");
        });
        Console.WriteLine($"Passed {passed} character-details checks.");
    }

    private static void Test(string name, Action action) { action(); passed++; Console.WriteLine($"PASS {name}"); }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        catch (ArgumentOutOfRangeException) { return; }
        throw new InvalidOperationException("Expected the invalid bounds to be rejected.");
    }
}

public class ReadinessProxy : DispatchProxy
{
    public readonly List<string> Calls = [];
    protected override object? Invoke(MethodInfo? method, object?[]? arguments)
    {
        Calls.Add(method!.Name);
        if (method.Name == "get_IsLoggedIn") return false;
        throw new InvalidOperationException("Unexpected service read in logged-out fixture.");
    }
}
