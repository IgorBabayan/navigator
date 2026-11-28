using Navigator.Plugins;

namespace Navigator.Plugin.DefaultFileManager;

/// <summary>
/// "Use Navigator as the default file manager", switched in Settings → Plugins. Nothing changes until the user turns it
/// on; removing the plugin turns it off first. After start the registration is checked once in the background and
/// pointed at the running Navigator if it moved.
/// </summary>
public sealed class DefaultFileManagerPlugin : INavigatorPlugin, IPluginUninstallHandler
{
    internal const string EnabledSetting = "enabled";

    private static PluginStrings? _text;
    private IDefaultFileManager? _manager;
    private IPluginContext? _context;

    internal static PluginStrings Text => _text ?? throw new InvalidOperationException("The default file manager plugin is not initialized.");

    public void Initialize(IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _text = new PluginStrings(typeof(DefaultFileManagerPlugin).Assembly, () => context.Language);
        var preferences = new PluginPreferences(() => context.DataDirectory);
        if (OperatingSystem.IsWindows())
            _manager = new WindowsDefaultFileManager(preferences);
        else if (OperatingSystem.IsLinux())
            _manager = new LinuxDefaultFileManager(preferences, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable);
        if (_manager is null)
            return;

        var page = new DefaultFileManagerSettingsPage(_manager, context);
        context.SetSettingsPage(page);
        _ = Task.Run(() => RepairAsync(_manager, context));
    }

    /// <summary>The plugin is being removed: Explorer (or the previous handler) gets its role back.</summary>
    public async Task OnUninstallingAsync(CancellationToken cancellationToken)
    {
        if (_manager is { } manager && await Task.Run(manager.IsNavigatorDefault, cancellationToken).ConfigureAwait(false))
            await Task.Run(() => manager.DisableAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The executable Navigator runs from; null for development runs through the dotnet host.</summary>
    internal static string? Executable()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path))
            return null;
        var name = Path.GetFileNameWithoutExtension(path);
        return string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase) ? null : path;
    }

    private static async Task RepairAsync(IDefaultFileManager manager, IPluginContext context)
    {
        try
        {
            if (Executable() is { } executable)
                await manager.RepairAsync(executable, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            context.Log.Warning("The default file manager registration could not be checked", ex);
        }
    }
}

/// <summary>Settings → Plugins → Default file manager: one switch.</summary>
internal sealed class DefaultFileManagerSettingsPage(IDefaultFileManager manager, IPluginContext context) : IPluginSettingsPage
{
    private bool? _isDefault;
    private bool _reading;

    public event EventHandler? Changed;

    public IReadOnlyList<PluginSetting> GetSettings()
    {
        // Reading the state can start a helper (xdg-mime): never on the UI thread.
        if (_isDefault is null && !_reading)
        {
            _reading = true;
            _ = Task.Run(() =>
            {
                try
                {
                    _isDefault = manager.IsNavigatorDefault();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    _isDefault = false;
                }
                finally
                {
                    _reading = false;
                }

                Changed?.Invoke(this, EventArgs.Empty);
            });
        }

        var text = DefaultFileManagerPlugin.Text;
        var available = DefaultFileManagerPlugin.Executable() is not null;
        return
        [
            new PluginSetting(DefaultFileManagerPlugin.EnabledSetting, PluginSettingKind.Toggle, text["Default.Settings.Enabled"],
                available ? text[OperatingSystem.IsWindows() ? "Default.Settings.EnabledHintWindows" : "Default.Settings.EnabledHintLinux"] : text["Default.Settings.DevelopmentBuild"])
            {
                IsOn = _isDefault == true,
                IsEnabled = available && _isDefault is not null,
            },
        ];
    }

    public async Task ApplyAsync(string settingId, bool? value, CancellationToken cancellationToken)
    {
        if (settingId != DefaultFileManagerPlugin.EnabledSetting || value is not { } on)
            return;
        var executable = DefaultFileManagerPlugin.Executable() ?? throw new InvalidOperationException(DefaultFileManagerPlugin.Text["Default.Settings.DevelopmentBuild"]);
        try
        {
            await Task.Run(() => on ? manager.EnableAsync(executable, cancellationToken) : manager.DisableAsync(cancellationToken), cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
        finally
        {
            _isDefault = null; // read again from the system
            Changed?.Invoke(this, EventArgs.Empty);
        }

        context.Ui.Notify(DefaultFileManagerPlugin.Text["Default.Title"],
            DefaultFileManagerPlugin.Text[on ? "Default.Enabled" : "Default.Disabled"], PluginNotificationKind.Success);
    }
}
