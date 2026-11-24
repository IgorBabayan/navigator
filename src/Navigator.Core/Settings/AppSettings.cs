using Navigator.Core.Sorting;

namespace Navigator.Core.Settings;

public enum ThemeMode
{
    System,
    Light,
    Dark,
}

public enum BackdropMaterial
{
    None,
    Mica,
    Acrylic,
}

public enum InterfaceDensity
{
    Compact,
    Standard,
    Comfortable,
}

public enum AnimationLevel
{
    Full,
    Reduced,
    Off,
}

public enum StartupBehavior
{
    OpenHome,
    RestoreSession,
    OpenSpecificLocations,
}

public enum FolderLayout
{
    Details,
    List,
    Tiles,
    SmallIcons,
    MediumIcons,
    LargeIcons,
    ExtraLargeIcons,
    Columns,
}

public enum UpdateChannel
{
    Stable,
    Prerelease,
}

public enum LogVerbosity
{
    Error,
    Warning,
    Information,
    Debug,
}

/// <summary>
/// Persistent user settings. Every property has a safe default so that a missing or partially
/// corrupt file still produces a working configuration. Bump <see cref="CurrentSchemaVersion"/>
/// and add an <see cref="ISettingsMigration"/> whenever a stored shape changes incompatibly.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public AppearanceSettings Appearance { get; set; } = new();

    public GeneralSettings General { get; set; } = new();

    public SidebarSettings Sidebar { get; set; } = new();

    public FilesAndFoldersSettings FilesAndFolders { get; set; } = new();

    public FileOperationSettings Operations { get; set; } = new();

    public SearchSettings Search { get; set; } = new();

    public PerformanceSettings Performance { get; set; } = new();

    public UpdateSettings Updates { get; set; } = new();

    public AdvancedSettings Advanced { get; set; } = new();

    /// <summary>Command id → key gestures ("Ctrl+Shift+T"). Only overrides of the defaults are stored.</summary>
    public Dictionary<string, List<string>> KeyBindings { get; set; } = [];

    public List<TagDefinition> Tags { get; set; } =
    [
        new() { Id = "red", Name = "Red", Color = "#E74856" },
        new() { Id = "orange", Name = "Orange", Color = "#F7630C" },
        new() { Id = "yellow", Name = "Yellow", Color = "#FCE100" },
        new() { Id = "green", Name = "Green", Color = "#16C60C" },
        new() { Id = "blue", Name = "Blue", Color = "#0078D4" },
        new() { Id = "purple", Name = "Purple", Color = "#886CE4" },
    ];
}

public sealed class AppearanceSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>"#RRGGBB" or null to follow the system accent color.</summary>
    public string? AccentColor { get; set; }

    /// <summary>"#RRGGBB" overriding the window background, or null for the theme default.</summary>
    public string? BackgroundColor { get; set; }

    public BackdropMaterial Backdrop { get; set; } = BackdropMaterial.Mica;

    /// <summary>0 = opaque, 1 = maximum see-through for the backdrop tint.</summary>
    public double Transparency { get; set; } = 0.5;

    public string? BackgroundImagePath { get; set; }

    public double BackgroundImageOpacity { get; set; } = 0.2;

    public InterfaceDensity Density { get; set; } = InterfaceDensity.Standard;

    public AnimationLevel Animations { get; set; } = AnimationLevel.Full;

    /// <summary>Null = platform UI font.</summary>
    public string? FontFamily { get; set; }

    public double FontScale { get; set; } = 1.0;

    public bool ShowToolbarLabels { get; set; } = true;
}

public sealed class GeneralSettings
{
    /// <summary>"system", "en" or "uk".</summary>
    public string Language { get; set; } = "system";

    public StartupBehavior Startup { get; set; } = StartupBehavior.OpenHome;

    public List<string> StartupLocations { get; set; } = [];

    public bool OpenFoldersInNewTab { get; set; }

    public bool ConfirmCloseWithMultipleTabs { get; set; } = true;

    public bool ShowHomeQuickAccess { get; set; } = true;

    public bool ShowHomeDrives { get; set; } = true;

    public bool ShowHomeTags { get; set; } = true;

    public bool ShowHomeRecentFiles { get; set; } = true;

    public bool ShowOpenInNewWindowInContextMenu { get; set; } = true;

    public bool ShowCopyPathInContextMenu { get; set; } = true;

    public bool ShowOpenTerminalInContextMenu { get; set; } = true;

    public string? CodeEditorCommand { get; set; }
}

