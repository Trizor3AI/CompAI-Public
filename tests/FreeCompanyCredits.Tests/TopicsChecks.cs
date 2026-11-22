using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CompAI;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;

internal static unsafe class TopicsChecks
{
    private static int passed;
    internal static FreeCompanyCreditsObservation? Capture(string text)
    {
        using var f = new Fixture(); f.SetText(text);
        return FreeCompanyTopicsCreditsReader.CaptureStable(() => f.Owner, FreeCompanyTopicsCreditsReader.Copy);
    }

    internal static void Run()
    {
        Test("Topics native copy preserves confirmed grouping zero and UInt32 maximum with exact source", () =>
        {
            var before = DateTimeOffset.UtcNow;
            var observed = Capture("1,234,567")!;
            Check(observed.Credits == 1234567 && observed.Source == FreeCompanyCreditsSource.Topics &&
                observed.CapturedAtUtc >= before && observed.CapturedAtUtc <= DateTimeOffset.UtcNow);
            Check(Capture("0")?.Credits == 0 && Capture("4,294,967,295")?.Credits == uint.MaxValue);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(observed));
            Check(json.RootElement.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(["CapturedAtUtc", "Credits", "Source"]));
        });
        Test("numeric parser accepts only canonical ASCII decimals and strict comma groups", () =>
        {
            foreach (var (text, value) in new (string, uint)[] { ("0", 0), ("999", 999), ("1,000", 1000),
                ("1234567", 1234567), ("1,234,567", 1234567), ("4294967295", uint.MaxValue), ("4,294,967,295", uint.MaxValue) })
                Check(FreeCompanyTopicsCreditsReader.TryParseBalance(Encoding.UTF8.GetBytes(text), out var result) && result == value);
            foreach (var text in new[] { "", "00", "01", "0,000", "1,00", "1,0000", "1000,000", ",123", "123,", "1,,000",
                "4294967296", "4,294,967,296", "-1", "+1", " 1", "1 ", "1\n", "1.000", "1 000", "1\u00a0000",
                "９", "1e3", "NaN", "1\0", "\u00021,000\u0003", "Credits 1,000" })
                Check(!FreeCompanyTopicsCreditsReader.TryParseBalance(Encoding.UTF8.GetBytes(text), out _));
        });
        Test("Topics invalid pointers identities and all other tabs reject before copying", () =>
        {
            using var f = new Fixture();
            foreach (var owner in new[] { f.Owner with { Player = 0 }, f.Owner with { Player = -8 }, f.Owner with { ContentId = 0 },
                f.Owner with { Info = 0 }, f.Owner with { Agents = 0 }, f.Owner with { Addons = 0 }, f.Owner with { Agent = 1 },
                f.Owner with { Profile = 0 }, f.Owner with { Members = 0 }, f.Owner with { CompanyId = 0 }, f.Owner with { Addon = 0 },
                f.Owner with { AddonId = 0 }, f.Owner with { AddonId = 65536 }, f.Owner with { Root = 1 }, f.Owner with { NodeList = 1 },
                f.Owner with { NodeCount = 0 }, f.Owner with { NodeCount = 513 } }.Concat(
                    Enumerable.Range(1, 255).Select(tab => f.Owner with { SelectedTab = (byte)tab })))
                Check(FreeCompanyTopicsCreditsReader.CaptureStable(() => owner, _ => throw new Exception("Unexpected copy")) is null);
            Check(FreeCompanyTopicsCreditsReader.CaptureStable(() => null, _ => throw new Exception("Unexpected copy")) is null);
        });
        Test("only exact ready visible FreeCompany addon identity matches Topics", () =>
        {
            Check(FreeCompanyTopicsCreditsReader.MatchesAddon(8, 10, "FreeCompany", 8, 10, true, true));
            Check(!FreeCompanyTopicsCreditsReader.MatchesAddon(8, 10, "FreeCompanyCreditShop", 8, 10, true, true));
            Check(!FreeCompanyTopicsCreditsReader.MatchesAddon(8, 10, "FreeCompany", 16, 10, true, true));
            Check(!FreeCompanyTopicsCreditsReader.MatchesAddon(8, 10, "FreeCompany", 8, 11, true, true));
            Check(!FreeCompanyTopicsCreditsReader.MatchesAddon(8, 10, "FreeCompany", 8, 10, false, true));
            Check(!FreeCompanyTopicsCreditsReader.MatchesAddon(8, 10, "FreeCompany", 8, 10, true, false));
        });
        Test("exact root text17 parent16 is required and unrelated text buffers are never inspected", () =>
        {
            using var f = new Fixture(); Check(f.Copy() is not null); // Unrelated text has an invalid buffer by design.
            f.Text->NodeId = 18; Check(f.Copy() is null); f.Text->NodeId = 17;
            f.Text->Type = NodeType.Image; Check(f.Copy() is null); f.Text->Type = NodeType.Text;
            f.Parent->NodeId = 15; Check(f.Copy() is null); f.Parent->NodeId = 16;
            f.Other->NodeId = 17; Check(f.Copy() is null); f.Other->NodeId = 99;
            f.Text->ParentNode = null; Check(f.Copy() is null); f.Text->ParentNode = f.Parent;
            f.List[2] = null; Check(f.Copy() is null);
        });
        Test("Topics rejects unowned cyclic hidden transparent or disabled ancestors", () =>
        {
            using var f = new Fixture();
            foreach (var address in new[] { (nint)f.Text, (nint)f.Parent, (nint)f.Root })
            {
                var node = (AtkResNode*)address;
                node->NodeFlags = 0; Check(f.Copy() is null); node->NodeFlags = NodeFlags.Visible;
                node->Color.A = 0; Check(f.Copy() is null); node->Color.A = 255;
                node->IsDrawDisabled = true; Check(f.Copy() is null); node->IsDrawDisabled = false;
            }
            f.Parent->ParentNode = f.Parent; Check(f.Copy() is null); f.Parent->ParentNode = f.Root;
            f.Parent->ParentNode = (AtkResNode*)1; Check(f.Copy() is null); f.Parent->ParentNode = f.Root;
            f.List[0] = null; Check(f.Copy() is null); f.List[0] = f.Root;
            f.Addon->RootNode = (AtkResNode*)f.Text;
            Check(FreeCompanyTopicsCreditsReader.Copy(f.Owner with { Root = (nint)f.Text }) is null);
        });
        Test("Topics node list bounds aliases and alignment reject before field access", () =>
        {
            using var f = new Fixture();
            f.Addon->UldManager.NodeListCount = 513;
            Check(FreeCompanyTopicsCreditsReader.Copy(f.Owner with { NodeCount = 513 }) is null);
            f.Addon->UldManager.NodeListCount = 4;
            f.List[3] = f.List[2]; Check(f.Copy() is null); f.List[3] = (AtkResNode*)1; Check(f.Copy() is null);
        });
        Test("Topics byte buffer requires bounded populated NUL terminated plain numeric content", () =>
        {
            using var f = new Fixture();
            var text = &f.Text->NodeText; var used = text->BufUsed;
            text->BufUsed = 1; Check(f.Copy() is null); text->BufUsed = 15; Check(f.Copy() is null); text->BufUsed = used;
            text->BufSize = used - 1; Check(f.Copy() is null); text->BufSize = used;
            var buffer = (byte*)text->StringPtr; text->StringPtr = null; Check(f.Copy() is null);
            text->StringPtr = (byte*)1; Check(f.Copy() is null); text->StringPtr = buffer;
            buffer[used - 1] = (byte)'1'; Check(f.Copy() is null); buffer[used - 1] = 0;
            buffer[1] = 0; Check(f.Copy() is null); f.SetText("1,234,567");
            f.Text->NodeText.StringLength = 99; Check(f.Copy()?.Credits == 1234567); // BufUsed, not metadata, bounds bytes.
            f.SetText("1.234.567"); Check(f.Copy() is null);
        });
        Test("Topics native copy rechecks loaded player local UI company proxy tab and addon identity", () =>
        {
            using var f = new Fixture();
            f.Player->IsLoaded = false; Check(f.Copy() is null); f.Player->IsLoaded = true;
            f.Player->ContentId++; Check(f.Copy() is null); f.Player->ContentId--;
            f.Info->LocalContentId++; Check(f.Copy() is null); f.Info->LocalContentId--;
            f.Agent->InfoProxyFreeCompany = null; Check(f.Copy() is null); f.Agent->InfoProxyFreeCompany = f.Profile;
            f.Agent->InfoProxyFreeCompanyMember = null; Check(f.Copy() is null); f.Agent->InfoProxyFreeCompanyMember = f.Members;
            f.Profile->Id++; Check(f.Copy() is null); f.Profile->Id--;
            f.Members->FreeCompanyId++; Check(f.Copy() is null); f.Members->FreeCompanyId--;
            f.Profile->ActiveListItemNum = 1; Check(f.Copy() is null); f.Profile->ActiveListItemNum = 0;
            f.Agent->AddonId++; Check(f.Copy() is null); f.Agent->AddonId--;
            f.Addon->Id++; Check(f.Copy() is null); f.Addon->Id--;
            f.Addon->NameString = "FreeCompanyProfile"; Check(f.Copy() is null); f.Addon->NameString = "FreeCompany";
            f.Addon->Flags1A1 = 0; Check(f.Copy() is null); f.Addon->Flags1A1 = 1;
            f.Addon->IsVisible = false; Check(f.Copy() is null); f.Addon->IsVisible = true;
            f.Addon->Alpha = 0; Check(f.Copy() is null); f.Addon->Alpha = 255;
            f.Addon->UldManager.NodeList = null; Check(f.Copy() is null); f.Addon->UldManager.NodeList = f.List;
            f.Addon->UldManager.NodeListCount = 3; Check(f.Copy() is null);
        });
        Test("Topics ownership changes after first copy prevent a second read", () =>
        {
            using var f = new Fixture(); var sample = f.Copy();
            foreach (var changed in new[] { f.Owner with { ContentId = 101 }, f.Owner with { CompanyId = 201 },
                f.Owner with { Player = 8 }, f.Owner with { Info = 8 }, f.Owner with { Agents = 24 }, f.Owner with { Addons = 24 },
                f.Owner with { Agent = 8 }, f.Owner with { Profile = 8 }, f.Owner with { Members = 8 },
                f.Owner with { Addon = 8 }, f.Owner with { AddonId = 11 }, f.Owner with { SelectedTab = 1 },
                f.Owner with { Root = 8 }, f.Owner with { NodeList = 8 }, f.Owner with { NodeCount = 3 } })
            {
                var current = f.Owner; var copies = 0;
                Check(FreeCompanyTopicsCreditsReader.CaptureStable(() => current, _ => { ++copies; current = changed; return sample; }) is null);
                Check(copies == 1);
            }
        });
        Test("Topics requires identical values buffers node membership and ancestor metadata across copies", () =>
        {
            using var f = new Fixture(); var first = f.Copy()!;
            foreach (var second in new[] { first with { Credits = 1 }, first with { Buffer = 8 }, first with { Used = first.Used - 1 },
                first with { Size = first.Size + 1 }, first with { Bytes = "31" }, first with { NodeList = [8] },
                first with { Ancestors = [] } })
            {
                var copies = 0;
                Check(FreeCompanyTopicsCreditsReader.CaptureStable(() => f.Owner, _ => ++copies == 1 ? first : second) is null);
            }
            Check(FreeCompanyTopicsCreditsReader.CaptureStable(() => f.Owner, _ => null) is null);
        });
        Console.WriteLine($"{passed} Topics credit groups passed; synthetic only, live Topics balance comparison remains required.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly List<nint> allocations = [];
        internal readonly PlayerState* Player;
        internal readonly InfoModule* Info;
        internal readonly AgentFreeCompany* Agent;
        internal readonly InfoProxyFreeCompany* Profile;
        internal readonly InfoProxyFreeCompanyMember* Members;
        internal readonly AtkUnitBase* Addon;
        internal readonly AtkResNode* Root;
        internal readonly AtkResNode* Parent;
        internal readonly AtkTextNode* Text;
        internal readonly AtkTextNode* Other;
        internal readonly AtkResNode** List;
        internal readonly FreeCompanyTopicsCreditsOwner Owner;

        internal Fixture()
        {
            Player = Allocate<PlayerState>(); Info = Allocate<InfoModule>(); Agent = Allocate<AgentFreeCompany>();
            Profile = Allocate<InfoProxyFreeCompany>(); Members = Allocate<InfoProxyFreeCompanyMember>();
            Addon = Allocate<AtkUnitBase>(); Root = Allocate<AtkResNode>(); Parent = Allocate<AtkResNode>();
            Text = Allocate<AtkTextNode>(); Other = Allocate<AtkTextNode>();
            List = (AtkResNode**)NativeMemory.AllocZeroed(4, (nuint)sizeof(AtkResNode*)); allocations.Add((nint)List);
            Player->IsLoaded = true; Player->ContentId = 100; Info->LocalContentId = 100; Agent->AddonId = 10;
            Agent->InfoProxyFreeCompany = Profile; Agent->InfoProxyFreeCompanyMember = Members;
            Profile->Id = Members->FreeCompanyId = 200; Profile->ActiveListItemNum = 0;
            Addon->NameString = "FreeCompany"; Addon->Id = 10; Addon->Flags1A1 = 1; Addon->IsVisible = true; Addon->Alpha = 255;
            Addon->RootNode = Root; Addon->UldManager.NodeList = List; Addon->UldManager.NodeListCount = 4;
            List[0] = Root; List[1] = Parent; List[2] = (AtkResNode*)Text; List[3] = (AtkResNode*)Other;
            for (var i = 0; i < 4; ++i) { List[i]->NodeFlags = NodeFlags.Visible; List[i]->Color.A = 255; }
            Root->NodeId = 1; Parent->NodeId = 16; Parent->ParentNode = Root;
            Text->NodeId = 17; Text->Type = NodeType.Text; Text->ParentNode = Parent;
            Other->NodeId = 99; Other->Type = NodeType.Text; Other->ParentNode = Root;
            Other->NodeText.StringPtr = (byte*)1; Other->NodeText.BufUsed = 8; Other->NodeText.BufSize = 8;
            SetText("1,234,567");
            Owner = new((nint)Player, 100, (nint)Info, 8, 16, (nint)Agent, (nint)Profile, (nint)Members, 200, 0,
                (nint)Addon, 10, (nint)Root, (nint)List, 4);
        }
        private T* Allocate<T>() where T : unmanaged
        { var value = (T*)NativeMemory.AllocZeroed((nuint)sizeof(T)); allocations.Add((nint)value); return value; }
        internal void SetText(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text); var buffer = (byte*)NativeMemory.AllocZeroed((nuint)bytes.Length + 1);
            allocations.Add((nint)buffer); bytes.CopyTo(new Span<byte>(buffer, bytes.Length));
            Text->NodeText.StringPtr = buffer; Text->NodeText.BufUsed = Text->NodeText.BufSize = bytes.Length + 1;
        }
        internal FreeCompanyTopicsCreditsSample? Copy() => FreeCompanyTopicsCreditsReader.Copy(Owner);
        public void Dispose() { foreach (var pointer in allocations) NativeMemory.Free((void*)pointer); }
    }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Topics credit assertion failed."); }
    private static void Test(string name, Action action) { action(); ++passed; Console.WriteLine("PASS " + name); }
}
