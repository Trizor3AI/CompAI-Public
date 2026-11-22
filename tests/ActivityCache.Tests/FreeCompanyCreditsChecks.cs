using System.Text.Json;

namespace CompAI;

public sealed partial class Plugin
{
    internal static void ProduceFcCreditFixtures(string path)
    {
        var now = DateTimeOffset.UtcNow;
        const string category = "free_company_credits";
        var available = CaptureFreeCompanyCredits(new(now, 12345));
        var zero = CaptureFreeCompanyCredits(new(now, 0));
        var maximum = CaptureFreeCompanyCredits(new(now, uint.MaxValue));
        var unavailable = CaptureFreeCompanyCredits(null);
        Check(available.Entries.Single().Value == 12345 && zero.Entries.Single().Value == 0 &&
            maximum.Entries.Single().Value == uint.MaxValue && !unavailable.Available,
            "FC credits preserve exact UInt range and unavailable is not zero");
        Throws<ArgumentException>(() => new ActivitySnapshot(category, now, true, []));
        Throws<ArgumentException>(() => new ActivitySnapshot(category, now, true,
            available.Entries.Select(row => row with { Value = (long)uint.MaxValue + 1 })));
        Throws<ArgumentException>(() => new ActivitySnapshot(category, now, true,
            available.Entries.Select(row => row with { ReferenceId = 5, ReferenceKind = "free_company" })));
        Throws<ArgumentException>(() => new ActivitySnapshot(category, now, true,
            available.Entries.Select(row => row with { Text = "Unverified company name" })));
        var cases = new Dictionary<string, object>();
        var args = Arguments(category);
        foreach (var (name, snapshot) in new[] { ("all", available), ("zero", zero), ("maximum", maximum), ("unavailable", unavailable) })
            cases[name] = new { arguments = args,
                result = new ActivityLookup(snapshot.Available ? snapshot.AsLastObserved() : snapshot).Query(args) };
        foreach (var quantity in new uint[] { 0, 23456 })
        {
            var topics = CaptureFreeCompanyCredits(new(now, quantity, FreeCompanyCreditsSource.Topics));
            Check(topics.Entries.Single().Name == "Company credits shown on Topics", "Topics source remains visible");
            cases[quantity == 0 ? "topics_zero" : "topics"] = new { arguments = args,
                result = new ActivityLookup(topics.AsLastObserved()).Query(args) };
        }
        File.WriteAllText(path, JsonSerializer.Serialize(cases));
        Console.WriteLine("PASS FC credit projection, UInt bounds, absent identity and current bridge producer");
    }
}
