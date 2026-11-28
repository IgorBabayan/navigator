using CommunityToolkit.Mvvm.ComponentModel;
using Navigator.Core.IO;
using Navigator.Presentation.Collections;
using Navigator.Presentation.Items;

namespace Navigator.Presentation.Panes;

/// <summary>An item of a column left of the current folder; <see cref="IsOpen"/> marks the one the trail goes through.</summary>
public sealed class ColumnEntryViewModel(FileItemViewModel item) : ObservableObject
{
    private bool _isOpen;

    public FileItemViewModel Item { get; } = item;

    public bool IsOpen
    {
        get => _isOpen;
        internal set => SetProperty(ref _isOpen, value);
    }
}

/// <summary>
/// One column of the column (Miller) layout that sits left of the current folder: the listing of an ancestor with the
/// child on the way to the current folder highlighted. Read-only by design (the current folder, rightmost, is the
/// pane's own list with every command); clicking here navigates. Listings come from the folder just left, from the
/// directory cache, or from one background enumeration, and are not watched for changes afterwards.
/// </summary>
public sealed class MillerColumnViewModel : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private string _openChild = string.Empty;
    private int _openIndex = -1;
    private bool _isLoading = true;
    private string? _errorText;
    private bool _disposed;

    internal MillerColumnViewModel(string path, string title)
    {
        Path = path;
        Title = title;
    }

    public string Path { get; }

    public string Title { get; }

    public RangeObservableCollection<ColumnEntryViewModel> Entries { get; } = [];

    /// <summary>Index of the highlighted entry (for scrolling it into view), -1 while unknown.</summary>
    public int OpenIndex
    {
        get => _openIndex;
        private set => SetProperty(ref _openIndex, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string? ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => _errorText is not null;

    internal CancellationToken Token => _cancellation.Token;

    internal IReadOnlyList<FileItemViewModel> Items => [.. Entries.Select(e => e.Item)];

    internal void SetItems(IReadOnlyList<FileItemViewModel> items)
    {
        var entries = new List<ColumnEntryViewModel>(items.Count);
        foreach (var item in items)
            entries.Add(new ColumnEntryViewModel(item));
        Entries.ReplaceAll(entries);
        IsLoading = false;
        ErrorText = null;
        MarkOpen();
    }

    internal void SetError(string message)
    {
        Entries.ReplaceAll([]);
        IsLoading = false;
        ErrorText = message;
        OpenIndex = -1;
    }

    /// <summary>Highlights the child the trail continues through (the only entry whose state changes on navigation).</summary>
    internal void SetOpenChild(string path)
    {
        if (PathUtil.AreEqual(_openChild, path))
            return;
        _openChild = path;
        MarkOpen();
    }

    private void MarkOpen()
    {
        var open = -1;
        for (var i = 0; i < Entries.Count; i++)
        {
            var isOpen = open < 0 && PathUtil.AreEqual(Entries[i].Item.FullPath, _openChild);
            Entries[i].IsOpen = isOpen;
            if (isOpen)
                open = i;
        }

        OpenIndex = open;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cancellation.Cancel();
        _cancellation.Dispose();
    }
}
