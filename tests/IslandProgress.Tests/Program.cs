using System.Reflection;
using System.Runtime.InteropServices;
using CompAI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

if (args is ["--public-catalog", var path]) { CatalogProbe.Run(path); return; }
if (args.Length != 0) throw new ArgumentException("Expected no arguments, or --public-catalog <sqpack path>.");
Tests.Run();

internal static unsafe class Tests
{
    private static int groups;
    private static readonly IslandProgressOwner Owner = new(1, 42, 2, 3, 4, 5, 6, 7, 32);
    private static readonly int Unsigned = (int)AtkValueType.UInt;
    private static readonly IslandProgressSample Normal = new(19, 12345, 100000, Unsigned, Unsigned, Unsigned);
    private static IslandRankDefinition? Definition(int rank) => rank is >= 1 and <= 20 ? new(rank, rank == 20 ? 0 : 100000) : null;
    private static IslandProgressObservation? Capture(IslandProgressSample? sample = null,
        Func<IslandProgressOwner?>? observe = null, Func<IslandProgressOwner, int, IslandProgressSample?>? copy = null,
        Func<int, IslandRankDefinition?>? definition = null, Func<bool>? catalog = null, int maximumRank = 20) =>
        IslandProgressReader.CaptureStable(observe ?? (() => Owner), copy ?? ((_, _) => sample ?? Normal),
            definition ?? Definition, catalog ?? (() => true), maximumRank);

