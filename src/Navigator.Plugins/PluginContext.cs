using Navigator.Core.Platform;

namespace Navigator.Plugins;

/// <summary>One plugin's view of the host. Collects the plugin's contributions so they can be withdrawn together.</summary>
internal sealed class PluginContext(string pluginId, string dataDirectory, IPluginHostServices services, Action contributionsChanged) : IPluginContext
{
    private readonly Lock _gate = new();
    private readonly List<IPluginSidebarSection> _sections = [];
    private readonly List<IRemoteFileSystem> _fileSystems = [];

    public string PluginId { get; } = pluginId;

    public string DataDirectory
    {
        get
        {
            Directory.CreateDirectory(dataDirectory);
            return dataDirectory;
        }
    }

    public string Language => services.Language;

    public IPluginUi Ui => services.Ui;

    public IPluginLog Log { get; } = services.CreateLog(pluginId);

    public IVolumeProvider Volumes => services.Volumes;

    public IPlatformInfo Platform => services.Platform;

    /// <summary>False after the plugin was removed: late registrations are ignored.</summary>
    public bool IsAttached { get; private set; } = true;

    /// <summary>
    /// Set by the host once <see cref="INavigatorPlugin.Initialize"/> returned. Registrations made during initialization
    /// are published in one go afterwards instead of one sidebar rebuild each.
    /// </summary>
    public bool IsLive { get; set; }

    public string GetHostString(string key) => services.GetString(key);

    public VolumeDisplay DescribeVolume(VolumeInfo volume) => services.DescribeVolume(volume);

    public void AddSidebarSection(IPluginSidebarSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        lock (_gate)
        {
            if (!IsAttached)
                return;
            _sections.Add(section);
        }

        if (IsLive)
            contributionsChanged();
    }

    public void AddFileSystem(IRemoteFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        lock (_gate)
        {
            if (!IsAttached)
                return;
            _fileSystems.Add(fileSystem);
        }

        if (IsLive)
            contributionsChanged();
    }

    public (IPluginSidebarSection[] Sections, IRemoteFileSystem[] FileSystems) Snapshot()
    {
        lock (_gate)
            return ([.. _sections], [.. _fileSystems]);
    }

    public void Detach()
    {
        lock (_gate)
        {
            IsAttached = false;
            _sections.Clear();
            _fileSystems.Clear();
        }
    }
}
