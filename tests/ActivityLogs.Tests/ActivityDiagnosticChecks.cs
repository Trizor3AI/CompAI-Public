using System.Text;
using System.Text.Json;
using CompAI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

internal static unsafe class ActivityDiagnosticChecks
{
    internal static void Run()
    {
        int[] ids = [452, 232, 383, 375, 185, 437, 486, 79, 320];
        for (var i = 0; i < ids.Length; ++i) Check(ActivityMenuDiagnostic.AgentFor((ActivityDiagnosticTarget)i) == (AgentId)ids[i]);
        Check(ActivityMenuDiagnostic.AgentFor((ActivityDiagnosticTarget)999) is null);
        Check(ActivityMenuDiagnostic.AgentFor(ActivityDiagnosticTarget.CosmicMissions) == AgentId.WKSMission);
        Check(ActivityMenuDiagnostic.Labels.Length == Enum.GetValues<ActivityDiagnosticTarget>().Length);
        Console.WriteLine("PASS verified agent allowlist includes Occult Record; ArchiveItem remains explicitly unconfirmed");
        using (var f = new ChallengeDiagnosticChecks.Fixture())
        {
            var component = f.Component(15); var title = f.Text(7, "Synthetic menu record");
            title->ParentNode = (AtkResNode*)component;
            f.Nodes(&component->Component->UldManager, [(nint)title]);
            var hidden = f.Text(9, "Unrelated hidden text"); hidden->NodeFlags = 0;
            f.Nodes(f.Root, [(nint)component, (nint)hidden]);
            Check(ActivityMenuDiagnostic.TryCopyVisibleText(f.Root, out var first, out _) && first.Texts.Length == 1);
            Check(ActivityMenuDiagnostic.TryCopyVisibleText(f.Root, out var repeated, out _) && ActivityMenuDiagnostic.SameText(first, repeated));
            var result = ActivityMenuDiagnostic.Format(ActivityDiagnosticTarget.FieldRecord, "MYCWarResultNotebook", null, first);
            using var doc = Parse(result); var value = doc.RootElement;
            Check(value.EnumerateObject().Select(field => field.Name).SequenceEqual(["diagnostic", "selected_menu", "agent", "addon", "selected_tab", "selected_tab_label", "truncated", "truncation_reasons", "nodes_examined", "output_rows_omitted", "text_nodes"]));
            Check(!value.GetProperty("truncated").GetBoolean() && value.GetProperty("text_nodes")[0].GetProperty("component_path")[0].GetUInt32() == 15);
            Check(!result.Text!.Contains("Unrelated hidden text"));
            Check(!ActivityMenuDiagnostic.SameText(first, first with { Texts = [first.Texts[0] with { Text = "Changed" }] }));
            Check(!ActivityMenuDiagnostic.SameText(first, first with { Texts = [first.Texts[0] with { ComponentPath = [99] }] }));
            Check(!ActivityMenuDiagnostic.SameText(first, first with { TruncationReasons = ["node_visit_limit"] }));
            Console.WriteLine("PASS only stable visible text and public metadata are projected; text, path and cap changes invalidate capture");
        }
        using (var f = new ChallengeDiagnosticChecks.Fixture())
        {
            var firstList = f.Component(79); var secondList = f.Component(80);
            var hiddenAndDecorative = new List<nint>();
            for (uint i = 0; i < 1000; ++i)
            {
                var node = f.Text(1000 + i, "Unused layout text");
                if (i % 2 == 0) node->NodeFlags = 0; else node->Type = NodeType.Image;
                node->NodeText.StringPtr = (byte*)1; // ignored text buffers must not be read
                hiddenAndDecorative.Add((nint)node);
            }
            f.Nodes(&firstList->Component->UldManager, hiddenAndDecorative.Take(500).ToArray());
            var footer = f.Text(72, "Objectives can be repeated");
            f.Nodes(&secondList->Component->UldManager, [..hiddenAndDecorative.Skip(500), (nint)footer]);
            var selected = f.Text(40, "Standard Objectives"); var eventLabel = f.Text(41, "First Hunt (finished)");
            f.Nodes(f.Root, [(nint)firstList, (nint)secondList, (nint)selected, (nint)eventLabel]);
            Check(ActivityMenuDiagnostic.TryCopyVisibleText(f.Root, out var capture, out _));
            Check(capture.NodesExamined == 1005 && capture.TruncationReasons.Length == 0);
            Check(capture.Texts.Select(row => row.Text).SequenceEqual(["Standard Objectives", "First Hunt (finished)", "Objectives can be repeated"]));
            Check(capture.Texts[2].ComponentPath.SequenceEqual([80u]));
            Check(ActivityMenuDiagnostic.TryCopyVisibleText(f.Root, out var repeated, out _) && ActivityMenuDiagnostic.SameText(capture, repeated));
            Console.WriteLine("PASS hidden and decorative list nodes consume examination budget without starving later visible Mogpendium labels");
        }
        using (var f = new ChallengeDiagnosticChecks.Fixture())
        {
            var list = f.Component(79);
            f.Nodes(&list->Component->UldManager, Enumerable.Range(0, 220).Select(id => (nint)f.Text((uint)id, "Duty row")).ToArray());
            var selected = f.Text(40, "Selected page metadata");
            f.Nodes(f.Root, [(nint)list, (nint)selected]);
            Check(ActivityMenuDiagnostic.TryCopyVisibleText(f.Root, out var capture, out _));
            Check(capture.Texts.Length == 200 && capture.Texts[0].Text == "Selected page metadata" && capture.Texts[0].ComponentPath.Length == 0);
            Check(capture.TruncationReasons.Contains("text_node_limit") && capture.NodesExamined == 222);
            using var output = Parse(ActivityMenuDiagnostic.Format(ActivityDiagnosticTarget.Mogpendium, "MoogleCollection", null, capture));
            Console.WriteLine("PASS breadth-first capture preserves page-level fields before dense descendants without raising row limits");
        }
        using (var f = new ChallengeDiagnosticChecks.Fixture())
        {
            var lore = f.Text(45, new string('x', 1025)); lore->NodeText.StringPtr = (byte*)1; // must never dereference oversized buffer
            var title = f.Text(44, "Synthetic owned field record"); var counter = f.Text(28, "Total: 23/50");
            f.Nodes(f.Root, [(nint)lore, (nint)title, (nint)counter]);
            Check(ActivityMenuDiagnostic.TryCopyVisibleText(f.Root, out var capture, out _) && capture.Texts.Length == 3);
            Check(capture.Texts[0].NodeId == 45 && capture.Texts[0].Text is null && capture.Texts[0].Omission == "text_byte_limit");
            Check(capture.Texts[1].Text == "Synthetic owned field record" && capture.Texts[2].Text == "Total: 23/50");
            using var output = Parse(ActivityMenuDiagnostic.Format(ActivityDiagnosticTarget.FieldRecord, "MYCWarResultNotebook", null, capture));
            Check(output.RootElement.GetProperty("truncated").GetBoolean());
            Check(!ChallengeLogDiagnostic.TryCopyVisibleText(f.Root, out var strict) && strict.Length == 0);
            lore->NodeText.BufSize = 1;
            Check(!ActivityMenuDiagnostic.TryCopyVisibleText(f.Root, out capture, out var reason) && capture.Texts.Length == 0 && reason == "text buffer metadata");
            Console.WriteLine("PASS long lore is omitted before dereference, short fields survive, malformed buffers fail and Challenge stays strict");
        }
        using (var f = new ChallengeDiagnosticChecks.Fixture())
        {
            var title = f.Text(1, "Synthetic Mogpendium"); var nodes = new nint[513]; nodes[0] = (nint)title; nodes[512] = 1;
            f.Nodes(f.Root, nodes);
            Check(ActivityMenuDiagnostic.TryCopyVisibleText(f.Root, out var capture, out _) && capture.Texts.Single().Text == "Synthetic Mogpendium");
            Check(capture.NodesExamined == 512 && capture.TruncationReasons.Contains("node_list_limit"));
            Check(!ChallengeLogDiagnostic.TryCopyVisibleText(f.Root, out _));
            using var output = Parse(ActivityMenuDiagnostic.Format(ActivityDiagnosticTarget.Mogpendium, "MoogleCollection", null, capture));
            Console.WriteLine("PASS large layouts preserve bounded prefix metadata without touching the pointer beyond the 512-node cap");
        }
        using (var f = new ChallengeDiagnosticChecks.Fixture())
        {
            var nodes = new List<nint>();
            for (uint i = 0; i < 9; ++i) nodes.Add((nint)f.Text(i, new string('x', 1000)));
            nodes.Add((nint)f.Text(100, "7/10")); f.Nodes(f.Root, nodes.ToArray());
            Check(ActivityMenuDiagnostic.TryCopyVisibleText(f.Root, out var capture, out _));
            Check(capture.Texts[8].Omission == "total_text_byte_limit" && capture.Texts[9].Text == "7/10");
            f.Nodes(f.Root, Enumerable.Range(0, 201).Select(id => (nint)f.Text((uint)id, "x")).ToArray());
            Check(ActivityMenuDiagnostic.TryCopyVisibleText(f.Root, out capture, out _) && capture.Texts.Length == 200 && capture.TruncationReasons.Contains("text_node_limit"));
            var cyclic = f.Text(900, "Unproven visibility"); cyclic->ParentNode = (AtkResNode*)cyclic; f.Nodes(f.Root, [(nint)cyclic]);
            Check(ActivityMenuDiagnostic.TryCopyVisibleText(f.Root, out capture, out _) && capture.Texts.Length == 0 && capture.TruncationReasons.Contains("parent_depth_limit"));
            Console.WriteLine("PASS total byte and row limits retain later short counters; parent cycles are bounded and marked");
        }
        using (var f = new ChallengeDiagnosticChecks.Fixture())
        {
            var first = f.Component(1); var current = first;
            for (uint i = 2; i <= 9; ++i)
            {
                var child = f.Component(i); f.Nodes(&current->Component->UldManager, [(nint)child]); current = child;
            }
            current->Component = (AtkComponentBase*)1; // deeper component must not be dereferenced
            f.Nodes(f.Root, [(nint)first]);
            Check(ActivityMenuDiagnostic.TryCopyVisibleText(f.Root, out var capture, out _) && capture.TruncationReasons.Contains("component_depth_limit"));
            var duplicate = f.Text(1, "Bounded"); var components = new List<nint>();
            for (uint i = 0; i < 5; ++i)
            {
                var component = f.Component(i + 10); components.Add((nint)component);
                f.Nodes(&component->Component->UldManager, Enumerable.Repeat((nint)duplicate, 512).ToArray());
            }
            f.Nodes(f.Root, components.ToArray());
            Check(ActivityMenuDiagnostic.TryCopyVisibleText(f.Root, out capture, out _) && capture.NodesExamined == 2048 && capture.TruncationReasons.Contains("node_examination_limit"));
            Console.WriteLine("PASS nested and duplicate component graphs cannot exceed depth or 2048 examined-entry bounds");
        }
        ActivityDiagnosticTextCapture ordinary = new([new(1, null, [], "Synthetic")], [], 1);
        foreach (var target in Enum.GetValues<ActivityDiagnosticTarget>())
        {
            var result = ActivityMenuDiagnostic.Format(target, "SyntheticMenu", target switch { ActivityDiagnosticTarget.GoldSaucer => 4, ActivityDiagnosticTarget.FreeCompany => 3, _ => (int?)null }, ordinary);
            using var parsed = Parse(result);
            if (target == ActivityDiagnosticTarget.ArchiveItem) Check(result.Text!.Contains("not confirmed Portable Archive"));
        }
        Check(ActivityMenuDiagnostic.Format(ActivityDiagnosticTarget.FieldRecord, "SyntheticMenu", 1, ordinary).Text is null);
        Check(ActivityMenuDiagnostic.Format(ActivityDiagnosticTarget.GoldSaucer, "SyntheticMenu", 6, ordinary).Text is null);
        Check(ActivityMenuDiagnostic.Format(ActivityDiagnosticTarget.GoldSaucer, "SyntheticMenu", null, ordinary).Text is null);
        foreach (var tab in new int?[] { null, -1, 1, 4, 6 })
            Check(ActivityMenuDiagnostic.Format(ActivityDiagnosticTarget.FreeCompany, "FreeCompany", tab, ordinary).Text is null);
        foreach (var tab in new[] { 0, 2, 3, 5 })
        {
            using var output = Parse(ActivityMenuDiagnostic.Format(ActivityDiagnosticTarget.FreeCompany, "FreeCompany", tab, ordinary));
            Check(output.RootElement.GetProperty("selected_tab_label").ValueKind == JsonValueKind.String);
        }
        var readerStatus = new { owner_ready = true, profile_ready = false };
        using (var output = Parse(ActivityMenuDiagnostic.Format(ActivityDiagnosticTarget.FreeCompany, "FreeCompany", 0, ordinary, readerStatus)))
        {
            var status = output.RootElement.GetProperty("reader_status");
            Check(status.EnumerateObject().Select(field => field.Name).SequenceEqual(["owner_ready", "profile_ready"]));
            Check(status.GetProperty("owner_ready").GetBoolean() && !status.GetProperty("profile_ready").GetBoolean());
        }
        Check(ActivityMenuDiagnostic.Format(ActivityDiagnosticTarget.Mogpendium, "MoogleCollection", null, ordinary, readerStatus).Text is null);
        Check(ActivityMenuDiagnostic.Format(ActivityDiagnosticTarget.FreeCompany, "FreeCompany", 0, ordinary, true).Text is null);
        Check(ActivityMenuDiagnostic.Format(ActivityDiagnosticTarget.FreeCompany, "FreeCompany", 0, ordinary,
            new { oversized = new string('x', ActivityMenuDiagnostic.MaxOutputBytes) }).Text is null);
        foreach (var (target, addon, tab) in new[]
        {
            (ActivityDiagnosticTarget.GoldSaucer, "GoldSaucerInfo", (int?)5),
            (ActivityDiagnosticTarget.Description, "DescriptionYTC", (int?)null),
            (ActivityDiagnosticTarget.CosmicMissions, "WKSMission", (int?)null),
        })
        {
            var stage = new { access_enabled = false, retained_observation_available = false,
                capture = new { stage = "synthetic_guard", available = false } };
            using var output = Parse(ActivityMenuDiagnostic.Format(target, addon, tab, ordinary, stage));
            var status = output.RootElement.GetProperty("reader_status");
            Check(!status.GetProperty("access_enabled").GetBoolean() &&
                !status.GetProperty("retained_observation_available").GetBoolean() &&
                status.GetProperty("capture").GetProperty("stage").GetString() == "synthetic_guard");
            Check(ActivityMenuDiagnostic.Format(target, addon, tab, ordinary, true).Text is null);
            Check(ActivityMenuDiagnostic.Format(target, addon, tab, ordinary,
                new { oversized = new string('x', ActivityMenuDiagnostic.MaxOutputBytes) }).Text is null);
        }
        Console.WriteLine("PASS human reader status is target-limited, bounded and preserves failed stages with visible text");
        var selection = new FieldRecordDiagnosticSelection(49, 8, 0);
        var identity = new ActivityDiagnosticObservation(1, 2, 3, 4, 5, 6, 7, "MYCWarResultNotebook", null, FieldRecord: selection);
        Check(identity == identity with { FieldRecord = new(49, 8, 0) });
        Check(identity != identity with { FieldRecord = selection with { MaxNoteIndex = 50 } });
        Check(identity != identity with { FieldRecord = selection with { CurrentNoteIndex = 9 } });
        Check(identity != identity with { FieldRecord = selection with { CurrentPageIndex = 1 } });
        using (var output = Parse(ActivityMenuDiagnostic.Format(ActivityDiagnosticTarget.FieldRecord, "MYCWarResultNotebook", null, ordinary, fieldRecord: selection)))
        {
            var values = output.RootElement.GetProperty("field_record_selection");
            Check(values.GetProperty("current_note_index").GetInt32() == 8);
            Check(values.EnumerateObject().Select(field => field.Name).SequenceEqual(["coverage", "max_note_index", "current_note_index", "current_page_index"]));
            Check(values.GetProperty("coverage").GetString()!.Contains("unverified"));
        }
        Check(ActivityMenuDiagnostic.Format(ActivityDiagnosticTarget.FieldRecord, "OtherNotebook", null, ordinary, fieldRecord: selection).Text is null);
        Check(ActivityMenuDiagnostic.Format(ActivityDiagnosticTarget.Collection, "MYCWarResultNotebook", null, ordinary, fieldRecord: selection).Text is null);
        using (var output = Parse(ActivityMenuDiagnostic.Format(ActivityDiagnosticTarget.Description, "Description", null, ordinary)))
        {
            Check(output.RootElement.GetProperty("agent").GetString() == "Description");
            Check(output.RootElement.GetProperty("scope").GetString()!.Contains("this diagnostic alone does not establish"));
        }
        Console.WriteLine("PASS raw Field Record selection changes invalidate identity; Description diagnostic is not registration evidence");
        var panel = new GoldSaucerChildObservation(1234567, "SyntheticMahjong", "one_visible_owned_child",
            [new(0, "SyntheticMahjong", 321, 1, 1, 77, true, true)]) { Parent = 7654321 };
        using (var output = Parse(ActivityMenuDiagnostic.Format(ActivityDiagnosticTarget.GoldSaucer, "GoldSaucerInfo", 5, ordinary, goldSaucerChild: panel)))
        {
            var data = output.RootElement.GetProperty("gold_saucer_panel");
            Check(data.GetProperty("text_source_addon").GetString() == "SyntheticMahjong");
            Check(data.GetProperty("children")[0].EnumerateObject().Select(field => field.Name)
                .SequenceEqual(["ordinal", "addon", "raw_tab_index", "ready", "visible"]));
            Check(!output.RootElement.GetRawText().Contains("1234567") && !output.RootElement.GetRawText().Contains("7654321"));
        }
        var emptyPanel = panel with { TextAddon = 0, TextAddonName = null, Status = "multiple_visible_owned_children" };
        using (var output = Parse(ActivityMenuDiagnostic.Format(ActivityDiagnosticTarget.GoldSaucer, "GoldSaucerInfo", 5, ordinary, goldSaucerChild: emptyPanel)))
            Check(output.RootElement.GetProperty("gold_saucer_panel").GetProperty("text_source_addon").GetString() == "GoldSaucerInfo");
        Check(ActivityMenuDiagnostic.Format(ActivityDiagnosticTarget.GoldSaucer, "GoldSaucerInfo", 4, ordinary, goldSaucerChild: panel).Text is null);
        Check(ActivityMenuDiagnostic.Format(ActivityDiagnosticTarget.FieldRecord, "MYCWarResultNotebook", null, ordinary, goldSaucerChild: panel).Text is null);
        Check(ActivityMenuDiagnostic.Format(ActivityDiagnosticTarget.GoldSaucer, "GoldSaucerInfo", 5, ordinary,
            goldSaucerChild: panel with { Status = "private error text" }).Text is null);
        var parentIdentity = new ActivityDiagnosticObservation(1, 2, 3, 4, 5, 6, 7, "GoldSaucerInfo", 5, GoldSaucerChild: panel);
        Check(ActivityMenuDiagnostic.SameObservation(parentIdentity, parentIdentity with { GoldSaucerChild = panel with { Children = panel.Children.ToArray() } }));
        Check(!ActivityMenuDiagnostic.SameObservation(parentIdentity, parentIdentity with { GoldSaucerChild = panel with { TextAddon = 999 } }));
        Check(!ActivityMenuDiagnostic.SameObservation(parentIdentity, parentIdentity with { GoldSaucerChild = null }));
        Check(!ActivityMenuDiagnostic.SameObservation(parentIdentity, parentIdentity with { Owner = 999 }));
        Check(!ActivityMenuDiagnostic.SameObservation(parentIdentity, null));
        Console.WriteLine("PASS Mahjong text source is explicit, private native tokens are excluded, repeated identities compare structurally and stale captures fail");
        Check(ActivityMenuDiagnostic.Format((ActivityDiagnosticTarget)999, "SyntheticMenu", null, ordinary).Text is null);
        Check(ActivityMenuDiagnostic.Format(ActivityDiagnosticTarget.Mogpendium, "C:\\Users\\private", null, ordinary).Text is null);
        Check(ActivityMenuDiagnostic.Format(ActivityDiagnosticTarget.Mogpendium, "SyntheticMenu", null, ordinary with { Texts = [new(1, null, new uint[9], "Too deep")] }).Text is null);
        Check(ActivityMenuDiagnostic.Format(ActivityDiagnosticTarget.Mogpendium, "SyntheticMenu", null, ordinary with { TruncationReasons = ["C:\\private"] }).Text is null);
        var escaped = Enumerable.Range(0, 30).Select(id => new ActivityDiagnosticText((uint)id, null, [], new('界', 90))).ToArray();
        using (var output = Parse(ActivityMenuDiagnostic.Format(ActivityDiagnosticTarget.Mogpendium, "MoogleCollection", null, new(escaped, [], 30))))
        {
            var value = output.RootElement;
            Check(value.GetProperty("truncation_reasons").EnumerateArray().Any(reason => reason.GetString() == "output_byte_limit"));
            Check(value.GetProperty("text_nodes").GetArrayLength() == 30 && value.GetProperty("output_rows_omitted").GetInt32() == 0);
        }
        var metadata = Enumerable.Range(0, 200).Select(id => new ActivityDiagnosticText((uint)id, null, Enumerable.Repeat(uint.MaxValue, 8).ToArray(), "x")).ToArray();
        using (var output = Parse(ActivityMenuDiagnostic.Format(ActivityDiagnosticTarget.Mogpendium, "MoogleCollection", null, new(metadata, [], 200))))
            Check(output.RootElement.GetProperty("output_rows_omitted").GetInt32() > 0);
        Console.WriteLine("PASS 16 KB output cap includes JSON escaping with explicit omissions, while malformed metadata fails closed");
        MogpendiumListChecks.Run();
    }
    private static JsonDocument Parse(ChallengeDiagnosticResult result)
    {
        Check(result.Text is not null && Encoding.UTF8.GetByteCount(result.Text) <= ActivityMenuDiagnostic.MaxOutputBytes);
        return JsonDocument.Parse(result.Text!);
    }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Activity diagnostic check failed."); }
}
