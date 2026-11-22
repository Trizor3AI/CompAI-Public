using System.Runtime.Loader;
using System.Text.Json;
using CompAI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

var libraryPath = Environment.GetEnvironmentVariable("DALAMUD_LIB_PATH") ??
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "addon", "Hooks", "dev");
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var path = Path.Combine(libraryPath, name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
Tests.Run();
if (args is ["--produce-fixtures", var path]) Tests.WriteProducerFixtures(path);

internal static class Tests
{
    private static int passed;
    private static GcSupplyEntry Entry(int position = 0) => new(position, (uint)(position + 8),
        GcSupplySnapshot.JobAbbreviation(position), position < 8 ? "supply" : "provisioning",
        (uint)(1000 + position), "Synthetic item " + position, position < 8 ? 1 : 10, 12345, 678, false, null);
    private static GcSupplySnapshot Full() => new(DateTimeOffset.UtcNow, true, Enumerable.Range(0, 11).Select(Entry));
    internal static void Run()
    {
        Test("fixed positions map eight crafting and three gathering requests", () =>
        {
            var result = new GcSupplyLookup(Full()).Query(Args("{}"));
            Check(result.available && result.entries.Length == 11 && result.entries[0].class_job_abbreviation == "CRP");
            Check(result.entries[10].class_job_id == 18 && result.entries[10].class_job_abbreviation == "FSH");
            Check(new GcSupplyLookup(Full()).Query(Args("{\"category\":\"supply\"}")).entries.Length == 8);
            Check(new GcSupplyLookup(Full()).Query(Args("{\"category\":\"provisioning\"}")).entries.Length == 3);
        });
        Test("eligibility false is retained separately from unknown bonus and never completion", () =>
        {
            Check(GcSupplyReader.TurnInFlag(0) == true && GcSupplyReader.TurnInFlag(1) == false && GcSupplyReader.TurnInFlag(2) is null);
            var result = new GcSupplyLookup(Full()).Query(Args("{}"));
            Check(result.entries.All(row => row.turn_in_available == false && row.bonus_reward is null));
            var json = JsonSerializer.Serialize(result);
            Check(!json.Contains("\"completed\"") && !json.Contains("possessed") && !json.Contains("OwnerContentId"));
        });
        Test("synthetic installed structs preserve item identities and exclude expert rows", SyntheticNativeRows);
        Test("empty displayed delivery list does not hide independently populated daily requests", SyntheticEmptyDeliveryList);
        Test("all native bonus bytes remain unknown even after ineligible hand-in state", SyntheticUnverifiedBonusBytes);
        Test("expiry rejects retained personal rows but preserves availability reason", () =>
        {
            var time = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(31);
            var snapshot = new GcSupplySnapshot(time, true, [Entry()]);
            Check(!snapshot.IsFresh(DateTimeOffset.UtcNow));
            var result = new GcSupplyLookup(snapshot).Query(Args("{}"));
            Check(!result.available && result.entries.Length == 0 && result.unavailable_reason is not null);
            Check(!new GcSupplySnapshot(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3), true, [Entry()]).IsFresh(DateTimeOffset.UtcNow));
        });
        Test("invalid arguments and duplicate category keys rejected", () =>
        {
            foreach (var argument in new[] { "null", "[]", "{\"category\":null}", "{\"category\":\"expert\"}",
                         "{\"item_ids\":[1]}", "{\"category\":\"all\",\"category\":\"supply\"}" })
                Throws(() => GcSupplyLookup.ValidateArguments(Args(argument)));
        });
        Test("unavailable empty duplicate out of bounds and mislabelled observations are rejected", () =>
        {
            Throws(() => new GcSupplySnapshot(DateTimeOffset.UtcNow, true, []));
            Throws(() => new GcSupplySnapshot(DateTimeOffset.UtcNow, false, [Entry()], "Closed"));
            Throws(() => new GcSupplySnapshot(DateTimeOffset.UtcNow, true, [Entry(), Entry()]));
            foreach (var entry in new[] { Entry() with { Position = 11 }, Entry() with { ClassJobId = 18 },
                         Entry() with { Category = "provisioning" }, Entry() with { QuantityRequested = 0 },
                         Entry() with { ExperienceReward = -1 }, Entry() with { Name = "bad\nname" },
                         Entry() with { BonusReward = true }, Entry() with { BonusReward = false } })
                Throws(() => new GcSupplySnapshot(DateTimeOffset.UtcNow, true, [entry]));
            var result = new GcSupplyLookup(GcSupplySnapshot.Unavailable("Open delivery window")).Query(Args("{}"));
            Check(!result.available && result.entries.Length == 0 && result.matched_count == 0 && !result.truncated);
        });
        Test("snapshots and result arrays cannot mutate frozen observations", () =>
        {
            var source = new[] { Entry() };
            var lookup = new GcSupplyLookup(new(DateTimeOffset.UtcNow, true, source));
            source[0] = Entry(1);
            lookup.Query(Args("{}")).entries[0] = new(9, 17, "BTN", "provisioning", 9, "Changed", 1, 0, 0, true, true);
            Check(lookup.Query(Args("{}")).entries[0].position == 0);
        });
        Test("maximum multilingual payload fits protocol bound", () =>
        {
            var snapshot = new GcSupplySnapshot(DateTimeOffset.UtcNow, true,
                Enumerable.Range(0, 11).Select(position => Entry(position) with { Name = new('界', 128) }));
            Check(JsonSerializer.SerializeToUtf8Bytes(new GcSupplyLookup(snapshot).Query(Args("{}"))).Length <= GcSupplyLookup.MaxBytes);
        });
        Console.WriteLine($"{passed} Grand Company fixture groups passed; menu hydration requires in-game verification.");
    }
    private static unsafe void SyntheticNativeRows()
    {
        // These are zeroed stack values, never pointers into the game process.
        SupplyProvisioningData source = default;
        var items = stackalloc GrandCompanyItem[12];
        new Span<GrandCompanyItem>(items, 12).Clear();
        source.SupplyData[0].ItemId = 1000;
        source.SupplyData[0].NumRequested = 1;
        source.SupplyData[0].ExpReward = 12345;
        source.SupplyData[0].SealReward = 678;
        source.ProvisioningData[2].ItemId = 1010;
        source.ProvisioningData[2].NumRequested = 10;
        items[0].Position = 0;
        items[0].ItemId = 1000;
        items[0].NumRequested = 1;
        items[0].TurnInAvailable = 0;
        items[0].Bonus = 1;
        // A matching-looking expert row must not be traversed or create an ambiguous daily match.
        items[11] = items[0];
        AgentGrandCompanySupply agent = default;
        agent.SupplyProvisioningData = &source;
        agent.ItemArray = items;
        agent.NumItems = 12;
        var rows = GcSupplyReader.CopyRows(&agent);
        Check(rows.Length == 2 && rows[0].Position == 0 && rows[1].Position == 10);
        Check(rows[0].TurnInAvailable == items[0].IsTurnInAvailable && rows[0].TurnInAvailable == true);
        Check(items[0].IsBonusReward && rows[0].BonusReward is null);
        Check(rows[1].TurnInAvailable is null && rows[1].BonusReward is null);
        items[0].ItemId = 9999;
        Check(GcSupplyReader.CopyRows(&agent)[0].TurnInAvailable is null);
    }
    private static unsafe void SyntheticUnverifiedBonusBytes()
    {
        SupplyProvisioningData source = default;
        GrandCompanyItem item = default;
        source.SupplyData[0].ItemId = item.ItemId = 1000;
        source.SupplyData[0].NumRequested = 1;
        item.NumRequested = 1;
        item.Position = 0;
        AgentGrandCompanySupply agent = default;
        agent.SupplyProvisioningData = &source;
        agent.ItemArray = &item;
        agent.NumItems = 1;
        // Both eligible and ineligible rows must withhold star assertions for
        // every byte value; an ineligible row does not prove a completed hand-in.
        foreach (var eligibility in new byte[] { 0, 1, 2 })
        {
            item.TurnInAvailable = eligibility;
            for (var bonus = 0; bonus <= byte.MaxValue; bonus++)
            {
                item.Bonus = (byte)bonus;
                var row = GcSupplyReader.CopyRows(&agent)[0];
                Check(row.BonusReward is null && row.TurnInAvailable == GcSupplyReader.TurnInFlag(eligibility));
            }
        }
    }
    private static unsafe void SyntheticEmptyDeliveryList()
    {
        SupplyProvisioningData source = default;
        source.SupplyData[0].ItemId = 1000;
        source.SupplyData[0].NumRequested = 1;
        source.ProvisioningData[2].ItemId = 1010;
        source.ProvisioningData[2].NumRequested = 10;
        AgentGrandCompanySupply agent = default;
        agent.SupplyProvisioningData = &source;
        // No displayed rows or item-list allocation. No claim about why it is empty.
        Check(GcSupplyReader.HasRequestData(&agent));
        var rows = GcSupplyReader.CopyRows(&agent);
        Check(rows.Length == 2 && rows[0].ItemId == 1000 && rows[1].Position == 10);
        Check(rows.All(row => row.TurnInAvailable is null && row.BonusReward is null));
        agent.NumItems = 1;
        Check(!GcSupplyReader.HasRequestData(&agent));
        agent.NumItems = -1;
        Check(!GcSupplyReader.HasRequestData(&agent));
        agent.NumItems = 10001;
        Check(!GcSupplyReader.HasRequestData(&agent));
        agent.NumItems = 0;
        agent.SupplyProvisioningData = null;
        Check(!GcSupplyReader.HasRequestData(&agent) && !GcSupplyReader.HasRequestData(null));
        // Empty fixed arrays still yield no requests; caller keeps unavailable.
        source = default;
        agent.SupplyProvisioningData = &source;
        Check(GcSupplyReader.CopyRows(&agent).Length == 0);
        source.SupplyData[0].ItemId = 1000;
        try { GcSupplyReader.CopyRows(&agent); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Unhydrated requested quantity must still fail closed.");
    }
    internal static void WriteProducerFixtures(string output)
    {
        static object Fixture(string name, GcSupplySnapshot snapshot, string args)
        { var arguments = Args(args); return new { name, arguments, result = new GcSupplyLookup(snapshot).Query(arguments) }; }
        var fixtures = new[] { Fixture("all_jobs", Full(), "{}"), Fixture("provisioning", Full(), "{\"category\":\"provisioning\"}"),
            Fixture("unavailable", GcSupplySnapshot.Unavailable("Window closed"), "{}"),
            Fixture("stale", new(DateTimeOffset.UtcNow.AddSeconds(-31), true, [Entry()]), "{}") };
        var path = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(fixtures));
    }
    private static JsonElement Args(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private static void Check(bool value) { if (!value) throw new Exception("Assertion failed"); }
    private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    private static void Throws(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Expected invalid input rejection"); }
}
