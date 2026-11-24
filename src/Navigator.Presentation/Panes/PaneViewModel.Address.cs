using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Navigator.Core.IO;
using Navigator.Core.Navigation;
using Navigator.Core.Platform;
using Navigator.Presentation.Collections;

namespace Navigator.Presentation.Panes;

/// <summary>
/// Omnibar behaviour, modelled on a desktop file manager's address bar: typed-path navigation (absolute, relative,
/// "~", environment variables, shell names), folder autocompletion while typing, a most-recent-first history of typed
/// addresses, chevron lists of sub-folders for every breadcrumb segment, and copy/open-in-new-tab commands.
/// </summary>
public sealed partial class PaneViewModel
{
    /// <summary>Typed addresses kept in settings (the same depth a desktop file manager keeps).</summary>
    public const int AddressHistoryCapacity = 25;

    private const int MaxFolderSuggestions = 200;
    private const int MaxHistorySuggestions = 6;
    private const int MaxChevronEntries = 500;
    private const int SuggestionDelayMilliseconds = 120;

    private CancellationTokenSource? _suggest;
    private bool _suppressAddressSuggestions;
    private bool _isAddressDropDownOpen;
    private bool _isShowingAddressHistory;

    /// <summary>Rows of the omnibar drop-down: completions while typing, or the typed-address history.</summary>
    public RangeObservableCollection<AddressSuggestion> AddressSuggestions { get; } = [];

    public bool IsAddressDropDownOpen
    {
        get => _isAddressDropDownOpen;
        set => SetProperty(ref _isAddressDropDownOpen, value);
    }

    /// <summary>True while the drop-down lists the history rather than completions.</summary>
    public bool IsShowingAddressHistory
    {
        get => _isShowingAddressHistory;
        private set => SetProperty(ref _isShowingAddressHistory, value);
    }

    public IRelayCommand CopyAddressCommand { get; private set; } = null!;

    public IRelayCommand ClearAddressHistoryCommand { get; private set; } = null!;

    public IRelayCommand ToggleAddressHistoryCommand { get; private set; } = null!;

    public IRelayCommand<BreadcrumbViewModel> OpenBreadcrumbInNewTabCommand { get; private set; } = null!;

    public IRelayCommand<BreadcrumbViewModel> CopyBreadcrumbPathCommand { get; private set; } = null!;

    private void InitializeAddressCommands()
    {
        CopyAddressCommand = new AsyncRelayCommand(() => CopyTextAsync(_location.Path), () => _location.Path is not null);
        ClearAddressHistoryCommand = new RelayCommand(ClearAddressHistory);
        ToggleAddressHistoryCommand = new RelayCommand(ToggleAddressHistory);
        OpenBreadcrumbInNewTabCommand = new RelayCommand<BreadcrumbViewModel>(crumb =>
        {
            if (crumb is { HasPath: true })
                OpenInNewTab(Location.Folder(crumb.Path));
        });
        CopyBreadcrumbPathCommand = new AsyncRelayCommand<BreadcrumbViewModel>(crumb => CopyTextAsync(crumb?.HasPath == true ? crumb.Path : null));
    }

    /// <summary>Opens a location in a new tab (middle-click on a breadcrumb or chevron entry).</summary>
    public void OpenInNewTab(Location location) => OpenInNewTabRequested?.Invoke(this, location);

    // ── Typed navigation ──────────────────────────────────────

    /// <summary>
    /// Resolution order: an existing absolute or expanded path ("~", %VAR%, $VAR, file://), a path relative to the
    /// current folder (including ".."), then a shell name ("Downloads", "Recycle Bin", "shell:Desktop").
    /// Folders open in place, files open with their default app; successful entries are added to the history.
    /// </summary>
    private async Task NavigateToAddressAsync()
    {
        CloseAddressDropDown();
        var input = AddressText.Trim();
        if (input.Length == 0)
        {
            IsEditingAddress = false;
            return;
        }

        var home = _services.Platform.HomeDirectory;
        var resolved = PathUtil.ExpandUserInput(input, home);
        if (resolved is null && !Path.IsPathRooted(input.Trim('"')) && CurrentDirectory is { } current)
            resolved = PathUtil.ExpandUserInput(Path.Combine(current, input.Trim('"')), home);

        if (resolved is not null && Directory.Exists(resolved))
        {
            RememberAddress(resolved);
            Navigate(Location.Folder(resolved));
            return;
        }

        if (resolved is not null && File.Exists(resolved))
        {
            RememberAddress(resolved);
            IsEditingAddress = false;
            await _services.Launcher.OpenAsync(resolved).ConfigureAwait(true);
            return;
        }

        if (AddressAliases.Resolve(input, _services.Localizer, _services.KnownFolders) is { } alias)
        {
            RememberAddress(input);
            Navigate(alias);
            return;
        }

        await _services.Dialogs.ShowMessageAsync(_services.Localizer["Error.PathNotFoundTitle"], _services.Localizer.Format("Error.PathNotFound", input)).ConfigureAwait(true);
    }

