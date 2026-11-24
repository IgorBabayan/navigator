using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Navigator.Core.Formatting;
using Navigator.Core.Operations;
using Navigator.Core.Settings;
using Navigator.Presentation.Localization;
using Navigator.Presentation.Services;
using Navigator.Presentation.Settings;
using Navigator.Updater;

namespace Navigator.Presentation.Updates;

public enum UpdateState
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    ReadyToRestart,
    WaitingForOperations,
    ManualUpdateRequired,
    NotConfigured,
    Failed,
}

/// <summary>Update UI state (Settings → Updates and the title-bar badge). Never blocks the UI thread.</summary>
public sealed class UpdateViewModel : ObservableObject, IDisposable
{
    private readonly UpdateService _updates;
    private readonly SettingsService _settings;
    private readonly Localizer _localizer;
    private readonly FileOperationQueue _operations;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<UpdateViewModel> _logger;
    private UpdateCheckResult? _result;
    private CancellationTokenSource? _work;
    private Timer? _periodic;
    private UpdateState _state;
    private double _progress;
    private string _progressText = string.Empty;
    private string? _error;

    public UpdateViewModel(UpdateService updates, SettingsService settings, Localizer localizer, FileOperationQueue operations, IUiDispatcher dispatcher, ILogger<UpdateViewModel> logger)
    {
        _updates = updates;
        _settings = settings;
        _localizer = localizer;
        _operations = operations;
        _dispatcher = dispatcher;
        _logger = logger;
        _state = updates.IsConfigured ? UpdateState.Idle : UpdateState.NotConfigured;
        CheckCommand = new AsyncRelayCommand(() => CheckAsync(userInitiated: true), () => _state is not (UpdateState.Checking or UpdateState.Downloading));
        DownloadCommand = new AsyncRelayCommand(DownloadAsync, () => _state == UpdateState.Available);
        RestartCommand = new RelayCommand(Restart, () => _state is UpdateState.ReadyToRestart or UpdateState.WaitingForOperations);
        CancelCommand = new RelayCommand(() => _work?.Cancel(), () => _state is UpdateState.Checking or UpdateState.Downloading);
        SkipVersionCommand = new RelayCommand(SkipVersion, () => _state == UpdateState.Available);
    }

    public string CurrentVersion => _updates.CurrentVersion.ToString();

    public string? AvailableVersion => _result?.Release?.Version.ToString();

    public string? ReleaseNotes => _result?.Release?.ReleaseNotes;

    public string? ReleasesPage => _result?.Release?.ReleasePage.ToString() ?? _updates.ReleasesPage;

    public bool IsSignatureVerified => _result?.Release?.SignatureVerified == true;

    public bool IsChecksumOnly => _result?.Release is { SignatureVerified: false };

    public string InstallationKind => _localizer["Installation." + _updates.Installation];

