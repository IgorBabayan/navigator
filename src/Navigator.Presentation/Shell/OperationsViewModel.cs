using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Navigator.Core.Formatting;
using Navigator.Core.Operations;
using Navigator.Presentation.Localization;
using Navigator.Presentation.Services;

namespace Navigator.Presentation.Shell;

/// <summary>Visual outcome of an operation (and of the whole status center), used for badges and progress colors.</summary>
public enum OperationTone
{
    None,
    Active,
    Paused,
    Success,
    Warning,
    Error,
}

/// <summary>
/// One card of the status center. All values are pulled from <see cref="FileOperation.GetProgress"/> by
/// <see cref="Refresh"/>; change notifications are raised only for values that actually changed, so an idle
/// card costs nothing and a running one costs a handful of string comparisons per tick.
/// </summary>
public sealed class OperationItemViewModel : ObservableObject
{
    /// <summary>Number of throughput samples kept for the speed graph (one sample every <see cref="SampleEveryTicks"/> refreshes).</summary>
    public const int SpeedHistoryLength = 60;

    private const int SampleEveryTicks = 5;

    private readonly Localizer _localizer;
    private readonly Action<OperationItemViewModel>? _dismiss;
    private readonly List<double> _speedSamples = new(SpeedHistoryLength);
    private FileOperationState? _lastState;
    private int _tick;
    private int _speedRevision;
    private double _progress;
    private string _detail = string.Empty;
    private string _percentText = string.Empty;
    private string _speedText = string.Empty;
    private string _itemsText = string.Empty;
    private string _remainingText = string.Empty;
    private string _currentItemText = string.Empty;
    private bool _isIndeterminate = true;
    private bool _isExpanded;
    private bool _hasByteProgress;

    public OperationItemViewModel(FileOperation operation, Localizer localizer, Action<OperationItemViewModel>? dismiss = null)
    {
        Operation = operation;
        _localizer = localizer;
        _dismiss = dismiss;
        Title = localizer.Format("Operation." + operation.Kind, operation.Sources.Count, LeafName(operation.Destination));
        Route = BuildRoute(operation, localizer);
        KindIconKey = operation.Kind switch
        {
            FileOperationKind.Copy => "Copy",
            FileOperationKind.Move => "Cut",
            FileOperationKind.Delete => "Delete",
            FileOperationKind.Recycle => "Trash",
            FileOperationKind.Restore => "Restore",
            FileOperationKind.CreateArchive => "Compress",
            FileOperationKind.ExtractArchive => "Extract",
            _ => "Multiple",
        };

        CancelCommand = new RelayCommand(operation.Cancel, () => !operation.IsFinished);
        PauseCommand = new RelayCommand(() =>
        {
            if (operation.State == FileOperationState.Paused)
                operation.Resume();
            else
                operation.Pause();
            Refresh();
        }, () => operation.CanPause && !operation.IsFinished);
        DismissCommand = new RelayCommand(() => _dismiss?.Invoke(this), () => operation.IsFinished);
        ToggleDetailsCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public FileOperation Operation { get; }

    public string Title { get; }

    /// <summary>"From A to B" (or just "From A") line under the title; empty when there is nothing useful to show.</summary>
    public string Route { get; }

    public bool HasRoute => Route.Length > 0;

    public string KindIconKey { get; }

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        private set => SetProperty(ref _isIndeterminate, value);
    }

    public string Detail
    {
        get => _detail;
        private set
        {
            if (SetProperty(ref _detail, value))
                OnPropertyChanged(nameof(HasDetail));
        }
    }

    public bool HasDetail => _detail.Length > 0;

    public string PercentText
    {
        get => _percentText;
        private set => SetProperty(ref _percentText, value);
    }

    public string SpeedText
    {
        get => _speedText;
        private set => SetProperty(ref _speedText, value);
    }

    public string ItemsText
    {
        get => _itemsText;
        private set => SetProperty(ref _itemsText, value);
    }

    public string RemainingText
    {
        get => _remainingText;
        private set => SetProperty(ref _remainingText, value);
    }

    public string CurrentItemText
    {
        get => _currentItemText;
        private set => SetProperty(ref _currentItemText, value);
    }

