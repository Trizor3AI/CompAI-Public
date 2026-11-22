using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using CompAI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

var libraryPath = Environment.GetEnvironmentVariable("DALAMUD_LIB_PATH") ??
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "addon", "Hooks", "dev");
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var path = Path.Combine(libraryPath, name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
Checks.Run();
if (args is ["--produce-fixtures", var output]) Checks.Produce(output);

internal static unsafe class Checks
{
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-10-25T19:00:00Z");
    private static readonly MahjongDisplay Display = new("Synthetic rank", "15 points", "5 pts", "3", "1600", "1650");
    private static int passed;
    internal static void Run()
    {
        GoldSaucerChildDiagnosticChecks.Run();
        Test("six typed visible fields copy with stable ownership and exact display values", () =>
        {
            using var fixture = new Fixture();
            Check(fixture.Copy(out var first) && fixture.Copy(out var second) && MahjongReader.SamePanel(first, second));
            Check(first!.Display == Display);
        });
        Test("screenshot layout binds current points to node9 and next-rank text to node11", () =>
        {
            using var fixture = new Fixture();
            fixture.SetText(0, "Novice");
            Check(fixture.Addon->Points->NodeId == 9 && fixture.Addon->NextRankPoints->NodeId == 11);
            Check(MahjongReader.TryCopyPanel(fixture.Addon, out var panel, out var diagnostic) && diagnostic.status == "panel_ready");
            var snapshot = MahjongReader.Describe(Time, panel!.Display, panel.Display);
            Check(snapshot.Available && snapshot.Entries[0].Text == "Novice" &&
                snapshot.Entries[1].Name == "Points (displayed)" && snapshot.Entries[1].Text == "15 points" &&
                snapshot.Entries[2].Name == "Next rank (displayed)" && snapshot.Entries[2].Text == "5 pts");
            Check(snapshot.Entries.All(row => row.Value is null && row.Required is null));
            // Reversing the typed pointers reproduces the old wrong association;
            // ownership alone must not allow those two fields to trade meanings.
            fixture.Addon->Points = fixture.Texts + 2; fixture.Addon->NextRankPoints = fixture.Texts + 1;
            Check(!MahjongReader.TryCopyPanel(fixture.Addon, out var rejected, out var mismatch) && rejected is null &&
                mismatch.status == "field_node_id_mismatch" && mismatch.field == "points");
            Check(!fixture.Copy(out _));
        });
        Test("wrong child identity readiness visibility and root reject before text", () =>
        {
            using var fixture = new Fixture();
            fixture.Addon->NameString = "WrongPanel"; Check(!fixture.Copy(out _)); fixture.Addon->NameString = "GSInfoEmj";
            fixture.Addon->Id = 0; Check(!fixture.Copy(out _)); fixture.Addon->Id = 75;
            fixture.Addon->Flags1A1 = 0; Check(!fixture.Copy(out _)); fixture.Addon->Flags1A1 = 1;
            fixture.Addon->IsVisible = false; Check(!fixture.Copy(out _)); fixture.Addon->IsVisible = true;
            fixture.Addon->Alpha = 0; Check(!fixture.Copy(out _)); fixture.Addon->Alpha = 255;
            fixture.Addon->RootNode = (AtkResNode*)1; Check(!fixture.Copy(out _));
        });
        Test("opaque external typed pointers aliases wrong node IDs and parent IDs fail closed", () =>
        {
            using var fixture = new Fixture();
            fixture.Addon->Rank = (AtkTextNode*)1; Check(!fixture.Copy(out _)); fixture.Addon->Rank = fixture.Texts;
            fixture.Addon->Rank = fixture.Texts + 1; Check(!fixture.Copy(out _)); fixture.Addon->Rank = fixture.Texts;
            fixture.Texts[0].NodeId = 66; Check(!fixture.Copy(out _)); fixture.Texts[0].NodeId = 7;
            fixture.Texts[0].ParentNode = fixture.Groups + 1; Check(!fixture.Copy(out _));
        });
        Test("ancestor visibility opacity cycles and external ancestry cannot authorize text", () =>
        {
            using var fixture = new Fixture();
            fixture.Groups[0].NodeFlags = 0; Check(!fixture.Copy(out _)); fixture.Groups[0].NodeFlags = NodeFlags.Visible;
            fixture.Groups[0].Color.A = 0; Check(!fixture.Copy(out _)); fixture.Groups[0].Color.A = 255;
            fixture.Groups[0].ParentNode = fixture.Groups; Check(!fixture.Copy(out _));
            fixture.Groups[0].ParentNode = (AtkResNode*)1; Check(!fixture.Copy(out _));
            fixture.Groups[0].ParentNode = fixture.Root;
            fixture.Root->NodeFlags = 0; Check(!fixture.Copy(out _));
        });
        Test("bounded lists require unique owned fields and a registered root", () =>
        {
            using var fixture = new Fixture();
            fixture.Addon->UldManager.NodeListCount = MahjongReader.MaxNodes + 1; Check(!fixture.Copy(out _));
            fixture.Addon->UldManager.NodeListCount = Fixture.Count;
            fixture.List[0] = null; Check(!fixture.Copy(out _)); fixture.List[0] = fixture.Root;
            fixture.Addon->RootNode = (AtkResNode*)fixture.Texts; Check(!fixture.Copy(out _)); fixture.Addon->RootNode = fixture.Root;
            fixture.List[5] = fixture.List[4]; Check(!fixture.Copy(out _));
            fixture.List[5] = (AtkResNode*)(fixture.Texts + 1);
            fixture.List[4] = null; Check(!fixture.Copy(out _));
        });
        Test("invalid bounded text buffers cannot be silently truncated or decoded", () =>
        {
            using var fixture = new Fixture();
            fixture.Texts[0].NodeText.BufUsed = MahjongReader.MaxTextBytes + 2; Check(!fixture.Copy(out _)); fixture.SetText(0, "Synthetic rank");
            fixture.Texts[0].NodeText.BufSize = 1; Check(!fixture.Copy(out _)); fixture.SetText(0, "Synthetic rank");
            fixture.Texts[0].NodeText.StringPtr = (byte*)1; Check(!fixture.Copy(out _)); fixture.SetText(0, "Synthetic rank");
            fixture.SetText(0, ""); Check(!fixture.Copy(out _));
            fixture.SetText(0, "  "); Check(!fixture.Copy(out _));
            fixture.SetText(0, new string('x', 65)); Check(!fixture.Copy(out _));
            fixture.SetText(0, "bad\ntext"); Check(!fixture.Copy(out _));
            fixture.SetText(0, "bad\0text"); Check(!fixture.Copy(out _));
            fixture.SetText(0, "bad"); fixture.Buffers[0][3] = (byte)'x'; Check(!fixture.Copy(out _));
            fixture.SetText(0, "bad"); fixture.Buffers[0][0] = 0xFF; Check(!fixture.Copy(out _));
        });
        Test("display strings retain local formatting and do not depend on stale StringLength", () =>
        {
            using var fixture = new Fixture();
            fixture.SetText(0, "雀士"); fixture.SetText(4, "1.600"); fixture.Texts[0].NodeText.StringLength = 1;
            Check(fixture.Copy(out var copied) && copied!.Display.Rank == "雀士" && copied.Display.CurrentRating == "1.600");
        });
        Test("changed values buffers IDs and node identities invalidate repeated copies", () =>
        {
            using var fixture = new Fixture();
            Check(fixture.Copy(out var first));
            fixture.SetText(4, "1601"); Check(fixture.Copy(out var changed) && !MahjongReader.SamePanel(first, changed));
            fixture.SetText(4, "1600"); Check(fixture.Copy(out var rebound) && !MahjongReader.SamePanel(first, rebound));
            Check(!MahjongReader.SamePanel(first, first! with { Header = first.Header with { Id = 99 } }));
            Check(!MahjongReader.SamePanel(first, first! with { NodeList = [.. first.NodeList.Reverse()] }));
        });
        Test("player agent parent tab and linked-child identities must match", () =>
        {
            var child = new GoldSaucerChildObservation(600000, "GSInfoEmj", "one_visible_owned_child",
                [new(0, "GSInfoEmj", 75, 0, 0, 19, true, true)]);
            var owner = new MahjongObservation(100000, 123, 200000, 300000, 400000, 500000, 74, 5, 0, 75, child);
            Check(MahjongReader.SameObservation(owner, owner));
            foreach (var changed in new[] { owner with { Player = 1 }, owner with { Owner = 124 }, owner with { Module = 1 },
                owner with { Manager = 1 }, owner with { Agent = 1 }, owner with { Parent = 1 }, owner with { ParentId = 73 },
                owner with { Category = 4 }, owner with { SubCategory = 1 }, owner with { ChildId = 76 },
                owner with { Child = child with { TextAddon = 700000 } },
                owner with { Child = child with { Status = "multiple_visible_owned_children" } } })
                Check(!MahjongReader.SameObservation(owner, changed));
        });
        Test("six-row snapshot preserves next-rank wording without numerical progress", () =>
        {
            var snapshot = MahjongReader.Describe(Time, Display, Display);
            Check(snapshot.Available && snapshot.Entries.Count == 6 && MahjongContract.ValidShape(snapshot.Entries.ToArray()));
            Check(snapshot.Entries[1].Text == "15 points" && snapshot.Entries[2].Text == "5 pts");
            Check(snapshot.Entries.All(row => row.Value is null && row.Required is null && row.Unit is null));
            var zero = Display with { MatchesPlayed = "0" };
            Check(MahjongReader.Describe(Time, zero, zero).Entries[3].Text == "0");
            Check(!MahjongReader.Describe(Time, Display, Display with { Points = "6 pts" }).Available);
            Check(!MahjongReader.Describe(Time, Display with { Rank = "" }, Display with { Rank = "" }).Available);
        });
        Test("contract rejects extra counters missing rows duplicate rows and fabricated progress", () =>
        {
            var rows = MahjongReader.Describe(Time, Display, Display).Entries.ToArray();
            Check(!MahjongContract.ValidShape(rows[..5]));
            Check(!MahjongContract.ValidShape([.. rows, new(7, "statistics", "NPC wins", Text: "0")]));
            Check(!MahjongContract.ValidShape([rows[0], .. rows[..5]]));
            foreach (var wrong in new[] { rows[0] with { Name = "Wrong" }, rows[0] with { Group = "npc_matches" },
                rows[0] with { Value = 5 }, rows[0] with { Required = 15 }, rows[0] with { State = "completed" },
                rows[0] with { Unit = "points" }, rows[0] with { ReferenceId = 1, ReferenceKind = "item" }, rows[0] with { Slot = 1 },
                rows[0] with { Text = new string('x', 65) } })
            {
                Check(!MahjongContract.ValidEntry(wrong));
                Throws(() => new ActivitySnapshot("mahjong", Time, true, [wrong, .. rows.Skip(1)]));
            }
        });
        Test("lookup projects only six fields with truthful filtering and historical readiness", () =>
        {
            var snapshot = MahjongReader.Describe(Time, Display, Display);
            var lookup = new ActivityLookup(snapshot);
            var result = lookup.Query(Args("""{"category":"mahjong"}"""));
            Check(result.entry_count == 6 && result.entries.Length == 6 && result.source == MahjongContract.Source);
            var json = JsonSerializer.Serialize(result);
            foreach (var privateName in new[] { "NodeList", "Buffer", "Raw", "ContentId", "Owner", "0x" }) Check(!json.Contains(privateName));
            Check(lookup.Query(Args("""{"category":"mahjong","query":"rating"}""")).entries.Length == 2);
            Check(lookup.Query(Args("""{"category":"mahjong","query":"npc"}""")).entries.Length == 0);
            Check(new ActivityLookup(snapshot.AsLastObserved()).Query(Args("""{"category":"mahjong"}""")).readiness == "last_observed_menu");
        });
        Test("human diagnostic shares successful capture and exposes only fixed bounded metadata", () =>
        {
            using var fixture = new Fixture();
            var child = new GoldSaucerChildObservation((nint)fixture.Addon, "GSInfoEmj", "one_visible_owned_child",
                [new(0, "GSInfoEmj", 75, 0, 0, 0, true, true)]);
            var owner = new MahjongObservation(100000, 123, 200000, 300000, 400000, 500000, 74, 5, 0, 75, child);
            var snapshot = MahjongReader.CaptureCore(() => (owner, new("compai_mahjong_reader_v1", "mahjong", "owner_ready", null, null)),
                address =>
                {
                    var accepted = MahjongReader.TryCopyPanel((AddonGSInfoEmj*)address, out var panel, out var result);
                    return (accepted ? panel : null, result);
                }, out var diagnostic);
            Check(snapshot.Available && diagnostic.status == "ready" && diagnostic.field is null && diagnostic.node_count == Fixture.Count);
            Check(fixture.Copy(out var ordinary) && snapshot.Entries.Select(row => row.Text).SequenceEqual(new[]
                { ordinary!.Display.Rank, ordinary.Display.Points, ordinary.Display.NextRank, ordinary.Display.MatchesPlayed, ordinary.Display.CurrentRating, ordinary.Display.HighestRating }));
            var json = JsonSerializer.Serialize(diagnostic);
            using var document = JsonDocument.Parse(json);
            Check(document.RootElement.EnumerateObject().Select(property => property.Name).SequenceEqual(
                ["schema", "category", "status", "field", "node_count"]));
            Check(diagnostic.schema == "compai_mahjong_reader_v1" && diagnostic.category == "mahjong");
            foreach (var privateValue in new[] { Display.Rank, Display.Points, Display.NextRank, "Buffer", "Owner", "Player", "Raw", "NodeList" })
                Check(!json.Contains(privateValue));
        });
        Test("diagnostic pinpoints rejected field and ownership without bypassing capture guards", () =>
        {
            using var fixture = new Fixture();
            fixture.Texts[4].NodeId = 99;
            Check(!MahjongReader.TryCopyPanel(fixture.Addon, out var failed, out var fieldFailure) && failed is null &&
                fieldFailure.status == "field_node_id_mismatch" && fieldFailure.field == "current_rating" && fieldFailure.node_count == Fixture.Count);
            Check(!fixture.Copy(out _));
            fixture.Texts[4].NodeId = 23;
            fixture.Groups[0].NodeFlags = 0;
            Check(!MahjongReader.TryCopyPanel(fixture.Addon, out _, out var hidden) && hidden.status == "field_or_ancestor_hidden" && hidden.field == "rank");
            fixture.Groups[0].NodeFlags = NodeFlags.Visible;
            fixture.Addon->UldManager.NodeListCount = MahjongReader.MaxNodes + 1;
            Check(!MahjongReader.TryCopyPanel(fixture.Addon, out _, out var overBound) && overBound.status == "node_list_bounds" && overBound.node_count is null);
            var copies = 0;
            var unavailable = MahjongReader.CaptureCore(() => (null, new("compai_mahjong_reader_v1", "mahjong", "mahjong_tab_not_selected", null, null)),
                _ => { ++copies; throw new Exception("Other tab must not be copied"); }, out var notSelected);
            Check(!unavailable.Available && copies == 0 && notSelected.status == "mahjong_tab_not_selected");
        });
        Console.WriteLine($"Passed {passed} Mahjong reader groups plus 8 owned-child groups.");
    }

    internal static void Produce(string output)
    {
        var snapshot = MahjongReader.Describe(Time, Display, Display);
        object Fixture(string name, ActivitySnapshot value, string query)
        { var arguments = Args(query); return new { name, arguments, result = new ActivityLookup(value).Query(arguments) }; }
        var fixtures = new[]
        {
            Fixture("mahjong_display", snapshot, """{"category":"mahjong"}"""),
            Fixture("mahjong_filtered", snapshot, """{"category":"mahjong","query":"rating"}"""),
            Fixture("mahjong_empty_filter", snapshot, """{"category":"mahjong","query":"npc"}"""),
            Fixture("mahjong_last_observed", snapshot.AsLastObserved(), """{"category":"mahjong"}"""),
            Fixture("mahjong_unavailable", ActivitySnapshot.Unavailable("mahjong", "Synthetic unavailable panel"), """{"category":"mahjong"}"""),
        };
        File.WriteAllText(output, JsonSerializer.Serialize(fixtures));
        Console.WriteLine($"Wrote {fixtures.Length} synthetic C# Mahjong lookup fixtures");
    }

    private static JsonElement Args(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
    private static void Test(string name, Action action) { action(); ++passed; Console.WriteLine("PASS " + name); }
    private static void Check(bool value) { if (!value) throw new Exception("Mahjong fixture assertion failed"); }
    private static void Throws(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Expected invalid contract rejection"); }

    private sealed class Fixture : IDisposable
    {
        internal const ushort Count = 10;
        internal readonly AddonGSInfoEmj* Addon;
        internal readonly AtkResNode* Root;
        internal readonly AtkResNode* Groups;
        internal readonly AtkTextNode* Texts;
        internal readonly AtkResNode** List;
        internal readonly byte*[] Buffers = new byte*[6];
        private readonly List<nint> allocatedText = [];
        internal Fixture()
        {
            Addon = (AddonGSInfoEmj*)NativeMemory.AllocZeroed((nuint)sizeof(AddonGSInfoEmj));
            Root = (AtkResNode*)NativeMemory.AllocZeroed((nuint)sizeof(AtkResNode));
            Groups = (AtkResNode*)NativeMemory.AllocZeroed(3, (nuint)sizeof(AtkResNode));
            Texts = (AtkTextNode*)NativeMemory.AllocZeroed(6, (nuint)sizeof(AtkTextNode));
            List = (AtkResNode**)NativeMemory.AllocZeroed(Count, (nuint)sizeof(AtkResNode*));
            Addon->Id = 75; Addon->NameString = "GSInfoEmj"; Addon->Flags1A1 = 1; Addon->IsVisible = true; Addon->Alpha = 255;
            Addon->RootNode = Root; Addon->UldManager.NodeList = List; Addon->UldManager.NodeListCount = Count;
            Root->NodeId = 1; Root->NodeFlags = NodeFlags.Visible; Root->Color.A = 255; List[0] = Root;
            var ids = new uint[] { 4, 13, 18 };
            for (var i = 0; i < 3; ++i)
            {
                Groups[i].NodeId = ids[i]; Groups[i].ParentNode = Root; Groups[i].NodeFlags = NodeFlags.Visible; Groups[i].Color.A = 255;
                List[i + 1] = Groups + i;
            }
            var nodeIds = new uint[] { 7, 9, 11, 16, 23, 24 };
            var values = new[] { Display.Rank, Display.Points, Display.NextRank, Display.MatchesPlayed, Display.CurrentRating, Display.HighestRating };
            for (var i = 0; i < 6; ++i)
            {
                Texts[i].NodeId = nodeIds[i]; Texts[i].Type = NodeType.Text; Texts[i].NodeFlags = NodeFlags.Visible; Texts[i].Color.A = 255;
                Texts[i].ParentNode = Groups + (i < 3 ? 0 : i == 3 ? 1 : 2); List[i + 4] = (AtkResNode*)(Texts + i); SetText(i, values[i]);
            }
            Addon->Rank = Texts; Addon->Points = Texts + 1; Addon->NextRankPoints = Texts + 2;
            Addon->MatchesPlayed = Texts + 3; Addon->CurrentRating = Texts + 4; Addon->HighestRating = Texts + 5;
        }
        internal void SetText(int index, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var buffer = (byte*)NativeMemory.AllocZeroed((nuint)bytes.Length + 1);
            bytes.CopyTo(new Span<byte>(buffer, bytes.Length)); allocatedText.Add((nint)buffer); Buffers[index] = buffer;
            Texts[index].NodeText.StringPtr = buffer; Texts[index].NodeText.BufSize = Texts[index].NodeText.BufUsed = bytes.Length + 1;
            Texts[index].NodeText.StringLength = bytes.Length;
        }
        internal bool Copy(out MahjongPanelCopy? copy) => MahjongReader.TryCopyPanel(Addon, out copy);
        public void Dispose()
        {
            foreach (var buffer in allocatedText) NativeMemory.Free((void*)buffer);
            NativeMemory.Free(Addon); NativeMemory.Free(Root); NativeMemory.Free(Groups); NativeMemory.Free(Texts); NativeMemory.Free(List);
        }
    }
}
