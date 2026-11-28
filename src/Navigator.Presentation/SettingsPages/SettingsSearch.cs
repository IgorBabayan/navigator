using Navigator.Presentation.Localization;

namespace Navigator.Presentation.SettingsPages;

/// <summary>A setting that can be found by name: the page that shows it and the string key of its label.</summary>
public sealed record SettingsSearchEntry(string PageId, string TitleKey, string? DescriptionKey = null, string? PageTitleKey = null);

/// <summary>A match for the omnibar: the localized setting and page names, and where to open the settings window.</summary>
public sealed record SettingsSearchResult(string PageId, string TitleKey, string Title, string PageTitle);

/// <summary>
/// Finds settings by what they are called in the current language, so the address bar can open the right page
/// ("hidden", "accent", "toolbar"…). The index is a fixed list of string keys; it is localized per query, which
/// costs a few hundred dictionary lookups and nothing while the user is not typing.
/// </summary>
public static class SettingsSearch
{
    public const int MinimumQueryLength = 2;

    private static readonly Dictionary<string, string> PageTitles = new(StringComparer.Ordinal)
    {
        ["general"] = "Settings.General",
        ["appearance"] = "Settings.Appearance",
        ["files"] = "Settings.FilesAndFolders",
        ["sidebar"] = "Settings.Sidebar",
        ["operations"] = "Settings.Operations",
        ["search"] = "Settings.Search",
        ["tags"] = "Settings.Tags",
        ["keyboard"] = "Settings.Keyboard",
        ["performance"] = "Settings.Performance",
        ["plugins"] = "Settings.Plugins",
        ["updates"] = "Settings.Updates",
        ["advanced"] = "Settings.Advanced",
        ["about"] = "Settings.About",
    };

    /// <summary>Searchable settings, in the order the settings window shows them.</summary>
    public static IReadOnlyList<SettingsSearchEntry> Entries { get; } =
    [
        new("general", "Settings.Language", "Settings.LanguageDescription"),
        new("general", "Settings.Startup"),
        new("general", "Settings.StartupLocations", "Settings.StartupLocationsDescription"),
        new("general", "Settings.OpenFoldersInNewTab"),
        new("general", "Settings.ConfirmCloseTabs"),
        new("general", "Command.ActivateApp", PageTitleKey: "Settings.GlobalShortcutSection"),
        new("general", "Settings.GlobalShortcutWhenClosed", "Settings.GlobalShortcutWhenClosedDescription"),
        new("general", "Settings.KeepRunningInBackground", "Settings.KeepRunningInBackgroundDescription"),
        new("general", "Home.QuickAccess", PageTitleKey: "Settings.HomeWidgets"),
        new("general", "Home.Drives", PageTitleKey: "Settings.HomeWidgets"),
        new("general", "Home.RecentFiles", PageTitleKey: "Settings.HomeWidgets"),
        new("general", "Settings.ContextMenu"),
        new("general", "Settings.CodeEditor", "Settings.CodeEditorDescription"),
        new("general", "Omnibar.ClearHistory", "Settings.AddressHistoryDescription"),
        new("appearance", "Settings.Theme"),
        new("appearance", "Settings.AccentColor"),
        new("appearance", "Settings.Backdrop", "Settings.BackdropDescription"),
        new("appearance", "Settings.Transparency"),
        new("appearance", "Settings.BackgroundColor", "Settings.BackgroundColorDescription"),
        new("appearance", "Settings.BackgroundImage"),
        new("appearance", "Settings.Density"),
        new("appearance", "Settings.Animations", "Settings.AnimationsDescription"),
        new("appearance", "Settings.FontFamily"),
        new("appearance", "Settings.FontScale"),
        new("appearance", "Settings.ToolbarLabels"),
        new("appearance", "Settings.Toolbar", "Settings.ToolbarDescription"),
        new("files", "Settings.ShowHiddenItems"),
        new("files", "Settings.ShowDotFiles"),
        new("files", "Settings.ShowProtectedSystemItems"),
        new("files", "Settings.ShowFileExtensions"),
        new("files", "Settings.UseSystemIcons"),
        new("files", "Settings.ShowThumbnails"),
        new("files", "Settings.SingleClick"),
        new("files", "Settings.DoubleClickUp"),
        new("files", "Settings.FolderSizes"),
        new("files", "Settings.ConfirmDelete"),
        new("files", "Command.Layout", PageTitleKey: "Settings.DefaultView"),
        new("files", "Command.Sort", PageTitleKey: "Settings.DefaultView"),
        new("files", "Command.Group", PageTitleKey: "Settings.DefaultView"),
        new("files", "Sort.FoldersFirst", PageTitleKey: "Settings.DefaultView"),
        new("files", "Settings.RememberViewPerFolder"),
        new("files", "Settings.ForgetFolderViews", "Settings.ForgetFolderViewsDescription"),
        new("sidebar", "Settings.ShowSidebar"),
        new("sidebar", "Sidebar.Pinned"),
        new("sidebar", "Sidebar.Network"),
        new("sidebar", "Sidebar.Cloud"),
        new("operations", "Settings.Concurrency", "Settings.ConcurrencyDescription"),
        new("operations", "Settings.VerifyMoves", "Settings.VerifyMovesDescription"),
        new("operations", "Settings.NotifyCompleted"),
        new("search", "Settings.SearchSubfolders"),
        new("search", "Settings.SearchContents"),
        new("search", "Settings.SearchHistory"),
        new("search", "Settings.SearchMaxResults"),
        new("tags", "Settings.AddTag"),
        new("keyboard", "Settings.Keyboard"),
        new("performance", "Settings.ThumbnailMemory", "Settings.ThumbnailMemoryDescription"),
        new("performance", "Settings.ThumbnailDisk", "Settings.ThumbnailDiskDescription"),
        new("performance", "Settings.DirectoryCache", "Settings.DirectoryCacheDescription"),
        new("performance", "Settings.BackgroundWorkers"),
        new("performance", "Settings.PerformanceOverlay", "Settings.PerformanceOverlayDescription"),
        new("plugins", "Settings.Plugins"),
        new("updates", "Settings.CheckOnStartup"),
        new("updates", "Settings.CheckInterval"),
        new("updates", "Settings.Channel"),
        new("advanced", "Settings.LogLevel"),
        new("advanced", "Settings.Backup"),
        new("advanced", "Settings.ResetAll"),
        new("about", "Settings.About"),
    ];

