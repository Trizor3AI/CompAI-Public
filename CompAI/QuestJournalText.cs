using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

namespace CompAI;

/// <summary>Static journal paragraphs only; never evaluates game-dependent SeString macros.</summary>
internal sealed class QuestJournalText(IDataManager data)
{
    private const int MaxCachedQuests = 64;
    private const int MaxSheetRows = 20000;
    private const int MaxTextBytes = 8192;
    private const int MaxTextCharacters = 2048;
    private readonly Dictionary<uint, Dictionary<byte, string>> cache = new();
    private readonly Queue<uint> cacheOrder = new();

    // Sheet layout/path and SEQ key convention verified against HaselDebug:
    // https://github.com/Haselnussbomber/HaselDebug/blob/91b4e56b626d0347edf22f3825b187ac07bc5996/HaselDebug/Utils/UnlocksTabUtils.cs
    // https://github.com/Haselnussbomber/HaselDebug/blob/91b4e56b626d0347edf22f3825b187ac07bc5996/HaselDebug/Sheets/QuestText.cs
    // SEQ paragraphs are journal narrative, not per-objective completion or counters.
    internal string? TryRead(Quest row, byte sequence)
    {
        if (row.RowId <= 0x10000 || row.RowId >= 0x20000)
            return null;

        if (!this.cache.TryGetValue(row.RowId, out var paragraphs))
        {
            paragraphs = this.Load(row);
            if (this.cache.Count >= MaxCachedQuests)
                this.cache.Remove(this.cacheOrder.Dequeue());
            this.cache.Add(row.RowId, paragraphs);
            this.cacheOrder.Enqueue(row.RowId);
        }

        return paragraphs.GetValueOrDefault(sequence);
    }

    private Dictionary<byte, string> Load(Quest row)
    {
        var paragraphs = new Dictionary<byte, string>();
        try
        {
            var id = PlainText(row.Id, 128);
            if (string.IsNullOrEmpty(id)) return paragraphs;
            foreach (var character in id)
                if (!char.IsAsciiLetterOrDigit(character) && character != '_') return paragraphs;

            var sheet = data.GetExcelSheet<RawRow>(name: $"quest/{(row.RowId - 0x10000) / 100:000}/{id}");
            if (sheet.Count > MaxSheetRows) return paragraphs;
            var prefix = $"TEXT_{id.ToUpperInvariant()}_SEQ_";
            var duplicateSequences = new HashSet<byte>();
            foreach (var textRow in sheet)
            {
                if (textRow.Columns.Count != 2) return new();
                var key = PlainText(textRow.ReadStringColumn(0), 192);
                if (key is null || !key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var suffix = key.AsSpan(prefix.Length);
                if (!byte.TryParse(suffix, out var sequence) ||
                    !suffix.SequenceEqual(sequence.ToString("00").AsSpan())) continue;

                var text = PlainText(textRow.ReadStringColumn(1), MaxTextCharacters);
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (!paragraphs.TryAdd(sequence, text)) duplicateSequences.Add(sequence);
            }

            foreach (var duplicate in duplicateSequences) paragraphs.Remove(duplicate);
            return paragraphs;
        }
        catch (Exception)
        {
            // Missing/localized sheets, incompatible layouts and malformed text are unavailable.
            return new();
        }
    }

    private static string? PlainText(ReadOnlySeString value, int maxCharacters)
    {
        if (value.IsEmpty || value.ByteLength > MaxTextBytes) return null;
        foreach (var payload in value)
            if (payload.Type != ReadOnlySePayloadType.Text) return null;
        var text = value.ExtractText();
        return text.Length <= maxCharacters ? text : null;
    }
}
