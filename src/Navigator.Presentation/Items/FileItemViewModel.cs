using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Navigator.Core.Formatting;
using Navigator.Core.IO;
using Navigator.Core.Platform;
using Navigator.Presentation.Localization;

namespace Navigator.Presentation.Items;

/// <summary>Per-listing formatting choices shared by all rows (one instance per refresh, not per item).</summary>
/// <param name="localizer">Texts and culture.</param>
/// <param name="showExtensions">Settings → Files and folders → show file extensions.</param>
/// <param name="systemIcons">The OS icon source; null (or unsupported) keeps Navigator's own icons for every row.</param>
public sealed class ItemDisplayContext(Localizer localizer, bool showExtensions, ISystemIconProvider? systemIcons = null)
{
    private static readonly string FolderIcon = SystemIconKey.Stock(SystemIconKind.Folder);

    private readonly ISystemIconProvider? _systemIcons = systemIcons is { IsSupported: true } ? systemIcons : null;

    // One key string per file type for the whole listing, however many rows share it.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _typeKeys = new(StringComparer.OrdinalIgnoreCase);

    public Localizer Localizer { get; } = localizer;

    public bool ShowExtensions { get; } = showExtensions;

    public CultureInfo Culture => Localizer.Culture;

    /// <summary>
    /// The OS icon for an item: the shared folder icon, the shared icon of its file type, or — for types whose files
    /// carry their own picture (programs, shortcuts) — the item's own icon. Null when system icons are off.
    /// </summary>
    public string? SystemIconFor(FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_systemIcons is null)
            return null;
        if (entry.IsDirectory)
        {
            // Windows reads a folder's desktop.ini (custom icon) only when the folder is marked read-only or system;
            // those few folders get their own icon, every other folder shares one. Costs no I/O: the flags come with the listing.
            return OperatingSystem.IsWindows() && !entry.IsRemote && (entry.Attributes & (FileAttributes.ReadOnly | FileAttributes.System)) != 0
                ? SystemIconKey.ForPath(entry.FullPath)
                : FolderIcon;
        }

        var extension = entry.Extension;
        if (!entry.IsRemote && _systemIcons.HasOwnIcon(extension))
            return SystemIconKey.ForPath(entry.FullPath);
        return _typeKeys.GetOrAdd(extension, SystemIconKey.ForFileType);
    }
}

public sealed record TagChip(string Id, string Name, string Color);

/// <summary>
/// One row/tile. Text columns are formatted lazily on first access (only realized, visible rows pay for it).
/// </summary>
public sealed class FileItemViewModel : ObservableObject
{
    private readonly ItemDisplayContext _context;
    private string? _typeText;
    private string? _sizeText;
    private string? _modifiedText;
    private string? _createdText;
    private bool _isSelected;
    private bool _isCut;
    private object? _thumbnail;
    private IReadOnlyList<TagChip> _tags = [];
    private long? _folderSize;
    private CancellationTokenSource? _thumbnailRequest;
    private PluginItemDecoration? _decoration;

    public FileItemViewModel(FileEntry entry, ItemDisplayContext context, TrashItem? trashItem = null)
    {
        Entry = entry;
        _context = context;
        TrashItem = trashItem;
        Category = entry.IsDirectory ? ItemCategory.Folder : entry.IsSymbolicLink && entry.Extension.Length == 0 ? ItemCategory.Link : ItemCategories.FromExtension(entry.Extension);
    }

    public FileEntry Entry { get; }

    /// <summary>Set when the row represents an item in the Trash.</summary>
    public TrashItem? TrashItem { get; }

    public string Name => Entry.Name;

    public string FullPath => Entry.FullPath;

    public bool IsDirectory => Entry.IsDirectory;

    public bool IsHiddenItem => Entry.IsHidden;

    public bool IsSymbolicLink => Entry.IsSymbolicLink;

    public ItemCategory Category { get; }

    public string IconKey => Category.ToString();

    /// <summary>The icon the operating system shows for this item (see <see cref="ItemDisplayContext.SystemIconFor"/>); null keeps <see cref="IconKey"/>.</summary>
    public string? SystemIconKey => _context.SystemIconFor(Entry);

    public string DisplayName
    {
        get
        {
            if (TrashItem is not null || _context.ShowExtensions || Entry.IsDirectory || Entry.Extension.Length == 0 || Entry.IsDotFile)
                return TrashItem?.Name ?? Entry.Name;
            return Entry.Name[..^Entry.Extension.Length];
        }
    }

    public string TypeText => _typeText ??= Entry.IsDirectory
        ? _context.Localizer["Item.FolderType"]
        : Entry.Extension.Length == 0
            ? _context.Localizer["Item.FileType"]
            : _context.Localizer.Format("Item.ExtensionFileType", Entry.Extension[1..].ToUpperInvariant());

    public string SizeText => _sizeText ??= Entry.IsDirectory
        ? (_folderSize is { } size ? ByteSize.Format(size, _context.Culture, _context.Localizer["Units.Bytes"], _context.Localizer["Units.Byte"]) : string.Empty)
        : ByteSize.Format(Entry.Length, _context.Culture, _context.Localizer["Units.Bytes"], _context.Localizer["Units.Byte"]);

    public string ModifiedText => _modifiedText ??= FormatDate(Entry.LastWriteTimeUtc);

    public string CreatedText => _createdText ??= FormatDate(Entry.CreationTimeUtc);