    /// <summary>The localized title of a settings page ("Appearance").</summary>
    public static string PageTitle(string pageId, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return PageTitles.TryGetValue(pageId, out var key) ? localizer[key] : pageId;
    }

    /// <summary>
    /// Settings whose name, description or page contain every word of <paramref name="query"/> (case- and
    /// accent-insensitive in the UI culture). Matches at the start of the name rank first, then matches in the name,
    /// then matches found only in the description or page.
    /// </summary>
    public static IReadOnlyList<SettingsSearchResult> Find(string query, Localizer localizer, int maxResults)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        var terms = (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (maxResults <= 0 || terms.Length == 0 || terms.Sum(t => t.Length) < MinimumQueryLength)
            return [];

        var compare = localizer.Culture.CompareInfo;
        const System.Globalization.CompareOptions Loose = System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace;
        var ranked = new List<(int Rank, int Order, SettingsSearchResult Result)>();
        for (var order = 0; order < Entries.Count; order++)
        {
            var entry = Entries[order];
            var title = localizer[entry.TitleKey];
            var page = PageTitle(entry.PageId, localizer);
            var context = entry.PageTitleKey is { } section ? page + " " + localizer[section] : page;
            var description = entry.DescriptionKey is { } d ? localizer[d] : string.Empty;

            // An entry is as good as its weakest term; one term found nowhere drops it.
            var rank = 0;
            foreach (var term in terms)
            {
                var inTitle = compare.IndexOf(title, term, Loose);
                var termRank = inTitle == 0 || (inTitle > 0 && !char.IsLetterOrDigit(title[inTitle - 1])) ? 0
                    : inTitle > 0 ? 1
                    : compare.IndexOf(description, term, Loose) >= 0 || compare.IndexOf(context, term, Loose) >= 0 ? 2
                    : -1;
                if (termRank < 0)
                {
                    rank = -1;
                    break;
                }

                rank = Math.Max(rank, termRank);
            }

            if (rank >= 0)
                ranked.Add((rank, order, new SettingsSearchResult(entry.PageId, entry.TitleKey, title, page)));
        }

        return [.. ranked.OrderBy(r => r.Rank).ThenBy(r => r.Order).Take(maxResults).Select(r => r.Result)];
    }
}
