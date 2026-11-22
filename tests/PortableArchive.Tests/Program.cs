using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using CompAI;
using FFXIVClientStructs.FFXIV.Component.GUI;

var libraryPath = Environment.GetEnvironmentVariable("DALAMUD_LIB_PATH") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "addon", "Hooks", "dev");
AssemblyLoadContext.Default.Resolving += (context, name) => { var path = Path.Combine(libraryPath, name.Name + ".dll"); return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null; };
Checks.Run();
if (args is ["--produce-fixture", var output]) Checks.Produce(output);
if (args is ["--verify-public-capture", var sqpack, var capture]) Checks.VerifyPublicCapture(sqpack, capture);

internal static unsafe class Checks
{
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-10-25T12:00:00Z");
    private static PortableArchiveDefinition[] Definitions() => new uint[] { 12, 22, 37 }
        .SelectMany(page => Enumerable.Range(0, page == 37 ? 6 : 11).Select(index =>
            new PortableArchiveDefinition(page, (ushort)index, page * 100 + (uint)index,
                $"Synthetic archive {page}/{index}"))).ToArray();
    private static PortableArchiveView View() => new("Synthetic archive 12/0");
    private static readonly PortableArchiveOwner Owner = new(1, 42, 2, 3, 4, 5, 6);

    internal static void Run()
    {
        var definitions = Definitions();
        var rows = PortableArchiveReader.DescribeRows(View(), definitions)!;
        Check(rows.Length == 2 && rows[0].ReferenceId == 1200 && rows[1].ReferenceId == 12 && rows[1].Value == 0);
        Check(PortableArchiveReader.DescribeRows(View() with { Title = "Synthetic archive 12/..." }, definitions) is null);
        Check(PortableArchiveReader.DescribeRows(new("???"), definitions) is null);
        Check(PortableArchiveReader.DescribeRows(new("Unknown title"), definitions) is null);
        Check(PortableArchiveReader.Normalize(" First\r\n second\t third ") == "First second third");
        foreach (var definition in definitions)
        {
            var identified = PortableArchiveReader.DescribeRows(new(definition.Title), definitions);
            Check(identified is { Length: 2 } && identified[0].Text == definition.Title &&
                identified[0].ReferenceId == definition.TitleId && identified[1].ReferenceId == definition.PageId &&
                identified[1].Value == definition.SubrowId && identified.All(row => row.State == "observed"));
        }
        Console.WriteLine("PASS all28 unique full titles identify selected references; clipped/unknown/placeholder titles rejected");

        foreach (var bad in new[]
        {
            definitions[..27], definitions.Append(definitions[0]).ToArray(),
            definitions.Select((row,index) => index == 0 ? row with { PageId = 13 } : row).ToArray(),
            definitions.Select((row,index) => index == 0 ? row with { SubrowId = 11 } : row).ToArray(),
            definitions.Select((row,index) => index == 1 ? row with { Title = definitions[0].Title } : row).ToArray(),
        }) Check(PortableArchiveReader.DescribeRows(View(), bad) is null);
        Check(!PortableArchiveContract.ValidShape([rows[0], rows[0]]));
        Check(!PortableArchiveContract.ValidEntry(rows[1] with { ReferenceId = 37, Value = 6 }));
        Check(!PortableArchiveContract.ValidEntry(rows[0] with { Text = "bad\ntext" }));
        Console.WriteLine("PASS invalid, duplicate, missing, future and ambiguous definitions fail closed");

        Check(PortableArchiveReader.CaptureRows(() => Owner, _ => View(), definitions, () => true) is not null);
        Check(PortableArchiveReader.CaptureRows(() => null, _ => throw new Exception("Unexpected copy"), definitions, () => true) is null);
        var observations = 0;
        Check(PortableArchiveReader.CaptureRows(() => ++observations == 1 ? Owner : Owner with { ContentId = 43 }, _ => View(), definitions, () => true) is null);
        var copies = 0;
        Check(PortableArchiveReader.CaptureRows(() => Owner, _ => ++copies == 1 ? View() : View() with { Title = "Changed" }, definitions, () => true) is null);
        Check(PortableArchiveReader.CaptureRows(() => Owner, _ => View(), definitions, () => false) is null);
        Console.WriteLine("PASS owner, selection and catalog stability guards");

        using (var fixture = new Fixture())
        {
            Check(PortableArchiveReader.CopyView(fixture.Addon) == View());
            fixture.List->Component = (AtkComponentBase*)1; // The unrelated recycled list is never traversed.
            fixture.BodyParent->Component = (AtkComponentBase*)1; // Lore is never traversed, including personalized messages.
            Check(PortableArchiveReader.CopyView(fixture.Addon) == View());
            fixture.Title->ParentNode = (AtkResNode*)fixture.BodyParent;
            Check(PortableArchiveReader.CopyView(fixture.Addon) is null);
        }
        using (var fixture = new Fixture())
        {
            fixture.Detail->NodeFlags = 0;
            Check(PortableArchiveReader.CopyView(fixture.Addon) is null);
        }
        using (var fixture = new Fixture())
        {
            fixture.Title->ParentNode = (AtkResNode*)fixture.Title;
            Check(PortableArchiveReader.CopyView(fixture.Addon) is null);
        }
        using (var fixture = new Fixture())
        {
            fixture.Title->NodeText.BufUsed = 258; fixture.Title->NodeText.BufSize = 258;
            fixture.Title->NodeText.StringPtr = (byte*)1;
            Check(PortableArchiveReader.CopyView(fixture.Addon) is null);
        }
        using (var fixture = new Fixture())
        {
            fixture.Addon->UldManager.NodeListCount = 513;
            Check(PortableArchiveReader.CopyView(fixture.Addon) is null);
        }
        Console.WriteLine("PASS fixed native detail paths, hidden/cyclic/malformed/bounded states and unrelated-list exclusion");
        using (var fixture = new Fixture("Synthetic archive 37/0", "Dear Synthetic, personalized message body."))
        {
            var selected = PortableArchiveReader.CopyView(fixture.Addon);
            Check(selected?.Title == "Synthetic archive 37/0" && PortableArchiveReader.DescribeRows(selected, definitions) is { Length: 2 });
            fixture.Body->NodeText.StringPtr = (byte*)1;
            fixture.Body->NodeText.BufUsed = long.MaxValue;
            Check(PortableArchiveReader.CopyView(fixture.Addon) == selected);
        }
        Console.WriteLine("PASS personalized or malformed lore has no effect and is never read");

        var snapshot = new ActivitySnapshot("portable_archive", Time, true, rows);
        var lookup = new ActivityLookup(snapshot);
        var result = lookup.Query(Arguments("{\"category\":\"portable_archive\"}"));
        Check(result.entry_count == 2 && result.entries.Length == 2 && result.source == PortableArchiveContract.Source);
        var page = lookup.Query(Arguments("{\"category\":\"portable_archive\",\"limit\":1}"));
        Check(page.entries.Length == 1 && page.next_offset == 1 && page.truncated);
        Check(!new ActivityLookup(snapshot.AsLastObserved()).IsUsable(Time.AddMinutes(15)));
        Console.WriteLine("PASS real snapshot, lookup, pagination and exact expiry contracts");
        Diagnostics();
        BufferLengthRegression();
    }

