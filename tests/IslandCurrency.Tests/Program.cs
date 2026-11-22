using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using CompAI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

var library = Environment.GetEnvironmentVariable("DALAMUD_LIB_PATH") ??
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "addon", "Hooks", "dev");
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var path = Path.Combine(library, name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
Tests.Run();

internal static unsafe class Tests
{
    private static int passed;
    private static readonly IslandCurrencyOwner Owner = new(1, 100, 2, 3, 4, 5, 6, 7);
    private static readonly IslandCurrencySlot Seafarer = new(37549, 1001, 1234, 999999);
    private static readonly IslandCurrencySlot Islander = new(37550, 1002, 50, 999999);
    private static readonly IslandCurrencySample Sample = new(3, true, Seafarer, Islander);
    private static IslandCurrencyDefinition? Definition(uint id) => id switch
    {
        37549 => new("Synthetic Seafarer's Cowrie", 1001),
        37550 => new("Synthetic Islander's Cowrie", 1002),
        _ => null,
    };
    private static IslandCurrencyObservation? Capture(IslandCurrencySample sample,
        Func<uint, IslandCurrencyDefinition?>? definition = null) =>
        IslandCurrencyReader.CaptureStable(() => Owner, _ => sample, definition ?? Definition, () => true);

    internal static void Run()
    {
        Test("complete named pair is ordered by item identity and timestamped", () =>
        {
            var before = DateTimeOffset.UtcNow;
            var observation = Capture(Sample with { First = Islander, Second = Seafarer });
            Check(observation is not null && observation.CapturedAtUtc >= before && observation.CapturedAtUtc <= DateTimeOffset.UtcNow);
            Check(observation!.Seafarer.ItemId == 37549 && observation.Seafarer.Quantity == 1234);
            Check(observation.Islander.ItemId == 37550 && observation.Islander.Quantity == 50);
        });
        Test("zero is accepted only in a complete ready pair", () =>
        {
            var observation = Capture(Sample with { First = Seafarer with { Quantity = 0 }, Second = Islander with { Quantity = 0 } });
            Check(observation?.Seafarer.Quantity == 0 && observation.Islander.Quantity == 0);
            Check(Capture(Sample with { InitializationState = 0 }) is null);
            Check(Capture(Sample with { InitializationState = 1 }) is null);
            Check(Capture(Sample with { InitializationState = 2 }) is null);
            Check(Capture(Sample with { InitializationState = 4 }) is null);
            Check(Capture(Sample with { DataInitialized = false }) is null);
        });
        Test("only the exact two supported IDs are accepted", () =>
        {
            foreach (var id in new uint[] { 0, 1, 31135, 37551, 37550, uint.MaxValue })
                Check(Capture(Sample with { First = Seafarer with { ItemId = id } }) is null);
            Check(Capture(Sample with { Second = Seafarer }) is null);
        });
        Test("negative quantities invalid limits and over-cap values fail closed", () =>
        {
            foreach (var bad in new[] { Seafarer with { Quantity = -1 }, Seafarer with { Maximum = 0 },
                         Seafarer with { Maximum = -1 }, Seafarer with { Quantity = Seafarer.Maximum!.Value + 1 },
                         Seafarer with { Icon = 0 } })
                Check(Capture(Sample with { First = bad }) is null);
            Check(Capture(Sample with { Second = Islander with { Quantity = -1 } }) is null);
            Check(Capture(Sample with { First = Seafarer with { Quantity = Seafarer.Maximum!.Value } }) is not null);
        });
        Test("local definitions must identify both icons and bounded valid names", () =>
        {
            foreach (var bad in new IslandCurrencyDefinition?[] { null, new("", 1001), new(" \t", 1001),
                         new("bad\0name", 1001), new(new string('x', 129), 1001), new("Synthetic", 9999) })
                Check(Capture(Sample, id => id == 37549 ? bad : Definition(id)) is null);
            Check(Capture(Sample, id => id == 37550 ? null : Definition(id)) is null);
            Check(Capture(Sample, id => id == 37549 ? new("  Synthetic Cowrie  ", 1001) : Definition(id))?.Seafarer.Name == "Synthetic Cowrie");
        });
        Test("unavailable or malformed owner prevents any native copy", () =>
        {
            var copies = 0;
            IslandCurrencySample? Copy(IslandCurrencyOwner _) { copies++; return Sample; }
            Check(IslandCurrencyReader.CaptureStable(() => null, Copy, Definition, () => true) is null);
            foreach (var bad in new[] { Owner with { Player = 0 }, Owner with { ContentId = 0 }, Owner with { Module = 0 },
                         Owner with { Manager = 0 }, Owner with { Agent = 0 }, Owner with { Addon = 0 },
                         Owner with { Data = 0 }, Owner with { AddonId = 0 }, Owner with { AddonId = 65536 } })
                Check(IslandCurrencyReader.CaptureStable(() => bad, Copy, Definition, () => true) is null);
            Check(copies == 0);
        });
        Test("catalog load cannot leave an old address available for first copy", () =>
        {
            var copies = 0;
            Check(IslandCurrencyReader.CaptureStable(() => Owner with { Data = 8 },
                _ => { copies++; return Sample; }, Definition, () => true, Owner) is null);
            Check(copies == 0);
            Check(IslandCurrencyReader.CaptureStable(() => Owner,
                _ => { copies++; return Sample; }, Definition, () => false, Owner) is null);
            Check(copies == 0);
        });
        Test("all ownership components are rechecked before second copy", () =>
        {
            foreach (var changed in new[] { Owner with { Player = 11 }, Owner with { ContentId = 101 },
                         Owner with { Module = 12 }, Owner with { Manager = 13 }, Owner with { Agent = 14 },
                         Owner with { Addon = 15 }, Owner with { AddonId = 16 }, Owner with { Data = 17 } })
            {
                var current = Owner;
                var copies = 0;
                IslandCurrencyDefinition? Change(uint id) { current = changed; return Definition(id); }
                Check(IslandCurrencyReader.CaptureStable(() => current, _ => { copies++; return Sample; }, Change, () => true) is null);
                Check(copies == 1);
            }
        });
        Test("owner disappearance after either copy rejects the result", () =>
        {
            foreach (var failedCopy in new[] { 1, 2 })
            {
                var copies = 0;
                Check(IslandCurrencyReader.CaptureStable(() => copies >= failedCopy ? null : Owner,
                    _ => { copies++; return Sample; }, Definition, () => true) is null);
                Check(copies == failedCopy);
            }
        });
        Test("every sampled currency field must remain stable", () =>
        {
            foreach (var changed in new[] { Sample with { InitializationState = 1 }, Sample with { DataInitialized = false },
                         Sample with { First = Seafarer with { Quantity = 1235 } }, Sample with { First = Seafarer with { Maximum = 999998 } },
                         Sample with { First = Seafarer with { Icon = 1003 } }, Sample with { First = Seafarer with { ItemId = 37550 } },
                         Sample with { Second = Islander with { Quantity = 49 } } })
            {
                var copies = 0;
                Check(IslandCurrencyReader.CaptureStable(() => Owner,
                    _ => ++copies == 1 ? Sample : changed, Definition, () => true) is null);
                Check(copies == 2);
            }
            var failed = 0;
            Check(IslandCurrencyReader.CaptureStable(() => Owner, _ => ++failed == 1 ? Sample : null, Definition, () => true) is null);
        });
        Test("definition or catalog changes discard the detached sample", () =>
        {
            var calls = 0;
            Check(IslandCurrencyReader.CaptureStable(() => Owner, _ => Sample,
                id => ++calls > 2 ? new("Changed", Definition(id)!.Value.Icon) : Definition(id), () => true) is null);
            var checks = 0;
            Check(IslandCurrencyReader.CaptureStable(() => Owner, _ => Sample, Definition, () => ++checks < 3) is null);
        });
        Test("final definition checks cannot outlive the menu owner", () =>
        {
            var current = Owner;
            var calls = 0;
            IslandCurrencyDefinition? Change(uint id)
            {
                if (++calls > 2) current = Owner with { ContentId = 101 };
                return Definition(id);
            }
            Check(IslandCurrencyReader.CaptureStable(() => current, _ => Sample, Change, () => true) is null);
        });
        Test("exact ready visible owned addon is mandatory", () =>
        {
            Check(IslandCurrencyReader.MatchesAddon(10, 20, "MJIDisposeShop", 10, 20, true, true));
            foreach (var name in new[] { "", "Shop", "MJIPouch", "MJIDisposeShopShipping" })
                Check(!IslandCurrencyReader.MatchesAddon(10, 20, name, 10, 20, true, true));
            Check(!IslandCurrencyReader.MatchesAddon(10, 20, "MJIDisposeShop", 11, 20, true, true));
            Check(!IslandCurrencyReader.MatchesAddon(10, 20, "MJIDisposeShop", 10, 21, true, true));
            Check(!IslandCurrencyReader.MatchesAddon(10, 20, "MJIDisposeShop", 10, 20, false, true));
            Check(!IslandCurrencyReader.MatchesAddon(10, 20, "MJIDisposeShop", 10, 20, true, false));
            Check(!IslandCurrencyReader.MatchesAddon(0, 20, "MJIDisposeShop", 0, 20, true, true));
        });
        Test("native copier reads only the fixed pair and ignores unrelated dirty state", () =>
        {
            using var fixture = new Fixture();
            fixture.Data->AddonDirty = true;
            Check(IslandCurrencyReader.Copy(fixture.Owner) == Sample);
            var observation = IslandCurrencyReader.CaptureStable(() => fixture.Owner, IslandCurrencyReader.Copy, Definition, () => true);
            Check(observation?.Seafarer.Quantity == 1234 && observation.Islander.Quantity == 50);
            fixture.Data->CurrencyCounts[0] = 99;
            Check(observation!.Seafarer.Quantity == 1234);
            // All resource vectors and strings remain zero/uninitialized throughout.
        });
        Test("native copier rejects stale data binding and unfinished initialization", () =>
        {
            using var fixture = new Fixture();
            Check(IslandCurrencyReader.Copy(fixture.Owner with { Data = 1 }) is null);
            Check(IslandCurrencyReader.Copy(fixture.Owner with { AddonId = 999 }) is null);
            fixture.Data->InitializationState = 1;
            Check(IslandCurrencyReader.Copy(fixture.Owner) is null);
            fixture.Data->InitializationState = 3;
            fixture.Data->DataInitialized = false;
            Check(IslandCurrencyReader.Copy(fixture.Owner) is null);
            fixture.Agent->Data = null;
            Check(IslandCurrencyReader.Copy(fixture.Owner) is null);
        });
        Test("Index reads the documented balance slots without upkeep or other UI values", () =>
        {
            using var fixture = new IndexFixture();
            var first = IslandCurrencyReader.Copy(fixture.Owner);
            Check(first is { FromIndex: true, First.Quantity: 143857, Second.Quantity: 109584 });
            var sample = first ?? throw new InvalidOperationException("Expected an Index sample.");
            Check(sample.First.Maximum is null && sample.Second.Maximum is null && sample.First.Icon is null);
            var observation = IslandCurrencyReader.CaptureStable(() => fixture.Owner, IslandCurrencyReader.Copy, Definition, () => true);
            Check(observation?.Seafarer.Quantity == 143857 && observation.Islander.Quantity == 109584);
            for (var index = 0; index < fixture.Owner.ValueCount; index++)
                if (index is not 14 and not 17) fixture.Values[index].Int = -999;
            Check(IslandCurrencyReader.Copy(fixture.Owner) == first);
            fixture.Values[17].UInt = 1;
            Check(observation!.Seafarer.Quantity == 143857);
        });
        Test("Index accepts nonnegative integral types and rejects other unions or overflows", () =>
        {
            using var fixture = new IndexFixture();
            fixture.Values[14].Type = AtkValueType.Int;
            fixture.Values[14].Int = 0;
            fixture.Values[17].UInt = int.MaxValue;
            var result = IslandCurrencyReader.CaptureStable(() => fixture.Owner, IslandCurrencyReader.Copy, Definition, () => true);
            Check(result?.Islander.Quantity == 0 && result.Seafarer.Quantity == int.MaxValue);
            fixture.Values[14].Int = -1;
            Check(IslandCurrencyReader.Copy(fixture.Owner) is null);
            fixture.Values[14].Int = 1;
            fixture.Values[17].UInt = (uint)int.MaxValue + 1;
            Check(IslandCurrencyReader.Copy(fixture.Owner) is null);
            fixture.Values[17].UInt = 1;
            fixture.Values[14].Type = AtkValueType.String;
            Check(IslandCurrencyReader.Copy(fixture.Owner) is null);
        });
        Test("Index count pointer and addon identities are checked before copying values", () =>
        {
            using var fixture = new IndexFixture();
            foreach (var count in new[] { 0, 17, 257 })
                Check(IslandCurrencyReader.Copy(fixture.Owner with { ValueCount = count }) is null);
            Check(IslandCurrencyReader.Copy(fixture.Owner with { Data = 1 }) is null);
            Check(IslandCurrencyReader.Copy(fixture.Owner with { AddonId = 999 }) is null);
            fixture.Addon->AtkValuesCount = 31;
            Check(IslandCurrencyReader.Copy(fixture.Owner) is null);
            fixture.Addon->AtkValuesCount = 32;
            fixture.Addon->AtkValues = null;
            Check(IslandCurrencyReader.Copy(fixture.Owner) is null);
        });
        Test("Index native type and source binding must be stable across the pair", () =>
        {
            using var fixture = new IndexFixture();
            var copies = 0;
            IslandCurrencySample? Read(IslandCurrencyOwner owner)
            {
                if (++copies == 2) fixture.Values[14].Type = AtkValueType.Int;
                return IslandCurrencyReader.Copy(owner);
            }
            Check(IslandCurrencyReader.CaptureStable(() => fixture.Owner, Read, Definition, () => true) is null);
            Check(copies == 2);
            Check(IslandCurrencyReader.CaptureStable(() => fixture.Owner, _ => Sample, Definition, () => true) is null);
            var indexSample = IslandCurrencyReader.Copy(fixture.Owner);
            Check(IslandCurrencyReader.CaptureStable(() => Owner, _ => indexSample, Definition, () => true) is null);
        });
        Test("Index owner metadata changes cannot reach a second native copy", () =>
        {
            using var fixture = new IndexFixture();
            foreach (var changed in new[] { fixture.Owner with { ValueCount = 31 }, fixture.Owner with { Data = 1 },
                         fixture.Owner with { FromIndex = false }, fixture.Owner with { Addon = 1 } })
            {
                var current = fixture.Owner;
                var copies = 0;
                IslandCurrencyDefinition? Change(uint id) { current = changed; return Definition(id); }
                Check(IslandCurrencyReader.CaptureStable(() => current,
                    owner => { copies++; return IslandCurrencyReader.Copy(owner); }, Change, () => true) is null);
                Check(copies == 1);
            }
        });
        Test("Index exact visible addon binding and valid public names remain required", () =>
        {
            Check(IslandCurrencyReader.MatchesAddon(10, 20, "MJIHud", 10, 20, true, true, fromIndex: true));
            Check(!IslandCurrencyReader.MatchesAddon(10, 20, "MJIDisposeShop", 10, 20, true, true, fromIndex: true));
            Check(!IslandCurrencyReader.MatchesAddon(10, 20, "MJIHud", 10, 20, true, false, fromIndex: true));
            Check(!IslandCurrencyReader.MatchesAddon(10, 20, "MJIHud", 10, 20, false, true, fromIndex: true));
            using var fixture = new IndexFixture();
            Check(IslandCurrencyReader.CaptureStable(() => fixture.Owner, IslandCurrencyReader.Copy,
                id => id == 37549 ? null : Definition(id), () => true) is null);
            Check(IslandCurrencyReader.CaptureStable(() => fixture.Owner, IslandCurrencyReader.Copy,
                id => id == 37549 ? new("Synthetic", 0) : Definition(id), () => true) is null);
        });
        Test("returned observation contains only timestamp and immutable public balance records", () =>
        {
            var observation = Capture(Sample)!;
            var replacement = observation.Seafarer with { Quantity = 9 };
            Check(observation.Seafarer.Quantity == 1234 && replacement.Quantity == 9);
            var json = JsonSerializer.Serialize(observation);
            Check(!json.Contains("ContentId") && !json.Contains("Manager") && !json.Contains("Addon") &&
                !json.Contains("Maximum") && !json.Contains("Icon") && json.Contains("CapturedAtUtc"));
        });
        Console.WriteLine($"{passed} Island currency groups passed; synthetic only, live Index/export menus still require comparison.");
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly AgentMJIDisposeShop* Agent;
        internal readonly AgentMJIDisposeShop.AgentData* Data;
        internal readonly IslandCurrencyOwner Owner;
        internal Fixture()
        {
            Agent = (AgentMJIDisposeShop*)NativeMemory.AllocZeroed((nuint)sizeof(AgentMJIDisposeShop));
            Data = (AgentMJIDisposeShop.AgentData*)NativeMemory.AllocZeroed((nuint)sizeof(AgentMJIDisposeShop.AgentData));
            Agent->AddonId = 6;
            Agent->Data = Data;
            Data->InitializationState = 3;
            Data->DataInitialized = true;
            Write(0, Seafarer); Write(1, Islander);
            Owner = Tests.Owner with { Agent = (nint)Agent, Data = (nint)Data };
        }
        private void Write(int index, IslandCurrencySlot slot)
        {
            Data->CurrencyItemIds[index] = slot.ItemId; Data->CurrencyIconIds[index] = slot.Icon!.Value;
            Data->CurrencyCounts[index] = slot.Quantity; Data->CurrencyStackSizes[index] = slot.Maximum!.Value;
        }
        public void Dispose() { NativeMemory.Free(Data); NativeMemory.Free(Agent); }
    }
    private sealed class IndexFixture : IDisposable
    {
        internal readonly AgentInterface* Agent;
        internal readonly AtkUnitBase* Addon;
        internal readonly AtkValue* Values;
        internal readonly IslandCurrencyOwner Owner;
        internal IndexFixture()
        {
            Agent = (AgentInterface*)NativeMemory.AllocZeroed((nuint)sizeof(AgentInterface));
            Addon = (AtkUnitBase*)NativeMemory.AllocZeroed((nuint)sizeof(AtkUnitBase));
            Values = (AtkValue*)NativeMemory.AllocZeroed(32, (nuint)sizeof(AtkValue));
            Agent->AddonId = 6;
            Addon->Id = 6;
            Addon->AtkValues = Values;
            Addon->AtkValuesCount = 32;
            for (var index = 0; index < 32; index++)
            { Values[index].Type = AtkValueType.Int; Values[index].Int = -300; }
            Values[14].Type = AtkValueType.UInt; Values[14].UInt = 109584;
            Values[17].Type = AtkValueType.UInt; Values[17].UInt = 143857;
            Owner = Tests.Owner with { Agent = (nint)Agent, Addon = (nint)Addon, Data = (nint)Values, FromIndex = true, ValueCount = 32 };
        }
        public void Dispose() { NativeMemory.Free(Values); NativeMemory.Free(Addon); NativeMemory.Free(Agent); }
    }
    private static void Check(bool result) { if (!result) throw new InvalidOperationException("Island currency assertion failed."); }
    private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
}
