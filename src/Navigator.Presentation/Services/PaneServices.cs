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
    ISystemIconProvider systemIcons,
    INotificationCenter notifications,
    IWindowService windows,
    FileOperationService operations,
    ITrashService trash,
    TrashStatus trashStatus,
    IFileLauncher launcher,
    IShellMenuProvider shellMenus,
    INewItemTemplates newItems,
    IKnownFolderProvider knownFolders,
    IVolumeProvider volumes,
    IPlatformInfo platform,
    TagStore tags,
    RecentItems recent,
    BackgroundWorkScheduler scheduler,
    DirectoryListingCache directoryCache,
    RemoteFileService remote,
    Navigator.Plugins.PluginHost plugins,
    IQuickLookService quickLook,
    IAudioPreviewPlayer audioPlayer,
    ILoggerFactory loggerFactory,
    ItemActionServices? itemActions = null)
{
    public SettingsService Settings { get; } = settings;

    public Localizer Localizer { get; } = localizer;

    public IUiDispatcher Dispatcher { get; } = dispatcher;

    public IDialogService Dialogs { get; } = dialogs;

    public IClipboardService Clipboard { get; } = clipboard;

    public IThumbnailProvider Thumbnails { get; } = thumbnails;

    /// <summary>Icons the OS draws for file types, folders and drives (Settings → Files and folders → system icons).</summary>
    public ISystemIconProvider SystemIcons { get; } = systemIcons;

    public INotificationCenter Notifications { get; } = notifications;

    public IWindowService Windows { get; } = windows;

    public FileOperationService Operations { get; } = operations;

    public ITrashService Trash { get; } = trash;

    /// <summary>Empty/full state of the trash (icon); shared by every window.</summary>
    public TrashStatus TrashStatus { get; } = trashStatus;

    public IFileLauncher Launcher { get; } = launcher;

    /// <summary>The OS context menu behind "Show more options" (Windows); unsupported elsewhere.</summary>
    public IShellMenuProvider ShellMenus { get; } = shellMenus;

    /// <summary>Document types for the "New" menu (Windows ShellNew registrations, the Templates folder elsewhere).</summary>
    public INewItemTemplates NewItems { get; } = newItems;

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

    /// <summary>External previewers the space bar can hand the selection to (QuickLook, Seer, Peek, macOS Quick Look…).</summary>
    public IQuickLookService QuickLook { get; } = quickLook;

    /// <summary>Plays audio files in the preview pane and the Quick Look window.</summary>
    public IAudioPreviewPlayer AudioPlayer { get; } = audioPlayer;

    public ILoggerFactory LoggerFactory { get; } = loggerFactory;

    /// <summary>Image, font, disk image, drive and share actions of the item menus (unsupported everywhere when absent).</summary>
    public ItemActionServices ItemActions { get; } = itemActions ?? ItemActionServices.Unsupported;

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

/// <summary>The platform services behind the item actions: desktop pictures, fonts, drives and sharing.</summary>
public sealed class ItemActionServices(IDesktopBackgroundService backgrounds, IFontInstaller fonts, IDriveService drives, IShareService share)
{
    /// <summary>No item actions (tests, unsupported platforms).</summary>
    public static ItemActionServices Unsupported { get; } = new(UnsupportedItemActions.Instance, UnsupportedItemActions.Instance, UnsupportedItemActions.Instance, UnsupportedItemActions.Instance);

    public IDesktopBackgroundService Backgrounds { get; } = backgrounds;

    public IFontInstaller Fonts { get; } = fonts;

    public IDriveService Drives { get; } = drives;

    public IShareService Share { get; } = share;
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
