using System.Text.Json.Serialization;
using Navigator.Core.Navigation;

namespace Navigator.Plugins.Remote;

public enum RemoteAuthentication
{
    /// <summary>Password asked when connecting (kept in memory for the session only, never saved).</summary>
    Password,

    /// <summary>Private key file (SSH); its passphrase, if any, is asked when connecting.</summary>
    KeyFile,

    /// <summary>No credentials (anonymous FTP).</summary>
    Anonymous,
}

/// <summary>A saved server. Secrets are never part of it.</summary>
public sealed class RemoteConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public string Scheme { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    /// <summary>0 = the scheme's default port.</summary>
    public int Port { get; set; }

    public string? UserName { get; set; }

    public RemoteAuthentication Authentication { get; set; } = RemoteAuthentication.Password;

    public string? KeyFile { get; set; }

    /// <summary>Folder opened when the connection is clicked.</summary>
    public string InitialPath { get; set; } = "/";

    /// <summary>The address the sidebar entry navigates to.</summary>
    [JsonIgnore]
    public RemoteAddress Address => new(Scheme, string.IsNullOrWhiteSpace(UserName) ? null : UserName, Host.ToLowerInvariant(), Port > 0 ? Port : null, RemoteUri.NormalizePath(InitialPath));

    /// <summary>True when <paramref name="address"/> points at this server (same scheme, host, port and user).</summary>
    public bool Matches(RemoteAddress address, int defaultPort)
        => string.Equals(Scheme, address.Scheme, StringComparison.OrdinalIgnoreCase)
           && string.Equals(Host, address.Host, StringComparison.OrdinalIgnoreCase)
           && (Port > 0 ? Port : defaultPort) == (address.Port ?? defaultPort)
           && string.Equals(UserName ?? string.Empty, address.UserName ?? string.Empty, StringComparison.Ordinal);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(List<RemoteConnection>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class RemoteJsonContext : JsonSerializerContext;
