using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace CompAI;

/// <summary>Framework-thread only. Copies the loaded character's notebook bits; never requests data or opens a menu.</summary>
internal sealed unsafe class VariantNotebookReader(IDataManager data)
{
    internal const int CompletionBitCount = 49;
    internal const int CompletionByteCount = 7;
    internal const int SeriesCount = 4;
    internal const int SeriesSlots = 13;
    private VariantNotebookCatalog? catalog;

    internal ProgressionSnapshot Capture()
    {
        try
        {
            var player = PlayerState.Instance();
            if (player == null || !player->IsLoaded || player->ContentId == 0)
                return Unavailable("Loaded character Variant notebook state is unavailable.");
            var owner = player->ContentId;
            var currentCatalog = GetCatalog();
            var first = ReadBitmap(player);
            var second = ReadBitmap(player);
            var snapshot = Describe(DateTimeOffset.UtcNow, currentCatalog.Definitions, first, second);
            if (PlayerState.Instance() != player || !player->IsLoaded || player->ContentId != owner ||
                !CatalogMatchesCurrent(currentCatalog))
            {
                catalog = null;
                return Unavailable("Character or public Variant notebook state changed during reading; try again after loading.");
            }
            return snapshot;
        }
        catch (Exception ex)
        {
            catalog = null;
            return Unavailable($"Variant notebook read unavailable ({ex.GetType().Name}).");
        }
    }

    private static byte[] ReadBitmap(PlayerState* player)
    {
        if (player->CompletedVVDNotebookContentsBitArray.BitCount != CompletionBitCount ||
            player->CompletedVVDNotebookContents.Length != CompletionByteCount)
            throw new InvalidOperationException("Variant notebook structure bounds are unsupported.");
        return player->CompletedVVDNotebookContents.ToArray();
    }

    private VariantNotebookCatalog GetCatalog()
    {
        var gameData = data.GameData;
        var language = data.Language.ToString();
        var contentsSheet = data.GetExcelSheet<VVDNotebookContents>();
        var seriesSheet = data.GetExcelSheet<VVDNotebookSeries>();
        if (catalog is { } cached && cached.Matches(contentsSheet.RawSheet, seriesSheet.RawSheet, gameData, language))
            return cached;
        catalog = null;
        var contents = contentsSheet.Take(CompletionBitCount + 2).ToArray();
        var series = seriesSheet.Take(SeriesCount + 2).ToArray();
        if (contents.Length != CompletionBitCount + 1 || series.Length != SeriesCount + 1 ||
            contents.Count(row => row.RowId == 0) != 1 || series.Count(row => row.RowId == 0) != 1)
            throw new InvalidOperationException("Variant notebook public catalog bounds are unsupported.");
        // Titles only: notebook Description contains story paragraphs, which this reader never copies.
        var definitions = BuildCatalog(contents.Where(row => row.RowId != 0)
                .Select(row => new VariantNotebookContent(row.RowId, row.Name.ExtractText().Trim())),
            series.Where(row => row.RowId != 0).Select(row => new VariantNotebookSeriesDefinition(
                row.RowId, row.Name.ExtractText().Trim(), row.Contents.Select(entry => entry.RowId).ToArray())));
        var result = new VariantNotebookCatalog(contentsSheet.RawSheet, seriesSheet.RawSheet, gameData, language, definitions);
        if (!CatalogMatchesCurrent(result))
            throw new InvalidOperationException("Public Variant notebook definitions changed during capture.");
        return catalog = result;
    }

    private bool CatalogMatchesCurrent(VariantNotebookCatalog current) =>
        current.Matches(data.GetExcelSheet<VVDNotebookContents>().RawSheet, data.GetExcelSheet<VVDNotebookSeries>().RawSheet,
            data.GameData, data.Language.ToString());

