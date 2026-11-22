using System.Buffers.Binary;
using System.IO.Compression;
using CompAI;

internal static class ClipboardImageChecks
{
    internal static int Run()
    {
        var checks = 0;
        void Check(bool condition, string message)
        { if (!condition) throw new Exception(message); checks++; }
        void Reject(Action action, string message)
        {
            try { action(); throw new Exception("Expected rejection: " + message); }
            catch (InvalidDataException) { checks++; }
        }

        // No native clipboard APIs are called: every image below is synthetic memory.
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC");
        var pasted = ChatAttachment.FromClipboardPng(png);
        Check(pasted.IsImage && pasted.Name == "Pasted image.png" && pasted.ByteCount == png.Length, "PNG paste metadata");
        png[40] ^= 1;
        Check(Convert.FromBase64String(pasted.Base64)[40] != png[40], "PNG paste freezes bytes");
        Reject(() => ChatAttachment.FromClipboardPng(png), "PNG checksum");
        Reject(() => ChatAttachment.FromClipboardPng(new byte[ChatAttachment.MaxImageBytes + 1]), "PNG byte budget");

        foreach (var bits in new[] { 24, 32 })
        foreach (var topDown in new[] { false, true })
        {
            var dib = MakeDib(1, topDown ? -2 : 2, bits);
            var stride = bits / 8;
            stride = (stride + 3) & ~3;
            // Logical top red, bottom blue. A 24-bit row has a padding byte.
            var red = 40 + (topDown ? 0 : stride);
            var blue = 40 + (topDown ? stride : 0);
            dib[red + 2] = 255; dib[blue] = 255;
            if (bits == 32) { dib[red + 3] = 0; dib[blue + 3] = 17; }
            var result = ClipboardImage.FromDib(dib);
            var decoded = Pixels(result);
            Check(decoded.SequenceEqual(new byte[] { 255, 0, 0, 0, 0, 255 }), $"{bits}-bit rows and opaque RGB, topDown={topDown}");
            Check(result.ByteCount <= ChatAttachment.MaxImageBytes, "DIB output budget");
        }

        foreach (var headerSize in new[] { 40, 108, 124 })
        {
            var dib = MakeDib(2, -1, 32, headerSize, bitfields: true);
            var offset = headerSize == 40 ? 52 : headerSize;
            dib[offset] = 3; dib[offset + 1] = 2; dib[offset + 2] = 1; dib[offset + 3] = 127;
            dib[offset + 4] = 30; dib[offset + 5] = 20; dib[offset + 6] = 10; dib[offset + 7] = 255;
            if (headerSize != 40) BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(52), 0xff000000);
            var expected = headerSize == 40 ? new byte[] { 1, 2, 3, 10, 20, 30 } : new byte[] { 1, 2, 3, 127, 10, 20, 30, 255 };
            Check(Pixels(ClipboardImage.FromDib(dib)).SequenceEqual(expected), $"BITFIELDS masks/alpha with header {headerSize}");
        }

        var invalid = MakeDib(1, 1, 24);
        Reject(() => ClipboardImage.FromDib(invalid[..^1]), "truncated pixels");
        foreach (var (offset, value) in new[] { (0, 12), (4, 8193), (8, int.MinValue), (8, 0), (16, 1), (32, 1) })
        {
            var changed = (byte[])invalid.Clone(); BinaryPrimitives.WriteInt32LittleEndian(changed.AsSpan(offset), value);
            Reject(() => ClipboardImage.FromDib(changed), $"invalid header at {offset}");
        }
        var oversized = (byte[])invalid.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(oversized.AsSpan(4), 8192);
        BinaryPrimitives.WriteInt32LittleEndian(oversized.AsSpan(8), 8192);
        Reject(() => ClipboardImage.FromDib(oversized), "pixel budget before allocation");
        var masks = MakeDib(1, 1, 32, 124, bitfields: true);
        BinaryPrimitives.WriteUInt32LittleEndian(masks.AsSpan(40), 0x000000ff);
        Reject(() => ClipboardImage.FromDib(masks), "unsupported RGB masks");
        var profile = MakeDib(1, 1, 32, 124);
        BinaryPrimitives.WriteInt32LittleEndian(profile.AsSpan(112), 124);
        BinaryPrimitives.WriteInt32LittleEndian(profile.AsSpan(116), 100);
        Reject(() => ClipboardImage.FromDib(profile), "embedded profile offsets");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            try { ClipboardImage.FromDib(invalid, canceled.Token); throw new Exception("Expected canceled encoding"); }
            catch (OperationCanceledException) { checks++; }
        }
        var noise = MakeDib(1024, 768, 24);
        new Random(12345).NextBytes(noise.AsSpan(40));
        Reject(() => ClipboardImage.FromDib(noise), "compressed output stops at image byte budget");
        return checks;
    }

    private static byte[] MakeDib(int width, int signedHeight, int bits, int headerSize = 40, bool bitfields = false)
    {
        var offset = headerSize + (headerSize == 40 && bitfields ? 12 : 0);
        var stride = ((width * bits + 31) / 32) * 4;
        var result = new byte[offset + stride * Math.Abs(signedHeight)];
        BinaryPrimitives.WriteInt32LittleEndian(result, headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), signedHeight);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(14), (ushort)bits);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(16), bitfields ? 3 : 0);
        if (bitfields)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(40), 0x00ff0000);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(44), 0x0000ff00);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(48), 0x000000ff);
        }
        return result;
    }

    // Decode the output's PNG rows independently of the DIB parser to check colors,
    // orientation, row padding and alpha, not just successful construction.
    private static byte[] Pixels(ChatAttachment attachment)
    {
        var png = Convert.FromBase64String(attachment.Base64);
        var width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16));
        var height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20));
        var channels = png[25] == 6 ? 4 : 3;
        var compressedLength = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(33));
        using var compressed = new MemoryStream(png, 41, compressedLength);
        using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
        var row = new byte[1 + width * channels];
        var pixels = new byte[width * height * channels];
        for (var y = 0; y < height; y++)
        {
            inflater.ReadExactly(row);
            if (row[0] != 1) throw new Exception("Unexpected PNG filter");
            for (var x = 1; x < row.Length; x++)
                row[x] = unchecked((byte)(row[x] + (x > channels ? row[x - channels] : 0)));
            row.AsSpan(1).CopyTo(pixels.AsSpan(y * width * channels));
        }
        if (inflater.ReadByte() != -1) throw new Exception("Unexpected trailing pixel bytes");
        return pixels;
    }
}
