using Navigator.Core.Navigation;

namespace Navigator.Plugins;

/// <summary>An action offered by a plugin: a section button (e.g. "Add connection") or an entry's context menu item.</summary>
public sealed record PluginCommand(string Id, string Title, string IconKey, bool IsDestructive = false);

/// <summary>One sidebar row. Clicking it navigates to <see cref="Location"/>.</summary>
public sealed record PluginSidebarEntry(string Id, string Name, string IconKey, Location Location, string? Detail = null, string? SystemIconKey = null)
{
    public IReadOnlyList<PluginCommand> Commands { get; init; } = [];
}

/// <summary>
/// A sidebar section contributed by a plugin. <see cref="GetEntriesAsync"/> runs on a background thread whenever the
/// sidebar is (re)built or <see cref="Changed"/> fires; keep it fast (no network round trips).
/// </summary>
public interface IPluginSidebarSection
{
    /// <summary>Stable id, unique across plugins (used to remember the collapsed state).</summary>
    string Id { get; }

    /// <summary>Localized title; read again after a language change.</summary>
    string Title { get; }

    string IconKey { get; }

    string? SystemIconKey { get; }

    /// <summary>Position among plugin sections (lower first). Plugin sections sit between Drives and Tags.</summary>
    int Order { get; }

    /// <summary>False hides the section while it has no entries (e.g. no network drives mounted).</summary>
    bool ShowWhenEmpty { get; }

    /// <summary>Buttons on the section header (localized titles; read again after a language change).</summary>
    IReadOnlyList<PluginCommand> Commands { get; }

    ValueTask<IReadOnlyList<PluginSidebarEntry>> GetEntriesAsync(CancellationToken cancellationToken);

    /// <summary>Runs a section command (<paramref name="entry"/> null) or an entry command. Called on the UI thread.</summary>
    Task ExecuteAsync(string commandId, PluginSidebarEntry? entry, CancellationToken cancellationToken);

    /// <summary>Raise (from any thread) when the entries or commands changed.</summary>
    event EventHandler? Changed;
}
