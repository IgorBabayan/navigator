using Avalonia.Controls;
using Avalonia.Input.Platform;
using Navigator.Core.Platform;
using Navigator.Presentation.Services;

namespace Navigator.App.Services;

/// <summary>
/// File clipboard. Copy/cut/paste between Navigator windows uses an in-process list (exact paths, cut state).
/// The paths are also placed on the OS clipboard as text so they can be pasted into other applications.
/// Files copied in other applications (Explorer, a Remote Desktop session) are read from the OS clipboard through
/// <see cref="ISystemFileClipboard"/>; on Linux and macOS that is not implemented yet (docs/KNOWN_ISSUES.md).
/// </summary>
internal sealed class ClipboardService(WindowService windows, ISystemFileClipboard system) : IClipboardService
{
    private IReadOnlyList<string> _paths = [];
    private bool _isCut;
    private bool _externalCut;
    private string? _publishedText;

    public event EventHandler? Changed;

    public async Task SetFilesAsync(IReadOnlyList<string> paths, bool cut)
    {
        _paths = [.. paths];
        _isCut = cut;
        _externalCut = false;
        _publishedText = string.Join(Environment.NewLine, paths);
        await SetTextCoreAsync(_publishedText).ConfigureAwait(true);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<ClipboardFileList?> GetFilesAsync()
    {
        _externalCut = false;
        if (await IsOwnListCurrentAsync().ConfigureAwait(true))
            return new ClipboardFileList(_paths, _isCut);

        // Another application owns the clipboard: Explorer's files, or a file copied inside a Remote Desktop session.
        if (!system.HasFiles())
            return null;
        var files = await system.ReadAsync().ConfigureAwait(true);
        _externalCut = files is { IsCut: true };
        return files;
    }

    public async Task<bool> HasFilesAsync()
        => system.HasFiles() || await IsOwnListCurrentAsync().ConfigureAwait(true);

    public Task<IReadOnlyList<string>> PasteStreamedAsync(string destination, CancellationToken cancellationToken)
        => system.PasteStreamedAsync(destination, cancellationToken);

    /// <summary>True while Navigator's own file list is what the OS clipboard holds.</summary>
    private async Task<bool> IsOwnListCurrentAsync()
    {
        if (_paths.Count == 0)
            return false;

        // If another application replaced the clipboard since our copy, our file list is stale. Files put there by
        // another program carry no text, so "no text" plus files on the clipboard also means it was replaced.
        var clipboard = Clipboard;
        if (clipboard is not null)
        {
            var text = await clipboard.TryGetTextAsync().ConfigureAwait(true);
            if ((text is not null && !string.Equals(text, _publishedText, StringComparison.Ordinal))
                || (text is null && system.HasFiles()))
            {
                _paths = [];
                return false;
            }
        }

        return true;
    }

    public Task SetTextAsync(string text)
    {
        _paths = [];
        _publishedText = null;
        return SetTextCoreAsync(text);
    }

    public Task ClearCutStateAsync()
    {
        if (_externalCut)
        {
            // Files cut in another application were moved: like Explorer, empty the clipboard so they are not pasted twice.
            _externalCut = false;
            system.ForgetCut();
        }

        if (_isCut)
        {
            _paths = [];
            _isCut = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return Task.CompletedTask;
    }

    private IClipboard? Clipboard => windows.ActiveTopLevel is { } window ? TopLevel.GetTopLevel(window)?.Clipboard : null;

    private async Task SetTextCoreAsync(string text)
    {
        if (Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
    }
}
