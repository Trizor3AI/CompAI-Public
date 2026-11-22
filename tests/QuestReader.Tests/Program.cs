using System.Collections.Immutable;
using System.Runtime.Loader;
using CompAI;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Application.Network.WorkDefinitions;

var libraryPath = Environment.GetEnvironmentVariable("DALAMUD_LIB_PATH") ??
                  Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                      "XIVLauncher", "addon", "Hooks", "dev");
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var path = Path.Combine(libraryPath, name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};

Tests.Run();

internal static class Tests
{
    private static int passed;
    private static readonly DateTimeOffset CapturedAt = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    internal static void Run()
    {
        Test("supported full row namespace only", () =>
        {
            Check(QuestCatalog.TryMap(0x10001, out var id) && id == 1, "first valid row");
            Check(QuestCatalog.TryMap(0x1FFFF, out id) && id == ushort.MaxValue, "last mappable row");
            foreach (var invalid in new uint[] { 0, 1, 0x10000, 0x20001, 0xFFFF0001, uint.MaxValue })
                Check(!QuestCatalog.TryMap(invalid, out id) && id == 0, $"reject {invalid:X}");
        });
        Test("duplicate and cross-namespace aliases never reach completion", () =>
        {
            var catalog = QuestCatalog.Create([Definition(1), Definition(1), Definition(2), new(0x20002, "Alias", false), Definition(3)]);
            var called = new List<ushort>();
            var snapshot = Build(catalog, [], id => { called.Add(id); return true; });
            Check(called.SequenceEqual(new ushort[] { 3 }), "only unambiguous ID checked");
            Check(snapshot.Quests.Length == 1 && snapshot.CatalogNote!.Contains("2 ambiguous"), "ambiguity visible");
        });
        Test("capacity guard excludes zero and index 6008", () =>
        {
            Check(!QuestCatalog.CanCheckCompletion(0), "zero excluded");
            Check(QuestCatalog.CanCheckCompletion(1) && QuestCatalog.CanCheckCompletion(6007), "inclusive safe range");
            Check(!QuestCatalog.CanCheckCompletion(6008) && !QuestCatalog.CanCheckCompletion(ushort.MaxValue), "unsafe indexes excluded");
            var calls = new List<ushort>();
            var snapshot = Build(QuestCatalog.Create([Definition(6007), Definition(6008)]), [], id => { calls.Add(id); return false; });
            Check(calls.SequenceEqual(new ushort[] { 6007 }), "guard applied before native delegate");
            Check(snapshot.Quests[1].Completed is null && snapshot.Quests[1].UnavailableReason is not null, "outside guard is unknown");
        });
        Test("active repeatable completion independent; raw sequence zero preserved", () =>
        {
            var snapshot = Build(QuestCatalog.Create([new(0x10001, " Repeatable ", true), Definition(2)]), [Active(1, 0)], id => id == 1);
            var repeatable = snapshot.Quests[0];
            Check(repeatable.Accepted && repeatable.Completed == true && repeatable.IsRepeatable == true, "both states retained");
            Check(repeatable.Sequence == 0 && repeatable.Flags == 9 && repeatable.AcceptClassJob == 3, "raw fields retained");
            Check(repeatable.Variables.SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6 }), "raw diagnostics retained");
            Check(repeatable.Name == "Repeatable", "whitespace normalized");
            var other = snapshot.Quests[1];
            Check(!other.Accepted && other.Completed == false && other.Sequence is null, "inactive sequence absent");
            Check(snapshot.ReadinessNote.Contains("unverified") && snapshot.ReadinessNote.Contains("not observed completed"), "honest negatives");
        });
        Test("unknown active identity retained with no completion call", () =>
        {
            var calls = 0;
            var snapshot = Build(QuestCatalog.Unavailable("Fixture unavailable"), [Active(ushort.MaxValue, 255)], _ => { calls++; return true; });
            var quest = snapshot.Quests.Single();
            Check(calls == 0 && snapshot.CompletionChecks == 0, "no arbitrary completion call");
            Check(quest.Accepted && quest.NativeId == ushort.MaxValue && quest.RowId is null && quest.Name is null, "numeric active identity");
            Check(quest.Completed is null && quest.Sequence == 255 && quest.IsRepeatable is null, "unknown metadata preserved");
            Check(snapshot.CatalogNote == "Fixture unavailable", "catalog failure visible");
        });
        Test("empty slots ignored; duplicate and excess active records rejected", () =>
        {
            var catalog = QuestCatalog.Create([Definition(1)]);
            Check(Build(catalog, [Active(0), Active(1)], _ => false).Quests.Single().Accepted, "nonempty slot retained");
            var called = false;
            Throws<InvalidOperationException>(() => Build(catalog, [Active(1), Active(1)], _ => { called = true; return true; }));
            Check(!called, "duplicate check precedes completion calls");
            Throws<InvalidOperationException>(() => Build(catalog, Enumerable.Range(1, 31).Select(i => Active((ushort)i)), _ => false));
        });
        Test("missing names retain full and native identity", () =>
        {
            var snapshot = Build(QuestCatalog.Create([new(0x10001, " ", false)]), [], _ => true);
            var quest = snapshot.Quests.Single();
            Check(quest.Name is null && quest.RowId == 0x10001 && quest.NativeId == 1 && quest.Completed == true, "numeric fallback");
            Check(snapshot.CapturedAtUtc == CapturedAt && snapshot.SourceBuild == "Fixture", "metadata retained");
        });
        Test("completion failure cannot publish a partial snapshot", () =>
        {
            var attempts = 0;
            QuestSnapshot? published = null;
            Throws<InvalidOperationException>(() => published = Build(QuestCatalog.Create([Definition(1), Definition(2)]), [], _ =>
            {
                if (++attempts == 2) throw new InvalidOperationException("Fixture failure");
                return true;
            }));
            Check(published is null, "no partial publication");
        });
        Test("logged-out capture rejects before native access", () =>
            Throws<InvalidOperationException>(() => new QuestReader(null!).Capture(false)));
        Test("active progress reuses verified completion with bounded native work", () =>
        {
            var catalog = QuestCatalog.Create(Enumerable.Range(1, 4000).Select(id => Definition((ushort)id)));
            var calls = 0;
            bool Completion(ushort id) { calls++; return id % 3 == 0; }
            var snapshot = Build(catalog, [Active(1)], Completion);
            var prior = snapshot;
            for (byte sequence = 1; sequence <= 100; sequence++)
            {
                snapshot = QuestSnapshotBuilder.Build(catalog, [Active(1, sequence)], Completion,
                    CapturedAt, "Fixture", snapshot);
                Check(snapshot.CompletionChecks == 0 && snapshot.Quests[0].Sequence == sequence,
                    "progress refreshed without completion calls");
                Check(snapshot.Quests.Select(q => q.Completed).SequenceEqual(prior.Quests.Select(q => q.Completed)),
                    "positive and negative completion observations preserved");
            }
            Check(calls == 4000, "only the initial full completion scan ran");
            Console.WriteLine($"  Work: 101 captures / 4,000 definitions: native completion calls 404,000 -> {calls:N0}.");
            var forced = Build(catalog, [Active(1, 101)], Completion);
            Check(forced.CompletionChecks == 4000 && calls == 8000, "no baseline forces explicit/reload checks");

            var incomplete = Build(QuestCatalog.Unavailable("unavailable"), [Active(1)], Completion);
            var recovered = QuestSnapshotBuilder.Build(catalog, [Active(1)], Completion,
                CapturedAt, "Fixture", incomplete);
            Check(recovered.CompletionChecks == 4000 && calls == 12000,
                "unavailable baseline cannot suppress newly available catalog checks");
            var unknown = QuestSnapshotBuilder.Build(QuestCatalog.Create([Definition(6008)]),
                [Active(ushort.MaxValue)], _ => throw new Exception("unsafe completion call"),
                CapturedAt, "Fixture", prior);
            Check(unknown.Quests.All(q => q.Completed is null), "unknown and guarded-out completion stays unknown");

            var reordered = prior with { Quests = prior.Quests.Reverse().ToImmutableArray() };
            var reorderedResult = QuestSnapshotBuilder.Build(catalog, [Active(1)], Completion,
                CapturedAt, "Fixture", reordered);
            Check(reorderedResult.CompletionChecks == 4000 &&
                reorderedResult.Quests.Select(q => q.Completed).SequenceEqual(prior.Quests.Select(q => q.Completed)),
                "baseline reordering forces exact native fallback");
            var expanded = QuestSnapshotBuilder.Build(
                QuestCatalog.Create(Enumerable.Range(1, 4001).Select(id => Definition((ushort)id))),
                [], Completion, CapturedAt, "Fixture", prior);
            Check(expanded.CompletionChecks == 1, "new catalog row gets its own native check");

            var progress = new[] { Active(1, 99) };
            // Warm both paths, then compare allocations in the same builder. This
            // uses synthetic completion calls and makes no timing claim about the game.
            Build(catalog, progress, Completion);
            QuestSnapshotBuilder.Build(catalog, progress, Completion, CapturedAt, "Fixture", prior);
            long Allocation(QuestSnapshot? baseline)
            {
                var started = GC.GetAllocatedBytesForCurrentThread();
                for (var iteration = 0; iteration < 20; iteration++)
                    QuestSnapshotBuilder.Build(catalog, progress, Completion, CapturedAt, "Fixture", baseline);
                return GC.GetAllocatedBytesForCurrentThread() - started;
            }
            var originalBytes = Allocation(null);
            var reusedBytes = Allocation(prior);
            Check(reusedBytes <= originalBytes, "completion reuse added per-capture allocation");
            Console.WriteLine($"  Allocation: 20 progress builds: {originalBytes:N0} -> {reusedBytes:N0} bytes.");
        });
        Test("logged-out polling rejects before native access", () =>
            Throws<InvalidOperationException>(() => new QuestReader(null!).ReadState(false)));
        Test("state equality includes identity and owns copied input bytes", () =>
        {
            var completed = new byte[QuestCatalog.ExpectedCompletionBytes];
            var active = new byte[QuestStateStamp.ExpectedActiveBytes];
            var first = new QuestStateStamp(42, completed, active);
            var equal = new QuestStateStamp(42, completed, active);
            Check(first.Equals(equal) && first == equal && first.GetHashCode() == equal.GetHashCode(), "same values equal");
            Check(!first.Equals(null) && first != null && !first.Equals(new object()), "other types rejected");
            Check(first != new QuestStateStamp(43, completed, active), "character identity included");
            completed[0] = 1;
            active[^1] = 1;
            Check(first == equal, "caller mutation cannot change existing stamps");
            Check(first != new QuestStateStamp(42, completed, active), "new input differs");
            Throws<InvalidOperationException>(() => new QuestStateStamp(0, completed, active));
            Throws<InvalidOperationException>(() => new QuestStateStamp(42, completed.AsSpan(1), active));
            Throws<InvalidOperationException>(() => new QuestStateStamp(42, completed, active.AsSpan(1)));
        });
        Test("polling copy detects every completion byte and remains immutable", () =>
        {
            QuestManager manager = default;
            var original = State(ref manager);
            for (var i = 0; i < manager.CompletedQuests.Length; i++)
            {
                manager.CompletedQuests[i] = 1;
                Check(original != State(ref manager), $"completion byte {i}");
                manager.CompletedQuests[i] = 0;
            }
            Check(original == State(ref manager), "baseline restored with original stamp unchanged");
        });
        Test("polling copy detects every active field in every slot", () =>
        {
            QuestManager manager = default;
            var original = State(ref manager);
            for (var slot = 0; slot < manager.NormalQuests.Length; slot++)
            {
                ref var work = ref manager.NormalQuests[slot];
                work.QuestId = 1;
                Check(original != State(ref manager), $"slot {slot} acceptance");
                var accepted = State(ref manager);
                work.QuestId = 257;
                Check(accepted != State(ref manager), $"slot {slot} high ID byte");
                work.QuestId = 1;
                work.Sequence = 1;
                Check(accepted != State(ref manager), $"slot {slot} sequence");
                work.Sequence = 0;
                work.Flags = 1;
                Check(accepted != State(ref manager), $"slot {slot} flags");
                work.Flags = 0;
                work.AcceptClassJob = 1;
                Check(accepted != State(ref manager), $"slot {slot} class job");
                work.AcceptClassJob = 0;
                for (var variable = 0; variable < work.Variables.Length; variable++)
                {
                    work.Variables[variable] = 1;
                    Check(accepted != State(ref manager), $"slot {slot} variable {variable}");
                    work.Variables[variable] = 0;
                }
                Check(accepted == State(ref manager), $"slot {slot} progress baseline restored");
                work.QuestId = 0;
                Check(accepted != State(ref manager) && original == State(ref manager), $"slot {slot} removal");
            }
            Throws<InvalidOperationException>(() => QuestReader.CopyState(42, new byte[750], new QuestWork[30]));
            Throws<InvalidOperationException>(() => QuestReader.CopyState(42, new byte[751], new QuestWork[29]));
        });
        Test("installed layout matches constants using zeroed test-owned structs", () =>
        {
            // Test-owned values, not game pointers or live state. No native methods run.
            QuestManager manager = default;
            QuestWork work = default;
            Check(manager.NormalQuests.Length == QuestCatalog.ExpectedActiveSlots, "active capacity");
            Check(manager.CompletedQuests.Length == QuestCatalog.ExpectedCompletionBytes, "completion capacity");
            Check(work.Variables.Length == 6, "variables capacity");
            Check(typeof(PlayerState).GetField(nameof(PlayerState.ContentId))?.FieldType == typeof(ulong), "character identity metadata");
            Console.WriteLine($"  Metadata: FFXIVClientStructs {typeof(QuestManager).Assembly.GetName().Version}");
        });
        Console.WriteLine($"PASS {passed} quest reader fixture groups; no game state or native functions used.");
    }

    private static QuestDefinition Definition(ushort id) => new(0x10000u + id, $"Quest {id}", false);
    private static QuestStateStamp State(ref QuestManager manager) =>
        QuestReader.CopyState(42, manager.CompletedQuests, manager.NormalQuests);
    private static ActiveQuestData Active(ushort id, byte sequence = 3) => new(id, sequence, 9, 3, ImmutableArray.Create<byte>(1, 2, 3, 4, 5, 6));
    private static QuestSnapshot Build(QuestCatalog catalog, IEnumerable<ActiveQuestData> active, Func<ushort, bool> completion) =>
        QuestSnapshotBuilder.Build(catalog, active, completion, CapturedAt, "Fixture");
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }
    private static void Test(string name, Action action)
    {
        action(); passed++; Console.WriteLine("PASS " + name);
    }
}
