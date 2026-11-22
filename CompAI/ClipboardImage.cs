using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading;

namespace CompAI;

// Called only by an explicit paste gesture. No clipboard listener, timer, GDI bitmap
// ownership or general image decoder is needed for PNG and standard screenshot DIBs.
internal static class ClipboardImage
{
    private const uint Dib = 8;
    private const uint DibV5 = 17;
    private const int MaxDibBytes = 32_000_000 * 4 + 124;
    private const string Unsupported = "This clipboard image format is unsupported. Copy a screenshot or attach a PNG/JPEG file.";
    private const string Changed = "The clipboard changed before the image was read. Paste the image again.";
    private const string TooLarge = "The pasted image exceeds 2 MiB as a PNG. Crop it or attach a smaller image.";

    internal readonly record struct Request(uint Sequence, uint Format, bool IsPng);
    private readonly record struct DibLayout(int Width, int Height, int Stride, int PixelOffset,
        int EndOffset, int BytesPerPixel, bool TopDown, bool Alpha);

    // Format availability and sequence are cheap; pixel copying and encoding run on a worker.
    // Reading only image formats also leaves ImGui's ordinary text paste untouched.
    internal static Request? CaptureRequest()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var sequence = GetClipboardSequenceNumber();
        var png = RegisterClipboardFormat("PNG");
        var format = png != 0 && IsClipboardFormatAvailable(png) ? png :
            IsClipboardFormatAvailable(DibV5) ? DibV5 : IsClipboardFormatAvailable(Dib) ? Dib : 0;
        if (format == 0) return null;
        if (sequence == 0 || sequence != GetClipboardSequenceNumber()) throw new InvalidDataException(Changed);
        return new Request(sequence, format, format == png);
    }

    internal static ChatAttachment Read(Request request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OpenClipboard(IntPtr.Zero)) throw new IOException("The clipboard is busy. Paste the image again.");
        byte[] bytes;
        try
        {
            if (request.Sequence != GetClipboardSequenceNumber()) throw new InvalidDataException(Changed);
            var handle = GetClipboardData(request.Format);
            if (handle == IntPtr.Zero) throw new InvalidDataException(Unsupported);
            var length = GlobalSize(handle).ToUInt64();
            var maximum = request.IsPng ? ChatAttachment.MaxImageBytes : MaxDibBytes;
            if (length == 0 || length > (ulong)maximum)
                throw new InvalidDataException(request.IsPng ? TooLarge : "The clipboard image exceeds the attachment dimensions or size limit.");
            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero) throw new IOException("The clipboard image could not be read. Paste it again.");
            try
            {
                var count = checked((int)length);
                if (!request.IsPng)
                {
                    // Validate dimensions, offsets and stride before allocating the pixel buffer.
                    var header = new byte[Math.Min(count, 124)];
                    Marshal.Copy(pointer, header, 0, header.Length);
                    count = ParseDib(header, count).EndOffset;
                }
                cancellationToken.ThrowIfCancellationRequested();
                bytes = new byte[count];
                Marshal.Copy(pointer, bytes, 0, count);
            }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
        cancellationToken.ThrowIfCancellationRequested();
        return request.IsPng ? ChatAttachment.FromClipboardPng(bytes) : FromDib(bytes, cancellationToken);
    }

    internal static ChatAttachment FromDib(byte[] bytes, CancellationToken cancellationToken = default)
    {
        var layout = ParseDib(bytes, bytes.Length);
        // Filter one bounded row at a time; stop compressed output at the existing 2 MiB limit.
        using var compressed = new BoundedPngStream(ChatAttachment.MaxImageBytes - 57);
        var channels = layout.Alpha ? 4 : 3;
        var row = new byte[layout.Width * channels + 1];
        row[0] = 1; // PNG Sub filter, improves screenshots without buffering another image.
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (var y = 0; y < layout.Height; ++y)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceY = layout.TopDown ? y : layout.Height - y - 1;
                var source = layout.PixelOffset + sourceY * layout.Stride;
                byte priorRed = 0, priorGreen = 0, priorBlue = 0, priorAlpha = 0;
                for (var x = 0; x < layout.Width; ++x)
                {
                    var pixel = source + x * layout.BytesPerPixel;
                    var target = 1 + x * channels;
                    var red = bytes[pixel + 2]; var green = bytes[pixel + 1]; var blue = bytes[pixel];
                    row[target] = unchecked((byte)(red - priorRed));
                    row[target + 1] = unchecked((byte)(green - priorGreen));
                    row[target + 2] = unchecked((byte)(blue - priorBlue));
                    priorRed = red; priorGreen = green; priorBlue = blue;
                    if (layout.Alpha)
                    {
                        var alpha = bytes[pixel + 3];
                        row[target + 3] = unchecked((byte)(alpha - priorAlpha)); priorAlpha = alpha;
                    }
                }
                deflate.Write(row);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        using var png = new MemoryStream(checked((int)compressed.Length + 57));
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        Span<byte> header = stackalloc byte[13];
        header.Clear();
        BinaryPrimitives.WriteInt32BigEndian(header, layout.Width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], layout.Height);
        header[8] = 8; header[9] = layout.Alpha ? (byte)6 : (byte)2;
        WriteChunk(png, "IHDR"u8, header);
        WriteChunk(png, "IDAT"u8, compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length)));
        WriteChunk(png, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return ChatAttachment.FromClipboardPng(png.ToArray());
    }

    private static DibLayout ParseDib(ReadOnlySpan<byte> bytes, int totalLength)
    {
        if (bytes.Length < 40 || totalLength > MaxDibBytes) throw new InvalidDataException(Unsupported);
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (headerSize is not (40 or 108 or 124) || headerSize > bytes.Length) throw new InvalidDataException(Unsupported);
        var width = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
        var signedHeight = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        if (width is <= 0 or > 8192 || signedHeight is < -8192 or 0 or > 8192 ||
            (long)width * Math.Abs(signedHeight) > 32_000_000)
            throw new InvalidDataException("The clipboard image dimensions exceed 8192 pixels per side or 32 million pixels.");
        var height = Math.Abs(signedHeight);
        var planes = BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]);
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]);
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
        var colors = BinaryPrimitives.ReadUInt32LittleEndian(bytes[32..]);
        if (planes != 1 || bits is not (24 or 32) || compression is not (0 or 3) ||
            (compression == 3 && bits != 32) || colors != 0)
            throw new InvalidDataException(Unsupported);
        // Embedded/linked color profiles need color management; don't misread their offsets.
        if (headerSize == 124 && (BinaryPrimitives.ReadUInt32LittleEndian(bytes[112..]) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[116..]) != 0)) throw new InvalidDataException(Unsupported);
        var pixelOffset = (int)headerSize;
        var alpha = false;
        if (compression == 3)
        {
            if (bytes.Length < 52 || BinaryPrimitives.ReadUInt32LittleEndian(bytes[40..]) != 0x00ff0000 ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[44..]) != 0x0000ff00 ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[48..]) != 0x000000ff) throw new InvalidDataException(Unsupported);
            if (headerSize == 40) pixelOffset += 12;
            else
            {
                var alphaMask = BinaryPrimitives.ReadUInt32LittleEndian(bytes[52..]);
                if (alphaMask is not (0 or 0xff000000)) throw new InvalidDataException(Unsupported);
                alpha = alphaMask != 0;
            }
        }
        var stride = checked(((width * bits + 31) / 32) * 4);
        var end = checked(pixelOffset + stride * height);
        if (end > totalLength) throw new InvalidDataException("The clipboard image is incomplete. Copy and paste it again.");
        return new(width, height, stride, pixelOffset, end, bits / 8, signedHeight < 0, alpha);
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> field = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(field, data.Length); stream.Write(field);
        stream.Write(type); stream.Write(data);
        var crc = PngCrc.Update(PngCrc.Update(0xffffffff, type), data);
        BinaryPrimitives.WriteUInt32BigEndian(field, crc ^ 0xffffffff); stream.Write(field);
    }

    private sealed class BoundedPngStream(int maximum) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        { if (Length + count > maximum) throw new InvalidDataException(TooLarge); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer)
        { if (Length + buffer.Length > maximum) throw new InvalidDataException(TooLarge); base.Write(buffer); }
    }

    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClipboardFormatW")]
    private static extern uint RegisterClipboardFormat(string format);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenClipboard(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(IntPtr handle);
}
