using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Navigator.Core.Documents;

/// <summary>Text and properties of an Office document, as shown by the preview pane.</summary>
public sealed record DocumentSummary
{
    public string Format { get; init; } = string.Empty;

    public string? Title { get; init; }

    public string? Author { get; init; }

    public string? LastModifiedBy { get; init; }

    public DateTime? Modified { get; init; }

    public int? Pages { get; init; }

    public int? Words { get; init; }

    public int? Slides { get; init; }

    public int? Sheets { get; init; }

    /// <summary>Plain text of the beginning of the document (paragraphs, slide text, the first sheet's cells).</summary>
    public string Text { get; init; } = string.Empty;

    public bool IsTruncated { get; init; }
}

/// <summary>
/// Reads Office Open XML (.docx, .xlsx, .pptx) and OpenDocument (.odt, .ods, .odp) packages with the base class
/// library only: the ZIP container is opened, the few XML parts that matter are streamed with an <see cref="XmlReader"/>
/// and reading stops as soon as enough text was collected, so even a huge workbook costs a few milliseconds.
/// DTDs and external entities are refused, and parts whose uncompressed size is implausible are skipped.
/// </summary>
public static class OfficeDocumentReader
{
    private const long MaxPartBytes = 256L * 1024 * 1024;
    private const int MaxThumbnailBytes = 8 * 1024 * 1024;
    private const int MaxRows = 200;

    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string DrawingNamespace = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string SheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string OdfText = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private const string OdfTable = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private const string OdfDraw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private const string OdfMeta = "urn:oasis:names:tc:opendocument:xmlns:meta:1.0";
    private const string DublinCore = "http://purl.org/dc/elements/1.1/";