    private static void Diagnostics()
    {
        var plain = PortableArchiveReader.CaptureRows(() => Owner, _ => View(), Definitions(), () => true);
        var diagnosed = PortableArchiveReader.CaptureRows(() => Owner, _ => View(), Definitions(), () => true, out var success);
        Check(plain is not null && diagnosed is not null && plain.SequenceEqual(diagnosed));
        Diagnostic(success, "available", "complete", null, "passed");
        Check(PortableArchiveReader.CaptureRows(() => null, _ => throw new Exception("Unexpected copy"), Definitions(), () => true, out var noOwner) is null);
        Diagnostic(noOwner, "unavailable", "owner", "initial", "owner_unavailable");
        Check(PortableArchiveReader.CaptureRows(() => Owner, _ => View(), Definitions(), () => true, out var retainedOwner, Owner) is not null);
        Diagnostic(retainedOwner, "available", "complete", null, "passed");
        foreach (var newOwner in new PortableArchiveOwner?[] { null, Owner with { Addon = Owner.Addon + 1 } })
        {
            var staleHandleCopies = 0;
            Check(PortableArchiveReader.CaptureRows(() => newOwner, _ => { staleHandleCopies++; return View(); },
                Definitions(), () => true, out var changedBeforeCopy, Owner) is null);
            Check(staleHandleCopies == 0);
            Diagnostic(changedBeforeCopy, "unavailable", "stability", "owner_before_first_copy", "owner_changed");
        }
        Check(PortableArchiveReader.CaptureRows(() => Owner, _ => View(), Definitions(), () => false, out var changedCatalog) is null);
        Diagnostic(changedCatalog, "unavailable", "stability", "catalog", "catalog_changed");
        Check(PortableArchiveReader.CaptureRows(() => Owner, _ => View() with { Title = "Unknown title" }, Definitions(), () => true, out var noMatch) is null);
        Diagnostic(noMatch, "unavailable", "match", null, "selection_not_corroborated");
        var copies = 0;
        Check(PortableArchiveReader.CaptureRows(() => Owner, _ => ++copies == 1 ? View() : View() with { Title = "Changed selection" }, Definitions(), () => true, out var changedSelection) is null);
        Diagnostic(changedSelection, "unavailable", "stability", "selection", "selection_changed");
        Console.WriteLine("PASS diagnostic capture preserves success and owner/catalog/match/selection failures; stale retained owners are rejected before copying");

        using (var fixture = new Fixture())
        {
            Check(PortableArchiveReader.CopyView(fixture.Addon) == PortableArchiveReader.CopyView(fixture.Addon, out var diagnostic));
            Diagnostic(diagnostic, "available", "copy", null, "passed");
        }
        using (var fixture = new Fixture())
        {
            fixture.Addon->UldManager.NodeList[2]->NodeId = 99;
            CopyFailure(fixture, "root_manager", "node_missing");
        }
        using (var fixture = new Fixture())
        {
            fixture.List->NodeId = 14;
            CopyFailure(fixture, "root_manager", "node_duplicate");
        }
        using (var fixture = new Fixture())
        {
            fixture.Detail->Component->UldManager.NodeList[0]->Type = NodeType.Text;
            CopyFailure(fixture, "title_manager", "component_type");
        }
        using (var fixture = new Fixture())
        {
            ((AtkComponentNode*)fixture.Title->ParentNode)->Component->UldManager.NodeList = null;
            CopyFailure(fixture, "title_manager", "node_list_pointer");
        }
        using (var fixture = new Fixture())
        {
            var unowned = new AtkResNode { NodeId = 2, NodeFlags = NodeFlags.Visible };
            fixture.Title->ParentNode = &unowned;
            CopyFailure(fixture, "title", "parent_unowned");
        }
        using (var fixture = new Fixture())
        {
            var unowned = new AtkResNode { NodeId = 99, NodeFlags = NodeFlags.Visible };
            fixture.Detail->ParentNode = &unowned;
            CopyFailure(fixture, "title", "parent_chain_unowned");
        }
        using (var fixture = new Fixture())
        {
            fixture.Title->ParentNode = (AtkResNode*)fixture.BodyParent;
            CopyFailure(fixture, "title", "parent_id_mismatch");
        }
        using (var fixture = new Fixture())
        {
            fixture.Detail->NodeFlags = 0;
            CopyFailure(fixture, "title", "node_hidden");
        }
        using (var fixture = new Fixture())
        {
            fixture.Title->NodeText.BufUsed = 258;
            fixture.Title->NodeText.StringPtr = (byte*)1;
            CopyFailure(fixture, "title", "text_buffer_bounds");
        }
        using (var fixture = new Fixture())
        {
            fixture.Title->NodeText.StringPtr = null;
            CopyFailure(fixture, "title", "text_buffer_pointer");
        }
        using (var fixture = new Fixture())
        {
            ((byte*)fixture.Title->NodeText.StringPtr)[fixture.Title->NodeText.BufUsed - 1] = 1;
            CopyFailure(fixture, "title", "text_terminator");
        }
        Console.WriteLine("PASS fixed native diagnostic stages identify missing/duplicate/component/parent/visibility/buffer failures without accepting them");
    }

