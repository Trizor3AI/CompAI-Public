using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Application.Network.WorkDefinitions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace CompAI;

/// <summary>
/// Local reader. ReadState and Capture must run on the framework thread. The caller must
/// discard results after logout/session changes, Clear or unload using its capture epoch.
/// Native pointers and spans never leave the reader; snapshots contain managed values.
/// </summary>
internal sealed unsafe class QuestReader(IDataManager data)
{
    private QuestCatalog? catalog;
    private ExcelSheet<Quest>? questSheet;
    private readonly QuestJournalText journalText = new(data);

    internal QuestStateStamp ReadState(bool loggedIn)
    {
        if (!loggedIn)
            throw new InvalidOperationException("Not logged in; quest state unavailable.");
        var player = PlayerState.Instance();
        var manager = QuestManager.Instance();
        if (player == null || !player->IsLoaded || manager == null)
            throw new InvalidOperationException("Player/quest manager unavailable or PlayerState is not loaded.");
        var contentId = player->ContentId;
        if (contentId == 0)
            throw new InvalidOperationException("Character identity is unavailable for quest state.");

        // Compare storage as opaque bytes: this does not interpret completion bit indexes,
        // fetch sheets, or call the native per-quest completion/acceptance lookups.
        var state = CopyState(contentId, manager->CompletedQuests, manager->NormalQuests);
        if (PlayerState.Instance() != player || QuestManager.Instance() != manager ||
            !player->IsLoaded || player->ContentId != contentId)
            throw new InvalidOperationException("Player/quest state changed during polling; retry after loading.");
        return state;
    }

    // Kept pointer-free so the exact polling copy can be verified with test-owned structs.
    internal static QuestStateStamp CopyState(ulong contentId, ReadOnlySpan<byte> completedQuests,
        ReadOnlySpan<QuestWork> normalQuests)
    {
        if (completedQuests.Length != QuestCatalog.ExpectedCompletionBytes ||
            normalQuests.Length != QuestCatalog.ExpectedActiveSlots)
            throw new InvalidOperationException("Quest layout differs from the inspected build; state unavailable.");

        Span<byte> active = stackalloc byte[QuestStateStamp.ExpectedActiveBytes];
        var offset = 0;
        foreach (ref readonly var work in normalQuests)
        {
            if (work.Variables.Length != QuestStateStamp.ExpectedVariableCount)
                throw new InvalidOperationException("Quest variable layout differs from the inspected build; state unavailable.");
            BinaryPrimitives.WriteUInt16LittleEndian(active.Slice(offset, 2), work.QuestId);
            active[offset + 2] = work.Sequence;
            active[offset + 3] = work.Flags;
            active[offset + 4] = work.AcceptClassJob;
            work.Variables.CopyTo(active.Slice(offset + 5, QuestStateStamp.ExpectedVariableCount));
            offset += QuestStateStamp.ActiveSlotBytes;
        }
        return new QuestStateStamp(contentId, completedQuests, active);
    }

    internal QuestSnapshot Capture(bool loggedIn, QuestSnapshot? completionBaseline = null)
    {
        if (!loggedIn)
            throw new InvalidOperationException("Not logged in; quest state unavailable.");
        using var timing = PerformanceDiagnostics.Measure("reader.quests");
        var started = DateTimeOffset.UtcNow;
        var timer = Stopwatch.StartNew();
        var player = PlayerState.Instance();
        var manager = QuestManager.Instance();
        if (player == null || !player->IsLoaded || manager == null)
            throw new InvalidOperationException("Player/quest manager unavailable or PlayerState is not loaded.");
        if (manager->NormalQuests.Length != QuestCatalog.ExpectedActiveSlots ||
            manager->CompletedQuests.Length != QuestCatalog.ExpectedCompletionBytes)
            throw new InvalidOperationException("Quest layout differs from the inspected build; capture unavailable.");

        var definitions = this.GetCatalog();
        var active = new List<ActiveQuestData>();
        foreach (ref var work in manager->NormalQuests)
        {
            if (work.QuestId == 0)
                continue;
            // Acceptance has separate semantics from completion. For these copied,
            // nonempty normal records, cross-check the native acceptance lookup.
            if (!manager->IsQuestAccepted(work.QuestId))
                throw new InvalidOperationException("Active quest acceptance changed during capture; refresh again.");
            active.Add(new ActiveQuestData(work.QuestId, work.Sequence, work.Flags,
                work.AcceptClassJob, ImmutableArray.CreateRange(work.Variables.ToArray())));
        }

        var source = $"QuestManager.NormalQuests + IsQuestComplete; local Lumina Quest sheet. FFXIVClientStructs {typeof(QuestManager).Assembly.GetName().Version}; Lumina.Excel {typeof(Quest).Assembly.GetName().Version}";
        // The pure builder guards every call and uses only the ushort overload.
        var snapshot = QuestSnapshotBuilder.Build(definitions, active,
            static id => QuestManager.IsQuestComplete(id), started, source, completionBaseline);
        if (this.questSheet is { } sheet)
        {
            var observations = snapshot.Quests.ToBuilder();
            for (var i = 0; i < observations.Count; i++)
            {
                var observation = observations[i];
                if (observation.Accepted && observation.RowId is { } rowId &&
                    observation.Sequence is { } sequence && sheet.TryGetRow(rowId, out var row))
                    observations[i] = observation with { JournalText = this.journalText.TryRead(row, sequence) };
            }
            snapshot = snapshot with { Quests = observations.ToImmutable() };
        }

        // The caller also rechecks login + session epoch immediately before publication.
        if (PlayerState.Instance() != player || QuestManager.Instance() != manager || !player->IsLoaded)
            throw new InvalidOperationException("Player/quest state changed during capture; refresh again.");
        return snapshot with { CaptureDurationMs = timer.Elapsed.TotalMilliseconds };
    }

    private QuestCatalog GetCatalog()
    {
        if (this.catalog is not null)
            return this.catalog;
        try
        {
            var sheet = data.GetExcelSheet<Quest>();
            var rows = new List<QuestDefinition>();
            foreach (var row in sheet)
            {
                if (rows.Count >= 65536)
                    throw new InvalidOperationException("Quest sheet row count exceeded the reader bound.");
                string? name;
                try { name = row.Name.ExtractText(); }
                catch { name = null; } // Preserve numeric IDs when a name cannot be read.
                rows.Add(new QuestDefinition(row.RowId, name, row.IsRepeatable));
            }
            var result = QuestCatalog.Create(rows);
            if (result.Definitions.Count == 0)
                return QuestCatalog.Unavailable("No supported unique Quest sheet rows; completion scan unavailable.");
            this.questSheet = sheet;
            return this.catalog = result;
        }
        catch (Exception ex)
        {
            // A failed catalog is retried on the next explicit capture; active IDs remain useful.
            return QuestCatalog.Unavailable($"Local Quest catalog unavailable ({ex.GetType().Name}); completion scan unavailable.");
        }
    }
}