public sealed class SidebarSettings
{
    public bool IsVisible { get; set; } = true;

    public double Width { get; set; } = 240;

    public bool ShowQuickAccess { get; set; } = true;

    public bool ShowDrives { get; set; } = true;

    public bool ShowNetwork { get; set; } = true;

    public bool ShowTags { get; set; } = true;

    public bool ShowTrash { get; set; } = true;

    /// <summary>User pinned folders in display order. Null on first run means "use the platform defaults".</summary>
    public List<string>? PinnedFolders { get; set; }

    public List<string> CollapsedSections { get; set; } = [];
}

public sealed class FilesAndFoldersSettings
{
    public bool ShowHiddenItems { get; set; }

    public bool ShowProtectedSystemItems { get; set; }

    public bool ShowDotFiles { get; set; }

    public bool ShowFileExtensions { get; set; } = true;

    public bool ShowThumbnails { get; set; } = true;

    public bool ShowCheckBoxes { get; set; }

    public bool OpenItemsWithSingleClick { get; set; }

    public bool DoubleClickEmptyAreaGoesUp { get; set; } = true;

    public bool CalculateFolderSizes { get; set; }

    public bool ConfirmDelete { get; set; } = true;

    public bool ShowFileTagsColumn { get; set; } = true;

    public FolderLayout DefaultLayout { get; set; } = FolderLayout.Details;

    public SortColumn DefaultSortColumn { get; set; } = SortColumn.Name;

    public SortDirection DefaultSortDirection { get; set; } = SortDirection.Ascending;

    /// <summary>False = "mixed sorting": folders sorted together with files.</summary>
    public bool ListFoldersFirst { get; set; } = true;

    public GroupBy DefaultGroupBy { get; set; } = GroupBy.None;

    public bool RememberViewPerFolder { get; set; } = true;

    /// <summary>Per-folder layout/sort preferences keyed by folder path. Bounded by <see cref="FolderViewPreferences"/>.</summary>
    public Dictionary<string, FolderViewPreference> FolderViews { get; set; } = [];

    public List<ColumnSetting> Columns { get; set; } =
    [
        new() { Id = "name", Width = 320, IsVisible = true },
        new() { Id = "modified", Width = 170, IsVisible = true },
        new() { Id = "type", Width = 140, IsVisible = true },
        new() { Id = "size", Width = 100, IsVisible = true },
        new() { Id = "created", Width = 170, IsVisible = false },
        new() { Id = "tags", Width = 120, IsVisible = true },
    ];
}

public sealed class FolderViewPreference
{
    public FolderLayout Layout { get; set; }

    public SortColumn SortColumn { get; set; }

    public SortDirection SortDirection { get; set; }

    public GroupBy GroupBy { get; set; }

    public DateTimeOffset LastUsed { get; set; }
}

public sealed class ColumnSetting
{
    public string Id { get; set; } = string.Empty;

    public double Width { get; set; }

    public bool IsVisible { get; set; } = true;
}

public sealed class FileOperationSettings
{
    public int MaxConcurrentOperations { get; set; } = 2;

    public bool VerifyMovedFileContents { get; set; }

    public bool ShowOperationCompletedNotification { get; set; } = true;
}

public sealed class SearchSettings
{
    public bool IncludeSubfolders { get; set; } = true;

    public bool SearchFileContents { get; set; }

    public int MaxResults { get; set; } = 5000;

    public bool KeepHistory { get; set; } = true;

    public List<string> History { get; set; } = [];
}

public sealed class PerformanceSettings
{
    public int ThumbnailMemoryCacheMegabytes { get; set; } = 128;

    public int ThumbnailDiskCacheMegabytes { get; set; } = 512;

    public int DirectoryCacheEntries { get; set; } = 32;

    public int MaxBackgroundWorkers { get; set; } = 2;
}

public sealed class UpdateSettings
{
    public bool CheckOnStartup { get; set; } = true;

    /// <summary>0 disables periodic checks.</summary>
    public int CheckIntervalHours { get; set; } = 24;

    public UpdateChannel Channel { get; set; } = UpdateChannel.Stable;

    public string? SkippedVersion { get; set; }
}

public sealed class AdvancedSettings
{
    public LogVerbosity LogLevel { get; set; } = LogVerbosity.Information;

    public bool EnableExperimentalFeatures { get; set; }

    public bool ShowPerformanceOverlay { get; set; }
}

public sealed class TagDefinition
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Color { get; set; } = "#808080";
}
