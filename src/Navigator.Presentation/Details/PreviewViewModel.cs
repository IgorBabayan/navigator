using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Navigator.Core.Formatting;
using Navigator.Core.Hashing;
using Navigator.Core.IO;
using Navigator.Presentation.Items;
using Navigator.Presentation.Panes;
using Navigator.Presentation.Services;

namespace Navigator.Presentation.Details;

public enum PreviewKind
{
    None,
    Image,
    Text,
    Markdown,
    Folder,
    MultipleItems,
    Metadata,
}

public sealed record PropertyRow(string Label, string Value);

/// <summary>
/// Preview / details pane for the active pane's selection. Loading is debounced (rapid arrow-key navigation
/// does not start a load per row), cancellable and always off the UI thread; text previews read at most 64 KB.
/// </summary>
public sealed class PreviewViewModel : ObservableObject, IDisposable
{
    private const int MaxTextBytes = 64 * 1024;
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(120);

    private readonly PaneServices _services;
    private PaneViewModel? _pane;
    private CancellationTokenSource? _current;
    private PreviewKind _kind;
    private object? _image;
    private string? _text;
    private string _title = string.Empty;
    private string _iconKey = "File";
    private IReadOnlyList<PropertyRow> _properties = [];
    private string? _sha256;
    private bool _isEnabled;
    private bool _isLoading;

