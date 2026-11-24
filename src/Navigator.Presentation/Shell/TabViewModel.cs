using CommunityToolkit.Mvvm.ComponentModel;
using Navigator.Core.Navigation;
using Navigator.Core.Settings;
using Navigator.Presentation.Panes;
using Navigator.Presentation.Services;

namespace Navigator.Presentation.Shell;

/// <summary>A tab: one pane, or two side by side in split (dual pane) view, each with its own history.</summary>
public sealed class TabViewModel : ObservableObject, IDisposable
{
    private readonly PaneServices _services;
    private readonly PinnedFolders _pins;
    private PaneViewModel? _secondary;
    private PaneViewModel _activePane;
    private bool _isPinned;
    private bool _isSelected;

    public TabViewModel(PaneServices services, PinnedFolders pins, Location location, Location? secondary = null)
    {
        _services = services;
        _pins = pins;
        Primary = CreatePane(location);
        _activePane = Primary;
        Primary.IsActive = true;
        if (secondary is not null)
            EnableSplit(secondary);
        _services.TrashStatus.PropertyChanged += OnTrashStatusChanged;
    }

    public PaneViewModel Primary { get; }

    public PaneViewModel? Secondary => _secondary;

    public bool IsSplit => _secondary is not null;

    public PaneViewModel ActivePane
    {
        get => _activePane;
        set
        {
            if (value != Primary && value != _secondary)
                return;
            var previous = _activePane;
            if (SetProperty(ref _activePane, value))
            {
                previous.IsActive = false;
                // The omnibar edits the active pane only: an address edit left open in the other pane must not reappear later.
                previous.IsEditingAddress = false;
                value.IsActive = true;
                RaiseHeader();
                ActivePaneChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool IsPinned
    {
        get => _isPinned;
        set => SetProperty(ref _isPinned, value);
    }

    /// <summary>Set by the shell; drives the selected tab visual.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>Title of the pane; in split view both titles, left first, separated by " | ".</summary>
    public string Header => _secondary is { } secondary ? Primary.Title + " | " + secondary.Title : Primary.Title;

    public string IconKey => ActivePane.Location.Kind switch
    {
        LocationKind.Home => "Home",
        LocationKind.Trash => "Trash",
        LocationKind.Search => "Search",
        LocationKind.Tag => "Tag",
        _ => "Folder",
    };

    /// <summary>OS-drawn icon for locations that have one (Recycle Bin); null keeps <see cref="IconKey"/> at no cost.</summary>
    public string? SystemIconKey => ActivePane.Location.Kind == LocationKind.Trash ? _services.TrashStatus.SystemIconKey : null;

    public string ToolTip => _secondary is { } secondary
        ? PaneToolTip(Primary) + Environment.NewLine + PaneToolTip(secondary)
        : PaneToolTip(Primary);

    public event EventHandler<Location>? OpenInNewTabRequested;

    public event EventHandler? ActivePaneChanged;

    public void Start()
    {
        Primary.Start();
        _secondary?.Start();
    }

    public void ToggleSplit()
    {
        if (_secondary is null)
        {
            EnableSplit(ActivePane.Location);
            _secondary!.Start();
            ActivePane = _secondary;
        }
        else
        {
            var closing = _secondary;
            ActivePane = Primary;
            _secondary = null;
            closing.Dispose();
            OnPropertyChanged(nameof(Secondary));
            OnPropertyChanged(nameof(IsSplit));
            RaiseHeader();
        }
    }

    private void EnableSplit(Location location)
    {
        _secondary = CreatePane(location);
        OnPropertyChanged(nameof(Secondary));
        OnPropertyChanged(nameof(IsSplit));
        RaiseHeader();
    }

    private PaneViewModel CreatePane(Location location)
    {
        var pane = new PaneViewModel(_services, _pins, location);
        pane.OpenInNewTabRequested += (_, target) => OpenInNewTabRequested?.Invoke(this, target);
        // Either pane changes the header in split view (it shows both titles).
        pane.Navigated += (_, _) => RaiseHeader();
        return pane;
    }

    private static string PaneToolTip(PaneViewModel pane) => pane.Location.Path ?? pane.Title;

    private void RaiseHeader()
    {
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(IconKey));
        OnPropertyChanged(nameof(SystemIconKey));
        OnPropertyChanged(nameof(ToolTip));
    }

    public TabSession ToSession() => new()
    {
        Location = Primary.Location.Serialize(),
        SecondaryLocation = _secondary?.Location.Serialize(),
        IsPinned = IsPinned,
    };

    private void OnTrashStatusChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (ActivePane.Location.Kind == LocationKind.Trash)
            OnPropertyChanged(nameof(SystemIconKey));
    }

    public void Dispose()
    {
        _services.TrashStatus.PropertyChanged -= OnTrashStatusChanged;
        Primary.Dispose();
        _secondary?.Dispose();
    }
}