    private static void BufferLengthRegression()
    {
        foreach (var unusedLength in new long[] { -1, 0, 5, 1024, long.MaxValue })
        using (var fixture = new Fixture())
        {
            var heading = (AtkTextNode*)fixture.Addon->UldManager.NodeList[2];
            heading->NodeText.StringLength = unusedLength;
            fixture.Title->NodeText.StringLength = unusedLength;
            fixture.Body->NodeText.StringLength = unusedLength;
            Check(heading->NodeText.Length == 4 && heading->NodeText.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("Data")));
            Check(PortableArchiveReader.CopyView(fixture.Addon, out var diagnostic) == View());
            Diagnostic(diagnostic, "available", "copy", null, "passed");
        }
        using (var fixture = new Fixture())
        {
            var node = fixture.Title;
            var used = node->NodeText.BufUsed;
            var bytes = new ReadOnlySpan<byte>((byte*)node->NodeText.StringPtr, (int)used).ToArray();
            var copy = new PortableArchiveTextBuffer((nint)node, (nint)(byte*)node->NodeText.StringPtr, used, node->NodeText.BufSize, bytes);
            Check(PortableArchiveReader.TextBufferMatches(copy));
            node->NodeText.StringLength = -1;
            Check(PortableArchiveReader.TextBufferMatches(copy));
            node->NodeText.BufUsed++;
            Check(!PortableArchiveReader.TextBufferMatches(copy));
            node->NodeText.BufUsed--;
            node->NodeText.BufSize++;
            Check(!PortableArchiveReader.TextBufferMatches(copy));
            node->NodeText.BufSize--;
            ((byte*)node->NodeText.StringPtr)[0] ^= 1;
            Check(!PortableArchiveReader.TextBufferMatches(copy));
            ((byte*)node->NodeText.StringPtr)[0] ^= 1;
            node->NodeText.StringPtr = (byte*)1;
            Check(!PortableArchiveReader.TextBufferMatches(copy));
        }
        using (var fixture = new Fixture())
        {
            fixture.Title->NodeText.BufSize = fixture.Title->NodeText.BufUsed - 1;
            CopyFailure(fixture, "title", "text_capacity_metadata");
        }
        using (var fixture = new Fixture())
        {
            ((byte*)fixture.Title->NodeText.StringPtr)[1] = 0;
            CopyFailure(fixture, "title", "text_terminator");
        }
        using (var fixture = new Fixture())
        {
            ((byte*)fixture.Title->NodeText.StringPtr)[0] = 0xFF;
            CopyFailure(fixture, "title", "text_decode_error");
        }
        Console.WriteLine("PASS installed BufUsed-based getters accept stale StringLength while byte/capacity/terminator/UTF8/stability guards remain strict");
    }

    private static void CopyFailure(Fixture fixture, string field, string reason)
    {
        Check(PortableArchiveReader.CopyView(fixture.Addon) is null);
        Check(PortableArchiveReader.CopyView(fixture.Addon, out var diagnostic) is null);
        Diagnostic(diagnostic, "unavailable", "copy", field, reason);
    }

    private static void Diagnostic(PortableArchiveReadDiagnostic value, string status, string stage, string? field, string reason)
    {
        Check(value.schema == "compai_portable_archive_reader_v1" && value.category == "portable_archive" &&
            value.status == status && value.stage == stage && value.field == field && value.reason == reason);
        var json = JsonSerializer.Serialize(value);
        using var document = JsonDocument.Parse(json);
        Check(json.Length < 512 && document.RootElement.EnumerateObject().Select(property => property.Name)
            .SequenceEqual(["schema", "category", "status", "stage", "field", "reason"]));
        Check(!json.Contains("Synthetic", StringComparison.Ordinal) && !json.Contains("ContentId", StringComparison.Ordinal));
    }

    internal static void Produce(string path)
    {
        var snapshot = new ActivitySnapshot("portable_archive", Time, true, PortableArchiveReader.DescribeRows(View(), Definitions())!);
        var full = Arguments("{\"category\":\"portable_archive\"}");
        var filtered = Arguments("{\"category\":\"portable_archive\",\"ids\":[2]}");
        var paged = Arguments("{\"category\":\"portable_archive\",\"limit\":1}");
        var unavailable = ActivitySnapshot.Unavailable("portable_archive", "No supported selected archive entry.");
        var fixtures = new[]
        {
            new { name = "archive_selected", arguments = full, result = new ActivityLookup(snapshot).Query(full) },
            new { name = "archive_last_observed", arguments = full, result = new ActivityLookup(snapshot.AsLastObserved()).Query(full) },
            new { name = "archive_reference_only", arguments = filtered, result = new ActivityLookup(snapshot).Query(filtered) },
            new { name = "archive_paged", arguments = paged, result = new ActivityLookup(snapshot).Query(paged) },
            new { name = "archive_unavailable", arguments = full, result = new ActivityLookup(unavailable).Query(full) },
        };
        File.WriteAllText(path, JsonSerializer.Serialize(fixtures));
    }
    internal static void VerifyPublicCapture(string sqpack, string capture)
    {
        using var game = new Lumina.GameData(sqpack, new Lumina.LuminaOptions { DefaultExcelLanguage = Lumina.Data.Language.English });
        var pages = game.Excel.GetSubrowSheet<Lumina.Excel.Sheets.DescriptionPage>();
        var section = game.Excel.GetSubrowSheet<Lumina.Excel.Sheets.DescriptionSection>().GetRow(5).ToArray();
        Check(section.Select(row => row.Page.RowId).SequenceEqual([12u, 22u, 37u]));
        var definitions = section.SelectMany(row => pages.GetRow(row.Page.RowId)).Select(page =>
        {
            Check(page.Text.Count == 11);
            return new PortableArchiveDefinition(page.RowId, page.SubrowId, page.Text[0].RowId,
                PortableArchiveReader.Normalize(page.Text[0].Value.Text.ExtractText()));
        }).ToArray();
        PortableArchiveReader.BuildCatalog(definitions);
        using var document = JsonDocument.Parse(File.ReadAllText(capture));
        Check(document.RootElement.GetProperty("agent").GetString() == "Description" &&
            document.RootElement.GetProperty("addon").GetString() == "DescriptionYTC" &&
            !document.RootElement.GetProperty("truncated").GetBoolean());
        var nodes = document.RootElement.GetProperty("text_nodes").EnumerateArray().ToArray();
        string Read(uint[] path, uint id, uint parent) => PortableArchiveReader.Normalize(nodes.Single(node =>
            node.GetProperty("node_id").GetUInt32() == id && node.GetProperty("parent_node_id").GetUInt32() == parent &&
            node.GetProperty("component_path").EnumerateArray().Select(part => part.GetUInt32()).SequenceEqual(path)).GetProperty("text").GetString()!);
        Check(Read([], 14, 6) == "Data" && Read([], 32, 31) == "View recorded data.");
        var rows = PortableArchiveReader.DescribeRows(new(Read([30, 2], 3, 2)), definitions);
        Check(rows is { Length: 2 } && rows[0].ReferenceId == 226 && rows[1].ReferenceId == 12 && rows[1].Value == 0);
        Console.WriteLine("PASS supplied DescriptionYTC title matches installed Section5 -> Page12/subrow0 -> String226; all28 unique public titles validated");
    }
    private static JsonElement Arguments(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Portable Archive assertion failed."); }

    private sealed class Fixture : IDisposable
    {
        private readonly List<nint> allocations = [];
        internal readonly AtkUnitBase* Addon;
        internal readonly AtkComponentNode* Detail;
        internal readonly AtkComponentNode* BodyParent;
        internal readonly AtkComponentNode* List;
        internal readonly AtkTextNode* Title;
        internal readonly AtkTextNode* Body;
        internal Fixture(string? selectedTitle = null, string bodyText = "Synthetic body never read.")
        {
            Addon = Allocate<AtkUnitBase>();
            var headingParent = Node(6); var hintParent = Node(31);
            var heading = Text(14, "Data", headingParent); var hint = Text(32, "View recorded data.", hintParent);
            Detail = Component(30); List = Component(26);
            var titleParent = Component(2); BodyParent = Component(70001);
            titleParent->ParentNode = (AtkResNode*)Detail; BodyParent->ParentNode = (AtkResNode*)Detail;
            Title = Text(3, selectedTitle ?? View().Title, (AtkResNode*)titleParent); Body = Text(2, bodyText, (AtkResNode*)BodyParent);
            Nodes(&Addon->UldManager, [(nint)headingParent, (nint)hintParent, (nint)heading, (nint)hint, (nint)Detail, (nint)List]);
            Nodes(&Detail->Component->UldManager, [(nint)titleParent, (nint)BodyParent]);
            Nodes(&titleParent->Component->UldManager, [(nint)Title]);
            Nodes(&BodyParent->Component->UldManager, [(nint)Body]);
        }
        private T* Allocate<T>() where T : unmanaged { var value = (T*)NativeMemory.AllocZeroed((nuint)sizeof(T)); allocations.Add((nint)value); return value; }
        private AtkResNode* Node(uint id) { var node = Allocate<AtkResNode>(); node->NodeId = id; node->NodeFlags = NodeFlags.Visible; node->Color.A = 255; return node; }
        private AtkComponentNode* Component(uint id) { var node = Allocate<AtkComponentNode>(); node->NodeId = id; node->Type = (NodeType)1005; node->NodeFlags = NodeFlags.Visible; node->Color.A = 255; node->Component = Allocate<AtkComponentBase>(); return node; }
        private AtkTextNode* Text(uint id, string text, AtkResNode* parent)
        {
            var node = Allocate<AtkTextNode>(); node->NodeId = id; node->Type = NodeType.Text; node->NodeFlags = NodeFlags.Visible; node->Color.A = 255; node->ParentNode = parent;
            var encoded = Encoding.UTF8.GetBytes(text); var bytes = (byte*)NativeMemory.AllocZeroed((nuint)encoded.Length + 1); allocations.Add((nint)bytes);
            encoded.CopyTo(new Span<byte>(bytes, encoded.Length)); node->NodeText.StringPtr = bytes;
            node->NodeText.BufUsed = node->NodeText.BufSize = encoded.Length + 1; node->NodeText.StringLength = encoded.Length;
            return node;
        }
        private void Nodes(AtkUldManager* manager, nint[] nodes)
        {
            var list = (AtkResNode**)NativeMemory.AllocZeroed((nuint)(IntPtr.Size * nodes.Length)); allocations.Add((nint)list);
            for (var index = 0; index < nodes.Length; ++index) list[index] = (AtkResNode*)nodes[index];
            manager->NodeList = list; manager->NodeListCount = (ushort)nodes.Length;
        }
        public void Dispose() { foreach (var pointer in allocations) NativeMemory.Free((void*)pointer); }
    }
}
