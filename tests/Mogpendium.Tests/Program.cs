using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using CompAI;
using FFXIVClientStructs.FFXIV.Component.GUI;

var libraryPath = Environment.GetEnvironmentVariable("DALAMUD_LIB_PATH") ??
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "addon", "Hooks", "dev");
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var path = Path.Combine(libraryPath, name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
Checks.Run();
ActiveListChecks.Run();
if (args is ["--produce-fixtures", var output]) Checks.Produce(output);
if (args is ["--public-catalog", var catalog]) Checks.PublicCatalog(catalog);
if (args is ["--captured-lists", var publicCatalog, var weeklyCapture, var minimogCapture]) Checks.CapturedLists(publicCatalog, weeklyCapture, minimogCapture);

internal static unsafe class Checks
{
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-10-20T12:00:00Z");
    private static readonly string Heading = "Moogle Treasure Trove The First Hunt for Astronomy";
    private static readonly string Currency = "Irregular Tomestone of Astronomy I";
    private static readonly string Secondary = "Uolon Horn Token";
    private static readonly string[] Labels = ["Standard Objectives", "Weekly Objectives", "Minimog Challenges", "Ultimog Challenge"];
    private static readonly string[] Descriptions = [
        "These objectives can be completed repeatedly during the event period. Moogle Treasure Trove rewards will be claimed automatically upon completion.",
        "These bonus objectives can be completed once per week, and each player will be assigned their own objective. Moogle Treasure Trove rewards can be claimed from the Mogpendium upon completion.",
        "These challenges can be completed once per week by completing one of the two objectives. Moogle Treasure Trove rewards can be claimed from the Mogpendium upon completion.",
        "This challenge can be completed once per event period. Moogle Treasure Trove rewards can be claimed from the Mogpendium upon completion."];
    private static readonly string[] Titles = ["Halatali (Hard) Lv. 50 Dungeons", "The Gilded Araya Lv. 90 Trials", "Save the Queen Lv. 80", "V&C Dungeon Finder Lv. 90"];
    private static readonly string[] Overviews = ["Complete Halatali (Hard).", "Complete the Gilded Araya.",
        "Complete Delubrum Reginae 1 time. ※Delubrum Reginae (Savage) will not be counted.", "Complete Aloalo Island 6 times."];
    private static readonly string[] Targets = ["Halatali (Hard)", "The Gilded Araya", "Delubrum Reginae", "Aloalo Island"];
    private static readonly uint[] Rewards = [4, 20, 30, 70];
    private static readonly string[] Templates = [
        "Complete <sheet(InstanceContent,lnum5,3)>.", "Complete <sheet(InstanceContent,lnum5,3)>.",
        "Complete <sheet(PublicContent,lnum5,3)> <num(lnum8)> <if([lnum8==1],time,times)>.<br>※Delubrum Reginae (Savage) will not be counted.",
        "Complete <sheet(InstanceContent,lnum5,3)> <num(lnum8)> times."];
    private static int passed;
    private static MogpendiumView View(int category) => new(Heading, Currency, Secondary, category,
        Descriptions[category], Titles[category], Overviews[category], Rewards[category], category switch { 2 => 1, 3 => 5, _ => null });
    private static MogpendiumSeason Season(int standardCount = 47)
    {
        var categories = Enumerable.Range(0, 4).Select(index => new[] {
            new MogpendiumObjective((uint)(300 + index), Targets[index], Templates[index], Rewards[index], [Targets[index]]) }).ToArray();
        categories[0] = categories[0].Concat(Enumerable.Range(1, standardCount - 1).Select(index =>
            new MogpendiumObjective((uint)(400 + index), "Public duty " + index, Templates[0], index % 2 == 0 ? 4u : null, ["Public duty " + index]))).ToArray();
        return new(28, Heading, Currency, 51750, Secondary, 51752, categories, Labels, Descriptions);
    }
    private static ActivitySnapshot Snapshot(int category = 0, int standardCount = 47) =>
        MogpendiumReader.Describe(Time, View(category), [Season(standardCount)]) ?? throw new Exception("Expected supported snapshot");
    private static MogpendiumObservedList ObservedList(int category) => new(Enumerable.Range(1, 6).Reverse().SelectMany(week =>
        Enumerable.Range(1, category).Select(slot => new MogpendiumObservedChoice("Week " + week,
            category == 1 ? "Synthetic duty " + week : slot == 1 ? "Shared feature" : "Generic feature " + week, slot))).ToArray());
    private static MogpendiumSeason ListSeason()
    {
        var season = Season(10);
        season.Categories[1] = Enumerable.Range(1, 6).Select(week => new MogpendiumObjective((uint)(1000 + week),
            "Synthetic duty " + week, Templates[1], 20, ["Synthetic duty " + week])).ToArray();
        return season;
    }
    private static ActivitySnapshot ListSnapshot(int category, bool includeSelection)
    {
        var view = category == 1 ? View(1) with { Title = "Synthetic duty 1", Overview = "Complete Synthetic duty 1." } : View(2);
        if (!includeSelection) view = view with { Title = "Generic feature", Overview = "Unsupported displayed instruction." };
        return MogpendiumReader.Describe(Time, view, [ListSeason()], ObservedList(category)) ?? throw new Exception("Expected observed list");
    }

    internal static void Run()
    {
        Test("four selected views publish corroborated event, detail and reward previews only", () =>
        {
            for (var category = 0; category < 4; ++category)
            {
                var value = Snapshot(category);
                Check(value.Category == MogpendiumContract.Categories[category] && value.Entries.Single(row => row.Id == 1).Value == 28);
                Check(value.Entries.Single(row => row.Id == 10).Text == Overviews[category]);
                Check(value.Entries.Single(row => row.Id == 11).Value == Rewards[category]);
                Check(value.Entries.All(row => row.Required is null && row.Slot is null && row.State == "observed"));
                Check(value.Entries.Any(row => row.Id == 12) == (category >= 2));
                Check(value.Entries.Count(row => row.Group == "catalog") == (category == 0 ? 47 : 0));
            }
        });
        Test("off-screen Standard definitions paginate with unknown variable rewards", () =>
        {
            var snapshot = Snapshot();
            var result = new ActivityLookup(snapshot).Query(Args("{\"category\":\"mogpendium_standard\"}"));
            Check(snapshot.Entries.Count == 54 && result.entry_count == 54 && result.next_offset == 50 && result.entries.Length == 50);
            var tail = new ActivityLookup(snapshot).Query(Args("{\"category\":\"mogpendium_standard\",\"offset\":50}"));
            Check(tail.entries.Length == 4 && tail.next_offset is null);
            Check(snapshot.Entries.Single(row => row.Id == 101).Value is null && snapshot.Entries.Single(row => row.Id == 102).Value == 4);
            Check(JsonSerializer.SerializeToUtf8Bytes(result).Length <= ActivityLookup.MaxBytes);
        });
        Test("event binding rejects current/historical currency confusion and ambiguous labels", () =>
        {
            var season = Season();
            Check(MogpendiumReader.Describe(Time, View(0) with { CurrencyName = "Irregular Tomestone of Astronomy II" }, [season]) is null);
            Check(MogpendiumReader.Describe(Time, View(0) with { SecondaryCurrencyName = null }, [season]) is null);
            Check(MogpendiumReader.Describe(Time, View(0) with { Heading = "Moogle Treasure Trove The Second Hunt for Astronomy" }, [season]) is null);
            Check(MogpendiumReader.SelectSeason(new(Heading, Currency, Secondary), [season, season with { Id = 30 }]) is null);
            var pending = season with { Id = 30, Heading = "Moogle Treasure Trove Pending", CurrencyName = "Future currency", CurrencyItemId = 51751, Categories = [[], [], [], []] };
            Check(MogpendiumReader.SelectSeason(new(pending.Heading, pending.CurrencyName, Secondary), [season, pending])?.Id == 30);
            Check(MogpendiumReader.Describe(Time, View(0) with { Heading = pending.Heading, CurrencyName = pending.CurrencyName }, [pending]) is null);
        });
        Test("category and target bindings reject stale detail, ambiguity and mismatched reward", () =>
        {
            Check(MogpendiumReader.Describe(Time, View(0) with { Description = Descriptions[1] }, [Season()]) is null);
            Check(MogpendiumReader.Describe(Time, View(1) with { Overview = Overviews[0], Title = Titles[0] }, [Season()]) is null);
            Check(MogpendiumReader.Describe(Time, View(0) with { PrimaryReward = 20 }, [Season()]) is null);
            var season = Season();
            season.Categories[2] = [season.Categories[2][0], new(600, "Delubrum Reginae (Savage)", Templates[2], 30, ["Delubrum Reginae (Savage)"])];
            Check(MogpendiumReader.Describe(Time, View(2), [season]) is not null); // Exclusion note is not a candidate.
            season.Categories[2] = [season.Categories[2][0], season.Categories[2][0] with { ContentId = 601 }];
            Check(MogpendiumReader.Describe(Time, View(2), [season]) is null);
        });
        Test("anchored public instruction rejects shorter-name variants and unsupported macro forms", () =>
        {
            var season = Season(); var hard = season.Categories[0][0];
            var normal = hard with { ContentId = 999, Name = "Halatali", TargetNames = ["Halatali"] };
            season.Categories[0] = [normal];
            Check(MogpendiumReader.Describe(Time, View(0), [season]) is null);
            season.Categories[0] = [hard, normal];
            Check(MogpendiumReader.Describe(Time, View(0), [season])?.Entries.Single(row => row.Id == 10).ReferenceId == hard.ContentId);
            Check(!MogpendiumCatalog.TryMatchOverview(hard with { Overview = "Complete <sheet(Unknown,lnum5,3)>." }, Overviews[0]));
            Check(!MogpendiumCatalog.TryMatchOverview(hard, "Complete Halatali (Hard) and another duty."));
            var mini = Season().Categories[2][0];
            Check(!MogpendiumCatalog.TryMatchOverview(mini, Overviews[2].Replace("1 time", "65536 times")));
            Check(!MogpendiumCatalog.TryMatchOverview(mini, Overviews[2].Replace("1 time", "-1 time")));
            Check(!MogpendiumCatalog.TryMatchOverview(mini, Overviews[2].Replace("1 time", "1 times")));
            Check(!MogpendiumCatalog.TryMatchOverview(mini, Overviews[2].Replace("will not be counted", "will be counted")));
        });
        Test("wire shapes reject injected fields, missing metadata and incompatible rewards", () =>
        {
            var rows = Snapshot().Entries.ToArray();
            Check(!MogpendiumContract.ValidShape("mogpendium_standard", rows.Where(row => row.Id != 3).ToArray()));
            Check(!MogpendiumContract.ValidShape("mogpendium_standard", rows.Concat([rows.Single(row => row.Id == 5)]).ToArray()));
            Check(!MogpendiumContract.ValidShape("mogpendium_standard", rows.Select(row => row.Id == 11 ? row with { ReferenceId = 999 } : row).ToArray()));
            Throws(() => new ActivitySnapshot("mogpendium_weekly", Time, true, Snapshot(1).Entries.Select(row => row.Id == 10 ? row with { Required = 5, Value = 0 } : row)));
            Throws(() => new ActivitySnapshot("mogpendium_standard", Time, true, rows.Select(row => row.Id == 101 ? row with { Id = 199 } : row)));
            Throws(() => new ActivitySnapshot("mogpendium_standard", Time, true, rows.Select(row => row.Id == 10 ? row with { Text = new string('x', 257) } : row)));
        });
        Test("native projection reads only exact visible page paths and ignores stale renderers", () =>
        {
            for (var category = 0; category < 4; ++category)
            {
                using var fixture = new Fixture(View(category));
                var copied = MogpendiumReader.CopyView(fixture.Addon);
                Check(copied == View(category));
                Check(MogpendiumReader.CopyEvent(fixture.Addon) == new MogpendiumEvent(Heading, Currency, Secondary));
                fixture.Title->NodeFlags = 0;
                Check(MogpendiumReader.CopyView(fixture.Addon) is null && MogpendiumReader.CopyEvent(fixture.Addon) is not null);
            }
        });
        Test("native projection fails closed on layout, parent, visibility and string bounds", () =>
        {
            using var fixture = new Fixture(View(0));
            fixture.Addon->UldManager.NodeListCount = 513;
            Check(MogpendiumReader.CopyView(fixture.Addon) is null);
            fixture.Addon->UldManager.NodeListCount = (ushort)fixture.RootNodes.Length;
            fixture.Nodes(&fixture.Addon->UldManager, fixture.RootNodes.Concat([(nint)fixture.HeadingNode]).ToArray());
            Check(MogpendiumReader.CopyEvent(fixture.Addon) is null);
            fixture.Nodes(&fixture.Addon->UldManager, fixture.RootNodes);
            fixture.Title->ParentNode->NodeId = 999;
            Check(MogpendiumReader.CopyView(fixture.Addon) is null);
            fixture.Title->ParentNode->NodeId = 8;
            fixture.Title->NodeText.BufUsed = 1026;
            Check(MogpendiumReader.CopyView(fixture.Addon) is null);
        });
        Test("bounded BufUsed payload accepts length metadata drift and rejects invalid UTF8", () =>
        {
            using var fixture = new Fixture(View(2));
            fixture.Title->NodeText.StringLength = 1;
            Check(MogpendiumReader.CopyView(fixture.Addon) == View(2));
            var colored = new Dalamud.Game.Text.SeStringHandling.SeStringBuilder().AddUiForeground(500)
                .AddText(Titles[2]).AddUiForegroundOff().Build().Encode();
            fixture.SetText(fixture.Title, colored);
            Check(MogpendiumReader.CopyView(fixture.Addon) == View(2));
            fixture.SetText(fixture.Title, Encoding.UTF8.GetBytes(Titles[2]));
            ((byte*)fixture.Title->NodeText.StringPtr)[0] = 255;
            Check(MogpendiumReader.CopyView(fixture.Addon) is null);
        });
        Test("all reward parsing and overview output stay explicit and bounded", () =>
        {
            Check(MogpendiumReader.TryReward("×70", out var count) && count == 70);
            foreach (var text in new[] { "70", "×0", "×65536", "×-1", "×1/5", "×1,000" }) Check(!MogpendiumReader.TryReward(text, out _));
            Check(MogpendiumReader.Describe(Time, View(0) with { Overview = new string('x', 257) }, [Season()]) is null);
            var noSecondary = Season() with { SecondaryCurrencyName = null, SecondaryCurrencyItemId = null };
            Check(MogpendiumReader.Describe(Time, View(2) with { SecondaryCurrencyName = null }, [noSecondary]) is null);
        });
        Test("complete Weekly list binds each displayed duty and preserves week labels", () =>
        {
            var value = ListSnapshot(1, true);
            var list = value.Entries.Where(row => row.Group == "observed_list").ToArray();
            Check(list.Length == 6 && list[0].Text == "Week 6" && list[^1].Text == "Week 1");
            Check(list.All(row => row.Slot == 1 && row.ReferenceId is >= 1001 and <= 1006 && row.Value is null && row.Required is null));
            Check(value.Entries.Any(row => row.Id == 10));
            var filtered = new ActivityLookup(value).Query(Args("{\"category\":\"mogpendium_weekly\",\"group\":\"observed_list\",\"offset\":2,\"limit\":2}"));
            Check(filtered.entries.Length == 2 && filtered.next_offset == 4);
        });
        Test("generic Minimog pairs remain complete without invented target or selected detail", () =>
        {
            var value = ListSnapshot(2, false);
            var list = value.Entries.Where(row => row.Group == "observed_list").ToArray();
            Check(list.Length == 12 && !value.Entries.Any(row => row.Group == "selection"));
            Check(list.All(row => row.ReferenceId is null && row.ReferenceKind is null && row.Value is null && row.Required is null));
            Check(list.Select(row => row.Slot).SequenceEqual(Enumerable.Range(0, 12).Select(index => (int?)(index % 2 + 1))));
            Check(ListSnapshot(2, true).Entries.Any(row => row.Id == 10));
        });
        Test("observed list contracts reject holes, malformed pairs, inferred values and ambiguous binding", () =>
        {
            var weekly = ListSnapshot(1, false).Entries.ToArray();
            var mini = ListSnapshot(2, false).Entries.ToArray();
            Check(!MogpendiumContract.ValidShape("mogpendium_weekly", weekly.Where(row => row.Id != 101).ToArray()));
            Check(!MogpendiumContract.ValidShape("mogpendium_minimog", mini.Select(row => row.Id == 101 ? row with { Slot = 1 } : row).ToArray()));
            Check(!MogpendiumContract.ValidShape("mogpendium_minimog", mini.Select(row => row.Id == 101 ? row with { Text = "Other week" } : row).ToArray()));
            Check(!MogpendiumContract.ValidShape("mogpendium_minimog", mini.Select(row => row.Id == 100 ? row with { Value = 0, Required = 1 } : row).ToArray()));
            Check(!MogpendiumContract.ValidShape("mogpendium_weekly", weekly.Where(row => row.Id < 100).ToArray()));
            Check(!MogpendiumContract.ValidShape("mogpendium_minimog", ListSnapshot(2, true).Entries.Where(row => row.Id != 11).ToArray()));
            Check(!MogpendiumContract.ValidShape("mogpendium_weekly", weekly.Select(row => row.Id == 100 ? row with { ReferenceId = null, ReferenceKind = null } : row).ToArray()));
            var season = ListSeason(); season.Categories[1] = [..season.Categories[1], season.Categories[1][0] with { ContentId = 9999 }];
            Check(MogpendiumReader.Describe(Time, View(1), [season], ObservedList(1)) is null);
            var objective = new MogpendiumObjective(1, "Duty", Templates[1], 20, ["Duty"]);
            Check(MogpendiumCatalog.MatchDisplayedLabel([objective], "Duty (Hard)") is null);
        });
        Test("entirely absent detail retains an independent list while partial or malformed detail fails closed", () =>
        {
            using var fixture = new Fixture(View(2));
            foreach (var address in fixture.RootNodes)
            {
                var node = (AtkResNode*)address;
                if (node->NodeId is 8 or 34 or 38) node->NodeFlags = 0;
            }
            var page = MogpendiumReader.CopyPage(fixture.Addon);
            Check(page is not null && page.Detail is null);
            var list = MogpendiumReader.DescribePage(Time, page!, [ListSeason()], ObservedList(2));
            Check(list is not null && list.Entries.Count(row => row.Group == "observed_list") == 12 && !list.Entries.Any(row => row.Group == "selection"));
            Check(MogpendiumReader.DescribePage(Time, page!, [ListSeason()]) is null);
            foreach (var address in fixture.RootNodes)
            {
                var node = (AtkResNode*)address;
                if (node->NodeId is 8 or 34 or 38) node->NodeFlags = NodeFlags.Visible;
            }
            fixture.Title->NodeFlags = 0;
            Check(MogpendiumReader.CopyPage(fixture.Addon) is null);
            fixture.Title->NodeFlags = NodeFlags.Visible; fixture.Title->NodeText.BufUsed = 2048;
            Check(MogpendiumReader.CopyPage(fixture.Addon) is null);
        });
        Console.WriteLine($"Passed {passed} Mogpendium checks.");
    }

    internal static void PublicCatalog(string path)
    {
        var seasons = JsonSerializer.Deserialize<MogpendiumSeason[]>(File.ReadAllText(path))!;
        for (var category = 0; category < 4; ++category)
        {
            var result = MogpendiumReader.Describe(Time, View(category), seasons);
            Check(result is not null);
            Check(result!.Entries.Single(row => row.Id == 1).Value == 28);
            Console.WriteLine($"PASS installed public season28 category{category}: selected content {result.Entries.Single(row => row.Id == 10).ReferenceId}, catalog rows {result.Entries.Count(row => row.Group == "catalog")}");
        }
    }
    internal static void Produce(string path)
    {
        var cases = Enumerable.Range(0, 4).Select(category => ("available_" + category, Snapshot(category, 10), Args($"{{\"category\":\"{MogpendiumContract.Categories[category]}\"}}"))).ToList();
        cases.Add(("unavailable", ActivitySnapshot.Unavailable("mogpendium_standard", "No stable owned selected event detail."), Args("{\"category\":\"mogpendium_standard\"}")));
        cases.Add(("standard_full_page1", Snapshot(), Args("{\"category\":\"mogpendium_standard\"}")));
        cases.Add(("standard_full_page2", Snapshot(), Args("{\"category\":\"mogpendium_standard\",\"offset\":50}")));
        cases.Add(("standard_catalog_filter", Snapshot(), Args("{\"category\":\"mogpendium_standard\",\"group\":\"catalog\"}")));
        cases.Add(("weekly_observed_list", ListSnapshot(1, true), Args("{\"category\":\"mogpendium_weekly\"}")));
        cases.Add(("weekly_list_only", ListSnapshot(1, false), Args("{\"category\":\"mogpendium_weekly\"}")));
        cases.Add(("minimog_observed_list", ListSnapshot(2, true), Args("{\"category\":\"mogpendium_minimog\"}")));
        cases.Add(("minimog_list_only", ListSnapshot(2, false), Args("{\"category\":\"mogpendium_minimog\"}")));
        cases.Add(("weekly_list_filtered", ListSnapshot(1, true), Args("{\"category\":\"mogpendium_weekly\",\"group\":\"observed_list\",\"offset\":2,\"limit\":2}")));
        var output = cases.Select(value => new { name = value.Item1, arguments = value.Item3, result = new ActivityLookup(value.Item2).Query(value.Item3) });
        var fullPath = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Produced Mogpendium C# wire fixtures.");
    }
    internal static void CapturedLists(string catalogPath, string weeklyPath, string minimogPath)
    {
        var seasons = JsonSerializer.Deserialize<MogpendiumSeason[]>(File.ReadAllText(catalogPath))!;
        foreach (var (category, path) in new[] { (1, weeklyPath), (2, minimogPath) })
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            Check(!root.GetProperty("truncated").GetBoolean());
            var texts = root.GetProperty("text_nodes").EnumerateArray().ToArray();
            string Text(uint id, params uint[] componentPath) => MogpendiumReader.Normalize(texts.Single(row => row.GetProperty("node_id").GetUInt32() == id &&
                row.GetProperty("component_path").EnumerateArray().Select(value => value.GetUInt32()).SequenceEqual(componentPath)).GetProperty("text").GetString()!);
            Check(MogpendiumReader.TryReward(Text(7, 38), out var primary));
            uint? extra = category == 2 && MogpendiumReader.TryReward(Text(9, 38), out var secondary) ? secondary : null;
            var view = new MogpendiumView(Text(53), Text(54), Text(58), category, Text((uint)(72 + category)), Text(3, 8), Text(6, 34), primary, extra);
            var renderers = root.GetProperty("mogpendium_lists").GetProperty("renderers").EnumerateArray().OrderBy(row => row.GetProperty("tree_vector_slot").GetInt32()).ToArray();
            var choices = new List<MogpendiumObservedChoice>(); string? week = null;
            foreach (var renderer in renderers.Skip(1))
            {
                var slot = renderer.GetProperty("tree_vector_slot").GetInt32();
                var nodes = renderer.GetProperty("component_path").EnumerateArray().Select(value => value.GetUInt32()).ToArray();
                var offset = (slot - 1) % (category + 1);
                if (offset == 0) week = Text(7, nodes);
                else choices.Add(new(week!, Text(4, nodes), offset));
            }
            var result = MogpendiumReader.Describe(Time, view, seasons, new(choices.ToArray()));
            Check(result is not null && result.Entries.Count(row => row.Group == "observed_list") == 6 * category);
            if (category == 1) Check(result!.Entries.Where(row => row.Group == "observed_list").All(row => row.ReferenceId is not null));
            else Check(!result!.Entries.Any(row => row.Group == "selection")); // Fishing target macro is deliberately unsupported.
            Console.WriteLine($"PASS supplied category{category}: {6 * category} corroborated observed choices; selected detail {result!.Entries.Any(row => row.Id == 10)}");
        }
    }
    private static JsonElement Args(string text) => JsonDocument.Parse(text).RootElement.Clone();
    private static void Check(bool value) { if (!value) throw new Exception("Mogpendium assertion failed"); }
    private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    private static void Throws(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Expected contract rejection"); }

    private sealed class Fixture : IDisposable
    {
        private readonly List<nint> allocations = [];
        internal readonly AtkUnitBase* Addon;
        internal readonly AtkTextNode* Title;
        internal readonly AtkTextNode* HeadingNode;
        internal readonly nint[] RootNodes;
        internal Fixture(MogpendiumView view)
        {
            Addon = Allocate<AtkUnitBase>();
            var eventParent = Node(52); var descriptionParent = Node(71);
            HeadingNode = Text(53, view.Heading, eventParent);
            var currency = Text(54, view.CurrencyName, eventParent);
            var secondary = Text(58, view.SecondaryCurrencyName!, eventParent);
            var root = new List<nint> { (nint)HeadingNode, (nint)currency, (nint)secondary };
            for (var index = 0; index < 4; ++index)
            {
                var description = Text((uint)(72 + index), Descriptions[index], descriptionParent);
                if (index != view.Category) { description->NodeFlags = 0; description->NodeText.StringPtr = (byte*)1; }
                root.Add((nint)description);
            }
            var titleComponent = Component(8); Title = Text(3, view.Title, Node(8));
            Nodes(&titleComponent->Component->UldManager, [(nint)Title]);
            var overviewComponent = Component(34); var overview = Text(6, view.Overview, Node(34));
            Nodes(&overviewComponent->Component->UldManager, [(nint)overview]);
            var rewardsComponent = Component(38); var rewardParent = Node(5);
            var primary = Text(7, "×" + view.PrimaryReward, rewardParent);
            var extra = Text(9, "×" + (view.SecondaryReward ?? 1), rewardParent);
            if (view.SecondaryReward is null) extra->NodeFlags = 0;
            Nodes(&rewardsComponent->Component->UldManager, [(nint)primary, (nint)extra]);
            // Stale scrolling component is deliberately unsafe to descend into.
            var stale = Component(79); stale->Component = (AtkComponentBase*)1;
            root.AddRange([(nint)titleComponent, (nint)overviewComponent, (nint)rewardsComponent, (nint)stale]);
            RootNodes = root.ToArray(); Nodes(&Addon->UldManager, RootNodes);
        }
        private T* Allocate<T>() where T : unmanaged
        {
            var pointer = (T*)NativeMemory.AllocZeroed((nuint)sizeof(T)); allocations.Add((nint)pointer); return pointer;
        }
        private AtkResNode* Node(uint id)
        {
            var pointer = Allocate<AtkResNode>(); pointer->NodeId = id; pointer->NodeFlags = NodeFlags.Visible; pointer->Color.A = 255; return pointer;
        }
        private AtkComponentNode* Component(uint id)
        {
            var pointer = Allocate<AtkComponentNode>(); pointer->NodeId = id; pointer->NodeFlags = NodeFlags.Visible; pointer->Color.A = 255;
            pointer->Type = (NodeType)1005; pointer->Component = Allocate<AtkComponentBase>(); return pointer;
        }
        private AtkTextNode* Text(uint id, string value, AtkResNode* parent)
        {
            var pointer = Allocate<AtkTextNode>(); pointer->NodeId = id; pointer->Type = NodeType.Text; pointer->ParentNode = parent;
            pointer->NodeFlags = NodeFlags.Visible; pointer->Color.A = 255;
            SetText(pointer, Encoding.UTF8.GetBytes(value));
            return pointer;
        }
        internal void SetText(AtkTextNode* pointer, byte[] bytes)
        {
            var buffer = (byte*)NativeMemory.AllocZeroed((nuint)(bytes.Length + 1)); allocations.Add((nint)buffer);
            bytes.CopyTo(new Span<byte>(buffer, bytes.Length)); pointer->NodeText.StringPtr = buffer;
            pointer->NodeText.StringLength = bytes.Length; pointer->NodeText.BufUsed = pointer->NodeText.BufSize = bytes.Length + 1;
        }
        internal void Nodes(AtkUldManager* manager, nint[] nodes)
        {
            var pointers = (AtkResNode**)NativeMemory.AllocZeroed((nuint)(nodes.Length * IntPtr.Size)); allocations.Add((nint)pointers);
            for (var index = 0; index < nodes.Length; ++index) pointers[index] = (AtkResNode*)nodes[index];
            manager->NodeList = pointers; manager->NodeListCount = (ushort)nodes.Length;
        }
        public void Dispose() { foreach (var allocation in allocations) NativeMemory.Free((void*)allocation); }
    }
}
