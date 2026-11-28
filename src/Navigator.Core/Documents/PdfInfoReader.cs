using System.Globalization;
using System.Text;

namespace Navigator.Core.Documents;

public sealed record PdfSummary(string Version, int? PageCount, string? Title, string? Author, string? Producer, bool IsEncrypted);

/// <summary>
/// Version, page count and document information of a PDF, found by scanning its first and last bytes (at most a few
/// MB, no object graph is built). Files whose page tree sits in compressed object streams report no page count; the
/// rendered first page comes from the platform thumbnailer, not from here.
/// </summary>
public static class PdfInfoReader
{
    private const int HeadBytes = 2 * 1024 * 1024;
    private const int TailBytes = 256 * 1024;

    public static PdfSummary? Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess);
        return Read(stream);
    }

    public static PdfSummary? Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var head = new byte[(int)Math.Min(HeadBytes, stream.Length)];
        stream.Position = 0;
        var headLength = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        var start = head.AsSpan(0, Math.Min(headLength, 1024)).IndexOf("%PDF-"u8);
        if (start < 0)
            return null;

        var version = Encoding.ASCII.GetString(head, start + 5, Math.Min(3, headLength - start - 5));
        var text = Encoding.Latin1.GetString(head, 0, headLength);
        if (stream.Length > headLength)
        {
            var tailLength = (int)Math.Min(TailBytes, stream.Length - headLength);
            var tail = new byte[tailLength];
            stream.Position = stream.Length - tailLength;
            var read = stream.ReadAtLeast(tail, tailLength, throwOnEndOfStream: false);
            text += "\n" + Encoding.Latin1.GetString(tail, 0, read);
        }

        var info = InfoDictionary(text);
        return new PdfSummary(
            version.Trim(),
            PageCount(text),
            InfoString(info, "/Title"),
            InfoString(info, "/Author"),
            InfoString(info, "/Producer"),
            text.Contains("/Encrypt", StringComparison.Ordinal));
    }

    /// <summary>
    /// The largest /Count of a /Type /Pages node (the root of the page tree counts every page), or the linearization
    /// dictionary's /N.
    /// </summary>
    internal static int? PageCount(string text)
    {
        var best = 0;
        var index = 0;
        while ((index = text.IndexOf("/Type", index, StringComparison.Ordinal)) >= 0)
        {
            index += 5;
            var cursor = SkipWhitespace(text, index);
            if (!text.AsSpan(cursor).StartsWith("/Pages", StringComparison.Ordinal))
                continue;

            // The /Count of this dictionary: search the surrounding object (bounded window both ways).
            var objectStart = text.LastIndexOf("obj", cursor, Math.Min(cursor + 1, 512), StringComparison.Ordinal);
            var windowStart = objectStart >= 0 ? objectStart : Math.Max(0, cursor - 512);
            var windowEnd = text.IndexOf("endobj", cursor, Math.Min(text.Length - cursor, 2048), StringComparison.Ordinal);
            if (windowEnd < 0)
                windowEnd = Math.Min(text.Length, cursor + 512);
            var count = text.IndexOf("/Count", windowStart, windowEnd - windowStart, StringComparison.Ordinal);
            if (count >= 0 && ReadInteger(text, count + 6) is { } value && value > best)
                best = value;
        }

        if (best > 0)
            return best;

        var linearized = text.IndexOf("/Linearized", StringComparison.Ordinal);
        if (linearized >= 0 && linearized < 4096)
        {
            var n = text.IndexOf("/N ", linearized, Math.Min(text.Length - linearized, 512), StringComparison.Ordinal);
            if (n >= 0 && ReadInteger(text, n + 2) is > 0 and var pages)
                return pages;
        }

        return null;
    }

    /// <summary>
    /// The object the trailer's /Info points to ("/Info 12 0 R" → "12 0 obj … endobj"), when it is stored uncompressed;
    /// empty otherwise. Searching the whole file instead would pick up bookmark titles.
    /// </summary>
    internal static string InfoDictionary(string text)
    {
        var info = text.LastIndexOf("/Info", StringComparison.Ordinal);
        if (info < 0 || ReadInteger(text, info + 5) is not { } number)
            return string.Empty;
        var generation = ReadInteger(text, SkipWhitespace(text, info + 5) + number.ToString(CultureInfo.InvariantCulture).Length) ?? 0;
        var header = string.Create(CultureInfo.InvariantCulture, $"{number} {generation} obj");
        var start = -1;
        for (var search = 0; (search = text.IndexOf(header, search, StringComparison.Ordinal)) >= 0; search += header.Length)
        {
            if (search == 0 || !char.IsAsciiDigit(text[search - 1]))
                start = search; // the last definition wins (incremental updates append new versions)
        }

        if (start < 0)
            return string.Empty;
        var end = text.IndexOf("endobj", start, StringComparison.Ordinal);
        return end < 0 ? string.Empty : text[start..end];
    }

    /// <summary>A text value of the document information dictionary.</summary>
    internal static string? InfoString(string text, string key)
    {
        var index = text.IndexOf(key, StringComparison.Ordinal);
        if (index < 0)
            return null;
        var cursor = SkipWhitespace(text, index + key.Length);
        if (cursor >= text.Length)
            return null;

        if (text[cursor] == '(')
        {
            var bytes = new List<byte>();
            var depth = 0;
            for (var i = cursor + 1; i < text.Length && bytes.Count < 1024; i++)
            {
                var c = text[i];
                if (c == '\\' && i + 1 < text.Length)
                {
                    var next = text[++i];
                    if (next is >= '0' and <= '7')
                    {
                        var octal = next - '0';
                        for (var k = 0; k < 2 && i + 1 < text.Length && text[i + 1] is >= '0' and <= '7'; k++)
                            octal = (octal * 8) + (text[++i] - '0');
                        bytes.Add((byte)octal);
                    }
                    else
                    {
                        bytes.Add((byte)(next switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', _ => next }));
                    }

                    continue;
                }

                if (c == '(')
                    depth++;
                else if (c == ')' && depth-- == 0)
                    break;
                bytes.Add((byte)c);
            }

            return Decode([.. bytes]);
        }

        if (text[cursor] == '<' && cursor + 1 < text.Length && text[cursor + 1] != '<')
        {
            var end = text.IndexOf('>', cursor);
            if (end < 0 || end - cursor > 4096)
                return null;
            var hex = new string(text.AsSpan(cursor + 1, end - cursor - 1).ToArray().Where(Uri.IsHexDigit).ToArray());
            if (hex.Length % 2 == 1)
                hex += "0";
            try
            {
                return Decode(Convert.FromHexString(hex));
            }
            catch (FormatException)
            {
                return null;
            }
        }

        return null;
    }

    private static string? Decode(byte[] bytes)
    {
        string value;
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            value = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            value = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        else
            value = Encoding.Latin1.GetString(bytes);
        value = value.Trim('\0', ' ');
        return value.Length == 0 ? null : value;
    }

    private static int SkipWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
        return index;
    }

    private static int? ReadInteger(string text, int index)
    {
        index = SkipWhitespace(text, index);
        var end = index;
        while (end < text.Length && end - index < 9 && char.IsAsciiDigit(text[end]))
            end++;
        return end > index && int.TryParse(text.AsSpan(index, end - index), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
