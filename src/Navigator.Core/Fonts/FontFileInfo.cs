using System.Buffers.Binary;
using System.Text;

namespace Navigator.Core.Fonts;

/// <summary>What font installation needs to know about a font file: whether it is one, and the names it carries.</summary>
public sealed record FontFileInfo(string FullName, bool IsOpenTypeCff, int FaceCount)
{
    /// <summary>Installable font files (TrueType, OpenType and their collections). Decided from the name only.</summary>
    public static bool IsInstallable(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".ttf" or ".otf" or ".ttc" or ".otc";

    /// <summary>
    /// Reads the font's full name from its <c>name</c> table (Windows/Unicode records preferred, the first face of a
    /// collection; every face's name joined with " &amp; " for collections, as the Windows font registry lists them).
    /// Reads only the table directory and the name table. Null when the file is not a font.
    /// </summary>
    public static FontFileInfo? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }

    public static FontFileInfo? Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> head = stackalloc byte[12];
        if (!ReadAt(stream, 0, head))
            return null;

        var tag = BinaryPrimitives.ReadUInt32BigEndian(head);
        if (tag == 0x74746366) // "ttcf"
        {
            var count = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(head[8..]), 64);
            var offsets = new byte[count * 4];
            if (count == 0 || !ReadAt(stream, 12, offsets))
                return null;
            var names = new List<string>(count);
            var cff = false;
            for (var i = 0; i < count; i++)
            {
                if (ReadFace(stream, BinaryPrimitives.ReadUInt32BigEndian(offsets.AsSpan(i * 4))) is { } face)
                {
                    cff |= face.Cff;
                    if (!names.Contains(face.Name, StringComparer.Ordinal))
                        names.Add(face.Name);
                }
            }

            return names.Count == 0 ? null : new FontFileInfo(string.Join(" & ", names), cff, count);
        }

        return ReadFace(stream, 0) is { } single ? new FontFileInfo(single.Name, single.Cff, 1) : null;
    }

    private static (string Name, bool Cff)? ReadFace(Stream stream, long offset)
    {
        Span<byte> header = stackalloc byte[12];
        if (!ReadAt(stream, offset, header))
            return null;
        var version = BinaryPrimitives.ReadUInt32BigEndian(header);
        var cff = version == 0x4F54544F; // "OTTO"
        if (version != 0x00010000 && !cff && version != 0x74727565) // 1.0, OTTO, "true" (old Apple)
            return null;

        var tables = BinaryPrimitives.ReadUInt16BigEndian(header[4..]);
        var directory = new byte[tables * 16];
        if (!ReadAt(stream, offset + 12, directory))
            return null;
        for (var i = 0; i < tables; i++)
        {
            var record = directory.AsSpan(i * 16, 16);
            if (BinaryPrimitives.ReadUInt32BigEndian(record) != 0x6E616D65) // "name"
                continue;
            var at = BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
            var length = BinaryPrimitives.ReadUInt32BigEndian(record[12..]);
            if (length is < 6 or > 4 * 1024 * 1024)
                return null;
            var table = new byte[length];
            return ReadAt(stream, at, table) && ParseName(table) is { } name ? (name, cff) : null;
        }

        return null;
    }

    /// <summary>Full name (id 4), else family (1) plus subfamily (2). Windows Unicode English first, then any Unicode, then Mac Roman.</summary>
    internal static string? ParseName(ReadOnlySpan<byte> table)
    {
        if (FindName(table, 4) is { } full)
            return full;
        var family = FindName(table, 1);
        if (family is null)
            return null;
        return FindName(table, 2) is { } style && !style.Equals("Regular", StringComparison.OrdinalIgnoreCase) ? $"{family} {style}" : family;
    }

    private static string? FindName(ReadOnlySpan<byte> table, int nameId)
    {
        var count = BinaryPrimitives.ReadUInt16BigEndian(table[2..]);
        var storage = BinaryPrimitives.ReadUInt16BigEndian(table[4..]);
        string? fallback = null;
        for (var i = 0; i < count; i++)
        {
            var record = 6 + (i * 12);
            if (record + 12 > table.Length)
                break;
            var platform = BinaryPrimitives.ReadUInt16BigEndian(table[record..]);
            var encoding = BinaryPrimitives.ReadUInt16BigEndian(table[(record + 2)..]);
            var language = BinaryPrimitives.ReadUInt16BigEndian(table[(record + 4)..]);
            var id = BinaryPrimitives.ReadUInt16BigEndian(table[(record + 6)..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(table[(record + 8)..]);
            var start = storage + BinaryPrimitives.ReadUInt16BigEndian(table[(record + 10)..]);
            if (id != nameId || start + length > table.Length || length == 0)
                continue;
            var bytes = table.Slice(start, length);
            var unicode = platform == 0 || (platform == 3 && encoding is 0 or 1 or 10);
            var text = unicode ? Encoding.BigEndianUnicode.GetString(bytes) : platform == 1 && encoding == 0 ? Encoding.Latin1.GetString(bytes) : null;
            if (string.IsNullOrWhiteSpace(text))
                continue;
            if (platform == 3 && language == 0x0409)
                return text.Trim();
            if (fallback is null || unicode)
                fallback = text.Trim();
        }

        return fallback;
    }

    private static bool ReadAt(Stream stream, long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset + buffer.Length > stream.Length)
            return false;
        stream.Position = offset;
        stream.ReadExactly(buffer);
        return true;
    }
}