    internal static void Run()
    {
        Test("public progression catalog identifies cap dynamically and ignores only rank zero", () =>
        {
            Check(IslandProgressReader.ValidateCatalog([(0u, 0u), (2u, 0u), (1u, 3000u)]) == 2);
            Check(IslandProgressReader.ValidateCatalog(Enumerable.Range(1, 21).Select(rank => ((uint)rank, rank == 21 ? 0u : 3000u))) == 21);
        });
        Test("incomplete duplicate malformed and oversized catalogs are rejected", () =>
        {
            (uint, uint)[][] bad = [[], [(0, 0)], [(1, 0)], [(1, 3000), (3, 0)], [(1, 3000), (1, 0)],
                [(0, 5), (1, 3000), (2, 0)], [(1, 0), (2, 3000), (3, 0)], [(1, 3000), (2, 5)],
                [(1, (uint)int.MaxValue + 1), (2, 0)], [(1, 3000), (256, 0)]];
            foreach (var rows in bad) Check(IslandProgressReader.ValidateCatalog(rows) is null);
            Check(IslandProgressReader.ValidateCatalog(Enumerable.Range(0, 257).Select(rank => ((uint)rank, 0u))) is null);
        });
        Test("normal rank has exact observed experience and capture time", () =>
        {
            var before = DateTimeOffset.UtcNow;
            var result = Capture();
            Check(result is { Rank: 19, Experience: 12345, RequiredExperience: 100000, IsMaximumRank: false });
            Check(result!.CapturedAtUtc >= before && result.CapturedAtUtc <= DateTimeOffset.UtcNow);
            Check(Capture(Normal with { Experience = 0 })?.Experience == 0);
        });
        Test("cap is explicit with absent experience rather than a zero denominator", () =>
        {
            var cap = new IslandProgressSample(20, null, null, Unsigned);
            Check(Capture(cap) is { Rank: 20, Experience: null, RequiredExperience: null, IsMaximumRank: true });
            Check(Capture(cap with { Experience = 0, RequiredExperience = 0 }) is null);
            Check(Capture(cap, definition: rank => new(rank, 100000)) is null);
            Check(Capture(Normal with { RequiredExperience = 0 }, definition: rank => new(rank, 0)) is null);
        });
        Test("rank experience and public denominator inconsistencies fail closed", () =>
        {
            foreach (var sample in new[] { Normal with { Rank = 0 }, Normal with { Rank = 21 },
                Normal with { Experience = -1 }, Normal with { Experience = 100000 }, Normal with { Experience = null },
                Normal with { RequiredExperience = 99999 }, Normal with { RequiredExperience = null },
                Normal with { RankType = (int)AtkValueType.String }, Normal with { ExperienceType = 0 }, Normal with { RequiredType = 0 } })
                Check(Capture(sample) is null);
            Check(Capture(definition: rank => rank == 19 ? null : Definition(rank)) is null);
            Check(Capture(definition: rank => rank == 19 ? new(18, 100000) : Definition(rank)) is null);
        });
        Test("invalid owners and cap definitions prevent native copies", () =>
        {
            var copies = 0;
            IslandProgressSample? Copy(IslandProgressOwner _, int __) { copies++; return Normal; }
            foreach (var owner in InvalidOwners()) Check(Capture(observe: () => owner, copy: Copy) is null);
            foreach (var max in new[] { 0, 1, 256 }) Check(Capture(copy: Copy, maximumRank: max) is null);
            Check(Capture(copy: Copy, definition: _ => null) is null);
            Check(copies == 0);
        });
        Test("owner is rechecked after catalog loading before first copy", () =>
        {
            var observations = 0;
            var copies = 0;
            Check(Capture(observe: () => ++observations == 1 ? Owner : null,
                copy: (_, _) => { copies++; return Normal; }) is null);
            Check(copies == 0);
        });
        Test("every owner component is checked before the second copy", () =>
        {
            foreach (var changed in ChangedOwners())
            {
                var observations = 0;
                var copies = 0;
                Check(Capture(observe: () => ++observations < 4 ? Owner : changed,
                    copy: (_, _) => { copies++; return Normal; }) is null);
                Check(copies == 1);
            }
        });
        Test("owner disappearance after either copy or final sheet work rejects result", () =>
        {
            foreach (var failAt in new[] { 3, 5, 6 })
            {
                var observations = 0;
                Check(Capture(observe: () => ++observations < failAt ? Owner : null) is null);
            }
        });
        Test("every sampled value and type must remain stable", () =>
        {
            foreach (var changed in new[] { Normal with { Rank = 18 }, Normal with { Experience = 12346 },
                Normal with { RequiredExperience = 100001 }, Normal with { RankType = (int)AtkValueType.Int },
                Normal with { ExperienceType = (int)AtkValueType.Int }, Normal with { RequiredType = (int)AtkValueType.Int } })
            {
                var copies = 0;
                Check(Capture(copy: (_, _) => ++copies == 1 ? Normal : changed) is null);
            }
        });
        Test("public definitions and catalog identity must remain stable", () =>
        {
            var calls = 0;
            Check(Capture(definition: rank => ++calls < 3 ? Definition(rank) : new(rank, 1)) is null);
            foreach (var failAt in new[] { 1, 2, 3 })
            {
                var checks = 0;
                Check(Capture(catalog: () => ++checks < failAt) is null);
            }
        });
        Test("only the exact visible ready owned Index is accepted", () =>
        {
            Check(IslandProgressReader.MatchesAddon(5, 6, "MJIHud", 5, 6, true, true));
            Check(!IslandProgressReader.MatchesAddon(5, 6, "MJIDisposeShop", 5, 6, true, true));
            Check(!IslandProgressReader.MatchesAddon(5, 6, "MJIHud", 9, 6, true, true));
            Check(!IslandProgressReader.MatchesAddon(5, 6, "MJIHud", 5, 7, true, true));
            Check(!IslandProgressReader.MatchesAddon(5, 6, "MJIHud", 5, 6, false, true));
            Check(!IslandProgressReader.MatchesAddon(5, 6, "MJIHud", 5, 6, true, false));
        });
        Test("native copier reads the three documented progress slots only", () =>
        {
            using var fixture = new NativeFixture();
            var first = IslandProgressReader.Copy(fixture.Owner, 20);
            Check(first == Normal);
            for (var index = 0; index < 32; index++) if (index is < 11 or > 13) fixture.Values[index].Int = -300;
            Check(IslandProgressReader.Copy(fixture.Owner, 20) == first);
            var result = fixture.Capture();
            fixture.Values[12].UInt = 54321;
            Check(result?.Experience == 12345);
        });
        Test("cap never reads masked experience slots or turns them into zero progress", () =>
        {
            using var fixture = new NativeFixture();
            fixture.Values[11].UInt = 20;
            fixture.Values[12].Type = AtkValueType.String;
            fixture.Values[12].UInt = uint.MaxValue;
            fixture.Values[13].Type = AtkValueType.Int;
            fixture.Values[13].Int = -1;
            Check(fixture.Capture() is { Rank: 20, IsMaximumRank: true, Experience: null, RequiredExperience: null });
            fixture.Values[11].UInt = 19;
            Check(fixture.Capture() is null);
        });
        Test("native signed and unsigned values have bounded integral semantics", () =>
        {
            using var fixture = new NativeFixture();
            fixture.Values[11].Type = AtkValueType.Int;
            fixture.Values[12].Type = AtkValueType.Int;
            fixture.Values[13].Type = AtkValueType.Int;
            Check(fixture.Capture()?.Experience == 12345);
            foreach (var index in new[] { 11, 12, 13 })
            {
                var saved = fixture.Values[index];
                fixture.Values[index].Int = -1;
                Check(IslandProgressReader.Copy(fixture.Owner, 20) is null);
                fixture.Values[index].Type = AtkValueType.UInt;
                fixture.Values[index].UInt = (uint)int.MaxValue + 1;
                Check(IslandProgressReader.Copy(fixture.Owner, 20) is null);
                fixture.Values[index] = saved;
            }
        });
        Test("native copies reject stale values binding count and addon identities", () =>
        {
            using var fixture = new NativeFixture();
            foreach (var count in new[] { 0, 13, 257 })
                Check(IslandProgressReader.Copy(fixture.Owner with { ValueCount = count }, 20) is null);
            Check(IslandProgressReader.Copy(fixture.Owner with { Values = 1 }, 20) is null);
            Check(IslandProgressReader.Copy(fixture.Owner with { ValueCount = 31 }, 20) is null);
            fixture.Agent->AddonId++;
            Check(IslandProgressReader.Copy(fixture.Owner, 20) is null);
            fixture.Agent->AddonId--;
            fixture.Addon->Id++;
            Check(IslandProgressReader.Copy(fixture.Owner, 20) is null);
            fixture.Addon->Id--;
            fixture.Addon->AtkValues = null;
            Check(IslandProgressReader.Copy(fixture.Owner, 20) is null);
        });
        Test("future catalog cap is used without hardcoded rank twenty", () =>
        {
            using var fixture = new NativeFixture();
            fixture.Values[11].UInt = 20;
            Check(IslandProgressReader.CaptureStable(() => fixture.Owner, IslandProgressReader.Copy,
                rank => new(rank, rank == 21 ? 0 : 100000), () => true, 21) is
                { Rank: 20, IsMaximumRank: false, Experience: 12345, RequiredExperience: 100000 });
            fixture.Values[11].UInt = 21;
            Check(IslandProgressReader.CaptureStable(() => fixture.Owner, IslandProgressReader.Copy,
                rank => new(rank, rank == 21 ? 0 : 100000), () => true, 21) is
                { Rank: 21, IsMaximumRank: true, Experience: null, RequiredExperience: null });
        });
        Test("output contains only detached progress and its capture timestamp", () =>
        {
            var names = typeof(IslandProgressObservation).GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(p => p.Name).Order().ToArray();
            Check(names.SequenceEqual(new[] { "CapturedAtUtc", "Experience", "IsMaximumRank", "Rank", "RequiredExperience" }));
        });
        Console.WriteLine($"{groups} Island progress groups passed; synthetic only, live Index comparison remains required.");
    }

