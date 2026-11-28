using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Navigator.Core.Settings;
using Navigator.Plugins;
using Navigator.Presentation.Settings;

namespace Navigator.Presentation.Plugins;

/// <summary>
/// The folder decorators of all plugins, shared by every pane: it follows plugin installs and removals and forwards the
/// decorators' change notifications as one event, so panes subscribe once instead of once per decorator.
/// </summary>
public sealed class FolderDecorationHub : IDisposable
{
    private readonly PluginHost _host;
    private readonly Lock _gate = new();
    private IReadOnlyList<IFolderDecorator> _subscribed = [];
    private bool _disposed;

    public FolderDecorationHub(PluginHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _host.Changed += OnHostChanged;
        Resubscribe();
    }

    /// <summary>The current decorators (immutable snapshot).</summary>
    public IReadOnlyList<IFolderDecorator> Decorators => _host.FolderDecorators;

    /// <summary>A decorator reported stale results (raised on the decorator's thread).</summary>
    public event EventHandler<FolderDecorationsChangedEventArgs>? Invalidated;

    /// <summary>Decorators were added or removed (raised on any thread).</summary>
    public event EventHandler? DecoratorsChanged;

    private void OnHostChanged(object? sender, EventArgs e)
    {
        if (Resubscribe())
            DecoratorsChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool Resubscribe()
    {
        lock (_gate)
        {
            if (_disposed)
                return false;
            var current = _host.FolderDecorators;
            if (current.SequenceEqual(_subscribed))
                return false;
            foreach (var old in _subscribed)
                old.Changed -= OnDecoratorChanged;
            foreach (var decorator in current)
                decorator.Changed += OnDecoratorChanged;
            _subscribed = current;
            return true;
        }
    }

    private void OnDecoratorChanged(object? sender, FolderDecorationsChangedEventArgs e) => Invalidated?.Invoke(sender, e);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _host.Changed -= OnHostChanged;
            foreach (var decorator in _subscribed)
                decorator.Changed -= OnDecoratorChanged;
            _subscribed = [];
        }
    }
}

/// <summary>One plugin column of a pane's details view: header text, width (shared by the header and every row).</summary>
public sealed class PluginColumnViewModel : ObservableObject
{
    private double _width;
    private bool _isVisible;

    internal PluginColumnViewModel(string decoratorId, PluginColumn column, double width, bool isVisible)
    {
        DecoratorId = decoratorId;
        ColumnId = column.Id;
        Key = decoratorId + "." + column.Id;
        Title = column.Title;
        AlignRight = column.AlignRight;
        _width = width;
        _isVisible = isVisible;
    }

    /// <summary>"{decorator id}.{column id}", as in <see cref="PluginItemDecoration.Cells"/> and the settings.</summary>
    public string Key { get; }

    public string DecoratorId { get; }

    public string ColumnId { get; }

    public string Title { get; }

    public bool AlignRight { get; }

    public double Width
    {
        get => _width;
        set => SetProperty(ref _width, Math.Clamp(value, 48, 1200));
    }

    public bool IsVisible
    {
        get => _isVisible;
        internal set => SetProperty(ref _isVisible, value);
    }
}

