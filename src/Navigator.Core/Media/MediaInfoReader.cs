using System.Buffers.Binary;
using System.Text;

namespace Navigator.Core.Media;

/// <summary>
/// Reads duration, stream parameters, tags and (optionally) album art of common audio/video containers straight from
/// their headers: MP3 (ID3v1/v2, Xing/VBRI), FLAC, WAV, AVI, Ogg Vorbis/Opus, MP4/M4A/MOV and Matroska/WebM.
/// <para>
/// Only headers are touched: a typical file costs a few small reads (MP4 and Matroska are walked by seeking over the
/// media data), so this is cheap enough for the preview pane. Large payloads are skipped unless asked for (pictures),
/// and every size field is bounded, so a damaged or hostile file cannot make it allocate or loop without limit.
/// </para>
/// </summary>
public static class MediaInfoReader
{
    private const int MaxCoverArtBytes = 16 * 1024 * 1024;
    private const int MaxTextFrameBytes = 64 * 1024;
    private const int MaxCommentBlockBytes = 1024 * 1024;
    private const int MaxAtoms = 4096;

    private static readonly int[] Mpeg1Layer1 = [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448];
    private static readonly int[] Mpeg1Layer2 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384];
    private static readonly int[] Mpeg1Layer3 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] Mpeg2Layer1 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256];
    private static readonly int[] Mpeg2Layer23 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];

    /// <summary>Extensions whose headers this reader understands (decided from the name only).</summary>
    public static bool IsSupported(string extension) => extension.ToLowerInvariant() switch
    {
        ".mp3" or ".flac" or ".wav" or ".ogg" or ".oga" or ".opus" or ".m4a" or ".m4b" or ".aac" or ".mp4" or ".m4v" or ".mov"
            or ".3gp" or ".avi" or ".mkv" or ".mka" or ".webm" => true,
        _ => false,
    };

    /// <summary>Formats that commonly embed album art (used to decide whether a thumbnail is worth a look).</summary>
    public static bool CanContainCoverArt(string extension) => extension.ToLowerInvariant() is ".mp3" or ".flac" or ".m4a" or ".m4b";

    /// <summary>Reads the file's headers; null when the format is not recognized. I/O errors propagate.</summary>
    public static MediaInfo? Read(string path, bool includeCoverArt = false)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
        return Read(stream, includeCoverArt);
    }

    /// <summary>Reads from a seekable stream; null when the format is not recognized.</summary>
    public static MediaInfo? Read(Stream stream, bool includeCoverArt = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || stream.Length < 12)
            return null;

        Span<byte> head = stackalloc byte[12];
        if (ReadAt(stream, 0, head) < head.Length)
            return null;

        var info = new Builder(stream.Length);
        try
        {
            if (head.StartsWith("fLaC"u8))
                ReadFlac(stream, info, includeCoverArt);
            else if (head.StartsWith("ID3"u8) || IsMpegFrame(head))
                ReadMp3(stream, info, includeCoverArt);
            else if (head.StartsWith("RIFF"u8) && head[8..12].SequenceEqual("WAVE"u8))
                ReadRiff(stream, info, "WAV");
            else if (head.StartsWith("RIFF"u8) && head[8..12].SequenceEqual("AVI "u8))
                ReadRiff(stream, info, "AVI");
            else if (head.StartsWith("OggS"u8))
                ReadOgg(stream, info);
            else if (head[4..8].SequenceEqual("ftyp"u8) || head[4..8].SequenceEqual("moov"u8) || head[4..8].SequenceEqual("mdat"u8) || head[4..8].SequenceEqual("wide"u8))
                ReadMp4(stream, info, includeCoverArt);
            else if (head.StartsWith((ReadOnlySpan<byte>)[0x1A, 0x45, 0xDF, 0xA3]))
                ReadMatroska(stream, info);
            else
                return null;
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or EndOfStreamException or OverflowException or DecoderFallbackException)
        {
            // A damaged header: whatever was read before the damage is still shown.
        }

        return info.Format.Length == 0 ? null : info.Build();
    }

    // ── MP3 ─────────────────────────────────────────────────────

    private static void ReadMp3(Stream stream, Builder info, bool includeCoverArt)
    {
        info.Format = "MP3";
        long audioStart = 0;
        Span<byte> header = stackalloc byte[10];
        ReadAt(stream, 0, header);
        if (header.StartsWith("ID3"u8))
        {
            var major = header[3];
            var flags = header[5];
            var size = Syncsafe(header[6..10]);
            audioStart = 10 + size + ((flags & 0x10) != 0 ? 10 : 0);
            ReadId3v2(stream, info, major, flags, size, includeCoverArt);
        }

        if (info.Title is null && stream.Length >= 128 + audioStart)
            ReadId3v1(stream, info);

        // First MPEG frame after the tag (some encoders leave junk or padding before it).
        var window = new byte[(int)Math.Min(64 * 1024, Math.Max(0, stream.Length - audioStart))];
        var read = ReadAt(stream, audioStart, window);
        for (var i = 0; i + 4 <= read; i++)
        {
            if (!IsMpegFrame(window.AsSpan(i)))
                continue;
            if (TryReadMpegFrame(window.AsSpan(i, read - i), stream.Length - audioStart - i, info))
                return;
        }
    }

    private static bool IsMpegFrame(ReadOnlySpan<byte> b)
        => b.Length >= 4 && b[0] == 0xFF && (b[1] & 0xE0) == 0xE0 && ((b[1] >> 3) & 3) != 1 && ((b[1] >> 1) & 3) != 0
           && (b[2] >> 4) is > 0 and < 15 && ((b[2] >> 2) & 3) != 3;

    private static bool TryReadMpegFrame(ReadOnlySpan<byte> frame, long audioBytes, Builder info)
    {
        var versionBits = (frame[1] >> 3) & 3; // 0 = 2.5, 2 = 2, 3 = 1
        var layer = 4 - ((frame[1] >> 1) & 3);  // 1, 2, 3
        var bitrateIndex = frame[2] >> 4;
        var rateIndex = (frame[2] >> 2) & 3;
        var mono = (frame[3] >> 6) == 3;
        var mpeg1 = versionBits == 3;

        var bitrate = (mpeg1, layer) switch
        {
            (true, 1) => Mpeg1Layer1[bitrateIndex],
            (true, 2) => Mpeg1Layer2[bitrateIndex],
            (true, _) => Mpeg1Layer3[bitrateIndex],
            (false, 1) => Mpeg2Layer1[bitrateIndex],
            _ => Mpeg2Layer23[bitrateIndex],
        };
        int[] rates = versionBits switch { 3 => [44100, 48000, 32000], 2 => [22050, 24000, 16000], _ => [11025, 12000, 8000] };
        var sampleRate = rates[rateIndex];
        var samplesPerFrame = layer == 1 ? 384 : layer == 2 || mpeg1 ? 1152 : 576;

        info.SampleRate = sampleRate;
        info.Channels = mono ? 1 : 2;
        info.AudioCodec = "MPEG-" + (versionBits switch { 3 => "1", 2 => "2", _ => "2.5" }) + " Layer " + (layer == 1 ? "I" : layer == 2 ? "II" : "III");

        // VBR header: Xing/Info after the side information, or VBRI 32 bytes after the frame header.
        var sideInfo = mpeg1 ? (mono ? 17 : 32) : (mono ? 9 : 17);
        long frames = 0, bytes = 0;
        var xing = 4 + sideInfo;
        if (frame.Length >= xing + 16 && (frame.Slice(xing, 4).SequenceEqual("Xing"u8) || frame.Slice(xing, 4).SequenceEqual("Info"u8)))
        {
            var flags = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(xing + 4, 4));
            var offset = xing + 8;
            if ((flags & 1) != 0)
            {
                frames = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(offset, 4));
                offset += 4;
            }

            if ((flags & 2) != 0 && frame.Length >= offset + 4)
                bytes = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(offset, 4));
        }
        else if (frame.Length >= 36 + 18 && frame.Slice(36, 4).SequenceEqual("VBRI"u8))
        {
            bytes = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(36 + 10, 4));
            frames = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(36 + 14, 4));
        }

        if (frames > 0)
        {
            var seconds = (double)frames * samplesPerFrame / sampleRate;
            info.Duration = TimeSpan.FromSeconds(seconds);
            info.Bitrate = seconds > 0 ? (int)((bytes > 0 ? bytes : audioBytes) * 8 / seconds / 1000) : bitrate;
        }
        else if (bitrate > 0)
        {
            info.Bitrate = bitrate;
            info.Duration = TimeSpan.FromSeconds(audioBytes * 8.0 / (bitrate * 1000.0));
        }

        return true;
    }

    private static void ReadId3v2(Stream stream, Builder info, byte major, byte flags, int size, bool includeCoverArt)
    {
        if (major is < 2 or > 4)
            return;
        long position = 10;
        var end = Math.Min(10L + size, stream.Length);
        Span<byte> frameHeader = stackalloc byte[10];
        if ((flags & 0x40) != 0 && major >= 3)
        {
            ReadAt(stream, position, frameHeader[..4]);
            position += major == 4 ? Syncsafe(frameHeader[..4]) : BinaryPrimitives.ReadInt32BigEndian(frameHeader[..4]) + 4;
        }

        var unsynchronized = (flags & 0x80) != 0 && major < 4;
        var headerLength = major == 2 ? 6 : 10;
        while (position + headerLength <= end)
        {
            var frame = frameHeader[..headerLength];
            if (ReadAt(stream, position, frame) < headerLength || frame[0] == 0)
                break; // padding

            string id;
            int length;
            var dataLengthIndicator = false;
            if (major == 2)
            {
                id = Encoding.ASCII.GetString(frame[..3]);
                length = (frame[3] << 16) | (frame[4] << 8) | frame[5];
            }
            else
            {
                id = Encoding.ASCII.GetString(frame[..4]);
                length = major == 4 ? Syncsafe(frame[4..8]) : BinaryPrimitives.ReadInt32BigEndian(frame[4..8]);
                dataLengthIndicator = major == 4 && (frame[9] & 0x01) != 0;
            }

            if (length <= 0 || position + headerLength + length > end)
                break;

            var dataPosition = position + headerLength + (dataLengthIndicator ? 4 : 0);
            var dataLength = length - (dataLengthIndicator ? 4 : 0);
            if (id is "APIC" or "PIC")
            {
                if (includeCoverArt && info.CoverArt is null && dataLength <= MaxCoverArtBytes)
                    info.CoverArt = ParseId3Picture(Unsynchronize(ReadBytes(stream, dataPosition, dataLength), unsynchronized), id == "PIC");
            }
            else if (id[0] == 'T' && dataLength <= MaxTextFrameBytes && Id3Field(id) is { } field)
            {
                var text = DecodeId3Text(Unsynchronize(ReadBytes(stream, dataPosition, dataLength), unsynchronized));
                if (!string.IsNullOrWhiteSpace(text))
                    info.SetTag(field, text);
            }

            position += headerLength + length;
        }
    }

    private static string? Id3Field(string id) => id switch
    {
        "TIT2" or "TT2" => "TITLE",
        "TPE1" or "TP1" => "ARTIST",
        "TALB" or "TAL" => "ALBUM",
        "TYER" or "TYE" or "TDRC" => "DATE",
        "TRCK" or "TRK" => "TRACKNUMBER",
        "TCON" or "TCO" => "GENRE",
        _ => null,
    };

    private static void ReadId3v1(Stream stream, Builder info)
    {
        Span<byte> tag = stackalloc byte[128];
        if (ReadAt(stream, stream.Length - 128, tag) < 128 || !tag.StartsWith("TAG"u8))
            return;
        info.SetTag("TITLE", Latin1(tag[3..33]));
        info.SetTag("ARTIST", Latin1(tag[33..63]));
        info.SetTag("ALBUM", Latin1(tag[63..93]));
        info.SetTag("DATE", Latin1(tag[93..97]));
    }

    private static string DecodeId3Text(byte[] data)
    {
        if (data.Length < 2)
            return string.Empty;
        var body = data.AsSpan(1);
        var text = data[0] switch
        {
            1 => body.Length >= 2 && body[0] == 0xFE && body[1] == 0xFF ? Encoding.BigEndianUnicode.GetString(body[2..]) : Encoding.Unicode.GetString(body.Length >= 2 && body[0] == 0xFF && body[1] == 0xFE ? body[2..] : body),
            2 => Encoding.BigEndianUnicode.GetString(body),
            3 => Encoding.UTF8.GetString(body),
            _ => Encoding.Latin1.GetString(body),
        };

        // ID3v2.4 separates multiple values with NUL: the first one is shown.
        var nul = text.IndexOf('\0', StringComparison.Ordinal);
        return (nul >= 0 ? text[..nul] : text).Trim();
    }

    private static byte[]? ParseId3Picture(byte[] data, bool v22)
    {
        if (data.Length < 4)
            return null;
        var encoding = data[0];
        var position = 1;
        if (v22)
        {
            position += 3; // image format ("JPG")
        }
        else
        {
            while (position < data.Length && data[position] != 0)
                position++; // MIME type
            position++;
        }

        position++; // picture type
        var wide = encoding is 1 or 2;
        while (position < data.Length)
        {
            if (wide)
            {
                if (position + 1 < data.Length && data[position] == 0 && data[position + 1] == 0)
                {
                    position += 2;
                    break;
                }

                position += 2;
            }
            else if (data[position++] == 0)
            {
                break;
            }
        }

        return position < data.Length && IsImage(data.AsSpan(position)) ? data[position..] : null;
    }

    private static byte[] Unsynchronize(byte[] data, bool apply)
    {
        if (!apply || data.AsSpan().IndexOf((ReadOnlySpan<byte>)[0xFF, 0x00]) < 0)
            return data;
        var result = new List<byte>(data.Length);
        for (var i = 0; i < data.Length; i++)
        {
            result.Add(data[i]);
            if (data[i] == 0xFF && i + 1 < data.Length && data[i + 1] == 0)
                i++;
        }

        return [.. result];
    }

    private static int Syncsafe(ReadOnlySpan<byte> b) => ((b[0] & 0x7F) << 21) | ((b[1] & 0x7F) << 14) | ((b[2] & 0x7F) << 7) | (b[3] & 0x7F);

    // ── FLAC ────────────────────────────────────────────────────

    private static void ReadFlac(Stream stream, Builder info, bool includeCoverArt)
    {
        info.Format = "FLAC";
        info.AudioCodec = "FLAC";
        long position = 4;
        Span<byte> header = stackalloc byte[4];
        for (var blocks = 0; blocks < 128 && position + 4 <= stream.Length; blocks++)
        {
            ReadAt(stream, position, header);
            var last = (header[0] & 0x80) != 0;
            var type = header[0] & 0x7F;
            var length = (header[1] << 16) | (header[2] << 8) | header[3];
            var data = position + 4;
            if (type == 0 && length >= 18)
            {
                var block = ReadBytes(stream, data, 18);
                var sampleRate = (block[10] << 12) | (block[11] << 4) | (block[12] >> 4);
                var channels = ((block[12] >> 1) & 7) + 1;
                var bits = (((block[12] & 1) << 4) | (block[13] >> 4)) + 1;
                var samples = ((long)(block[13] & 0xF) << 32) | ((long)block[14] << 24) | ((long)block[15] << 16) | ((long)block[16] << 8) | block[17];
                info.SampleRate = sampleRate;
                info.Channels = channels;
                info.BitsPerSample = bits;
                if (sampleRate > 0 && samples > 0)
                    info.Duration = TimeSpan.FromSeconds((double)samples / sampleRate);
            }
            else if (type == 4 && length <= MaxCommentBlockBytes)
            {
                ReadVorbisComments(ReadBytes(stream, data, length), info);
            }
            else if (type == 6 && includeCoverArt && length <= MaxCoverArtBytes)
            {
                var picture = ParseFlacPicture(ReadBytes(stream, data, length), out var pictureType);
                if (picture is not null && (info.CoverArt is null || pictureType == 3))
                    info.CoverArt = picture;
            }

            position = data + length;
            if (last)
                break;
        }

        info.SetAverageBitrate(stream.Length - position);
    }

    private static byte[]? ParseFlacPicture(byte[] data, out uint pictureType)
    {
        pictureType = 0;
        if (data.Length < 32)
            return null;
        pictureType = BinaryPrimitives.ReadUInt32BigEndian(data);
        var position = 4;
        var mimeLength = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position));
        position += 4 + mimeLength;
        if (position + 4 > data.Length)
            return null;
        var descriptionLength = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position));
        position += 4 + descriptionLength + 16;
        if (position + 4 > data.Length || position < 0)
            return null;
        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position));
        position += 4;
        return length > 0 && position + length <= data.Length && IsImage(data.AsSpan(position, length)) ? data[position..(position + length)] : null;
    }

    private static void ReadVorbisComments(ReadOnlySpan<byte> block, Builder info)
    {
        if (block.Length < 8)
            return;
        var vendor = (int)BinaryPrimitives.ReadUInt32LittleEndian(block);
        var position = 4 + vendor;
        if (vendor < 0 || position + 4 > block.Length)
            return;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(block[position..]);
        position += 4;
        for (var i = 0; i < count && i < 1024 && position + 4 <= block.Length; i++)
        {
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(block[position..]);
            position += 4;
            if (length < 0 || position + length > block.Length)
                return;
            var comment = Encoding.UTF8.GetString(block.Slice(position, length));
            position += length;
            var equals = comment.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
                info.SetTag(comment[..equals].ToUpperInvariant(), comment[(equals + 1)..].Trim());
        }
    }

    // ── RIFF: WAV and AVI ───────────────────────────────────────

    private static void ReadRiff(Stream stream, Builder info, string format)
    {
        info.Format = format;
        var riff = new RiffState();
        WalkRiff(stream, 12, Math.Min(stream.Length, 8L + ReadUInt32Le(stream, 4)), info, riff, depth: 0);
        if (format == "WAV")
        {
            if (riff.ByteRate > 0 && riff.DataBytes > 0)
            {
                info.Duration = TimeSpan.FromSeconds((double)riff.DataBytes / riff.ByteRate);
                info.Bitrate = (int)(riff.ByteRate * 8L / 1000);
            }
        }
        else
        {
            info.HasVideo = info.Width is > 0;
            if (riff.MicrosecondsPerFrame > 0 && riff.TotalFrames > 0)
            {
                info.Duration = TimeSpan.FromMicroseconds((double)riff.MicrosecondsPerFrame * riff.TotalFrames);
                info.SetAverageBitrate(stream.Length);
            }
        }
    }

    private sealed class RiffState
    {
        public long ByteRate;
        public long DataBytes;
        public long MicrosecondsPerFrame;
        public long TotalFrames;
        public string? StreamType;
    }

    private static void WalkRiff(Stream stream, long position, long end, Builder info, RiffState state, int depth)
    {
        Span<byte> header = stackalloc byte[12];
        for (var chunks = 0; chunks < MaxAtoms && position + 8 <= end; chunks++)
        {
            if (ReadAt(stream, position, header[..8]) < 8)
                return;
            var id = Encoding.ASCII.GetString(header[..4]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
            var data = position + 8;
            var dataEnd = Math.Min(end, data + size);
            switch (id)
            {
                case "LIST" when depth < 3 && dataEnd - data >= 4:
                    ReadAt(stream, data, header[..4]);
                    var listType = Encoding.ASCII.GetString(header[..4]);
                    if (listType is "hdrl" or "strl" or "INFO")
                        WalkRiff(stream, data + 4, dataEnd, info, state, depth + 1);
                    break;
                case "fmt " when size >= 16:
                    var format = ReadBytes(stream, data, 16);
                    info.Channels = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(2));
                    info.SampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4));
                    state.ByteRate = BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(8));
                    info.BitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(14));
                    info.AudioCodec = WaveFormatName(BinaryPrimitives.ReadUInt16LittleEndian(format));
                    break;
                case "data":
                    state.DataBytes = size == uint.MaxValue ? stream.Length - data : size;
                    break;
                case "avih" when size >= 40:
                    var avih = ReadBytes(stream, data, 40);
                    state.MicrosecondsPerFrame = BinaryPrimitives.ReadUInt32LittleEndian(avih);
                    state.TotalFrames = BinaryPrimitives.ReadUInt32LittleEndian(avih.AsSpan(16));
                    info.Width = (int)BinaryPrimitives.ReadUInt32LittleEndian(avih.AsSpan(32));
                    info.Height = (int)BinaryPrimitives.ReadUInt32LittleEndian(avih.AsSpan(36));
                    break;
                case "strh" when size >= 8:
                    var strh = ReadBytes(stream, data, 8);
                    state.StreamType = Encoding.ASCII.GetString(strh, 0, 4);
                    if (state.StreamType == "vids")
                        info.VideoCodec ??= FourCcName(Encoding.ASCII.GetString(strh, 4, 4));
                    break;
                case "strf" when state.StreamType == "auds" && size >= 8:
                    var wave = ReadBytes(stream, data, 8);
                    info.AudioCodec ??= WaveFormatName(BinaryPrimitives.ReadUInt16LittleEndian(wave));
                    info.Channels ??= BinaryPrimitives.ReadUInt16LittleEndian(wave.AsSpan(2));
                    info.SampleRate ??= (int)BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(4));
                    break;
                case "INAM" or "IART" or "IPRD" or "ICRD" or "IGNR" or "ITRK" or "IPRT" when size <= MaxTextFrameBytes:
                    var text = Encoding.UTF8.GetString(ReadBytes(stream, data, (int)size)).TrimEnd('\0').Trim();
                    info.SetTag(id switch { "INAM" => "TITLE", "IART" => "ARTIST", "IPRD" => "ALBUM", "ICRD" => "DATE", "IGNR" => "GENRE", _ => "TRACKNUMBER" }, text);
                    break;
            }

            position = data + size + (size & 1);
        }
    }

    private static string WaveFormatName(ushort tag) => tag switch
    {
        0x0001 => "PCM",
        0x0003 => "PCM (float)",
        0x0006 => "A-law",
        0x0007 => "µ-law",
        0x0011 => "IMA ADPCM",
        0x0055 => "MP3",
        0x00FF or 0x1610 => "AAC",
        0x2000 => "AC-3",
        0xFFFE => "PCM (extensible)",
        _ => "0x" + tag.ToString("X4", System.Globalization.CultureInfo.InvariantCulture),
    };

    // ── Ogg (Vorbis, Opus) ──────────────────────────────────────

    private static void ReadOgg(Stream stream, Builder info)
    {
        var buffer = new byte[(int)Math.Min(64 * 1024, stream.Length)];
        var read = ReadAt(stream, 0, buffer);
        var packets = new List<byte[]>(2);
        var current = new List<byte>();
        uint serial = 0;
        var position = 0;
        while (packets.Count < 2 && position + 27 <= read && buffer.AsSpan(position, 4).SequenceEqual("OggS"u8))
        {
            var pageSerial = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(position + 14));
            if (position == 0)
                serial = pageSerial;
            var segments = buffer[position + 26];
            var payload = position + 27 + segments;
            if (payload > read)
                break;
            for (var s = 0; s < segments && packets.Count < 2; s++)
            {
                var lace = buffer[position + 27 + s];
                if (pageSerial == serial && payload + lace <= read && current.Count < MaxCommentBlockBytes)
                    current.AddRange(buffer.AsSpan(payload, lace).ToArray());
                payload += lace;
                if (lace < 255 && pageSerial == serial)
                {
                    packets.Add([.. current]);
                    current.Clear();
                }
            }

            position = payload;
        }

        if (packets.Count == 0)
            return;

        var identification = packets[0].AsSpan();
        double rate = 0;
        long preSkip = 0;
        if (identification.Length >= 30 && identification[..7].SequenceEqual("\x01vorbis"u8))
        {
            info.Format = "Ogg Vorbis";
            info.AudioCodec = "Vorbis";
            info.Channels = identification[11];
            info.SampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(identification[12..]);
            rate = info.SampleRate ?? 0;
        }
        else if (identification.Length >= 19 && identification[..8].SequenceEqual("OpusHead"u8))
        {
            info.Format = "Opus";
            info.AudioCodec = "Opus";
            info.Channels = identification[9];
            preSkip = BinaryPrimitives.ReadUInt16LittleEndian(identification[10..]);
            info.SampleRate = 48000;
            rate = 48000;
        }
        else
        {
            info.Format = "Ogg";
            return;
        }

        if (packets.Count > 1)
        {
            var comments = packets[1].AsSpan();
            if (comments.Length > 7 && comments[..7].SequenceEqual("\x03vorbis"u8))
                ReadVorbisComments(comments[7..], info);
            else if (comments.Length > 8 && comments[..8].SequenceEqual("OpusTags"u8))
                ReadVorbisComments(comments[8..], info);
        }

        // Duration: granule position of the stream's last page.
        var tailLength = (int)Math.Min(64 * 1024, stream.Length);
        var tail = new byte[tailLength];
        var tailRead = ReadAt(stream, stream.Length - tailLength, tail);
        for (var i = tailRead - 27; i >= 0; i--)
        {
            if (tail[i] != 'O' || !tail.AsSpan(i, 4).SequenceEqual("OggS"u8) || BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + 14)) != serial)
                continue;
            var granule = BinaryPrimitives.ReadInt64LittleEndian(tail.AsSpan(i + 6));
            if (granule > preSkip && rate > 0)
            {
                info.Duration = TimeSpan.FromSeconds((granule - preSkip) / rate);
                info.SetAverageBitrate(stream.Length);
            }

            break;
        }
    }

    // ── MP4 / QuickTime ─────────────────────────────────────────

    private sealed class Mp4State
    {
        public int Atoms;
        public long Timescale;
        public long DurationUnits;
        public Mp4Track? Track;
        public readonly List<Mp4Track> Tracks = [];
    }

    private sealed class Mp4Track
    {
        public string? Handler;
        public string? Codec;
        public int Width;
        public int Height;
        public int Channels;
        public int SampleRate;
    }

    private static void ReadMp4(Stream stream, Builder info, bool includeCoverArt)
    {
        info.Format = "MPEG-4";
        Span<byte> head = stackalloc byte[12];
        ReadAt(stream, 0, head);
        if (head[4..8].SequenceEqual("ftyp"u8))
        {
            var brand = Encoding.ASCII.GetString(head[8..12]);
            info.Format = brand switch
            {
                "qt  " => "QuickTime",
                "M4A " or "M4B " or "M4P " => "MPEG-4 Audio",
                _ when brand.StartsWith("3g", StringComparison.Ordinal) => "3GPP",
                _ => "MPEG-4",
            };
        }

        var state = new Mp4State();
        WalkMp4(stream, 0, stream.Length, "", state, info, includeCoverArt);

        if (state.Timescale > 0 && state.DurationUnits > 0)
        {
            info.Duration = TimeSpan.FromSeconds((double)state.DurationUnits / state.Timescale);
            info.SetAverageBitrate(stream.Length);
        }

        foreach (var track in state.Tracks)
        {
            if (track.Handler == "vide" && info.VideoCodec is null)
            {
                info.HasVideo = true;
                info.VideoCodec = FourCcName(track.Codec);
                if (track.Width > 0)
                {
                    info.Width = track.Width;
                    info.Height = track.Height;
                }
            }
            else if (track.Handler == "soun" && info.AudioCodec is null)
            {
                info.AudioCodec = FourCcName(track.Codec);
                info.Channels = track.Channels > 0 ? track.Channels : null;
                info.SampleRate = track.SampleRate > 0 ? track.SampleRate : null;
            }
        }
    }

    private static void WalkMp4(Stream stream, long position, long end, string parent, Mp4State state, Builder info, bool includeCoverArt)
    {
        Span<byte> header = stackalloc byte[16];
        while (position + 8 <= end && state.Atoms++ < MaxAtoms)
        {
            if (ReadAt(stream, position, header[..8]) < 8)
                return;
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Encoding.Latin1.GetString(header[4..8]);
            var headerLength = 8;
            if (size == 1)
            {
                if (ReadAt(stream, position + 8, header[8..16]) < 8)
                    return;
                size = BinaryPrimitives.ReadInt64BigEndian(header[8..16]);
                headerLength = 16;
            }
            else if (size == 0)
            {
                size = end - position;
            }

            if (size < headerLength)
                return;
            var data = position + headerLength;
            var atomEnd = Math.Min(end, position + size);

            switch (type)
            {
                case "moov" or "mdia" or "minf" or "stbl" or "udta" or "ilst":
                    WalkMp4(stream, data, atomEnd, type, state, info, includeCoverArt);
                    break;
                case "trak":
                    state.Track = new Mp4Track();
                    state.Tracks.Add(state.Track);
                    WalkMp4(stream, data, atomEnd, type, state, info, includeCoverArt);
                    state.Track = null;
                    break;
                case "meta":
                    // ISO meta is a full box (4 bytes version/flags before its children); QuickTime's is not.
                    ReadAt(stream, data, header[..8]);
                    var children = BinaryPrimitives.ReadUInt32BigEndian(header) == 0 ? data + 4 : data;
                    WalkMp4(stream, children, atomEnd, type, state, info, includeCoverArt);
                    break;
                case "mvhd":
                    var mvhd = ReadBytes(stream, data, (int)Math.Min(32, atomEnd - data));
                    if (mvhd.Length >= 20 && mvhd[0] == 0)
                    {
                        state.Timescale = BinaryPrimitives.ReadUInt32BigEndian(mvhd.AsSpan(12));
                        state.DurationUnits = BinaryPrimitives.ReadUInt32BigEndian(mvhd.AsSpan(16));
                    }
                    else if (mvhd.Length >= 32)
                    {
                        state.Timescale = BinaryPrimitives.ReadUInt32BigEndian(mvhd.AsSpan(20));
                        state.DurationUnits = BinaryPrimitives.ReadInt64BigEndian(mvhd.AsSpan(24));
                    }

                    break;
                case "tkhd" when state.Track is not null:
                    // Width and height (16.16 fixed point) close the box: after 76 bytes in version 0, 88 in version 1.
                    var tkhd = ReadBytes(stream, data, (int)Math.Min(96, atomEnd - data));
                    var sizeOffset = tkhd.Length > 0 && tkhd[0] == 1 ? 88 : 76;
                    if (sizeOffset + 8 <= tkhd.Length)
                    {
                        state.Track.Width = (int)(BinaryPrimitives.ReadUInt32BigEndian(tkhd.AsSpan(sizeOffset)) >> 16);
                        state.Track.Height = (int)(BinaryPrimitives.ReadUInt32BigEndian(tkhd.AsSpan(sizeOffset + 4)) >> 16);
                    }

                    break;
                case "hdlr" when state.Track is not null && parent == "mdia":
                    var hdlr = ReadBytes(stream, data, (int)Math.Min(12, atomEnd - data));
                    if (hdlr.Length >= 12)
                        state.Track.Handler = Encoding.Latin1.GetString(hdlr, 8, 4);
                    break;
                case "stsd" when state.Track is not null:
                    var stsd = ReadBytes(stream, data, (int)Math.Min(48, atomEnd - data));
                    if (stsd.Length >= 16)
                    {
                        state.Track.Codec = Encoding.Latin1.GetString(stsd, 12, 4);
                        if (state.Track.Handler == "soun" && stsd.Length >= 44)
                        {
                            state.Track.Channels = BinaryPrimitives.ReadUInt16BigEndian(stsd.AsSpan(8 + 24));
                            state.Track.SampleRate = (int)(BinaryPrimitives.ReadUInt32BigEndian(stsd.AsSpan(8 + 32)) >> 16);
                        }
                        else if (state.Track.Handler == "vide" && stsd.Length >= 44 && state.Track.Width == 0)
                        {
                            state.Track.Width = BinaryPrimitives.ReadUInt16BigEndian(stsd.AsSpan(8 + 32));
                            state.Track.Height = BinaryPrimitives.ReadUInt16BigEndian(stsd.AsSpan(8 + 34));
                        }
                    }

                    break;
                default:
                    if (parent == "ilst")
                        ReadIlstItem(stream, type, data, atomEnd, info, includeCoverArt);
                    break;
            }

            position += size;
        }
    }

    private static void ReadIlstItem(Stream stream, string type, long data, long end, Builder info, bool includeCoverArt)
    {
        Span<byte> header = stackalloc byte[16];
        if (end - data < 16 || ReadAt(stream, data, header) < 16 || !header[4..8].SequenceEqual("data"u8))
            return;
        var length = (long)BinaryPrimitives.ReadUInt32BigEndian(header) - 16;
        var kind = BinaryPrimitives.ReadUInt32BigEndian(header[8..12]) & 0xFFFFFF;
        var payload = data + 16;
        if (length <= 0 || payload + length > end)
            return;

        if (type == "covr")
        {
            if (includeCoverArt && info.CoverArt is null && length <= MaxCoverArtBytes)
            {
                var picture = ReadBytes(stream, payload, (int)length);
                if (IsImage(picture))
                    info.CoverArt = picture;
            }

            return;
        }

        if (type == "trkn" && length >= 6)
        {
            var track = ReadBytes(stream, payload, 6);
            var number = BinaryPrimitives.ReadUInt16BigEndian(track.AsSpan(2));
            var total = BinaryPrimitives.ReadUInt16BigEndian(track.AsSpan(4));
            if (number > 0)
                info.SetTag("TRACKNUMBER", total > 0 ? $"{number}/{total}" : number.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return;
        }

        var field = type switch
        {
            "©nam" => "TITLE",
            "©ART" => "ARTIST",
            "aART" when info.Artist is null => "ARTIST",
            "©alb" => "ALBUM",
            "©day" => "DATE",
            "©gen" => "GENRE",
            _ => null,
        };
        if (field is not null && kind == 1 && length <= MaxTextFrameBytes)
            info.SetTag(field, Encoding.UTF8.GetString(ReadBytes(stream, payload, (int)length)));
    }

    // ── Matroska / WebM ─────────────────────────────────────────

    private static void ReadMatroska(Stream stream, Builder info)
    {
        info.Format = "Matroska";
        var position = 0L;
        var elements = 0;
        long scale = 1_000_000;
        double duration = 0;
        while (position < stream.Length && elements++ < MaxAtoms)
        {
            if (!TryReadEbmlHeader(stream, position, out var id, out var size, out var headerLength))
                return;
            var data = position + headerLength;
            var end = size < 0 ? stream.Length : Math.Min(stream.Length, data + size);
            if (id == 0x1A45DFA3)
            {
                foreach (var (childId, childData, childEnd) in EbmlChildren(stream, data, end))
                {
                    if (childId == 0x4282 && ReadEbmlString(stream, childData, childEnd) == "webm")
                        info.Format = "WebM";
                }
            }
            else if (id == 0x18538067)
            {
                foreach (var (childId, childData, childEnd) in EbmlChildren(stream, data, end))
                {
                    if (childId == 0x1F43B675)
                        break; // first cluster: the headers are behind us
                    if (childId == 0x1549A966)
                    {
                        foreach (var (field, fieldData, fieldEnd) in EbmlChildren(stream, childData, childEnd))
                        {
                            if (field == 0x2AD7B1)
                                scale = (long)ReadEbmlUInt(stream, fieldData, fieldEnd);
                            else if (field == 0x4489)
                                duration = ReadEbmlFloat(stream, fieldData, fieldEnd);
                            else if (field == 0x7BA9)
                                info.SetTag("TITLE", ReadEbmlString(stream, fieldData, fieldEnd));
                        }
                    }
                    else if (childId == 0x1654AE6B)
                    {
                        foreach (var (entry, entryData, entryEnd) in EbmlChildren(stream, childData, childEnd))
                        {
                            if (entry == 0xAE)
                                ReadMatroskaTrack(stream, entryData, entryEnd, info);
                        }
                    }
                }

                break;
            }

            position = end;
        }

        if (duration > 0 && scale > 0)
        {
            info.Duration = TimeSpan.FromMilliseconds(duration * scale / 1_000_000.0);
            info.SetAverageBitrate(stream.Length);
        }
    }

    private static void ReadMatroskaTrack(Stream stream, long data, long end, Builder info)
    {
        ulong type = 0;
        string? codec = null;
        int width = 0, height = 0, channels = 0;
        double rate = 0;
        foreach (var (id, fieldData, fieldEnd) in EbmlChildren(stream, data, end))
        {
            switch (id)
            {
                case 0x83:
                    type = ReadEbmlUInt(stream, fieldData, fieldEnd);
                    break;
                case 0x86:
                    codec = ReadEbmlString(stream, fieldData, fieldEnd);
                    break;
                case 0xE0:
                    foreach (var (video, videoData, videoEnd) in EbmlChildren(stream, fieldData, fieldEnd))
                    {
                        if (video == 0xB0)
                            width = (int)ReadEbmlUInt(stream, videoData, videoEnd);
                        else if (video == 0xBA)
                            height = (int)ReadEbmlUInt(stream, videoData, videoEnd);
                    }

                    break;
                case 0xE1:
                    foreach (var (audio, audioData, audioEnd) in EbmlChildren(stream, fieldData, fieldEnd))
                    {
                        if (audio == 0xB5)
                            rate = ReadEbmlFloat(stream, audioData, audioEnd);
                        else if (audio == 0x9F)
                            channels = (int)ReadEbmlUInt(stream, audioData, audioEnd);
                    }

                    break;
            }
        }

        if (type == 1 && info.VideoCodec is null)
        {
            info.HasVideo = true;
            info.VideoCodec = MatroskaCodecName(codec);
            info.Width = width > 0 ? width : null;
            info.Height = height > 0 ? height : null;
        }
        else if (type == 2 && info.AudioCodec is null)
        {
            info.AudioCodec = MatroskaCodecName(codec);
            info.Channels = channels > 0 ? channels : 1;
            info.SampleRate = rate > 0 ? (int)rate : null;
        }
    }

    private static IEnumerable<(long Id, long Data, long End)> EbmlChildren(Stream stream, long position, long end)
    {
        for (var count = 0; position < end && count < MaxAtoms; count++)
        {
            if (!TryReadEbmlHeader(stream, position, out var id, out var size, out var headerLength))
                yield break;
            var data = position + headerLength;
            var childEnd = size < 0 ? end : Math.Min(end, data + size);
            yield return (id, data, childEnd);
            position = childEnd;
        }
    }

    private static bool TryReadEbmlHeader(Stream stream, long position, out long id, out long size, out int headerLength)
    {
        id = 0;
        size = 0;
        headerLength = 0;
        Span<byte> buffer = stackalloc byte[12];
        var read = ReadAt(stream, position, buffer);
        if (read < 2)
            return false;
        var idLength = VintLength(buffer[0]);
        if (idLength is 0 or > 4 || idLength >= read)
            return false;
        for (var i = 0; i < idLength; i++)
            id = (id << 8) | buffer[i];
        var sizeLength = VintLength(buffer[idLength]);
        if (sizeLength == 0 || idLength + sizeLength > read)
            return false;
        long value = buffer[idLength] & (0xFF >> sizeLength);
        var allOnes = value == (0xFF >> sizeLength);
        for (var i = 1; i < sizeLength; i++)
        {
            value = (value << 8) | buffer[idLength + i];
            allOnes &= buffer[idLength + i] == 0xFF;
        }

        size = allOnes ? -1 : value;
        headerLength = idLength + sizeLength;
        return true;
    }

    private static int VintLength(byte first)
    {
        for (var i = 0; i < 8; i++)
        {
            if ((first & (0x80 >> i)) != 0)
                return i + 1;
        }

        return 0;
    }

    private static ulong ReadEbmlUInt(Stream stream, long data, long end)
    {
        var length = (int)Math.Clamp(end - data, 0, 8);
        Span<byte> buffer = stackalloc byte[8];
        ReadAt(stream, data, buffer[..length]);
        ulong value = 0;
        for (var i = 0; i < length; i++)
            value = (value << 8) | buffer[i];
        return value;
    }

    private static double ReadEbmlFloat(Stream stream, long data, long end)
    {
        Span<byte> buffer = stackalloc byte[8];
        return (end - data) switch
        {
            4 => ReadAt(stream, data, buffer[..4]) == 4 ? BinaryPrimitives.ReadSingleBigEndian(buffer) : 0,
            8 => ReadAt(stream, data, buffer) == 8 ? BinaryPrimitives.ReadDoubleBigEndian(buffer) : 0,
            _ => 0,
        };
    }

    private static string ReadEbmlString(Stream stream, long data, long end)
        => end - data is > 0 and <= 1024 ? Encoding.UTF8.GetString(ReadBytes(stream, data, (int)(end - data))).TrimEnd('\0') : string.Empty;

    private static string? MatroskaCodecName(string? id) => id switch
    {
        null => null,
        "V_MPEG4/ISO/AVC" => "H.264",
        "V_MPEGH/ISO/HEVC" => "HEVC",
        "V_VP8" => "VP8",
        "V_VP9" => "VP9",
        "V_AV1" => "AV1",
        "V_MPEG2" => "MPEG-2",
        "A_OPUS" => "Opus",
        "A_VORBIS" => "Vorbis",
        "A_FLAC" => "FLAC",
        "A_AC3" => "AC-3",
        "A_EAC3" => "E-AC-3",
        "A_DTS" => "DTS",
        "A_MPEG/L3" => "MP3",
        _ when id.StartsWith("A_AAC", StringComparison.Ordinal) => "AAC",
        _ when id.StartsWith("A_PCM", StringComparison.Ordinal) => "PCM",
        _ => id,
    };

    private static string? FourCcName(string? fourCc) => fourCc?.Trim() switch
    {
        null or "" => null,
        "avc1" or "avc3" or "H264" or "h264" or "X264" or "x264" => "H.264",
        "hvc1" or "hev1" or "HEVC" => "HEVC",
        "av01" => "AV1",
        "vp09" => "VP9",
        "vp08" => "VP8",
        "mp4v" or "XVID" or "xvid" or "DIVX" or "divx" or "DX50" or "FMP4" => "MPEG-4 Part 2",
        "mp4a" => "AAC",
        "alac" => "ALAC",
        "ac-3" => "AC-3",
        "ec-3" => "E-AC-3",
        "Opus" => "Opus",
        "fLaC" => "FLAC",
        ".mp3" => "MP3",
        "MJPG" or "mjpa" or "jpeg" => "Motion JPEG",
        _ when fourCc.StartsWith("ap", StringComparison.Ordinal) => "Apple ProRes",
        var other => other,
    };

    // ── Shared helpers ──────────────────────────────────────────

    private static bool IsImage(ReadOnlySpan<byte> data)
        => (data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8) || (data.Length > 8 && data[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]));

    private static string Latin1(ReadOnlySpan<byte> data) => Encoding.Latin1.GetString(data).TrimEnd('\0', ' ').Trim();

    private static uint ReadUInt32Le(Stream stream, long position)
    {
        Span<byte> buffer = stackalloc byte[4];
        return ReadAt(stream, position, buffer) == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(buffer) : 0;
    }

    private static int ReadAt(Stream stream, long position, Span<byte> buffer)
    {
        if (position < 0 || position >= stream.Length)
            return 0;
        stream.Position = position;
        return stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
    }

    private static byte[] ReadBytes(Stream stream, long position, int count)
    {
        if (count <= 0)
            return [];
        var buffer = new byte[(int)Math.Min(count, Math.Max(0, stream.Length - position))];
        var read = ReadAt(stream, position, buffer);
        return read == buffer.Length ? buffer : buffer[..read];
    }

    private sealed class Builder(long fileLength)
    {
        public string Format = string.Empty;
        public bool HasVideo;
        public TimeSpan? Duration;
        public int? Width;
        public int? Height;
        public string? VideoCodec;
        public string? AudioCodec;
        public int? SampleRate;
        public int? Channels;
        public int? BitsPerSample;
        public int? Bitrate;
        public string? Title;
        public string? Artist;
        public string? Album;
        public string? Year;
        public string? Track;
        public string? Genre;
        public byte[]? CoverArt;

        public void SetTag(string field, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            value = value.Trim();
            switch (field)
            {
                case "TITLE": Title ??= value; break;
                case "ARTIST": Artist ??= value; break;
                case "ALBUM": Album ??= value; break;
                case "DATE" or "YEAR": Year ??= value.Length > 10 ? value[..10] : value; break;
                case "TRACKNUMBER": Track ??= value; break;
                case "GENRE": Genre ??= value; break;
            }
        }

        public void SetAverageBitrate(long bytes)
        {
            if (Bitrate is null && Duration is { TotalSeconds: > 0 } duration && bytes > 0)
                Bitrate = (int)(bytes * 8 / duration.TotalSeconds / 1000);
        }

        public MediaInfo Build() => new()
        {
            Format = Format,
            HasVideo = HasVideo,
            Duration = Duration is { Ticks: > 0 } d && d < TimeSpan.FromDays(7) ? d : null,
            Width = Width is > 0 and < 65536 ? Width : null,
            Height = Height is > 0 and < 65536 ? Height : null,
            VideoCodec = VideoCodec,
            AudioCodec = AudioCodec,
            SampleRate = SampleRate is > 0 ? SampleRate : null,
            Channels = Channels is > 0 and < 64 ? Channels : null,
            BitsPerSample = BitsPerSample is > 0 and <= 64 ? BitsPerSample : null,
            Bitrate = Bitrate is > 0 && Bitrate < 1_000_000 && fileLength > 0 ? Bitrate : null,
            Title = Title,
            Artist = Artist,
            Album = Album,
            Year = Year,
            Track = Track,
            Genre = Genre,
            CoverArt = CoverArt,
        };
    }
}