    /// <summary>True once a byte total is known (copy, move, archives): enables speed text and the speed graph.</summary>
    public bool HasByteProgress
    {
        get => _hasByteProgress;
        private set => SetProperty(ref _hasByteProgress, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>Throughput history (bytes per second, oldest first). Redraw when <see cref="SpeedRevision"/> changes.</summary>
    public IReadOnlyList<double> SpeedSamples => _speedSamples;

    public int SpeedRevision
    {
        get => _speedRevision;
        private set => SetProperty(ref _speedRevision, value);
    }

    public string StateText => _localizer["OperationState." + Operation.State];

    public bool IsFinished => Operation.IsFinished;

    public bool IsPaused => Operation.State == FileOperationState.Paused;

    public bool CanPause => Operation.CanPause && !Operation.IsFinished;

    public string PauseIconKey => IsPaused ? "Play" : "Pause";

    public bool HasErrors => Operation.State is FileOperationState.Failed or FileOperationState.CompletedWithErrors;

    public OperationTone Tone => Operation.State switch
    {
        FileOperationState.Completed => OperationTone.Success,
        FileOperationState.CompletedWithErrors => OperationTone.Warning,
        FileOperationState.Failed => OperationTone.Error,
        FileOperationState.Cancelled => OperationTone.None,
        FileOperationState.Paused => OperationTone.Paused,
        _ => OperationTone.Active,
    };

    /// <summary>Small badge drawn over the kind icon once the operation has an outcome; null while it runs.</summary>
    public string? StateIconKey => Tone switch
    {
        OperationTone.Success => "Success",
        OperationTone.Warning => "Warning",
        OperationTone.Error => "Error",
        OperationTone.Paused => "Pause",
        OperationTone.None => "Close",
        _ => null,
    };

    public bool HasStateIcon => StateIconKey is not null;

    public IRelayCommand CancelCommand { get; }

    public IRelayCommand PauseCommand { get; }

    public IRelayCommand DismissCommand { get; }

    public IRelayCommand ToggleDetailsCommand { get; }

    /// <summary>Polled by the status center timer (UI thread).</summary>
    public void Refresh()
    {
        var snapshot = Operation.GetProgress();
        var state = Operation.State;
        var finished = Operation.IsFinished;
        var culture = _localizer.Culture;

        IsIndeterminate = !finished && (state is FileOperationState.Queued or FileOperationState.Preparing || (snapshot.TotalBytes == 0 && snapshot.TotalItems == 0));
        Progress = state == FileOperationState.Completed ? 100 : snapshot.Fraction * 100;
        PercentText = IsIndeterminate ? string.Empty : _localizer.Format("Operations.Percent", (int)Math.Floor(Progress));
        HasByteProgress = snapshot.TotalBytes > 0;
        ItemsText = snapshot.TotalItems > 0 ? _localizer.Format("Operation.ProgressItems", snapshot.ProcessedItems, snapshot.TotalItems) : string.Empty;
        CurrentItemText = finished || string.IsNullOrEmpty(snapshot.CurrentItem) ? string.Empty : LeafName(snapshot.CurrentItem);
        SpeedText = !finished && snapshot.BytesPerSecond > 0 ? ByteSize.FormatRate(snapshot.BytesPerSecond, culture) : string.Empty;
        RemainingText = !finished && snapshot.Remaining is { } remaining ? FormatDuration(remaining) : string.Empty;

        if (finished)
        {
            Detail = state switch
            {
                FileOperationState.CompletedWithErrors => _localizer.Format("Notify.SkippedItems", Operation.Errors.Count),
                FileOperationState.Failed => Operation.Failure?.Message ?? string.Empty,
                _ => string.Empty,
            };
        }
        else if (snapshot.TotalBytes > 0)
        {
            var text = _localizer.Format("Operation.ProgressBytes", ByteSize.Format(snapshot.ProcessedBytes, culture), ByteSize.Format(snapshot.TotalBytes, culture));
            if (SpeedText.Length > 0)
                text += " · " + SpeedText;
            if (RemainingText.Length > 0)
                text += " · " + _localizer.Format("Operation.Remaining", RemainingText);
            Detail = text;
        }
        else
        {
            Detail = ItemsText;
        }

        if (state == FileOperationState.Running && snapshot.TotalBytes > 0 && ++_tick % SampleEveryTicks == 0)
            AddSpeedSample(snapshot.BytesPerSecond);

        if (_lastState != state)
        {
            _lastState = state;
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(IsFinished));
            OnPropertyChanged(nameof(IsPaused));
            OnPropertyChanged(nameof(CanPause));
            OnPropertyChanged(nameof(PauseIconKey));
            OnPropertyChanged(nameof(HasErrors));
            OnPropertyChanged(nameof(Tone));
            OnPropertyChanged(nameof(StateIconKey));
            OnPropertyChanged(nameof(HasStateIcon));
            CancelCommand.NotifyCanExecuteChanged();
            PauseCommand.NotifyCanExecuteChanged();
            DismissCommand.NotifyCanExecuteChanged();
        }
    }

    private void AddSpeedSample(double bytesPerSecond)
    {
        if (_speedSamples.Count == SpeedHistoryLength)
            _speedSamples.RemoveAt(0);
        _speedSamples.Add(Math.Max(0, bytesPerSecond));
        SpeedRevision = _speedRevision + 1;
    }

    private static string LeafName(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;
        var trimmed = path.TrimEnd('/', '\\');
        var name = Path.GetFileName(trimmed);
        return name.Length > 0 ? name : trimmed.Length > 0 ? trimmed : path;
    }

    private static string BuildRoute(FileOperation operation, Localizer localizer)
    {
        if (operation.Sources.Count == 0)
            return string.Empty;
        var origin = LeafName(Path.GetDirectoryName(operation.Sources[0].TrimEnd('/', '\\')));
        if (origin.Length == 0)
            return string.Empty;
        var target = LeafName(operation.Destination);
        return target.Length > 0 && operation.Kind is FileOperationKind.Copy or FileOperationKind.Move or FileOperationKind.ExtractArchive
            ? localizer.Format("Operations.RouteFromTo", origin, target)
            : localizer.Format("Operations.RouteFrom", origin);
    }

    private string FormatDuration(TimeSpan value)
        => value.TotalHours >= 1 ? _localizer.Format("Duration.Hours", (int)value.TotalHours, value.Minutes)
            : value.TotalMinutes >= 1 ? _localizer.Format("Duration.Minutes", (int)value.TotalMinutes, value.Seconds)
            : _localizer.Format("Duration.Seconds", Math.Max(1, (int)value.TotalSeconds));
}

/// <summary>
/// The status center for file operations. Progress is polled at 10 Hz on the UI thread only while something is
/// running (zero idle cost), instead of letting worker threads flood the dispatcher with progress events.
/// Summary values (counts, overall progress, tone) are computed once per tick and cached, so bindings never run
/// LINQ over the list.
/// </summary>
public sealed class OperationsViewModel : ObservableObject, IDisposable
{
    private readonly PaneServices _services;
    private readonly Action<OperationItemViewModel> _dismiss;
    private IDisposable? _timer;
    private int _runningCount;
    private int _completedCount;
    private int _failedCount;
    private int _cancelledCount;
    private double _overallProgress;
    private bool _isOverallIndeterminate;
    private OperationTone _overallTone;
    private string _summaryText = string.Empty;

