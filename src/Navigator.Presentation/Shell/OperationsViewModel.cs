using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Navigator.Core.Formatting;
using Navigator.Core.Operations;
using Navigator.Presentation.Localization;
using Navigator.Presentation.Services;

namespace Navigator.Presentation.Shell;

public sealed class OperationItemViewModel : ObservableObject
{
    private readonly Localizer _localizer;
    private double _progress;
    private string _detail = string.Empty;
    private bool _isIndeterminate = true;

    public OperationItemViewModel(FileOperation operation, Localizer localizer)
    {
        Operation = operation;
        _localizer = localizer;
        Title = localizer.Format("Operation." + operation.Kind, operation.Sources.Count, operation.Destination is null ? string.Empty : Path.GetFileName(operation.Destination.TrimEnd('/', '\\')));
        CancelCommand = new RelayCommand(operation.Cancel, () => !operation.IsFinished);
        PauseCommand = new RelayCommand(() =>
        {
            if (operation.State == FileOperationState.Paused)
                operation.Resume();
            else
                operation.Pause();
            Refresh();
        }, () => operation.CanPause && !operation.IsFinished);
    }

    public FileOperation Operation { get; }

    public string Title { get; }

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
        private set => SetProperty(ref _detail, value);
    }

    public string StateText => _localizer["OperationState." + Operation.State];

    public bool IsFinished => Operation.IsFinished;

    public bool IsPaused => Operation.State == FileOperationState.Paused;

    public string PauseIconKey => IsPaused ? "Play" : "Pause";

    public bool HasErrors => Operation.State is FileOperationState.Failed or FileOperationState.CompletedWithErrors;

    public IRelayCommand CancelCommand { get; }

    public IRelayCommand PauseCommand { get; }

    /// <summary>Polled by the operations panel timer (UI thread).</summary>
    public void Refresh()
    {
        var snapshot = Operation.GetProgress();
        IsIndeterminate = Operation.State is FileOperationState.Queued or FileOperationState.Preparing || (snapshot.TotalBytes == 0 && snapshot.TotalItems == 0);
        Progress = snapshot.Fraction * 100;

        if (Operation.IsFinished)
        {
            Detail = Operation.State == FileOperationState.CompletedWithErrors
                ? _localizer.Format("Notify.SkippedItems", Operation.Errors.Count)
                : Operation.Failure?.Message ?? string.Empty;
        }
        else if (snapshot.TotalBytes > 0)
        {
            var culture = _localizer.Culture;
            var text = _localizer.Format("Operation.ProgressBytes", ByteSize.Format(snapshot.ProcessedBytes, culture), ByteSize.Format(snapshot.TotalBytes, culture));
            if (snapshot.BytesPerSecond > 0)
                text += " · " + ByteSize.FormatRate(snapshot.BytesPerSecond, culture);
            if (snapshot.Remaining is { } remaining)
                text += " · " + _localizer.Format("Operation.Remaining", FormatDuration(remaining));
            Detail = text;
        }
        else
        {
            Detail = _localizer.Format("Operation.ProgressItems", snapshot.ProcessedItems, snapshot.TotalItems);
        }

        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(PauseIconKey));
        OnPropertyChanged(nameof(HasErrors));
        CancelCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
    }

    private string FormatDuration(TimeSpan value)
        => value.TotalHours >= 1 ? _localizer.Format("Duration.Hours", (int)value.TotalHours, value.Minutes)
            : value.TotalMinutes >= 1 ? _localizer.Format("Duration.Minutes", (int)value.TotalMinutes, value.Seconds)
            : _localizer.Format("Duration.Seconds", Math.Max(1, (int)value.TotalSeconds));
}

/// <summary>
/// The status center for file operations. Progress is polled at 10 Hz on the UI thread only while something is
/// running (zero idle cost), instead of letting worker threads flood the dispatcher with progress events.
/// </summary>
public sealed class OperationsViewModel : ObservableObject, IDisposable
{
    private readonly PaneServices _services;
    private IDisposable? _timer;

    public OperationsViewModel(PaneServices services)
    {
        _services = services;
        _services.Operations.Queue.OperationQueued += OnQueued;
        _services.Operations.Queue.OperationFinished += OnFinished;
        ClearFinishedCommand = new RelayCommand(ClearFinished);
        foreach (var operation in _services.Operations.Queue.ActiveOperations)
            Add(operation);
    }

    public ObservableCollection<OperationItemViewModel> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    public bool HasActive => Items.Any(i => !i.IsFinished);

    public double OverallProgress => Items.Where(i => !i.IsFinished).Select(i => i.Progress).DefaultIfEmpty(0).Average();

    public int ActiveCount => Items.Count(i => !i.IsFinished);

    public IRelayCommand ClearFinishedCommand { get; }

    private void OnQueued(object? sender, FileOperation operation) => _services.Dispatcher.Post(() => Add(operation));

    private void Add(FileOperation operation)
    {
        if (Items.Any(i => i.Operation == operation))
            return;
        Items.Insert(0, new OperationItemViewModel(operation, _services.Localizer));
        EnsureTimer();
        RaiseSummary();
    }

    private void OnFinished(object? sender, FileOperation operation)
        => _services.Dispatcher.Post(() =>
        {
            Items.FirstOrDefault(i => i.Operation == operation)?.Refresh();
            if (operation.State == FileOperationState.Completed && _services.Settings.Current.Operations.ShowOperationCompletedNotification
                && operation.Kind is FileOperationKind.Copy or FileOperationKind.Move && operation.FinishedAt - operation.StartedAt > TimeSpan.FromSeconds(5))
            {
                _services.Notifications.Show(new InAppNotification(_services.Localizer["Notify.OperationCompleted"],
                    Items.FirstOrDefault(i => i.Operation == operation)?.Title ?? string.Empty, NotificationSeverity.Success));
            }

            RaiseSummary();
        });

    private void EnsureTimer()
    {
        _timer ??= _services.Dispatcher.StartTimer(TimeSpan.FromMilliseconds(100), Tick);
    }

    private void Tick()
    {
        foreach (var item in Items.Where(i => !i.IsFinished || i.Progress < 100))
            item.Refresh();
        RaiseSummary();
        if (!HasActive)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void ClearFinished()
    {
        foreach (var item in Items.Where(i => i.IsFinished).ToList())
            Items.Remove(item);
        RaiseSummary();
    }

    private void RaiseSummary()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasActive));
        OnPropertyChanged(nameof(OverallProgress));
        OnPropertyChanged(nameof(ActiveCount));
    }

    public void Dispose()
    {
        _services.Operations.Queue.OperationQueued -= OnQueued;
        _services.Operations.Queue.OperationFinished -= OnFinished;
        _timer?.Dispose();
    }
}
