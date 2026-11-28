using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Navigator.Core.Documents;
using Navigator.Core.Formatting;
using Navigator.Core.Hashing;
using Navigator.Core.IO;
using Navigator.Core.Media;
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
    Pdf,
    Video,
    Audio,
    Document,
}

public sealed record PropertyRow(string Label, string Value);

/// <summary>
/// Preview of the active pane's selection, used by the preview pane and the Quick Look window: pictures, text and
/// rendered Markdown, PDFs (first page + document information), videos (a frame + stream details), audio (album art,
/// tags, playback) and Office/OpenDocument files (first page or saved thumbnail + their text).
/// <para>
/// Loading is debounced (rapid arrow-key navigation does not start a load per row), cancellable and always off the UI
/// thread. The picture and the file's details are read concurrently; text previews read at most 64 KB, media and PDF
/// readers only touch headers, documents stop after 16 KB of text.
/// </para>
/// </summary>
public sealed class PreviewViewModel : ObservableObject, IDisposable
{
    /// <summary>Picture size (longer side, px) for the preview pane.</summary>
    public const int PaneImageSize = 640;

    /// <summary>Picture size for the Quick Look window.</summary>
    public const int QuickLookImageSize = 1600;

    private const int MaxTextBytes = 64 * 1024;
    private const int MaxDocumentCharacters = 16 * 1024;
    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(120);

    private readonly PaneServices _services;
    private readonly int _imageSize;
    private readonly TimeSpan _debounce;
    private PaneViewModel? _pane;
    private CancellationTokenSource? _current;
    private IDisposable? _playbackTimer;
    private string? _loadedPath;
    private PreviewKind _kind;
    private object? _image;
    private string? _text;
    private IReadOnlyList<MarkdownBlock> _markdown = [];
    private string _title = string.Empty;
    private string _subtitle = string.Empty;
    private string _iconKey = "File";
    private IReadOnlyList<PropertyRow> _properties = [];
    private string? _sha256;
    private bool _isEnabled;
    private bool _isLoading;
    private bool _isPlaying;
    private bool _disposed;

    public PreviewViewModel(PaneServices services)
        : this(services, PaneImageSize, DefaultDebounce)
    {
    }

