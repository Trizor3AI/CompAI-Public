using System;
using System.Collections.Immutable;
using System.Linq;

namespace CompAI;

internal enum CharacterCacheLoadStatus { Missing, Loaded, Unreadable, UnsupportedSchema, TemporarilyUnavailable }

internal sealed record CharacterCacheLoadResult(CharacterCacheLoadStatus Status, CharacterCacheData? Data);

// Disk DTOs contain managed values only. Positional properties are public so all
// saved fields round-trip through System.Text.Json, including quest journal text.
internal sealed record CharacterCacheData(ulong ContentId, string CharacterLabel, long Revision,
    DateTimeOffset SavedAtUtc, CachedInventorySnapshot? Inventory, CachedQuestSnapshot? Quests)
{
    public int SchemaVersion { get; init; } = 4;
    public ImmutableArray<StorageContainerCapture> Storage { get; init; } = ImmutableArray<StorageContainerCapture>.Empty;
    public ImmutableArray<ProgressionMenuCapture> Progression { get; init; } = ImmutableArray<ProgressionMenuCapture>.Empty;
    public ImmutableArray<CachedMenuObservation> Menus { get; init; } = ImmutableArray<CachedMenuObservation>.Empty;
}

internal sealed record CachedInventorySnapshot(DateTimeOffset CapturedAtUtc,
    ImmutableArray<CachedInventoryBag> Bags);

internal sealed record CachedInventoryBag(string Bag, int? SlotCount, string? UnavailableReason,
    ImmutableArray<CachedInventoryItem> Items);

internal sealed record CachedInventoryItem(uint Slot, uint ItemId, uint BaseItemId, string? Name,
    int Quantity, bool IsHq, CachedPedigree? Pedigree = null);

internal sealed record CachedPedigree(string Source, string Status, DateTimeOffset ObservedAtUtc,
    string Kind, int Grade, string Sex, ImmutableArray<int> FatherStars, ImmutableArray<int> MotherStars)
{
    internal static CachedPedigree From(ObservedPedigree value) => new(value.Source, value.Status,
        value.ObservedAtUtc, value.Kind, value.Grade, value.Sex,
        value.FatherStars.ToImmutableArray(), value.MotherStars.ToImmutableArray());

    internal ObservedPedigree ToPedigree() => new(this.Source, this.Status, this.ObservedAtUtc,
        this.Kind, this.Grade, this.Sex, this.FatherStars.ToArray(), this.MotherStars.ToArray());
}

internal sealed record CachedQuestSnapshot(DateTimeOffset CapturedAtUtc, double CaptureDurationMs,
    string SourceBuild, string ReadinessNote, string CoverageNote, string? CatalogNote,
    int CatalogRows, int CompletionChecks, int ActiveSlotCount, ImmutableArray<CachedQuestObservation> Quests)
{
    internal static CachedQuestSnapshot From(QuestSnapshot value) => new(value.CapturedAtUtc,
        value.CaptureDurationMs, value.SourceBuild, value.ReadinessNote, value.CoverageNote, value.CatalogNote,
        value.CatalogRows, value.CompletionChecks, value.ActiveSlotCount,
        value.Quests.Select(CachedQuestObservation.From).ToImmutableArray());

    internal QuestSnapshot ToSnapshot() => new(this.CapturedAtUtc, this.CaptureDurationMs,
        this.SourceBuild, this.ReadinessNote, this.CoverageNote, this.CatalogNote,
        this.CatalogRows, this.CompletionChecks, this.ActiveSlotCount,
        this.Quests.Select(value => value.ToObservation()).ToImmutableArray());
}

internal sealed record CachedQuestObservation(uint? RowId, ushort NativeId, string? Name,
    bool? IsRepeatable, bool Accepted, bool? Completed, byte? Sequence, byte? Flags,
    byte? AcceptClassJob, ImmutableArray<byte> Variables, string? UnavailableReason, string? JournalText)
{
    internal static CachedQuestObservation From(QuestObservation value) => new(value.RowId, value.NativeId,
        value.Name, value.IsRepeatable, value.Accepted, value.Completed, value.Sequence, value.Flags,
        value.AcceptClassJob, value.Variables, value.UnavailableReason, value.JournalText);

    internal QuestObservation ToObservation() => new(this.RowId, this.NativeId, this.Name,
        this.IsRepeatable, this.Accepted, this.Completed, this.Sequence, this.Flags, this.AcceptClassJob,
        this.Variables, this.UnavailableReason) { JournalText = this.JournalText };
}
