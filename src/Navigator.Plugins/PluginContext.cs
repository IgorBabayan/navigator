using Navigator.Core.Platform;

namespace Navigator.Plugins;

/// <summary>One plugin's view of the host. Collects the plugin's contributions so they can be withdrawn together.</summary>
internal sealed class PluginContext(string pluginId, string dataDirectory, IPluginHostServices services, Action contributionsChanged) : IPluginContext
{
    private readonly Lock _gate = new();
    private readonly List<IPluginSidebarSection> _sections = [];
    private readonly List<IRemoteFileSystem> _fileSystems = [];
    private readonly List<IFolderDecorator> _decorators = [];
    private readonly List<IItemMenuProvider> _menus = [];
    private readonly List<ISearchProvider> _searches = [];
    private IPluginSettingsPage? _settingsPage;

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

    public IPluginSecretStore Secrets { get; } = new PluginSecretStore(pluginId, services.Secrets);

    public bool OpenInBrowser(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return IsAttached && address.IsAbsoluteUri && address.Scheme == Uri.UriSchemeHttps && services.OpenInBrowser(address);
    }

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
        Register(() => _sections.Add(section));
    }

    public void AddFileSystem(IRemoteFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        Register(() => _fileSystems.Add(fileSystem));
    }

    public void AddFolderDecorator(IFolderDecorator decorator)
    {
        ArgumentNullException.ThrowIfNull(decorator);
        Register(() => _decorators.Add(decorator));
    }

    public void AddMenuProvider(IItemMenuProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Register(() => _menus.Add(provider));
    }

    public void AddSearchProvider(ISearchProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Register(() => _searches.Add(provider));
    }

    public void SetSettingsPage(IPluginSettingsPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        Register(() => _settingsPage = page);
    }

    /// <summary>Everything the plugin registered so far, copied under the lock.</summary>
    public ContextContributions Snapshot()
    {
        lock (_gate)
            return new ContextContributions([.. _sections], [.. _fileSystems], [.. _decorators], [.. _menus], [.. _searches], _settingsPage);
    }

    public void Detach()
    {
        lock (_gate)
        {
            IsAttached = false;
            _sections.Clear();
            _fileSystems.Clear();
            _decorators.Clear();
            _menus.Clear();
            _searches.Clear();
            _settingsPage = null;
        }
    }

    private void Register(Action add)
    {
        lock (_gate)
        {
            if (!IsAttached)
                return;
            add();
        }

        if (IsLive)
            contributionsChanged();
    }
}

/// <summary>One plugin's contributions at one moment.</summary>
internal sealed record ContextContributions(
    IPluginSidebarSection[] Sections,
    IRemoteFileSystem[] FileSystems,
    IFolderDecorator[] Decorators,
    IItemMenuProvider[] MenuProviders,
    ISearchProvider[] SearchProviders,
    IPluginSettingsPage? SettingsPage);