    public PreviewViewModel(PaneServices services, int imageSize, TimeSpan debounce)
    {
        _services = services;
        _imageSize = imageSize;
        _debounce = debounce;
        ComputeHashCommand = new AsyncRelayCommand(ComputeHashAsync, () => _pane?.Selection.SelectedItems is [{ IsDirectory: false, Entry.IsRemote: false }]);
        TogglePlaybackCommand = new RelayCommand(TogglePlayback, () => CanPlay);
        OpenCommand = new AsyncRelayCommand(OpenAsync, () => _loadedPath is not null);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!SetProperty(ref _isEnabled, value))
                return;
            if (value)
            {
                _ = RefreshAsync();
            }
            else
            {
                StopPlayback();
                _current?.Cancel();
            }
        }
    }

    public PreviewKind Kind
    {
        get => _kind;
        private set
        {
            if (SetProperty(ref _kind, value))
            {
                OnPropertyChanged(nameof(IsText));
                OnPropertyChanged(nameof(IsMarkdown));
                OnPropertyChanged(nameof(IsDocumentText));
                OnPropertyChanged(nameof(IsMedia));
                OnPropertyChanged(nameof(CanPlay));
                OnPropertyChanged(nameof(HasNothing));
                RaiseLayoutFlags();
                TogglePlaybackCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Plain text and source code (monospace).</summary>
    public bool IsText => _kind == PreviewKind.Text && _text is not null;

    public bool IsMarkdown => _kind == PreviewKind.Markdown && _markdown.Count > 0;

    /// <summary>Text extracted from a document (proportional font).</summary>
    public bool IsDocumentText => _kind == PreviewKind.Document && !string.IsNullOrEmpty(_text);

    public bool IsMedia => _kind is PreviewKind.Audio or PreviewKind.Video;

    /// <summary>The selection is an audio file this platform can play.</summary>
    public bool CanPlay => _kind == PreviewKind.Audio && _services.AudioPlayer.IsSupported && _loadedPath is not null;

    public bool HasNothing => _kind == PreviewKind.None;

    /// <summary>A picture is shown (photo, video frame, PDF/document page, album art); otherwise the type icon.</summary>
    public bool HasImage => _image is not null;

    /// <summary>Quick Look: nothing to draw but the type icon (no picture and no text).</summary>
    public bool ShowLargeIcon => !HasImage && !IsText && !IsMarkdown && !IsDocumentText;

    /// <summary>Quick Look: a document without a page picture shows its text instead.</summary>
    public bool ShowDocumentTextOnly => IsDocumentText && !HasImage;

    public object? Image
    {
        get => _image;
        private set
        {
            if (SetProperty(ref _image, value))
            {
                OnPropertyChanged(nameof(HasImage));
                RaiseLayoutFlags();
            }
        }
    }

    public string? Text
    {
        get => _text;
        private set
        {
            if (SetProperty(ref _text, value))
            {
                OnPropertyChanged(nameof(IsText));
                OnPropertyChanged(nameof(IsDocumentText));
                RaiseLayoutFlags();
            }
        }
    }

    private void RaiseLayoutFlags()
    {
        OnPropertyChanged(nameof(ShowLargeIcon));
        OnPropertyChanged(nameof(ShowDocumentTextOnly));
    }

    public IReadOnlyList<MarkdownBlock> Markdown
    {
        get => _markdown;
        private set
        {
            if (SetProperty(ref _markdown, value))
            {
                OnPropertyChanged(nameof(IsMarkdown));
                RaiseLayoutFlags();
            }
        }
    }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    /// <summary>Type and size ("PDF document · 2.4 MB").</summary>
    public string Subtitle
    {
        get => _subtitle;
        private set => SetProperty(ref _subtitle, value);
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

    public bool IsPlaying
    {
        get => _isPlaying;
        private set => SetProperty(ref _isPlaying, value);
    }

    public IAsyncRelayCommand ComputeHashCommand { get; }

    /// <summary>Plays or stops the previewed audio file.</summary>
    public IRelayCommand TogglePlaybackCommand { get; }

    /// <summary>Opens the previewed file with its default application.</summary>
    public IAsyncRelayCommand OpenCommand { get; }

    public void Attach(PaneViewModel pane)
    {
        if (ReferenceEquals(_pane, pane))
            return;
        if (_pane is not null)
            _pane.SelectionChanged -= OnSelectionChanged;
        _pane = pane;
        _pane.SelectionChanged += OnSelectionChanged;
        if (_isEnabled)
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
        if (_disposed)
            return;
        _current?.Cancel();
        _current?.Dispose();
        var request = new CancellationTokenSource();
        _current = request;
        var token = request.Token;

        try
        {
            await Task.Delay(_debounce, token).ConfigureAwait(true);
            await LoadAsync(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or InvalidDataException)
        {
            if (!token.IsCancellationRequested)
            {
                Kind = PreviewKind.Metadata;
                Text = null;
            }
        }
        finally
        {
            if (ReferenceEquals(_current, request))
                IsLoading = false;
        }
    }

    private async Task LoadAsync(CancellationToken token)
    {
        StopPlayback();
        Image = null;
        Text = null;
        Markdown = [];
        Sha256 = null;
        SetLoadedPath(null);
        var localizer = _services.Localizer;
        var selected = _pane?.Selection.SelectedItems ?? [];

        if (selected.Count == 0)
        {
            Kind = PreviewKind.None;
            Title = _pane?.Title ?? string.Empty;
            Subtitle = string.Empty;
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
            Subtitle = ByteSize.Format(size, localizer.Culture);
            Properties = [new PropertyRow(localizer["Property.Size"], Subtitle)];
            return;
        }

        var item = selected[0];
        var entry = item.Entry;
        Title = item.Name;
        IconKey = item.IconKey;
        Subtitle = item.IsDirectory ? item.TypeText : item.TypeText + " · " + ByteSize.Format(Math.Max(0, entry.Length), localizer.Culture);
        IsLoading = true;
        Properties = BuildProperties(item, folderSize: null);

        if (entry.IsRemote)
        {
            // Plugin files are not read for previews (that would download them); metadata only.
            Kind = item.IsDirectory ? PreviewKind.Folder : PreviewKind.Metadata;
            return;
        }

        if (item.IsDirectory)
        {
            Kind = PreviewKind.Folder;
            var statistics = await Task.Run(() => FolderStatisticsCalculator.Calculate(item.FullPath, cancellationToken: token), token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            Properties = BuildProperties(item, statistics);
            return;
        }

        SetLoadedPath(item.FullPath);
        var path = item.FullPath;
        var extension = entry.Extension;
        var category = ItemCategories.FromExtension(extension);

        // The picture is made while the details are read.
        var imageTask = _services.Thumbnails.CanCreatePreviewImage(entry)
            ? _services.Thumbnails.GetPreviewImageAsync(entry, _imageSize, token)
            : Task.FromResult<object?>(null);

        if (ItemCategories.IsPreviewableImage(extension))
        {
            Kind = PreviewKind.Image;
            Image = await imageTask.ConfigureAwait(true);
            return;
        }

        if (extension == ".pdf")
        {
            Kind = PreviewKind.Pdf;
            var pdf = await Task.Run(() => PdfInfoReader.Read(path), token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (pdf is not null)
                Properties = [.. PdfRows(pdf), .. Properties];
            Image = await imageTask.ConfigureAwait(true);
            return;
        }

        if (category is ItemCategory.Video or ItemCategory.Audio)
        {
            Kind = category == ItemCategory.Video ? PreviewKind.Video : PreviewKind.Audio;
            OnPropertyChanged(nameof(CanPlay));
            TogglePlaybackCommand.NotifyCanExecuteChanged();
            if (MediaInfoReader.IsSupported(extension))
            {
                var media = await Task.Run(() => MediaInfoReader.Read(path), token).ConfigureAwait(true);
                token.ThrowIfCancellationRequested();
                if (media is not null)
                    Properties = [.. MediaRows(media), .. Properties];
            }

            Image = await imageTask.ConfigureAwait(true);
            return;
        }

        if (OfficeDocumentReader.IsSupported(extension))
        {
            Kind = PreviewKind.Document;
            var document = await Task.Run(() => OfficeDocumentReader.Read(path, MaxDocumentCharacters, token), token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (document is not null)
            {
                Properties = [.. DocumentRows(document), .. Properties];
                Text = document.IsTruncated ? document.Text + "\n…" : document.Text;
            }

            Image = await imageTask.ConfigureAwait(true);
            return;
        }

        if (category is ItemCategory.Document or ItemCategory.Spreadsheet or ItemCategory.Presentation && extension != ".csv")
        {
            Kind = PreviewKind.Document;
            Image = await imageTask.ConfigureAwait(true);
            return;
        }

        if (ItemCategories.IsPreviewableText(extension) || entry.Length is > 0 and < 4096 && extension.Length == 0)
        {
            var text = await Task.Run(() => ReadTextPreview(path), token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (text is not null)
            {
                if (extension is ".md" or ".markdown" or ".mdown" or ".mkd")
                {
                    var blocks = await Task.Run(() => MarkdownParser.Parse(text), token).ConfigureAwait(true);
                    token.ThrowIfCancellationRequested();
                    Kind = PreviewKind.Markdown;
                    Text = text;
                    Markdown = blocks;
                }
                else
                {
                    Kind = PreviewKind.Text;
                    Text = text;
                }

                return;
            }
        }

        // Anything else the platform can draw (HEIF, PSD, RAW…) is shown as a picture.
        var picture = await imageTask.ConfigureAwait(true);
        token.ThrowIfCancellationRequested();
        Image = picture;
        Kind = picture is not null ? PreviewKind.Image : PreviewKind.Metadata;
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

    private List<PropertyRow> PdfRows(PdfSummary pdf)
    {
        var localizer = _services.Localizer;
        var rows = new List<PropertyRow>();
        Add(rows, localizer["Property.Title"], pdf.Title);
        Add(rows, localizer["Property.Author"], pdf.Author);
        if (pdf.PageCount is { } pages)
            rows.Add(new PropertyRow(localizer["Property.Pages"], pages.ToString("N0", localizer.Culture)));
        Add(rows, localizer["Property.PdfVersion"], pdf.Version);
        Add(rows, localizer["Property.Producer"], pdf.Producer);
        if (pdf.IsEncrypted)
            rows.Add(new PropertyRow(localizer["Property.Protection"], localizer["Property.Encrypted"]));
        return rows;
    }

    private List<PropertyRow> MediaRows(MediaInfo media)
    {
        var localizer = _services.Localizer;
        var culture = localizer.Culture;
        var rows = new List<PropertyRow>();
        Add(rows, localizer["Media.Title"], media.Title);
        Add(rows, localizer["Media.Artist"], media.Artist);
        Add(rows, localizer["Media.Album"], media.Album);
        Add(rows, localizer["Media.Year"], media.Year);
        Add(rows, localizer["Media.Track"], media.Track);
        Add(rows, localizer["Media.Genre"], media.Genre);
        if (media.Duration is { } duration)
            rows.Add(new PropertyRow(localizer["Media.Duration"], FormatDuration(duration, culture)));
        if (media.Width is { } width && media.Height is { } height)
            rows.Add(new PropertyRow(localizer["Media.Resolution"], string.Create(culture, $"{width} × {height}")));
        Add(rows, localizer["Media.VideoCodec"], media.VideoCodec);
        Add(rows, localizer["Media.AudioCodec"], media.AudioCodec);
        if (media.SampleRate is { } rate)
            rows.Add(new PropertyRow(localizer["Media.SampleRate"], (rate / 1000.0).ToString("0.#", culture) + " kHz"));
        if (media.Channels is { } channels)
            rows.Add(new PropertyRow(localizer["Media.Channels"], channels switch { 1 => localizer["Media.Mono"], 2 => localizer["Media.Stereo"], _ => localizer.Format("Media.ChannelsValue", channels) }));
        if (media.BitsPerSample is { } bits)
            rows.Add(new PropertyRow(localizer["Media.BitDepth"], localizer.Format("Media.BitsValue", bits)));
        if (media.Bitrate is { } bitrate)
            rows.Add(new PropertyRow(localizer["Media.Bitrate"], localizer.Format("Media.BitrateValue", bitrate)));
        Add(rows, localizer["Media.Format"], media.Format);
        return rows;
    }

    private List<PropertyRow> DocumentRows(DocumentSummary document)
    {
        var localizer = _services.Localizer;
        var culture = localizer.Culture;
        var rows = new List<PropertyRow>();
        Add(rows, localizer["Property.Title"], document.Title);
        Add(rows, localizer["Property.Author"], document.Author);
        Add(rows, localizer["Property.LastModifiedBy"], document.LastModifiedBy);
        if (document.Pages is { } pages)
            rows.Add(new PropertyRow(localizer["Property.Pages"], pages.ToString("N0", culture)));
        if (document.Words is { } words)
            rows.Add(new PropertyRow(localizer["Property.Words"], words.ToString("N0", culture)));
        if (document.Slides is { } slides)
            rows.Add(new PropertyRow(localizer["Property.Slides"], slides.ToString("N0", culture)));
        if (document.Sheets is { } sheets)
            rows.Add(new PropertyRow(localizer["Property.Sheets"], sheets.ToString("N0", culture)));
        return rows;
    }

    private static void Add(List<PropertyRow> rows, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            rows.Add(new PropertyRow(label, value));
    }

    internal static string FormatDuration(TimeSpan duration, CultureInfo culture)
        => duration.TotalHours >= 1
            ? string.Create(culture, $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}")
            : string.Create(culture, $"{duration.Minutes}:{duration.Seconds:00}");

    private List<PropertyRow> BuildProperties(FileItemViewModel item, FolderStatistics? folderSize)
    {
        var localizer = _services.Localizer;
        var rows = new List<PropertyRow>
        {
            new(localizer["Property.Type"], item.TypeText),
            new(localizer["Property.Location"], (item.Entry.IsRemote ? Navigator.Core.Navigation.RemoteUri.GetParent(item.FullPath) : Path.GetDirectoryName(item.FullPath)) ?? string.Empty),
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
        if (!OperatingSystem.IsWindows() && !item.Entry.IsRemote)
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

    private void SetLoadedPath(string? path)
    {
        _loadedPath = path;
        OnPropertyChanged(nameof(CanPlay));
        TogglePlaybackCommand.NotifyCanExecuteChanged();
        OpenCommand.NotifyCanExecuteChanged();
    }

    private void TogglePlayback()
    {
        if (IsPlaying)
        {
            StopPlayback();
            return;
        }

        if (_loadedPath is null || !_services.AudioPlayer.Play(_loadedPath))
            return;
        IsPlaying = true;

        // The player has no completion event on every platform: poll while playing, so the button resets at the end.
        _playbackTimer?.Dispose();
        _playbackTimer = _services.Dispatcher.StartTimer(TimeSpan.FromMilliseconds(500), () =>
        {
            if (!_services.AudioPlayer.IsPlaying)
                StopPlayback();
        });
    }

    private void StopPlayback()
    {
        _playbackTimer?.Dispose();
        _playbackTimer = null;
        if (!_isPlaying)
            return;
        _services.AudioPlayer.Stop();
        IsPlaying = false;
    }

    private async Task OpenAsync()
    {
        if (_loadedPath is { } path)
            await _services.Launcher.OpenAsync(path).ConfigureAwait(true);
    }

    private async Task ComputeHashAsync()
    {
        if (_pane?.Selection.SelectedItems is not [{ IsDirectory: false } item] || item.Entry.IsRemote)
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

    /// <summary>Idempotent: the owning shell may be disposed by its window and again during application shutdown.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        StopPlayback();
        if (_pane is not null)
            _pane.SelectionChanged -= OnSelectionChanged;
        _pane = null;
        var current = _current;
        _current = null;
        current?.Cancel();
        current?.Dispose();
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
