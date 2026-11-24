using Microsoft.Extensions.Logging;
using Navigator.Core.Settings;

namespace Navigator.Presentation.Settings;

/// <summary>
/// Owns the live <see cref="AppSettings"/> instance. Changes apply immediately (view models listen to
/// <see cref="Changed"/>) and are persisted with a short debounce so dragging a slider does not write
/// the file dozens of times.
/// </summary>
public sealed class SettingsService : IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(600);
    private readonly SettingsStore _store;
    private readonly ILogger<SettingsService> _logger;
    private readonly Lock _gate = new();
    private Timer? _saveTimer;

    public SettingsService(SettingsStore store, ILogger<SettingsService> logger)
    {
        _store = store;
        _logger = logger;
        var result = store.Load();
        Current = result.Settings;
        LoadStatus = result.Status;
        if (result.Status is SettingsLoadStatus.ResetAfterCorruption or SettingsLoadStatus.RecoveredFromBackup && _logger.IsEnabled(LogLevel.Warning))
            _logger.LogWarning("Settings file was unreadable ({Status}); a copy was kept at {Copy}", result.Status, result.PreservedCopyPath);
    }

    public AppSettings Current { get; private set; }

    public SettingsLoadStatus LoadStatus { get; }

    /// <summary>Raised on the thread that made the change, with the name of the changed section (or "*" for all).</summary>
    public event EventHandler<string>? Changed;

    /// <summary>Applies a change, notifies listeners and schedules a save.</summary>
    public void Update(string section, Action<AppSettings> change)
    {
        change(Current);
        SettingsStore.Sanitize(Current);
        Changed?.Invoke(this, section);
        ScheduleSave();
    }

    public void ResetAll()
    {
        var keep = Current.Sidebar.PinnedFolders;
        Current = new AppSettings();
        Current.Sidebar.PinnedFolders = keep;
        Changed?.Invoke(this, "*");
        ScheduleSave();
    }

    public void Replace(AppSettings settings)
    {
        SettingsStore.Sanitize(settings);
        Current = settings;
        Changed?.Invoke(this, "*");
        ScheduleSave();
    }

    public void Export(string path) => SettingsStore.Export(Current, path);

    public void Import(string path) => Replace(_store.Import(path));

    public void ScheduleSave()
    {
        lock (_gate)
        {
            _saveTimer ??= new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    public void SaveNow()
    {
        try
        {
            lock (_gate)
                _store.Save(Current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Saving settings failed");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _saveTimer?.Dispose();
            _saveTimer = null;
        }

        SaveNow();
    }
}
