using Navigator.Core.IO;
using Navigator.Core.Operations;

namespace Navigator.Presentation.Services;

/// <summary>UI-thread access without referencing Avalonia from view models.</summary>
public interface IUiDispatcher
{
    bool CheckAccess();

    void Post(Action action);

    Task InvokeAsync(Action action);

    /// <summary>Starts a UI-thread timer. Dispose the result to stop it.</summary>
    IDisposable StartTimer(TimeSpan interval, Action tick);
}

/// <summary>Runs actions inline; used by unit tests and design-time data.</summary>
public sealed class ImmediateDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;

    public void Post(Action action) => action();

    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    public IDisposable StartTimer(TimeSpan interval, Action tick) => new NoopDisposable();

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

public enum DialogResult
{
    Primary,
    Secondary,
    Cancel,
}

public sealed record ConfirmRequest(string Title, string Message, string PrimaryText, string? SecondaryText = null, bool IsDestructive = false, string? CheckBoxText = null);

public sealed record ConfirmResponse(DialogResult Result, bool IsChecked);

public sealed record TextInputRequest(string Title, string Label, string InitialText, string PrimaryText, Func<string, string?>? Validate = null, int SelectionLength = -1);

/// <summary>Modal dialogs. All methods are called on the UI thread.</summary>
public interface IDialogService
{
    Task<ConfirmResponse> ConfirmAsync(ConfirmRequest request);

    Task<string?> PromptTextAsync(TextInputRequest request);

    Task<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict);

    Task<ErrorChoice> ResolveErrorAsync(string path, FileOperationErrorKind kind, string message, bool canRetry);

    Task ShowMessageAsync(string title, string message);

    Task<string?> PickFolderAsync(string title);

    Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null);

    Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension);

    void ShowProperties(IReadOnlyList<string> paths);
}

/// <summary>File clipboard (cut/copy/paste) and text.</summary>
public interface IClipboardService
{
    Task SetFilesAsync(IReadOnlyList<string> paths, bool cut);

    /// <summary>Paths currently on the clipboard and whether they were cut (null when the clipboard holds no files).</summary>
    Task<(IReadOnlyList<string> Paths, bool IsCut)?> GetFilesAsync();

    Task SetTextAsync(string text);

    /// <summary>Called after a cut-paste so the source items stop being shown as "cut".</summary>
    Task ClearCutStateAsync();

    event EventHandler? Changed;
}

/// <summary>Thumbnails and icons as opaque UI objects (bitmaps) so view models stay toolkit-agnostic.</summary>
public interface IThumbnailProvider
{
    bool CanCreateThumbnail(FileEntry entry);

    Task<object?> GetThumbnailAsync(FileEntry entry, int pixelSize, CancellationToken cancellationToken);

    /// <summary>Deletes the thumbnails stored on disk (Settings → Performance).</summary>
    Task ClearDiskCacheAsync();
}

public enum NotificationSeverity
{
    Information,
    Success,
    Warning,
    Error,
}

public sealed record InAppNotification(string Title, string Message, NotificationSeverity Severity, string? ActionText = null, Action? Action = null);

/// <summary>Transient in-app toasts.</summary>
public interface INotificationCenter
{
    void Show(InAppNotification notification);
}

/// <summary>Opens new top-level windows (multi-window support) and the settings window.</summary>
public interface IWindowService
{
    void OpenNewWindow(Navigator.Core.Navigation.Location? location = null);

    void OpenSettings(string? page = null);

    void CloseCurrentWindow();
}
