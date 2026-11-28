using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Navigator.Core.Customization;
using Navigator.Core.Documents;
using Navigator.Core.Media;
using Navigator.Core.Platform;
using Navigator.Core.Settings;
using Navigator.Core.Signatures;
using Navigator.Platform.Preview;
using Navigator.Presentation.Details;
using Navigator.Tests;

namespace Navigator.UnitTests;

[TestClass]
public sealed class MediaInfoTests
{
    [TestMethod]
    public void Wav_ReadsFormatDurationAndInfoTags()
    {
        using var stream = new MemoryStream();
        var data = new byte[44100 * 2 * 2]; // one second, 16-bit stereo
        var info = Chunk("INAM", "Morning\0"u8.ToArray());
        var list = Concat("INFO"u8.ToArray(), info);
        var fmt = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(fmt, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(4), 44100);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(8), 44100 * 4);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(12), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(14), 16);
        var body = Concat("WAVE"u8.ToArray(), Chunk("fmt ", fmt), Chunk("LIST", list), Chunk("data", data));
        stream.Write(Chunk("RIFF", body));

        var media = MediaInfoReader.Read(stream)!;
        Assert.AreEqual("WAV", media.Format);
        Assert.AreEqual(44100, media.SampleRate);
        Assert.AreEqual(2, media.Channels);
        Assert.AreEqual(16, media.BitsPerSample);
        Assert.AreEqual("PCM", media.AudioCodec);
        Assert.AreEqual(1.0, media.Duration!.Value.TotalSeconds, 0.001);
        Assert.AreEqual("Morning", media.Title);
    }

    [TestMethod]
    public void Mp3_ReadsId3TagsCoverAndXingDuration()
    {
        var cover = JpegStub();
        var frames = Concat(
            Id3Frame("TIT2", [3, .. "Song"u8.ToArray()]),
            Id3Frame("TPE1", [1, 0xFF, 0xFE, .. Encoding.Unicode.GetBytes("Артист")]),
            Id3Frame("APIC", [0, .. "image/jpeg"u8.ToArray(), 0, 3, 0, .. cover]));
        var header = new byte[10];
        "ID3"u8.CopyTo(header);
        header[3] = 3;
        WriteSyncsafe(header.AsSpan(6), frames.Length);

        // MPEG-1 Layer III, 128 kbit/s, 44.1 kHz, stereo, with a Xing header of 1000 frames.
        var frame = new byte[417];
        frame[0] = 0xFF;
        frame[1] = 0xFB;
        frame[2] = 0x90;
        frame[3] = 0x00;
        "Xing"u8.CopyTo(frame.AsSpan(36));
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(40), 1);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(44), 1000);
        using var stream = new MemoryStream(Concat(header, frames, frame, new byte[4096]));

        var media = MediaInfoReader.Read(stream, includeCoverArt: true)!;
        Assert.AreEqual("MP3", media.Format);
        Assert.AreEqual("Song", media.Title);
        Assert.AreEqual("Артист", media.Artist);
        Assert.AreEqual(44100, media.SampleRate);
        Assert.AreEqual(2, media.Channels);
        Assert.AreEqual(1000 * 1152 / 44100.0, media.Duration!.Value.TotalSeconds, 0.01);
        CollectionAssert.AreEqual(cover, media.CoverArt);

        stream.Position = 0;
        Assert.IsNull(MediaInfoReader.Read(stream)!.CoverArt, "pictures are only read when asked for");
    }

    [TestMethod]
    public void Flac_ReadsStreamInfoAndComments()
    {
        var streamInfo = new byte[34];
        // 48 kHz, 2 channels, 24 bits, 96000 samples (two seconds).
        var rate = 48000;
        streamInfo[10] = (byte)(rate >> 12);
        streamInfo[11] = (byte)(rate >> 4);
        streamInfo[12] = (byte)(((rate & 0xF) << 4) | ((2 - 1) << 1) | ((24 - 1) >> 4));
        streamInfo[13] = (byte)(((24 - 1) & 0xF) << 4);
        BinaryPrimitives.WriteUInt32BigEndian(streamInfo.AsSpan(14), 96000);
        var comments = VorbisComments("TITLE=Live", "ARTIST=Band", "DATE=2021-05-01");
        var flac = Concat("fLaC"u8.ToArray(), FlacBlock(0, streamInfo, last: false), FlacBlock(4, comments, last: true), new byte[1000]);

        var media = MediaInfoReader.Read(new MemoryStream(flac))!;
        Assert.AreEqual("FLAC", media.Format);
        Assert.AreEqual(48000, media.SampleRate);
        Assert.AreEqual(2, media.Channels);
        Assert.AreEqual(24, media.BitsPerSample);
        Assert.AreEqual(2.0, media.Duration!.Value.TotalSeconds, 0.001);
        Assert.AreEqual("Live", media.Title);
        Assert.AreEqual("Band", media.Artist);
        Assert.AreEqual("2021-05-01", media.Year);
    }

    [TestMethod]
    public void Mp4_ReadsDurationTracksAndTags()
    {
        var mvhd = new byte[100];
        BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(12), 1000);
        BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(16), 90_500);
        var tkhd = new byte[84];
        BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(76), 1920u << 16);
        BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(80), 1080u << 16);
        var hdlr = new byte[24];
        "vide"u8.CopyTo(hdlr.AsSpan(8));
        var stsd = new byte[8 + 86];
        BinaryPrimitives.WriteUInt32BigEndian(stsd.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(stsd.AsSpan(8), 86);
        "avc1"u8.CopyTo(stsd.AsSpan(12));
        var video = Atom("trak", Atom("tkhd", tkhd), Atom("mdia", Atom("hdlr", hdlr), Atom("minf", Atom("stbl", Atom("stsd", stsd)))));

        var title = Concat([0, 0, 0, 1, 0, 0, 0, 0], "Holiday"u8.ToArray());
        var ilst = Atom("ilst", Atom("\u00A9nam", Atom("data", title)));
        var meta = Atom("meta", [0, 0, 0, 0], Atom("hdlr", new byte[24]), ilst);
        var moov = Atom("moov", Atom("mvhd", mvhd), video, Atom("udta", meta));
        var ftyp = Atom("ftyp", "isom"u8.ToArray(), new byte[4]);
        var file = Concat(ftyp, Atom("mdat", new byte[2048]), moov); // moov at the end, as cameras write it

        var media = MediaInfoReader.Read(new MemoryStream(file))!;
        Assert.AreEqual("MPEG-4", media.Format);
        Assert.IsTrue(media.HasVideo);
        Assert.AreEqual(1920, media.Width);
        Assert.AreEqual(1080, media.Height);
        Assert.AreEqual("H.264", media.VideoCodec);
        Assert.AreEqual(90.5, media.Duration!.Value.TotalSeconds, 0.001);
        Assert.AreEqual("Holiday", media.Title);
    }

    [TestMethod]
    public void Matroska_ReadsInfoAndTracks()
    {
        var info = Ebml(0x1549A966, Concat(Ebml(0x2AD7B1, [0x0F, 0x42, 0x40]), Ebml(0x4489, DoubleBytes(12_000)), Ebml(0x7BA9, "Clip"u8.ToArray())));
        var video = Ebml(0xAE, Concat(Ebml(0x83, [1]), Ebml(0x86, "V_VP9"u8.ToArray()), Ebml(0xE0, Concat(Ebml(0xB0, [0x05, 0x00]), Ebml(0xBA, [0x02, 0xD0])))));
        var audio = Ebml(0xAE, Concat(Ebml(0x83, [2]), Ebml(0x86, "A_OPUS"u8.ToArray()), Ebml(0xE1, Concat(Ebml(0xB5, DoubleBytes(48000)), Ebml(0x9F, [2])))));
        var tracks = Ebml(0x1654AE6B, Concat(video, audio));
        var file = Concat(Ebml(0x1A45DFA3, Ebml(0x4282, "webm"u8.ToArray())), Ebml(0x18538067, Concat(info, tracks, Ebml(0x1F43B675, new byte[16]))));

        var media = MediaInfoReader.Read(new MemoryStream(file))!;
        Assert.AreEqual("WebM", media.Format);
        Assert.AreEqual("VP9", media.VideoCodec);
        Assert.AreEqual(1280, media.Width);
        Assert.AreEqual(720, media.Height);
        Assert.AreEqual("Opus", media.AudioCodec);
        Assert.AreEqual(48000, media.SampleRate);
        Assert.AreEqual(12.0, media.Duration!.Value.TotalSeconds, 0.001);
        Assert.AreEqual("Clip", media.Title);
    }

    [TestMethod]
    public void Ogg_ReadsVorbisHeadersAndDuration()
    {
        var identification = new byte[30];
        "\x01vorbis"u8.CopyTo(identification);
        identification[11] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(identification.AsSpan(12), 44100);
        var comments = Concat("\x03vorbis"u8.ToArray(), VorbisComments("TITLE=Rain"));
        var first = OggPage(serial: 7, granule: 0, identification, comments);
        var last = OggPage(serial: 7, granule: 44100 * 3, new byte[10]);

        var media = MediaInfoReader.Read(new MemoryStream(Concat(first, new byte[500], last)))!;
        Assert.AreEqual("Ogg Vorbis", media.Format);
        Assert.AreEqual(44100, media.SampleRate);
        Assert.AreEqual(2, media.Channels);
        Assert.AreEqual("Rain", media.Title);
        Assert.AreEqual(3.0, media.Duration!.Value.TotalSeconds, 0.001);
    }

    [TestMethod]
    public void Garbage_IsNotRecognizedAndTruncatedHeadersDoNotThrow()
    {
        Assert.IsNull(MediaInfoReader.Read(new MemoryStream(Encoding.ASCII.GetBytes("just some text, not media"))));
        var truncated = Concat("fLaC"u8.ToArray(), [0x00, 0x00, 0x00, 0x22], new byte[8]);
        Assert.AreEqual("FLAC", MediaInfoReader.Read(new MemoryStream(truncated))!.Format);
        var hostile = Atom("moov", Atom("trak", Atom("mdia", Atom("hdlr", [1, 2]))));
        Assert.IsNotNull(MediaInfoReader.Read(new MemoryStream(Concat(Atom("ftyp", new byte[8]), hostile))));
    }

    internal static byte[] JpegStub() => [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 0xFF, 0xD9];

    private static byte[] Chunk(string id, byte[] data)
    {
        var chunk = new byte[8 + data.Length + (data.Length & 1)];
        Encoding.ASCII.GetBytes(id).CopyTo(chunk, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)data.Length);
        data.CopyTo(chunk, 8);
        return chunk;
    }

    private static byte[] Id3Frame(string id, byte[] data)
    {
        var frame = new byte[10 + data.Length];
        Encoding.ASCII.GetBytes(id).CopyTo(frame, 0);
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(4), data.Length);
        data.CopyTo(frame, 10);
        return frame;
    }

    private static void WriteSyncsafe(Span<byte> target, int value)
    {
        target[0] = (byte)((value >> 21) & 0x7F);
        target[1] = (byte)((value >> 14) & 0x7F);
        target[2] = (byte)((value >> 7) & 0x7F);
        target[3] = (byte)(value & 0x7F);
    }

    private static byte[] FlacBlock(int type, byte[] data, bool last)
        => Concat([(byte)((last ? 0x80 : 0) | type), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length], data);

    private static byte[] VorbisComments(params string[] comments)
    {
        using var stream = new MemoryStream();
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(number, 4);
        stream.Write(number);
        stream.Write("test"u8);
        BinaryPrimitives.WriteUInt32LittleEndian(number, (uint)comments.Length);
        stream.Write(number);
        foreach (var comment in comments)
        {
            var bytes = Encoding.UTF8.GetBytes(comment);
            BinaryPrimitives.WriteUInt32LittleEndian(number, (uint)bytes.Length);
            stream.Write(number);
            stream.Write(bytes);
        }

        return stream.ToArray();
    }

    private static byte[] Atom(string type, params byte[][] children)
    {
        var body = Concat(children);
        var atom = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(atom, (uint)atom.Length);
        Encoding.Latin1.GetBytes(type).CopyTo(atom, 4);
        body.CopyTo(atom, 8);
        return atom;
    }

    private static byte[] Ebml(long id, byte[] data)
    {
        var idBytes = id > 0xFFFFFF ? 4 : id > 0xFFFF ? 3 : id > 0xFF ? 2 : 1;
        var element = new List<byte>();
        for (var i = idBytes - 1; i >= 0; i--)
            element.Add((byte)(id >> (8 * i)));
        element.Add(0x01); // 8-byte size: marker byte, then 7 bytes
        Span<byte> size = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(size, data.Length);
        element.AddRange(size[1..].ToArray());
        element.AddRange(data);
        return [.. element];
    }

    private static byte[] DoubleBytes(double value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(bytes, value);
        return bytes;
    }

    private static byte[] OggPage(uint serial, long granule, params byte[][] packets)
    {
        var lacing = new List<byte>();
        foreach (var packet in packets)
        {
            var remaining = packet.Length;
            while (remaining >= 255)
            {
                lacing.Add(255);
                remaining -= 255;
            }

            lacing.Add((byte)remaining);
        }

        var header = new byte[27];
        "OggS"u8.CopyTo(header);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(6), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(14), serial);
        header[26] = (byte)lacing.Count;
        return Concat([header, [.. lacing], .. packets]);
    }

    internal static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }
}

