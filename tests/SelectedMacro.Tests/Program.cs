using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using CompAI;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

if (args.Length != 0) throw new ArgumentException("This fixture accepts no arguments and never reads a game process or player files.");
Tests.Run();

internal static unsafe class Tests
{
    private static int groups;
    private static readonly SelectedMacroOwner Owner = new(8, 42, 16, 24, 32, 40, 6, 48, 0, 0);
    private static readonly string[] Lines = ["/echo first", "", "  /echo\tcafé 日本語  ", "", "", "", "", "", "", "", "", "", "", "", "/echo last"];

    private static SelectedMacroTextBuffer Buffer(int index, byte[] bytes) => new(1000 + index * 8, 200 + index, bytes.Length, bytes.Length, bytes);
    private static SelectedMacroTextBuffer Buffer(int index, string value) => Buffer(index, Encoding.UTF8.GetBytes(value + "\0"));
    private static SelectedMacroSample Sample(string name = "Sample", string[]? lines = null)
    {
        lines ??= Lines;
        return new(new[] { Buffer(0, name) }.Concat(lines.Select((line, index) => Buffer(index + 1, line))).ToArray());
    }
    private static SelectedMacroObservation? Capture(SelectedMacroSample? sample = null,
        Func<SelectedMacroOwner?>? observe = null, Func<SelectedMacroOwner, SelectedMacroSample?>? copy = null) =>
        SelectedMacroReader.CaptureStable(observe ?? (() => Owner), copy ?? (_ => sample ?? Sample()));
    private static SelectedMacroSample ChangeBuffer(SelectedMacroSample original, int index, SelectedMacroTextBuffer changed)
    {
        var buffers = original.Buffers.ToArray(); buffers[index] = changed; return new(buffers);
    }

