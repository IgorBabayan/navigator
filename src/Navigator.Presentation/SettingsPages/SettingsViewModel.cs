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

/// <summary>
/// One command in Settings → Keyboard shortcuts. Gestures are edited as comma-separated text or recorded from
/// the keyboard (the window forwards the next key press while <see cref="IsRecording"/> is set).
/// </summary>
public sealed class KeyBindingRowViewModel : ObservableObject
{
    private readonly AppCommandInfo _command;
    private readonly SettingsViewModel _owner;
    private string? _editText;
    private string? _error;
    private bool _isRecording;
    private bool _hasConflict;
    private bool _isVisible = true;
    private string _searchText = string.Empty;

    internal KeyBindingRowViewModel(AppCommandInfo command, string name, string category, SettingsViewModel owner)
    {
        _command = command;
        _owner = owner;
        Name = name;
        Category = category;
        DefaultGesturesText = owner.FormatGestures(CommandRegistry.NormalizedDefaults(command.Id, owner.IsMac));
        DefaultGesturesCaption = owner.FormatDefaultCaption(DefaultGesturesText);
        RebuildSearchText();
    }

    public string Id => _command.Id;

    public string Name { get; }

    public string Category { get; }

    public string DefaultGesturesText { get; }

    /// <summary>Shown under the name when the binding was changed, so the user knows what "reset" restores.</summary>
    public string DefaultGesturesCaption { get; }

    /// <summary>Comma-separated, human-friendly gestures. Setting it validates and saves (or shows <see cref="Error"/>).</summary>
    public string Gestures
    {
        get => _editText ?? _owner.FormatGestures(_owner.CurrentGestures(Id));
        set
        {
            if (value == Gestures)
                return;
            var parsed = _owner.ParseGestures(value, out var invalid);
            if (parsed is null)
            {
                _editText = value;
                Error = invalid;
                return;
            }

            _editText = null;
            Error = null;
            _owner.SaveKeyBinding(this, parsed);
        }
    }

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => _error is not null;

    public bool IsModified => _owner.IsKeyBindingModified(Id);

    public bool HasConflict
    {
        get => _hasConflict;
        internal set => SetProperty(ref _hasConflict, value);
    }

    public bool IsRecording
    {
        get => _isRecording;
        internal set => SetProperty(ref _isRecording, value);
    }

    /// <summary>False while the row is filtered out by the search box (cheaper than rebuilding the list).</summary>
    public bool IsVisible
    {
        get => _isVisible;
        internal set => SetProperty(ref _isVisible, value);
    }

    /// <summary>Called when a recorded gesture arrives; replaces the command's gestures with it.</summary>
    internal void ApplyRecorded(string normalized)
    {
        _editText = null;
        Error = null;
        _owner.SaveKeyBinding(this, [normalized]);
    }

    internal void ResetToDefault()
    {
        _editText = null;
        Error = null;
        _owner.SaveKeyBinding(this, null);
    }

    /// <summary>Re-reads the effective gestures after a change (own save, reset all, import).</summary>
    internal void Refresh()
    {
        _editText = null;
        Error = null;
        RebuildSearchText();
        OnPropertyChanged(nameof(Gestures));
        OnPropertyChanged(nameof(IsModified));
    }