[TestClass]
public sealed class DocumentReaderTests
{
    [TestMethod]
    public void Docx_ReadsParagraphsAndProperties()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("report.docx");
        WriteZip(path,
            ("word/document.xml", """
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
                <w:p><w:r><w:t>Quarterly</w:t></w:r><w:r><w:t xml:space="preserve"> report</w:t></w:r></w:p>
                <w:p><w:r><w:t>Second</w:t><w:tab/><w:t>line</w:t></w:r></w:p>
                </w:body></w:document>
                """),
            ("docProps/core.xml", """
                <cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/">
                <dc:title>Q3</dc:title><dc:creator>Ann</dc:creator><cp:lastModifiedBy>Bob</cp:lastModifiedBy><dcterms:modified>2026-01-02T03:04:05Z</dcterms:modified></cp:coreProperties>
                """),
            ("docProps/app.xml", """<Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties"><Pages>4</Pages><Words>1234</Words></Properties>"""));

        var document = OfficeDocumentReader.Read(path)!;
        Assert.AreEqual("Quarterly report\nSecond\tline", document.Text);
        Assert.AreEqual("Q3", document.Title);
        Assert.AreEqual("Ann", document.Author);
        Assert.AreEqual("Bob", document.LastModifiedBy);
        Assert.AreEqual(4, document.Pages);
        Assert.AreEqual(1234, document.Words);
        Assert.AreEqual(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), document.Modified);
        Assert.IsFalse(document.IsTruncated);
    }

    [TestMethod]
    public void Xlsx_ReadsSharedStringsOfTheFirstSheet()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("book.xlsx");
        WriteZip(path,
            ("xl/sharedStrings.xml", """<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>Name</t></si><si><r><t>Ag</t></r><r><t>e</t></r></si><si><t>Ann</t></si></sst>"""),
            ("xl/worksheets/sheet1.xml", """
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                <row><c t="s"><v>0</v></c><c t="s"><v>1</v></c></row>
                <row><c t="s"><v>2</v></c><c><v>42</v></c></row>
                </sheetData></worksheet>
                """),
            ("xl/worksheets/sheet2.xml", "<worksheet/>"));

        var document = OfficeDocumentReader.Read(path)!;
        Assert.AreEqual("Name\tAge\nAnn\t42", document.Text);
        Assert.AreEqual(2, document.Sheets);
    }

    [TestMethod]
    public void Pptx_ReadsSlidesInOrder()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("deck.pptx");
        const string Slide = """<p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><a:p><a:r><a:t>{0}</a:t></a:r></a:p></p:sld>""";
        WriteZip(path, ("ppt/slides/slide10.xml", string.Format(CultureInfo.InvariantCulture, Slide, "Ten")), ("ppt/slides/slide2.xml", string.Format(CultureInfo.InvariantCulture, Slide, "Two")));

        var document = OfficeDocumentReader.Read(path)!;
        Assert.AreEqual(2, document.Slides);
        Assert.IsTrue(document.Text.IndexOf("Two", StringComparison.Ordinal) < document.Text.IndexOf("Ten", StringComparison.Ordinal), document.Text);
    }

    [TestMethod]
    public void Odt_ReadsTextMetaAndThumbnail()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("letter.odt");
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
        WriteZip(path,
            ("content.xml", """
                <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0"><office:body><office:text>
                <text:h>Dear Ann</text:h><text:p>Two<text:s text:c="2"/>spaces</text:p></office:text></office:body></office:document-content>
                """),
            ("meta.xml", """
                <office:document-meta xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:meta="urn:oasis:names:tc:opendocument:xmlns:meta:1.0" xmlns:dc="http://purl.org/dc/elements/1.1/"><office:meta>
                <dc:title>Letter</dc:title><meta:initial-creator>Ivan</meta:initial-creator><meta:document-statistic meta:page-count="1" meta:word-count="4"/></office:meta></office:document-meta>
                """));
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            using var stream = archive.CreateEntry("Thumbnails/thumbnail.png").Open();
            stream.Write(png);
        }

        var document = OfficeDocumentReader.Read(path)!;
        Assert.AreEqual("Dear Ann\nTwo  spaces", document.Text);
        Assert.AreEqual("Letter", document.Title);
        Assert.AreEqual("Ivan", document.Author);
        Assert.AreEqual(1, document.Pages);
        Assert.AreEqual(4, document.Words);
        CollectionAssert.AreEqual(png, OfficeDocumentReader.ReadThumbnail(path));
    }

    [TestMethod]
    public void Document_RefusesDtdsAndStopsAtTheLimit()
    {
        using var temp = new TempDirectory();
        var hostile = temp.Combine("bomb.docx");
        WriteZip(hostile, ("word/document.xml", """<!DOCTYPE x [<!ENTITY a "aaaa">]><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:t>&a;</w:t></w:document>"""));
        Assert.IsNull(OfficeDocumentReader.Read(hostile));

        var large = temp.Combine("long.docx");
        var paragraphs = string.Concat(Enumerable.Repeat("<w:p><w:r><w:t>0123456789</w:t></w:r></w:p>", 1000));
        WriteZip(large, ("word/document.xml", $"""<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>{paragraphs}</w:body></w:document>"""));
        var document = OfficeDocumentReader.Read(large, maxCharacters: 100)!;
        Assert.IsTrue(document.IsTruncated);
        Assert.IsTrue(document.Text.Length <= 100);

        File.WriteAllText(temp.Combine("plain.docx"), "not a zip");
        Assert.IsNull(OfficeDocumentReader.Read(temp.Combine("plain.docx")));
    }

    [TestMethod]
    public void Pdf_ReadsVersionPagesAndInformation()
    {
        var pdf = """
            %PDF-1.7
            1 0 obj << /Type /Catalog /Pages 2 0 R /Outlines 9 0 R >> endobj
            2 0 obj << /Type /Pages /Kids [3 0 R 4 0 R 6 0 R] /Count 3 >> endobj
            3 0 obj << /Type /Page /Parent 2 0 R >> endobj
            9 0 obj << /Title (Chapter one) /Count 1 >> endobj
            5 0 obj << /Title (Hello \(World\) \101) /Author <FEFF0041006E006E> /Producer (Writer) >> endobj
            trailer << /Root 1 0 R /Info 5 0 R >>
            %%EOF
            """;
        var summary = PdfInfoReader.Read(new MemoryStream(Encoding.Latin1.GetBytes(pdf)))!;
        Assert.AreEqual("1.7", summary.Version);
        Assert.AreEqual(3, summary.PageCount);
        Assert.AreEqual("Hello (World) A", summary.Title);
        Assert.AreEqual("Ann", summary.Author);
        Assert.AreEqual("Writer", summary.Producer);
        Assert.IsFalse(summary.IsEncrypted);
        Assert.IsNull(PdfInfoReader.Read(new MemoryStream("hello"u8.ToArray())));
    }

    internal static void WriteZip(string path, params (string Name, string Content)[] entries)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }
}