    internal static VariantNotebookDefinition[] BuildCatalog(IEnumerable<VariantNotebookContent> contents,
        IEnumerable<VariantNotebookSeriesDefinition> series)
    {
        var rows = contents.Take(CompletionBitCount + 1).ToArray();
        var groups = series.Take(SeriesCount + 1).ToArray();
        if (rows.Length != CompletionBitCount || groups.Length != SeriesCount ||
            rows.Any(row => row is null || row.Id is < 1 or > CompletionBitCount ||
                !ProgressionSnapshot.ValidText(row.Title, ProgressionSnapshot.MaxNameLength)) ||
            rows.Select(row => row.Id).Distinct().Count() != rows.Length ||
            groups.Any(group => group is null || group.Id is < 1 or > SeriesCount ||
                !ProgressionSnapshot.ValidText(group.Name, ProgressionSnapshot.MaxNameLength) ||
                group.ContentIds is null || group.ContentIds.Count != SeriesSlots) ||
            groups.Select(group => group.Id).Distinct().Count() != groups.Length)
            throw new InvalidOperationException("Variant notebook catalog is invalid or unsupported.");
        var titles = rows.ToDictionary(row => row.Id, row => row.Title);
        var seen = new HashSet<uint>();
        var result = new List<VariantNotebookDefinition>();
        foreach (var group in groups)
        {
            var empty = false;
            for (var index = 0; index < SeriesSlots; index++)
            {
                var id = group.ContentIds[index];
                if (id == 0) { empty = true; continue; }
                if (empty || !titles.TryGetValue(id, out var title) || !seen.Add(id))
                    throw new InvalidOperationException("Variant notebook series links are invalid or unsupported.");
                // Number is the position in the public series Contents list, not the global notebook RowId.
                var name = $"{group.Name} — Record {index + 1}: {title}";
                if (!ProgressionSnapshot.ValidText(name, ProgressionSnapshot.MaxNameLength))
                    throw new InvalidOperationException("Variant notebook label exceeds the supported bounds.");
                result.Add(new(id, group.Id, index + 1, name));
            }
        }
        if (seen.Count != rows.Length)
            throw new InvalidOperationException("Variant notebook series do not cover the named catalog.");
        return result.OrderBy(row => row.Id).ToArray();
    }

    internal static ProgressionSnapshot Describe(DateTimeOffset capturedAt,
        IEnumerable<VariantNotebookDefinition> definitions, ReadOnlySpan<byte> first, ReadOnlySpan<byte> second,
        bool loaded = true)
    {
        if (!loaded) return Unavailable("Loaded character Variant notebook state is unavailable.");
        if (first.Length != CompletionByteCount || second.Length != CompletionByteCount || !first.SequenceEqual(second))
            return Unavailable("Variant notebook bounds or observations changed during reading.");
        var rows = definitions.Take(CompletionBitCount + 1).ToArray();
        if (rows.Length == 0 || rows.Length > CompletionBitCount ||
            rows.Any(row => row is null || row.Id is < 1 or > CompletionBitCount ||
                row.SeriesId is < 1 or > SeriesCount || row.RecordNumber is < 1 or > SeriesSlots ||
                !ProgressionSnapshot.ValidText(row.Name, ProgressionSnapshot.MaxNameLength)) ||
            rows.Select(row => row.Id).Distinct().Count() != rows.Length ||
            rows.Select(row => (row.SeriesId, row.RecordNumber)).Distinct().Count() != rows.Length)
            return Unavailable("Variant notebook catalog is invalid or unsupported.");
        var entries = new List<ProgressionEntry>();
        foreach (var row in rows)
        {
            // Pinned ClientStructs has 49 bits for 49 nonzero notebook rows. Independent readers
            // Recollection c49bb280/UnlockableSurveyRecord and Charon ebed9639/VariantRecord use RowId - 1.
            var bit = checked((int)row.Id - 1);
            var completed = (first[bit >> 3] & (1 << (bit & 7))) != 0;
            entries.Add(new(row.Id, row.Name, completed ? "completed" : "not_observed_complete",
                Completed: completed ? true : null, UnknownReason: completed ? null :
                    "No completion bit was observed, but notebook-specific negative readiness is unverified; this does not prove an unfinished record."));
        }
        return new("variant_notebook", capturedAt, true, "positive_observations_only", entries);
    }

    private static ProgressionSnapshot Unavailable(string reason) => ProgressionSnapshot.Unavailable("variant_notebook", reason);
}

internal sealed record VariantNotebookContent(uint Id, string Title);
internal sealed record VariantNotebookSeriesDefinition(uint Id, string Name, IReadOnlyList<uint> ContentIds);
internal sealed record VariantNotebookDefinition(uint Id, uint SeriesId, int RecordNumber, string Name);
internal sealed record VariantNotebookCatalog(object ContentsSheet, object SeriesSheet, object GameData, string Language,
    VariantNotebookDefinition[] Definitions)
{
    internal bool Matches(object contentsSheet, object seriesSheet, object gameData, string language) =>
        ReferenceEquals(ContentsSheet, contentsSheet) && ReferenceEquals(SeriesSheet, seriesSheet) &&
        ReferenceEquals(GameData, gameData) && Language == language;
}
