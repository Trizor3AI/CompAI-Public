using System;

namespace CompAI;

internal delegate string NpcTextDecoder(ReadOnlySpan<byte> bytes);

/// <summary>
/// One bounded, managed Talk page. Every event compares the full raw bytes, so a
/// rapidly advanced page is detected even when its pointer and byte length match.
/// The caller still checks native readiness/identity before publishing the result.
/// </summary>
internal sealed class NpcDialogueTextCache
{
    internal const int MaxSpeakerBytes = 512;
    internal const int MaxDialogueBytes = 16384;
    private byte[] speaker = [];
    private byte[] sentence = [];
    private NpcDialogueContext? decoded;

    internal bool Matches(ReadOnlySpan<byte> speakerBytes, ReadOnlySpan<byte> sentenceBytes) =>
        this.decoded is not null && speakerBytes.SequenceEqual(this.speaker) &&
        sentenceBytes.SequenceEqual(this.sentence);

    internal NpcDialogueContext Read(ReadOnlySpan<byte> speakerBytes, ReadOnlySpan<byte> sentenceBytes,
        NpcTextDecoder decode)
    {
        if (speakerBytes.Length > MaxSpeakerBytes || sentenceBytes.Length > MaxDialogueBytes)
            throw new InvalidOperationException("Dialogue text exceeds the reader bounds.");
        if (this.Matches(speakerBytes, sentenceBytes)) return this.decoded!;

        // Own both copies before decoding; no native-backed span is retained.
        var copiedSpeaker = speakerBytes.ToArray();
        var copiedSentence = sentenceBytes.ToArray();
        var speakerText = copiedSpeaker.Length == 0 ? string.Empty : decode(copiedSpeaker).Trim();
        var sentenceText = copiedSentence.Length == 0 ? string.Empty : decode(copiedSentence).Trim();
        if (speakerText.Length > NpcConversationStore.MaxSpeakerCharacters ||
            sentenceText.Length > NpcConversationStore.MaxTextCharacters)
            throw new InvalidOperationException("Dialogue text exceeds the reader bounds.");
        var result = new NpcDialogueContext(speakerText, sentenceText);
        this.speaker = copiedSpeaker;
        this.sentence = copiedSentence;
        return this.decoded = result;
    }

    internal void Clear()
    {
        this.speaker = [];
        this.sentence = [];
        this.decoded = null;
    }
}
