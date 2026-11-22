using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using CompAI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

if (args is ["--public-catalog", var path]) { CatalogProbe.Run(path); return; }
if (args.Length != 0) throw new ArgumentException("Expected no arguments, or --public-catalog <local sqpack directory>.");
Tests.Run();

internal static unsafe class Tests
{
    private static int groups;
    private static readonly int Stride = sizeof(AgentMJIPouch.PouchInventoryItem);
    private static readonly IslandMaterialSample[] Samples = [new(0, 37551, 25026, 0, 999, 0, 1, false), new(1, 37552, 25308, 42, 999, 0, 1, false)];
    private static readonly IslandMaterialSample Token = new(108, 41668, 65104, 150, 150, 255, 10, false);
    private static readonly IslandMaterialsOwner Owner = new(1, 42, 2, 3, 4, 5, 6, 7, 4096, 4096 + 2 * Stride, 4096 + 2 * Stride);
    private static IslandMaterialDefinition? Definition(uint id) => id switch
    {
        0 => new(0, 37551, "Island Palm Leaf", 25026, 999, 1, "Materials"),
        1 => new(1, 37552, "Island Apple", 25308, 999, 1, "Materials"),
        108 => new(108, 41668, "Felicitous Token", 65104, 150, 10, ""),
        _ => null,
    };
    private static IslandMaterialsObservation? Capture(IslandMaterialSample[]? samples = null,
        Func<IslandMaterialsOwner?>? observe = null, Func<IslandMaterialsOwner, IslandMaterialSample[]?>? copy = null,
        Func<uint, IslandMaterialDefinition?>? definition = null, Func<bool>? catalog = null) =>
        IslandMaterialsReader.CaptureStable(observe ?? (() => Owner), copy ?? (_ => (samples ?? Samples).ToArray()),
            definition ?? Definition, catalog ?? (() => true));

