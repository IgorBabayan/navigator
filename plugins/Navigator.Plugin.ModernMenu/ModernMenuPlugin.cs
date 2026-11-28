using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Navigator.Plugins;

namespace Navigator.Plugin.ModernMenu;

/// <summary>
/// Windows 11 app commands in Navigator's context menus. Windows 10 is left alone: its classic menu (behind "Show more
/// options") already hosts these commands. Initialization only registers the provider.
/// </summary>
public sealed class ModernMenuPlugin : INavigatorPlugin, IDisposable
{
    internal const string EnabledPreference = "enabled";
    private const int Windows11Build = 22000;

    private ModernMenuProvider? _provider;

    public void Initialize(IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, Windows11Build) || !Environment.Is64BitProcess)
            return;
        var text = new PluginStrings(typeof(ModernMenuPlugin).Assembly, () => context.Language);
        var preferences = new PluginPreferences(() => context.DataDirectory);
        var catalog = new PackagedVerbCatalog();
        var provider = new ModernMenuProvider(catalog, preferences, context.Log);
        _provider = provider;
        context.AddMenuProvider(provider);
        context.SetSettingsPage(new ModernMenuSettingsPage(catalog, preferences, text));
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
            _provider?.Dispose();
    }
}

/// <summary>
/// The app commands for a right-click: verbs registered for the clicked items' types, created and described on the
/// COM worker. The package catalog is read on a worker thread too (the first menu after a package change).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ModernMenuProvider(PackagedVerbCatalog catalog, PluginPreferences preferences, IPluginLog log) : IItemMenuProvider, IDisposable
{
    private readonly Lazy<ComWorker> _worker = new(() => new ComWorker());

    public string Id => "win11-menu";

    public async Task<IReadOnlyList<PluginMenuItem>> GetItemsAsync(PluginMenuRequest request, CancellationToken menuLifetime)
    {
        if (!preferences.GetBool(ModernMenuPlugin.EnabledPreference, true))
            return [];
        var targets = request.Targets;
        if (targets.Count == 0 || targets.Count > 64 || targets.Any(p => p.StartsWith(@"\\", StringComparison.Ordinal) && !p.StartsWith(@"\\wsl", StringComparison.OrdinalIgnoreCase)))
            return []; // like Explorer: no app commands for huge selections; network items would wake slow servers

        var (verbs, kinds) = await Task.Run(() =>
        {
            var all = catalog.GetVerbs();
            var described = targets.Select(p => (p, request.IsBackground || Directory.Exists(p))).ToList();
            return (all, described);
        }, menuLifetime).ConfigureAwait(false);
        var matching = PackagedVerbManifest.Matching(verbs, kinds, request.IsBackground);
        if (matching.Count == 0)
            return [];
        try
        {
            return await ExplorerCommandSession.BuildAsync(_worker.Value, matching, targets, menuLifetime).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or COMException)
        {
            log.Warning("App commands could not be listed", ex);
            return [];
        }
    }

    public void Dispose()
    {
        if (_worker.IsValueCreated)
            _worker.Value.Dispose();
    }
}

/// <summary>Settings → Plugins → Windows 11 app commands.</summary>
[SupportedOSPlatform("windows")]
internal sealed class ModernMenuSettingsPage : IPluginSettingsPage
{
    private readonly PackagedVerbCatalog _catalog;
    private readonly PluginPreferences _preferences;
    private readonly PluginStrings _text;
    private int? _count;

    public ModernMenuSettingsPage(PackagedVerbCatalog catalog, PluginPreferences preferences, PluginStrings text)
    {
        _catalog = catalog;
        _preferences = preferences;
        _text = text;
        _preferences.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    public IReadOnlyList<PluginSetting> GetSettings()
    {
        if (_count is null)
        {
            _count = -1;
            _ = Task.Run(() =>
            {
                _count = _catalog.GetVerbs().Select(v => v.Package).Distinct(StringComparer.Ordinal).Count();
                Changed?.Invoke(this, EventArgs.Empty);
            });
        }

        return
        [
            new PluginSetting(ModernMenuPlugin.EnabledPreference, PluginSettingKind.Toggle, _text["ModernMenu.Settings.Enabled"],
                _count is > 0 and var apps ? _text.Format("ModernMenu.Settings.Found", apps) : _text["ModernMenu.Settings.EnabledHint"])
            {
                IsOn = _preferences.GetBool(ModernMenuPlugin.EnabledPreference, true),
            },
        ];
    }

    public Task ApplyAsync(string settingId, bool? value, CancellationToken cancellationToken)
    {
        if (settingId == ModernMenuPlugin.EnabledPreference && value is { } on)
            _preferences.SetBool(ModernMenuPlugin.EnabledPreference, on);
        return Task.CompletedTask;
    }
}