    public string DeletedText => TrashItem?.DeletedAt is { } deleted ? deleted.LocalDateTime.ToString("g", _context.Culture) : string.Empty;

    public string OriginalLocationText => TrashItem is null ? string.Empty : Path.GetDirectoryName(TrashItem.OriginalPath) ?? string.Empty;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>On the clipboard as "cut": drawn semi-transparent until pasted.</summary>
    public bool IsCut
    {
        get => _isCut;
        set
        {
            if (SetProperty(ref _isCut, value))
                OnPropertyChanged(nameof(ContentOpacity));
        }
    }

    public double ContentOpacity => _isCut ? 0.5 : Entry.IsHidden ? 0.65 : 1.0;

    public object? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            if (SetProperty(ref _thumbnail, value))
                OnPropertyChanged(nameof(HasThumbnail));
        }
    }

    public bool HasThumbnail => _thumbnail is not null;

    public IReadOnlyList<TagChip> Tags
    {
        get => _tags;
        set
        {
            if (SetProperty(ref _tags, value))
                OnPropertyChanged(nameof(HasTags));
        }
    }

    public bool HasTags => _tags.Count > 0;

    /// <summary>Plugin columns and icon badge (folder decorators); null when no plugin says anything about the item.</summary>
    public PluginItemDecoration? Decoration
    {
        get => _decoration;
        set
        {
            if (Equals(_decoration, value))
                return;
            var badgeChanged = !Equals(_decoration?.Badge, value?.Badge);
            _decoration = value;
            OnPropertyChanged(nameof(Decoration));
            if (badgeChanged)
            {
                OnPropertyChanged(nameof(BadgeIcon));
                OnPropertyChanged(nameof(BadgeTint));
            }
        }
    }

    /// <summary>Icon catalog key of the badge drawn on the item's icon; null for none.</summary>
    public string? BadgeIcon => _decoration?.Badge?.IconKey;

    /// <summary>Color of the badge as "#RRGGBB"; null for the text color.</summary>
    public string? BadgeTint => _decoration?.Badge?.Tint;

    public void SetFolderSize(long bytes)
    {
        _folderSize = bytes;
        _sizeText = null;
        OnPropertyChanged(nameof(SizeText));
    }

    /// <summary>
    /// Called when the row becomes visible. Loading is cancelled if the row scrolls away first,
    /// so fast scrolling through thousands of images never queues thousands of decodes.
    /// </summary>
    public async void RequestThumbnail(Services.IThumbnailProvider provider, int pixelSize)
    {
        // Remote entries would have to be downloaded first: they keep their type icon.
        if (_thumbnail is not null || _thumbnailRequest is not null || Entry.IsRemote || !provider.CanCreateThumbnail(Entry))
            return;

        var request = new CancellationTokenSource();
        _thumbnailRequest = request;
        try
        {
            var result = await provider.GetThumbnailAsync(Entry, pixelSize, request.Token).ConfigureAwait(true);
            if (!request.IsCancellationRequested)
                Thumbnail = result;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            // Unreadable or undecodable image: keep the type icon.
        }
        finally
        {
            if (ReferenceEquals(_thumbnailRequest, request))
                _thumbnailRequest = null;
            request.Dispose();
        }
    }

    /// <summary>Row scrolled out of view: cancel any pending thumbnail work.</summary>
    public void CancelThumbnail()
    {
        var request = _thumbnailRequest;
        _thumbnailRequest = null;
        try
        {
            request?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private string FormatDate(DateTime utc)
        => utc <= DateTime.MinValue.AddDays(1) || utc.Year < 1980 ? string.Empty : utc.ToLocalTime().ToString("g", _context.Culture);

    public override string ToString() => Name;
}

/// <summary>
/// What the folder decorators of plugins say about one item: the badge on its icon and the texts of the plugin columns,
/// keyed "{decorator id}.{column id}". Immutable; equal decorations compare equal, so re-applying unchanged results
/// raises no property changes and redraws nothing.
/// </summary>
public sealed class PluginItemDecoration : IEquatable<PluginItemDecoration>
{
    public static readonly IReadOnlyDictionary<string, string> NoCells = new Dictionary<string, string>(0);

    public PluginItemDecoration(Navigator.Plugins.PluginBadge? badge, IReadOnlyDictionary<string, string>? cells)
    {
        Badge = badge;
        Cells = cells is { Count: > 0 } ? cells : NoCells;
    }

    public Navigator.Plugins.PluginBadge? Badge { get; }

    public IReadOnlyDictionary<string, string> Cells { get; }

    public string CellText(string columnKey) => Cells.TryGetValue(columnKey, out var text) ? text : string.Empty;

    public bool Equals(PluginItemDecoration? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null || !Equals(Badge, other.Badge) || Cells.Count != other.Cells.Count)
            return false;
        foreach (var (key, text) in Cells)
        {
            if (!other.Cells.TryGetValue(key, out var theirs) || !string.Equals(text, theirs, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as PluginItemDecoration);

    public override int GetHashCode() => HashCode.Combine(Badge, Cells.Count);
}

/// <summary>Header row inserted in front of each group when grouping is on.</summary>
public sealed class GroupHeaderViewModel(string title, int count)
{
    public string Title { get; } = title;

    public int Count { get; } = count;
}

/// <summary>One row of tiles for the icon/grid layouts (rows are virtualized; tiles inside a row are few).</summary>
public sealed class TileRowViewModel(IReadOnlyList<FileItemViewModel> items)
{
    public IReadOnlyList<FileItemViewModel> Items { get; } = items;
}
