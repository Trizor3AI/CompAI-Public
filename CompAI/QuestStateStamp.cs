using System;

namespace CompAI;

/// <summary>
/// Local change-detection state only; never part of a UI snapshot or export.
/// Owns its copied bytes so later native or caller mutations cannot alter equality.
/// </summary>
internal sealed class QuestStateStamp : IEquatable<QuestStateStamp>
{
    internal const int ExpectedVariableCount = 6;
    internal const int ActiveSlotBytes = 2 + 3 + ExpectedVariableCount;
    internal const int ExpectedActiveBytes = QuestCatalog.ExpectedActiveSlots * ActiveSlotBytes;

    private readonly byte[] state;

    internal QuestStateStamp(ulong contentId, ReadOnlySpan<byte> completedQuests, ReadOnlySpan<byte> activeQuests)
    {
        if (contentId == 0)
            throw new InvalidOperationException("Character identity is unavailable for quest state.");
        if (completedQuests.Length != QuestCatalog.ExpectedCompletionBytes || activeQuests.Length != ExpectedActiveBytes)
            throw new InvalidOperationException("Quest layout differs from the inspected build; state unavailable.");

        this.ContentId = contentId;
        this.state = new byte[completedQuests.Length + activeQuests.Length];
        completedQuests.CopyTo(this.state);
        activeQuests.CopyTo(this.state.AsSpan(completedQuests.Length));
    }

    internal ulong ContentId { get; }

    // Completion storage is compared opaquely; only the native API interprets it.
    internal bool HasSameCompletionState(QuestStateStamp? other) =>
        other is not null && this.ContentId == other.ContentId &&
        this.state.AsSpan(0, QuestCatalog.ExpectedCompletionBytes)
            .SequenceEqual(other.state.AsSpan(0, QuestCatalog.ExpectedCompletionBytes));

    public bool Equals(QuestStateStamp? other) =>
        ReferenceEquals(this, other) ||
        (other is not null && this.ContentId == other.ContentId && this.state.AsSpan().SequenceEqual(other.state));

    public override bool Equals(object? obj) => obj is QuestStateStamp other && this.Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(this.ContentId);
        hash.AddBytes(this.state);
        return hash.ToHashCode();
    }

    public static bool operator ==(QuestStateStamp? left, QuestStateStamp? right) =>
        ReferenceEquals(left, right) || (left is not null && left.Equals(right));

    public static bool operator !=(QuestStateStamp? left, QuestStateStamp? right) => !(left == right);
}
