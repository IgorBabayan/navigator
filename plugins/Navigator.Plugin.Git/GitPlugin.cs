using Navigator.Plugins;

namespace Navigator.Plugin.Git;

/// <summary>
/// Git for local folders: branch in the status bar (Pull, Push, Fetch), Git columns and status badges, and a "Git"
/// sub-menu in the background menu of a repository folder. Initialization only registers contributions: git runs the
/// first time a folder inside a repository is shown.
/// </summary>
public sealed class GitPlugin : INavigatorPlugin, IDisposable
{
    internal const string BadgesPreference = "badges";

    private static PluginStrings? _text;
    private GitDecorator? _decorator;

    /// <summary>The plugin's strings (set in <see cref="Initialize"/>).</summary>
    internal static PluginStrings Text
    {
        get => _text ?? throw new InvalidOperationException("The Git plugin is not initialized.");
        set => _text = value;
    }

    public void Initialize(IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _text = new PluginStrings(typeof(GitPlugin).Assembly, () => context.Language);
        var preferences = new PluginPreferences(() => context.DataDirectory);
        var client = new GitClient();
        var locator = new GitRepositoryLocator();
        _decorator = new GitDecorator(client, locator, context, preferences);
        context.AddFolderDecorator(_decorator);
        context.AddMenuProvider(new GitMenuProvider(_decorator, locator, client));
        context.SetSettingsPage(new GitSettingsPage(client, preferences));
    }

    public void Dispose() => _decorator?.Dispose();
}

/// <summary>"Git ▸ Pull / Push / Fetch" in the background menu of a folder inside a repository.</summary>
internal sealed class GitMenuProvider(GitDecorator decorator, GitRepositoryLocator locator, GitClient client) : IItemMenuProvider
{
    public string Id => "git";

    public Task<IReadOnlyList<PluginMenuItem>> GetItemsAsync(PluginMenuRequest request, CancellationToken menuLifetime)
    {
        if (!request.IsBackground || request.FolderPath is not { } folder || client.Executable is null || locator.Find(folder) is not { } repository)
            return Task.FromResult<IReadOnlyList<PluginMenuItem>>([]);

        PluginMenuItem Operation(GitOperation operation, string icon) => new(GitPlugin.Text["Git.Operation." + operation])
        {
            IconKey = icon,
            Invoke = token => decorator.RunAsync(repository, operation, token),
        };

        IReadOnlyList<PluginMenuItem> items =
        [
            new PluginMenuItem(GitPlugin.Text["Git.Menu"])
            {
                IconKey = "Git",
                Children = [Operation(GitOperation.Pull, "Download"), Operation(GitOperation.Push, "Upload"), Operation(GitOperation.Fetch, "Refresh")],
            },
        ];
        return Task.FromResult(items);
    }
}

/// <summary>Settings → Plugins → Git: where git was found, and the badge switch.</summary>
internal sealed class GitSettingsPage : IPluginSettingsPage
{
    private readonly GitClient _client;
    private readonly PluginPreferences _preferences;
    private string? _version;
    private bool _probing;

    public GitSettingsPage(GitClient client, PluginPreferences preferences)
    {
        _client = client;
        _preferences = preferences;
        _preferences.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    public IReadOnlyList<PluginSetting> GetSettings()
    {
        if (_version is null && !_probing)
        {
            _probing = true;
            _ = ProbeAsync();
        }

        var found = _client.Executable is { } path
            ? GitPlugin.Text.Format("Git.Settings.Found", _version ?? "git", path)
            : GitPlugin.Text["Git.NotInstalled"];
        return
        [
            new PluginSetting("git", PluginSettingKind.Info, GitPlugin.Text["Git.Settings.Executable"], found),
            new PluginSetting(GitPlugin.BadgesPreference, PluginSettingKind.Toggle, GitPlugin.Text["Git.Settings.Badges"], GitPlugin.Text["Git.Settings.BadgesHint"])
            {
                IsOn = _preferences.GetBool(GitPlugin.BadgesPreference, true),
            },
        ];
    }

    public Task ApplyAsync(string settingId, bool? value, CancellationToken cancellationToken)
    {
        if (settingId == GitPlugin.BadgesPreference && value is { } on)
            _preferences.SetBool(GitPlugin.BadgesPreference, on);
        return Task.CompletedTask;
    }

    private async Task ProbeAsync()
    {
        _version = await Task.Run(() => _client.GetVersionAsync(CancellationToken.None)).ConfigureAwait(false) ?? string.Empty;
        if (_version.Length > 0)
            Changed?.Invoke(this, EventArgs.Empty);
    }
}
