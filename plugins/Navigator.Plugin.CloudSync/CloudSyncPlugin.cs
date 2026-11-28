using System.Runtime.CompilerServices;
using Navigator.Core.Navigation;
using Navigator.Core.Platform;
using Navigator.Plugins;

namespace Navigator.Plugin.CloudSync;

/// <summary>
/// Cloud sync folders: the sync folders of OneDrive, Dropbox, iCloud Drive, Google Drive and others in the sidebar's
/// Cloud group, and their items' sync state as icon badges and in a "Status" column. Initialization only registers
/// contributions; sync folders are looked up when the sidebar or a folder needs them.
/// </summary>
public sealed class CloudSyncPlugin : INavigatorPlugin, IDisposable
{
    internal const string SidebarPreference = "sidebar";
    internal const string BadgesPreference = "badges";

    private SyncFoldersSection? _section;

    public void Initialize(IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var text = new PluginStrings(typeof(CloudSyncPlugin).Assembly, () => context.Language);
        var preferences = new PluginPreferences(() => context.DataDirectory);
        var locator = new SyncRootLocator();
        _section = new SyncFoldersSection(context, locator, preferences, text);
        var decorator = new CloudSyncDecorator(locator, preferences, text);
        context.AddSidebarSection(_section);
        context.AddFolderDecorator(decorator);
        context.SetSettingsPage(new CloudSyncSettingsPage(locator, preferences, text));
    }

    public void Dispose() => _section?.Dispose();
}

/// <summary>The sync folders, in the sidebar's Cloud group (before the cloud accounts of other plugins).</summary>
internal sealed class SyncFoldersSection : IPluginSidebarSection, IPluginSidebarGroupMember, IDisposable
{
    private readonly IPluginContext _context;
    private readonly SyncRootLocator _locator;
    private readonly PluginPreferences _preferences;
    private readonly PluginStrings _text;

    public SyncFoldersSection(IPluginContext context, SyncRootLocator locator, PluginPreferences preferences, PluginStrings text)
    {
        _context = context;
        _locator = locator;
        _preferences = preferences;
        _text = text;
        // New drives and mounts are also when sync clients tend to appear or move: cheap to look again then.
        _context.Volumes.VolumesChanged += OnVolumesChanged;
        _preferences.Changed += OnPreferencesChanged;
    }

    public string Id => "cloudsync";

    public string GroupId => PluginSidebarGroups.Cloud;

    public string Title => _text["CloudSync.Section"];

    public string IconKey => "CloudSync";

    public string? SystemIconKey => null;

    public int Order => 5;

    public bool ShowWhenEmpty => false;

    public IReadOnlyList<PluginCommand> Commands => [];

    public event EventHandler? Changed;

    public ValueTask<IReadOnlyList<PluginSidebarEntry>> GetEntriesAsync(CancellationToken cancellationToken)
    {
        if (!_preferences.GetBool(CloudSyncPlugin.SidebarPreference, true))
            return ValueTask.FromResult<IReadOnlyList<PluginSidebarEntry>>([]);
        var entries = new List<PluginSidebarEntry>();
        foreach (var root in _locator.GetRoots())
        {
            entries.Add(new PluginSidebarEntry("sync:" + root.Path, root.DisplayName, IconOf(root.Provider), Location.Folder(root.Path), root.Path,
                // The shell draws a sync root with its client's icon (OneDrive's cloud, Dropbox's box…).
                OperatingSystem.IsWindows() ? Navigator.Core.Platform.SystemIconKey.ForPath(root.Path) : null));
        }

        return ValueTask.FromResult<IReadOnlyList<PluginSidebarEntry>>(entries);
    }

    public Task ExecuteAsync(string commandId, PluginSidebarEntry? entry, CancellationToken cancellationToken) => Task.CompletedTask;

    internal static string IconOf(CloudProvider provider) => provider switch
    {
        CloudProvider.OneDrive => "MicrosoftOnedrive",
        CloudProvider.Dropbox => "Dropbox",
        CloudProvider.ICloud => "AppleIcloud",
        CloudProvider.GoogleDrive => "GoogleDrive",
        _ => "Cloud",
    };

    private void OnVolumesChanged(object? sender, EventArgs e)
    {
        _locator.Invalidate();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnPreferencesChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        _context.Volumes.VolumesChanged -= OnVolumesChanged;
        _preferences.Changed -= OnPreferencesChanged;
    }
}

/// <summary>Sync state of the items of a sync folder: a badge on the icon and the "Status" column, like File Explorer.</summary>
internal sealed class CloudSyncDecorator : IFolderDecorator
{
    public const string StatusColumn = "status";

    private readonly SyncRootLocator _locator;
    private readonly PluginPreferences _preferences;
    private readonly PluginStrings _text;

