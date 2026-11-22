using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using CompAI;
using FFXIVClientStructs.FFXIV.Client.Game.WKS;

var library = Environment.GetEnvironmentVariable("DALAMUD_LIB_PATH") ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "addon", "Hooks", "dev");
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var path = Path.Combine(library, name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
Tests.Run();
if (args is ["--produce-fixture", var output])
    File.WriteAllText(output, JsonSerializer.Serialize(Tests.Capture(Tests.Sample()), new JsonSerializerOptions { WriteIndented = true }));

internal static unsafe class Tests
{
    private static int passed;
    private static readonly CosmicResearchOwner Owner = new(1, 100, 2, 3, 1319, 4, 5, 6, 7, 8);
    internal static CosmicResearchSample Sample() => new(Enumerable.Repeat((byte)20, 11).ToArray(),
        Enumerable.Repeat((byte)20, 11).ToArray(), Enumerable.Range(0, 11).SelectMany(_ =>
            new ushort[] { 9500, 9500, 9500, 9500, 5600, 2700, 1600 }).ToArray());
    private static CosmicResearchDefinition Definition(int toolClass) => new((uint)toolClass + 7,
        "Synthetic job " + toolClass, Enumerable.Range(0, 20).Select(i => i == 0 ? null : "Research stage " + (i + 1)).ToArray(),
        Enumerable.Range(1, 7).Select(i => "Synthetic research " + toolClass + "-" + i).ToArray());
    internal static CosmicResearchObservation? Capture(CosmicResearchSample sample) =>
        CosmicResearchReader.CaptureStable(() => Owner, _ => sample, Definition, () => true, 1319);

    internal static void Run()
    {
        Test("all eleven jobs retain seven banked values and public stage labels", () =>
        {
            var before = DateTimeOffset.UtcNow;
            var result = Capture(Sample())!;
            Check(result.Jobs.Count == 11 && result.CapturedAtUtc >= before && result.CapturedAtUtc <= DateTimeOffset.UtcNow);
            for (var job = 0; job < 11; job++)
            {
                var row = result.Jobs[job];
                Check(row.ToolClassId == job + 1 && row.ClassJobId == job + 8 && row.Data.Count == 7);
                Check(row.CurrentStage == 20 && row.UnlockedStage == 20 && row.CurrentStageName == "Research stage 20");
                Check(row.Data.Select(value => value.Type).SequenceEqual(Enumerable.Range(1, 7).Select(i => (uint)i)));
                Check(row.Data.Select(value => value.Quantity).SequenceEqual(new[] { 9500, 9500, 9500, 9500, 5600, 2700, 1600 }));
            }
        });
        Test("zero and initial stage observations do not invent a named stage or requirement", () =>
        {
            var sample = new CosmicResearchSample(new byte[11], Enumerable.Repeat((byte)1, 11).ToArray(), new ushort[77]);
            var result = Capture(sample)!;
            Check(result.Jobs.All(job => job.CurrentStage == 0 && job.CurrentStageName is null &&
                job.UnlockedStage == 1 && job.UnlockedStageName is null && job.Data.All(value => value.Quantity == 0)));
            var json = JsonSerializer.Serialize(result);
            Check(!json.Contains("Required") && !json.Contains("Maximum") && !json.Contains("Owner") && !json.Contains("Rate"));
        });
        Test("unsupported owner or territory is rejected before native copying", () =>
        {
            foreach (var owner in new[] { Owner with { Player = 0 }, Owner with { ContentId = 0 }, Owner with { Wks = 0 },
                Owner with { Research = 0 }, Owner with { Territory = 1 }, Owner with { Agents = 0 }, Owner with { Addons = 0 },
                Owner with { Agent = 0 }, Owner with { Addon = 0 }, Owner with { AddonId = 0 }, Owner with { AddonId = 65536 } })
                Check(CosmicResearchReader.CaptureStable(() => owner, _ => throw new Exception("Must not copy"), Definition, () => true, 1319) is null);
            Check(CosmicResearchReader.CaptureStable(() => null, _ => throw new Exception("Must not copy"), Definition, () => true, 1319) is null);
            Check(CosmicResearchReader.CaptureStable(() => Owner, _ => Sample(), Definition, () => true, 0) is null);
        });
        Test("invalid dimensions and unsupported stages fail closed", () =>
        {
            var invalid = new[] { Sample() with { CurrentStages = new byte[10] }, Sample() with { UnlockedStages = new byte[12] },
                Sample() with { Analysis = new ushort[76] }, Sample() with { Analysis = new ushort[78] },
                Sample() with { CurrentStages = Enumerable.Repeat((byte)21, 11).ToArray() },
                Sample() with { UnlockedStages = Enumerable.Repeat(byte.MaxValue, 11).ToArray() } };
            foreach (var sample in invalid) Check(Capture(sample) is null);
        });
        Test("every named definition and the exact tool to job mapping are required", () =>
        {
            foreach (var change in new Func<CosmicResearchDefinition, CosmicResearchDefinition?>[] {
                _ => null, d => d with { ClassJobId = 99 }, d => d with { Name = "" }, d => d with { Name = "bad\0" },
                d => d with { Name = new string('x', 129) }, d => d with { StageNames = new string?[19] },
                d => d with { DataNames = new string[7] }, d => d with { StageNames = new string?[20] } })
                Check(CosmicResearchReader.CaptureStable(() => Owner, _ => Sample(),
                    id => id == 11 ? change(Definition(id)) : Definition(id), () => true, 1319) is null);
        });
        Test("owner changes during definitions prevent a second native copy", () =>
        {
            foreach (var changed in new[] { Owner with { ContentId = 101 }, Owner with { Research = 10 },
                         Owner with { Wks = 10 }, Owner with { Addon = 10 }, Owner with { Territory = 1237 } })
            {
                var current = Owner; var copies = 0;
                Check(CosmicResearchReader.CaptureStable(() => current, _ => { copies++; return Sample(); },
                    id => { current = changed; return Definition(id); }, () => true, 1319) is null);
                Check(copies == 1);
            }
        });
        Test("changed counts stages definitions or catalog reject the complete observation", () =>
        {
            foreach (var change in new Action<CosmicResearchSample>[] { s => s.Analysis[76]++,
                s => s.CurrentStages[10]--, s => s.UnlockedStages[10]-- })
            {
                var changed = Sample(); change(changed); var copies = 0;
                Check(CosmicResearchReader.CaptureStable(() => Owner, _ => ++copies == 1 ? Sample() : changed,
                    Definition, () => true, 1319) is null);
            }
            var calls = 0;
            Check(CosmicResearchReader.CaptureStable(() => Owner, _ => Sample(),
                id => ++calls > 11 ? Definition(id) with { Name = "Changed" } : Definition(id), () => true, 1319) is null);
            var checks = 0;
            Check(CosmicResearchReader.CaptureStable(() => Owner, _ => Sample(), Definition, () => ++checks < 2, 1319) is null);
        });
        Test("native typed copier preserves job-major type order and detached data", () =>
        {
            using var fixture = new Fixture();
            var result = CosmicResearchReader.CaptureStable(() => fixture.Owner, CosmicResearchReader.Copy, Definition, () => true, 1319)!;
            for (var job = 0; job < 11; job++)
                for (var type = 0; type < 7; type++) Check(result.Jobs[job].Data[type].Quantity == job * 100 + type);
            fixture.Research->Analysis[0] = 999;
            Check(result.Jobs[0].Data[0].Quantity == 0);
        });
        Test("native typed copier requires loaded matching manager and module", () =>
        {
            using var fixture = new Fixture();
            fixture.Manager->IsLoaded = false; Check(CosmicResearchReader.Copy(fixture.Owner) is null);
            fixture.Manager->IsLoaded = true; fixture.Research->IsLoaded = false; Check(CosmicResearchReader.Copy(fixture.Owner) is null);
            fixture.Research->IsLoaded = true; fixture.Manager->TerritoryId = 1; Check(CosmicResearchReader.Copy(fixture.Owner) is null);
            fixture.Manager->TerritoryId = 1319; fixture.Manager->ResearchModule = null; Check(CosmicResearchReader.Copy(fixture.Owner) is null);
        });
        Test("only the exact ready visible owned research menu matches", () =>
        {
            Check(CosmicResearchReader.MatchesAddon(1, 2, "WKSToolCustomize", 1, 2, true, true));
            Check(!CosmicResearchReader.MatchesAddon(1, 2, "WKSHud", 1, 2, true, true));
            Check(!CosmicResearchReader.MatchesAddon(1, 2, "WKSToolCustomize", 3, 2, true, true));
            Check(!CosmicResearchReader.MatchesAddon(1, 2, "WKSToolCustomize", 1, 3, true, true));
            Check(!CosmicResearchReader.MatchesAddon(1, 2, "WKSToolCustomize", 1, 2, false, true));
            Check(!CosmicResearchReader.MatchesAddon(1, 2, "WKSToolCustomize", 1, 2, true, false));
        });
        Test("detached output cannot be rewritten through sample or result collections", () =>
        {
            var sample = Sample(); var result = Capture(sample)!;
            sample.Analysis[0] = 3; sample.CurrentStages[0] = 2;
            Check(result.Jobs[0].Data[0].Quantity == 9500 && result.Jobs[0].CurrentStage == 20);
            Check(((IList<CosmicResearchJob>)result.Jobs).IsReadOnly && ((IList<CosmicResearchData>)result.Jobs[0].Data).IsReadOnly);
        });
        Console.WriteLine($"{passed} Cosmic research groups passed; synthetic only, live research-menu comparison remains required.");
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly WKSManager* Manager;
        internal readonly WKSResearchModule* Research;
        internal readonly CosmicResearchOwner Owner;
        internal Fixture()
        {
            Manager = (WKSManager*)NativeMemory.AllocZeroed((nuint)sizeof(WKSManager));
            Research = (WKSResearchModule*)NativeMemory.AllocZeroed((nuint)sizeof(WKSResearchModule));
            Manager->TerritoryId = 1319; Manager->IsLoaded = true; Manager->ResearchModule = Research; Research->IsLoaded = true;
            for (var job = 0; job < 11; job++)
            {
                Research->CurrentStages[job] = Research->UnlockedStages[job] = 20;
                for (var type = 0; type < 7; type++) Research->Analysis[job * 7 + type] = (ushort)(job * 100 + type);
            }
            Owner = Tests.Owner with { Wks = (nint)Manager, Research = (nint)Research };
        }
        public void Dispose() { NativeMemory.Free(Research); NativeMemory.Free(Manager); }
    }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Cosmic research assertion failed."); }
    private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
}
