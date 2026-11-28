using System.Buffers.Binary;

namespace Navigator.Core.Imaging;

/// <summary>
/// Reads and changes the EXIF orientation of JPEG files without touching the compressed picture, so rotating a photo
/// is lossless and costs a few header bytes. When the tag exists (every camera and phone writes it) its two bytes are
/// patched in place; otherwise the EXIF block is extended or created and the file is rewritten once through a
/// temporary sibling. An XMP <c>tiff:Orientation</c>, when present, is kept in step.
/// </summary>
public static class JpegOrientation
{
    private const ushort OrientationTag = 0x0112;
    private const ushort ShortType = 3;

    /// <summary>JPEG headers worth searching; EXIF must sit in the first 64 KB segment(s) after SOI.</summary>
    private const int HeaderWindow = 256 * 1024;

    private static readonly byte[] ExifSignature = "Exif\0\0"u8.ToArray();
    private static readonly byte[] XmpSignature = "http://ns.adobe.com/xap/1.0/\0"u8.ToArray();

    public static bool IsJpeg(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".jpe" or ".jfif";

    /// <summary>The stored orientation, <see cref="ImageOrientation.Normal"/> when absent or unreadable. Reads the header only.</summary>
    public static ImageOrientation Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16 * 1024);
            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ImageOrientation.Normal;
        }
    }

    /// <summary>
    /// The orientation stored in a JPEG stream (read from its current position, which is left changed). Walks the
    /// segment headers and reads only the EXIF block (at most 64 KB, pooled), so it is cheap enough for every thumbnail.
    /// </summary>
    public static ImageOrientation Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> marker = stackalloc byte[4];
        if (stream.ReadAtLeast(marker[..2], 2, throwOnEndOfStream: false) < 2 || marker[0] != 0xFF || marker[1] != 0xD8)
            return ImageOrientation.Normal;

        for (var segments = 0; segments < 64; segments++)
        {
            if (stream.ReadAtLeast(marker, 4, throwOnEndOfStream: false) < 4 || marker[0] != 0xFF)
                return ImageOrientation.Normal;
            var type = marker[1];
            if (type is 0xDA or 0xD9)
                return ImageOrientation.Normal; // image data starts: no EXIF
            var length = BinaryPrimitives.ReadUInt16BigEndian(marker[2..]) - 2;
            if (length < 0)
                return ImageOrientation.Normal;

            if (type != 0xE1 || length < ExifSignature.Length + 8)
            {
                if (stream.CanSeek)
                    stream.Seek(length, SeekOrigin.Current);
                else
                    Skip(stream, length);
                continue;
            }

            var body = System.Buffers.ArrayPool<byte>.Shared.Rent(length);
            try
            {
                if (stream.ReadAtLeast(body.AsSpan(0, length), length, throwOnEndOfStream: false) < length)
                    return ImageOrientation.Normal;
                var segment = body.AsSpan(0, length);
                if (!segment.StartsWith(ExifSignature))
                    continue; // XMP or another APP1: keep looking
                var tiff = segment[ExifSignature.Length..];
                var at = FindOrientation(tiff, out var bigEndian);
                if (at < 0)
                    return ImageOrientation.Normal;
                var value = U16(tiff, at, bigEndian);
                return PixelOrientation.IsValid(value) ? (ImageOrientation)value : ImageOrientation.Normal;
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(body);
            }
        }

        return ImageOrientation.Normal;
    }

    private static void Skip(Stream stream, int count)
    {
        Span<byte> sink = stackalloc byte[512];
        while (count > 0)
        {
            var read = stream.Read(sink[..Math.Min(sink.Length, count)]);
            if (read <= 0)
                return;
            count -= read;
        }
    }

    /// <summary>Turns the picture a quarter turn by changing its orientation tag. Throws <see cref="InvalidDataException"/> for a non-JPEG file.</summary>
    public static void Rotate(string path, bool clockwise)
    {
        byte[] header;
        Layout layout;
        using (var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024))
        {
            header = ReadHeader(probe);
            layout = Locate(header);
        }

        if (!layout.IsJpeg)
            throw new InvalidDataException($"'{Path.GetFileName(path)}' is not a JPEG image.");

        var current = ImageOrientation.Normal;
        if (layout.OrientationOffset >= 0)
        {
            var stored = layout.BigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(layout.OrientationOffset))
                : BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(layout.OrientationOffset));
            if (PixelOrientation.IsValid(stored))
                current = (ImageOrientation)stored;
        }

        var next = PixelOrientation.Turn(current, clockwise);
        if (layout.OrientationOffset >= 0)
        {
            // The common case: two bytes (plus one XMP digit) rewritten in place, whatever the file size.
            using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 1);
            Span<byte> value = stackalloc byte[2];
            if (layout.BigEndian)
                BinaryPrimitives.WriteUInt16BigEndian(value, (ushort)next);
            else
                BinaryPrimitives.WriteUInt16LittleEndian(value, (ushort)next);
            file.Position = layout.OrientationOffset;
            file.Write(value);
            if (layout.XmpDigitOffset >= 0)
            {
                file.Position = layout.XmpDigitOffset;
                file.WriteByte((byte)('0' + (int)next));
            }

            file.Flush(flushToDisk: true);
            return;
        }

        RewriteWithOrientation(path, header, layout, next);
    }

    /// <summary>Returns the JPEG bytes with the orientation set; used when the tag has to be added (tests use it directly).</summary>
    internal static byte[] WithOrientation(byte[] jpeg, ImageOrientation orientation)
    {
        var layout = Locate(jpeg);
        if (!layout.IsJpeg)
            throw new InvalidDataException("Not a JPEG image.");
        using var output = new MemoryStream(jpeg.Length + 64);
        using (var input = new MemoryStream(jpeg, writable: false))
            Compose(input, output, jpeg, layout, orientation);
        return output.ToArray();
    }

    private static void RewriteWithOrientation(string path, byte[] header, Layout layout, ImageOrientation orientation)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.navigator-partial");
        try
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920))
            {
                Compose(input, output, header, layout, orientation);
                output.Flush(flushToDisk: true);
            }

            // File.Replace keeps the original's attributes and (on Windows) its security descriptor.
            File.Replace(temporary, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    /// <summary>
    /// Writes the image with an orientation entry added to (or a new EXIF block before) the header. The header bytes come
    /// from <paramref name="header"/> (with an XMP orientation digit updated), everything after it from <paramref name="input"/>.
    /// </summary>
    private static void Compose(Stream input, Stream output, byte[] header, Layout layout, ImageOrientation orientation)
    {
        var source = header;
        if (layout.XmpDigitOffset >= 0)
        {
            source = (byte[])header.Clone();
            source[layout.XmpDigitOffset] = (byte)('0' + (int)orientation);
        }

        void CopyFrom(int offset)
        {
            if (offset < source.Length)
                output.Write(source, offset, source.Length - offset);
            input.Position = Math.Max(offset, source.Length);
            input.CopyTo(output);
        }

        if (layout.ExifSegmentOffset >= 0)
        {
            // Extend the existing EXIF: a copy of IFD0 with the orientation entry is appended to the TIFF data and the
            // header points at it. Nothing else moves, so every offset inside the block stays valid.
            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(source.AsSpan(layout.ExifSegmentOffset + 2));
            var tiffStart = layout.ExifSegmentOffset + 4 + ExifSignature.Length;
            var tiffLength = layout.ExifSegmentOffset + 2 + segmentLength - tiffStart;
            var extended = AppendIfd0WithOrientation(source.AsSpan(tiffStart, tiffLength), layout.BigEndian, orientation)
                ?? throw new InvalidDataException("The EXIF block cannot take an orientation entry.");

            output.Write(source, 0, layout.ExifSegmentOffset);
            WriteApp1(output, extended);
            CopyFrom(layout.ExifSegmentOffset + 2 + segmentLength);
            return;
        }

        // No EXIF at all: a minimal one goes after SOI and a leading JFIF APP0 (readers expect APP0 first).
        output.Write(source, 0, layout.AfterApp0);
        WriteApp1(output, MinimalTiff(orientation));
        CopyFrom(layout.AfterApp0);
    }

    private static void WriteApp1(Stream output, ReadOnlySpan<byte> tiff)
    {
        var length = 2 + ExifSignature.Length + tiff.Length;
        if (length > ushort.MaxValue)
            throw new InvalidDataException("The EXIF block would exceed the JPEG segment limit.");
        Span<byte> marker = [0xFF, 0xE1, (byte)(length >> 8), (byte)length];
        output.Write(marker);
        output.Write(ExifSignature);
        output.Write(tiff);
    }

    private static byte[] MinimalTiff(ImageOrientation orientation)
    {
        // Big-endian TIFF header, IFD0 at 8 holding one SHORT entry, no next IFD.
        var tiff = new byte[8 + 2 + 12 + 4];
        "MM\0*"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(10), OrientationTag);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(12), ShortType);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(18), (ushort)orientation);
        return tiff;
    }

    private static byte[]? AppendIfd0WithOrientation(ReadOnlySpan<byte> tiff, bool bigEndian, ImageOrientation orientation)
    {
        if (tiff.Length < 8)
            return null;
        var ifd0 = (int)U32(tiff, 4, bigEndian);
        if (ifd0 < 8 || ifd0 + 2 > tiff.Length)
            return null;
        var count = U16(tiff, ifd0, bigEndian);
        var entriesEnd = ifd0 + 2 + (count * 12);
        if (entriesEnd + 4 > tiff.Length)
            return null;

        // Word-aligned new IFD at the end of the TIFF data.
        var start = (tiff.Length + 1) & ~1;
        var result = new byte[start + 2 + ((count + 1) * 12) + 4];
        tiff.CopyTo(result);
        W16(result, start, (ushort)(count + 1), bigEndian);
        var write = start + 2;
        var inserted = false;
        for (var i = 0; i < count; i++)
        {
            var entry = tiff.Slice(ifd0 + 2 + (i * 12), 12);
            if (!inserted && U16(entry, 0, bigEndian) > OrientationTag)
            {
                WriteOrientationEntry(result, write, bigEndian, orientation);
                write += 12;
                inserted = true;
            }

            entry.CopyTo(result.AsSpan(write));
            write += 12;
        }

        if (!inserted)
        {
            WriteOrientationEntry(result, write, bigEndian, orientation);
            write += 12;
        }

        tiff.Slice(entriesEnd, 4).CopyTo(result.AsSpan(write)); // next IFD (IFD1, the embedded thumbnail)
        W32(result, 4, (uint)start, bigEndian);
        return result;
    }

    private static void WriteOrientationEntry(byte[] target, int at, bool bigEndian, ImageOrientation orientation)
    {
        W16(target, at, OrientationTag, bigEndian);
        W16(target, at + 2, ShortType, bigEndian);
        W32(target, at + 4, 1, bigEndian);
        W16(target, at + 8, (ushort)orientation, bigEndian);
        W16(target, at + 10, 0, bigEndian);
    }

    private static byte[] ReadHeader(Stream stream)
    {
        var buffer = new byte[HeaderWindow];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
            total += read;
        return total == buffer.Length ? buffer : buffer[..total];
    }

    /// <summary>Where the parts that matter sit in the header (offsets from the start of the file, -1 when absent).</summary>
    private readonly record struct Layout(bool IsJpeg, int AfterApp0, int ExifSegmentOffset, bool BigEndian, int OrientationOffset, int XmpDigitOffset);

    private static Layout Locate(byte[] header)
    {
        if (header.Length < 4 || header[0] != 0xFF || header[1] != 0xD8)
            return new Layout(false, 0, -1, false, -1, -1);

        int afterApp0 = 2, exif = -1, orientation = -1, xmpDigit = -1;
        var bigEndian = false;
        var position = 2;
        var first = true;
        while (position + 4 <= header.Length && header[position] == 0xFF)
        {
            var marker = header[position + 1];
            if (marker == 0xFF)
            {
                position++; // fill byte
                continue;
            }

            if (marker is 0xDA or 0xD9)
                break; // start of scan / end of image: no metadata after this point
            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(position + 2));
            if (length < 2 || position + 2 + length > header.Length)
                break;
            var body = header.AsSpan(position + 4, length - 2);

            if (marker == 0xE0 && first)
                afterApp0 = position + 2 + length;
            else if (marker == 0xE1 && exif < 0 && body.StartsWith(ExifSignature))
            {
                exif = position;
                var tiffStart = position + 4 + ExifSignature.Length;
                orientation = FindOrientation(header.AsSpan(tiffStart, body.Length - ExifSignature.Length), out bigEndian);
                if (orientation >= 0)
                    orientation += tiffStart;
            }
            else if (marker == 0xE1 && xmpDigit < 0 && body.StartsWith(XmpSignature))
            {
                var digit = FindXmpOrientationDigit(body[XmpSignature.Length..]);
                if (digit >= 0)
                    xmpDigit = position + 4 + XmpSignature.Length + digit;
            }

            first = false;
            position += 2 + length;
        }

        return new Layout(true, afterApp0, exif, bigEndian, orientation, xmpDigit);
    }

    /// <summary>Offset (within the TIFF data) of the orientation value in IFD0, or -1.</summary>
    private static int FindOrientation(ReadOnlySpan<byte> tiff, out bool bigEndian)
    {
        bigEndian = false;
        if (tiff.Length < 8)
            return -1;
        if (tiff[0] == 'M' && tiff[1] == 'M')
            bigEndian = true;
        else if (tiff[0] != 'I' || tiff[1] != 'I')
            return -1;
        if (U16(tiff, 2, bigEndian) != 42)
            return -1;

        var ifd0 = U32(tiff, 4, bigEndian);
        if (ifd0 < 8 || ifd0 + 2 > (uint)tiff.Length)
            return -1;
        var count = U16(tiff, (int)ifd0, bigEndian);
        for (var i = 0; i < count; i++)
        {
            var entry = (int)ifd0 + 2 + (i * 12);
            if (entry + 12 > tiff.Length)
                return -1;
            if (U16(tiff, entry, bigEndian) == OrientationTag)
                return U16(tiff, entry + 2, bigEndian) == ShortType ? entry + 8 : -1;
        }

        return -1;
    }

    /// <summary>Offset of the single digit in <c>tiff:Orientation="6"</c> or <c>&lt;tiff:Orientation&gt;6&lt;</c>, or -1.</summary>
    private static int FindXmpOrientationDigit(ReadOnlySpan<byte> xmp)
    {
        var name = "tiff:Orientation"u8;
        var at = xmp.IndexOf(name);
        while (at >= 0)
        {
            var after = at + name.Length;
            if (after + 3 <= xmp.Length && (xmp[after] == '>' || (xmp[after] == '=' && xmp[after + 1] is (byte)'"' or (byte)'\'')))
            {
                var digit = xmp[after] == '>' ? after + 1 : after + 2;
                if (digit + 1 < xmp.Length && xmp[digit] is >= (byte)'1' and <= (byte)'8' && xmp[digit + 1] is (byte)'"' or (byte)'\'' or (byte)'<')
                    return digit;
            }

            var next = xmp[after..].IndexOf(name);
            at = next < 0 ? -1 : after + next;
        }

        return -1;
    }

    private static ushort U16(ReadOnlySpan<byte> data, int at, bool bigEndian)
        => bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(data[at..]) : BinaryPrimitives.ReadUInt16LittleEndian(data[at..]);

    private static uint U32(ReadOnlySpan<byte> data, int at, bool bigEndian)
        => bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data[at..]) : BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);

    private static void W16(Span<byte> data, int at, ushort value, bool bigEndian)
    {
        if (bigEndian)
            BinaryPrimitives.WriteUInt16BigEndian(data[at..], value);
        else
            BinaryPrimitives.WriteUInt16LittleEndian(data[at..], value);
    }

    private static void W32(Span<byte> data, int at, uint value, bool bigEndian)
    {
        if (bigEndian)
            BinaryPrimitives.WriteUInt32BigEndian(data[at..], value);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(data[at..], value);
    }
}
