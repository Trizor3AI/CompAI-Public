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
if (args is ["--public-catalog", var path, var registered, var absent]) Checks.PublicCatalog(path, registered, absent);
if (args is ["--produce-fixtures", var output]) Checks.Produce(output);

internal static unsafe class Checks
{
    private static int passed;
    private static readonly FieldRecordSelection Selection = new(50, 0, 0);
    private static FieldRecordView View() => new(Selection, "01.", "Record 1", null, "Total: 23/50");
    private static FieldRecordDefinition[] Catalog() => Enumerable.Range(1, 50)
        .Select(number => new FieldRecordDefinition((uint)number, number, "Record " + number)).ToArray();
    private static FieldRecordOwner Owner() => new(1, 2, 3, 4, 5, 6, 7, Selection);
    private static ActivityEntry[]? Describe(FieldRecordView view) => FieldRecordReader.DescribeRows(view, Catalog());

    internal static void Run()
    {
        Test("only selected display and total survive projection", () =>
        {
            var rows = Describe(View());
            Check(rows is { Length: 4 } && rows[0].Value == 1 && rows[1].Text == "Record 1" &&
                rows[2].Text == FieldRecordContract.Registered && rows[3].Value == 23);
            Check(rows!.All(row => row.ReferenceId is null && row.ReferenceKind is null));
            var missing = Describe(View() with { Number = "02.", Name = "???", Absent = "No record available" });
            Check(missing is { Length: 4 } && missing[1].Text == "???" && missing[2].Text == FieldRecordContract.Unavailable);
            Check(!JsonSerializer.Serialize(missing).Contains("Record 2"));
        });
        Test("public displayed number never aliases raw selection or row ID", () =>
        {
            var catalog = Catalog();
            catalog[8] = new(13, 9, "Record Nine"); catalog[12] = new(9, 13, "Record Thirteen");
            var rows = FieldRecordReader.DescribeRows(View() with { Number = "09.", Name = "Record Nine", Selection = new(50, 12, 1) }, catalog);
            Check(rows is not null && rows[0].Value == 9 && rows[1].Text == "Record Nine");
            Check(FieldRecordReader.DescribeRows(View() with { Number = "09.", Name = "Record Thirteen" }, catalog) is null);
        });
        Test("contradictory status and total do not become registration", () =>
        {
            foreach (var view in new[]
            {
                View() with { Name = "???" }, View() with { Absent = "No record available" },
                View() with { Absent = "Loading" }, View() with { Name = "Unmatched record" },
                View() with { Total = "Total: 0/50" },
                View() with { Name = "???", Absent = "No record available", Total = "Total: 50/50" },
            }) Check(Describe(view) is null);
        });
        Test("unsupported numbers, selection bounds and total formats fail closed", () =>
        {
            foreach (var number in new[] { "1.", "00.", "51.", "+1.", "01", "０１.", "01.\n" })
                Check(Describe(View() with { Number = number }) is null);
            foreach (var total in new[] { "Total: -1/50", "Total: 51/50", "Total: 23/49", "Total: 23/50/1", "Total: 23/50\n", "Gesamt: 23/50" })
                Check(Describe(View() with { Total = total }) is null);
            foreach (var selection in new[] { new FieldRecordSelection(49, 0, 0), new(50, -1, 0), new(50, 50, 0), new(50, 0, -1), new(50, 0, 5) })
                Check(Describe(View() with { Selection = selection }) is null);
        });
        Test("malformed public catalogs cannot corroborate titles", () =>
        {
            var catalog = Catalog(); catalog[1] = catalog[0];
            Check(FieldRecordReader.DescribeRows(View(), catalog) is null);
            Check(FieldRecordReader.DescribeRows(View(), Catalog().Skip(1)) is null);
            catalog = Catalog(); catalog[0] = catalog[0] with { Name = "???" };
            Check(FieldRecordReader.DescribeRows(View(), catalog) is null);
            Check(FieldRecordReader.DescribeRows(View(), Catalog().Append(new(51, 0, ""))) is not null);
            Check(FieldRecordReader.DescribeRows(View(), Catalog().Append(new(51, 0, "Unexpected title"))) is null);
            Check(FieldRecordReader.DescribeRows(View(), Catalog().Concat(Enumerable.Range(51, 6).Select(id => new FieldRecordDefinition((uint)id, 0, "")))) is null);
        });
        Test("character, selection, display and catalog changes reject capture", () =>
        {
            Check(FieldRecordReader.CaptureRows(() => Owner(), _ => View(), Catalog(), () => true) is not null);
            Check(FieldRecordReader.CaptureRows(() => null, _ => throw new Exception("Must not copy"), Catalog(), () => true) is null);
            var reads = 0;
            Check(FieldRecordReader.CaptureRows(() => ++reads == 1 ? Owner() : Owner() with { ContentId = 3 }, _ => View(), Catalog(), () => true) is null);
            reads = 0;
            Check(FieldRecordReader.CaptureRows(() => Owner(), _ => ++reads == 1 ? View() : View() with { Total = "Total: 24/50" }, Catalog(), () => true) is null);
            Check(FieldRecordReader.CaptureRows(() => Owner(), _ => View() with { Selection = new(50, 1, 0) }, Catalog(), () => true) is null);
            Check(FieldRecordReader.CaptureRows(() => Owner(), _ => View(), Catalog(), () => false) is null);
        });
        Test("native copier ignores biography and respects exact owned text paths", () =>
        {
            using var fixture = new Fixture();
            Check(FieldRecordReader.CopyView(fixture.Addon) == View());
            fixture.Name->ParentNode = fixture.TotalParent;
            Check(FieldRecordReader.CopyView(fixture.Addon) is null);
            fixture.Name->ParentNode = fixture.DetailParent;
            fixture.DetailParent->NodeFlags = 0;
            Check(FieldRecordReader.CopyView(fixture.Addon) is null);
            fixture.DetailParent->NodeFlags = NodeFlags.Visible;
            fixture.DetailParent->ParentNode = fixture.DetailParent;
            Check(FieldRecordReader.CopyView(fixture.Addon) is null);
        });
        Test("duplicate targets, foreign ancestry, type and layout bounds fail closed", () =>
        {
            using var fixture = new Fixture();
            fixture.Nodes(fixture.AllNodes.Append((nint)fixture.Name).ToArray());
            Check(FieldRecordReader.CopyView(fixture.Addon) is null);
            fixture.Nodes(fixture.AllNodes);
            fixture.Name->Type = NodeType.Image;
            Check(FieldRecordReader.CopyView(fixture.Addon) is null);
            fixture.Name->Type = NodeType.Text;
            var foreign = fixture.Node(100);
            fixture.DetailParent->ParentNode = foreign;
            Check(FieldRecordReader.CopyView(fixture.Addon) is null);
            fixture.DetailParent->ParentNode = null;
            fixture.Addon->UldManager.NodeListCount = 513;
            Check(FieldRecordReader.CopyView(fixture.Addon) is null);
        });
        Test("oversized, malformed, unterminated text never produces a result", () =>
        {
            using var fixture = new Fixture();
            fixture.Name->NodeText.BufUsed = 258; fixture.Name->NodeText.BufSize = 258;
            fixture.Name->NodeText.StringPtr = (byte*)1;
            Check(FieldRecordReader.CopyView(fixture.Addon) is null);
            fixture.SetText(fixture.Name, [0xc3, 0x28]);
            Check(FieldRecordReader.CopyView(fixture.Addon) is null);
            fixture.SetText(fixture.Name, Encoding.UTF8.GetBytes("Record 1"));
            ((byte*)fixture.Name->NodeText.StringPtr)[fixture.Name->NodeText.BufUsed - 1] = 1;
            Check(FieldRecordReader.CopyView(fixture.Addon) is null);
            fixture.SetText(fixture.Name, Encoding.UTF8.GetBytes("Record\n1"));
            Check(FieldRecordReader.CopyView(fixture.Addon) is null);
        });
        Test("hidden absence buffer is not read and visible absence remains explicit", () =>
        {
            using var fixture = new Fixture();
            fixture.Absent->NodeText.StringPtr = (byte*)1;
            Check(FieldRecordReader.CopyView(fixture.Addon) == View());
            fixture.SetText(fixture.Absent, Encoding.UTF8.GetBytes("No record available"));
            fixture.Absent->NodeFlags = NodeFlags.Visible;
            fixture.SetText(fixture.Name, Encoding.UTF8.GetBytes("???"));
            Check(Describe(FieldRecordReader.CopyView(fixture.Addon)!)![2].Text == FieldRecordContract.Unavailable);
        });
        Console.WriteLine($"Passed {passed} Field Record checks.");
    }