/// <summary>
/// The plugin columns of one pane: those of the decorators that apply to the folder shown. <see cref="Visible"/> is
/// replaced (never mutated) when the set changes, so views rebuild their headers only then; a width change bumps
/// <see cref="Revision"/>, which is all realized rows need to redraw. Widths and visibility are kept in
/// <see cref="FilesAndFoldersSettings.PluginColumns"/>.
/// </summary>
public sealed class PluginColumnStrip : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly Dictionary<string, PluginColumnViewModel> _known = new(StringComparer.Ordinal);
    private IReadOnlyList<PluginColumnViewModel> _available = [];
    private IReadOnlyList<PluginColumnViewModel> _visible = [];
    private int _revision;

    public PluginColumnStrip(SettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <summary>Every column the applicable decorators offer (header context menu: show/hide).</summary>
    public IReadOnlyList<PluginColumnViewModel> Available
    {
        get => _available;
        private set => SetProperty(ref _available, value);
    }

    /// <summary>The columns drawn, left to right.</summary>
    public IReadOnlyList<PluginColumnViewModel> Visible
    {
        get => _visible;
        private set
        {
            if (SetProperty(ref _visible, value))
                OnPropertyChanged(nameof(HasVisible));
        }
    }

    public bool HasVisible => _visible.Count > 0;

    /// <summary>Changes whenever a visible column's width changes.</summary>
    public int Revision
    {
        get => _revision;
        private set => SetProperty(ref _revision, value);
    }

    /// <summary>Ids of <paramref name="decorator"/>'s columns that are shown (tells it what it may skip computing).</summary>
    public IReadOnlySet<string> VisibleColumnsOf(IFolderDecorator decorator)
    {
        ArgumentNullException.ThrowIfNull(decorator);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in _visible)
        {
            if (string.Equals(column.DecoratorId, decorator.Id, StringComparison.Ordinal))
                ids.Add(column.ColumnId);
        }

        return ids;
    }

    /// <summary>Shows the columns of <paramref name="decorators"/> (the ones that apply to the folder). UI thread.</summary>
    public void Show(IReadOnlyList<IFolderDecorator> decorators)
    {
        ArgumentNullException.ThrowIfNull(decorators);
        var available = new List<PluginColumnViewModel>();
        foreach (var decorator in decorators)
        {
            IReadOnlyList<PluginColumn> columns;
            try
            {
                columns = decorator.Columns;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                continue; // a failing plugin simply has no columns
            }

            foreach (var column in columns)
                available.Add(ModelFor(decorator.Id, column));
        }

        if (!available.SequenceEqual(_available))
            Available = available;
        var visible = available.Where(c => c.IsVisible).ToList();
        if (!visible.SequenceEqual(_visible))
            Visible = visible;
    }

    /// <summary>Header context menu: shows or hides a column (remembered).</summary>
    public void SetVisible(PluginColumnViewModel column, bool isVisible)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (column.IsVisible == isVisible)
            return;
        column.IsVisible = isVisible;
        Visible = [.. _available.Where(c => c.IsVisible)];
        Save(column);
    }

    /// <summary>Header grip dragged.</summary>
    public void Resize(PluginColumnViewModel column, double delta)
    {
        ArgumentNullException.ThrowIfNull(column);
        var before = column.Width;
        column.Width += delta;
        if (Math.Abs(column.Width - before) < 0.01)
            return;
        Revision++;
        Save(column);
    }

    private PluginColumnViewModel ModelFor(string decoratorId, PluginColumn column)
    {
        var key = decoratorId + "." + column.Id;
        if (_known.TryGetValue(key, out var known) && string.Equals(known.Title, column.Title, StringComparison.Ordinal) && known.AlignRight == column.AlignRight)
            return known;

        var stored = _settings.Current.FilesAndFolders.PluginColumns.Find(c => string.Equals(c.Id, key, StringComparison.Ordinal));
        var model = new PluginColumnViewModel(decoratorId, column, stored?.Width ?? column.DefaultWidth, stored?.IsVisible ?? column.IsVisibleByDefault);
        _known[key] = model;
        return model;
    }

    private void Save(PluginColumnViewModel column) => _settings.Update("columns", s =>
    {
        var list = s.FilesAndFolders.PluginColumns;
        var index = list.FindIndex(c => string.Equals(c.Id, column.Key, StringComparison.Ordinal));
        var setting = new ColumnSetting { Id = column.Key, Width = column.Width, IsVisible = column.IsVisible };
        if (index >= 0)
            list[index] = setting;
        else
            list.Insert(0, setting); // most recently changed first: the bound drops the oldest
    });
}

/// <summary>One command of a status bar segment ("Pull", "Push").</summary>
public sealed record StatusSegmentCommandViewModel(string Title, string IconKey, bool IsDestructive, ICommand Command);

/// <summary>A plugin's segment of the status bar for the folder shown (e.g. the Git branch).</summary>
public sealed class StatusSegmentViewModel(string decoratorId, PluginStatusSegment segment, IReadOnlyList<StatusSegmentCommandViewModel> commands)
{
    public string DecoratorId { get; } = decoratorId;

    public string Text { get; } = segment.Text;

    public string IconKey { get; } = segment.IconKey;

    public string? ToolTip { get; } = segment.ToolTip;

    public IReadOnlyList<StatusSegmentCommandViewModel> Commands { get; } = commands;

    public bool HasCommands => Commands.Count > 0;

    internal PluginStatusSegment Source { get; } = segment;
}