    private void RememberAddress(string entry)
        => _services.Settings.Update("general", s => AddressCompletion.Remember(s.General.AddressHistory, entry, AddressHistoryCapacity));

    private void ClearAddressHistory()
    {
        _services.Settings.Update("general", s => s.General.AddressHistory.Clear());
        if (IsShowingAddressHistory)
            CloseAddressDropDown();
    }

    private async Task CopyTextAsync(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        try
        {
            await _services.Clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Copying the address to the clipboard failed");
        }
    }

    // ── Drop-down: history and completions ───────────────────

    /// <summary>Puts text into the address box without starting a completion request (keyboard browsing of the list).</summary>
    public void SetAddressTextSilently(string text)
    {
        _suppressAddressSuggestions = true;
        try
        {
            AddressText = text;
        }
        finally
        {
            _suppressAddressSuggestions = false;
        }
    }

    /// <summary>Enters edit mode and lists the typed-address history (the drop-down arrow, F4, Alt+Down).</summary>
    public void ShowAddressHistory()
    {
        IsEditingAddress = true;
        CancelAddressSuggestions();
        var history = _services.Settings.Current.General.AddressHistory;
        AddressSuggestions.ReplaceAll(history.Select(ToHistorySuggestion).ToList());
        IsShowingAddressHistory = true;
        IsAddressDropDownOpen = AddressSuggestions.Count > 0;
    }

    public void ToggleAddressHistory()
    {
        if (IsAddressDropDownOpen && IsShowingAddressHistory)
            CloseAddressDropDown();
        else
            ShowAddressHistory();
    }

    public void CloseAddressDropDown()
    {
        CancelAddressSuggestions();
        IsAddressDropDownOpen = false;
        IsShowingAddressHistory = false;
    }

    /// <summary>Puts a drop-down row into the address box and navigates to it.</summary>
    public void AcceptAddressSuggestion(AddressSuggestion? suggestion)
    {
        if (suggestion is null)
            return;
        SetAddressTextSilently(suggestion.Text);
        NavigateToAddressCommand.Execute(null);
    }

    private void ScheduleAddressSuggestions()
    {
        CancelAddressSuggestions();
        var source = new CancellationTokenSource();
        _suggest = source;
        _ = UpdateAddressSuggestionsAsync(AddressText, source.Token);
    }

    private void CancelAddressSuggestions()
    {
        var source = _suggest;
        _suggest = null;
        if (source is null)
            return;
        source.Cancel();
        source.Dispose();
    }

