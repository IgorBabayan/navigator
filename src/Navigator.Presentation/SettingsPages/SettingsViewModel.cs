using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Navigator.Core.Platform;
using Navigator.Core.Settings;
using Navigator.Core.Sorting;
using Navigator.Core.Tags;
using Navigator.Presentation.Commands;
using Navigator.Presentation.Localization;
using Navigator.Presentation.Services;
using Navigator.Presentation.Settings;
using Navigator.Presentation.Updates;

namespace Navigator.Presentation.SettingsPages;

public sealed record SettingsPage(string Id, string Title, string IconKey);

public sealed record Option<T>(T Value, string Text)
{
    public override string ToString() => Text;
}

public sealed class TagRowViewModel(TagDefinition tag, Action<TagRowViewModel> changed) : ObservableObject
{
    public string Id { get; } = tag.Id;

    public string Name
    {
        get => tag.Name;
        set
        {
            if (tag.Name == value || string.IsNullOrWhiteSpace(value))
                return;
            tag.Name = value.Trim();
            OnPropertyChanged();
            changed(this);
        }
    }

    public string Color
    {
        get => tag.Color;
        set
        {
            if (tag.Color == value || !IsColor(value))
                return;
            tag.Color = value;
            OnPropertyChanged();
            changed(this);
        }
    }

    private static readonly System.Buffers.SearchValues<char> HexDigits = System.Buffers.SearchValues.Create("0123456789abcdefABCDEF");

    internal static bool IsColor(string value)
        => value.Length == 7 && value[0] == '#' && value.AsSpan(1).IndexOfAnyExcept(HexDigits) < 0;
}

public sealed class KeyBindingRowViewModel(AppCommandInfo command, string name, Func<string, IReadOnlyList<string>> current, Action<string, IReadOnlyList<string>?> save) : ObservableObject
{
    private string? _editText;

    public string Id => command.Id;

    public string Name { get; } = name;

    public string Category => command.Category;

    public string Gestures
    {
        get => _editText ?? string.Join(", ", current(command.Id));
        set
        {
            _editText = value;
            var gestures = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            save(command.Id, gestures);
            _editText = null;
            OnPropertyChanged();
        }
    }

    public void ResetToDefault()
    {
        save(command.Id, null);
        OnPropertyChanged(nameof(Gestures));
    }
}

