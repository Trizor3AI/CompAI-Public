using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace CompAI;

// Only explicit picker selections or image paste gestures enter here. Paths never survive this read.
internal sealed class ChatAttachment
{
    internal const int MaxCount = 3;
    internal const int MaxTextBytes = 32 * 1024;
    internal const int MaxImageBytes = 2 * 1024 * 1024;
    internal const int MaxTotalBytes = 4 * 1024 * 1024;
    internal const int MaxSessionBytes = 16 * 1024 * 1024;
    internal const int MaxRequestBytes = 6 * 1024 * 1024;
    internal const string Limits = "Up to 3 files, 4 MiB per question and 16 MiB across open drafts/retries. PNG/JPEG: 2 MiB each, at most 8192 pixels per side and 32 million pixels. UTF-8 TXT/MD/LOG/JSON: 32 KiB each. Files are sent to the AI only with this question. Contents are kept in memory for an explicit retry, not saved with chats or automatically sent again.";
    internal string Name { get; }
    internal string MediaType { get; }
    internal string Base64 { get; }
    internal int ByteCount { get; }
    internal bool IsImage => this.MediaType.StartsWith("image/", StringComparison.Ordinal);
    internal string Label => $"{this.Name} ({this.ByteCount / 1024.0:0.#} KiB)";
    internal object Wire => new { name = this.Name, media_type = this.MediaType,
        data_base64 = this.Base64, byte_count = this.ByteCount };

    private ChatAttachment(string name, string type, byte[] bytes)
    { this.Name = name; this.MediaType = type; this.ByteCount = bytes.Length; this.Base64 = Convert.ToBase64String(bytes); }

    internal static ChatAttachment[] Freeze(IReadOnlyList<ChatAttachment>? attachments)
    {
        var result = attachments?.ToArray() ?? Array.Empty<ChatAttachment>();
        if (result.Length > MaxCount || result.Any(a => a is null) || result.Sum(a => (long)a.ByteCount) > MaxTotalBytes)
            throw new InvalidDataException("Attachments exceed the 3-file or 4 MiB total limit.");
        return result;
    }

    internal static ChatAttachment ReadSelected(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var mediaType = extension switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg",
            ".json" => "application/json", ".txt" or ".md" or ".log" => "text/plain", _ => null };
        if (mediaType is null) throw new InvalidDataException("Choose a PNG, JPEG, UTF-8 text or JSON file.");
        var name = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 120 || name.Any(c => char.IsControl(c) || c is '/' or '\\' or ':'))
            throw new InvalidDataException("The selected filename is too long or contains unsupported characters.");
        var maximum = mediaType.StartsWith("image/", StringComparison.Ordinal) ? MaxImageBytes : MaxTextBytes;
        // A bounded stream read avoids allocating based on an untrusted or changing size.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 || stream.Length > maximum)
            throw new InvalidDataException(mediaType.StartsWith("image/", StringComparison.Ordinal)
                ? "The image must be nonempty and no larger than 2 MiB." : "The text file must be nonempty and no larger than 32 KiB.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("The file changed while it was being read; select it again.");
        ValidateContent(mediaType, bytes);
        return new(name, mediaType, bytes);
    }

    internal static ChatAttachment FromClipboardPng(byte[] bytes)
    {
        if (bytes.Length is <= 0 or > MaxImageBytes)
            throw new InvalidDataException("The pasted image must be no larger than 2 MiB. Crop it or attach a smaller image.");
        ValidateContent("image/png", bytes);
        // No clipboard owner, source path, window title or other clipboard format is retained.
        return new("Pasted image.png", "image/png", bytes);
    }

    internal static void ValidateContent(string mediaType, byte[] bytes)
    {
        if (mediaType is "text/plain" or "application/json")
        {
            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes); if (text.StartsWith('\ufeff')) text = text[1..]; }
            catch (DecoderFallbackException) { throw new InvalidDataException("The selected text file must use valid UTF-8 encoding."); }
            if (text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
                throw new InvalidDataException("The selected file contains binary control characters.");
            if (mediaType == "application/json")
            {
                try { using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 }); }
                catch (JsonException) { throw new InvalidDataException("The JSON file is invalid or exceeds 32 nesting levels."); }
            }
            return;
        }
        var (width, height) = ImageDimensions(mediaType, bytes);
        if (width is <= 0 or > 8192 || height is <= 0 or > 8192 || (long)width * height > 32_000_000)
            throw new InvalidDataException("The image dimensions are invalid or exceed the attachment limit.");
    }

    private static (int Width, int Height) ImageDimensions(string type, ReadOnlySpan<byte> bytes)
    {
        if (type == "image/png" && bytes.Length >= 33 && bytes[..8].SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}) &&
            bytes.Slice(12, 4).SequenceEqual("IHDR"u8) && BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(8, 4)) == 13)
        {
            ValidatePngChunks(bytes);
            return (checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(16, 4))),
                checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(20, 4))));
        }
        if (type == "image/jpeg" && bytes.Length >= 4 && bytes[0] == 255 && bytes[1] == 216 && bytes[^2] == 255 && bytes[^1] == 217)
        {
            var offset = 2;
            while (offset + 4 <= bytes.Length)
            {
                if (bytes[offset++] != 255) break;
                while (offset < bytes.Length && bytes[offset] == 255) offset++;
                if (offset + 3 > bytes.Length) break;
                var marker = bytes[offset++];
                if (marker is 0xD9 or 0xDA) break;
                var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
                if (length < 2 || offset + length > bytes.Length) break;
                if (marker is 0xC0 or 0xC1 or 0xC2 && length >= 8)
                    return (BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 5, 2)),
                        BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 3, 2)));
                offset += length;
            }
        }
        throw new InvalidDataException("The selected file does not contain a supported PNG or JPEG image.");
    }

    private static void ValidatePngChunks(ReadOnlySpan<byte> bytes)
    {
        var offset = 8;
        var imageData = false;
        while (offset + 12 <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            if (length > bytes.Length - offset - 12) break;
            var chunk = bytes.Slice(offset + 4, checked((int)length + 4));
            var crc = PngCrc.Update(0xffffffff, chunk);
            if ((crc ^ 0xffffffff) != BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 8 + (int)length, 4))) break;
            imageData |= chunk[..4].SequenceEqual("IDAT"u8);
            offset += checked((int)length + 12);
            if (chunk[..4].SequenceEqual("IEND"u8) && length == 0 && offset == bytes.Length && imageData) return;
        }
        throw new InvalidDataException("The PNG image is incomplete or has invalid checksums.");
    }

    internal static string Metadata(IReadOnlyList<ChatAttachment> attachments) => attachments.Count == 0 ? "" :
        "Explicit attachments: " + string.Join(", ", attachments.Select(a => a.Label)) +
        ". File contents were supplied for this answer only and are not retained in saved chats or resent with later questions.";
}

internal static class PngCrc
{
    private static readonly uint[] Table = CreateTable();

    internal static uint Update(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes) crc = (crc >> 8) ^ Table[(byte)(crc ^ value)];
        return crc;
    }

    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; ++i)
        {
            var value = i;
            for (var bit = 0; bit < 8; ++bit)
                value = (value >> 1) ^ ((value & 1) == 0 ? 0 : 0xedb88320u);
            table[i] = value;
        }
        return table;
    }
}