    internal static void Run()
    {
        Test("row zero is a real resource and explicit zero quantity is preserved", () =>
        {
            var before = DateTimeOffset.UtcNow;
            var result = Capture();
            Check(result is not null && result.Items.Count == 2 && result.CapturedAtUtc >= before);
            Check(result!.Items[0] is { PouchRowId: 0, ItemId: 37551, Quantity: 0, CategoryId: 1, CategoryName: "Materials" });
            Check(result.Items[1].Quantity == 42);
        });
        Test("only supported public resource categories are accepted", () =>
        {
            foreach (var category in new uint[] { 1, 3, 4, 5, 6, 7 }) Check(IslandMaterialsReader.SupportedCategory(category));
            foreach (var category in new uint[] { 0, 2, 8, 9, 10, 255 }) Check(!IslandMaterialsReader.SupportedCategory(category));
        });
        Test("undiscovered rows never resolve or reveal their names or counts", () =>
        {
            var samples = Samples.ToArray(); samples[0] = samples[0] with { IsUndiscovered = true, Quantity = -1 };
            var result = Capture(samples, definition: id => id == 0 ? throw new Exception("Hidden lookup") : Definition(id));
            Check(result?.Items.Count == 1 && result.Items[0].ItemId == 37552);
        });
        Test("key tools arbitrary tokens and unknown rows are omitted", () =>
        {
            var samples = Samples.ToArray(); samples[0] = samples[0] with { InventoryIndex = 3 };
            Check(Capture(samples)?.Items.Count == 1);
            samples[0] = Samples[0] with { ItemId = 99999 }; // key row ID overlaps resource row ID
            Check(Capture(samples)?.Items.Count == 1);
            Check(Capture(definition: id => id == 0 ? Definition(id)!.Value with { CategoryId = 10 } : Definition(id))?.Items.Count == 1);
            samples[0] = Samples[0] with { PouchRowId = 999 };
            Check(Capture(samples)?.Items.Count == 1);
        });
        Test("exact Felicitous Token uses native quantity and a documented display label", () =>
        {
            foreach (var quantity in new[] { 0, 42, 150 })
            {
                var result = Capture([Token with { Quantity = quantity }, Samples[1]]);
                Check(result?.Items.Count == 2 && result.Items[1] is { PouchRowId: 108, ItemId: 41668,
                    CategoryId: 10, CategoryName: "Tokens", Name: "Felicitous Token" } && result.Items[1].Quantity == quantity);
            }
            // This field's token mapping is deliberately not assumed or inferred.
            for (var index = 0; index <= byte.MaxValue; index++)
                Check(Capture([Token with { InventoryIndex = (byte)index }, Samples[1]])?.Items.Count == 2);
        });
        Test("token exception cannot admit another item category row or cap", () =>
        {
            var original = Definition(108)!.Value;
            foreach (var changed in new[] { original with { PouchRowId = 107 }, original with { ItemId = 41669 },
                original with { CategoryId = 9 }, original with { Maximum = 999 } })
            {
                var row = Token with { PouchRowId = changed.PouchRowId, ItemId = changed.ItemId,
                    CategoryId = (byte)changed.CategoryId, Maximum = changed.Maximum };
                Check(Capture([row, Samples[1]], definition: id => id == row.PouchRowId ? changed : Definition(id))?.Items.Count == 1);
            }
            var hidden = Token with { IsUndiscovered = true, Quantity = -1 };
            Check(Capture([hidden, Samples[1]], definition: id => id == 108 ? throw new Exception("Hidden token lookup") : Definition(id))?.Items.Count == 1);
            Check(Capture([Token with { InventoryIndex = 3, ItemId = 99999 }, Samples[1]],
                definition: id => id == 108 ? throw new Exception("Key item lookup") : Definition(id))?.Items.Count == 1);
        });
        Test("token quantity metadata and public text remain strictly corroborated", () =>
        {
            foreach (var changed in new[] { Token with { Quantity = -1 }, Token with { Quantity = 151 },
                Token with { Maximum = 999 }, Token with { Icon = 0 }, Token with { Icon = 65105 }, Token with { CategoryId = 9 } })
                Check(Capture([changed, Samples[1]]) is null);
            var original = Definition(108)!.Value;
            foreach (var changed in new[] { original with { Name = "" }, original with { Name = "bad\nname" },
                original with { CategoryName = " " }, original with { CategoryName = "bad\ncategory" }, original with { Icon = 0 } })
                Check(Capture([Token, Samples[1]], definition: id => id == 108 ? changed : Definition(id)) is null);
            Check(Capture([Token, Token]) is null);
        });
        Test("token definitions grouping and quantities must remain stable", () =>
        {
            foreach (var changed in new[] { Token with { Quantity = 149 }, Token with { InventoryIndex = 3 }, Token with { IsUndiscovered = true } })
            {
                var copies = 0;
                Check(Capture(copy: _ => ++copies == 1 ? [Token, Samples[1]] : [changed, Samples[1]]) is null);
            }
            foreach (var changed in new[] { Definition(108)!.Value with { Name = "Changed" },
                Definition(108)!.Value with { CategoryName = "Tokens" }, Definition(108)!.Value with { Maximum = 149 } })
            {
                var definitions = 0;
                Check(Capture([Token, Samples[1]], definition: id => id == 108 && ++definitions > 1 ? changed : Definition(id)) is null);
            }
        });
        Test("empty observed subset is distinct from an empty or unloaded source", () =>
        {
            var hidden = Samples.Select(row => row with { IsUndiscovered = true }).ToArray();
            Check(Capture(hidden)?.Items.Count == 0);
            Check(Capture(copy: _ => []) is null);
            Check(Capture(copy: _ => null) is null);
            Check(Capture(observe: () => Owner with { Last = Owner.First }) is null);
        });
        Test("identified rows reject negative over-cap and mismatched native metadata", () =>
        {
            foreach (var changed in new[] { Samples[0] with { Quantity = -1 }, Samples[0] with { Quantity = 1000 },
                Samples[0] with { Maximum = 0 }, Samples[0] with { Maximum = 998 }, Samples[0] with { Icon = 0 },
                Samples[0] with { Icon = 999 }, Samples[0] with { CategoryId = 3 }, Samples[0] with { InventoryIndex = 4 } })
                Check(Capture([changed, Samples[1]]) is null);
            Check(Capture([Samples[0] with { Quantity = 999 }, Samples[1]])?.Items[0].Quantity == 999);
        });
        Test("public metadata bounds and duplicate item identities fail closed", () =>
        {
            var original = Definition(0)!.Value;
            foreach (var changed in new[] { original with { Name = "" }, original with { Name = "bad\nname" },
                original with { Name = new string('x', 129) }, original with { CategoryName = "" },
                original with { CategoryName = new string('x', 257) }, original with { Icon = 0 },
                original with { Maximum = 1000 }, original with { PouchRowId = 1 } })
                Check(Capture(definition: id => id == 0 ? changed : Definition(id)) is null);
            Check(Capture([Samples[0], Samples[0]]) is null);
        });
        Test("native vector ranges are aligned ordered integral and bounded", () =>
        {
            Check(IslandMaterialsReader.ValidRange(4096, 4096 + Stride, 4096 + 512 * Stride, out var count) && count == 1);
            foreach (var range in new (nint First, nint Last, nint End)[] { (0, 0, 0), (-1, 10, 20), (4096, 4096, 4096),
                (4096, 4000, 5000), (4096, 4096 + Stride, 4096), (4097, 4097 + Stride, 4097 + Stride),
                (4096, 4097 + Stride, 4097 + Stride), (4096, 4096 + Stride, 4097 + Stride),
                (4096, 4096 + Stride, 4096 + 513 * Stride) })
                Check(!IslandMaterialsReader.ValidRange(range.First, range.Last, range.End, out _));
        });
        Test("invalid owners prevent native copy", () =>
        {
            var copies = 0;
            foreach (var owner in new IslandMaterialsOwner?[] { null, Owner with { Player = 0 }, Owner with { ContentId = 0 },
                Owner with { Module = 0 }, Owner with { Manager = 0 }, Owner with { Agent = 0 }, Owner with { Addon = 0 },
                Owner with { Data = 0 }, Owner with { AddonId = 0 }, Owner with { AddonId = 65536 }, Owner with { First = 0 } })
                Check(Capture(observe: () => owner, copy: _ => { copies++; return Samples; }) is null);
            Check(copies == 0);
        });
        Test("catalog loading cannot leave an old native address for first copy", () =>
        {
            var observations = 0; var copies = 0;
            Check(Capture(observe: () => ++observations == 1 ? Owner : null,
                copy: _ => { copies++; return Samples; }) is null);
            Check(copies == 0);
        });
        Test("every owner and vector component is rechecked before the second copy", () =>
        {
            foreach (var changed in new[] { Owner with { Player = 99 }, Owner with { ContentId = 99 }, Owner with { Module = 99 },
                Owner with { Manager = 99 }, Owner with { Agent = 99 }, Owner with { Addon = 99 }, Owner with { AddonId = 99 },
                Owner with { Data = 99 }, Owner with { First = 99 }, Owner with { Last = 99 }, Owner with { End = 99 } })
            {
                var observations = 0; var copies = 0;
                Check(Capture(observe: () => ++observations < 4 ? Owner : changed,
                    copy: _ => { copies++; return Samples.ToArray(); }) is null);
                Check(copies == 1);
            }
        });
        Test("whole sampled rows including omitted metadata must remain stable", () =>
        {
            foreach (var changed in new[] { Samples[0] with { Quantity = 1 }, Samples[0] with { ItemId = 1 },
                Samples[0] with { PouchRowId = 1 }, Samples[0] with { Icon = 1 }, Samples[0] with { Maximum = 1 },
                Samples[0] with { CategoryId = 3 }, Samples[0] with { InventoryIndex = 1 }, Samples[0] with { IsUndiscovered = true } })
            {
                var copies = 0;
                Check(Capture(copy: _ => ++copies == 1 ? Samples.ToArray() : [changed, Samples[1]]) is null);
            }
        });
        Test("definition and catalog changes reject detached results", () =>
        {
            var definitions = 0;
            Check(Capture(definition: id => ++definitions > 2 ? Definition(id)!.Value with { Name = "Changed" } : Definition(id)) is null);
            foreach (var failAt in new[] { 1, 2, 3 })
            {
                var checks = 0; Check(Capture(catalog: () => ++checks < failAt) is null);
            }
            foreach (var failAt in new[] { 3, 5, 6 })
            {
                var checks = 0; Check(Capture(observe: () => ++checks < failAt ? Owner : null) is null);
            }
        });
        Test("only the exact ready visible agent-owned Isleventory is accepted", () =>
        {
            Check(IslandMaterialsReader.MatchesAddon(5, 6, "MJIPouch", 5, 6, true, true));
            Check(!IslandMaterialsReader.MatchesAddon(5, 6, "MJIHud", 5, 6, true, true));
            Check(!IslandMaterialsReader.MatchesAddon(5, 6, "MJIPouch", 9, 6, true, true));
            Check(!IslandMaterialsReader.MatchesAddon(5, 6, "MJIPouch", 5, 7, true, true));
            Check(!IslandMaterialsReader.MatchesAddon(5, 6, "MJIPouch", 5, 6, false, true));
            Check(!IslandMaterialsReader.MatchesAddon(5, 6, "MJIPouch", 5, 6, true, false));
        });
        Test("native copy uses only the owned contiguous vector", () =>
        {
            using var fixture = new NativeFixture();
            Check(IslandMaterialsReader.Copy(fixture.Owner)!.SequenceEqual(Samples));
            // Unused category pointers and native text can be unusable; no traversal is permitted.
            fixture.Data->Materials.First = (FFXIVClientStructs.Interop.Pointer<AgentMJIPouch.PouchInventoryItem>*)1;
            fixture.Rows[0].Name.StringPtr = (byte*)1;
            Check(IslandMaterialsReader.Copy(fixture.Owner)!.SequenceEqual(Samples));
        });
        Test("native copy rejects changed data vector and addon bindings", () =>
        {
            using var fixture = new NativeFixture();
            Check(IslandMaterialsReader.Copy(fixture.Owner with { Data = 1 }) is null);
            Check(IslandMaterialsReader.Copy(fixture.Owner with { First = fixture.Owner.First + Stride }) is null);
            fixture.Agent->AddonId++;
            Check(IslandMaterialsReader.Copy(fixture.Owner) is null); fixture.Agent->AddonId--;
            fixture.Addon->Id++;
            Check(IslandMaterialsReader.Copy(fixture.Owner) is null); fixture.Addon->Id--;
            fixture.Data->Inventory.Last = fixture.Rows + 1;
            Check(IslandMaterialsReader.Copy(fixture.Owner) is null);
            fixture.Agent->InventoryData = null;
            Check(IslandMaterialsReader.Copy(fixture.Owner) is null);
        });
        Test("token shares the bounded typed native row layout without pointer traversal", () =>
        {
            IslandMaterialSample[] samples = [Token, Samples[1]];
            using var fixture = new NativeFixture(samples);
            fixture.Data->Tools.First = (FFXIVClientStructs.Interop.Pointer<AgentMJIPouch.PouchInventoryItem>*)1;
            fixture.Rows[0].Name.StringPtr = (byte*)1;
            Check(IslandMaterialsReader.Copy(fixture.Owner)!.SequenceEqual(samples));
            var result = IslandMaterialsReader.CaptureStable(() => fixture.Owner, IslandMaterialsReader.Copy, Definition, () => true);
            Check(result?.Items.Count == 2 && result.Items[1] is { ItemId: 41668, Quantity: 150, CategoryName: "Tokens" });
        });
        Test("observations are detached sorted and read-only", () =>
        {
            var rows = Samples.Reverse().ToArray(); var result = Capture(rows)!;
            rows[0] = rows[0] with { Quantity = 500 };
            Check(result.Items[0].PouchRowId == 0 && result.Items[1].Quantity == 42);
            try { ((IList)result.Items)[0] = result.Items[1]; throw new Exception("Mutable output"); }
            catch (NotSupportedException) { }
            Check(typeof(IslandMaterialsObservation).GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(p => p.Name).Order().SequenceEqual(new[] { "CapturedAtUtc", "Items" }));
        });
        Console.WriteLine($"{groups} Island materials groups passed; synthetic only, live Isleventory comparison remains required.");
    }

