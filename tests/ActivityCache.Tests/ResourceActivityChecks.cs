using System.Text.Json;

namespace CompAI;

public sealed partial class Plugin
{
    private static IslandMaterialsObservation MaterialObservation(DateTimeOffset time) => new(time, Array.AsReadOnly(new[]
    {
        new IslandMaterialBalance(37551, "Synthetic leaf", 12, 0, 1, "Materials"),
        new IslandMaterialBalance(37552, "Synthetic feed", 0, 107, 6, "Feed"),
    }));
    private static CosmicMissionObservation MissionObservation(DateTimeOffset time) => new(time, Array.AsReadOnly(new[]
    {
        new CosmicMissionEntry(20, "Synthetic EX mission", 8, "Carpenter", "Gold"),
        new CosmicMissionEntry(15, "Synthetic gathering mission", 18, "Fisher", "Silver"),
    }));
    internal static void ProduceResourceFixtures(string path)
    {
        var now = DateTimeOffset.UtcNow;
        var materials = CaptureIslandMaterials(MaterialObservation(now));
        Check(materials.Entries[0].Id == 1 && materials.Entries[0].Value == 12 && materials.Entries[0].Slot is null,
            "Pouch row zero is a real public resource, not an empty slot");
        Check(materials.Entries[1].Value == 0, "identified observed zero is retained");
        Throws<ArgumentException>(() => new ActivitySnapshot("island_materials", now, true,
            materials.Entries.Select(row => row with { Group = "pouch_category_10" })));
        Throws<ArgumentException>(() => new ActivitySnapshot("island_materials", now, true,
            materials.Entries.Select(row => row with { Value = 1000 })));
        Throws<ArgumentException>(() => new ActivitySnapshot("island_materials", now, true,
            materials.Entries.Select(row => row with { ReferenceId = 37551 })));

        var missions = CaptureCosmicMissions(MissionObservation(now));
        Check(missions.Entries[0].ReferenceId == 15 && missions.Entries[0].Text == "Silver" &&
            missions.Entries[0].Group == "class_job_18" && missions.Entries[1].Text == "Gold",
            "Mission projection sorts stable public references and preserves observed medal/job");
        foreach (var invalid in new[] { "Bronze", "Complete", "No medal", "" })
            Throws<ArgumentException>(() => new ActivitySnapshot("cosmic_missions", now, true,
                missions.Entries.Select(row => row with { Text = invalid })));
        Throws<ArgumentException>(() => new ActivitySnapshot("cosmic_missions", now, true,
            missions.Entries.Select(row => row with { Group = "class_job_1" })));
        Throws<ArgumentException>(() => new ActivitySnapshot("cosmic_missions", now, true,
            missions.Entries.Select(row => row with { ReferenceId = 15, Slot = 18, Group = "class_job_18" })));
        Throws<ArgumentException>(() => new ActivitySnapshot("cosmic_missions", now, true,
            missions.Entries.Skip(1)));

        var fixtures = new Dictionary<string, object>();
        void Add(string name, ActivitySnapshot snapshot, string arguments)
        {
            var args = JsonDocument.Parse(arguments).RootElement.Clone();
            fixtures[name] = new { arguments = args,
                result = new ActivityLookup(snapshot.Available ? snapshot.AsLastObserved() : snapshot).Query(args) };
        }
        Add("materials_all", materials, "{\"category\":\"island_materials\"}");
        Add("materials_zero", materials, "{\"category\":\"island_materials\",\"query\":\"feed\"}");
        Add("materials_empty", CaptureIslandMaterials(new(now, Array.AsReadOnly(Array.Empty<IslandMaterialBalance>()))), "{\"category\":\"island_materials\"}");
        Add("materials_unavailable", CaptureIslandMaterials(null), "{\"category\":\"island_materials\"}");
        foreach (var quantity in new[] { 0, 150 })
        {
            var token = CaptureIslandMaterials(new(now, Array.AsReadOnly(new[] {
                new IslandMaterialBalance(41668, "Felicitous Token", quantity, 108, 10, "Tokens") })));
            Check(token.Entries.Single().Id == 109 && token.Entries.Single().ReferenceId == 41668, "exact token projection");
            Throws<ArgumentException>(() => new ActivitySnapshot("island_materials", now, true,
                token.Entries.Select(row => row with { Value = 151 })));
            foreach (var category in new[] { 1, 3, 4, 5, 6, 7 })
                foreach (var badRow in new[] {
                    token.Entries.Single() with { Group = $"pouch_category_{category}" },
                    token.Entries.Single() with { Group = $"pouch_category_{category}", Value = 999 },
                    token.Entries.Single() with { Group = $"pouch_category_{category}", Id = 110 },
                    token.Entries.Single() with { Group = $"pouch_category_{category}", ReferenceId = 1 } })
                    Throws<ArgumentException>(() => new ActivitySnapshot("island_materials", now, true, new[] { badRow }));
            Add(quantity == 0 ? "token_zero" : "token_maximum", token, "{\"category\":\"island_materials\"}");
        }
        Add("missions_all", missions, "{\"category\":\"cosmic_missions\"}");
        Add("missions_filtered", missions, "{\"category\":\"cosmic_missions\",\"group\":\"class_job_8\"}");
        Add("missions_page", missions, "{\"category\":\"cosmic_missions\",\"limit\":1}");
        Add("missions_empty", CaptureCosmicMissions(new(now, Array.AsReadOnly(Array.Empty<CosmicMissionEntry>()))), "{\"category\":\"cosmic_missions\"}");
        Add("missions_unavailable", CaptureCosmicMissions(null), "{\"category\":\"cosmic_missions\"}");
        File.WriteAllText(path, JsonSerializer.Serialize(fixtures));
        Console.WriteLine("PASS resource projections preserve row zero, explicit quantities, positive medals and unknown absence");
    }
}
