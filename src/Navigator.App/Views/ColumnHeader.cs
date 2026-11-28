using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Navigator.App.Controls;
using Navigator.Core.Settings;
using Navigator.Core.Sorting;

namespace Navigator.App.Views;

/// <summary>Widths of the details-view columns, shared by the header and every realized row of one pane.</summary>
public sealed class ColumnLayout : ObservableObject
{
    private double _name = 320, _modified = 170, _type = 140, _size = 100, _created = 170, _tags = 120;
    private bool _showCreated, _showTags = true;
    private IReadOnlyList<string> _order = ColumnOrder.Known;

    public double NameWidth { get => _name; set => SetProperty(ref _name, Clamp(value)); }

    public double ModifiedWidth { get => _modified; set => SetProperty(ref _modified, Clamp(value)); }

    public double TypeWidth { get => _type; set => SetProperty(ref _type, Clamp(value)); }

    public double SizeWidth { get => _size; set => SetProperty(ref _size, Clamp(value)); }

    public double CreatedWidth { get => _created; set => SetProperty(ref _created, Clamp(value)); }

    public double TagsWidth { get => _tags; set => SetProperty(ref _tags, Clamp(value)); }

    public bool ShowCreated { get => _showCreated; set => SetProperty(ref _showCreated, value); }

    public bool ShowTags { get => _showTags; set => SetProperty(ref _showTags, value); }

    /// <summary>Column ids left to right (the name column first); rows and the header arrange their cells by it.</summary>
    public IReadOnlyList<string> Order { get => _order; private set => SetProperty(ref _order, value); }

    private static double Clamp(double value) => Math.Clamp(value, 48, 1200);

    /// <summary>Moves a column to <paramref name="index"/> in <see cref="Order"/> (the name column stays first).</summary>
    public void Move(string id, int index) => Order = ColumnOrder.Move(_order, id, index);

    public void Load(IEnumerable<ColumnSetting> columns, bool showTags)
    {
        var stored = columns as IReadOnlyCollection<ColumnSetting> ?? [.. columns];
        Order = ColumnOrder.Normalize(stored.Select(c => c.Id));
        foreach (var column in stored)
        {
            switch (column.Id)
            {
                case "name": NameWidth = column.Width; break;
                case "modified": ModifiedWidth = column.Width; break;
                case "type": TypeWidth = column.Width; break;
                case "size": SizeWidth = column.Width; break;
                case "created": CreatedWidth = column.Width; ShowCreated = column.IsVisible; break;
                case "tags": TagsWidth = column.Width; break;
            }
        }

        ShowTags = showTags;
    }

    /// <summary>The stored form: one entry per column, in display order.</summary>
    public List<ColumnSetting> ToSettings() => [.. _order.Select(ToSetting)];

    private ColumnSetting ToSetting(string id) => id switch
    {
        "modified" => new() { Id = id, Width = ModifiedWidth },
        "type" => new() { Id = id, Width = TypeWidth },
        "size" => new() { Id = id, Width = SizeWidth },
        "created" => new() { Id = id, Width = CreatedWidth, IsVisible = ShowCreated },
        "tags" => new() { Id = id, Width = TagsWidth, IsVisible = ShowTags },
        _ => new() { Id = id, Width = NameWidth },
    };
}

/// <summary>
/// One details-view column header: sort on click (with direction glyph), resize by dragging its right edge, and move
/// the column by dragging the header sideways (it follows the pointer through a render transform, so nothing is laid
/// out while dragging; the drop position is reported to the pane, which reorders the columns once).
/// </summary>
public sealed class ColumnHeader : Grid
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<ColumnHeader, string?>(nameof(Text));

    private const double ReorderThreshold = 8;
    private readonly TranslateTransform _shift = new();
    private Point? _pressAt;
    private bool _reordering;
    private bool _suppressClick;

    private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
    private readonly IconView _glyph = new() { Kind = "ChevronDown", Width = 10, Height = 10, IsVisible = false, Margin = new Thickness(6, 0, 0, 0) };

    public ColumnHeader()
    {
        ColumnDefinitions = new ColumnDefinitions("*,6");
        var button = new Button
        {
            Classes = { "subtle" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 0),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { _label, _glyph } },
        };
        button.Click += (_, _) =>
        {
            if (_suppressClick)
            {
                _suppressClick = false;
                return;
            }

            if (IsSortable)
                SortRequested?.Invoke(this, Column);
        };
        // The button handles presses and releases itself, so the reorder gesture listens to handled events too.
        button.AddHandler(PointerPressedEvent, OnHeaderPressed, handledEventsToo: true);
        button.AddHandler(PointerMovedEvent, OnHeaderMoved, handledEventsToo: true);
        button.AddHandler(PointerReleasedEvent, OnHeaderReleased, handledEventsToo: true);
        // Capture can be released just before the release event is seen here: cancel a little later, so a drop wins.
        button.AddHandler(PointerCaptureLostEvent, (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => EndReorder(drop: false, x: 0)), handledEventsToo: true);
        RenderTransform = _shift;

        var grip = new Thumb { Cursor = new Cursor(StandardCursorType.SizeWestEast), Background = Brushes.Transparent, Width = 6 };
        grip.DragDelta += (_, e) => Resized?.Invoke(this, e.Vector.X);
        SetColumn(grip, 1);
        Children.Add(button);
        Children.Add(grip);
    }

    public SortColumn Column { get; set; }

    /// <summary>Column id ("modified", "tags"…) as stored in settings; the name column cannot be moved.</summary>
    public string ColumnId { get; set; } = ColumnOrder.NameColumn;

    private bool IsMovable => !string.Equals(ColumnId, ColumnOrder.NameColumn, StringComparison.Ordinal);

    public bool IsSortable { get; set; } = true;

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public event EventHandler<SortColumn>? SortRequested;

    public event EventHandler<double>? Resized;

    /// <summary>A sideways drag ended; the argument is the pointer's x position relative to the header row.</summary>
    public event EventHandler<double>? ReorderDropped;

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        _suppressClick = false;
        _pressAt = IsMovable && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ? e.GetPosition(Parent as Visual ?? this) : null;
    }

    private void OnHeaderMoved(object? sender, PointerEventArgs e)
    {
        if (_pressAt is not { } start || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var dx = e.GetPosition(Parent as Visual ?? this).X - start.X;
        if (!_reordering)
        {
            if (Math.Abs(dx) < ReorderThreshold)
                return;
            _reordering = true;
            _suppressClick = true; // the button raises Click on release before this control sees the release
            ZIndex = 1;
            Opacity = 0.8;
        }

        _shift.X = dx;
    }

    private void OnHeaderReleased(object? sender, PointerReleasedEventArgs e)
        => EndReorder(drop: _reordering, x: e.GetPosition(Parent as Visual ?? this).X);

    private void EndReorder(bool drop, double x)
    {
        var wasReordering = _reordering;
        _pressAt = null;
        _reordering = false;
        if (!wasReordering)
            return;
        _shift.X = 0;
        ZIndex = 0;
        Opacity = 1;
        if (drop)
            ReorderDropped?.Invoke(this, x);
    }

    public void ShowSort(SortOptions sort)
    {
        _glyph.IsVisible = IsSortable && sort.Column == Column;
        _glyph.Kind = sort.Direction == SortDirection.Descending ? "ChevronDown" : "Up";
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
            _label.Text = change.GetNewValue<string?>();
    }
}