    internal static void Produce(string path)
    {
        var time = DateTimeOffset.Parse("2026-10-25T12:00:00Z");
        var observed = new ActivitySnapshot(FieldRecordContract.Category, time, true, Describe(View())!);
        var absent = new ActivitySnapshot(FieldRecordContract.Category, time, true,
            Describe(View() with { Number = "02.", Name = "???", Absent = "No record available" })!);
        var arguments = JsonDocument.Parse("{\"category\":\"field_record\"}").RootElement.Clone();
        var cases = new[]
        {
            ("registered", observed), ("uncollected", absent), ("last_observed", observed.AsLastObserved()),
            ("unavailable", ActivitySnapshot.Unavailable(FieldRecordContract.Category, "No stable owned Field Record display.")),
        };
        var fixtures = cases.Select(item => new { name = item.Item1, arguments, result = new ActivityLookup(item.Item2).Query(arguments) });
        var fullPath = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(fixtures, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Produced Field Record C# wire fixtures.");
    }

    internal static void PublicCatalog(string path, string registered, string absent)
    {
        using var game = new Lumina.GameData(path);
        var raw = game.Excel.GetSheet<Lumina.Excel.Sheets.MYCWarResultNotebook>()
            .Where(row => row.RowId != 0).Select(row => new FieldRecordDefinition(row.RowId, row.Number, row.Name.ExtractText().Trim())).ToArray();
        var definitions = FieldRecordReader.BuildCatalog(raw);
        foreach (var capture in new[] { registered, absent })
        {
            using var document = JsonDocument.Parse(File.ReadAllText(capture));
            var root = document.RootElement;
            var selection = root.GetProperty("field_record_selection");
            string? Text(int id) => root.GetProperty("text_nodes").EnumerateArray().Where(row =>
                row.GetProperty("node_id").GetInt32() == id && row.GetProperty("component_path").GetArrayLength() == 0)
                .Select(row => row.GetProperty("text").GetString()).SingleOrDefault();
            var view = new FieldRecordView(new(selection.GetProperty("max_note_index").GetInt32(),
                selection.GetProperty("current_note_index").GetInt32(), selection.GetProperty("current_page_index").GetInt32()),
                Text(43)!, Text(44)!, Text(45), Text(28)!);
            var rows = FieldRecordReader.DescribeRows(view, definitions);
            Check(rows is { Length: 4 });
        }
        Check(definitions.Single(row => row.Id == 13).Number == 9);
        Console.WriteLine("PASS installed public Number/name mapping and both supplied display captures; no native capture performed.");
    }

    private static void Test(string name, Action check) { check(); ++passed; Console.WriteLine("PASS " + name); }
    private static void Check(bool value) { if (!value) throw new Exception("Field Record assertion failed."); }

    private sealed class Fixture : IDisposable
    {
        private readonly List<nint> allocations = [];
        internal readonly AddonMYCWarResultNotebook* Addon;
        internal readonly AtkTextNode* Name;
        internal readonly AtkTextNode* Absent;
        internal readonly AtkResNode* DetailParent;
        internal readonly AtkResNode* TotalParent;
        internal readonly nint[] AllNodes;
        internal Fixture()
        {
            Addon = Allocate<AddonMYCWarResultNotebook>(); Addon->MaxNoteIndex = 50;
            DetailParent = Node(34); TotalParent = Node(6);
            Name = Text(44, "Record 1", DetailParent);
            Absent = Text(45, "No record available", DetailParent); Absent->NodeFlags = 0;
            var number = Text(43, "01.", DetailParent);
            var total = Text(28, "Total: 23/50", TotalParent);
            var biography = Allocate<AtkComponentNode>(); biography->NodeId = 46;
            biography->Type = (NodeType)1000; biography->Component = (AtkComponentBase*)1;
            biography->NodeFlags = NodeFlags.Visible; biography->Color.A = 255;
            AllNodes = [(nint)DetailParent, (nint)TotalParent, (nint)Name, (nint)Absent, (nint)number, (nint)total, (nint)biography];
            Nodes(AllNodes);
        }
        private T* Allocate<T>() where T : unmanaged
        {
            var pointer = (T*)NativeMemory.AllocZeroed((nuint)sizeof(T)); allocations.Add((nint)pointer); return pointer;
        }
        internal AtkResNode* Node(uint id)
        {
            var node = Allocate<AtkResNode>(); node->NodeId = id; node->NodeFlags = NodeFlags.Visible; node->Color.A = 255; return node;
        }
        private AtkTextNode* Text(uint id, string value, AtkResNode* parent)
        {
            var node = Allocate<AtkTextNode>(); node->NodeId = id; node->ParentNode = parent; node->Type = NodeType.Text;
            node->NodeFlags = NodeFlags.Visible; node->Color.A = 255; SetText(node, Encoding.UTF8.GetBytes(value)); return node;
        }
        internal void SetText(AtkTextNode* node, byte[] bytes)
        {
            var buffer = (byte*)NativeMemory.AllocZeroed((nuint)(bytes.Length + 1)); allocations.Add((nint)buffer);
            bytes.CopyTo(new Span<byte>(buffer, bytes.Length)); node->NodeText.StringPtr = buffer;
            node->NodeText.StringLength = bytes.Length; node->NodeText.BufUsed = node->NodeText.BufSize = bytes.Length + 1;
        }
        internal void Nodes(nint[] values)
        {
            var list = (AtkResNode**)NativeMemory.AllocZeroed((nuint)(values.Length * IntPtr.Size)); allocations.Add((nint)list);
            for (var index = 0; index < values.Length; ++index) list[index] = (AtkResNode*)values[index];
            Addon->UldManager.NodeList = list; Addon->UldManager.NodeListCount = (ushort)values.Length;
        }
        public void Dispose() { foreach (var allocation in allocations) NativeMemory.Free((void*)allocation); }
    }
}
