using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Navigator.Plugins;
using Navigator.Presentation.Localization;
using Navigator.Presentation.Services;
using Navigator.Updater;

namespace Navigator.Presentation.Plugins;

/// <summary>One plugin in the shop: installed (with its status) or available from the catalog.</summary>
public sealed class PluginRowViewModel : ObservableObject
{
    private string? _updateVersion;
    private readonly Func<string, string>? _updateText;

    internal PluginRowViewModel(string id, string name, string description, string author, string version, string iconKey, bool isBuiltIn, bool isInstalled, string statusText, bool isProblem,
        Func<string, string>? updateText = null)
    {
        _updateText = updateText;
        Id = id;
        Name = name;
        Description = description;
        Author = author;
        Version = version;
        IconKey = iconKey;
        IsBuiltIn = isBuiltIn;
        IsInstalled = isInstalled;
        StatusText = statusText;
        IsProblem = isProblem;
    }

    public string Id { get; }

    public string Name { get; }

    public string Description { get; }

    public string Author { get; }

    public string Version { get; }

    public string IconKey { get; }

    /// <summary>Ships with Navigator and cannot be removed (Network).</summary>
    public bool IsBuiltIn { get; }

    public bool IsInstalled { get; }

    public bool CanRemove => IsInstalled && !IsBuiltIn && !IsRemoved;

    public bool CanInstall => !IsInstalled;

    public string StatusText { get; }

    public bool HasStatus => StatusText.Length > 0;

    /// <summary>Failed / incompatible: shown in the error color.</summary>
    public bool IsProblem { get; }

    internal bool IsRemoved { get; init; }

    internal PluginCatalogEntry? CatalogEntry { get; init; }

    /// <summary>Newer version offered by the catalog, or null.</summary>
    public string? UpdateVersion
    {
        get => _updateVersion;
        internal set
        {
            if (!SetProperty(ref _updateVersion, value))
                return;
            OnPropertyChanged(nameof(HasUpdate));
            OnPropertyChanged(nameof(UpdateText));
        }
    }

    public bool HasUpdate => _updateVersion is not null;

    /// <summary>"Update to 1.2.0".</summary>
    public string UpdateText => _updateVersion is null || _updateText is null ? string.Empty : _updateText(_updateVersion);

    /// <summary>The plugin's own settings (plugin API 1.3), shown under the plugin; empty for most plugins.</summary>
    public ObservableCollection<PluginSettingViewModel> Settings { get; } = [];

    public bool HasSettings => Settings.Count > 0;

    internal void SetSettings(IEnumerable<PluginSettingViewModel> settings)
    {
        Settings.Clear();
        foreach (var setting in settings)
            Settings.Add(setting);
        OnPropertyChanged(nameof(HasSettings));
    }
}

/// <summary>One row of a plugin's settings page: a switch, a button or a line of text.</summary>
public sealed class PluginSettingViewModel : ObservableObject
{
    private readonly Func<string, bool?, Task<bool>> _apply;
    private bool _isOn;
    private bool _isBusy;

    internal PluginSettingViewModel(PluginSetting setting, Func<string, bool?, Task<bool>> apply)
    {
        _apply = apply;
        Id = setting.Id;
        Kind = setting.Kind;
        Title = setting.Title;
        Description = setting.Description ?? string.Empty;
        ActionText = setting.ActionText ?? setting.Title;
        IsEnabled = setting.IsEnabled;
        _isOn = setting.IsOn;
        RunCommand = new AsyncRelayCommand(RunAsync, () => !_isBusy && IsEnabled);
    }

    public string Id { get; }

    public PluginSettingKind Kind { get; }

    public string Title { get; }

    public string Description { get; }

    public bool HasDescription => Description.Length > 0;

    public string ActionText { get; }

    public bool IsEnabled { get; }

    public bool IsToggle => Kind == PluginSettingKind.Toggle;

    public bool IsAction => Kind == PluginSettingKind.Action;