    public UpdateState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value))
                return;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(IsUpdateAvailable));
            OnPropertyChanged(nameof(IsBusy));
            CheckCommand.NotifyCanExecuteChanged();
            DownloadCommand.NotifyCanExecuteChanged();
            RestartCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
            SkipVersionCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsUpdateAvailable => _state is UpdateState.Available or UpdateState.ReadyToRestart or UpdateState.WaitingForOperations or UpdateState.ManualUpdateRequired;

    public bool IsBusy => _state is UpdateState.Checking or UpdateState.Downloading;

    public string StatusText => _state switch
    {
        UpdateState.Failed => _localizer.Format("Update.Failed", _error),
        UpdateState.Available or UpdateState.ManualUpdateRequired => _localizer.Format("Update." + _state, AvailableVersion),
        _ => _localizer["Update." + _state],
    };

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public IAsyncRelayCommand CheckCommand { get; }

    public IAsyncRelayCommand DownloadCommand { get; }

    public IRelayCommand RestartCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public IRelayCommand SkipVersionCommand { get; }

    /// <summary>Raised when the app must exit so the update can be applied.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Called once the main window is visible: background check on startup plus the periodic timer.</summary>
    public void StartBackgroundChecks()
    {
        if (!_updates.IsConfigured)
            return;

        _ = Task.Run(_updates.CleanupPreviousUpdate);
        var settings = _settings.Current.Updates;
        if (settings.CheckOnStartup)
            _dispatcher.Post(() => _ = CheckAsync(userInitiated: false));
        if (settings.CheckIntervalHours > 0)
        {
            var interval = TimeSpan.FromHours(settings.CheckIntervalHours);
            _periodic = new Timer(_ => _dispatcher.Post(() =>
            {
                if (_state is UpdateState.Idle or UpdateState.UpToDate or UpdateState.Failed)
                    _ = CheckAsync(userInitiated: false);
            }), null, interval, interval);
        }
    }

    public async Task CheckAsync(bool userInitiated)
    {
        if (!_updates.IsConfigured)
        {
            State = UpdateState.NotConfigured;
            return;
        }

        _work?.Dispose();
        _work = new CancellationTokenSource();
        State = UpdateState.Checking;
        try
        {
            var includePrereleases = _settings.Current.Updates.Channel == UpdateChannel.Prerelease;
            _result = await _updates.CheckAsync(includePrereleases, _work.Token).ConfigureAwait(true);
            OnPropertyChanged(nameof(AvailableVersion));
            OnPropertyChanged(nameof(ReleaseNotes));
            OnPropertyChanged(nameof(ReleasesPage));
            OnPropertyChanged(nameof(IsSignatureVerified));
            OnPropertyChanged(nameof(IsChecksumOnly));

            var skipped = _settings.Current.Updates.SkippedVersion;
            State = _result.Status switch
            {
                UpdateCheckStatus.UpdateAvailable when !userInitiated && skipped == AvailableVersion => UpdateState.UpToDate,
                UpdateCheckStatus.UpdateAvailable => UpdateState.Available,
                UpdateCheckStatus.ManualUpdateRequired => UpdateState.ManualUpdateRequired,
                UpdateCheckStatus.UpToDate => UpdateState.UpToDate,
                UpdateCheckStatus.NotConfigured => UpdateState.NotConfigured,
                _ => Fail(_result.Error),
            };
        }
        catch (OperationCanceledException)
        {
            State = UpdateState.Idle;
        }
    }

    private async Task DownloadAsync()
    {
        if (_result is null)
            return;

        _work?.Dispose();
        _work = new CancellationTokenSource();
        State = UpdateState.Downloading;
        var progress = new Progress<DownloadProgress>(p =>
        {
            Progress = p.Fraction * 100;
            ProgressText = _localizer.Format("Update.DownloadProgress",
                ByteSize.Format(p.Received, _localizer.Culture), ByteSize.Format(p.Total, _localizer.Culture), ByteSize.FormatRate(p.BytesPerSecond, _localizer.Culture));
        });

        try
        {
            await _updates.DownloadAndPrepareAsync(_result, progress, _work.Token).ConfigureAwait(true);
            State = UpdateState.ReadyToRestart;
        }
        catch (OperationCanceledException)
        {
            State = UpdateState.Available;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Update download or preparation failed");
            State = Fail(ex.Message);
        }
    }

    private void Restart()
    {
        // Never replace the application while files are being copied or moved.
        if (_updates.TryRestartToUpdate(() => !_operations.HasActiveOperations))
        {
            _settings.SaveNow();
            ExitRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        State = UpdateState.WaitingForOperations;
        _operations.OperationFinished -= RetryWhenIdle;
        _operations.OperationFinished += RetryWhenIdle;
    }

    private void RetryWhenIdle(object? sender, FileOperation operation)
    {
        if (_operations.HasActiveOperations)
            return;
        _operations.OperationFinished -= RetryWhenIdle;
        _dispatcher.Post(() => State = UpdateState.ReadyToRestart);
    }

    private void SkipVersion()
    {
        if (AvailableVersion is { } version)
            _settings.Update("updates", s => s.Updates.SkippedVersion = version);
        State = UpdateState.UpToDate;
    }

    private UpdateState Fail(string? error)
    {
        _error = error;
        return UpdateState.Failed;
    }

    public void Dispose()
    {
        _periodic?.Dispose();
        _work?.Cancel();
        _work?.Dispose();
        _operations.OperationFinished -= RetryWhenIdle;
    }
}
