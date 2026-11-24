using System.Globalization;

namespace Navigator.Core.Formatting;

/// <summary>Human-readable sizes using binary multiples (1 KB = 1024 bytes), matching desktop file managers.</summary>
public static class ByteSize
{
    private static readonly string[] Units = ["KB", "MB", "GB", "TB", "PB", "EB"];

    public static string Format(long bytes, IFormatProvider? provider = null, string bytesLabel = "bytes", string byteLabel = "byte")
    {
        provider ??= CultureInfo.CurrentCulture;
        if (bytes < 0)
            return string.Empty;
        if (bytes == 1)
            return "1 " + byteLabel;
        if (bytes < 1024)
            return bytes.ToString(provider) + " " + bytesLabel;

        double value = bytes;
        var unit = -1;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // Three significant digits: 1.23 MB, 12.3 MB, 123 MB
        var format = value < 10 ? "0.##" : value < 100 ? "0.#" : "0";
        return value.ToString(format, provider) + " " + Units[unit];
    }

    /// <summary>Formats bytes/second ("12.4 MB/s").</summary>
    public static string FormatRate(double bytesPerSecond, IFormatProvider? provider = null)
        => bytesPerSecond <= 0 ? string.Empty : Format((long)bytesPerSecond, provider) + "/s";
}