[TestClass]
public sealed class SignatureReaderTests
{
    [TestMethod]
    public void Authenticode_ReadsSignerDigestAndTimeFromAPeFile()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Contoso Software, O=Contoso", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var signingTime = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var pkcs7 = SignedData(certificate, signingTime);

        using var temp = new TempDirectory();
        var path = temp.Combine("app.exe");
        File.WriteAllBytes(path, PeWithCertificate(pkcs7));

        Assert.IsTrue(AuthenticodeReader.IsPortableExecutable(path));
        var signature = AuthenticodeReader.ReadEmbedded(path)!;
        Assert.AreEqual("Contoso Software", signature.Signer.DisplayName);
        Assert.AreEqual("Contoso Software", signature.Signer.IssuerDisplayName);
        Assert.AreEqual(certificate.Thumbprint, signature.Signer.Thumbprint);
        Assert.AreEqual("SHA-256", signature.DigestAlgorithm);
        Assert.AreEqual(signingTime.UtcDateTime, signature.SigningTime);
        Assert.AreEqual(1, signature.Chain.Count);

        var unsigned = temp.Combine("plain.exe");
        File.WriteAllBytes(unsigned, PeWithCertificate(null));
        Assert.IsTrue(AuthenticodeReader.IsPortableExecutable(unsigned));
        Assert.IsNull(AuthenticodeReader.ReadEmbedded(unsigned));
        Assert.IsNull(AuthenticodeReader.Decode([1, 2, 3]));
        Assert.IsFalse(AuthenticodeReader.IsPortableExecutable(temp.CreateFile("text.exe", "MZ but nothing else")));
    }

    private static byte[] SignedData(X509Certificate2 certificate, DateTimeOffset signingTime)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier("1.2.840.113549.1.7.2");
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            using (writer.PushSequence())
            {
                writer.WriteInteger(1);
                using (writer.PushSetOf())
                using (writer.PushSequence())
                    writer.WriteObjectIdentifier("2.16.840.1.101.3.4.2.1");
                using (writer.PushSequence())
                    writer.WriteObjectIdentifier("1.3.6.1.4.1.311.2.1.4");
                using (writer.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 0)))
                    writer.WriteEncodedValue(certificate.RawData);
                using (writer.PushSetOf())
                using (writer.PushSequence())
                {
                    writer.WriteInteger(1);
                    using (writer.PushSequence())
                    {
                        writer.WriteEncodedValue(certificate.IssuerName.RawData);
                        writer.WriteInteger(certificate.SerialNumberBytes.Span);
                    }

                    using (writer.PushSequence())
                        writer.WriteObjectIdentifier("2.16.840.1.101.3.4.2.1");
                    using (writer.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 0)))
                    using (writer.PushSequence())
                    {
                        writer.WriteObjectIdentifier("1.2.840.113549.1.9.5");
                        using (writer.PushSetOf())
                            writer.WriteUtcTime(signingTime);
                    }

                    using (writer.PushSequence())
                        writer.WriteObjectIdentifier("1.2.840.113549.1.1.1");
                    writer.WriteOctetString(new byte[256]);
                }
            }
        }

        return writer.Encode();
    }

    /// <summary>A PE32+ header whose certificate table points at a WIN_CERTIFICATE appended to the file.</summary>
    private static byte[] PeWithCertificate(byte[]? pkcs7)
    {
        var image = new byte[0x200];
        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), 0x80);
        "PE\0\0"u8.CopyTo(image.AsSpan(0x80));
        var optional = 0x80 + 24;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optional), 0x20B);
        if (pkcs7 is null)
            return image;

        var certificate = new byte[8 + pkcs7.Length];
        BinaryPrimitives.WriteInt32LittleEndian(certificate, certificate.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(certificate.AsSpan(4), 0x0200);
        BinaryPrimitives.WriteUInt16LittleEndian(certificate.AsSpan(6), 2);
        pkcs7.CopyTo(certificate, 8);
        var directory = optional + 112 + (4 * 8);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(directory), (uint)image.Length);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(directory + 4), certificate.Length);
        return MediaInfoTests.Concat(image, certificate);
    }
}