/// <summary>
/// Settings window. Every property writes through <see cref="SettingsService"/> immediately (no Apply button);
/// every property can be reset individually through <see cref="ResetCommand"/> with its name as parameter.
/// Platform-specific settings are only exposed where they work (see the Is*Supported properties).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Instance properties are required for compiled XAML bindings.")]
public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly Localizer _localizer;
    private readonly IDialogService _dialogs;
    private readonly IFileLauncher _launcher;
    private readonly TagStore _tags;
    private readonly AppDirectories _directories;
    private readonly Dictionary<string, (string Section, Action<AppSettings, AppSettings> Reset)> _resetters = [];
    private SettingsPage _selectedPage;

    public SettingsViewModel(SettingsService settings, Localizer localizer, IDialogService dialogs, IFileLauncher launcher, TagStore tags, AppDirectories directories, UpdateViewModel update)
    {
        _settings = settings;
        _localizer = localizer;
        _dialogs = dialogs;
        _launcher = launcher;
        _tags = tags;
        _directories = directories;
        Update = update;

        Pages =
        [
            new("general", localizer["Settings.General"], "Settings"),
            new("appearance", localizer["Settings.Appearance"], "Appearance"),
            new("files", localizer["Settings.FilesAndFolders"], "Folder"),
            new("sidebar", localizer["Settings.Sidebar"], "Sidebar"),
            new("operations", localizer["Settings.Operations"], "Copy"),
            new("search", localizer["Settings.Search"], "Search"),
            new("tags", localizer["Settings.Tags"], "Tag"),
            new("keyboard", localizer["Settings.Keyboard"], "Keyboard"),
            new("performance", localizer["Settings.Performance"], "Speed"),
            new("updates", localizer["Settings.Updates"], "Update"),
            new("advanced", localizer["Settings.Advanced"], "Advanced"),
            new("about", localizer["Settings.About"], "Info"),
        ];
        _selectedPage = Pages[0];

        Languages = [new("system", localizer["Language.System"]), .. Localizer.AvailableLanguages.Select(code => new Option<string>(code, localizer["Language." + code]))];
        Themes = [.. Enum.GetValues<ThemeMode>().Select(v => new Option<ThemeMode>(v, localizer["Theme." + v]))];
        Backdrops = [.. Enum.GetValues<BackdropMaterial>().Select(v => new Option<BackdropMaterial>(v, localizer["Backdrop." + v]))];
        Densities = [.. Enum.GetValues<InterfaceDensity>().Select(v => new Option<InterfaceDensity>(v, localizer["Density." + v]))];
        AnimationLevels = [.. Enum.GetValues<AnimationLevel>().Select(v => new Option<AnimationLevel>(v, localizer["Animations." + v]))];
        StartupOptions = [.. Enum.GetValues<StartupBehavior>().Select(v => new Option<StartupBehavior>(v, localizer["Startup." + v]))];
        Layouts = [.. Enum.GetValues<FolderLayout>().Where(l => l != FolderLayout.Columns).Select(v => new Option<FolderLayout>(v, localizer["Layout." + v]))];
        SortColumns = [.. Enum.GetValues<SortColumn>().Select(v => new Option<SortColumn>(v, localizer["Sort." + v]))];
        GroupOptions = [.. Enum.GetValues<GroupBy>().Select(v => new Option<GroupBy>(v, localizer["GroupBy." + v]))];
        Channels = [.. Enum.GetValues<UpdateChannel>().Select(v => new Option<UpdateChannel>(v, localizer["Channel." + v]))];
        LogLevels = [.. Enum.GetValues<LogVerbosity>().Select(v => new Option<LogVerbosity>(v, localizer["LogLevel." + v]))];
        AccentPresets = ["#0078D4", "#0063B1", "#8764B8", "#E3008C", "#E81123", "#F7630C", "#CA5010", "#498205", "#00B7C3", "#2D7D9A", "#5D5A58"];

        foreach (var tag in settings.Current.Tags)
            TagRows.Add(new TagRowViewModel(tag, _ => SaveTags()));
        var isMac = OperatingSystem.IsMacOS();
        foreach (var command in CommandRegistry.All)
        {
            KeyBindings.Add(new KeyBindingRowViewModel(command, localizer[command.NameKey],
                id => CommandRegistry.GesturesFor(id, _settings.Current, isMac),
                SaveKeyBinding));
        }

        ResetCommand = new RelayCommand<string>(Reset);
        ResetAllCommand = new AsyncRelayCommand(ResetAllAsync);
        ExportCommand = new AsyncRelayCommand(ExportAsync);
        ImportCommand = new AsyncRelayCommand(ImportAsync);
        OpenLogFolderCommand = new AsyncRelayCommand(() => _launcher.OpenAsync(_directories.LogDirectory));
        OpenSettingsFolderCommand = new AsyncRelayCommand(() => _launcher.OpenAsync(_directories.ConfigDirectory));
        AddTagCommand = new RelayCommand(AddTag);
        RemoveTagCommand = new RelayCommand<TagRowViewModel>(RemoveTag);
        ResetKeyBindingCommand = new RelayCommand<KeyBindingRowViewModel>(row => row?.ResetToDefault());
        ClearSearchHistoryCommand = new RelayCommand(() => Set("search", new List<string>(), s => s.Search.History, (s, v) => s.Search.History = v));
        PickAccentCommand = new RelayCommand<string>(color => AccentColor = color);
        PickBackgroundImageCommand = new AsyncRelayCommand(PickBackgroundImageAsync);
        RegisterResetters();
    }

    private AppSettings S => _settings.Current;

    public UpdateViewModel Update { get; }

    public IReadOnlyList<SettingsPage> Pages { get; }

    public SettingsPage SelectedPage
    {
        get => _selectedPage;
        set => SetProperty(ref _selectedPage, value);
    }

    public IReadOnlyList<Option<string>> Languages { get; }

    public IReadOnlyList<Option<ThemeMode>> Themes { get; }

    public IReadOnlyList<Option<BackdropMaterial>> Backdrops { get; }

    public IReadOnlyList<Option<InterfaceDensity>> Densities { get; }

    public IReadOnlyList<Option<AnimationLevel>> AnimationLevels { get; }

    public IReadOnlyList<Option<StartupBehavior>> StartupOptions { get; }

    public IReadOnlyList<Option<FolderLayout>> Layouts { get; }

    public IReadOnlyList<Option<SortColumn>> SortColumns { get; }

    public IReadOnlyList<Option<GroupBy>> GroupOptions { get; }

    public IReadOnlyList<Option<UpdateChannel>> Channels { get; }

    public IReadOnlyList<Option<LogVerbosity>> LogLevels { get; }

    public IReadOnlyList<string> AccentPresets { get; }

    public ObservableCollection<TagRowViewModel> TagRows { get; } = [];

    public ObservableCollection<KeyBindingRowViewModel> KeyBindings { get; } = [];

    public string KeyBindingConflicts
        => string.Join(Environment.NewLine, CommandRegistry.FindConflicts(S, OperatingSystem.IsMacOS())
            .Select(c => $"{c.Gesture}: {string.Join(", ", c.CommandIds.Select(id => _localizer[CommandRegistry.Find(id)!.NameKey]))}"));

    /// <summary>Mica/Acrylic are native on Windows 11 and macOS; elsewhere a tinted fallback is drawn.</summary>
    public bool IsNativeBackdropSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || OperatingSystem.IsMacOS();

    public bool IsProtectedSystemItemsSupported => OperatingSystem.IsWindows();

    public string AppVersion => Update.CurrentVersion;

    public string RuntimeVersion => System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;

    public string OperatingSystemText => System.Runtime.InteropServices.RuntimeInformation.OSDescription + " (" + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture + ")";

    public string LogDirectory => _directories.LogDirectory;

    public string SettingsDirectory => _directories.ConfigDirectory;

    // ── General ─────────────────────────────────────────────
    public Option<string> Language
    {
        get => Languages.FirstOrDefault(l => l.Value == S.General.Language) ?? Languages[0];
        set
        {
            if (value is null)
                return;
            Set("language", value.Value, s => s.General.Language, (s, v) => s.General.Language = v);
            _localizer.SetLanguage(value.Value);
        }
    }

    public Option<StartupBehavior> Startup
    {
        get => StartupOptions.First(o => o.Value == S.General.Startup);
        set { if (value is not null) Set("general", value.Value, s => s.General.Startup, (s, v) => s.General.Startup = v); }
    }

    public bool OpenFoldersInNewTab { get => S.General.OpenFoldersInNewTab; set => Set("general", value, s => s.General.OpenFoldersInNewTab, (s, v) => s.General.OpenFoldersInNewTab = v); }

    public bool ConfirmCloseWithMultipleTabs { get => S.General.ConfirmCloseWithMultipleTabs; set => Set("general", value, s => s.General.ConfirmCloseWithMultipleTabs, (s, v) => s.General.ConfirmCloseWithMultipleTabs = v); }

    public bool ShowHomeQuickAccess { get => S.General.ShowHomeQuickAccess; set => Set("home", value, s => s.General.ShowHomeQuickAccess, (s, v) => s.General.ShowHomeQuickAccess = v); }

    public bool ShowHomeDrives { get => S.General.ShowHomeDrives; set => Set("home", value, s => s.General.ShowHomeDrives, (s, v) => s.General.ShowHomeDrives = v); }

    public bool ShowHomeTags { get => S.General.ShowHomeTags; set => Set("home", value, s => s.General.ShowHomeTags, (s, v) => s.General.ShowHomeTags = v); }

    public bool ShowHomeRecentFiles { get => S.General.ShowHomeRecentFiles; set => Set("home", value, s => s.General.ShowHomeRecentFiles, (s, v) => s.General.ShowHomeRecentFiles = v); }

    public bool ShowOpenInNewWindowInContextMenu { get => S.General.ShowOpenInNewWindowInContextMenu; set => Set("contextMenu", value, s => s.General.ShowOpenInNewWindowInContextMenu, (s, v) => s.General.ShowOpenInNewWindowInContextMenu = v); }

    public bool ShowCopyPathInContextMenu { get => S.General.ShowCopyPathInContextMenu; set => Set("contextMenu", value, s => s.General.ShowCopyPathInContextMenu, (s, v) => s.General.ShowCopyPathInContextMenu = v); }

    public bool ShowOpenTerminalInContextMenu { get => S.General.ShowOpenTerminalInContextMenu; set => Set("contextMenu", value, s => s.General.ShowOpenTerminalInContextMenu, (s, v) => s.General.ShowOpenTerminalInContextMenu = v); }

    public string CodeEditorCommand { get => S.General.CodeEditorCommand ?? string.Empty; set => Set("general", string.IsNullOrWhiteSpace(value) ? null : value.Trim(), s => s.General.CodeEditorCommand, (s, v) => s.General.CodeEditorCommand = v); }

    // ── Appearance ──────────────────────────────────────────
    public Option<ThemeMode> Theme
    {
        get => Themes.First(o => o.Value == S.Appearance.Theme);
        set { if (value is not null) Set("appearance", value.Value, s => s.Appearance.Theme, (s, v) => s.Appearance.Theme = v); }
    }

    public string? AccentColor
    {
        get => S.Appearance.AccentColor;
        set => Set("appearance", value is null || TagRowViewModel.IsColor(value) ? value : S.Appearance.AccentColor, s => s.Appearance.AccentColor, (s, v) => s.Appearance.AccentColor = v);
    }

    public bool UseSystemAccent
    {
        get => S.Appearance.AccentColor is null;
        set
        {
            AccentColor = value ? null : AccentPresets[0];
            OnPropertyChanged();
        }
    }

    public string? BackgroundColor
    {
        get => S.Appearance.BackgroundColor;
        set => Set("appearance", string.IsNullOrWhiteSpace(value) ? null : TagRowViewModel.IsColor(value) ? value : S.Appearance.BackgroundColor, s => s.Appearance.BackgroundColor, (s, v) => s.Appearance.BackgroundColor = v);
    }

    public Option<BackdropMaterial> Backdrop
    {
        get => Backdrops.First(o => o.Value == S.Appearance.Backdrop);
        set { if (value is not null) Set("appearance", value.Value, s => s.Appearance.Backdrop, (s, v) => s.Appearance.Backdrop = v); }
    }

    public double Transparency { get => S.Appearance.Transparency; set => Set("appearance", Math.Round(value, 2), s => s.Appearance.Transparency, (s, v) => s.Appearance.Transparency = v); }

    public string? BackgroundImagePath { get => S.Appearance.BackgroundImagePath; set => Set("appearance", string.IsNullOrWhiteSpace(value) ? null : value, s => s.Appearance.BackgroundImagePath, (s, v) => s.Appearance.BackgroundImagePath = v); }

    public double BackgroundImageOpacity { get => S.Appearance.BackgroundImageOpacity; set => Set("appearance", Math.Round(value, 2), s => s.Appearance.BackgroundImageOpacity, (s, v) => s.Appearance.BackgroundImageOpacity = v); }

    public Option<InterfaceDensity> Density
    {
        get => Densities.First(o => o.Value == S.Appearance.Density);
        set { if (value is not null) Set("appearance", value.Value, s => s.Appearance.Density, (s, v) => s.Appearance.Density = v); }
    }

    public Option<AnimationLevel> Animations
    {
        get => AnimationLevels.First(o => o.Value == S.Appearance.Animations);
        set { if (value is not null) Set("appearance", value.Value, s => s.Appearance.Animations, (s, v) => s.Appearance.Animations = v); }
    }

    public double FontScale { get => S.Appearance.FontScale; set => Set("appearance", Math.Round(value, 2), s => s.Appearance.FontScale, (s, v) => s.Appearance.FontScale = v); }

    public bool ShowToolbarLabels { get => S.Appearance.ShowToolbarLabels; set => Set("appearance", value, s => s.Appearance.ShowToolbarLabels, (s, v) => s.Appearance.ShowToolbarLabels = v); }

    // ── Files and folders ───────────────────────────────────
    public bool ShowHiddenItems { get => S.FilesAndFolders.ShowHiddenItems; set => Set("filesAndFolders", value, s => s.FilesAndFolders.ShowHiddenItems, (s, v) => s.FilesAndFolders.ShowHiddenItems = v); }

    public bool ShowProtectedSystemItems { get => S.FilesAndFolders.ShowProtectedSystemItems; set => Set("filesAndFolders", value, s => s.FilesAndFolders.ShowProtectedSystemItems, (s, v) => s.FilesAndFolders.ShowProtectedSystemItems = v); }

    public bool ShowDotFiles { get => S.FilesAndFolders.ShowDotFiles; set => Set("filesAndFolders", value, s => s.FilesAndFolders.ShowDotFiles, (s, v) => s.FilesAndFolders.ShowDotFiles = v); }

    public bool ShowFileExtensions { get => S.FilesAndFolders.ShowFileExtensions; set => Set("filesAndFolders", value, s => s.FilesAndFolders.ShowFileExtensions, (s, v) => s.FilesAndFolders.ShowFileExtensions = v); }

    public bool ShowThumbnails { get => S.FilesAndFolders.ShowThumbnails; set => Set("filesAndFolders", value, s => s.FilesAndFolders.ShowThumbnails, (s, v) => s.FilesAndFolders.ShowThumbnails = v); }

    public bool ShowCheckBoxes { get => S.FilesAndFolders.ShowCheckBoxes; set => Set("filesAndFolders", value, s => s.FilesAndFolders.ShowCheckBoxes, (s, v) => s.FilesAndFolders.ShowCheckBoxes = v); }

    public bool OpenItemsWithSingleClick { get => S.FilesAndFolders.OpenItemsWithSingleClick; set => Set("filesAndFolders", value, s => s.FilesAndFolders.OpenItemsWithSingleClick, (s, v) => s.FilesAndFolders.OpenItemsWithSingleClick = v); }

    public bool DoubleClickEmptyAreaGoesUp { get => S.FilesAndFolders.DoubleClickEmptyAreaGoesUp; set => Set("filesAndFolders", value, s => s.FilesAndFolders.DoubleClickEmptyAreaGoesUp, (s, v) => s.FilesAndFolders.DoubleClickEmptyAreaGoesUp = v); }

    public bool CalculateFolderSizes { get => S.FilesAndFolders.CalculateFolderSizes; set => Set("filesAndFolders", value, s => s.FilesAndFolders.CalculateFolderSizes, (s, v) => s.FilesAndFolders.CalculateFolderSizes = v); }

    public bool ConfirmDelete { get => S.FilesAndFolders.ConfirmDelete; set => Set("filesAndFolders", value, s => s.FilesAndFolders.ConfirmDelete, (s, v) => s.FilesAndFolders.ConfirmDelete = v); }

    public bool ShowFileTagsColumn { get => S.FilesAndFolders.ShowFileTagsColumn; set => Set("filesAndFolders", value, s => s.FilesAndFolders.ShowFileTagsColumn, (s, v) => s.FilesAndFolders.ShowFileTagsColumn = v); }

    public bool ListFoldersFirst { get => S.FilesAndFolders.ListFoldersFirst; set => Set("filesAndFolders", value, s => s.FilesAndFolders.ListFoldersFirst, (s, v) => s.FilesAndFolders.ListFoldersFirst = v); }

    public bool RememberViewPerFolder { get => S.FilesAndFolders.RememberViewPerFolder; set => Set("filesAndFolders", value, s => s.FilesAndFolders.RememberViewPerFolder, (s, v) => s.FilesAndFolders.RememberViewPerFolder = v); }

    public Option<FolderLayout> DefaultLayout
    {
        get => Layouts.FirstOrDefault(o => o.Value == S.FilesAndFolders.DefaultLayout) ?? Layouts[0];
        set { if (value is not null) Set("filesAndFolders", value.Value, s => s.FilesAndFolders.DefaultLayout, (s, v) => s.FilesAndFolders.DefaultLayout = v); }
    }

    public Option<SortColumn> DefaultSortColumn
    {
        get => SortColumns.First(o => o.Value == S.FilesAndFolders.DefaultSortColumn);
        set { if (value is not null) Set("filesAndFolders", value.Value, s => s.FilesAndFolders.DefaultSortColumn, (s, v) => s.FilesAndFolders.DefaultSortColumn = v); }
    }

    public bool DefaultSortDescending
    {
        get => S.FilesAndFolders.DefaultSortDirection == SortDirection.Descending;
        set => Set("filesAndFolders", value ? SortDirection.Descending : SortDirection.Ascending, s => s.FilesAndFolders.DefaultSortDirection, (s, v) => s.FilesAndFolders.DefaultSortDirection = v);
    }

    public Option<GroupBy> DefaultGroupBy
    {
        get => GroupOptions.First(o => o.Value == S.FilesAndFolders.DefaultGroupBy);
        set { if (value is not null) Set("filesAndFolders", value.Value, s => s.FilesAndFolders.DefaultGroupBy, (s, v) => s.FilesAndFolders.DefaultGroupBy = v); }
    }

    // ── Sidebar ─────────────────────────────────────────────
    public bool SidebarShowQuickAccess { get => S.Sidebar.ShowQuickAccess; set => Set("sidebarOptions", value, s => s.Sidebar.ShowQuickAccess, (s, v) => s.Sidebar.ShowQuickAccess = v); }

    public bool SidebarShowDrives { get => S.Sidebar.ShowDrives; set => Set("sidebarOptions", value, s => s.Sidebar.ShowDrives, (s, v) => s.Sidebar.ShowDrives = v); }

    public bool SidebarShowNetwork { get => S.Sidebar.ShowNetwork; set => Set("sidebarOptions", value, s => s.Sidebar.ShowNetwork, (s, v) => s.Sidebar.ShowNetwork = v); }

    public bool SidebarShowTags { get => S.Sidebar.ShowTags; set => Set("sidebarOptions", value, s => s.Sidebar.ShowTags, (s, v) => s.Sidebar.ShowTags = v); }

    public bool SidebarShowTrash { get => S.Sidebar.ShowTrash; set => Set("sidebarOptions", value, s => s.Sidebar.ShowTrash, (s, v) => s.Sidebar.ShowTrash = v); }

    // ── File operations ─────────────────────────────────────
    public double MaxConcurrentOperations { get => S.Operations.MaxConcurrentOperations; set => Set("operations", (int)Math.Round(value), s => s.Operations.MaxConcurrentOperations, (s, v) => s.Operations.MaxConcurrentOperations = v); }

    public bool VerifyMovedFileContents { get => S.Operations.VerifyMovedFileContents; set => Set("operations", value, s => s.Operations.VerifyMovedFileContents, (s, v) => s.Operations.VerifyMovedFileContents = v); }

    public bool ShowOperationCompletedNotification { get => S.Operations.ShowOperationCompletedNotification; set => Set("operations", value, s => s.Operations.ShowOperationCompletedNotification, (s, v) => s.Operations.ShowOperationCompletedNotification = v); }

    // ── Search ──────────────────────────────────────────────
    public bool SearchIncludeSubfolders { get => S.Search.IncludeSubfolders; set => Set("search", value, s => s.Search.IncludeSubfolders, (s, v) => s.Search.IncludeSubfolders = v); }

    public bool SearchFileContents { get => S.Search.SearchFileContents; set => Set("search", value, s => s.Search.SearchFileContents, (s, v) => s.Search.SearchFileContents = v); }

    public double SearchMaxResults { get => S.Search.MaxResults; set => Set("search", (int)Math.Round(value), s => s.Search.MaxResults, (s, v) => s.Search.MaxResults = v); }

    public bool SearchKeepHistory { get => S.Search.KeepHistory; set => Set("search", value, s => s.Search.KeepHistory, (s, v) => s.Search.KeepHistory = v); }

    // ── Performance ─────────────────────────────────────────
    public double ThumbnailMemoryCacheMegabytes { get => S.Performance.ThumbnailMemoryCacheMegabytes; set => Set("performance", (int)Math.Round(value), s => s.Performance.ThumbnailMemoryCacheMegabytes, (s, v) => s.Performance.ThumbnailMemoryCacheMegabytes = v); }

    public double ThumbnailDiskCacheMegabytes { get => S.Performance.ThumbnailDiskCacheMegabytes; set => Set("performance", (int)Math.Round(value), s => s.Performance.ThumbnailDiskCacheMegabytes, (s, v) => s.Performance.ThumbnailDiskCacheMegabytes = v); }

    public double MaxBackgroundWorkers { get => S.Performance.MaxBackgroundWorkers; set => Set("performance", (int)Math.Round(value), s => s.Performance.MaxBackgroundWorkers, (s, v) => s.Performance.MaxBackgroundWorkers = v); }

    public double ProcessorCount => Environment.ProcessorCount;

    // ── Updates ─────────────────────────────────────────────
    public bool CheckForUpdatesOnStartup { get => S.Updates.CheckOnStartup; set => Set("updates", value, s => s.Updates.CheckOnStartup, (s, v) => s.Updates.CheckOnStartup = v); }

    public double UpdateCheckIntervalHours { get => S.Updates.CheckIntervalHours; set => Set("updates", (int)Math.Round(value), s => s.Updates.CheckIntervalHours, (s, v) => s.Updates.CheckIntervalHours = v); }

    public Option<UpdateChannel> Channel
    {
        get => Channels.First(o => o.Value == S.Updates.Channel);
        set { if (value is not null) Set("updates", value.Value, s => s.Updates.Channel, (s, v) => s.Updates.Channel = v); }
    }

    // ── Advanced ────────────────────────────────────────────
    public Option<LogVerbosity> LogLevel
    {
        get => LogLevels.First(o => o.Value == S.Advanced.LogLevel);
        set { if (value is not null) Set("advanced", value.Value, s => s.Advanced.LogLevel, (s, v) => s.Advanced.LogLevel = v); }
    }

    public bool EnableExperimentalFeatures { get => S.Advanced.EnableExperimentalFeatures; set => Set("advanced", value, s => s.Advanced.EnableExperimentalFeatures, (s, v) => s.Advanced.EnableExperimentalFeatures = v); }

    public bool ShowPerformanceOverlay { get => S.Advanced.ShowPerformanceOverlay; set => Set("advanced", value, s => s.Advanced.ShowPerformanceOverlay, (s, v) => s.Advanced.ShowPerformanceOverlay = v); }

    public IRelayCommand<string> ResetCommand { get; }

    public IAsyncRelayCommand ResetAllCommand { get; }

    public IAsyncRelayCommand ExportCommand { get; }

    public IAsyncRelayCommand ImportCommand { get; }

    public IAsyncRelayCommand OpenLogFolderCommand { get; }

    public IAsyncRelayCommand OpenSettingsFolderCommand { get; }

    public IRelayCommand AddTagCommand { get; }

    public IRelayCommand<TagRowViewModel> RemoveTagCommand { get; }

    public IRelayCommand<KeyBindingRowViewModel> ResetKeyBindingCommand { get; }

    public IRelayCommand ClearSearchHistoryCommand { get; }

    public IRelayCommand<string> PickAccentCommand { get; }

    public IAsyncRelayCommand PickBackgroundImageCommand { get; }

    private void Set<T>(string section, T value, Func<AppSettings, T> get, Action<AppSettings, T> set, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(get(S), value))
            return;
        _settings.Update(section, s => set(s, value));
        OnPropertyChanged(name);
    }

    private void Register(string name, string section, Action<AppSettings, AppSettings> reset) => _resetters[name] = (section, reset);

    private void RegisterResetters()
    {
        Register(nameof(Language), "language", (s, d) => s.General.Language = d.General.Language);
        Register(nameof(Startup), "general", (s, d) => s.General.Startup = d.General.Startup);
        Register(nameof(OpenFoldersInNewTab), "general", (s, d) => s.General.OpenFoldersInNewTab = d.General.OpenFoldersInNewTab);
        Register(nameof(Theme), "appearance", (s, d) => s.Appearance.Theme = d.Appearance.Theme);
        Register(nameof(AccentColor), "appearance", (s, d) => s.Appearance.AccentColor = d.Appearance.AccentColor);
        Register(nameof(BackgroundColor), "appearance", (s, d) => s.Appearance.BackgroundColor = d.Appearance.BackgroundColor);
        Register(nameof(Backdrop), "appearance", (s, d) => s.Appearance.Backdrop = d.Appearance.Backdrop);
        Register(nameof(Transparency), "appearance", (s, d) => s.Appearance.Transparency = d.Appearance.Transparency);
        Register(nameof(Density), "appearance", (s, d) => s.Appearance.Density = d.Appearance.Density);
        Register(nameof(Animations), "appearance", (s, d) => s.Appearance.Animations = d.Appearance.Animations);
        Register(nameof(FontScale), "appearance", (s, d) => s.Appearance.FontScale = d.Appearance.FontScale);
        Register(nameof(BackgroundImagePath), "appearance", (s, d) => s.Appearance.BackgroundImagePath = d.Appearance.BackgroundImagePath);
        Register(nameof(BackgroundImageOpacity), "appearance", (s, d) => s.Appearance.BackgroundImageOpacity = d.Appearance.BackgroundImageOpacity);
        Register(nameof(DefaultLayout), "filesAndFolders", (s, d) => s.FilesAndFolders.DefaultLayout = d.FilesAndFolders.DefaultLayout);
        Register(nameof(DefaultSortColumn), "filesAndFolders", (s, d) => s.FilesAndFolders.DefaultSortColumn = d.FilesAndFolders.DefaultSortColumn);
        Register(nameof(DefaultGroupBy), "filesAndFolders", (s, d) => s.FilesAndFolders.DefaultGroupBy = d.FilesAndFolders.DefaultGroupBy);
        Register(nameof(MaxConcurrentOperations), "operations", (s, d) => s.Operations.MaxConcurrentOperations = d.Operations.MaxConcurrentOperations);
        Register(nameof(SearchMaxResults), "search", (s, d) => s.Search.MaxResults = d.Search.MaxResults);
        Register(nameof(ThumbnailMemoryCacheMegabytes), "performance", (s, d) => s.Performance.ThumbnailMemoryCacheMegabytes = d.Performance.ThumbnailMemoryCacheMegabytes);
        Register(nameof(ThumbnailDiskCacheMegabytes), "performance", (s, d) => s.Performance.ThumbnailDiskCacheMegabytes = d.Performance.ThumbnailDiskCacheMegabytes);
        Register(nameof(MaxBackgroundWorkers), "performance", (s, d) => s.Performance.MaxBackgroundWorkers = d.Performance.MaxBackgroundWorkers);
        Register(nameof(UpdateCheckIntervalHours), "updates", (s, d) => s.Updates.CheckIntervalHours = d.Updates.CheckIntervalHours);
        Register(nameof(Channel), "updates", (s, d) => s.Updates.Channel = d.Updates.Channel);
        Register(nameof(LogLevel), "advanced", (s, d) => s.Advanced.LogLevel = d.Advanced.LogLevel);
    }

    private void Reset(string? name)
    {
        if (name is null || !_resetters.TryGetValue(name, out var reset))
            return;
        var defaults = new AppSettings();
        _settings.Update(reset.Section, s => reset.Reset(s, defaults));
        if (name == nameof(Language))
            _localizer.SetLanguage(S.General.Language);
        OnPropertyChanged(name);
    }

    public bool CanReset(string name) => _resetters.ContainsKey(name);

    private async Task ResetAllAsync()
    {
        var confirm = await _dialogs.ConfirmAsync(new ConfirmRequest(_localizer["Settings.ResetAllTitle"], _localizer["Settings.ResetAllMessage"], _localizer["Settings.ResetAll"], IsDestructive: true)).ConfigureAwait(true);
        if (confirm.Result != DialogResult.Primary)
            return;
        _settings.ResetAll();
        _localizer.SetLanguage(S.General.Language);
        OnPropertyChanged(string.Empty);
    }

    private async Task ExportAsync()
    {
        var path = await _dialogs.PickSaveFileAsync(_localizer["Settings.Export"], "navigator-settings.json", ".json").ConfigureAwait(true);
        if (path is null)
            return;
        try
        {
            _settings.Export(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowMessageAsync(_localizer["Settings.Export"], ex.Message).ConfigureAwait(true);
        }
    }

    private async Task ImportAsync()
    {
        var path = await _dialogs.PickFileAsync(_localizer["Settings.Import"], [".json"]).ConfigureAwait(true);
        if (path is null)
            return;
        try
        {
            _settings.Import(path);
            _localizer.SetLanguage(S.General.Language);
            OnPropertyChanged(string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await _dialogs.ShowMessageAsync(_localizer["Settings.Import"], ex.Message).ConfigureAwait(true);
        }
    }

    private async Task PickBackgroundImageAsync()
    {
        var path = await _dialogs.PickFileAsync(_localizer["Settings.BackgroundImage"], [".png", ".jpg", ".jpeg", ".webp", ".bmp"]).ConfigureAwait(true);
        if (path is not null)
            BackgroundImagePath = path;
    }

    private void AddTag()
    {
        var tag = new TagDefinition { Id = Guid.NewGuid().ToString("N")[..8], Name = _localizer["Settings.NewTag"], Color = "#808080" };
        _settings.Update("tags", s => s.Tags.Add(tag));
        TagRows.Add(new TagRowViewModel(tag, _ => SaveTags()));
    }

    private void RemoveTag(TagRowViewModel? row)
    {
        if (row is null)
            return;
        _settings.Update("tags", s => s.Tags.RemoveAll(t => t.Id == row.Id));
        _tags.RemoveTagEverywhere(row.Id);
        TagRows.Remove(row);
    }

    private void SaveTags() => _settings.Update("tags", _ => { });

    private void SaveKeyBinding(string id, IReadOnlyList<string>? gestures)
    {
        _settings.Update("keyBindings", s =>
        {
            if (gestures is null)
                s.KeyBindings.Remove(id);
            else
                s.KeyBindings[id] = [.. gestures];
        });
        OnPropertyChanged(nameof(KeyBindingConflicts));
    }
}
