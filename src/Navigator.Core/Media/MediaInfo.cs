namespace Navigator.Core.Media;

/// <summary>
/// Technical data and tags of an audio or video file, read from its headers only (see <see cref="MediaInfoReader"/>).
/// Every field is optional: a damaged or unusual file yields whatever could be read.
/// </summary>
public sealed record MediaInfo
{
    /// <summary>Container name for display ("MP3", "FLAC", "MP4", "Matroska"…).</summary>
    public string Format { get; init; } = string.Empty;

    public bool HasVideo { get; init; }

    public TimeSpan? Duration { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public string? VideoCodec { get; init; }

    public string? AudioCodec { get; init; }

    public int? SampleRate { get; init; }

    public int? Channels { get; init; }

    public int? BitsPerSample { get; init; }

    /// <summary>Average bit rate in kbit/s.</summary>
    public int? Bitrate { get; init; }

    public string? Title { get; init; }

    public string? Artist { get; init; }

    public string? Album { get; init; }

    public string? Year { get; init; }

    public string? Track { get; init; }

    public string? Genre { get; init; }

    /// <summary>Embedded picture (album art, poster) as an encoded JPEG/PNG; only filled when requested.</summary>
    public byte[]? CoverArt { get; init; }
}
