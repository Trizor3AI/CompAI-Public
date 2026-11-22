using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using CompAI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Text;

var libraryPath = Environment.GetEnvironmentVariable("DALAMUD_LIB_PATH") ??
                  Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                      "XIVLauncher", "addon", "Hooks", "dev");
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var path = Path.Combine(libraryPath, name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};

Tests.Run();

internal static unsafe class Tests
{
    private static readonly string[] Labels = ["Maximum Speed", "Acceleration", "Endurance", "Stamina", "Cunning"];

    internal static void Run()
    {
        using (var fixture = MakeCard())
        {
            Check(PedigreeTooltipReader.TryCopyVisibleText(fixture.Root, out var texts), "nested copy");
            Check(texts.Length == 10, "raw component IDs 1005 expose ten nested text nodes");
            Check(texts.All(text => string.Concat(text.Runs.Select(run => run.Text)) == text.Text),
                "resolved ColorType text matches Dalamud visible text");
            var sample = new HoveredChocoboTooltip("Inventory1", 0, 9602, 9602, "Fixture card", 0,
                DateTimeOffset.UtcNow, texts, Labels);
            Check(PedigreeDecoder.TryDecode(sample, out var reading, out var reason), "nested rows decode: " + reason);
            Check(reading!.FatherStars.SequenceEqual(new[] { 1, 2, 3, 4, 1 }) &&
                  reading.MotherStars.SequenceEqual(new[] { 4, 3, 2, 1, 4 }), "exact parent star values");
            Console.WriteLine("PASS raw component traversal + real ColorType encoding + decoder integration");
        }

        using (var fixture = MakeCard())
        {
            fixture.Root->NodeList[0]->NodeFlags = 0;
            Check(PedigreeTooltipReader.TryCopyVisibleText(fixture.Root, out var texts) && texts.Length == 8,
                "hidden component children excluded");
            Console.WriteLine("PASS hidden component visibility");
        }

        using (var fixture = MakeCard())
        {
            fixture.Root->NodeList[0]->ParentNode = fixture.Root->NodeList[0];
            Check(PedigreeTooltipReader.TryCopyVisibleText(fixture.Root, out var texts) && texts.Length == 8,
                "cyclic ancestors excluded without unbounded traversal");
            Console.WriteLine("PASS ancestor-cycle bound");
        }

        using (var fixture = new Fixture())
        {
            var text = fixture.Text(1, 0, 0, Encoding.UTF8.GetBytes(new string('x', 2049)));
            fixture.SetNodes(fixture.Root, [(nint)text]);
            Check(!PedigreeTooltipReader.TryCopyVisibleText(fixture.Root, out var texts) && texts.Length == 0,
                "oversized string fails without exposing partial results");
            Console.WriteLine("PASS string-size bound");
        }

        using (var fixture = new Fixture())
        {
            fixture.Root->NodeListCount = 257;
            Check(!PedigreeTooltipReader.TryCopyVisibleText(fixture.Root, out var texts) && texts.Length == 0,
                "node count rejected before dereferencing a list");
            Console.WriteLine("PASS node-count bound");
        }

        using (var fixture = new Fixture())
        {
            var manager = fixture.Root;
            for (var i = 0; i < 10; i++)
            {
                var node = fixture.Component((uint)i + 1);
                fixture.SetNodes(manager, [(nint)node]);
                manager = &node->Component->UldManager;
            }
            Check(!PedigreeTooltipReader.TryCopyVisibleText(fixture.Root, out var texts) && texts.Length == 0,
                "deep components rejected without partial results");
            Console.WriteLine("PASS component-depth bound");
        }
    }

    private static Fixture MakeCard()
    {
        var fixture = new Fixture();
        var components = new nint[5];
        for (var i = 0; i < 5; i++)
        {
            var component = fixture.Component((uint)(100 + i));
            components[i] = (nint)component;
            var label = fixture.Text(2, 22, 50 + i * 24, Encoding.UTF8.GetBytes(Labels[i]));
            var parentage = fixture.Text(3, 192, 50 + i * 24, MakeStars(i % 4 + 1, 4 - i % 4));
            label->ParentNode = (AtkResNode*)component;
            parentage->ParentNode = (AtkResNode*)component;
            fixture.SetNodes(&component->Component->UldManager, [(nint)label, (nint)parentage]);
        }
        fixture.SetNodes(fixture.Root, components);
        return fixture;
    }

    private static byte[] MakeStars(int father, int mother)
    {
        var builder = new SeStringBuilder();
        builder.PushColorType(529).Append("♂").PopColorType();
        builder.PushColorType(530).Append(new string('★', 4 - father)).PopColorType();
        builder.PushColorType(529).Append(new string('★', father)).PopColorType().Append("　");
        builder.PushColorType(531).Append("♀").PopColorType();
        builder.PushColorType(532).Append(new string('★', 4 - mother)).PopColorType();
        builder.PushColorType(531).Append(new string('★', mother)).PopColorType();
        return builder.ToArray();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly List<nint> allocations = [];
        internal AtkUldManager* Root { get; }
        internal Fixture() => this.Root = this.Alloc<AtkUldManager>();

        private T* Alloc<T>() where T : unmanaged
        {
            var block = (T*)NativeMemory.AllocZeroed((nuint)sizeof(T));
            this.allocations.Add((nint)block);
            return block;
        }

        internal AtkComponentNode* Component(uint nodeId)
        {
            var node = this.Alloc<AtkComponentNode>();
            node->NodeId = nodeId;
            node->Type = (NodeType)1005;
            node->NodeFlags = NodeFlags.Visible;
            node->Color.A = 255;
            node->Component = this.Alloc<AtkComponentBase>();
            return node;
        }

        internal AtkTextNode* Text(uint nodeId, float x, float y, byte[] encoded)
        {
            var node = this.Alloc<AtkTextNode>();
            node->NodeId = nodeId;
            node->Type = NodeType.Text;
            node->NodeFlags = NodeFlags.Visible;
            node->Color.A = 255;
            node->ScreenX = x;
            node->ScreenY = y;
            var buffer = (byte*)NativeMemory.AllocZeroed((nuint)(encoded.Length + 1));
            this.allocations.Add((nint)buffer);
            encoded.CopyTo(new Span<byte>(buffer, encoded.Length));
            node->NodeText.StringPtr = buffer;
            node->NodeText.StringLength = encoded.Length;
            node->NodeText.BufUsed = encoded.Length + 1;
            node->NodeText.BufSize = encoded.Length + 1;
            return node;
        }

        internal void SetNodes(AtkUldManager* manager, nint[] nodes)
        {
            var list = (AtkResNode**)NativeMemory.AllocZeroed((nuint)(IntPtr.Size * nodes.Length));
            this.allocations.Add((nint)list);
            for (var i = 0; i < nodes.Length; i++) list[i] = (AtkResNode*)nodes[i];
            manager->NodeList = list;
            manager->NodeListCount = (ushort)nodes.Length;
        }

        public void Dispose()
        {
            foreach (var allocation in this.allocations) NativeMemory.Free((void*)allocation);
        }
    }
}