    private static readonly XmlReaderSettings XmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CloseInput = false,
    };

    public static bool IsSupported(string extension) => Kind(extension) != PackageKind.None;

    /// <summary>Reads the document's text (at most <paramref name="maxCharacters"/>) and properties; null when it is not a readable package.</summary>
    public static DocumentSummary? Read(string path, int maxCharacters = 16 * 1024, CancellationToken cancellationToken = default)
    {
        var kind = Kind(Path.GetExtension(path));
        if (kind == PackageKind.None)
            return null;
        try
        {
            using var archive = ZipFile.OpenRead(path);
            return kind switch
            {
                PackageKind.Word => ReadWord(archive, maxCharacters, cancellationToken),
                PackageKind.Excel => ReadExcel(archive, maxCharacters, cancellationToken),
                PackageKind.PowerPoint => ReadPowerPoint(archive, maxCharacters, cancellationToken),
                _ => ReadOpenDocument(archive, kind, maxCharacters, cancellationToken),
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or NotSupportedException)
        {
            return null; // not a ZIP package, or damaged XML
        }
    }

    /// <summary>
    /// The picture the authoring application saved inside the package (docProps/thumbnail.* or Thumbnails/thumbnail.png),
    /// encoded as JPEG or PNG; null when there is none.
    /// </summary>
    public static byte[]? ReadThumbnail(string path)
    {
        if (Kind(Path.GetExtension(path)) == PackageKind.None)
            return null;
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.GetEntry("Thumbnails/thumbnail.png") ?? archive.GetEntry("docProps/thumbnail.jpeg")
                        ?? archive.GetEntry("docProps/thumbnail.jpg") ?? archive.GetEntry("docProps/thumbnail.png");
            if (entry is null || entry.Length is <= 0 or > MaxThumbnailBytes)
                return null;
            using var stream = entry.Open();
            var buffer = new byte[entry.Length];
            stream.ReadExactly(buffer);
            return buffer;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or NotSupportedException)
        {
            return null;
        }
    }

    private enum PackageKind
    {
        None,
        Word,
        Excel,
        PowerPoint,
        OdfText,
        OdfSpreadsheet,
        OdfPresentation,
    }

    private static PackageKind Kind(string extension) => extension.ToLowerInvariant() switch
    {
        ".docx" or ".docm" or ".dotx" or ".dotm" => PackageKind.Word,
        ".xlsx" or ".xlsm" or ".xltx" or ".xltm" => PackageKind.Excel,
        ".pptx" or ".pptm" or ".ppsx" or ".ppsm" or ".potx" => PackageKind.PowerPoint,
        ".odt" or ".ott" => PackageKind.OdfText,
        ".ods" or ".ots" => PackageKind.OdfSpreadsheet,
        ".odp" or ".otp" => PackageKind.OdfPresentation,
        _ => PackageKind.None,
    };

    // ── Office Open XML ─────────────────────────────────────────

    private static DocumentSummary ReadWord(ZipArchive archive, int max, CancellationToken token)
    {
        var text = new TextCollector(max);
        WithPart(archive, "word/document.xml", reader =>
        {
            while (!text.IsFull && reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (reader.NamespaceURI != WordNamespace)
                    continue;
                if (reader.NodeType == XmlNodeType.Element)
                {
                    switch (reader.LocalName)
                    {
                        case "t":
                            text.Append(ReadText(reader));
                            break;
                        case "tab":
                            text.Append("\t");
                            break;
                        case "br" or "cr":
                            text.NewLine();
                            break;
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "p")
                {
                    text.NewLine();
                }
            }
        });

        return WithOpenXmlProperties(archive, new DocumentSummary { Format = "Word", Text = text.ToString(), IsTruncated = text.IsFull });
    }

    private static DocumentSummary ReadPowerPoint(ZipArchive archive, int max, CancellationToken token)
    {
        var slides = archive.Entries
            .Select(e => (Entry: e, Number: SlideNumber(e.FullName, "ppt/slides/slide")))
            .Where(s => s.Number > 0)
            .OrderBy(s => s.Number)
            .ToList();

        var text = new TextCollector(max);
        foreach (var (entry, number) in slides)
        {
            if (text.IsFull)
                break;
            text.Append(string.Create(CultureInfo.InvariantCulture, $"— {number} —"));
            text.NewLine();
            WithPart(entry, reader =>
            {
                while (!text.IsFull && reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (reader.NamespaceURI != DrawingNamespace)
                        continue;
                    if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t")
                        text.Append(ReadText(reader));
                    else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "p")
                        text.NewLine();
                }
            });
            text.NewLine();
        }

        var summary = WithOpenXmlProperties(archive, new DocumentSummary { Format = "PowerPoint", Text = text.ToString(), IsTruncated = text.IsFull });
        return slides.Count > 0 ? summary with { Slides = slides.Count } : summary;
    }

    private static DocumentSummary ReadExcel(ZipArchive archive, int max, CancellationToken token)
    {
        var sheets = archive.Entries.Count(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal));

        // Shared strings are only read up to the largest index the first rows use; they are kept in order.
        var shared = new List<string>();
        WithPart(archive, "xl/sharedStrings.xml", reader =>
        {
            var builder = new StringBuilder();
            var characters = 0;
            while (reader.Read() && shared.Count < 100_000 && characters < 4 * 1024 * 1024)
            {
                token.ThrowIfCancellationRequested();
                if (reader.NamespaceURI != SheetNamespace)
                    continue;
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si")
                {
                    builder.Clear();
                }
                else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t")
                {
                    var value = ReadText(reader);
                    builder.Append(value);
                    characters += value.Length;
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "si")
                {
                    shared.Add(builder.ToString());
                }
            }
        });

        var text = new TextCollector(max);
        var first = archive.GetEntry("xl/worksheets/sheet1.xml")
                    ?? archive.Entries.FirstOrDefault(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal));
        if (first is not null)
        {
            WithPart(first, reader =>
            {
                var rows = 0;
                var cellsInRow = 0;
                string? cellType = null;
                while (!text.IsFull && rows < MaxRows && reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (reader.NamespaceURI != SheetNamespace)
                        continue;
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        switch (reader.LocalName)
                        {
                            case "c":
                                if (cellsInRow++ > 0)
                                    text.Append("\t");
                                cellType = reader.GetAttribute("t");
                                break;
                            case "v":
                                var value = ReadText(reader);
                                text.Append(cellType == "s" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < shared.Count
                                    ? shared[index]
                                    : value);
                                break;
                            case "t" when cellType == "inlineStr":
                                text.Append(ReadText(reader));
                                break;
                        }
                    }
                    else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "row")
                    {
                        rows++;
                        cellsInRow = 0;
                        text.NewLine();
                    }
                }
            });
        }

        var summary = WithOpenXmlProperties(archive, new DocumentSummary { Format = "Excel", Text = text.ToString(), IsTruncated = text.IsFull });
        return summary with { Sheets = sheets > 0 ? sheets : summary.Sheets };
    }

    private static DocumentSummary WithOpenXmlProperties(ZipArchive archive, DocumentSummary summary)
    {
        string? title = null, author = null, modifiedBy = null;
        DateTime? modified = null;
        int? pages = null, words = null, slides = null;
        WithPart(archive, "docProps/core.xml", reader =>
        {
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                    continue;
                switch (reader.LocalName)
                {
                    case "title": title = NullIfEmpty(ReadText(reader)); break;
                    case "creator": author = NullIfEmpty(ReadText(reader)); break;
                    case "lastModifiedBy": modifiedBy = NullIfEmpty(ReadText(reader)); break;
                    case "modified": modified = ParseDate(ReadText(reader)); break;
                }
            }
        });
        WithPart(archive, "docProps/app.xml", reader =>
        {
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                    continue;
                switch (reader.LocalName)
                {
                    case "Pages": pages = ParseCount(ReadText(reader)); break;
                    case "Words": words = ParseCount(ReadText(reader)); break;
                    case "Slides": slides = ParseCount(ReadText(reader)); break;
                }
            }
        });

        // Generators that do not lay out the document (scripts, converters) write 0 here: no information.
        return summary with { Title = title, Author = author, LastModifiedBy = modifiedBy, Modified = modified, Pages = Positive(pages), Words = Positive(words), Slides = Positive(slides) };
    }

    private static int SlideNumber(string name, string prefix)
        => name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(".xml", StringComparison.Ordinal)
           && int.TryParse(name.AsSpan(prefix.Length, name.Length - prefix.Length - 4), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : 0;

    // ── OpenDocument ────────────────────────────────────────────

    private static DocumentSummary ReadOpenDocument(ZipArchive archive, PackageKind kind, int max, CancellationToken token)
    {
        var text = new TextCollector(max);
        int sheets = 0, slides = 0, rows = 0;
        WithPart(archive, "content.xml", reader =>
        {
            var cellsInRow = 0;
            while (!text.IsFull && reader.Read())
            {
                token.ThrowIfCancellationRequested();
                var ns = reader.NamespaceURI;
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (ns == OdfText)
                    {
                        switch (reader.LocalName)
                        {
                            case "s":
                                var count = int.TryParse(reader.GetAttribute("c", OdfText), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? Math.Clamp(c, 1, 64) : 1;
                                text.Append(new string(' ', count));
                                break;
                            case "tab":
                                text.Append("\t");
                                break;
                            case "line-break":
                                text.NewLine();
                                break;
                        }
                    }
                    else if (ns == OdfTable && reader.LocalName == "table")
                    {
                        sheets++;
                        if (kind == PackageKind.OdfSpreadsheet && sheets > 1)
                            break; // only the first sheet's cells
                    }
                    else if (ns == OdfTable && reader.LocalName == "table-cell" && cellsInRow++ > 0)
                    {
                        text.Append("\t");
                    }
                    else if (ns == OdfDraw && reader.LocalName == "page")
                    {
                        slides++;
                        text.Append(string.Create(CultureInfo.InvariantCulture, $"— {slides} —"));
                        text.NewLine();
                    }
                }
                else if (reader.NodeType == XmlNodeType.Text || reader.NodeType == XmlNodeType.SignificantWhitespace)
                {
                    text.Append(reader.Value);
                }
                else if (reader.NodeType == XmlNodeType.EndElement)
                {
                    if (ns == OdfText && reader.LocalName is "p" or "h" && kind != PackageKind.OdfSpreadsheet)
                    {
                        text.NewLine();
                    }
                    else if (ns == OdfTable && reader.LocalName == "table-row")
                    {
                        cellsInRow = 0;
                        if (++rows >= MaxRows && kind == PackageKind.OdfSpreadsheet)
                            break;
                        text.NewLine();
                    }
                }
            }
        });

        string? title = null, author = null;
        DateTime? modified = null;
        int? pages = null, words = null, metaSheets = null;
        WithPart(archive, "meta.xml", reader =>
        {
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                    continue;
                if (reader.NamespaceURI == DublinCore && reader.LocalName == "title")
                    title = NullIfEmpty(ReadText(reader));
                else if (reader.NamespaceURI == DublinCore && reader.LocalName == "creator")
                    author ??= NullIfEmpty(ReadText(reader));
                else if (reader.NamespaceURI == OdfMeta && reader.LocalName == "initial-creator")
                    author = NullIfEmpty(ReadText(reader));
                else if (reader.NamespaceURI == DublinCore && reader.LocalName == "date")
                    modified = ParseDate(ReadText(reader));
                else if (reader.NamespaceURI == OdfMeta && reader.LocalName == "document-statistic")
                {
                    pages = ParseCount(reader.GetAttribute("page-count", OdfMeta));
                    words = ParseCount(reader.GetAttribute("word-count", OdfMeta));
                    metaSheets = ParseCount(reader.GetAttribute("table-count", OdfMeta));
                }
            }
        });

        return new DocumentSummary
        {
            Format = kind switch { PackageKind.OdfText => "OpenDocument Text", PackageKind.OdfSpreadsheet => "OpenDocument Spreadsheet", _ => "OpenDocument Presentation" },
            Title = title,
            Author = author,
            Modified = modified,
            Pages = kind == PackageKind.OdfText ? pages : null,
            Words = kind == PackageKind.OdfText ? words : null,
            Sheets = kind == PackageKind.OdfSpreadsheet ? metaSheets ?? (sheets > 0 ? sheets : null) : null,
            Slides = kind == PackageKind.OdfPresentation && slides > 0 ? slides : null,
            Text = text.ToString(),
            IsTruncated = text.IsFull,
        };
    }

    // ── Helpers ─────────────────────────────────────────────────

    private static void WithPart(ZipArchive archive, string name, Action<XmlReader> read)
    {
        if (archive.GetEntry(name) is { } entry)
            WithPart(entry, read);
    }

    private static void WithPart(ZipArchiveEntry entry, Action<XmlReader> read)
    {
        if (entry.Length > MaxPartBytes)
            return; // implausible for a real document: a ZIP bomb is not unpacked
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, XmlSettings);
        read(reader);
    }

    /// <summary>
    /// Text of the element the reader is on. Leaves the reader on that element's end tag (not past it), so the caller's
    /// next Read() sees the following node — ReadElementContentAsString would silently skip it.
    /// </summary>
    private static string ReadText(XmlReader reader)
    {
        if (reader.IsEmptyElement || !reader.Read())
            return string.Empty;
        return reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace
            ? reader.ReadContentAsString()
            : string.Empty;
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int? Positive(int? value) => value > 0 ? value : null;

    private static int? ParseCount(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count >= 0 ? count : null;

    private static DateTime? ParseDate(string value)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date) ? date : null;

    /// <summary>Accumulates text up to a limit, collapsing runs of blank lines.</summary>
    private sealed class TextCollector(int max)
    {
        private readonly StringBuilder _builder = new(Math.Min(max, 4096));
        private int _newLines;

        public bool IsFull => _builder.Length >= max;

        public void Append(string value)
        {
            if (value.Length == 0 || IsFull)
                return;
            _newLines = 0;
            _builder.Append(value.AsSpan(0, Math.Min(value.Length, max - _builder.Length)));
        }

        public void NewLine()
        {
            if (IsFull || _builder.Length == 0 || _newLines >= 2)
                return;
            _newLines++;
            _builder.Append('\n');
        }

        public override string ToString() => _builder.ToString().TrimEnd();
    }
}
