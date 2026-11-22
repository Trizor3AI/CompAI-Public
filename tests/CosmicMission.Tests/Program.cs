using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using CompAI;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.Game.WKS;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

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
    private static readonly CosmicMissionOwner Owner = new(1, 100, 2, 1319, 3, 4, 5, 6, 7, 8, 9,
        10, 1200, 4, 1024, 1120, 1152, 0, 0, 0, 0);
    internal static CosmicMissionSample Sample() => new(
        [new(1, 62008, 4, 1, 8), new(2, 62008, 8, 1, 8), new(3, 62008, 0, 1, 8)],
        [new(0, null), new(1, "Mission 1"), new(2, "Mission 2"), new(3, "Mission 3")]);
    private static CosmicMissionDefinition Definition(uint id, uint job) => new(job, "Mission " + id, "Carpenter");
    internal static CosmicMissionObservation? Capture(CosmicMissionSample sample) =>
        CosmicMissionReader.CaptureStable(() => Owner, _ => sample, Definition, () => true, 1319);

    internal static void Run()
    {
        Test("positive medals keep displayed order and omit unobserved medal states", () =>
        {
            var before = DateTimeOffset.UtcNow; var result = Capture(Sample())!;
            Check(result.Entries.Count == 2 && result.CapturedAtUtc >= before && result.CapturedAtUtc <= DateTimeOffset.UtcNow);
            Check(result.Entries[0] == new CosmicMissionEntry(1, "Mission 1", 8, "Carpenter", "Silver"));
            Check(result.Entries[1].MissionId == 2 && result.Entries[1].Medal == "Gold");
            var json = JsonSerializer.Serialize(result);
            Check(!json.Contains("Territory") && !json.Contains("Planet") && !json.Contains("Required") && !json.Contains("Completed"));
        });
        Test("populated log without positive medals is available but an empty or header-only log is not", () =>
        {
            var sample = Sample(); sample.Native[0] = sample.Native[0] with { Flags = 32 };
            sample.Native[1] = sample.Native[1] with { Flags = 0 };
            Check(Capture(sample) is { Entries.Count: 0 });
            Check(Capture(sample with { Native = [] }) is null);
            Check(Capture(sample with { Displayed = [] }) is null);
            Check(Capture(sample with { Displayed = Enumerable.Repeat(new CosmicMissionDisplayedEntry(0, null), 4).ToArray() }) is null);
        });
        Test("invalid owner is rejected before any native copy", () =>
        {
            foreach (var owner in new[] { Owner with { Player = 0 }, Owner with { ContentId = 0 }, Owner with { Wks = 0 },
                Owner with { Agents = 0 }, Owner with { Addons = 0 }, Owner with { Agent = 0 }, Owner with { Data = 0 },
                Owner with { Addon = 0 }, Owner with { AddonId = 0 }, Owner with { AddonId = 65536 }, Owner with { Values = 0 },
                Owner with { ValueCount = 803 }, Owner with { ValueCount = 4097 }, Owner with { DisplayedCount = 0 },
                Owner with { DisplayedCount = 129 }, Owner with { SelectedJob = 11 }, Owner with { Territory = 1237 } })
                Check(CosmicMissionReader.CaptureStable(() => owner, _ => throw new Exception("Unexpected copy"), Definition, () => true, 1319) is null);
        });
        Test("vector bounds reject reversed misaligned empty and oversized allocations", () =>
        {
            Check(CosmicMissionReader.VectorCount(1024, 1120, 1152) == 3);
            foreach (var pointers in new (nint First, nint Last, nint End)[] {
                (0, 0, 0), (-1024, -928, -896), (1025, 1121, 1153), (1028, 1124, 1156),
                (1024, 1000, 1152), (1024, 1120, 1088), (1024, 1121, 1152),
                (1024, 1120, 1153), (1024, 1120, 1024 + 513 * 32) })
                Check(CosmicMissionReader.VectorCount(pointers.First, pointers.Last, pointers.End) == -1);
            Check(!CosmicMissionReader.ValidOwner(Owner with { Last = Owner.First }, 1319));
        });
        Test("public identity and exact displayed names are required even for a non-medal row", () =>
        {
            foreach (var change in new Func<CosmicMissionDefinition, CosmicMissionDefinition?>[] {
                _ => null, d => d with { ClassJobId = 9 }, d => d with { Name = "Different" },
                d => d with { JobName = "bad\0" }, d => d with { JobName = new string('x', 121) } })
                Check(CosmicMissionReader.CaptureStable(() => Owner, _ => Sample(),
                    (id, job) => id == 3 ? change(Definition(id, job)) : Definition(id, job), () => true, 1319) is null);
            var sample = Sample(); sample.Displayed[3] = new(3, "Wrong public title"); Check(Capture(sample) is null);
        });
        Test("duplicate missing ambiguous medal and invalid job rows fail closed", () =>
        {
            foreach (var replacement in new[] { new CosmicMissionNativeEntry(1, 62008, 8, 1, 8), new(2, 62008, 12, 1, 8),
                new(0, 62008, 8, 1, 8), new(65536, 62008, 8, 1, 8), new(uint.MaxValue, 62008, 8, 1, 8),
                new(2, 62008, 8, 1, 0), new(2, 62008, 8, 1, 19) })
            { var sample = Sample(); sample.Native[1] = replacement; Check(Capture(sample) is null); }
            foreach (var replacement in new[] { new CosmicMissionDisplayedEntry(1, "Mission 1"), new(4, "Mission 4") })
            { var sample = Sample(); sample.Displayed[2] = replacement; Check(Capture(sample) is null); }
        });
        Test("filtered-out native positives are never included", () =>
        {
            var owner = Owner with { DisplayedCount = 2 };
            var sample = Sample() with { Displayed = [new(0, null), new(1, "Mission 1")] };
            var result = CosmicMissionReader.CaptureStable(() => owner, _ => sample, Definition, () => true, 1319)!;
            Check(result.Entries.Count == 1 && result.Entries[0].MissionId == 1);
        });
        Test("owner changes during catalog resolution stop before a second native copy", () =>
        {
            foreach (var changed in new[] { Owner with { ContentId = 101 }, Owner with { Planet = 2 }, Owner with { Territory = 1237 },
                Owner with { Data = 20 }, Owner with { Values = 20 }, Owner with { First = 2048 },
                Owner with { SelectedJob = 1 }, Owner with { SelectedTab = 1 }, Owner with { SelectedFilter = 1 }, Owner with { AgentTab = 1 } })
            {
                var owner = Owner; var copies = 0;
                Check(CosmicMissionReader.CaptureStable(() => owner, _ => { copies++; return Sample(); },
                    (id, job) => { owner = changed; return Definition(id, job); }, () => true, 1319) is null);
                Check(copies == 1);
            }
        });
        Test("changed native rows displayed rows public names or catalog reject the capture", () =>
        {
            foreach (var change in new Action<CosmicMissionSample>[] { s => s.Native[0] = s.Native[0] with { Flags = 8 },
                s => s.Native[0] = s.Native[0] with { Group = 2 }, s => s.Displayed[1] = new(2, "Mission 2") })
            {
                var changed = Sample(); change(changed); var copies = 0;
                Check(CosmicMissionReader.CaptureStable(() => Owner, _ => ++copies == 1 ? Sample() : changed, Definition, () => true, 1319) is null);
            }
            var calls = 0;
            Check(CosmicMissionReader.CaptureStable(() => Owner, _ => Sample(),
                (id, job) => ++calls > 3 ? Definition(id, job) with { Name = "Changed" } : Definition(id, job), () => true, 1319) is null);
            var checks = 0;
            Check(CosmicMissionReader.CaptureStable(() => Owner, _ => Sample(), Definition, () => ++checks < 2, 1319) is null);
        });
        Test("only exact ready visible mission addon identity matches", () =>
        {
            Check(CosmicMissionReader.MatchesAddon(1, 2, "WKSMission", 1, 2, true, true));
            Check(!CosmicMissionReader.MatchesAddon(1, 2, "WKSMissionInfomation", 1, 2, true, true));
            Check(!CosmicMissionReader.MatchesAddon(1, 2, "WKSMission", 3, 2, true, true));
            Check(!CosmicMissionReader.MatchesAddon(1, 2, "WKSMission", 1, 3, true, true));
            Check(!CosmicMissionReader.MatchesAddon(1, 2, "WKSMission", 1, 2, false, true));
            Check(!CosmicMissionReader.MatchesAddon(1, 2, "WKSMission", 1, 2, true, false));
        });
        Test("typed native copier reads exact ID and name slots and detaches the observation", () =>
        {
            using var fixture = new Fixture();
            var result = CosmicMissionReader.CaptureStable(() => fixture.Owner, CosmicMissionReader.Copy, Definition, () => true, 1319)!;
            Check(result.Entries.Count == 2 && result.Entries[1].Medal == "Gold");
            fixture.Native[1].Flags = 0;
            Check(result.Entries[1].Medal == "Gold" && ((IList<CosmicMissionEntry>)result.Entries).IsReadOnly);
        });
        Test("native copier rejects wrong mode pending update owner changes and unloaded state", () =>
        {
            using var f = new Fixture();
            f.Data->IsMissionLog = false; Check(CosmicMissionReader.Copy(f.Owner) is null); f.Data->IsMissionLog = true;
            f.Data->UpdateFlags = 1; Check(CosmicMissionReader.Copy(f.Owner) is null); f.Data->UpdateFlags = 0;
            f.Manager->IsLoaded = false; Check(CosmicMissionReader.Copy(f.Owner) is null); f.Manager->IsLoaded = true;
            f.Player->IsLoaded = false; Check(CosmicMissionReader.Copy(f.Owner) is null); f.Player->IsLoaded = true;
            f.Player->ContentId++; Check(CosmicMissionReader.Copy(f.Owner) is null); f.Player->ContentId--;
            f.Manager->State.CurrentPlanetIndex--; Check(CosmicMissionReader.Copy(f.Owner) is null); f.Manager->State.CurrentPlanetIndex++;
            f.Manager->TerritoryId = 1237; Check(CosmicMissionReader.Copy(f.Owner) is null); f.Manager->TerritoryId = 1319;
            f.Data->SelectedFilterIndex = 1; Check(CosmicMissionReader.Copy(f.Owner) is null); f.Data->SelectedFilterIndex = 0;
            f.Agent->Data = null; Check(CosmicMissionReader.Copy(f.Owner) is null); f.Agent->Data = f.Data;
            f.Addon->AtkValues = null; Check(CosmicMissionReader.Copy(f.Owner) is null);
        });
        Test("native text reads reject wrong types invalid UTF8 and unterminated bounded text", () =>
        {
            using var f = new Fixture();
            f.Values[42].Type = AtkValueType.Int; Check(CosmicMissionReader.Copy(f.Owner) is null); f.Values[42].Type = AtkValueType.UInt;
            f.Values[804].Type = AtkValueType.UInt; Check(CosmicMissionReader.Copy(f.Owner) is null); f.Values[804].Type = AtkValueType.String;
            var pointer = f.Values[804].String.Value; var saved = pointer[0]; pointer[0] = 0xFF;
            Check(CosmicMissionReader.Copy(f.Owner) is null); pointer[0] = saved;
            new Span<byte>(pointer, CosmicMissionReader.MaximumNameBytes + 1).Fill((byte)'x');
            Check(CosmicMissionReader.Copy(f.Owner) is null);
        });
        Test("managed output cannot be changed through detached sample arrays", () =>
        {
            var sample = Sample(); var result = Capture(sample)!;
            sample.Native[0] = sample.Native[0] with { Flags = 8 };
            sample.Displayed[1] = new(1, "Changed");
            Check(result.Entries[0].Name == "Mission 1" && result.Entries[0].Medal == "Silver");
        });
        Test("hidden non-medal rows do not suppress medals or disclose hidden names", () =>
        {
            var sample = Sample(); sample.Native[2] = sample.Native[2] with { Flags = 1056 };
            sample.Displayed[3] = new(3, "???");
            var result = Capture(sample);
            Check(result is { Entries.Count: 2 } && result.Entries.All(row => row.MissionId != 3));
            Check(!JsonSerializer.Serialize(result).Contains("Mission 3", StringComparison.Ordinal));
            Check(CosmicMissionReader.CaptureStable(() => Owner, _ => sample,
                (id, job) => id == 3 ? null : Definition(id, job), () => true, 1319) is null);
            var hiddenDefinitionCalls = 0;
            Check(CosmicMissionReader.CaptureStable(() => Owner, _ => sample,
                (id, job) => id == 3 && ++hiddenDefinitionCalls > 1
                    ? Definition(id, job) with { Name = "Changed hidden title" } : Definition(id, job), () => true, 1319) is null);
            var changed = Sample(); changed.Native[2] = changed.Native[2] with { Flags = 1056 };
            var copies = 0;
            Check(CosmicMissionReader.CaptureStable(() => Owner, _ => ++copies == 1 ? sample : changed,
                Definition, () => true, 1319) is null);
        });
        Test("hidden medal rows still fail without revealing names or identities", () =>
        {
            var sample = Sample(); sample.Native[2] = sample.Native[2] with { Flags = 1064 };
            sample.Displayed[3] = new(3, "???"); CosmicMissionCaptureStatus? status = null;
            Check(CosmicMissionReader.CaptureStable(() => Owner, _ => sample, Definition, () => true, 1319,
                report: value => status = value) is null);
            Check(status is { Stage: "name_match", Reason: "display_public_name_mismatch", RowId: 3,
                NativeJob: 8, NativeFlags: 1064, NameMatch: false, Placeholder: true,
                DisplayedCount: 4, NativeCount: 3, NativeCapacity: 4, SelectedJob: 0, UpdateFlags: 0 });
            var json = JsonSerializer.Serialize(status);
            foreach (var excluded in new[] { "Mission 3", "Carpenter", "???", "ContentId", "Player", "Territory", "Planet", "Pointer", "Address" })
                Check(!json.Contains(excluded, StringComparison.Ordinal));
            var allowed = new HashSet<string> { "Stage", "Reason", "DisplayedCount", "NativeCount", "NativeCapacity", "ValueCount",
                "SelectedJob", "UpdateFlags", "RowId", "NativeJob", "NativeFlags", "NameMatch", "Placeholder", "ReturnedCount" };
            Check(typeof(CosmicMissionCaptureStatus).GetProperties().All(p => allowed.Contains(p.Name)));
        });
        Test("diagnostics distinguish bounds public mappings and native flags", () =>
        {
            CosmicMissionCaptureStatus? status = null;
            var oversized = Owner with { End = Owner.First + 513 * sizeof(AgentWKSMission.MissionEntry) };
            Check(CosmicMissionReader.CaptureStable(() => oversized, _ => throw new Exception("Unexpected copy"),
                Definition, () => true, 1319, report: value => status = value) is null);
            Check(status is { Stage: "bounds", NativeCount: 3, NativeCapacity: 513 });
            Check(CosmicMissionReader.CaptureStable(() => Owner, _ => Sample(), (_, _) => null,
                () => true, 1319, report: value => status = value) is null);
            Check(status is { Stage: "public_mapping", RowId: 1, NativeJob: 8, NativeFlags: 4 });
            var sample = Sample(); sample.Native[0] = sample.Native[0] with { Flags = 12 };
            Check(CosmicMissionReader.CaptureStable(() => Owner, _ => sample, Definition,
                () => true, 1319, report: value => status = value) is null);
            Check(status is { Stage: "native_mapping", Reason: "conflicting_medals", RowId: 1, NativeFlags: 12 });
        });
        Test("copy failure diagnostics preserve the first concrete reason", () =>
        {
            using var fixture = new Fixture(); fixture.Values[42].Type = AtkValueType.Int;
            CosmicMissionCaptureStatus? status = null;
            void RecordFirst(CosmicMissionCaptureStatus value) => status ??= value;
            Check(CosmicMissionReader.CaptureStable(() => fixture.Owner,
                owner => CosmicMissionReader.Copy(owner, RecordFirst), Definition, () => true, 1319, report: RecordFirst) is null);
            Check(status is { Stage: "copy", Reason: "mission_id_type", DisplayedCount: 4 });
        });
        Test("postcopy changes remain distinct from successful capture diagnostics", () =>
        {
            CosmicMissionCaptureStatus? status = null; var copies = 0;
            var changed = Sample(); changed.Native[0] = changed.Native[0] with { Flags = 8 };
            Check(CosmicMissionReader.CaptureStable(() => Owner, _ => ++copies == 1 ? Sample() : changed,
                Definition, () => true, 1319, report: value => status = value) is null);
            Check(status is { Stage: "postcopy", Reason: "sample_changed", ReturnedCount: null });
            Check(CosmicMissionReader.CaptureStable(() => Owner, _ => Sample(), Definition, () => true, 1319,
                report: value => status = value) is not null);
            Check(status is { Stage: "captured", Reason: null, ReturnedCount: 2, RowId: null });
        });
        Test("reader diagnostic begins unknown and invalid territory fails without native access", () =>
        {
            var reader = new CosmicMissionReader(null!, null!);
            Check(reader.LastCaptureStatus.Stage == "not_captured");
            Check(reader.Capture(0) is null);
            Check(reader.LastCaptureStatus is { Stage: "owner", Reason: "territory_unavailable", SelectedJob: null, UpdateFlags: null });
        });
        Console.WriteLine($"{passed} Cosmic mission groups passed; synthetic only, live Mission Log comparison remains required.");
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly PlayerState* Player = (PlayerState*)NativeMemory.AllocZeroed((nuint)sizeof(PlayerState));
        internal readonly WKSManager* Manager = (WKSManager*)NativeMemory.AllocZeroed((nuint)sizeof(WKSManager));
        internal readonly AgentWKSMission* Agent = (AgentWKSMission*)NativeMemory.AllocZeroed((nuint)sizeof(AgentWKSMission));
        internal readonly AgentWKSMission.MissionData* Data = (AgentWKSMission.MissionData*)NativeMemory.AllocZeroed((nuint)sizeof(AgentWKSMission.MissionData));
        internal readonly AtkUnitBase* Addon = (AtkUnitBase*)NativeMemory.AllocZeroed((nuint)sizeof(AtkUnitBase));
        internal readonly AtkValue* Values = (AtkValue*)NativeMemory.AllocZeroed((nuint)(1200 * sizeof(AtkValue)));
        internal readonly AgentWKSMission.MissionEntry* Native = (AgentWKSMission.MissionEntry*)NativeMemory.AllocZeroed((nuint)(4 * sizeof(AgentWKSMission.MissionEntry)));
        internal readonly CosmicMissionOwner Owner;
        private readonly List<nint> texts = [];
        internal Fixture()
        {
            Player->IsLoaded = true; Player->ContentId = 100;
            Manager->IsLoaded = true; Manager->TerritoryId = 1319; Manager->State.CurrentPlanetIndex = 3;
            Agent->Data = Data; Agent->AddonId = 9; Data->IsMissionLog = true;
            Data->MissionList.First = Native; Data->MissionList.Last = Native + 3; Data->MissionList.End = Native + 4;
            Addon->Id = 9; Addon->AtkValues = Values; Addon->AtkValuesCount = 1200;
            Values[33].Type = AtkValueType.UInt; Values[33].UInt = 4;
            Values[36].Type = AtkValueType.UInt;
            for (var i = 0; i < 3; i++)
            {
                Native[i].MissionUnitId = (uint)i + 1; Native[i].ClassJobIndex = 8; Native[i].IconId = 62008;
                Native[i].Flags = i == 0 ? AgentWKSMission.MissionFlags.Silver : i == 1 ? AgentWKSMission.MissionFlags.Gold : 0;
                var row = i + 1;
                Values[36 + row * 6].Type = AtkValueType.UInt; Values[36 + row * 6].UInt = (uint)i + 1;
                var text = (byte*)NativeMemory.AllocZeroed(CosmicMissionReader.MaximumNameBytes + 1);
                Encoding.UTF8.GetBytes("Mission " + (i + 1)).CopyTo(new Span<byte>(text, CosmicMissionReader.MaximumNameBytes));
                texts.Add((nint)text); Values[802 + row * 2].Type = AtkValueType.String; Values[802 + row * 2].String = text;
            }
            Owner = Tests.Owner with { Player = (nint)Player, Wks = (nint)Manager, Agent = (nint)Agent, Data = (nint)Data,
                Addon = (nint)Addon, Values = (nint)Values, First = (nint)Native, Last = (nint)(Native + 3), End = (nint)(Native + 4) };
        }
        public void Dispose()
        {
            foreach (var text in texts) NativeMemory.Free((void*)text);
            NativeMemory.Free(Native); NativeMemory.Free(Values); NativeMemory.Free(Addon); NativeMemory.Free(Data);
            NativeMemory.Free(Agent); NativeMemory.Free(Manager); NativeMemory.Free(Player);
        }
    }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Cosmic mission assertion failed."); }
    private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
}
