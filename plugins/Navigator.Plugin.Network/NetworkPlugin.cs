using Navigator.Core.Platform;
using Navigator.Plugins;

namespace Navigator.Plugin.Network;

/// <summary>Built-in, permanent plugin that contributes the sidebar's Network section.</summary>
public sealed class NetworkPlugin : INavigatorPlugin, IDisposable
{
    // Identity used by the host to describe this built-in plugin (it has no plugin.json: it ships inside the app,
    // carries the app's version and is updated together with Navigator).
    public const string Id = "network";
    public const string Name = "Network";
    public const string Description = "Computers and shared folders on the local network, and mapped network drives.";
    public const string IconKey = "DriveNetwork";

    private NetworkSection? _section;

    public void Initialize(IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _section = new NetworkSection(context);
        context.AddSidebarSection(_section);
        if (OperatingSystem.IsWindows())
            context.AddFileSystem(new WindowsNetworkFileSystem(context));
    }

    public void Dispose() => _section?.Dispose();
}

/// <summary>
/// Mounted network volumes (SMB/NFS/WebDAV/SSHFS shares, mapped network drives). Hidden while there are none.
/// Listing reuses the shared volume snapshot, so it adds no extra drive queries to a sidebar refresh.
/// </summary>
internal sealed class NetworkSection : IPluginSidebarSection, IDisposable
{
    private readonly IPluginContext _context;

    public NetworkSection(IPluginContext context)
    {
        _context = context;
        _context.Volumes.VolumesChanged += OnVolumesChanged;
    }

    /// <summary>Same id as before plugins existed, so the remembered collapsed state still applies.</summary>
    public string Id => NetworkPlugin.Id;

    public string Title => _context.GetHostString("Sidebar.Network");

    public string IconKey => "DriveNetwork";

    public string? SystemIconKey => Navigator.Core.Platform.SystemIconKey.Stock(SystemIconKind.Network);

    public int Order => 100;

    /// <summary>On Windows the section always has its "Network" entry; elsewhere it shows only while shares are mounted.</summary>
    public bool ShowWhenEmpty => false;

    public IReadOnlyList<PluginCommand> Commands => [];

    public event EventHandler? Changed;

    public ValueTask<IReadOnlyList<PluginSidebarEntry>> GetEntriesAsync(CancellationToken cancellationToken)
    {
        var entries = new List<PluginSidebarEntry>();
        if (OperatingSystem.IsWindows())
        {
            // Like File Explorer: browse the computers on the local network and their shared folders.
            entries.Add(new PluginSidebarEntry("network-computers", _context.GetHostString("Sidebar.Network"), "DriveNetwork",
                Navigator.Core.Navigation.Location.Remote(WindowsNetworkFileSystem.RootAddress), SystemIconKey: Navigator.Core.Platform.SystemIconKey.Stock(SystemIconKind.Network)));
        }

        foreach (var volume in _context.Volumes.GetVolumes())
        {
            if (volume.Kind != VolumeKind.Network)
                continue;
            var display = _context.DescribeVolume(volume);
            entries.Add(new PluginSidebarEntry(volume.RootPath, display.Name, display.IconKey, Navigator.Core.Navigation.Location.Folder(volume.RootPath), display.Detail, display.SystemIconKey));
        }

        return ValueTask.FromResult<IReadOnlyList<PluginSidebarEntry>>(entries);
    }

    public Task ExecuteAsync(string commandId, PluginSidebarEntry? entry, CancellationToken cancellationToken) => Task.CompletedTask;

    private void OnVolumesChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose() => _context.Volumes.VolumesChanged -= OnVolumesChanged;
}
