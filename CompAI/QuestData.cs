using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace CompAI;

internal sealed record QuestSnapshot(DateTimeOffset CapturedAtUtc, double CaptureDurationMs,
    string SourceBuild, string ReadinessNote, string CoverageNote, string? CatalogNote,
    int CatalogRows, int CompletionChecks, int ActiveSlotCount, ImmutableArray<QuestObservation> Quests);

// Accepted and Completed are independent: a repeatable quest can have both flags.
// Completed=false means only "not observed completed", never "not started".
internal sealed record QuestObservation(uint? RowId, ushort NativeId, string? Name,
    bool? IsRepeatable, bool Accepted, bool? Completed, byte? Sequence, byte? Flags,
    byte? AcceptClassJob, ImmutableArray<byte> Variables, string? UnavailableReason)
{
    internal string? JournalText { get; init; }
}

internal sealed record QuestDefinition(uint RowId, string? Name, bool IsRepeatable);
internal sealed record ActiveQuestData(ushort NativeId, byte Sequence, byte Flags,
    byte AcceptClassJob, ImmutableArray<byte> Variables);

/// <summary>Managed catalog only. No arbitrary row ID is passed through a masking native overload.</summary>
internal sealed class QuestCatalog
{
    internal const uint RowNamespace = 0x00010000;
    internal const int ExpectedCompletionBytes = 751;
    internal const int ExpectedActiveSlots = 30;

    private QuestCatalog(ImmutableSortedDictionary<ushort, QuestDefinition> definitions, int rows, string? note)
    {
        this.Definitions = definitions;
        this.RowCount = rows;
        this.Note = note;
    }

    internal ImmutableSortedDictionary<ushort, QuestDefinition> Definitions { get; }
    internal int RowCount { get; }
    internal string? Note { get; }

    internal static bool TryMap(uint rowId, out ushort nativeId)
    {
        nativeId = 0;
        if ((rowId & 0xFFFF0000) != RowNamespace || (rowId & 0xFFFF) == 0)
            return false;
        nativeId = checked((ushort)(rowId - RowNamespace));
        return true;
    }

    // Conservative storage-capacity guard, NOT a claim that all these IDs exist or
    // have complete historical coverage. Excluding 6008 is safe for either zero-
    // or one-based indexing; use the API, never decode the completion bitset.
    internal static bool CanCheckCompletion(ushort nativeId) =>
        nativeId > 0 && nativeId < ExpectedCompletionBytes * 8;

    internal static QuestCatalog Unavailable(string note) => new(
        ImmutableSortedDictionary<ushort, QuestDefinition>.Empty, 0, note);

    internal static QuestCatalog Create(IEnumerable<QuestDefinition> rows)
    {
        var definitions = ImmutableSortedDictionary.CreateBuilder<ushort, QuestDefinition>();
        var seen = new HashSet<ushort>();
        var collisions = new HashSet<ushort>();
        var rowCount = 0;
        var invalidCount = 0;
        foreach (var row in rows)
        {
            if (++rowCount > 65536)
                throw new InvalidOperationException("Quest catalog exceeded its bounded row count.");
            // Detect aliasing even when a second row belongs to an unsupported namespace.
            var lowId = (ushort)(row.RowId & 0xFFFF);
            if (lowId != 0 && !seen.Add(lowId))
                collisions.Add(lowId);
            if (!TryMap(row.RowId, out var id))
            {
                invalidCount++;
                continue;
            }
            definitions[id] = row with { Name = string.IsNullOrWhiteSpace(row.Name) ? null : row.Name.Trim() };
        }
        foreach (var collision in collisions)
            definitions.Remove(collision);
        var outsideGuard = definitions.Keys.Count(id => !CanCheckCompletion(id));
        var note = invalidCount == 0 && collisions.Count == 0 && outsideGuard == 0 ? null :
            $"Catalog limits: {invalidCount} unsupported/empty row IDs; {collisions.Count} ambiguous native IDs excluded; {outsideGuard} mapped IDs outside the completion guard.";
        return new QuestCatalog(definitions.ToImmutable(), rowCount, note);
    }
}

internal static class QuestSnapshotBuilder
{
    internal const string Readiness = "Logged in and PlayerState.IsLoaded observed. Quest-state readiness is unverified; negative completion results mean only not observed completed.";
    internal const string Coverage = "Ordinary character quests from the local Quest catalog and 30 NormalQuests slots. Completion calls use unique 0x0001xxxx rows and native IDs 1-6007 only. Plain journal paragraphs may be available for an exact sequence; dynamic text and live objective counters are unavailable. Coverage is unverified: no complete-history claim, completion dates, account-wide history, daily/leve/legacy-specific state or dialogue. Sequence is raw; variables are diagnostics, not universal objective counters.";

    internal static QuestSnapshot Build(QuestCatalog catalog, IEnumerable<ActiveQuestData> activeRecords,
        Func<ushort, bool> isComplete, DateTimeOffset capturedAtUtc, string sourceBuild,
        QuestSnapshot? completionBaseline = null)
    {
        var active = new Dictionary<ushort, ActiveQuestData>();
        var slots = 0;
        foreach (var record in activeRecords)
        {
            if (++slots > QuestCatalog.ExpectedActiveSlots)
                throw new InvalidOperationException("Active quest records exceeded the known slot capacity.");
            if (record.NativeId == 0)
                continue;
            if (!active.TryAdd(record.NativeId, record))
                throw new InvalidOperationException("Duplicate active quest IDs observed; capture unavailable.");
        }

        var observations = ImmutableArray.CreateBuilder<QuestObservation>();
        // The caller supplies a baseline only after matching its committed native
        // completion stamp. Match full catalog identity as well, and never turn an
        // unavailable result into a negative completion flag.
        // Catalog observations have deterministic order. Exact indexed matching
        // avoids constructing another catalog-sized dictionary for every progress
        // update; changed ordering or added rows simply fall back to native checks.
        var baselineIndex = 0;
        var checks = 0;
        foreach (var (id, definition) in catalog.Definitions)
        {
            var previous = completionBaseline is not null && baselineIndex < completionBaseline.Quests.Length
                ? completionBaseline.Quests[baselineIndex] : null;
            baselineIndex++;
            bool? completed = null;
            if (QuestCatalog.CanCheckCompletion(id))
            {
                if (previous?.NativeId == id && previous.RowId == definition.RowId && previous.Completed is { } known)
                    completed = known;
                else
                {
                    completed = isComplete(id);
                    checks++;
                }
            }
            active.Remove(id, out var work);
            observations.Add(Observe(id, definition, work, completed, completed.HasValue ? null :
                "Completion unavailable: ID is outside the conservative native completion guard."));
        }
        foreach (var (id, work) in active.OrderBy(pair => pair.Key))
            observations.Add(Observe(id, null, work, null,
                "Completion/name mapping unavailable: no unique supported Quest catalog row."));

        return new QuestSnapshot(capturedAtUtc, 0, sourceBuild, Readiness, Coverage, catalog.Note,
            catalog.RowCount, checks, QuestCatalog.ExpectedActiveSlots, observations.ToImmutable());
    }

    private static QuestObservation Observe(ushort id, QuestDefinition? definition, ActiveQuestData? work,
        bool? completed, string? unavailable) => new(definition?.RowId, id, definition?.Name,
        definition?.IsRepeatable, work is not null, completed, work?.Sequence, work?.Flags,
        work?.AcceptClassJob, work?.Variables ?? ImmutableArray<byte>.Empty, unavailable);
}
