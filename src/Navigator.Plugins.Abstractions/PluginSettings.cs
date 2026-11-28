namespace Navigator.Plugins;

public enum PluginSettingKind
{
    /// <summary>A switch; <see cref="PluginSetting.IsOn"/> is its state.</summary>
    Toggle,

    /// <summary>A button labelled <see cref="PluginSetting.ActionText"/>.</summary>
    Action,

    /// <summary>A line of text (state, hints); not interactive.</summary>
    Info,
}

/// <summary>One row of a plugin's settings, shown under the plugin in Settings → Plugins (plugin API 1.3).</summary>
/// <param name="Id">Passed back to <see cref="IPluginSettingsPage.ApplyAsync"/>.</param>
/// <param name="Title">Localized label.</param>
/// <param name="Description">Localized explanation; null for none.</param>
public sealed record PluginSetting(string Id, PluginSettingKind Kind, string Title, string? Description = null)
{
    public bool IsOn { get; init; }

    public string? ActionText { get; init; }

    public bool IsEnabled { get; init; } = true;
}

/// <summary>
/// The settings a plugin shows in Settings → Plugins (plugin API 1.3). One page per plugin.
/// </summary>
public interface IPluginSettingsPage
{
    /// <summary>The rows, localized. Called on the UI thread whenever the page is shown or <see cref="Changed"/> fired: keep it quick.</summary>
    IReadOnlyList<PluginSetting> GetSettings();

    /// <summary>
    /// A toggle was switched (<paramref name="value"/> is the new state) or a button pressed (<paramref name="value"/> null).
    /// Called on the UI thread; throw <see cref="InvalidOperationException"/> or <see cref="IOException"/> with a localized
    /// message to report a failure (a toggle then shows its previous state again).
    /// </summary>
    Task ApplyAsync(string settingId, bool? value, CancellationToken cancellationToken);

    /// <summary>Raise (from any thread) when the rows changed.</summary>
    event EventHandler? Changed;
}

/// <summary>
/// Optional for an <see cref="INavigatorPlugin"/> (plugin API 1.3): called before the plugin is removed from the plugin
/// shop, so it can undo what it changed outside its data folder (e.g. restore the system's default file manager).
/// Not called when Navigator merely exits.
/// </summary>
public interface IPluginUninstallHandler
{
    Task OnUninstallingAsync(CancellationToken cancellationToken);
}

public enum PluginNotificationKind
{
    Information,
    Success,
    Warning,
    Error,
}
