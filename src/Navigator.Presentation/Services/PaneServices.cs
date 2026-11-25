using Microsoft.Extensions.Logging;
using Navigator.Core.Caching;
using Navigator.Core.Operations;
using Navigator.Core.Platform;
using Navigator.Core.Scheduling;
using Navigator.Core.Tags;
using Navigator.Presentation.Localization;
using Navigator.Presentation.Settings;

namespace Navigator.Presentation.Services;

/// <summary>Everything a browsing pane needs, resolved once from DI and shared by all panes and tabs.</summary>
public sealed class PaneServices(
    SettingsService settings,
    Localizer localizer,
    IUiDispatcher dispatcher,
    IDialogService dialogs,
    IClipboardService clipboard,
    IThumbnailProvider thumbnails,
    INotificationCenter notifications,
    IWindowService windows,
    FileOperationService operations,
    ITrashService trash,
    TrashStatus trashStatus,
    IFileLauncher launcher,
    IShellMenuProvider shellMenus,
    IKnownFolderProvider knownFolders,
    IVolumeProvider volumes,
    IPlatformInfo platform,
    TagStore tags,
    RecentItems recent,
    BackgroundWorkScheduler scheduler,
    DirectoryListingCache directoryCache,
    RemoteFileService remote,
    Navigator.Plugins.PluginHost plugins,
    ILoggerFactory loggerFactory)
{
    public SettingsService Settings { get; } = settings;

    public Localizer Localizer { get; } = localizer;

    public IUiDispatcher Dispatcher { get; } = dispatcher;

    public IDialogService Dialogs { get; } = dialogs;

    public IClipboardService Clipboard { get; } = clipboard;

    public IThumbnailProvider Thumbnails { get; } = thumbnails;

    public INotificationCenter Notifications { get; } = notifications;

    public IWindowService Windows { get; } = windows;

    public FileOperationService Operations { get; } = operations;

    public ITrashService Trash { get; } = trash;

    /// <summary>Empty/full state of the trash (icon); shared by every window.</summary>
    public TrashStatus TrashStatus { get; } = trashStatus;

    public IFileLauncher Launcher { get; } = launcher;

    /// <summary>The OS context menu behind "Show more options" (Windows); unsupported elsewhere.</summary>
    public IShellMenuProvider ShellMenus { get; } = shellMenus;

    public IKnownFolderProvider KnownFolders { get; } = knownFolders;

    public IVolumeProvider Volumes { get; } = volumes;

    public IPlatformInfo Platform { get; } = platform;

    public TagStore Tags { get; } = tags;

    public RecentItems Recent { get; } = recent;

    public BackgroundWorkScheduler Scheduler { get; } = scheduler;

    /// <summary>Recently visited folder listings shared by every pane (Performance.DirectoryCacheEntries).</summary>
    public DirectoryListingCache DirectoryCache { get; } = FollowSettings(directoryCache, settings);

    /// <summary>Folders served by plugins (SFTP, FTP…).</summary>
    public RemoteFileService Remote { get; } = remote;

    /// <summary>Plugin contributions (sidebar sections, file systems).</summary>
    public Navigator.Plugins.PluginHost Plugins { get; } = plugins;

    public ILoggerFactory LoggerFactory { get; } = loggerFactory;

    private static DirectoryListingCache FollowSettings(DirectoryListingCache cache, SettingsService settings)
    {
        cache.Resize(settings.Current.Performance.DirectoryCacheEntries);
        settings.Changed += (_, section) =>
        {
            if (section is "performance" or "*")
                cache.Resize(settings.Current.Performance.DirectoryCacheEntries);
        };
        return cache;
    }

    /// <summary>Operation callbacks routed to dialogs on the UI thread.</summary>
    public IFileOperationInteraction CreateInteraction() => new DialogInteraction(Dispatcher, Dialogs);
}

/// <summary>Bridges background operation questions (conflicts, errors) to modal dialogs on the UI thread.</summary>
public sealed class DialogInteraction(IUiDispatcher dispatcher, IDialogService dialogs) : IFileOperationInteraction, IDisposable
{
    public void Dispose() => _oneAtATime.Dispose();

    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public async ValueTask<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict, CancellationToken cancellationToken)
    {
        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var completion = new TaskCompletionSource<ConflictChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
            dispatcher.Post(async () =>
            {
                try
                {
                    completion.TrySetResult(await dialogs.ResolveConflictAsync(conflict).ConfigureAwait(true));
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    public async ValueTask<ErrorChoice> ResolveErrorAsync(string path, FileOperationErrorKind kind, string message, bool canRetry, CancellationToken cancellationToken)
    {
        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var completion = new TaskCompletionSource<ErrorChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
            dispatcher.Post(async () =>
            {
                try
                {
                    completion.TrySetResult(await dialogs.ResolveErrorAsync(path, kind, message, canRetry).ConfigureAwait(true));
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }
}
