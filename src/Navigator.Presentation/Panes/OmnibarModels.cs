using Navigator.Core.IO;
using Navigator.Core.Navigation;
using Navigator.Core.Platform;
using Navigator.Presentation.Localization;

namespace Navigator.Presentation.Panes;

/// <summary>One segment of the omnibar breadcrumb.</summary>
/// <param name="name">Display name.</param>
/// <param name="path">Folder the segment navigates to; empty for path-less locations (Home, Trash, tags).</param>
/// <param name="isLast">True for the segment of the current location.</param>
/// <param name="canExpand">True when the segment has a chevron listing its sub-folders.</param>
/// <param name="nextPath">Path of the following segment (shown highlighted in the chevron list), if any.</param>
public sealed class BreadcrumbViewModel(string name, string path, bool isLast, bool canExpand = false, string? nextPath = null)
{
    public string Name { get; } = name;

    public string Path { get; } = path;

    public bool IsLast { get; } = isLast;

    public bool HasPath => Path.Length > 0;

    public bool CanExpand { get; } = canExpand;

    public string? NextPath { get; } = nextPath;
}

/// <summary>An entry of a breadcrumb chevron list (a sub-folder, or a root location for the leading chevron).</summary>
public sealed record BreadcrumbChild(string Name, Location Location, string IconKey, bool IsOnPath);

public enum AddressSuggestionKind
{
    Folder,
    History,
    Location,

    /// <summary>A setting found by name; choosing it opens the settings window on its page.</summary>
    Setting,
}

/// <summary>
/// A row of the omnibar drop-down. <see cref="Text"/> is what goes into the address box when the row is chosen;
/// <see cref="Detail"/> is the secondary line (usually the full path). Setting rows also carry the settings page and
/// the string key of the setting, which the settings window uses to scroll to it.
/// </summary>
public sealed record AddressSuggestion(string Text, string Name, string Detail, AddressSuggestionKind Kind, string IconKey,
    string? SettingsPage = null, string? SettingKey = null);

/// <summary>
/// Names that can be typed into the omnibar instead of a path, like the shell names of a desktop file manager:
/// "Desktop", "Downloads", "Recycle Bin", "shell:Documents"… Both the English and the localized names work.
/// </summary>
public static class AddressAliases
{
    private const string ShellPrefix = "shell:";

    /// <summary>Every alias with the location it opens and its icon, in display order (localized names).</summary>
    public static IReadOnlyList<(string Name, Location Location, string IconKey)> All(Localizer localizer, IKnownFolderProvider knownFolders)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(knownFolders);

        var list = new List<(string, Location, string)> { (localizer["Location.Home"], Location.Home, "Home") };
        foreach (var folder in Enum.GetValues<KnownFolder>())
        {
            if (knownFolders.GetPath(folder) is { } path)
                list.Add((localizer["KnownFolder." + folder], Location.Folder(path), folder == KnownFolder.Home ? "Home" : folder.ToString()));
        }

        list.Add((localizer[OperatingSystem.IsWindows() ? "Location.RecycleBin" : "Location.Trash"], Location.Trash, "Trash"));
        return list;
    }

    /// <summary>Resolves typed text to an alias location, or null when the text is not an alias.</summary>
    public static Location? Resolve(string input, Localizer localizer, IKnownFolderProvider knownFolders)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(knownFolders);

        var text = PathUtil.TrimTrailingSeparator(input.Trim().Trim('"'));
        if (text.StartsWith(ShellPrefix, StringComparison.OrdinalIgnoreCase))
            text = text[ShellPrefix.Length..].Trim();
        if (text.Length == 0)
            return null;

        // Invariant names (and Windows shell folder names) first, then the localized display names.
        switch (Normalize(text))
        {
            case "HOME":
                return Location.Home;
            case "TRASH" or "RECYCLEBIN" or "RECYCLEBINFOLDER" or "BIN":
                return Location.Trash;
            case "PROFILE" or "USERPROFILE" or "HOMEFOLDER":
                return knownFolders.GetPath(KnownFolder.Home) is { } profile ? Location.Folder(profile) : null;
            case "PERSONAL" or "MYDOCUMENTS":
                return knownFolders.GetPath(KnownFolder.Documents) is { } documents ? Location.Folder(documents) : null;
            case "MYMUSIC":
                return knownFolders.GetPath(KnownFolder.Music) is { } music ? Location.Folder(music) : null;
            case "MYPICTURES":
                return knownFolders.GetPath(KnownFolder.Pictures) is { } pictures ? Location.Folder(pictures) : null;
            case "MYVIDEO" or "MYVIDEOS":
                return knownFolders.GetPath(KnownFolder.Videos) is { } videos ? Location.Folder(videos) : null;
        }

        foreach (var folder in Enum.GetValues<KnownFolder>())
        {
            if (string.Equals(Normalize(folder.ToString()), Normalize(text), StringComparison.Ordinal))
                return knownFolders.GetPath(folder) is { } path ? Location.Folder(path) : null;
        }

        foreach (var (name, location, _) in All(localizer, knownFolders))
        {
            if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
                return location;
        }

        return null;
    }

    private static string Normalize(string text)
        => string.Concat(text.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
}