    /// <summary>Every search term must occur in the name, category, id or current/default gestures.</summary>
    internal bool Matches(string[] terms)
    {
        foreach (var term in terms)
        {
            if (!_searchText.Contains(term, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    // Precomputed so filtering while typing is a few string scans per row, with no allocations.
    private void RebuildSearchText()
        => _searchText = string.Join('\n', Name, Category, Id, Gestures, DefaultGesturesText);
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
    private string _keyBindingFilter = string.Empty;
    private string _keyBindingConflicts = string.Empty;
    private bool _hasKeyBindingResults = true;
    private KeyBindingRowViewModel? _recordingRow;
    private IReadOnlyList<Option<string>>? _fontFamilies;

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

        ReloadTagRows();
        ReloadStartupLocations();
        foreach (var command in CommandRegistry.All)
            KeyBindings.Add(new KeyBindingRowViewModel(command, localizer[command.NameKey], localizer["CommandCategory." + command.Category], this));
        UpdateKeyBindingConflicts();

        ResetCommand = new RelayCommand<string>(Reset);
        ResetAllCommand = new AsyncRelayCommand(ResetAllAsync);
        ExportCommand = new AsyncRelayCommand(ExportAsync);
        ImportCommand = new AsyncRelayCommand(ImportAsync);
        OpenLogFolderCommand = new AsyncRelayCommand(() => _launcher.OpenAsync(_directories.LogDirectory));
        OpenSettingsFolderCommand = new AsyncRelayCommand(() => _launcher.OpenAsync(_directories.ConfigDirectory));
        AddTagCommand = new RelayCommand(AddTag);
        RemoveTagCommand = new RelayCommand<TagRowViewModel>(RemoveTag);
        ResetKeyBindingCommand = new RelayCommand<KeyBindingRowViewModel>(row =>
        {
            CancelKeyBindingRecording();
            row?.ResetToDefault();
        });
        ResetAllKeyBindingsCommand = new AsyncRelayCommand(ResetAllKeyBindingsAsync);
        RecordKeyBindingCommand = new RelayCommand<KeyBindingRowViewModel>(ToggleRecording);
        AddStartupLocationCommand = new AsyncRelayCommand(AddStartupLocationAsync);
        RemoveStartupLocationCommand = new RelayCommand<string>(RemoveStartupLocation);
        ClearAddressHistoryCommand = new RelayCommand(() => _settings.Update("general", s => s.General.AddressHistory.Clear()));
        ClearFolderViewsCommand = new RelayCommand(() => _settings.Update("filesAndFolders", s => s.FilesAndFolders.FolderViews.Clear()));
        ClearSkippedVersionCommand = new RelayCommand(() => Set("updates", (string?)null, s => s.Updates.SkippedVersion, (s, v) => s.Updates.SkippedVersion = v, nameof(SkippedVersion)));
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
        set
        {
            if (!SetProperty(ref _selectedPage, value))
                return;
            CancelKeyBindingRecording();
            if (value?.Id == "appearance")
                EnsureFontFamilies();
        }
    }

    internal bool IsMac { get; } = OperatingSystem.IsMacOS();

    /// <summary>
    /// Set by the view: returns false for gestures the UI framework cannot parse (unknown key names).
    /// Without it only the structure of a gesture is validated.
    /// </summary>
    public Func<string, bool>? GestureValidator { get; set; }

    /// <summary>Set by the view: installed font family names. Enumerated lazily, on the first visit of the Appearance page.</summary>
    public Func<IEnumerable<string>>? FontFamilyProvider { get; set; }

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

    public ObservableCollection<string> StartupLocations { get; } = [];

    /// <summary>Search text for the shortcut list: matches command name, category, id and gestures ("ctrl+t", "tab close").</summary>
    public string KeyBindingFilter
    {
        get => _keyBindingFilter;
        set
        {
            value ??= string.Empty;
            if (!SetProperty(ref _keyBindingFilter, value))
                return;
            ApplyKeyBindingFilter();
        }
    }

    public bool HasKeyBindingResults
    {
        get => _hasKeyBindingResults;
        private set => SetProperty(ref _hasKeyBindingResults, value);
    }

    public string KeyBindingConflicts
    {
        get => _keyBindingConflicts;
        private set
        {
            if (SetProperty(ref _keyBindingConflicts, value))
                OnPropertyChanged(nameof(HasKeyBindingConflicts));
        }
    }

    public bool HasKeyBindingConflicts => _keyBindingConflicts.Length > 0;

    public bool IsRecordingKeyBinding => _recordingRow is not null;

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
        set
        {
            if (value is null)
                return;
            Set("general", value.Value, s => s.General.Startup, (s, v) => s.General.Startup = v);
            OnPropertyChanged(nameof(IsStartupWithSpecificLocations));
        }
    }

    public bool IsStartupWithSpecificLocations => S.General.Startup == StartupBehavior.OpenSpecificLocations;

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

    /// <summary>"System default" followed by the installed families; filled on the first visit of the Appearance page.</summary>
    public IReadOnlyList<Option<string>> FontFamilies
    {
        get
        {
            if (_fontFamilies is not null)
                return _fontFamilies;

            // Before the fonts are enumerated only the current choice is offered, so the combo box still shows it.
            var system = new Option<string>(string.Empty, _localizer["Settings.FontFamilySystem"]);
            return S.Appearance.FontFamily is { } current ? [system, new Option<string>(current, current)] : [system];
        }
    }

    private void EnsureFontFamilies()
    {
        if (_fontFamilies is not null || FontFamilyProvider is null)
            return;
        var current = S.Appearance.FontFamily;
        IEnumerable<string> names = FontFamilyProvider().Where(f => !string.IsNullOrWhiteSpace(f));
        if (current is not null)
            names = names.Append(current); // keep a configured font that is not installed (any more) selectable
        _fontFamilies = [new Option<string>(string.Empty, _localizer["Settings.FontFamilySystem"]),
            .. names.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).Select(f => new Option<string>(f, f))];
        OnPropertyChanged(nameof(FontFamilies));
        OnPropertyChanged(nameof(FontFamily));
    }

    public Option<string>? FontFamily
    {
        get
        {
            var current = S.Appearance.FontFamily ?? string.Empty;
            return FontFamilies.FirstOrDefault(f => string.Equals(f.Value, current, StringComparison.OrdinalIgnoreCase));
        }
        set
        {
            if (value is not null)
                Set("appearance", value.Value.Length == 0 ? null : value.Value, s => s.Appearance.FontFamily, (s, v) => s.Appearance.FontFamily = v);
        }
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
    public bool SidebarIsVisible { get => S.Sidebar.IsVisible; set => Set("sidebarVisible", value, s => s.Sidebar.IsVisible, (s, v) => s.Sidebar.IsVisible = v); }

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

    public string? SkippedVersion => S.Updates.SkippedVersion;

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

    public IAsyncRelayCommand ResetAllKeyBindingsCommand { get; }

    public IRelayCommand<KeyBindingRowViewModel> RecordKeyBindingCommand { get; }

    public IAsyncRelayCommand AddStartupLocationCommand { get; }

    public IRelayCommand<string> RemoveStartupLocationCommand { get; }

    public IRelayCommand ClearAddressHistoryCommand { get; }

    public IRelayCommand ClearFolderViewsCommand { get; }

    public IRelayCommand ClearSkippedVersionCommand { get; }

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
        Register(nameof(FontFamily), "appearance", (s, d) => s.Appearance.FontFamily = d.Appearance.FontFamily);
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
        ReloadAfterReplace();
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
            ReloadAfterReplace();
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

    /// <summary>The settings object was replaced (reset all, import): row view models must re-bind to it.</summary>
    private void ReloadAfterReplace()
    {
        CancelKeyBindingRecording();
        ReloadTagRows();
        ReloadStartupLocations();
        foreach (var row in KeyBindings)
            row.Refresh();
        UpdateKeyBindingConflicts();
        ApplyKeyBindingFilter();
        OnPropertyChanged(string.Empty);
    }

    private void ReloadTagRows()
    {
        TagRows.Clear();
        foreach (var tag in S.Tags)
            TagRows.Add(new TagRowViewModel(tag, _ => SaveTags()));
    }

    // ── Startup locations ───────────────────────────────────

    private void ReloadStartupLocations()
    {
        StartupLocations.Clear();
        foreach (var location in S.General.StartupLocations)
            StartupLocations.Add(location);
    }

    private async Task AddStartupLocationAsync()
    {
        var path = await _dialogs.PickFolderAsync(_localizer["Settings.AddStartupLocation"]).ConfigureAwait(true);
        if (path is null || S.General.StartupLocations.Contains(path, StringComparer.Ordinal))
            return;
        _settings.Update("general", s => s.General.StartupLocations.Add(path));
        StartupLocations.Add(path);
    }

    private void RemoveStartupLocation(string? path)
    {
        if (path is null)
            return;
        _settings.Update("general", s => s.General.StartupLocations.Remove(path));
        StartupLocations.Remove(path);
    }

    // ── Keyboard shortcuts ──────────────────────────────────

    internal string FormatDefaultCaption(string defaults)
        => _localizer.Format("Settings.DefaultShortcut", defaults.Length > 0 ? defaults : _localizer["Settings.ShortcutNotAssigned"]);

    internal IReadOnlyList<string> CurrentGestures(string id) => CommandRegistry.GesturesFor(id, S, IsMac);

    internal bool IsKeyBindingModified(string id) => S.KeyBindings.ContainsKey(id);

    internal string FormatGestures(IReadOnlyList<string> gestures)
        => gestures.Count switch
        {
            0 => string.Empty,
            1 => CommandRegistry.FormatForDisplay(CommandRegistry.ResolvePrimary(gestures[0], IsMac)),
            _ => string.Join(", ", gestures.Select(g => CommandRegistry.FormatForDisplay(CommandRegistry.ResolvePrimary(g, IsMac)))),
        };

    /// <summary>
    /// Parses comma-separated gesture text into canonical gestures. An empty text means "no shortcut".
    /// Returns null and a user-facing message when any part is invalid.
    /// </summary>
    internal List<string>? ParseGestures(string? text, out string? error)
    {
        error = null;
        var result = new List<string>();
        foreach (var part in SplitGestureList(text ?? string.Empty))
        {
            var normalized = NormalizeAndValidate(part);
            if (normalized is null)
            {
                error = _localizer.Format("Settings.InvalidShortcut", part);
                return null;
            }

            if (!result.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                result.Add(normalized);
        }

        return result;
    }

    private string? NormalizeAndValidate(string text)
    {
        var normalized = CommandRegistry.NormalizeGesture(text, IsMac);
        if (normalized is null)
            return null;
        if (GestureValidator is { } validator && !validator(CommandRegistry.ResolvePrimary(normalized, IsMac)))
            return null;
        return normalized;
    }

    /// <summary>Splits "Ctrl+R, F5" on commas, keeping a comma that is itself the key ("Ctrl+,").</summary>
    internal static List<string> SplitGestureList(string text)
    {
        var parts = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != ',')
                continue;
            var before = text.AsSpan(start, i - start).TrimEnd();
            if (before.Length == 0 || before[^1] == '+')
                continue; // the comma is the key of this gesture
            parts.Add(before.Trim().ToString());
            start = i + 1;
        }

        var last = text.AsSpan(start).Trim();
        if (last.Length > 0)
            parts.Add(last.ToString());
        return parts;
    }

    internal void SaveKeyBinding(KeyBindingRowViewModel row, List<string>? gestures)
    {
        // Storing the defaults as an override would hide future default changes: treat it as a reset.
        if (gestures is not null && gestures.SequenceEqual(CommandRegistry.NormalizedDefaults(row.Id, IsMac), StringComparer.OrdinalIgnoreCase))
            gestures = null;
        _settings.Update("keyBindings", s =>
        {
            if (gestures is null)
                s.KeyBindings.Remove(row.Id);
            else
                s.KeyBindings[row.Id] = gestures;
        });
        row.Refresh();
        UpdateKeyBindingConflicts();
    }

    private void UpdateKeyBindingConflicts()
    {
        var conflicts = CommandRegistry.FindConflicts(S, IsMac);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var conflict in conflicts)
            ids.UnionWith(conflict.CommandIds);
        foreach (var row in KeyBindings)
            row.HasConflict = ids.Contains(row.Id);
        KeyBindingConflicts = string.Join(Environment.NewLine, conflicts.Select(c =>
            $"{CommandRegistry.FormatForDisplay(c.Gesture)}: {string.Join(", ", c.CommandIds.Select(id => _localizer[CommandRegistry.Find(id)!.NameKey]))}"));
    }

