using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using CompAI;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
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
TopicsChecks.Run();
if (args is ["--produce-fixtures", var output])
    File.WriteAllText(output, JsonSerializer.Serialize(new {
        all = Tests.Capture(Tests.Sample()), zero = Tests.Capture(Tests.Sample(0)),
        maximum = Tests.Capture(Tests.Sample(uint.MaxValue)), unavailable = (FreeCompanyCreditsObservation?)null,
        topics = TopicsChecks.Capture("1,234,567"), topicsZero = TopicsChecks.Capture("0"),
        topicsMaximum = TopicsChecks.Capture("4,294,967,295"),
    }, new JsonSerializerOptions { WriteIndented = true }));

internal static unsafe class Tests
{
    private static int passed;
    private static readonly FreeCompanyCreditsOwner Owner = new(8, 100, 16, 24, 32, 40, 10, 48, 150, 2);
    internal static FreeCompanyCreditsSample Sample(uint credits = 1234567) => new(30, credits, [new(100, 200), new(101, 300)]);
    private static bool RankExists(uint rank) => rank is >= 1 and <= 30;
    private static bool OfferExists(FreeCompanyCreditOffer row) => row is { ItemId: 100, Cost: 200 } or { ItemId: 101, Cost: 300 };
    internal static FreeCompanyCreditsObservation? Capture(FreeCompanyCreditsSample sample) =>
        FreeCompanyCreditsReader.CaptureStable(() => Owner, _ => sample, RankExists, OfferExists, () => true);

