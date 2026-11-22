using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace CompAI;

/// <summary>Framework-thread only. Learned spell observations never depend on the 24 equipped action slots.</summary>
internal sealed unsafe class BlueMageReader(IDataManager data)
{
    internal const int UnlockLinkBitCount = 736;
    private BlueMageDefinition[]? catalog;

    internal CollectionSnapshot Capture()
    {
        const string category = "blue_mage_spells";
        try
        {
            var player = PlayerState.Instance();
            var ui = UIState.Instance();
            if (player == null || !player->IsLoaded || player->ContentId == 0 || ui == null)
                return CollectionSnapshot.Unavailable(category, "Loaded character Blue Mage spellbook state is unavailable.");
            if (ui->UnlockLinksBitArray.BitCount != UnlockLinkBitCount)
                return CollectionSnapshot.Unavailable(category, "Native unlock-link bounds do not match the supported structure.");
            var owner = player->ContentId;
            var result = Describe(DateTimeOffset.UtcNow, GetCatalog(), link =>
            {
                var learned = ui->IsUnlockLinkUnlocked(link);
                if (learned != ui->IsUnlockLinkUnlocked(link))
                    throw new InvalidOperationException("Blue Mage learned-spell state changed during reading.");
                return learned;
            });
            if (PlayerState.Instance() != player || UIState.Instance() != ui || !player->IsLoaded ||
                player->ContentId != owner || ui->UnlockLinksBitArray.BitCount != UnlockLinkBitCount)
                return CollectionSnapshot.Unavailable(category, "Character or spellbook state changed during reading; try again after loading.");
            return result;
        }
        catch (Exception ex)
        {
            return CollectionSnapshot.Unavailable(category, $"Blue Mage spellbook read unavailable ({ex.GetType().Name}).");
        }
    }

    private BlueMageDefinition[] GetCatalog()
    {
        if (this.catalog is not null) return this.catalog;
        var rows = data.GetExcelSheet<AozAction>().Take(CollectionSnapshot.MaxEntries + 1).ToArray();
        if (rows.Length > CollectionSnapshot.MaxEntries)
            throw new InvalidOperationException("Blue Mage catalog exceeds the supported bound.");
        var transient = data.GetExcelSheet<AozActionTransient>();
        var definitions = new List<BlueMageDefinition>();
        foreach (var row in rows)
        {
            if (row.RowId == 0) continue;
            if (!transient.TryGetRow(row.RowId, out var details) || details.Number == 0)
                throw new InvalidOperationException("A displayed Blue Mage spellbook number is unavailable.");
            var validAction = row.Action.RowId != 0 && row.Action.IsValid;
            var name = validAction ? row.Action.Value.Name.ExtractText().Trim() : "";
            if (!CollectionSnapshot.ValidText(name, CollectionSnapshot.MaxNameLength))
                name = "[Blue Mage spell name unavailable]";
            // The three ID domains are independent: AozAction row, Action row, and UnlockLink.
            // Public result IDs use the displayed Number; native calls receive only the validated UnlockLink.
            uint? link = validAction && !row.Action.Value.UnlockLink.Is<Quest>()
                ? row.Action.Value.UnlockLink.RowId : null;
            definitions.Add(new(details.Number, name, row.RowId, validAction ? row.Action.RowId : 0, link));
        }
        if (definitions.Count == 0 || definitions.Select(row => row.SpellbookNumber).Distinct().Count() != definitions.Count)
            throw new InvalidOperationException("Blue Mage spellbook numbers are missing or ambiguous.");
        return this.catalog = definitions.OrderBy(row => row.SpellbookNumber).ToArray();
    }

    internal static CollectionSnapshot Describe(DateTimeOffset capturedAt, IEnumerable<BlueMageDefinition> definitions,
        Func<uint, bool> observeUnlockLink, bool loaded = true)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(observeUnlockLink);
        const string category = "blue_mage_spells";
        if (!loaded) return CollectionSnapshot.Unavailable(category, "Loaded character Blue Mage spellbook state is unavailable.");
        var rows = definitions.Take(CollectionSnapshot.MaxEntries + 1).ToArray();
        if (rows.Length == 0 || rows.Length > CollectionSnapshot.MaxEntries ||
            rows.Any(row => row is null || row.SpellbookNumber == 0 ||
                !CollectionSnapshot.ValidText(row.Name, CollectionSnapshot.MaxNameLength)) ||
            rows.Select(row => row.SpellbookNumber).Distinct().Count() != rows.Length)
            return CollectionSnapshot.Unavailable(category, "Blue Mage spellbook catalog is invalid or unavailable.");
        var entries = new List<CollectionEntry>();
        foreach (var row in rows)
        {
            if (row.SpellbookNumber > byte.MaxValue || row.AozActionId is 0 or > CollectionSnapshot.MaxEntries ||
                row.ActionId == 0 || row.UnlockLink is not (> 0 and < UnlockLinkBitCount))
            {
                entries.Add(new(row.SpellbookNumber, row.Name, "unknown", null,
                    "This spell has no supported action/unlock-link mapping; learned state is unknown."));
                continue;
            }
            var learned = observeUnlockLink(row.UnlockLink.Value);
            entries.Add(new(row.SpellbookNumber, row.Name, learned ? "unlocked" : "not_observed_unlocked",
                learned ? true : null, learned ? null :
                    "A native false was observed, but learned-spell negative-state readiness is unverified; this does not mean unlearned."));
        }
        return new(category, capturedAt, true, entries);
    }
}

internal sealed record BlueMageDefinition(uint SpellbookNumber, string Name, uint AozActionId,
    uint ActionId, uint? UnlockLink);
