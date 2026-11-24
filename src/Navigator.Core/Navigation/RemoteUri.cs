using System.Globalization;
using System.Text;

namespace Navigator.Core.Navigation;

/// <summary>
/// Parsed form of a plugin address: <c>scheme://[user@]host[:port]/path</c>. The path is kept unescaped and always
/// starts with "/" (spaces and non-ASCII names are allowed as they are; only the user name is percent-decoded).
/// </summary>
public sealed record RemoteAddress(string Scheme, string? UserName, string Host, int? Port, string Path)
{
    /// <summary>"scheme://user@host:port" without the path; identifies one server connection.</summary>
    public string Authority
    {
        get
        {
            var builder = new StringBuilder(Scheme.Length + Host.Length + 16).Append(Scheme).Append("://");
            if (!string.IsNullOrEmpty(UserName))
                builder.Append(Uri.EscapeDataString(UserName)).Append('@');
            builder.Append(Host);
            if (Port is { } port)
                builder.Append(':').Append(port.ToString(CultureInfo.InvariantCulture));
            return builder.ToString();
        }
    }

    public bool IsRoot => Path == "/";

    /// <summary>The same server, another path.</summary>
    public RemoteAddress WithPath(string path) => this with { Path = RemoteUri.NormalizePath(path) };

    public override string ToString() => Authority + Path;
}

/// <summary>Helpers for plugin addresses. Everything is string based and allocation-light: no <see cref="Uri"/> escaping rules.</summary>
public static class RemoteUri
{
    /// <summary>True for "scheme://…" addresses other than file://. Windows drive paths ("C:\") never match.</summary>
    public static bool IsRemote(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        var separator = value.IndexOf("://", StringComparison.Ordinal);
        if (separator < 2 || separator > 16 || !char.IsAsciiLetter(value[0]))
            return false;
        for (var i = 1; i < separator; i++)
        {
            var c = value[i];
            if (!char.IsAsciiLetterOrDigit(c) && c is not '+' and not '-' and not '.')
                return false;
        }

        return !value.AsSpan(0, separator).Equals("file", StringComparison.OrdinalIgnoreCase) && value.Length > separator + 3;
    }

    public static bool TryParse(string? value, out RemoteAddress address)
    {
        address = null!;
        if (!IsRemote(value))
            return false;

        var separator = value!.IndexOf("://", StringComparison.Ordinal);
        var scheme = value[..separator].ToLowerInvariant();
        var rest = value[(separator + 3)..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var authority = slash < 0 ? rest : rest[..slash];
        var path = slash < 0 ? "/" : rest[slash..];

        string? user = null;
        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            user = Uri.UnescapeDataString(authority[..at]);
            authority = authority[(at + 1)..];
        }

        int? port = null;
        string host;
        if (authority.StartsWith('['))
        {
            // IPv6 literal: [::1]:22
            var close = authority.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
                return false;
            host = authority[..(close + 1)];
            if (close + 1 < authority.Length)
            {
                if (authority[close + 1] != ':' || !TryParsePort(authority[(close + 2)..], out var p))
                    return false;
                port = p;
            }
        }
        else
        {
            var colon = authority.LastIndexOf(':');
            host = colon < 0 ? authority : authority[..colon];
            if (colon >= 0)
            {
                if (!TryParsePort(authority[(colon + 1)..], out var p))
                    return false;
                port = p;
            }
        }

        if (host.Length == 0 || host.AsSpan().IndexOfAny(" \t/\\") >= 0)
            return false;

        address = new RemoteAddress(scheme, string.IsNullOrEmpty(user) ? null : user, host.ToLowerInvariant(), port, NormalizePath(path));
        return true;
    }

    public static RemoteAddress Parse(string value)
        => TryParse(value, out var address) ? address : throw new FormatException($"'{value}' is not a remote address.");

    /// <summary>Canonical text: lower-case scheme and host, single slashes, no "." / ".." segments, no trailing slash.</summary>
    public static string Normalize(string value) => TryParse(value, out var address) ? address.ToString() : value;

    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return "/";
        var segments = new List<string>();
        foreach (var segment in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;
            if (segment == "..")
            {
                if (segments.Count > 0)
                    segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        return segments.Count == 0 ? "/" : "/" + string.Join('/', segments);
    }

    /// <summary>Parent folder address, or null at the server root.</summary>
    public static string? GetParent(string value)
    {
        if (!TryParse(value, out var address) || address.IsRoot)
            return null;
        var slash = address.Path.LastIndexOf('/');
        return address.WithPath(slash <= 0 ? "/" : address.Path[..slash]).ToString();
    }

    public static string Combine(string folder, string name)
    {
        var address = Parse(folder);
        return address.WithPath(address.IsRoot ? "/" + name : address.Path + "/" + name).ToString();
    }

    /// <summary>Last path segment; the server ("user@host") at the root.</summary>
    public static string GetName(string value)
    {
        if (!TryParse(value, out var address))
            return value;
        if (address.IsRoot)
            return address.UserName is null ? address.Host : address.UserName + "@" + address.Host;
        return address.Path[(address.Path.LastIndexOf('/') + 1)..];
    }

    /// <summary>Breadcrumb segments from the server root to <paramref name="value"/>: (display name, address).</summary>
    public static IReadOnlyList<(string Name, string Address)> GetSegments(string value)
    {
        if (!TryParse(value, out var address))
            return [];
        var result = new List<(string, string)> { (GetName(address.WithPath("/").ToString()), address.WithPath("/").ToString()) };
        if (address.IsRoot)
            return result;
        var path = new StringBuilder(address.Path.Length);
        foreach (var segment in address.Path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path.Append('/').Append(segment);
            result.Add((segment, address.WithPath(path.ToString()).ToString()));
        }

        return result;
    }

    private static bool TryParsePort(string text, out int port)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;
}
