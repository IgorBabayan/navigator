using Avalonia.Controls;
using Avalonia.Input.Platform;
using Navigator.Presentation.Services;

namespace Navigator.App.Services;

/// <summary>
/// File clipboard. Copy/cut/paste between Navigator windows uses an in-process list (exact paths, cut state).
/// The paths are also placed on the OS clipboard as text so they can be pasted into other applications.
/// Pasting files copied in other file managers needs the Avalonia 12 file data-transfer formats and is tracked
/// in docs/KNOWN_ISSUES.md.
/// </summary>
internal sealed class ClipboardService(WindowService windows) : IClipboardService
{
    private IReadOnlyList<string> _paths = [];
    private bool _isCut;
    private string? _publishedText;

    public event EventHandler? Changed;

    public async Task SetFilesAsync(IReadOnlyList<string> paths, bool cut)
    {
        _paths = [.. paths];
        _isCut = cut;
        _publishedText = string.Join(Environment.NewLine, paths);
        await SetTextCoreAsync(_publishedText).ConfigureAwait(true);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<(IReadOnlyList<string> Paths, bool IsCut)?> GetFilesAsync()
    {
        if (_paths.Count == 0)
            return null;

        // If another application replaced the clipboard since our copy, our file list is stale.
        var clipboard = Clipboard;
        if (clipboard is not null)
        {
            var text = await clipboard.TryGetTextAsync().ConfigureAwait(true);
            if (text is not null && !string.Equals(text, _publishedText, StringComparison.Ordinal))
            {
                _paths = [];
                return null;
            }
        }

        return (_paths, _isCut);
    }

    public Task SetTextAsync(string text)
    {
        _paths = [];
        _publishedText = null;
        return SetTextCoreAsync(text);
    }

    public Task ClearCutStateAsync()
    {
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