[TestClass]
public sealed class CustomizationTests
{
    [TestMethod]
    public void CompatibilityLayers_RoundTripAndKeepUnknownLayers()
    {
        var (settings, other) = CompatibilityLayers.Parse("~ WIN7RTM RUNASADMIN GDIDPISCALING DPIUNAWARE ELEVATECREATEPROCESS");
        Assert.AreEqual(CompatibilityMode.Windows7, settings.Mode);
        Assert.IsTrue(settings.RunAsAdministrator);
        Assert.AreEqual(DpiScalingOverride.SystemEnhanced, settings.DpiOverride);
        CollectionAssert.AreEqual(new[] { "ELEVATECREATEPROCESS" }, other.ToArray());

        var changed = settings with { RunAsAdministrator = false, ReducedColor = ReducedColorMode.Colors16Bit, DisableFullscreenOptimizations = true };
        Assert.AreEqual("~ WIN7RTM 16BITCOLOR DISABLEDXMAXIMIZEDWINDOWEDMODE GDIDPISCALING DPIUNAWARE ELEVATECREATEPROCESS", CompatibilityLayers.Format(changed, other));
        Assert.IsNull(CompatibilityLayers.Format(CompatibilitySettings.Default, []));
        Assert.AreEqual(DpiScalingOverride.System, CompatibilityLayers.Parse("DPIUNAWARE").Settings.DpiOverride);
        Assert.IsTrue(CompatibilityLayers.Parse(null).Settings.IsDefault);
    }