    private void ApplyKeyBindingFilter()
    {
        var terms = _keyBindingFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var any = false;
        foreach (var row in KeyBindings)
        {
            var visible = terms.Length == 0 || row.Matches(terms);
            row.IsVisible = visible;
            any |= visible;
        }

        HasKeyBindingResults = any;
    }

    private void ToggleRecording(KeyBindingRowViewModel? row)
    {
        var wasRecording = _recordingRow == row;
        CancelKeyBindingRecording();
        if (row is null || wasRecording)
            return;
        _recordingRow = row;
        row.IsRecording = true;
        OnPropertyChanged(nameof(IsRecordingKeyBinding));
    }

    /// <summary>Called by the view with the pressed key combination (e.g. "Primary+Shift+T") while recording.</summary>
    public void CompleteKeyBindingRecording(string gesture)
    {
        if (_recordingRow is not { } row)
            return;
        CancelKeyBindingRecording();
        if (NormalizeAndValidate(gesture) is { } normalized)
            row.ApplyRecorded(normalized);
    }

    public void CancelKeyBindingRecording()
    {
        if (_recordingRow is null)
            return;
        _recordingRow.IsRecording = false;
        _recordingRow = null;
        OnPropertyChanged(nameof(IsRecordingKeyBinding));
    }

    private async Task ResetAllKeyBindingsAsync()
    {
        CancelKeyBindingRecording();
        if (S.KeyBindings.Count == 0)
            return;
        var confirm = await _dialogs.ConfirmAsync(new ConfirmRequest(_localizer["Settings.ResetShortcutsTitle"], _localizer["Settings.ResetShortcutsMessage"], _localizer["Settings.ResetShortcuts"], IsDestructive: true)).ConfigureAwait(true);
        if (confirm.Result != DialogResult.Primary)
            return;
        _settings.Update("keyBindings", s => s.KeyBindings.Clear());
        foreach (var row in KeyBindings)
            row.Refresh();
        UpdateKeyBindingConflicts();
        ApplyKeyBindingFilter();
    }
}