    internal static void Run()
    {
        Test("one selected record preserves name Unicode whitespace and all 15 positions", () =>
        {
            var before = DateTimeOffset.UtcNow;
            var result = Capture()!;
            Check(result.CapturedAtUtc >= before && result.Set == 0 && result.Index == 0 && result.Name == "Sample");
            Check(result.Lines.SequenceEqual(Lines) && result.Lines.Count == 15);
        });
        Test("both sets and index boundaries are supported without relabeling native indexes", () =>
        {
            foreach (var set in new[] { 0, 1 }) foreach (var index in new[] { 0, 99 })
            {
                var result = Capture(observe: () => Owner with { Set = set, Index = index });
                Check(result?.Set == set && result.Index == index);
            }
        });
        Test("initialized empty names and 15 empty lines are an observed blank record", () =>
        {
            var empty = Sample("", Enumerable.Repeat("", 15).ToArray());
            Check(Capture(empty) is { Name.Length: 0, Lines.Count: 15 } result && result.Lines.All(line => line == ""));
            Check(Capture(ChangeBuffer(empty, 0, Buffer(0, Array.Empty<byte>()))) is null);
            Check(Capture(ChangeBuffer(empty, 8, empty.Buffers[8] with { Buffer = 0 })) is null);
        });
        Test("invalid owners and selections prevent native copy", () =>
        {
            var copies = 0;
            foreach (var owner in new SelectedMacroOwner?[] { null, Owner with { Player = 0 }, Owner with { Player = -1 },
                Owner with { ContentId = 0 }, Owner with { Agents = 0 }, Owner with { Manager = 0 }, Owner with { Agent = 0 },
                Owner with { Addon = 0 }, Owner with { AddonId = 0 }, Owner with { AddonId = 65536 }, Owner with { Macros = 0 },
                Owner with { Set = -1 }, Owner with { Set = 2 }, Owner with { Index = -1 }, Owner with { Index = 100 } })
                Check(Capture(observe: () => owner, copy: _ => { copies++; return Sample(); }) is null);
            Check(copies == 0);
        });
        Test("misaligned owner pointers and string structures fail before native dereference", () =>
        {
            using var fixture = new NativeFixture(); var owner = fixture.Owner;
            foreach (var invalid in new[] { owner with { Player = owner.Player + 1 }, owner with { Agents = owner.Agents + 1 },
                owner with { Manager = owner.Manager + 1 }, owner with { Agent = owner.Agent + 1 },
                owner with { Addon = owner.Addon + 1 }, owner with { Macros = owner.Macros + 1 } })
            {
                Check(SelectedMacroReader.Copy(invalid) is null);
                var copies = 0;
                Check(Capture(observe: () => invalid, copy: _ => { copies++; return Sample(); }) is null && copies == 0);
            }
            var buffer = SelectedMacroReader.Copy(owner)!.Buffers[0];
            Check(!SelectedMacroReader.TextBufferMatches(buffer with { Address = buffer.Address + 1 }, SelectedMacroReader.NameByteLimit));
            Check(!SelectedMacroReader.MatchesAddon(9, 6, "Macro", 9, 6, true, true));
        });
        Test("owner and selection are re-resolved before first and second copies", () =>
        {
            var changes = new[] { Owner with { Player = 99 }, Owner with { ContentId = 99 }, Owner with { Agents = 99 },
                Owner with { Manager = 99 }, Owner with { Agent = 99 }, Owner with { Addon = 99 }, Owner with { AddonId = 99 },
                Owner with { Macros = 99 }, Owner with { Set = 1 }, Owner with { Index = 99 } };
            foreach (var changed in changes) foreach (var failAt in new[] { 2, 3, 4, 5 })
            {
                var observations = 0; var copies = 0;
                Check(Capture(observe: () => ++observations < failAt ? Owner : changed,
                    copy: _ => { copies++; return Sample(); }) is null);
                Check(copies == (failAt == 2 ? 0 : failAt == 5 ? 2 : 1));
            }
        });
        Test("missing partial or excessive line samples fail closed", () =>
        {
            Check(Capture(copy: _ => null) is null);
            Check(Capture(new([])) is null);
            Check(Capture(Sample(lines: Lines[..14])) is null);
            Check(Capture(Sample(lines: Lines.Concat(new[] { "extra" }).ToArray())) is null);
            var sample = Sample(); sample.Buffers[4] = null!;
            Check(Capture(sample) is null);
        });
        Test("every string identity capacity and byte content must match across samples", () =>
        {
            var original = Sample();
            foreach (var index in new[] { 0, 1, 2, 15 })
            {
                var value = original.Buffers[index];
                foreach (var changed in new[] { value with { Address = 999 }, value with { Buffer = 999 },
                    value with { Capacity = value.Capacity + 1 }, Buffer(index, "changed"), value with { Used = 0 } })
                {
                    var copies = 0;
                    Check(Capture(copy: _ => ++copies == 1 ? original : ChangeBuffer(original, index, changed)) is null);
                }
            }
        });
        Test("strict UTF-8 payload controls and embedded line breaks are rejected whole", () =>
        {
            foreach (var bytes in new byte[][] { [0xC0, 0xAF, 0], [0xED, 0xA0, 0x80, 0], [0xF4, 0x90, 0x80, 0x80, 0],
                [0x02, 0x2E, 0x01, 0x03, 0], [65, 10, 66, 0], [65, 13, 66, 0], [0x7F, 0], [0xC2, 0x85, 0] })
                foreach (var index in new[] { 0, 1, 15 })
                    Check(Capture(ChangeBuffer(Sample(), index, Buffer(index, bytes))) is null);
        });
        Test("TAB is preserved in lines but is unsupported in the macro name", () =>
        {
            Check(Capture()?.Lines[2] == Lines[2] && Lines[2].Contains('\t'));
            Check(Capture(Sample("name\twith tab")) is null);
        });
        Test("terminators pointers used lengths and capacity metadata are validated", () =>
        {
            var sample = Sample(); var value = sample.Buffers[1];
            foreach (var changed in new[] { value with { Address = 0 }, value with { Address = -1 }, value with { Buffer = 0 },
                value with { Buffer = -1 }, value with { Used = -1 }, value with { Used = value.Used - 1 },
                value with { Capacity = value.Used - 1 }, Buffer(1, [65]), Buffer(1, [65, 0, 66, 0]) })
                Check(Capture(ChangeBuffer(sample, 1, changed)) is null);
        });
        Test("capture limits count UTF-8 bytes and never truncate", () =>
        {
            Check(Capture(Sample(new string('x', SelectedMacroReader.NameByteLimit))) is not null);
            Check(Capture(Sample(new string('x', SelectedMacroReader.NameByteLimit + 1))) is null);
            Check(Capture(Sample(new string('x', 127) + "é")) is null);
            var lines = Lines.ToArray(); lines[14] = new string('x', SelectedMacroReader.LineByteLimit);
            Check(Capture(Sample(lines: lines))?.Lines[14].Length == SelectedMacroReader.LineByteLimit);
            lines[14] += "x"; Check(Capture(Sample(lines: lines)) is null);
        });
        Test("only the exact ready visible agent-owned Macro addon is accepted", () =>
        {
            Check(SelectedMacroReader.MatchesAddon(8, 6, "Macro", 8, 6, true, true));
            Check(!SelectedMacroReader.MatchesAddon(8, 6, "MacroIcon", 8, 6, true, true));
            Check(!SelectedMacroReader.MatchesAddon(8, 6, "Macro", 16, 6, true, true));
            Check(!SelectedMacroReader.MatchesAddon(8, 6, "Macro", 8, 7, true, true));
            Check(!SelectedMacroReader.MatchesAddon(8, 6, "Macro", 8, 6, false, true));
            Check(!SelectedMacroReader.MatchesAddon(8, 6, "Macro", 8, 6, true, false));
        });
        Test("native copy reads only the selected typed record with stale addon index ignored", () =>
        {
            using var fixture = new NativeFixture(0, 0);
            fixture.Addon->SelectedMacroIndex = 99;
            fixture.Agent->RawMacroString.StringPtr = (byte*)1;
            fixture.Agent->ParsedMacroString.StringPtr = (byte*)1;
            fixture.Agent->ClipboardMacro.Name.StringPtr = (byte*)1;
            fixture.Macros->Individual[99].Name.StringPtr = (byte*)1;
            fixture.Macros->Shared[0].Name.StringPtr = (byte*)1;
            var result = SelectedMacroReader.CaptureStable(() => fixture.Owner, SelectedMacroReader.Copy);
            Check(result?.Name == "Native" && result.Lines.SequenceEqual(Lines));
        });
        Test("shared index 99 uses its selected record without reading individual or other shared macros", () =>
        {
            using var fixture = new NativeFixture(1, 99);
            fixture.Macros->Individual[99].Name.StringPtr = (byte*)1;
            fixture.Macros->Shared[0].Name.StringPtr = (byte*)1;
            var result = SelectedMacroReader.CaptureStable(() => fixture.Owner, SelectedMacroReader.Copy);
            Check(result is { Set: 1, Index: 99, Name: "Native" } && result.Lines.SequenceEqual(Lines));
        });
        Test("native owner selection addon and loaded-character changes reject the copy", () =>
        {
            using var fixture = new NativeFixture();
            fixture.Player->IsLoaded = false; Check(SelectedMacroReader.Copy(fixture.Owner) is null); fixture.Player->IsLoaded = true;
            fixture.Player->ContentId++; Check(SelectedMacroReader.Copy(fixture.Owner) is null); fixture.Player->ContentId--;
            fixture.Macros->CharacterContentId++; Check(SelectedMacroReader.Copy(fixture.Owner) is null); fixture.Macros->CharacterContentId--;
            fixture.Agent->SelectedMacroSet = 1; Check(SelectedMacroReader.Copy(fixture.Owner) is null); fixture.Agent->SelectedMacroSet = 0;
            fixture.Agent->SelectedMacroIndex = 1; Check(SelectedMacroReader.Copy(fixture.Owner) is null); fixture.Agent->SelectedMacroIndex = 0;
            fixture.Agent->AddonId++; Check(SelectedMacroReader.Copy(fixture.Owner) is null); fixture.Agent->AddonId--;
            fixture.Addon->Id++; Check(SelectedMacroReader.Copy(fixture.Owner) is null); fixture.Addon->Id--;
            Check(SelectedMacroReader.Copy(fixture.Owner) is not null);
        });
        Test("native buffer initialization is required even for a blank selected macro", () =>
        {
            using var fixture = new NativeFixture(blank: true);
            Check(SelectedMacroReader.CaptureStable(() => fixture.Owner, SelectedMacroReader.Copy) is { Name.Length: 0 } result && result.Lines.All(string.IsNullOrEmpty));
            fixture.Selected->Lines[7].BufUsed = 0;
            Check(SelectedMacroReader.Copy(fixture.Owner) is null);
        });
        Test("native used length is authoritative and unrelated StringLength is ignored", () =>
        {
            using var fixture = new NativeFixture();
            Check(fixture.Selected->Name.StringLength == long.MaxValue);
            Check((nint)(byte*)fixture.Selected->Name.StringPtr % IntPtr.Size != 0); // Byte buffers need no struct alignment.
            Check(SelectedMacroReader.Copy(fixture.Owner) is not null);
            fixture.Selected->Name.BufUsed = SelectedMacroReader.NameByteLimit + 2;
            Check(SelectedMacroReader.Copy(fixture.Owner) is null);
        });
        Test("native string pointer size and bytes are rechecked before reuse", () =>
        {
            using var fixture = new NativeFixture();
            var buffer = SelectedMacroReader.Copy(fixture.Owner)!.Buffers[1];
            Check(SelectedMacroReader.TextBufferMatches(buffer, SelectedMacroReader.LineByteLimit));
            ((byte*)fixture.Selected->Lines[0].StringPtr)[0] = (byte)'!';
            Check(!SelectedMacroReader.TextBufferMatches(buffer, SelectedMacroReader.LineByteLimit));
            ((byte*)fixture.Selected->Lines[0].StringPtr)[0] = (byte)'/';
            fixture.Selected->Lines[0].BufSize++;
            Check(!SelectedMacroReader.TextBufferMatches(buffer, SelectedMacroReader.LineByteLimit));
            fixture.Selected->Lines[0].BufSize--;
            fixture.Selected->Lines[0].StringPtr = (byte*)1;
            Check(!SelectedMacroReader.TextBufferMatches(buffer, SelectedMacroReader.LineByteLimit));
        });
        Test("edits at the same native selection invalidate the repeated sample", () =>
        {
            using var fixture = new NativeFixture(); var copies = 0;
            var result = SelectedMacroReader.CaptureStable(() => fixture.Owner, owner =>
            {
                if (++copies == 2) ((byte*)fixture.Selected->Lines[14].StringPtr)[1] = (byte)'x';
                return SelectedMacroReader.Copy(owner);
            });
            Check(result is null && copies == 2);
        });
        Test("observation strings and line collection are detached and read-only", () =>
        {
            var sample = Sample(); var result = Capture(sample)!;
            sample.Buffers[0].Bytes[0] = (byte)'X'; sample.Buffers[1].Bytes[0] = (byte)'X';
            Check(result.Name == "Sample" && result.Lines[0] == "/echo first");
            try { ((IList)result.Lines)[0] = "changed"; throw new Exception("Mutable output"); }
            catch (NotSupportedException) { }
            Check(typeof(SelectedMacroObservation).GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(p => p.Name).Order().SequenceEqual(new[] { "CapturedAtUtc", "Index", "Lines", "Name", "Set" }));
        });
        Console.WriteLine($"{groups} selected macro groups passed; synthetic only, selected-record game comparison remains required.");
    }