    [TestMethod]
    public void IniText_EditsOneKeyAndKeepsTheRest()
    {
        const string DesktopIni = "[.ShellClassInfo]\r\nLocalizedResourceName=@shell32.dll,-21770\r\nIconFile=old.ico\r\n\r\n[ViewState]\r\nMode=4\r\n";
        var text = IniText.SetValue(DesktopIni, ".ShellClassInfo", "IconResource", @"C:\Icons\a.ico,0");
        text = IniText.SetValue(text, ".ShellClassInfo", "IconFile", null);
        Assert.AreEqual(@"C:\Icons\a.ico,0", IniText.GetValue(text, ".shellclassinfo", "iconresource"));
        Assert.IsNull(IniText.GetValue(text, ".ShellClassInfo", "IconFile"));
        Assert.AreEqual("4", IniText.GetValue(text, "ViewState", "Mode"));
        Assert.IsTrue(text.Contains("LocalizedResourceName=@shell32.dll,-21770\r\nIconResource=", StringComparison.Ordinal), text);

        var created = IniText.SetValue(string.Empty, "Desktop Entry", "Icon", "/icons/folder.png");
        Assert.AreEqual("[Desktop Entry]\r\nIcon=/icons/folder.png\r\n", created);
        var removed = IniText.SetValue(created, "Desktop Entry", "Icon", null);
        Assert.IsTrue(IniText.HasNoKeys(removed));
        Assert.IsTrue(IniText.IsSectionEmpty(removed, "Desktop Entry"));
        Assert.IsFalse(IniText.HasNoKeys(DesktopIni));
    }

