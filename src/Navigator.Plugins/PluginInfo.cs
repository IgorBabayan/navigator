using Navigator.Core.Platform;

namespace Navigator.Plugins;

public enum PluginStatus
{
    /// <summary>Loaded and running.</summary>
    Active,

    /// <summary>Could not be loaded or initialized (see <see cref="PluginInfo.Error"/>).</summary>
    Failed,

    /// <summary>Built for another plugin contract version; update the plugin or the app.</summary>
    Incompatible,

    /// <summary>A newer version is installed and becomes active after a restart.</summary>
    UpdatePending,

    /// <summary>Removed; its files are deleted on the next start.</summary>
    Removed,
}

/// <summary>Snapshot of one plugin for the UI (plugin shop "Installed" list).</summary>
public sealed record PluginInfo(PluginManifest Manifest, bool IsBuiltIn, PluginStatus Status, string? Error = null, string? PendingVersion = null)
{
    public string Id => Manifest.Id;

    /// <summary>Permanent plugins ship with the app and cannot be removed (Network).</summary>
    public bool IsPermanent => IsBuiltIn;
}

/// <summary>A plugin compiled into the app: its manifest and a factory (no assembly scanning, no reflection).</summary>
public sealed record BuiltInPlugin(PluginManifest Manifest, Func<INavigatorPlugin> Create);

/// <summary>What the host needs from the application to give plugins their context. Implemented by the presentation layer.</summary>
public interface IPluginHostServices
{
    IPluginUi Ui { get; }

    IVolumeProvider Volumes { get; }

    IPlatformInfo Platform { get; }

    string Language { get; }

    string GetString(string key);

    VolumeDisplay DescribeVolume(VolumeInfo volume);

    IPluginLog CreateLog(string pluginId);
}
