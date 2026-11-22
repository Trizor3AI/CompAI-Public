using System;
using System.Collections.Generic;
using System.Linq;

namespace CompAI;

// Compare decoded tooltip evidence with the exact managed rows used by inventory
// lookup. A decoded UI row alone is not proof that its refresh reached the snapshot.
internal sealed record PedigreeAttachmentStatus(int DecodedCount, int SavedCount,
    bool CapturePending, string? RefreshFailure)
{
    internal int MissingCount => this.DecodedCount - this.SavedCount;
    internal string? BlockReason => this.MissingCount == 0 ? null : this.CapturePending
        ? "Decoded pedigree readings are still joining the saved inventory. Wait for capture to finish."
        : "Decoded pedigree readings are missing from the saved inventory. Open Settings > Pedigrees and retry inventory refresh.";

    internal static PedigreeAttachmentStatus Create(IEnumerable<PedigreeCard> cards,
        IEnumerable<InventoryLookupRow> savedRows, bool capturePending, string? unavailableReason)
    {
        var decodedCards = cards.Where(card => card.Reading is not null).ToArray();
        if (decodedCards.Length == 0) return new(0, 0, capturePending, null);
        var rows = savedRows.ToDictionary(row => (row.bag, row.slot));
        var attached = decodedCards.Count(card => rows.TryGetValue((card.Sample.Bag, card.Sample.Slot), out var row) &&
            row.item_id == card.Sample.ItemId && row.base_item_id == card.Sample.BaseItemId &&
            row.quantity == 1 && !row.is_hq && row.pedigree is { } saved &&
            saved.Source == "ItemDetail" && saved.Status == "decoded_tooltip" &&
            saved.ObservedAtUtc == card.Sample.CapturedAtUtc && saved.Kind == card.Reading!.Kind &&
            saved.Grade == card.Reading.Grade && saved.Sex == card.Reading.Sex &&
            saved.FatherStars.SequenceEqual(card.Reading.FatherStars) &&
            saved.MotherStars.SequenceEqual(card.Reading.MotherStars));
        return new(decodedCards.Length, attached, capturePending,
            attached < decodedCards.Length && !capturePending ? unavailableReason : null);
    }
}
