using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CompAI;
using FFXIVClientStructs.FFXIV.Component.GUI;

internal static unsafe class ChallengeDiagnosticChecks
{
    internal static void Run()
    {
        using (var f = new Fixture())
        {
            var component = f.Component(100);
            var title = f.Text(7, "Just Leve It to Me");
            var counter = f.Text(8, "3/5");
            title->ParentNode = (AtkResNode*)component; counter->ParentNode = (AtkResNode*)component;
            f.Nodes(&component->Component->UldManager, [(nint)title, (nint)counter]);
            var hidden = f.Text(9, "Hidden unrelated value"); hidden->NodeFlags = 0;
            f.Nodes(f.Root, [(nint)component, (nint)hidden]);
            Check(ChallengeLogDiagnostic.TryCopyVisibleText(f.Root, out var rows) && rows.Length == 2);
            Check(rows.All(row => row.ComponentPath.SequenceEqual([100u]) && row.ParentNodeId == 100));
            var result = ChallengeLogDiagnostic.Format(4, [new(11, 3, false)], rows);
            Check(result.Text is not null && Encoding.UTF8.GetByteCount(result.Text) <= ChallengeLogDiagnostic.MaxOutputBytes);
            using var document = JsonDocument.Parse(result.Text!);
            var json = document.RootElement;
            Check(json.EnumerateObject().Select(field=>field.Name).SequenceEqual(["diagnostic","addon","selected_tab","rows","text_nodes"]));
            Check(json.GetProperty("rows")[0].EnumerateObject().Select(field=>field.Name).SequenceEqual(["id","raw_status","completed"]));
            Check(json.GetProperty("selected_tab").GetByte() == 4 && json.GetProperty("text_nodes")[1].GetProperty("text").GetString() == "3/5");
            Check(!result.Text!.Contains("Hidden unrelated value"));
            Console.WriteLine("PASS Challenge diagnostic copies only visible owned text with node IDs and explicit output fields");
        }
        using (var f = new Fixture())
        {
            var list=f.Component(8);var card=f.Component(5);
            var titleParent=f.Component(2);var countParent=f.Component(7);
            titleParent->ParentNode=(AtkResNode*)card;countParent->ParentNode=(AtkResNode*)card;
            card->ParentNode=(AtkResNode*)list;
            var title=f.Text(3,"Just Leve It to Me");var counter=f.Text(11,"3/5");
            title->ParentNode=(AtkResNode*)titleParent;counter->ParentNode=(AtkResNode*)countParent;
            f.Nodes(&card->Component->UldManager,[(nint)title,(nint)counter]);
            f.Nodes(&list->Component->UldManager,[(nint)card]);f.Nodes(f.Root,[(nint)list]);
            Check(ChallengeLogDiagnostic.TryCopyVisibleText(f.Root,out var text));
            ChallengeDefinition[] definitions=[new(11,"Levequests — Just Leve It to Me",5,"Just Leve It to Me")];
            ChallengeRowObservation[] observed=[new(11,3,false)];
            Check(ChallengeLogReader.VerifyVisibleCounters(4,definitions,observed,text).Single().Progress==3);
            countParent->NodeFlags=0;
            Check(ChallengeLogDiagnostic.TryCopyVisibleText(f.Root,out text));
            Check(ChallengeLogReader.VerifyVisibleCounters(4,definitions,observed,text).Length==0);
            Console.WriteLine("PASS actual leve node/component layout corroborates visible3/5 and rejects hidden ratio");
        }
        using (var f = new Fixture())
        {
            var oversized=f.Text(1,new string('x',1025)); f.Nodes(f.Root,[(nint)oversized]);
            Check(!ChallengeLogDiagnostic.TryCopyVisibleText(f.Root,out var rows) && rows.Length==0);
            var cyclic=f.Text(2,"Cycle"); cyclic->ParentNode=(AtkResNode*)cyclic; f.Nodes(f.Root,[(nint)cyclic]);
            Check(ChallengeLogDiagnostic.TryCopyVisibleText(f.Root,out rows) && rows.Length==0);
            f.Root->NodeListCount=513;
            Check(!ChallengeLogDiagnostic.TryCopyVisibleText(f.Root,out rows) && rows.Length==0);
            var tooMany=Enumerable.Range(0,201).Select(i=>new ChallengeDiagnosticText((uint)i,null,[],"x")).ToArray();
            Check(ChallengeLogDiagnostic.Format(4,[new(11,3,false)],tooMany).Text is null);
            var largeJson=Enumerable.Range(0,200).Select(i=>new ChallengeDiagnosticText((uint)i,null,[],new string('x',200))).ToArray();
            Check(ChallengeLogDiagnostic.Format(4,[new(11,3,false)],largeJson).Text is null);
            Console.WriteLine("PASS Challenge diagnostic rejects oversized strings, node/output bounds and cyclic parent visibility");
        }
    }

    private static void Check(bool value) { if(!value) throw new InvalidOperationException("Challenge diagnostic assertion failed."); }

    internal sealed class Fixture : IDisposable
    {
        private readonly List<nint> allocations=[];
        internal AtkUldManager* Root { get; }
        internal Fixture() => Root=Allocate<AtkUldManager>();
        private T* Allocate<T>() where T:unmanaged
        {
            var value=(T*)NativeMemory.AllocZeroed((nuint)sizeof(T)); allocations.Add((nint)value); return value;
        }
        internal AtkComponentNode* Component(uint id)
        {
            var node=Allocate<AtkComponentNode>(); node->NodeId=id; node->Type=(NodeType)1005;
            node->NodeFlags=NodeFlags.Visible; node->Color.A=255; node->Component=Allocate<AtkComponentBase>(); return node;
        }
        internal AtkTextNode* Text(uint id,string value)
        {
            var node=Allocate<AtkTextNode>(); node->NodeId=id; node->Type=NodeType.Text;
            node->NodeFlags=NodeFlags.Visible; node->Color.A=255;
            var encoded=Encoding.UTF8.GetBytes(value); var buffer=(byte*)NativeMemory.AllocZeroed((nuint)(encoded.Length+1));
            allocations.Add((nint)buffer); encoded.CopyTo(new Span<byte>(buffer,encoded.Length));
            node->NodeText.StringPtr=buffer; node->NodeText.StringLength=encoded.Length;
            node->NodeText.BufUsed=encoded.Length+1; node->NodeText.BufSize=encoded.Length+1; return node;
        }
        internal void Nodes(AtkUldManager* manager,nint[] nodes)
        {
            var list=(AtkResNode**)NativeMemory.AllocZeroed((nuint)(IntPtr.Size*nodes.Length)); allocations.Add((nint)list);
            for(var i=0;i<nodes.Length;++i) list[i]=(AtkResNode*)nodes[i];
            manager->NodeList=list; manager->NodeListCount=(ushort)nodes.Length;
        }
        public void Dispose() { foreach(var value in allocations) NativeMemory.Free((void*)value); }
    }
}
