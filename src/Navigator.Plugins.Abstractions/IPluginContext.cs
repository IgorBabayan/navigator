using Navigator.Core.Platform;

namespace Navigator.Plugins;

/// <summary>What the host offers a plugin. One instance per plugin; valid until the plugin is disposed.</summary>
public interface IPluginContext
{
    string PluginId { get; }

    /// <summary>Private, existing folder for the plugin's own settings (kept when the plugin is updated, deleted when it is removed).</summary>
    string DataDirectory { get; }

    /// <summary>Current UI language code ("en", "uk"). Changes at run time; read it when text is needed.</summary>
    string Language { get; }

    IPluginUi Ui { get; }

    IPluginLog Log { get; }

    IVolumeProvider Volumes { get; }

    IPlatformInfo Platform { get; }

    /// <summary>
    /// Secrets that must survive a restart (e.g. OAuth refresh tokens), kept by the operating system's credential store
    /// (Windows DPAPI, macOS Keychain, Secret Service on Linux) and scoped to this plugin (plugin API 1.2).
    /// </summary>
    IPluginSecretStore Secrets { get; }

    /// <summary>
    /// Opens an https:// address in the user's default browser (e.g. an OAuth sign-in page). Returns false when no browser
    /// could be started or the address is not https (plugin API 1.2).
    /// </summary>
    bool OpenInBrowser(Uri address);

    /// <summary>Shared UI vocabulary of the host ("Plugin.*" keys, e.g. "Plugin.Remote.Add"), already localized.</summary>
    string GetHostString(string key);

    /// <summary>Name, detail line and icons of a volume exactly as Navigator shows it on Home and in the sidebar.</summary>
    VolumeDisplay DescribeVolume(VolumeInfo volume);

    void AddSidebarSection(IPluginSidebarSection section);

    void AddFileSystem(IRemoteFileSystem fileSystem);
}

public sealed record VolumeDisplay(string Name, string? Detail, string IconKey, string? SystemIconKey);

/// <summary>Writes to Navigator's log, prefixed with the plugin id. Never log passwords or file contents.</summary>
public interface IPluginLog
{
    void Info(string message);

    void Warning(string message, Exception? exception = null);

    void Error(string message, Exception? exception = null);
}

/// <summary>
/// A plugin's persistent secrets. Values are never written to disk in plain text and never logged. Every method may be
/// called from any thread; calls may start a short helper process (macOS, Linux), so call them off the UI thread.
/// </summary>
public interface IPluginSecretStore
{
    /// <summary>
    /// False when the system offers no credential store (e.g. Linux without a Secret Service): <see cref="SetAsync"/>
    /// then keeps values in memory for the running session only.
    /// </summary>
    bool IsPersistent { get; }

    /// <summary>The secret stored under <paramref name="name"/>, or null.</summary>
    Task<string?> GetAsync(string name, CancellationToken cancellationToken);

    /// <summary>Stores or replaces a secret. Returns false when it could only be kept for this session.</summary>
    Task<bool> SetAsync(string name, string secret, CancellationToken cancellationToken);

    Task RemoveAsync(string name, CancellationToken cancellationToken);
}