    internal static void Run()
    {
        Test("only stable balance capture time and exact source enter the detached DTO", () =>
        {
            var before = DateTimeOffset.UtcNow; var result = Capture(Sample())!;
            Check(result.Credits == 1234567 && result.Source == FreeCompanyCreditsSource.ItemShop &&
                result.CapturedAtUtc >= before && result.CapturedAtUtc <= DateTimeOffset.UtcNow);
            var json = JsonSerializer.Serialize(result);
            using var document = JsonDocument.Parse(json);
            Check(document.RootElement.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "CapturedAtUtc", "Credits", "Source" }));
        });
        Test("identified ready zero and full native UInt range are preserved", () =>
        {
            Check(Capture(Sample(0))?.Credits == 0);
            Check(Capture(Sample(uint.MaxValue))?.Credits == uint.MaxValue);
        });
        Test("invalid owner identities pointers and bounds reject before copying", () =>
        {
            foreach (var owner in new[] { Owner with { Player = 0 }, Owner with { ContentId = 0 }, Owner with { Agents = 0 },
                Owner with { Addons = 0 }, Owner with { Agent = 0 }, Owner with { Addon = 0 }, Owner with { Values = 0 },
                Owner with { Player = -8 }, Owner with { Agent = 33 }, Owner with { Addon = 44 }, Owner with { Values = 49 },
                Owner with { AddonId = 0 }, Owner with { AddonId = 65536 }, Owner with { OfferCount = 0 },
                Owner with { OfferCount = 21 }, Owner with { OfferCount = uint.MaxValue }, Owner with { ValueCount = 131 },
                Owner with { ValueCount = 4097 } })
                Check(FreeCompanyCreditsReader.CaptureStable(() => owner, _ => throw new Exception("Unexpected copy"), RankExists, OfferExists, () => true) is null);
            Check(FreeCompanyCreditsReader.CaptureStable(() => null, _ => throw new Exception("Unexpected copy"), RankExists, OfferExists, () => true) is null);
        });
        Test("the one and twenty offer boundaries use the actual buffer size", () =>
        {
            Check(FreeCompanyCreditsReader.ValidOwner(Owner with { OfferCount = 1, ValueCount = 131 }));
            Check(FreeCompanyCreditsReader.ValidOwner(Owner with { OfferCount = 20, ValueCount = 150 }));
            Check(!FreeCompanyCreditsReader.ValidOwner(Owner with { OfferCount = 20, ValueCount = 149 }));
            Check(!FreeCompanyCreditsReader.ValidOwner(Owner with { OfferCount = 1, ValueCount = 130 }));
            using var f = new Fixture();
            f.Values[9].UInt = 20;
            for (var i = 0; i < 20; i++) { f.Values[30 + i] = UInt(100); f.Values[130 + i] = UInt(200); }
            var owner = f.Owner with { OfferCount = 20 };
            Check(FreeCompanyCreditsReader.CaptureStable(() => owner, FreeCompanyCreditsReader.Copy, RankExists, OfferExists, () => true)?.Credits == 1234567);
        });
        Test("unpopulated or invalid samples cannot turn into a zero balance", () =>
        {
            foreach (var sample in new[] { Sample(0) with { Rank = 0 }, Sample(0) with { Offers = [] },
                Sample(0) with { Offers = [new(100, 200)] }, Sample(0) with { Offers = [new(0, 200), new(101, 300)] } })
                Check(Capture(sample) is null);
            Check(FreeCompanyCreditsReader.CaptureStable(() => Owner, _ => null, RankExists, OfferExists, () => true) is null);
        });
        Test("every offer and the displayed rank must have public corroboration", () =>
        {
            Check(Capture(Sample() with { Rank = 31 }) is null);
            Check(Capture(Sample() with { Offers = [new(100, 201), new(101, 300)] }) is null);
            Check(Capture(Sample() with { Offers = [new(102, 200), new(101, 300)] }) is null);
            Check(FreeCompanyCreditsReader.CaptureStable(() => Owner, _ => Sample(), RankExists, _ => false, () => true) is null);
        });
        Test("changed balance rank item or price prevents the complete observation", () =>
        {
            foreach (var second in new[] { Sample(1), Sample() with { Rank = 29 },
                Sample() with { Offers = [new(101, 300), new(100, 200)] },
                Sample() with { Offers = [new(100, 201), new(101, 300)] } })
            {
                var copies = 0;
                Check(FreeCompanyCreditsReader.CaptureStable(() => Owner, _ => ++copies == 1 ? Sample() : second,
                    RankExists, OfferExists, () => true) is null);
            }
        });
        Test("owner changes during public resolution stop before a second copy", () =>
        {
            foreach (var changed in new[] { Owner with { ContentId = 101 }, Owner with { Player = 80 }, Owner with { Agent = 80 },
                Owner with { Addon = 80 }, Owner with { Values = 80 }, Owner with { ValueCount = 151 }, Owner with { OfferCount = 1 } })
            {
                var current = Owner; var copies = 0;
                Check(FreeCompanyCreditsReader.CaptureStable(() => current, _ => { copies++; return Sample(); }, RankExists,
                    offer => { current = changed; return OfferExists(offer); }, () => true) is null);
                Check(copies == 1);
            }
        });
        Test("changed public rank offer or catalog is rejected on revalidation", () =>
        {
            var rankCalls = 0;
            Check(FreeCompanyCreditsReader.CaptureStable(() => Owner, _ => Sample(), _ => ++rankCalls == 1, OfferExists, () => true) is null);
            var offerCalls = 0;
            Check(FreeCompanyCreditsReader.CaptureStable(() => Owner, _ => Sample(), RankExists, _ => ++offerCalls <= 2, () => true) is null);
            var catalogCalls = 0;
            Check(FreeCompanyCreditsReader.CaptureStable(() => Owner, _ => Sample(), RankExists, OfferExists, () => ++catalogCalls < 2) is null);
        });
        Test("only the exact ready visible agent-owned FC item shop matches", () =>
        {
            Check(FreeCompanyCreditsReader.MatchesAddon(8, 10, "FreeCompanyCreditShop", 8, 10, true, true));
            Check(!FreeCompanyCreditsReader.MatchesAddon(8, 10, "FreeCompany", 8, 10, true, true));
            Check(!FreeCompanyCreditsReader.MatchesAddon(8, 10, "FreeCompanyCreditShop", 16, 10, true, true));
            Check(!FreeCompanyCreditsReader.MatchesAddon(8, 10, "FreeCompanyCreditShop", 8, 11, true, true));
            Check(!FreeCompanyCreditsReader.MatchesAddon(8, 10, "FreeCompanyCreditShop", 8, 10, false, true));
            Check(!FreeCompanyCreditsReader.MatchesAddon(8, 10, "FreeCompanyCreditShop", 8, 10, true, false));
            Check(!FreeCompanyCreditsReader.MatchesAddon(9, 10, "FreeCompanyCreditShop", 9, 10, true, true));
        });
        Test("typed native copier reads the documented UInt header and offer slots", () =>
        {
            using var f = new Fixture();
            var result = FreeCompanyCreditsReader.CaptureStable(() => f.Owner, FreeCompanyCreditsReader.Copy, RankExists, OfferExists, () => true)!;
            Check(result.Credits == 1234567);
            f.Values[3].UInt = 0;
            Check(result.Credits == 1234567 && FreeCompanyCreditsReader.Copy(f.Owner)?.Credits == 0);
            f.Values[3].UInt = uint.MaxValue;
            Check(FreeCompanyCreditsReader.Copy(f.Owner)?.Credits == uint.MaxValue);
        });
        Test("all copied native values require their documented UInt union type", () =>
        {
            using var f = new Fixture();
            foreach (var index in new[] { 0, 3, 9, 30, 31, 130, 131 })
            {
                f.Values[index].Type = AtkValueType.Int;
                Check(FreeCompanyCreditsReader.Copy(f.Owner) is null);
                f.Values[index].Type = AtkValueType.UInt;
            }
        });
        Test("native owner loading pointer and header changes fail closed", () =>
        {
            using var f = new Fixture();
            f.Player->IsLoaded = false; Check(FreeCompanyCreditsReader.Copy(f.Owner) is null); f.Player->IsLoaded = true;
            f.Player->ContentId++; Check(FreeCompanyCreditsReader.Copy(f.Owner) is null); f.Player->ContentId--;
            f.Agent->AddonId++; Check(FreeCompanyCreditsReader.Copy(f.Owner) is null); f.Agent->AddonId--;
            f.Addon->Id++; Check(FreeCompanyCreditsReader.Copy(f.Owner) is null); f.Addon->Id--;
            f.Addon->AtkValues = null; Check(FreeCompanyCreditsReader.Copy(f.Owner) is null); f.Addon->AtkValues = f.Values;
            f.Addon->AtkValuesCount--; Check(FreeCompanyCreditsReader.Copy(f.Owner) is null); f.Addon->AtkValuesCount++;
            f.Values[9].UInt = 0; Check(FreeCompanyCreditsReader.Copy(f.Owner) is null);
            Check(FreeCompanyCreditsReader.Copy(f.Owner with { Values = (nint)f.Values + 1 }) is null);
        });
        Test("irrelevant inventory and transaction fields do not enter the observation", () =>
        {
            using var f = new Fixture();
            var first = FreeCompanyCreditsReader.Copy(f.Owner)!;
            foreach (var index in new[] { 1, 5, 10, 50, 70, 90, 110 })
                f.Values[index] = UInt(uint.MaxValue);
            var second = FreeCompanyCreditsReader.Copy(f.Owner)!;
            Check(first.Rank == second.Rank && first.Credits == second.Credits && first.Offers.SequenceEqual(second.Offers));
        });
        Console.WriteLine($"{passed} Free Company credit groups passed; synthetic only, live item-shop balance comparison remains required.");
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly PlayerState* Player = (PlayerState*)NativeMemory.AllocZeroed((nuint)sizeof(PlayerState));
        internal readonly AgentInterface* Agent = (AgentInterface*)NativeMemory.AllocZeroed((nuint)sizeof(AgentInterface));
        internal readonly AtkUnitBase* Addon = (AtkUnitBase*)NativeMemory.AllocZeroed((nuint)sizeof(AtkUnitBase));
        internal readonly AtkValue* Values = (AtkValue*)NativeMemory.AllocZeroed((nuint)(150 * sizeof(AtkValue)));
        internal readonly FreeCompanyCreditsOwner Owner;
        internal Fixture()
        {
            Player->IsLoaded = true; Player->ContentId = 100; Agent->AddonId = 10;
            Addon->Id = 10; Addon->AtkValues = Values; Addon->AtkValuesCount = 150;
            Values[0] = UInt(30); Values[3] = UInt(1234567); Values[9] = UInt(2);
            Values[30] = UInt(100); Values[31] = UInt(101); Values[130] = UInt(200); Values[131] = UInt(300);
            Owner = Tests.Owner with { Player = (nint)Player, Agent = (nint)Agent, Addon = (nint)Addon, Values = (nint)Values };
        }
        public void Dispose() { NativeMemory.Free(Values); NativeMemory.Free(Addon); NativeMemory.Free(Agent); NativeMemory.Free(Player); }
    }
    private static AtkValue UInt(uint value) => new() { Type = AtkValueType.UInt, UInt = value };
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("FC credits assertion failed."); }
    private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
}