    public CloudSyncDecorator(SyncRootLocator locator, PluginPreferences preferences, PluginStrings text)
    {
        _locator = locator;
        _preferences = preferences;
        _text = text;
        _preferences.Changed += (_, _) => Changed?.Invoke(this, FolderDecorationsChangedEventArgs.Everywhere);
    }

    public string Id => "cloudsync";

    public IReadOnlyList<PluginColumn> Columns => [new(StatusColumn, _text["CloudSync.Column.Status"], 190)];

    public event EventHandler<FolderDecorationsChangedEventArgs>? Changed;

    public bool AppliesTo(string folderPath) => _locator.FindRoot(folderPath) is not null;

    public async IAsyncEnumerable<FolderDecoration> DecorateAsync(FolderDecorationRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield(); // macOS reads one lstat per item: never on the caller's thread
        var badges = _preferences.GetBool(CloudSyncPlugin.BadgesPreference, true);
        var texts = new Dictionary<SyncState, Dictionary<string, string>>();
        var items = new Dictionary<string, ItemDecoration>(request.Entries.Count, StringComparer.Ordinal);
        var checkedSoFar = 0;
        foreach (var entry in request.Entries)
        {
            if ((++checkedSoFar & 255) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var state = SyncStates.Of(entry);
            if (state == SyncState.None)
                continue;
            // One cell dictionary per state, shared by every item in that state.
            if (!texts.TryGetValue(state, out var cells))
                texts[state] = cells = new Dictionary<string, string>(1, StringComparer.Ordinal) { [StatusColumn] = _text["CloudSync.State." + state] };
            items[entry.Name] = new ItemDecoration { Badge = badges ? BadgeOf(state) : null, Cells = cells };
        }

        yield return new FolderDecoration { Items = items };
    }

    public Task ExecuteAsync(string commandId, string folderPath, CancellationToken cancellationToken) => Task.CompletedTask;

    private static readonly PluginBadge OnlineOnly = new("CloudOutline", "#2563EB");
    private static readonly PluginBadge Local = new("CheckCircleOutline", "#16A34A");
    private static readonly PluginBadge Always = new("CheckCircle", "#16A34A");
    private static readonly PluginBadge Downloading = new("CloudSync", "#2563EB");

    internal static PluginBadge? BadgeOf(SyncState state) => state switch
    {
        SyncState.OnlineOnly => OnlineOnly,
        SyncState.Local => Local,
        SyncState.AlwaysAvailable => Always,
        SyncState.Downloading => Downloading,
        _ => null,
    };
}

/// <summary>Settings → Plugins → Cloud sync folders.</summary>
internal sealed class CloudSyncSettingsPage : IPluginSettingsPage
{
    private readonly SyncRootLocator _locator;
    private readonly PluginPreferences _preferences;
    private readonly PluginStrings _text;

    public CloudSyncSettingsPage(SyncRootLocator locator, PluginPreferences preferences, PluginStrings text)
    {
        _locator = locator;
        _preferences = preferences;
        _text = text;
        _preferences.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    public IReadOnlyList<PluginSetting> GetSettings()
    {
        var roots = _locator.GetRoots();
        var found = roots.Count == 0 ? _text["CloudSync.Settings.NoneFound"] : string.Join(Environment.NewLine, roots.Select(r => $"{r.DisplayName} — {r.Path}"));
        return
        [
            new PluginSetting("found", PluginSettingKind.Info, _text["CloudSync.Settings.Found"], found),
            new PluginSetting(CloudSyncPlugin.SidebarPreference, PluginSettingKind.Toggle, _text["CloudSync.Settings.Sidebar"], _text["CloudSync.Settings.SidebarHint"])
            {
                IsOn = _preferences.GetBool(CloudSyncPlugin.SidebarPreference, true),
            },
            new PluginSetting(CloudSyncPlugin.BadgesPreference, PluginSettingKind.Toggle, _text["CloudSync.Settings.Badges"], _text["CloudSync.Settings.BadgesHint"])
            {
                IsOn = _preferences.GetBool(CloudSyncPlugin.BadgesPreference, true),
            },
            new PluginSetting("rescan", PluginSettingKind.Action, _text["CloudSync.Settings.Rescan"], _text["CloudSync.Settings.RescanHint"]) { ActionText = _text["CloudSync.Settings.RescanButton"] },
        ];
    }

    public Task ApplyAsync(string settingId, bool? value, CancellationToken cancellationToken)
    {
        switch (settingId)
        {
            case CloudSyncPlugin.SidebarPreference or CloudSyncPlugin.BadgesPreference when value is { } on:
                _preferences.SetBool(settingId, on);
                break;
            case "rescan":
                _locator.Invalidate();
                Changed?.Invoke(this, EventArgs.Empty);
                break;
        }

        return Task.CompletedTask;
    }
}