    public OperationsViewModel(PaneServices services)
    {
        _services = services;
        _dismiss = Dismiss;
        _services.Operations.Queue.OperationQueued += OnQueued;
        _services.Operations.Queue.OperationFinished += OnFinished;
        ClearFinishedCommand = new RelayCommand(ClearFinished, () => HasFinished);
        CancelAllCommand = new RelayCommand(CancelAll, () => HasActive);
        foreach (var operation in _services.Operations.Queue.ActiveOperations)
            Add(operation);
    }

    public ObservableCollection<OperationItemViewModel> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    public bool HasActive => _runningCount > 0;

    public bool HasFinished => Items.Count > _runningCount;

    public double OverallProgress => _overallProgress;

    public bool IsOverallIndeterminate => _isOverallIndeterminate;

    public int ActiveCount => _runningCount;

    public int CompletedCount => _completedCount;

    public int FailedCount => _failedCount;

    /// <summary>Drives the toolbar indicator: Active while anything runs, otherwise the worst finished outcome.</summary>
    public OperationTone OverallTone => _overallTone;

    public string? OverallIconKey => _overallTone switch
    {
        OperationTone.Success => "Success",
        OperationTone.Warning => "Warning",
        OperationTone.Error => "Error",
        _ => null,
    };

    public bool HasOverallIcon => OverallIconKey is not null;

    /// <summary>"2 in progress · 1 completed · 1 failed".</summary>
    public string SummaryText => _summaryText;

    public IRelayCommand ClearFinishedCommand { get; }

    public IRelayCommand CancelAllCommand { get; }

    private void OnQueued(object? sender, FileOperation operation) => _services.Dispatcher.Post(() => Add(operation));

    private void Add(FileOperation operation)
    {
        foreach (var existing in Items)
        {
            if (existing.Operation == operation)
                return;
        }

        var item = new OperationItemViewModel(operation, _services.Localizer, _dismiss);
        item.Refresh();
        Items.Insert(0, item);
        EnsureTimer();
        RaiseSummary();
    }