    private async Task UpdateAddressSuggestionsAsync(string typed, CancellationToken token)
    {
        try
        {
            await Task.Delay(SuggestionDelayMilliseconds, token).ConfigureAwait(true);
            var text = typed.Trim();
            if (text.Length == 0)
            {
                ShowAddressHistory();
                return;
            }

            var settings = _services.Settings.Current;
            var input = AddressCompletion.Split(text.Trim('"'));
            var home = _services.Platform.HomeDirectory;
            var current = CurrentDirectory;
            var includeHidden = settings.FilesAndFolders.ShowHiddenItems;
            var (parent, names) = await Task.Run(() => FindCompletions(input, home, current, includeHidden, token), token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();

            var suggestions = new List<AddressSuggestion>();
            var seen = new HashSet<string>(PathUtil.Comparer);

            foreach (var entry in settings.General.AddressHistory)
            {
                if (suggestions.Count >= MaxHistorySuggestions)
                    break;
                if (entry.StartsWith(text, StringComparison.OrdinalIgnoreCase) && seen.Add(entry))
                    suggestions.Add(ToHistorySuggestion(entry));
            }

            // Shell names are offered for a bare word ("Dow" → Downloads), like a desktop address bar does.
            if (input.ParentText.Length == 0 && input.Prefix.Length > 0)
            {
                foreach (var (name, location, icon) in AddressAliases.All(_services.Localizer, _services.KnownFolders))
                {
                    if (name.StartsWith(input.Prefix, StringComparison.OrdinalIgnoreCase) && seen.Add(location.Path ?? name))
                        suggestions.Add(new AddressSuggestion(name, name, location.Path ?? string.Empty, AddressSuggestionKind.Location, icon));
                }
            }

            if (parent is not null)
            {
                foreach (var name in names)
                {
                    var full = Path.Combine(parent, name);
                    if (seen.Add(full))
                        suggestions.Add(new AddressSuggestion(input.ParentText + name, name, full, AddressSuggestionKind.Folder, "Folder"));
                }
            }

            if (!IsEditingAddress)
                return;
            AddressSuggestions.ReplaceAll(suggestions);
            IsShowingAddressHistory = false;
            IsAddressDropDownOpen = suggestions.Count > 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Superseded by newer input.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogDebug(ex, "Address completion failed");
        }
    }

    private static (string? Parent, IReadOnlyList<string> Names) FindCompletions(AddressInput input, string home, string? current, bool includeHidden, CancellationToken token)
    {
        var parent = AddressCompletion.ResolveParent(input, home, current);
        if (parent is null)
            return (null, Array.Empty<string>());
        return (parent, AddressCompletion.FindFolders(parent, input.Prefix, includeHidden, MaxFolderSuggestions, token));
    }

    private AddressSuggestion ToHistorySuggestion(string entry)
    {
        var name = Path.GetFileName(PathUtil.TrimTrailingSeparator(entry));
        return new AddressSuggestion(entry, string.IsNullOrEmpty(name) ? entry : name, entry, AddressSuggestionKind.History, "History");
    }

    // ── Breadcrumb chevrons ───────────────────────────────────

    /// <summary>Sub-folders of a breadcrumb segment, for its chevron list. The segment leading to the current folder is flagged.</summary>
    public async Task<IReadOnlyList<BreadcrumbChild>> GetBreadcrumbChildrenAsync(BreadcrumbViewModel crumb, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(crumb);
        if (!crumb.HasPath)
            return [];

        var includeHidden = _services.Settings.Current.FilesAndFolders.ShowHiddenItems;
        var names = await Task.Run(() => AddressCompletion.FindFolders(crumb.Path, string.Empty, includeHidden, MaxChevronEntries, cancellationToken), cancellationToken).ConfigureAwait(true);
        return [.. names.Select(name =>
        {
            var full = Path.Combine(crumb.Path, name);
            return new BreadcrumbChild(name, Location.Folder(full), "Folder", crumb.NextPath is not null && PathUtil.AreEqual(full, crumb.NextPath));
        })];
    }

    /// <summary>Entries of the leading chevron: Home, the user folders, every ready drive and the trash.</summary>
    public async Task<IReadOnlyList<BreadcrumbChild>> GetRootLocationsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<BreadcrumbChild>();
        foreach (var (name, location, icon) in AddressAliases.All(_services.Localizer, _services.KnownFolders))
        {
            if (location.Kind == LocationKind.Trash)
                continue;
            result.Add(new BreadcrumbChild(name, location, icon, IsOnRootPath(location)));
        }

        var volumes = await Task.Run(() => _services.Volumes.GetVolumes(), cancellationToken).ConfigureAwait(true);
        foreach (var volume in volumes.Where(v => v.IsReady))
        {
            var name = string.IsNullOrWhiteSpace(volume.Label) ? volume.RootPath : $"{volume.Label} ({PathUtil.TrimTrailingSeparator(volume.RootPath)})";
            var location = Location.Folder(volume.RootPath);
            result.Add(new BreadcrumbChild(name, location, DriveIcon(volume.Kind), IsOnRootPath(location)));
        }

        result.Add(new BreadcrumbChild(_services.Localizer[OperatingSystem.IsWindows() ? "Location.RecycleBin" : "Location.Trash"], Location.Trash, "Trash", IsTrash));
        return result;
    }

    private bool IsOnRootPath(Location location)
        => location.Kind == _location.Kind && (location.Path is null || (_location.Path is { } current && PathUtil.AreEqual(location.Path, current)));

    private static string DriveIcon(VolumeKind kind) => kind switch
    {
        VolumeKind.Removable => "DriveRemovable",
        VolumeKind.Network => "DriveNetwork",
        VolumeKind.Optical => "DriveOptical",
        _ => "Drive",
    };
}