    public PreviewViewModel(PaneServices services)
    {
        _services = services;
        ComputeHashCommand = new AsyncRelayCommand(ComputeHashAsync, () => _pane?.Selection.SelectedItems is [{ IsDirectory: false }]);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value) && value)
                _ = RefreshAsync();
        }
    }

    public PreviewKind Kind
    {
        get => _kind;
        private set
        {
            if (SetProperty(ref _kind, value))
            {
                OnPropertyChanged(nameof(IsImage));
                OnPropertyChanged(nameof(IsText));
                OnPropertyChanged(nameof(HasNothing));
            }
        }
    }

    public bool IsImage => _kind == PreviewKind.Image;

    public bool IsText => _kind is PreviewKind.Text or PreviewKind.Markdown;

    public bool HasNothing => _kind == PreviewKind.None;

    public object? Image
    {
        get => _image;
        private set => SetProperty(ref _image, value);
    }

    public string? Text
    {
        get => _text;
        private set => SetProperty(ref _text, value);
    }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string IconKey
    {
        get => _iconKey;
        private set => SetProperty(ref _iconKey, value);
    }

    public IReadOnlyList<PropertyRow> Properties
    {
        get => _properties;
        private set => SetProperty(ref _properties, value);
    }

    public string? Sha256
    {
        get => _sha256;
        private set => SetProperty(ref _sha256, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public IAsyncRelayCommand ComputeHashCommand { get; }

    public void Attach(PaneViewModel pane)
    {
        if (_pane is not null)
            _pane.SelectionChanged -= OnSelectionChanged;
        _pane = pane;
        _pane.SelectionChanged += OnSelectionChanged;
        _ = RefreshAsync();
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        ComputeHashCommand.NotifyCanExecuteChanged();
        if (_isEnabled)
            _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        _current?.Cancel();
        _current?.Dispose();
        var request = new CancellationTokenSource();
        _current = request;
        var token = request.Token;

        try
        {
            await Task.Delay(Debounce, token).ConfigureAwait(true);
            await LoadAsync(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            Kind = PreviewKind.Metadata;
            Text = null;
        }
        finally
        {
            if (ReferenceEquals(_current, request))
                IsLoading = false;
        }
    }

    private async Task LoadAsync(CancellationToken token)
    {
        Image = null;
        Text = null;
        Sha256 = null;
        var localizer = _services.Localizer;
        var selected = _pane?.Selection.SelectedItems ?? [];

        if (selected.Count == 0)
        {
            Kind = PreviewKind.None;
            Title = _pane?.Title ?? string.Empty;
            IconKey = "Folder";
            Properties = [];
            return;
        }

        if (selected.Count > 1)
        {
            Kind = PreviewKind.MultipleItems;
            Title = localizer.Format("Status.Selected", selected.Count);
            IconKey = "Multiple";
            var size = selected.Where(i => !i.IsDirectory).Sum(i => Math.Max(0, i.Entry.Length));
            Properties = [new PropertyRow(localizer["Property.Size"], ByteSize.Format(size, localizer.Culture))];
            return;
        }

        var item = selected[0];
        Title = item.Name;
        IconKey = item.IconKey;
        IsLoading = true;
        Properties = BuildProperties(item, folderSize: null);

        if (item.IsDirectory)
        {
            Kind = PreviewKind.Folder;
            var statistics = await Task.Run(() => FolderStatisticsCalculator.Calculate(item.FullPath, cancellationToken: token), token).ConfigureAwait(true);
            Properties = BuildProperties(item, statistics);
            return;
        }

        var extension = item.Entry.Extension;
        if (ItemCategories.IsPreviewableImage(extension))
        {
            Kind = PreviewKind.Image;
            Image = await _services.Thumbnails.GetThumbnailAsync(item.Entry, 640, token).ConfigureAwait(true);
            return;
        }

        if (ItemCategories.IsPreviewableText(extension) || item.Entry.Length is > 0 and < 4096 && extension.Length == 0)
        {
            var text = await Task.Run(() => ReadTextPreview(item.FullPath), token).ConfigureAwait(true);
            if (text is not null)
            {
                Kind = extension is ".md" or ".markdown" ? PreviewKind.Markdown : PreviewKind.Text;
                Text = text;
                return;
            }
        }

        Kind = PreviewKind.Metadata;
    }

    /// <summary>First 64 KB as UTF-8; null for binary content.</summary>
    internal static string? ReadTextPreview(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[(int)Math.Min(MaxTextBytes, stream.Length)];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        var span = buffer.AsSpan(0, read);
        if (span.Contains((byte)0))
            return null;
        var text = Encoding.UTF8.GetString(span);
        return stream.Length > MaxTextBytes ? text + "\n…" : text;
    }

    private List<PropertyRow> BuildProperties(FileItemViewModel item, FolderStatistics? folderSize)
    {
        var localizer = _services.Localizer;
        var rows = new List<PropertyRow>
        {
            new(localizer["Property.Type"], item.TypeText),
            new(localizer["Property.Location"], Path.GetDirectoryName(item.FullPath) ?? string.Empty),
        };

        if (!item.IsDirectory)
            rows.Add(new PropertyRow(localizer["Property.Size"], ByteSize.Format(item.Entry.Length, localizer.Culture)));
        else if (folderSize is { } statistics)
        {
            rows.Add(new PropertyRow(localizer["Property.Size"], ByteSize.Format(statistics.TotalBytes, localizer.Culture) + (statistics.IsPartial ? " *" : string.Empty)));
            rows.Add(new PropertyRow(localizer["Property.Contains"], localizer.Format("Property.ContainsValue", statistics.FileCount, statistics.FolderCount)));
        }

        rows.Add(new PropertyRow(localizer["Property.Modified"], item.ModifiedText));
        rows.Add(new PropertyRow(localizer["Property.Created"], item.CreatedText));
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                rows.Add(new PropertyRow(localizer["Property.Permissions"], PermissionText.Format(File.GetUnixFileMode(item.FullPath), item.IsDirectory)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return rows;
    }

    private async Task ComputeHashAsync()
    {
        if (_pane?.Selection.SelectedItems is not [{ IsDirectory: false } item])
            return;
        Sha256 = _services.Localizer["Property.Computing"];
        try
        {
            Sha256 = await FileHasher.ComputeAsync(item.FullPath, HashKind.Sha256).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Sha256 = ex.Message;
        }
    }

    public void Dispose()
    {
        if (_pane is not null)
            _pane.SelectionChanged -= OnSelectionChanged;
        _current?.Cancel();
        _current?.Dispose();
    }
}

public static class PermissionText
{
    /// <summary>"drwxr-xr-x" style text for a Unix mode.</summary>
    public static string Format(UnixFileMode mode, bool isDirectory)
    {
        Span<char> text = stackalloc char[10];
        text[0] = isDirectory ? 'd' : '-';
        text[1] = (mode & UnixFileMode.UserRead) != 0 ? 'r' : '-';
        text[2] = (mode & UnixFileMode.UserWrite) != 0 ? 'w' : '-';
        text[3] = (mode & UnixFileMode.SetUser) != 0 ? ((mode & UnixFileMode.UserExecute) != 0 ? 's' : 'S') : (mode & UnixFileMode.UserExecute) != 0 ? 'x' : '-';
        text[4] = (mode & UnixFileMode.GroupRead) != 0 ? 'r' : '-';
        text[5] = (mode & UnixFileMode.GroupWrite) != 0 ? 'w' : '-';
        text[6] = (mode & UnixFileMode.SetGroup) != 0 ? ((mode & UnixFileMode.GroupExecute) != 0 ? 's' : 'S') : (mode & UnixFileMode.GroupExecute) != 0 ? 'x' : '-';
        text[7] = (mode & UnixFileMode.OtherRead) != 0 ? 'r' : '-';
        text[8] = (mode & UnixFileMode.OtherWrite) != 0 ? 'w' : '-';
        text[9] = (mode & UnixFileMode.StickyBit) != 0 ? ((mode & UnixFileMode.OtherExecute) != 0 ? 't' : 'T') : (mode & UnixFileMode.OtherExecute) != 0 ? 'x' : '-';
        return new string(text);
    }
}
