using System.Buffers;
using Navigator.Core.Navigation;

namespace Navigator.Plugin.Network;

/// <summary>
/// Network addresses ↔ local share paths, without I/O: <c>network://computers/SERVER/share/sub</c> is the folder
/// <c>\\SERVER\share\sub</c>, and the parent of the share root <c>\\SERVER\share</c> is <c>network://computers/SERVER</c>.
/// </summary>
internal static class NetworkAddresses
{
    public const string Scheme = "network";
    public const string Host = "computers";
    public const string Root = Scheme + "://" + Host + "/";

    private static readonly SearchValues<char> InvalidServerCharacters = SearchValues.Create("\\/*?\"<>|");

    public static bool IsThisHost(RemoteAddress address)
        => string.Equals(address.Scheme, Scheme, StringComparison.OrdinalIgnoreCase) && string.Equals(address.Host, Host, StringComparison.OrdinalIgnoreCase);

    public static string[] Segments(RemoteAddress address) => address.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>NetBIOS/DNS names and IP addresses; nothing that could form a path.</summary>
    public static bool IsValidServer(string name)
        => name.Length is > 0 and <= 255 && name.AsSpan().IndexOfAny(InvalidServerCharacters) < 0 && name.Trim().Length == name.Length;

    /// <summary>The UNC folder for an address inside a share, or null (root and computer levels are plugin folders).</summary>
    public static string? ToSharePath(RemoteAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!IsThisHost(address))
            return null;
        var segments = Segments(address);
        return segments.Length >= 2 && IsValidServer(segments[0]) ? @"\\" + string.Join('\\', segments) : null;
    }

    /// <summary>"\\SERVER\share" (a share root, with or without a trailing separator) → "network://computers/SERVER".</summary>
    public static string? ServerOfShareRoot(string localPath)
    {
        if (string.IsNullOrEmpty(localPath) || !localPath.StartsWith(@"\\", StringComparison.Ordinal)
            || localPath.StartsWith(@"\\?\", StringComparison.Ordinal) || localPath.StartsWith(@"\\.\", StringComparison.Ordinal))
            return null;
        var parts = localPath[2..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && IsValidServer(parts[0]) ? Root + parts[0] : null;
    }
}