    [TestMethod]
    public void Settings_QuickLookDefaultsAndIsSanitized()
    {
        var settings = new AppSettings();
        Assert.AreEqual(QuickLookProvider.Automatic, settings.FilesAndFolders.QuickLook);
        Assert.IsTrue(settings.FilesAndFolders.ShowMediaThumbnails);
        settings.FilesAndFolders.QuickLook = (QuickLookProvider)99;
        SettingsStore.Sanitize(settings);
        Assert.AreEqual(QuickLookProvider.Automatic, settings.FilesAndFolders.QuickLook);
    }

    [TestMethod]
    public void FreedesktopThumbnail_ReadsTheModificationTimeTextChunk()
    {
        var text = MediaInfoTests.Concat(Encoding.Latin1.GetBytes("Thumb::MTime"), [0], "1700000000"u8.ToArray());
        var chunk = new byte[12 + text.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)text.Length);
        "tEXt"u8.CopyTo(chunk.AsSpan(4));
        text.CopyTo(chunk, 8);
        var png = MediaInfoTests.Concat([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], chunk, new byte[12]);
        Assert.AreEqual("1700000000", FreedesktopThumbnailer.PngTextValue(png, "Thumb::MTime"));
        Assert.IsNull(FreedesktopThumbnailer.PngTextValue(png, "Thumb::URI"));
    }
}