    private static void Test(string name, Action run) { run(); groups++; Console.WriteLine("PASS " + name); }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Fixture assertion failed."); }
    private sealed class NativeFixture : IDisposable
    {
        internal readonly AgentMJIPouch* Agent = (AgentMJIPouch*)NativeMemory.AllocZeroed((nuint)sizeof(AgentMJIPouch));
        internal readonly AtkUnitBase* Addon = (AtkUnitBase*)NativeMemory.AllocZeroed((nuint)sizeof(AtkUnitBase));
        internal readonly AgentMJIPouch.PouchInventoryData* Data = (AgentMJIPouch.PouchInventoryData*)NativeMemory.AllocZeroed((nuint)sizeof(AgentMJIPouch.PouchInventoryData));
        internal readonly AgentMJIPouch.PouchInventoryItem* Rows = (AgentMJIPouch.PouchInventoryItem*)NativeMemory.AllocZeroed(2, (nuint)Stride);
        internal IslandMaterialsOwner Owner { get; }
        internal NativeFixture(IslandMaterialSample[]? samples = null)
        {
            Agent->AddonId = 6; Addon->Id = 6; Agent->InventoryData = Data;
            Data->Inventory.First = Rows; Data->Inventory.Last = Rows + 2; Data->Inventory.End = Rows + 2;
            for (var index = 0; index < 2; index++)
            {
                var sample = (samples ?? Samples)[index];
                Rows[index].RowId = sample.PouchRowId; Rows[index].ItemId = sample.ItemId; Rows[index].IconId = sample.Icon;
                Rows[index].StackSize = sample.Quantity; Rows[index].MaxStackSize = sample.Maximum;
                Rows[index].InventoryIndex = sample.InventoryIndex; Rows[index].ItemCategory = sample.CategoryId;
                Rows[index].IsUndiscovered = sample.IsUndiscovered;
            }
            Owner = new(1, 42, 2, 3, (nint)Agent, (nint)Addon, 6, (nint)Data, (nint)Rows, (nint)(Rows + 2), (nint)(Rows + 2));
        }
        public void Dispose() { NativeMemory.Free(Rows); NativeMemory.Free(Data); NativeMemory.Free(Addon); NativeMemory.Free(Agent); }
    }
}