    private void OnFinished(object? sender, FileOperation operation)
        => _services.Dispatcher.Post(() =>
        {
            var item = Find(operation);
            item?.Refresh();
            if (operation.State == FileOperationState.Completed && _services.Settings.Current.Operations.ShowOperationCompletedNotification
                && operation.Kind is FileOperationKind.Copy or FileOperationKind.Move && operation.FinishedAt - operation.StartedAt > TimeSpan.FromSeconds(5))
            {
                _services.Notifications.Show(new InAppNotification(_services.Localizer["Notify.OperationCompleted"], item?.Title ?? string.Empty, NotificationSeverity.Success));
            }

            RaiseSummary();
        });

    private OperationItemViewModel? Find(FileOperation operation)
    {
        foreach (var item in Items)
        {
            if (item.Operation == operation)
                return item;
        }

        return null;
    }

    private void EnsureTimer()
    {
        _timer ??= _services.Dispatcher.StartTimer(TimeSpan.FromMilliseconds(100), Tick);
    }

    private void Tick()
    {
        foreach (var item in Items)
        {
            // Finished cards were refreshed once by OnFinished; only running ones change from tick to tick.
            if (!item.IsFinished)
                item.Refresh();
        }

        RaiseSummary();
        if (!HasActive)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Dismiss(OperationItemViewModel item)
    {
        if (!item.IsFinished)
            return;
        Items.Remove(item);
        RaiseSummary();
    }

    private void ClearFinished()
    {
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (Items[i].IsFinished)
                Items.RemoveAt(i);
        }

        RaiseSummary();
    }

    private void CancelAll()
    {
        foreach (var item in Items)
        {
            if (!item.IsFinished)
                item.Operation.Cancel();
        }
    }

    private void RaiseSummary()
    {
        var running = 0;
        var completed = 0;
        var failed = 0;
        var cancelled = 0;
        var determinate = 0;
        var progressSum = 0.0;
        var anyWarning = false;
        foreach (var item in Items)
        {
            switch (item.Operation.State)
            {
                case FileOperationState.Completed:
                    completed++;
                    break;
                case FileOperationState.CompletedWithErrors:
                    completed++;
                    anyWarning = true;
                    break;
                case FileOperationState.Failed:
                    failed++;
                    break;
                case FileOperationState.Cancelled:
                    cancelled++;
                    break;
                default:
                    running++;
                    if (!item.IsIndeterminate)
                    {
                        determinate++;
                        progressSum += item.Progress;
                    }

                    break;
            }
        }

        var tone = running > 0 ? OperationTone.Active
            : failed > 0 ? OperationTone.Error
            : anyWarning ? OperationTone.Warning
            : completed > 0 ? OperationTone.Success
            : OperationTone.None;

        var countsChanged = running != _runningCount || completed != _completedCount || failed != _failedCount || cancelled != _cancelledCount;
        _runningCount = running;
        _completedCount = completed;
        _failedCount = failed;
        _cancelledCount = cancelled;

        SetValue(ref _overallProgress, determinate > 0 ? progressSum / determinate : running > 0 ? 0 : 100, nameof(OverallProgress));
        SetValue(ref _isOverallIndeterminate, running > 0 && determinate == 0, nameof(IsOverallIndeterminate));
        if (SetValue(ref _overallTone, tone, nameof(OverallTone)))
        {
            OnPropertyChanged(nameof(OverallIconKey));
            OnPropertyChanged(nameof(HasOverallIcon));
        }

        if (!countsChanged && _summaryText.Length > 0)
            return;

        SetValue(ref _summaryText, BuildSummary(running, completed, failed, cancelled), nameof(SummaryText));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasActive));
        OnPropertyChanged(nameof(HasFinished));
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(CompletedCount));
        OnPropertyChanged(nameof(FailedCount));
        ClearFinishedCommand.NotifyCanExecuteChanged();
        CancelAllCommand.NotifyCanExecuteChanged();
    }

    private string BuildSummary(int running, int completed, int failed, int cancelled)
    {
        var localizer = _services.Localizer;
        var parts = new List<string>(4);
        if (running > 0)
            parts.Add(localizer.Format("Operations.SummaryRunning", running));
        if (completed > 0)
            parts.Add(localizer.Format("Operations.SummaryCompleted", completed));
        if (failed > 0)
            parts.Add(localizer.Format("Operations.SummaryFailed", failed));
        if (cancelled > 0)
            parts.Add(localizer.Format("Operations.SummaryCancelled", cancelled));
        return parts.Count == 0 ? localizer["Operations.Empty"] : string.Join(" · ", parts);
    }

    private bool SetValue<T>(ref T field, T value, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    public void Dispose()
    {
        _services.Operations.Queue.OperationQueued -= OnQueued;
        _services.Operations.Queue.OperationFinished -= OnFinished;
        _timer?.Dispose();
    }
}