[TestClass]
public sealed class MarkdownTests
{
    [TestMethod]
    public void Parse_RecognizesBlocks()
    {
        var blocks = MarkdownParser.Parse("""
            # Title
            Intro line one
            continues here.

            Setext
            ------

            - first
              continued
            - [x] done
            2. second

            > quoted
            > text

            ```cs
            var x = 1;
            ```

            | Name | Size |
            |------|-----:|
            | a    | 1    |

            ***
            """);

        var kinds = blocks.Select(b => b.Kind).ToArray();
        CollectionAssert.AreEqual(new[]
        {
            MarkdownBlockKind.Heading, MarkdownBlockKind.Paragraph, MarkdownBlockKind.Heading, MarkdownBlockKind.ListItem, MarkdownBlockKind.ListItem,
            MarkdownBlockKind.ListItem, MarkdownBlockKind.Quote, MarkdownBlockKind.Code, MarkdownBlockKind.Table, MarkdownBlockKind.Rule,
        }, kinds);
        Assert.AreEqual(1, blocks[0].Level);
        Assert.AreEqual("Intro line one continues here.", Plain(blocks[1]));
        Assert.AreEqual(2, blocks[2].Level);
        Assert.AreEqual("first continued", Plain(blocks[3]));
        Assert.AreEqual("☑", blocks[4].Marker);
        Assert.AreEqual("2.", blocks[5].Marker);
        Assert.AreEqual("quoted text", Plain(blocks[6]));
        Assert.AreEqual("var x = 1;", blocks[7].Code);
        Assert.AreEqual("cs", blocks[7].Language);
        Assert.AreEqual(2, blocks[8].Rows!.Count);
        Assert.AreEqual("Size", blocks[8].Rows![0][1][0].Text);
    }

