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