    private static IEnumerable<IslandProgressOwner?> InvalidOwners()
    {
        yield return null;
        yield return Owner with { Player = 0 }; yield return Owner with { ContentId = 0 };
        yield return Owner with { Module = 0 }; yield return Owner with { Manager = 0 };
        yield return Owner with { Agent = 0 }; yield return Owner with { Addon = 0 };
        yield return Owner with { AddonId = 0 }; yield return Owner with { AddonId = 65536 };
        yield return Owner with { Values = 0 }; yield return Owner with { ValueCount = 13 };
    }
    private static IEnumerable<IslandProgressOwner> ChangedOwners()
    {
        yield return Owner with { Player = 99 }; yield return Owner with { ContentId = 99 };
        yield return Owner with { Module = 99 }; yield return Owner with { Manager = 99 };
        yield return Owner with { Agent = 99 }; yield return Owner with { Addon = 99 };
        yield return Owner with { AddonId = 99 }; yield return Owner with { Values = 99 };
        yield return Owner with { ValueCount = 31 };
    }
    private static void Test(string name, Action run) { run(); groups++; Console.WriteLine("PASS " + name); }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Fixture assertion failed."); }

    private sealed class NativeFixture : IDisposable
    {
        internal readonly AgentInterface* Agent = (AgentInterface*)NativeMemory.AllocZeroed((nuint)sizeof(AgentInterface));
        internal readonly AtkUnitBase* Addon = (AtkUnitBase*)NativeMemory.AllocZeroed((nuint)sizeof(AtkUnitBase));
        internal readonly AtkValue* Values = (AtkValue*)NativeMemory.AllocZeroed(32, (nuint)sizeof(AtkValue));
        internal IslandProgressOwner Owner { get; }
        internal NativeFixture()
        {
            Agent->AddonId = 6; Addon->Id = 6; Addon->AtkValues = Values; Addon->AtkValuesCount = 32;
            Values[11].Type = Values[12].Type = Values[13].Type = AtkValueType.UInt;
            Values[11].UInt = 19; Values[12].UInt = 12345; Values[13].UInt = 100000;
            Owner = new(1, 42, 2, 3, (nint)Agent, (nint)Addon, 6, (nint)Values, 32);
        }
        internal IslandProgressObservation? Capture() => IslandProgressReader.CaptureStable(() => Owner,
            IslandProgressReader.Copy, Definition, () => true, 20);
        public void Dispose() { NativeMemory.Free(Values); NativeMemory.Free(Addon); NativeMemory.Free(Agent); }
    }
}
