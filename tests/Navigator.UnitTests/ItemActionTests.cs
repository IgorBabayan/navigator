using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Navigator.Core.Archives;
using Navigator.Core.Fonts;
using Navigator.Core.Imaging;
using Navigator.Platform.ItemActions;

namespace Navigator.UnitTests;

[TestClass]
public sealed class ImageOrientationTests
{
    [TestMethod]
    public void Turn_FourQuarterTurnsComeBack_AndMirroredStayMirrored()
    {
        foreach (var start in Enum.GetValues<ImageOrientation>())
        {
            var turned = start;
            for (var i = 0; i < 4; i++)
                turned = PixelOrientation.Turn(turned, clockwise: true);
            Assert.AreEqual(start, turned);
            Assert.AreEqual(start, PixelOrientation.Turn(PixelOrientation.Turn(start, clockwise: true), clockwise: false));
        }

        Assert.AreEqual(ImageOrientation.Rotate90, PixelOrientation.Turn(ImageOrientation.Normal, clockwise: true));
        Assert.AreEqual(ImageOrientation.Rotate270, PixelOrientation.Turn(ImageOrientation.Normal, clockwise: false));
        Assert.AreEqual(ImageOrientation.Transverse, PixelOrientation.Turn(ImageOrientation.FlipHorizontal, clockwise: true));
    }

    [TestMethod]
    public void Apply_TurnsPixels()
    {
        // 3 × 2, one byte per pixel:  a b c / d e f
        byte[] source = [1, 2, 3, 4, 5, 6];

        // Clockwise: d a / e b / f c
        CollectionAssert.AreEqual(new byte[] { 4, 1, 5, 2, 6, 3 }, PixelOrientation.Apply(source, 3, 2, 1, ImageOrientation.Rotate90));

        // Counter-clockwise: c f / b e / a d
        CollectionAssert.AreEqual(new byte[] { 3, 6, 2, 5, 1, 4 }, PixelOrientation.Apply(source, 3, 2, 1, ImageOrientation.Rotate270));
        CollectionAssert.AreEqual(new byte[] { 6, 5, 4, 3, 2, 1 }, PixelOrientation.Apply(source, 3, 2, 1, ImageOrientation.Rotate180));
        CollectionAssert.AreEqual(new byte[] { 3, 2, 1, 6, 5, 4 }, PixelOrientation.Apply(source, 3, 2, 1, ImageOrientation.FlipHorizontal));
        CollectionAssert.AreEqual(new byte[] { 4, 5, 6, 1, 2, 3 }, PixelOrientation.Apply(source, 3, 2, 1, ImageOrientation.FlipVertical));
        CollectionAssert.AreEqual(new byte[] { 1, 4, 2, 5, 3, 6 }, PixelOrientation.Apply(source, 3, 2, 1, ImageOrientation.Transpose));
        CollectionAssert.AreEqual(new byte[] { 6, 3, 5, 2, 4, 1 }, PixelOrientation.Apply(source, 3, 2, 1, ImageOrientation.Transverse));
    }

    [TestMethod]
    public void Apply_FourBytePixelsMoveAsUnits()
    {
        byte[] source = [1, 1, 1, 1, 2, 2, 2, 2];
        CollectionAssert.AreEqual(new byte[] { 1, 1, 1, 1, 2, 2, 2, 2 }, PixelOrientation.Apply(source, 2, 1, 4, ImageOrientation.Rotate90));
        CollectionAssert.AreEqual(new byte[] { 2, 2, 2, 2, 1, 1, 1, 1 }, PixelOrientation.Apply(source, 2, 1, 4, ImageOrientation.Rotate270));
    }

    [TestMethod]
    public void Jpeg_WithoutExif_GetsOneAndReadsBack()
    {
        var jpeg = TestImages.Jpeg(exif: null);
        Assert.AreEqual(ImageOrientation.Normal, JpegOrientation.Read(new MemoryStream(jpeg)));

        var turned = JpegOrientation.WithOrientation(jpeg, ImageOrientation.Rotate90);
        Assert.AreEqual(ImageOrientation.Rotate90, JpegOrientation.Read(new MemoryStream(turned)));
        CollectionAssert.AreEqual(jpeg[^8..], turned[^8..], "the compressed picture is untouched");
        Assert.AreEqual(0xE0, turned[3], "the JFIF segment stays first");
    }