    /// <summary>The switch; changing it applies the setting (a failure turns it back).</summary>
    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isBusy || !SetProperty(ref _isOn, value))
                return;
            _ = ToggleAsync(value);
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
                RunCommand.NotifyCanExecuteChanged();
        }
    }

    public IAsyncRelayCommand RunCommand { get; }

    private async Task ToggleAsync(bool value)
    {
        IsBusy = true;
        try
        {
            if (!await _apply(Id, value).ConfigureAwait(true))
            {
                _isOn = !value;
                OnPropertyChanged(nameof(IsOn));
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunAsync()
    {
        IsBusy = true;
        try
        {
            await _apply(Id, null).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>
/// Settings → Plugins: the plugin shop. Lists installed plugins (built-in ones are marked and cannot be removed)
/// and the plugins offered by the signed catalog; installs, updates and removes them. The catalog is fetched only
/// when the page is opened (or refreshed), never at startup.
/// </summary>
public sealed class PluginsViewModel : ObservableObject, IDisposable
{
    private readonly PluginHost _host;
    private readonly PluginStoreClient _store;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _dispatcher;
    private readonly Localizer _localizer;
    private readonly ILogger _logger;
    private readonly List<(IPluginSettingsPage Page, EventHandler Handler)> _pageSubscriptions = [];
    private PluginCatalogResult? _catalog;
    private CancellationTokenSource? _work;
    private bool _isBusy;
    private double _progress;
    private string _statusText = string.Empty;
    private string? _errorText;
    private bool _catalogRequested;

    public PluginsViewModel(PluginHost host, PluginStoreClient store, IDialogService dialogs, IUiDispatcher dispatcher, Localizer localizer, ILoggerFactory loggers)
    {
        _host = host;
        _store = store;
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        _localizer = localizer;
        _logger = loggers.CreateLogger<PluginsViewModel>();

        RefreshCommand = new AsyncRelayCommand(RefreshCatalogAsync, () => !IsBusy && _store.IsConfigured);
        InstallCommand = new AsyncRelayCommand<PluginRowViewModel>(row => InstallFromCatalogAsync(row), _ => !IsBusy);
        UpdateCommand = new AsyncRelayCommand<PluginRowViewModel>(row => InstallFromCatalogAsync(row), _ => !IsBusy);
        RemoveCommand = new AsyncRelayCommand<PluginRowViewModel>(RemoveAsync, _ => !IsBusy);
        InstallFromFileCommand = new AsyncRelayCommand(InstallFromFileAsync, () => !IsBusy);
        CancelCommand = new RelayCommand(() => _work?.Cancel());

        _host.Changed += OnHostChanged;
        RebuildRows();
    }

    public ObservableCollection<PluginRowViewModel> Installed { get; } = [];

    public ObservableCollection<PluginRowViewModel> Available { get; } = [];

    public bool HasAvailable => Available.Count > 0;

    public bool IsShopConfigured => _store.IsConfigured;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
                return;
            RefreshCommand.NotifyCanExecuteChanged();
            InstallCommand.NotifyCanExecuteChanged();
            UpdateCommand.NotifyCanExecuteChanged();
            RemoveCommand.NotifyCanExecuteChanged();
            InstallFromFileCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Download progress 0–100.</summary>
    public double Progress
    {
        get => _progress;
        private set
        {
            if (SetProperty(ref _progress, value))
                OnPropertyChanged(nameof(IsProgressIndeterminate));
        }
    }

    /// <summary>Before the first bytes arrive (catalog check, extraction) the bar only shows activity.</summary>
    public bool IsProgressIndeterminate => _progress <= 0;

    /// <summary>The catalog was read and offers nothing that is not installed already.</summary>
    public bool IsCatalogEmpty => _catalog is not null && Available.Count == 0;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string? ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => _errorText is not null;

    /// <summary>An update or removal finishes when Navigator restarts.</summary>
    public bool IsRestartPending => _host.GetPlugins().Any(p => p.Status is PluginStatus.UpdatePending or PluginStatus.Removed);

    public string CatalogSourceText => _catalog is null ? string.Empty
        : _catalog.SignatureVerified ? _localizer["Plugins.CatalogVerified"] : _localizer["Plugins.CatalogUnsigned"];

    public IAsyncRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand<PluginRowViewModel> InstallCommand { get; }

    public IAsyncRelayCommand<PluginRowViewModel> UpdateCommand { get; }

    public IAsyncRelayCommand<PluginRowViewModel> RemoveCommand { get; }

    public IAsyncRelayCommand InstallFromFileCommand { get; }

    public IRelayCommand CancelCommand { get; }

    /// <summary>Called when the page becomes visible: the catalog is fetched once per window, on demand.</summary>
    public void EnsureCatalog()
    {
        if (_catalogRequested || !_store.IsConfigured)
            return;
        _catalogRequested = true;
        RefreshCommand.Execute(null);
    }

    private async Task RefreshCatalogAsync()
    {
        await RunAsync(_localizer["Plugins.Checking"], async token =>
        {
            _catalog = await Task.Run(() => _store.GetCatalogAsync(token), token).ConfigureAwait(true);
            OnPropertyChanged(nameof(CatalogSourceText));
            RebuildRows();
        }).ConfigureAwait(true);
    }

    private async Task InstallFromCatalogAsync(PluginRowViewModel? row)
    {
        var entry = row?.CatalogEntry ?? _catalog?.Catalog.Plugins.FirstOrDefault(p => p.Id == row?.Id);
        if (entry is null)
            return;
        if (!entry.IsCompatible)
        {
            await _dialogs.ShowMessageAsync(_localizer["Plugins.Title"], _localizer.Format("Plugins.IncompatibleMessage", entry.Name)).ConfigureAwait(true);
            return;
        }

        await RunAsync(_localizer.Format("Plugins.Downloading", entry.Name), async token =>
        {
            var progress = new Progress<DownloadProgress>(p => Progress = p.Fraction * 100);
            var package = await Task.Run(() => _store.DownloadAsync(entry, progress, token), token).ConfigureAwait(true);
            StatusText = _localizer.Format("Plugins.Installing", entry.Name);
            var info = await _host.InstallAsync(package, entry.Id, token).ConfigureAwait(true);
            TryDeleteFile(package);
            ReportInstalled(info);
        }).ConfigureAwait(true);
    }

    private async Task InstallFromFileAsync()
    {
        var path = await _dialogs.PickFileAsync(_localizer["Plugins.InstallFromFile"], [PluginCatalog.PackageExtension]).ConfigureAwait(true);
        if (path is null)
            return;

        PluginManifest manifest;
        try
        {
            manifest = await Task.Run(() => PluginPackage.ReadManifest(path)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            ErrorText = ex.Message;
            return;
        }

        if (_host.Find(manifest.Id) is { IsBuiltIn: true })
        {
            ErrorText = _localizer.Format("Plugins.BuiltInConflict", manifest.Name);
            return;
        }

        // A package from disk has no catalog signature: the user decides whether to trust it.
        var confirm = await _dialogs.ConfirmAsync(new ConfirmRequest(_localizer["Plugins.UnverifiedTitle"],
            _localizer.Format("Plugins.UnverifiedMessage", manifest.Name, manifest.Version, manifest.Author), _localizer["Plugins.Install"], IsDestructive: true)).ConfigureAwait(true);
        if (confirm.Result != DialogResult.Primary)
            return;

        await RunAsync(_localizer.Format("Plugins.Installing", manifest.Name), async token =>
        {
            var info = await _host.InstallAsync(path, manifest.Id, token).ConfigureAwait(true);
            ReportInstalled(info);
        }).ConfigureAwait(true);
    }

    private async Task RemoveAsync(PluginRowViewModel? row)
    {
        if (row is not { CanRemove: true })
            return;
        var confirm = await _dialogs.ConfirmAsync(new ConfirmRequest(_localizer["Plugins.RemoveTitle"],
            _localizer.Format("Plugins.RemoveMessage", row.Name), _localizer["Plugins.Remove"], IsDestructive: true)).ConfigureAwait(true);
        if (confirm.Result != DialogResult.Primary)
            return;

        await RunAsync(_localizer.Format("Plugins.Removing", row.Name), async _ =>
        {
            await _host.UninstallAsync(row.Id).ConfigureAwait(true);
            StatusText = _localizer.Format("Plugins.Removed", row.Name);
        }).ConfigureAwait(true);
    }

    private void ReportInstalled(PluginInfo info)
    {
        StatusText = info.Status switch
        {
            PluginStatus.UpdatePending => _localizer.Format("Plugins.UpdateAfterRestart", info.Manifest.Name),
            PluginStatus.Active => _localizer.Format("Plugins.InstalledMessage", info.Manifest.Name),
            _ => _localizer.Format("Plugins.InstalledWithProblem", info.Manifest.Name, info.Error),
        };
    }

    private async Task RunAsync(string status, Func<CancellationToken, Task> work)
    {
        _work?.Dispose();
        var cancellation = new CancellationTokenSource();
        _work = cancellation;
        IsBusy = true;
        ErrorText = null;
        Progress = 0;
        StatusText = status;
        try
        {
            await work(cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusText = _localizer["Plugins.Cancelled"];
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "Plugin shop action failed");
            StatusText = string.Empty;
            ErrorText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
            RebuildRows();
        }
    }

    private void RebuildRows()
    {
        var plugins = _host.GetPlugins();
        var catalog = _catalog?.Catalog.Plugins ?? [];

        UnsubscribePages();
        Installed.Clear();
        foreach (var plugin in plugins)
        {
            var manifest = plugin.Manifest;
            var row = new PluginRowViewModel(manifest.Id, manifest.Name, DescriptionOf(plugin, catalog), manifest.Author, manifest.Version, manifest.IconKey,
                plugin.IsBuiltIn, isInstalled: true, StatusOf(plugin), plugin.Status is PluginStatus.Failed or PluginStatus.Incompatible,
                version => _localizer.Format("Plugins.UpdateTo", version))
            {
                IsRemoved = plugin.Status == PluginStatus.Removed,
                CatalogEntry = catalog.FirstOrDefault(c => c.Id == manifest.Id),
            };
            if (!plugin.IsBuiltIn && plugin.Status != PluginStatus.Removed && row.CatalogEntry is { IsCompatible: true } offered
                && offered.ParsedVersion > (plugin.PendingVersion is { } pending && Core.Versioning.SemVersion.TryParse(pending, out var p) ? p : manifest.ParsedVersion))
                row.UpdateVersion = offered.Version;
            if (plugin.Status == PluginStatus.Active && _host.GetSettingsPage(manifest.Id) is { } page)
                AttachSettings(row, page);
            Installed.Add(row);
        }

        Available.Clear();
        foreach (var entry in catalog.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var installed = plugins.FirstOrDefault(p => p.Id == entry.Id);
            if (installed is not null && installed.Status != PluginStatus.Removed)
                continue;
            if (!entry.SupportsCurrentPlatform)
                continue; // e.g. WSL or the Windows Search index on Linux and macOS
            Available.Add(new PluginRowViewModel(entry.Id, entry.Name, entry.Description, entry.Author, entry.Version, entry.IconKey, isBuiltIn: false, isInstalled: false,
                entry.IsCompatible ? string.Empty : _localizer["Plugins.StatusIncompatible"], !entry.IsCompatible)
            {
                CatalogEntry = entry,
            });
        }

        OnPropertyChanged(nameof(HasAvailable));
        OnPropertyChanged(nameof(IsCatalogEmpty));
        OnPropertyChanged(nameof(IsRestartPending));
    }

    /// <summary>Shows a plugin's settings under its row and keeps them current while the page is open.</summary>
    private void AttachSettings(PluginRowViewModel row, IPluginSettingsPage page)
    {
        Refresh();
        EventHandler handler = (_, _) => _dispatcher.Post(Refresh);
        page.Changed += handler;
        _pageSubscriptions.Add((page, handler));

        void Refresh()
        {
            IReadOnlyList<PluginSetting> settings;
            try
            {
                settings = page.GetSettings();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "Settings of plugin {Plugin} could not be read", row.Id);
                settings = [];
            }

            row.SetSettings(settings.Select(setting => new PluginSettingViewModel(setting, (id, value) => ApplySettingAsync(row, page, id, value))));
        }
    }

    /// <summary>Applies one plugin setting; a failure is shown and reported as false (the switch turns back).</summary>
    private async Task<bool> ApplySettingAsync(PluginRowViewModel row, IPluginSettingsPage page, string id, bool? value)
    {
        try
        {
            await page.ApplyAsync(id, value, CancellationToken.None).ConfigureAwait(true);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Setting {Setting} of plugin {Plugin} failed", id, row.Id);
            await _dialogs.ShowMessageAsync(row.Name, ex.Message).ConfigureAwait(true);
            return false;
        }
    }

    private void UnsubscribePages()
    {
        foreach (var (page, handler) in _pageSubscriptions)
            page.Changed -= handler;
        _pageSubscriptions.Clear();
    }

    private string DescriptionOf(PluginInfo plugin, IReadOnlyList<PluginCatalogEntry> catalog)
    {
        if (plugin.IsBuiltIn && _localizer.HasKey("Plugins.Description." + plugin.Id))
            return _localizer["Plugins.Description." + plugin.Id];
        return plugin.Manifest.Description.Length > 0 ? plugin.Manifest.Description : catalog.FirstOrDefault(c => c.Id == plugin.Id)?.Description ?? string.Empty;
    }

    private string StatusOf(PluginInfo plugin) => plugin.Status switch
    {
        PluginStatus.Active => plugin.IsBuiltIn ? _localizer["Plugins.StatusBuiltIn"] : string.Empty,
        PluginStatus.Failed => _localizer.Format("Plugins.StatusFailed", plugin.Error),
        PluginStatus.Incompatible => _localizer["Plugins.StatusIncompatible"],
        PluginStatus.UpdatePending => _localizer.Format("Plugins.StatusUpdatePending", plugin.PendingVersion),
        PluginStatus.Removed => _localizer["Plugins.StatusRemoved"],
        _ => string.Empty,
    };

    private void OnHostChanged(object? sender, EventArgs e) => _dispatcher.Post(() =>
    {
        if (!IsBusy)
            RebuildRows();
    });

    private void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Downloaded plugin package could not be deleted");
        }
    }

    public void Dispose()
    {
        _host.Changed -= OnHostChanged;
        UnsubscribePages();
        _work?.Cancel();
        _work?.Dispose();
    }
}
