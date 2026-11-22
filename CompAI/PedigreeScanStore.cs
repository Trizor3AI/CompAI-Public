using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CompAI;

// Managed state only. The owner serializes all access and clears this on inventory changes.
internal sealed class PedigreeScanStore
{
    private readonly Dictionary<string, PedigreeCard> cards = new(StringComparer.Ordinal);
    private string? pendingFingerprint;
    private DateTimeOffset pendingSince;

    // Only changes to decoded evidence invalidate an inventory capture. Starting or
    // stopping tooltip collection must not discard otherwise unchanged readings.
    internal int AttachmentRevision { get; private set; }
    internal bool HasReadings => this.cards.Values.Any(card => card.Reading is not null);

    internal PedigreeCard[] Cards => this.cards.Values
        .OrderBy(card => card.Sample.Bag, StringComparer.Ordinal).ThenBy(card => card.Sample.Slot)
        .Select(card => card with { Sample = CloneSample(card.Sample), Reading = CloneReading(card.Reading) }).ToArray();

    internal string Observe(HoveredChocoboTooltip sample)
    {
        if (sample.Bag is not ("Inventory1" or "Inventory2" or "Inventory3" or "Inventory4") ||
            sample.Slot >= 35 || sample.ItemId != sample.BaseItemId ||
            !PedigreeDecoder.TryClassify(sample.BaseItemId, out _, out _, out _))
        {
            this.ResetPending();
            return "The hovered item is not a supported carried chocobo card.";
        }
        // Coordinates may move with the tooltip. Require stable identity and encoded contents,
        // then decode using the latest geometry; timestamps are not part of identity.
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            sample.Bag, sample.Slot, sample.ItemId, sample.BaseItemId, sample.ItemName,
            sample.BreedingCapacity, sample.StatLabels,
            nodes = sample.Nodes.Select(node => new { node.NodeId, node.Text, node.Encoded, node.Runs }),
        })));
        var key = Key(sample.Bag, sample.Slot);
        // Revoke the old card as soon as differing contents are observed, even before the
        // replacement has settled. A send in that interval must not reuse old parentage.
        if (this.cards.TryGetValue(key, out var previous) && previous.Fingerprint != fingerprint)
        {
            if (previous.Reading is not null) ++this.AttachmentRevision;
            this.cards.Remove(key);
        }
        if (fingerprint != this.pendingFingerprint || sample.CapturedAtUtc < this.pendingSince)
        {
            this.pendingFingerprint = fingerprint;
            this.pendingSince = sample.CapturedAtUtc;
            return "Keep hovering briefly while the tooltip settles.";
        }
        if (sample.CapturedAtUtc - this.pendingSince < TimeSpan.FromMilliseconds(200))
            return "Keep hovering briefly while the tooltip settles.";

        var decoded = PedigreeDecoder.TryDecode(sample, out var reading, out var reason);
        if (this.cards.TryGetValue(key, out var existing) && existing.Fingerprint == fingerprint &&
            SameReading(existing.Reading, reading))
            return decoded ? "Card decoded locally. Parent stars are shown below." : reason;

        // Complete decoded rows become available automatically. An incomplete layout
        // remains diagnostic-only and never supplies guessed or partial parentage.
        if (existing?.Reading is not null || reading is not null) ++this.AttachmentRevision;
        this.cards[key] = new PedigreeCard(key, fingerprint, CloneSample(sample), reading, reason);
        return decoded ? "Card decoded locally. Parent stars are shown below." : reason;
    }

    internal ObservedPedigree? Attachment(string bag, uint slot, uint itemId, uint baseItemId,
        int quantity, bool isHq)
    {
        if (quantity != 1 || isHq || !this.cards.TryGetValue(Key(bag, slot), out var card) ||
            card.Reading is not { } reading || card.Sample.ItemId != itemId ||
            card.Sample.BaseItemId != baseItemId)
            return null;
        return new ObservedPedigree("ItemDetail", "decoded_tooltip", card.Sample.CapturedAtUtc,
            reading.Kind, reading.Grade, reading.Sex,
            (int[])reading.FatherStars.Clone(), (int[])reading.MotherStars.Clone());
    }

    internal void ResetPending() => this.pendingFingerprint = null;
    internal void Clear() { ++this.AttachmentRevision; this.cards.Clear(); this.ResetPending(); }
    internal bool HasReading(string bag, uint slot) =>
        this.cards.TryGetValue(Key(bag, slot), out var card) && card.Reading is not null;

    private static string Key(string bag, uint slot) => $"{bag}:{slot}";
    private static HoveredChocoboTooltip CloneSample(HoveredChocoboTooltip sample) => sample with
    {
        StatLabels = (string[])sample.StatLabels.Clone(),
        Nodes = sample.Nodes.Select(node => node with
        {
            Encoded = (byte[])node.Encoded.Clone(), Runs = (TooltipRun[])node.Runs.Clone(),
        }).ToArray(),
    };
    private static PedigreeReading? CloneReading(PedigreeReading? reading) => reading is null ? null : reading with
    {
        FatherStars = (int[])reading.FatherStars.Clone(), MotherStars = (int[])reading.MotherStars.Clone(),
    };
    private static bool SameReading(PedigreeReading? first, PedigreeReading? second) =>
        first is null ? second is null : second is not null && first.Kind == second.Kind &&
        first.Grade == second.Grade && first.Sex == second.Sex &&
        first.FatherStars.SequenceEqual(second.FatherStars) && first.MotherStars.SequenceEqual(second.MotherStars);
}

internal sealed record PedigreeCard(string Key, string Fingerprint, HoveredChocoboTooltip Sample,
    PedigreeReading? Reading, string Reason);

internal sealed record PedigreeScanView(bool Scanning, int SecondsLeft, string Status, PedigreeCard[] Cards);

internal sealed record ObservedPedigree(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("observed_at_utc")] DateTimeOffset ObservedAtUtc,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("grade")] int Grade,
    [property: JsonPropertyName("sex")] string Sex,
    [property: JsonPropertyName("father_stars")] int[] FatherStars,
    [property: JsonPropertyName("mother_stars")] int[] MotherStars);