    [TestMethod]
    public void Jpeg_ExifWithoutOrientation_KeepsItsOtherTags()
    {
        var jpeg = TestImages.Jpeg(TestImages.Tiff(bigEndian: false, (0x010F, "Cam"u8.ToArray())));
        var turned = JpegOrientation.WithOrientation(jpeg, ImageOrientation.Rotate270);
        Assert.AreEqual(ImageOrientation.Rotate270, JpegOrientation.Read(new MemoryStream(turned)));
        Assert.IsTrue(turned.AsSpan().IndexOf("Cam"u8) > 0);
    }

    [TestMethod]
    public void Jpeg_RotateInPlace_PatchesTagAndXmp()
    {
        var jpeg = TestImages.Jpeg(TestImages.Tiff(bigEndian: true, (0x0112, null)), xmp: "<x tiff:Orientation=\"1\"/>");
        var path = Path.Combine(Path.GetTempPath(), $"navigator-{Guid.NewGuid():N}.jpg");
        try
        {
            File.WriteAllBytes(path, jpeg);
            JpegOrientation.Rotate(path, clockwise: true);
            var after = File.ReadAllBytes(path);
            Assert.AreEqual(jpeg.Length, after.Length, "patched in place");
            Assert.AreEqual(ImageOrientation.Rotate90, JpegOrientation.Read(path));
            Assert.IsTrue(Encoding.ASCII.GetString(after).Contains("tiff:Orientation=\"6\"", StringComparison.Ordinal));

            JpegOrientation.Rotate(path, clockwise: false);
            JpegOrientation.Rotate(path, clockwise: false);
            Assert.AreEqual(ImageOrientation.Rotate270, JpegOrientation.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Png_QuarterTurns_AreLossless()
    {
        foreach (var (colorType, bitDepth, channels) in new[] { (6, 8, 4), (2, 8, 3), (0, 1, 1), (3, 4, 1), (0, 16, 1), (4, 8, 2) })
        {
            var png = TestImages.Png(5, 3, colorType, bitDepth, channels);
            var (width, height, unit, pixels) = PngRotation.ReadPixels(png);

            var clockwise = PngRotation.Rotate(png, clockwise: true);
            var turned = PngRotation.ReadPixels(clockwise);
            Assert.AreEqual((height, width), (turned.Width, turned.Height), $"type {colorType}/{bitDepth}");
            CollectionAssert.AreEqual(PixelOrientation.Apply(pixels, width, height, unit, ImageOrientation.Rotate90), turned.Pixels, $"type {colorType}/{bitDepth}");

            var back = PngRotation.ReadPixels(PngRotation.Rotate(clockwise, clockwise: false));
            CollectionAssert.AreEqual(pixels, back.Pixels, $"type {colorType}/{bitDepth}");
        }
    }

    [TestMethod]
    public void Png_DamagedOrAnimated_IsRefused()
    {
        var png = TestImages.Png(2, 2, 6, 8, 4);
        png[^20] ^= 0xFF; // inside IDAT: its CRC no longer matches
        Assert.ThrowsExactly<InvalidDataException>(() => PngRotation.Rotate(png, true));
        Assert.ThrowsExactly<InvalidDataException>(() => PngRotation.Rotate("not a png"u8, true));
    }

    [TestMethod]
    public void CanRotate_JpegAndPngOnly()
    {
        Assert.IsTrue(ImageRotation.CanRotate("/p/photo.JPG"));
        Assert.IsTrue(ImageRotation.CanRotate("/p/shot.png"));
        Assert.IsFalse(ImageRotation.CanRotate("/p/anim.gif"));
        Assert.IsFalse(ImageRotation.CanRotate("/p/scan.tiff"));
    }
}

[TestClass]
public sealed class FontAndArchiveToolTests
{
    [TestMethod]
    public void FontName_PrefersWindowsEnglishFullName()
    {
        var table = TestImages.NameTable((1, 0, 0, 4, "Mac Name"), (3, 1, 0x0407, 4, "Deutsch"), (3, 1, 0x0409, 4, "Roboto Bold"));
        Assert.AreEqual("Roboto Bold", FontFileInfo.ParseName(table));

        var familyOnly = TestImages.NameTable((3, 1, 0x0409, 1, "Inter"), (3, 1, 0x0409, 2, "Italic"));
        Assert.AreEqual("Inter Italic", FontFileInfo.ParseName(familyOnly));
    }

    [TestMethod]
    public void FontFile_ReadsTrueTypeAndCollections()
    {
        var face = TestImages.Font(TestImages.NameTable((3, 1, 0x0409, 4, "Sample Regular")), cff: true);
        var info = FontFileInfo.Read(new MemoryStream(face));
        Assert.IsNotNull(info);
        Assert.AreEqual("Sample Regular", info.FullName);
        Assert.IsTrue(info.IsOpenTypeCff);
        Assert.IsNull(FontFileInfo.Read(new MemoryStream("not a font at all"u8.ToArray())));
        Assert.IsTrue(FontFileInfo.IsInstallable("a.TTF"));
        Assert.IsFalse(FontFileInfo.IsInstallable("a.woff2"));
    }

    [TestMethod]
    public void Listing_SevenZipTechnicalFormat()
    {
        const string output = "7-Zip 23.01\n\nListing archive: t.7z\n\n--\nPath = t.7z\nType = 7z\n\n----------\nPath = sub\nFolder = +\n\nPath = sub\\b.txt\nSize = 5\n\nPath = a.txt\r\n";
        CollectionAssert.AreEqual(new[] { "sub", "sub/b.txt", "a.txt" }, ArchiveReaderTool.ParseListing(output, ArchiveReaderTool.ToolKind.SevenZip).ToArray());
        CollectionAssert.AreEqual(new[] { "x/y.txt" }, ArchiveReaderTool.ParseListing("t.rar: RAR\nx/y.txt\n", ArchiveReaderTool.ToolKind.Unar).ToArray());
        CollectionAssert.AreEqual(new[] { "a", "b/c" }, ArchiveReaderTool.ParseListing("a\nb/c\n", ArchiveReaderTool.ToolKind.Libarchive).ToArray());
    }

    [TestMethod]
    public void Formats_SevenZipAndRarAreDetected()
    {
        Assert.AreEqual(ArchiveFormat.SevenZip, ArchiveService.DetectFormat("/a/backup.7Z"));
        Assert.AreEqual(ArchiveFormat.Rar, ArchiveService.DetectFormat("/a/photos.part1.rar"));
        Assert.AreEqual("photos.part1", ArchiveService.ArchiveStem("photos.part1.rar"));
        Assert.AreEqual("backup", ArchiveService.ArchiveStem("backup.7z"));
    }

    [TestMethod]
    public void Mounts_DevicesAndEscapes()
    {
        var mounts = UnixMounts.ParseMounts("/dev/sdb1 /media/me/My\\040Stick vfat rw 0 0\n/dev/loop3 /media/me/DISC iso9660 ro 0 0\n").ToList();
        Assert.AreEqual(("/dev/sdb1", "/media/me/My Stick"), mounts[0]);
        Assert.AreEqual("/dev/sdb", UnixMounts.WholeDevice("/dev/sdb1"));
        Assert.AreEqual("/dev/nvme0n1", UnixMounts.WholeDevice("/dev/nvme0n1p2"));
        Assert.AreEqual("/dev/loop3", UnixMounts.WholeDevice("/dev/loop3p1"));
        Assert.AreEqual("/dev/loop3", UnixMounts.WholeDevice("/dev/loop3"));
        Assert.AreEqual("/dev/sr0", UnixMounts.WholeDevice("/dev/sr0"));
    }
}

/// <summary>Small synthetic images and fonts built byte by byte, so the tests need no binary fixtures.</summary>
internal static class TestImages
{
    /// <summary>SOI, JFIF APP0, optional EXIF APP1 (TIFF given), optional XMP APP1, a stand-in scan, EOI.</summary>
    public static byte[] Jpeg(byte[]? exif, string? xmp = null)
    {
        using var output = new MemoryStream();
        output.Write([0xFF, 0xD8]);
        Segment(output, 0xE0, [.. "JFIF\0"u8, 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        if (exif is not null)
            Segment(output, 0xE1, [.. "Exif\0\0"u8, .. exif]);
        if (xmp is not null)
            Segment(output, 0xE1, [.. "http://ns.adobe.com/xap/1.0/\0"u8, .. Encoding.ASCII.GetBytes(xmp)]);
        Segment(output, 0xDA, [1, 2, 3]);
        output.Write([9, 8, 7, 6, 5, 4, 0xFF, 0xD9]);
        return output.ToArray();
    }

    private static void Segment(Stream output, byte marker, byte[] body)
    {
        output.Write([0xFF, marker, (byte)((body.Length + 2) >> 8), (byte)(body.Length + 2)]);
        output.Write(body);
    }

    /// <summary>A TIFF header with IFD0 holding the given tags (ASCII values stored after the IFD; null = orientation SHORT 1).</summary>
    public static byte[] Tiff(bool bigEndian, params (ushort Tag, byte[]? Ascii)[] tags)
    {
        var ifdSize = 2 + (tags.Length * 12) + 4;
        var data = new List<byte>();
        var tiff = new byte[8 + ifdSize];
        tiff[0] = tiff[1] = (byte)(bigEndian ? 'M' : 'I');
        Write16(tiff, 2, 42, bigEndian);
        Write32(tiff, 4, 8, bigEndian);
        Write16(tiff, 8, (ushort)tags.Length, bigEndian);
        for (var i = 0; i < tags.Length; i++)
        {
            var at = 10 + (i * 12);
            Write16(tiff, at, tags[i].Tag, bigEndian);
            if (tags[i].Ascii is { } ascii)
            {
                Write16(tiff, at + 2, 2, bigEndian);
                Write32(tiff, at + 4, (uint)ascii.Length, bigEndian);
                Write32(tiff, at + 8, (uint)(tiff.Length + data.Count), bigEndian);
                data.AddRange(ascii);
            }
            else
            {
                Write16(tiff, at + 2, 3, bigEndian);
                Write32(tiff, at + 4, 1, bigEndian);
                Write16(tiff, at + 8, 1, bigEndian);
            }
        }

        return [.. tiff, .. data];
    }

    /// <summary>A non-interlaced PNG with deterministic pseudo-random samples.</summary>
    public static byte[] Png(int width, int height, int colorType, int bitDepth, int channels)
    {
        var stride = ((width * channels * bitDepth) + 7) / 8;
        var raw = new byte[(stride + 1) * height];
        var random = new Random(width * 31 + height + colorType + bitDepth);
        for (var y = 0; y < height; y++)
        {
            raw[y * (stride + 1)] = 0;
            random.NextBytes(raw.AsSpan((y * (stride + 1)) + 1, stride));
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(raw);

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = (byte)bitDepth;
        header[9] = (byte)colorType;
        Chunk(png, "IHDR", header);
        if (colorType == 3)
            Chunk(png, "PLTE", new byte[16 * 3]);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.Write(length);
        byte[] typed = [.. Encoding.ASCII.GetBytes(type), .. data];
        output.Write(typed);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, PngRotation.Crc32(typed));
        output.Write(crc);
    }

    /// <summary>A "name" table with (platform, encoding, language, nameId, text) records.</summary>
    public static byte[] NameTable(params (ushort Platform, ushort Encoding, ushort Language, ushort NameId, string Text)[] records)
    {
        var strings = new List<byte>();
        var table = new byte[6 + (records.Length * 12)];
        Write16(table, 2, (ushort)records.Length, true);
        Write16(table, 4, (ushort)table.Length, true);
        for (var i = 0; i < records.Length; i++)
        {
            var (platform, encoding, language, id, text) = records[i];
            var bytes = platform == 1 ? Encoding.Latin1.GetBytes(text) : Encoding.BigEndianUnicode.GetBytes(text);
            var at = 6 + (i * 12);
            Write16(table, at, platform, true);
            Write16(table, at + 2, encoding, true);
            Write16(table, at + 4, language, true);
            Write16(table, at + 6, id, true);
            Write16(table, at + 8, (ushort)bytes.Length, true);
            Write16(table, at + 10, (ushort)strings.Count, true);
            strings.AddRange(bytes);
        }

        return [.. table, .. strings];
    }

    /// <summary>A font file holding only a name table.</summary>
    public static byte[] Font(byte[] nameTable, bool cff)
    {
        var header = new byte[12 + 16];
        Write32(header, 0, cff ? 0x4F54544Fu : 0x00010000u, true);
        Write16(header, 4, 1, true);
        Write32(header, 12, 0x6E616D65, true);
        Write32(header, 20, (uint)header.Length, true);
        Write32(header, 24, (uint)nameTable.Length, true);
        return [.. header, .. nameTable];
    }

    private static void Write16(byte[] target, int at, ushort value, bool bigEndian)
    {
        if (bigEndian)
            BinaryPrimitives.WriteUInt16BigEndian(target.AsSpan(at), value);
        else
            BinaryPrimitives.WriteUInt16LittleEndian(target.AsSpan(at), value);
    }

    private static void Write32(byte[] target, int at, uint value, bool bigEndian)
    {
        if (bigEndian)
            BinaryPrimitives.WriteUInt32BigEndian(target.AsSpan(at), value);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(at), value);
    }
}
