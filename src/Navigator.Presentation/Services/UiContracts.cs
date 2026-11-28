using Navigator.Core.IO;
using Navigator.Core.Operations;
using Navigator.Core.Platform;

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

/// <summary>Outcome of registering the global "open Navigator" shortcut with the OS, shown in Settings.</summary>
public interface IGlobalHotkeyStatus
{
    /// <summary>Null when no shortcut is assigned.</summary>
    HotkeyRegistrationResult? State { get; }

    /// <summary>The assigned shortcut in display form ("Ctrl+Shift+Alt+E"), empty when none.</summary>
    string Gesture { get; }

    /// <summary>True when the shortcut also starts Navigator while it is not running (agent or desktop shortcut).</summary>
    bool WorksWhenClosed { get; }

    /// <summary>Raised on the UI thread after every (re-)registration.</summary>
    event EventHandler? Changed;
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

public sealed record TextInputRequest(string Title, string Label, string InitialText, string PrimaryText, Func<string, string?>? Validate = null, int SelectionLength = -1, bool IsSecret = false);

public enum FormFieldKind
{
    Text,
    Number,
    Password,
    FilePath,
    Choice,
    Toggle,
}

public sealed record FormChoice(string Id, string Text);

/// <summary>One input of a <see cref="FormRequest"/>. <see cref="Validate"/> returns an error message or null.</summary>
public sealed record FormField(string Id, string Label, FormFieldKind Kind, string Value, bool IsRequired, IReadOnlyList<FormChoice> Choices, string? Placeholder, Func<string, string?>? Validate);

/// <summary>A small modal form (plugin connection settings). The result maps field ids to values.</summary>
public sealed record FormRequest(string Title, string PrimaryText, IReadOnlyList<FormField> Fields);

/// <summary>Modal dialogs. All methods are called on the UI thread.</summary>
public interface IDialogService
{
    Task<ConfirmResponse> ConfirmAsync(ConfirmRequest request);

    Task<string?> PromptTextAsync(TextInputRequest request);

    /// <summary>The multi-rename dialog; true when the user confirmed (the model then holds the renames).</summary>
    Task<bool> ShowMultiRenameAsync(Panes.MultiRenameViewModel model);

    /// <summary>Shows a form; returns the values by field id, or null when cancelled.</summary>
    Task<IReadOnlyDictionary<string, string>?> ShowFormAsync(FormRequest request);

    Task<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict);

    /// <summary>
    /// "Conflicting file name": every incoming item whose name is taken, each with its own choice and one choice for
    /// all. Returns the choice per source path, or null when the user cancels the whole paste.
    /// </summary>
    Task<IReadOnlyDictionary<string, NameConflictAction>?> ResolveNameConflictsAsync(IReadOnlyList<NameConflict> conflicts);

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

    /// <summary>
    /// Files on the clipboard: Navigator's own copy/cut, or files another application put there (Explorer, a Remote
    /// Desktop session). Null when the clipboard holds no files.
    /// </summary>
    Task<ClipboardFileList?> GetFilesAsync();

    /// <summary>Whether Paste has anything to paste; does not fetch the files themselves (menu probes).</summary>
    Task<bool> HasFilesAsync();

    /// <summary>Writes streamed clipboard files (<see cref="ClipboardFileList.IsStreamed"/>) into a local folder; returns the new top-level items.</summary>
    Task<IReadOnlyList<string>> PasteStreamedAsync(string destination, CancellationToken cancellationToken);

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