    private static void Test(string name, Action action) { action(); groups++; Console.WriteLine("PASS " + name); }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Fixture assertion failed."); }

    private sealed class NativeFixture : IDisposable
    {
        internal readonly PlayerState* Player = (PlayerState*)NativeMemory.AllocZeroed((nuint)sizeof(PlayerState));
        internal readonly AgentMacro* Agent = (AgentMacro*)NativeMemory.AllocZeroed((nuint)sizeof(AgentMacro));
        internal readonly AddonMacro* Addon = (AddonMacro*)NativeMemory.AllocZeroed((nuint)sizeof(AddonMacro));
        internal readonly RaptureMacroModule* Macros = (RaptureMacroModule*)NativeMemory.AllocZeroed((nuint)sizeof(RaptureMacroModule));
        internal readonly RaptureMacroModule.Macro* Selected;
        internal SelectedMacroOwner Owner { get; }
        private readonly List<nint> buffers = [];

        internal NativeFixture(int set = 0, int index = 0, bool blank = false)
        {
            Player->IsLoaded = true; Player->ContentId = 42;
            Macros->CharacterContentId = 42;
            Agent->AddonId = 6; Agent->SelectedMacroSet = (uint)set; Agent->SelectedMacroIndex = (uint)index;
            Addon->Id = 6;
            var selectedSet = set == 0 ? Macros->Individual : Macros->Shared;
            Selected = (RaptureMacroModule.Macro*)Unsafe.AsPointer(ref selectedSet[index]);
            SetText(&Selected->Name, blank ? "" : "Native");
            for (var line = 0; line < 15; line++) SetText((Utf8String*)Unsafe.AsPointer(ref Selected->Lines[line]), blank ? "" : Lines[line]);
            Owner = new((nint)Player, 42, 16, 24, (nint)Agent, (nint)Addon, 6, (nint)Macros, set, index);
        }

        private void SetText(Utf8String* text, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value + "\0");
            var allocation = (byte*)NativeMemory.Alloc((nuint)bytes.Length + 1);
            var buffer = allocation + 1; // Deliberately unaligned byte data remains valid.
            bytes.CopyTo(new Span<byte>(buffer, bytes.Length)); buffers.Add((nint)allocation);
            text->StringPtr = buffer; text->BufUsed = bytes.Length; text->BufSize = bytes.Length;
            text->StringLength = long.MaxValue; // Deliberately not used for a bounded byte copy.
        }

        public void Dispose()
        {
            foreach (var buffer in buffers) NativeMemory.Free((void*)buffer);
            NativeMemory.Free(Player); NativeMemory.Free(Agent); NativeMemory.Free(Addon); NativeMemory.Free(Macros);
        }
    }
}
