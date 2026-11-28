using System.Buffers.Binary;
using System.IO.Compression;

namespace Navigator.Core.Imaging;

/// <summary>
/// Lossless quarter turns of PNG images with the base class library: the pixels are decoded (every color type and bit
/// depth, interlaced or not), turned, re-filtered and re-compressed. Metadata chunks are kept (pHYs gets its axes
/// swapped); animated PNGs are refused because their frames would need turning one by one.
/// </summary>
public static class PngRotation
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Refuses pictures whose pixels alone would take more memory than this.</summary>
    private const long MaxPixelBytes = 512L * 1024 * 1024;

    public static bool IsPng(string path) => Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase);

    /// <summary>Turns <paramref name="png"/> a quarter turn. Throws <see cref="InvalidDataException"/> for unsupported or damaged files.</summary>
    public static byte[] Rotate(ReadOnlySpan<byte> png, bool clockwise)
    {
        var (chunks, ihdr, width, height, bitsPerPixel, unitBytes, pixels) = Decode(png);
        var turned = PixelOrientation.Apply(pixels, width, height, unitBytes, clockwise ? ImageOrientation.Rotate90 : ImageOrientation.Rotate270);
        var (newWidth, newHeight) = (height, width);
        var packed = Pack(turned, newWidth, newHeight, bitsPerPixel);
        var stride = Stride(newWidth, bitsPerPixel);
        var filtered = Filter(packed, stride, newHeight, Math.Max(1, bitsPerPixel / 8));

        using var deflated = new MemoryStream(filtered.Length / 2);
        using (var deflater = new ZLibStream(deflated, CompressionLevel.Optimal, leaveOpen: true))
            deflater.Write(filtered);

        using var output = new MemoryStream(png.Length + 1024);
        output.Write(Signature);
        var newHeader = (byte[])ihdr.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(newHeader, (uint)newWidth);
        BinaryPrimitives.WriteUInt32BigEndian(newHeader.AsSpan(4), (uint)newHeight);
        newHeader[12] = 0; // written non-interlaced
        var wroteData = false;
        foreach (var chunk in chunks)
        {
            switch (chunk.Type)
            {
                case "IHDR":
                    WriteChunk(output, "IHDR", newHeader);
                    break;
                case "IDAT":
                    if (!wroteData)
                    {
                        WriteData(output, deflated.GetBuffer().AsSpan(0, (int)deflated.Length));
                        wroteData = true;
                    }

                    break;
                case "pHYs" when chunk.Data.Length == 9:
                {
                    var swapped = (byte[])chunk.Data.Clone();
                    chunk.Data.AsSpan(0, 4).CopyTo(swapped.AsSpan(4));
                    chunk.Data.AsSpan(4, 4).CopyTo(swapped);
                    WriteChunk(output, "pHYs", swapped);
                    break;
                }

                case "oFFs" or "sCAL":
                    break; // position/scale of the unrotated frame: no longer meaningful
                default:
                    WriteChunk(output, chunk.Type, chunk.Data);
                    break;
            }
        }

        return output.ToArray();
    }

    /// <summary>The chunks and the pixels (one unit of <c>unitBytes</c> per pixel, sub-byte samples widened) of a PNG.</summary>
    private static (List<Chunk> Chunks, byte[] Header, int Width, int Height, int BitsPerPixel, int UnitBytes, byte[] Pixels) Decode(ReadOnlySpan<byte> png)
    {
        if (png.Length < Signature.Length + 25 || !png[..Signature.Length].SequenceEqual(Signature))
            throw new InvalidDataException("Not a PNG image.");

        var chunks = ReadChunks(png);
        var header = chunks.Find(c => c.Type == "IHDR") ?? throw new InvalidDataException("The PNG has no header.");
        if (chunks.Exists(c => c.Type is "acTL" or "fcTL" or "fdAT"))
            throw new InvalidDataException("Animated PNG images cannot be rotated.");

        var ihdr = header.Data;
        var width = (int)BinaryPrimitives.ReadUInt32BigEndian(ihdr);
        var height = (int)BinaryPrimitives.ReadUInt32BigEndian(ihdr.AsSpan(4));
        int bitDepth = ihdr[8], colorType = ihdr[9], interlace = ihdr[12];
        var channels = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => throw new InvalidDataException("Unknown PNG color type."),
        };
        if (width <= 0 || height <= 0 || bitDepth is not (1 or 2 or 4 or 8 or 16) || ihdr[10] != 0 || ihdr[11] != 0 || interlace > 1)
            throw new InvalidDataException("Unsupported PNG header.");

        var bitsPerPixel = channels * bitDepth;

        // Pixels are handled as whole bytes: sub-byte samples are widened to one byte per pixel for the turn.
        var unitBytes = Math.Max(1, bitsPerPixel / 8);
        if ((long)width * height * unitBytes > MaxPixelBytes)
            throw new InvalidDataException("The image is too large to rotate.");

        using var compressed = new MemoryStream();
        foreach (var chunk in chunks)
        {
            if (chunk.Type == "IDAT")
                compressed.Write(chunk.Data);
        }

        compressed.Position = 0;
        byte[] raw;
        using (var inflater = new ZLibStream(compressed, CompressionMode.Decompress))
        using (var inflated = new MemoryStream())
        {
            inflater.CopyTo(inflated);
            raw = inflated.ToArray();
        }

        var pixels = interlace == 1
            ? Deinterlace(raw, width, height, bitsPerPixel, unitBytes)
            : Unpack(Unfilter(raw, 0, width, height, bitsPerPixel, out _), width, height, bitsPerPixel);

        return (chunks, ihdr, width, height, bitsPerPixel, unitBytes, pixels);
    }

    /// <summary>Width, height and pixel units of a PNG (tests compare pictures with it).</summary>
    internal static (int Width, int Height, int UnitBytes, byte[] Pixels) ReadPixels(ReadOnlySpan<byte> png)
    {
        var decoded = Decode(png);
        return (decoded.Width, decoded.Height, decoded.UnitBytes, decoded.Pixels);
    }

    /// <summary>Rotates a PNG file, replacing it through a temporary sibling (attributes and permissions kept).</summary>
    public static void RotateFile(string path, bool clockwise)
    {
        var turned = Rotate(File.ReadAllBytes(path), clockwise);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.navigator-partial");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(turned);
                stream.Flush(flushToDisk: true);
            }

            File.Replace(temporary, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private sealed record Chunk(string Type, byte[] Data);

    private static List<Chunk> ReadChunks(ReadOnlySpan<byte> png)
    {
        var chunks = new List<Chunk>();
        var position = Signature.Length;
        while (position + 12 <= png.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(png[position..]);
            if (length > int.MaxValue || position + 12 + (long)length > png.Length)
                throw new InvalidDataException("A PNG chunk is truncated.");
            var type = System.Text.Encoding.ASCII.GetString(png.Slice(position + 4, 4));
            var data = png.Slice(position + 8, (int)length);
            var stored = BinaryPrimitives.ReadUInt32BigEndian(png[(position + 8 + (int)length)..]);
            if (Crc32(png.Slice(position + 4, 4 + (int)length)) != stored)
                throw new InvalidDataException($"The PNG chunk {type} is damaged.");
            chunks.Add(new Chunk(type, data.ToArray()));
            position += 12 + (int)length;
            if (type == "IEND")
                break;
        }

        return chunks;
    }

    private static int Stride(int width, int bitsPerPixel) => (int)(((long)width * bitsPerPixel + 7) / 8);

    /// <summary>Reverses the scanline filters of one (sub-)image starting at <paramref name="offset"/>; returns the packed rows.</summary>
    private static byte[] Unfilter(byte[] raw, int offset, int width, int height, int bitsPerPixel, out int consumed)
    {
        var stride = Stride(width, bitsPerPixel);
        var left = Math.Max(1, bitsPerPixel / 8);
        consumed = (stride + 1) * height;
        if (offset + consumed > raw.Length)
            throw new InvalidDataException("The PNG image data is truncated.");

        var rows = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[offset + (y * (stride + 1))];
            var source = raw.AsSpan(offset + (y * (stride + 1)) + 1, stride);
            var row = rows.AsSpan(y * stride, stride);
            var above = y > 0 ? rows.AsSpan((y - 1) * stride, stride) : default;
            switch (filter)
            {
                case 0:
                    source.CopyTo(row);
                    break;
                case 1:
                    for (var i = 0; i < stride; i++)
                        row[i] = (byte)(source[i] + (i >= left ? row[i - left] : 0));
                    break;
                case 2:
                    for (var i = 0; i < stride; i++)
                        row[i] = (byte)(source[i] + (y > 0 ? above[i] : 0));
                    break;
                case 3:
                    for (var i = 0; i < stride; i++)
                        row[i] = (byte)(source[i] + (((i >= left ? row[i - left] : 0) + (y > 0 ? above[i] : 0)) >> 1));
                    break;
                case 4:
                    for (var i = 0; i < stride; i++)
                    {
                        int a = i >= left ? row[i - left] : 0;
                        int b = y > 0 ? above[i] : 0;
                        int c = y > 0 && i >= left ? above[i - left] : 0;
                        row[i] = (byte)(source[i] + Paeth(a, b, c));
                    }

                    break;
                default:
                    throw new InvalidDataException("Unknown PNG filter.");
            }
        }

        return rows;
    }

    /// <summary>Packed rows → one unit per pixel (samples below 8 bits widened to a byte each).</summary>
    private static byte[] Unpack(byte[] rows, int width, int height, int bitsPerPixel)
    {
        if (bitsPerPixel >= 8)
            return rows;
        var stride = Stride(width, bitsPerPixel);
        var pixels = new byte[width * height];
        var mask = (1 << bitsPerPixel) - 1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var bit = x * bitsPerPixel;
                var shift = 8 - bitsPerPixel - (bit & 7);
                pixels[(y * width) + x] = (byte)((rows[(y * stride) + (bit >> 3)] >> shift) & mask);
            }
        }

        return pixels;
    }

    private static byte[] Pack(byte[] pixels, int width, int height, int bitsPerPixel)
    {
        if (bitsPerPixel >= 8)
            return pixels;
        var stride = Stride(width, bitsPerPixel);
        var rows = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var bit = x * bitsPerPixel;
                var shift = 8 - bitsPerPixel - (bit & 7);
                rows[(y * stride) + (bit >> 3)] |= (byte)(pixels[(y * width) + x] << shift);
            }
        }

        return rows;
    }

    /// <summary>Adam7: the seven passes are decoded and their pixels placed into one full-size unit buffer.</summary>
    private static byte[] Deinterlace(byte[] raw, int width, int height, int bitsPerPixel, int unitBytes)
    {
        ReadOnlySpan<int> startX = [0, 4, 0, 2, 0, 1, 0];
        ReadOnlySpan<int> startY = [0, 0, 4, 0, 2, 0, 1];
        ReadOnlySpan<int> stepX = [8, 8, 4, 4, 2, 2, 1];
        ReadOnlySpan<int> stepY = [8, 8, 8, 4, 4, 2, 2];
        var pixels = new byte[width * height * unitBytes];
        var offset = 0;
        for (var pass = 0; pass < 7; pass++)
        {
            var passWidth = (width - startX[pass] + stepX[pass] - 1) / stepX[pass];
            var passHeight = (height - startY[pass] + stepY[pass] - 1) / stepY[pass];
            if (passWidth <= 0 || passHeight <= 0)
                continue;
            var rows = Unfilter(raw, offset, passWidth, passHeight, bitsPerPixel, out var consumed);
            offset += consumed;
            var units = Unpack(rows, passWidth, passHeight, bitsPerPixel);
            for (var y = 0; y < passHeight; y++)
            {
                for (var x = 0; x < passWidth; x++)
                {
                    var target = (((startY[pass] + (y * stepY[pass])) * width) + startX[pass] + (x * stepX[pass])) * unitBytes;
                    units.AsSpan(((y * passWidth) + x) * unitBytes, unitBytes).CopyTo(pixels.AsSpan(target));
                }
            }
        }

        return pixels;
    }

    /// <summary>Per row the filter with the smallest sum of absolute values (the usual encoder heuristic).</summary>
    private static byte[] Filter(byte[] rows, int stride, int height, int left)
    {
        var output = new byte[(stride + 1) * height];
        var candidate = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var row = rows.AsSpan(y * stride, stride);
            var above = y > 0 ? rows.AsSpan((y - 1) * stride, stride) : default;
            var best = output.AsSpan((y * (stride + 1)) + 1, stride);
            long bestScore = long.MaxValue;
            for (var filter = 0; filter <= 4; filter++)
            {
                long score = 0;
                switch (filter)
                {
                    case 0:
                        for (var i = 0; i < stride; i++)
                            score += Cost(candidate[i] = row[i]);
                        break;
                    case 1:
                        for (var i = 0; i < stride; i++)
                            score += Cost(candidate[i] = (byte)(row[i] - (i >= left ? row[i - left] : 0)));
                        break;
                    case 2:
                        if (y == 0)
                            continue; // same as "none" on the first row
                        for (var i = 0; i < stride; i++)
                            score += Cost(candidate[i] = (byte)(row[i] - above[i]));
                        break;
                    case 3:
                        for (var i = 0; i < stride; i++)
                            score += Cost(candidate[i] = (byte)(row[i] - (((i >= left ? row[i - left] : 0) + (y > 0 ? above[i] : 0)) >> 1)));
                        break;
                    default:
                        for (var i = 0; i < stride; i++)
                        {
                            int a = i >= left ? row[i - left] : 0;
                            int b = y > 0 ? above[i] : 0;
                            int c = y > 0 && i >= left ? above[i - left] : 0;
                            score += Cost(candidate[i] = (byte)(row[i] - Paeth(a, b, c)));
                        }

                        break;
                }

                if (score < bestScore)
                {
                    bestScore = score;
                    output[y * (stride + 1)] = (byte)filter;
                    candidate.CopyTo(best);
                }
            }
        }

        return output;
    }

    private static int Cost(byte value) => value < 128 ? value : 256 - value;

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>Large IDAT payloads are split into 256 KB chunks, as encoders commonly do.</summary>
    private static void WriteData(Stream output, ReadOnlySpan<byte> data)
    {
        const int Piece = 256 * 1024;
        do
        {
            var take = Math.Min(Piece, data.Length);
            WriteChunk(output, "IDAT", data[..take]);
            data = data[take..];
        }
        while (data.Length > 0);
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length);
        output.Write(number);
        var typed = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type, typed);
        data.CopyTo(typed.AsSpan(4));
        output.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(typed));
        output.Write(number);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }

    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