    [TestMethod]
    public void ParseInline_AppliesStylesAndLinks()
    {
        var spans = MarkdownParser.ParseInline("a **bold** *it* `co*de` ~~old~~ [site](https://x.org \"t\") snake_case_name \\*lit\\*");
        Assert.AreEqual(MarkdownStyle.Bold, spans.First(s => s.Text == "bold").Style);
        Assert.AreEqual(MarkdownStyle.Italic, spans.First(s => s.Text == "it").Style);
        Assert.AreEqual(MarkdownStyle.Code, spans.First(s => s.Text == "co*de").Style);
        Assert.AreEqual(MarkdownStyle.Strikethrough, spans.First(s => s.Text == "old").Style);
        var link = spans.First(s => s.Text == "site");
        Assert.AreEqual(MarkdownStyle.Link, link.Style);
        Assert.AreEqual("https://x.org", link.Url);
        var plain = string.Concat(spans.Select(s => s.Text));
        Assert.IsTrue(plain.Contains("snake_case_name", StringComparison.Ordinal), plain);
        Assert.IsTrue(plain.EndsWith("*lit*", StringComparison.Ordinal), plain);
    }

    [TestMethod]
    public void Duration_IsFormattedLikeAPlayer()
    {
        Assert.AreEqual("3:07", PreviewViewModel.FormatDuration(TimeSpan.FromSeconds(187), CultureInfo.InvariantCulture));
        Assert.AreEqual("1:02:03", PreviewViewModel.FormatDuration(new TimeSpan(1, 2, 3), CultureInfo.InvariantCulture));
    }

    private static string Plain(MarkdownBlock block) => string.Concat(block.Spans.Select(s => s.Text));
}
