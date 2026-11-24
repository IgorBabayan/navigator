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

    public double NameWidth { get => _name; set => SetProperty(ref _name, Clamp(value)); }

    public double ModifiedWidth { get => _modified; set => SetProperty(ref _modified, Clamp(value)); }

    public double TypeWidth { get => _type; set => SetProperty(ref _type, Clamp(value)); }

    public double SizeWidth { get => _size; set => SetProperty(ref _size, Clamp(value)); }

    public double CreatedWidth { get => _created; set => SetProperty(ref _created, Clamp(value)); }

    public double TagsWidth { get => _tags; set => SetProperty(ref _tags, Clamp(value)); }

    public bool ShowCreated { get => _showCreated; set => SetProperty(ref _showCreated, value); }

    public bool ShowTags { get => _showTags; set => SetProperty(ref _showTags, value); }

    private static double Clamp(double value) => Math.Clamp(value, 48, 1200);

    public void Load(IEnumerable<ColumnSetting> columns, bool showTags)
    {
        foreach (var column in columns)
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

    public List<ColumnSetting> ToSettings() =>
    [
        new() { Id = "name", Width = NameWidth },
        new() { Id = "modified", Width = ModifiedWidth },
        new() { Id = "type", Width = TypeWidth },
        new() { Id = "size", Width = SizeWidth },
        new() { Id = "created", Width = CreatedWidth, IsVisible = ShowCreated },
        new() { Id = "tags", Width = TagsWidth, IsVisible = ShowTags },
    ];
}

/// <summary>One details-view column header: sort on click (with direction glyph), resize by dragging its right edge.</summary>
public sealed class ColumnHeader : Grid
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<ColumnHeader, string?>(nameof(Text));

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
            if (IsSortable)
                SortRequested?.Invoke(this, Column);
        };

        var grip = new Thumb { Cursor = new Cursor(StandardCursorType.SizeWestEast), Background = Brushes.Transparent, Width = 6 };
        grip.DragDelta += (_, e) => Resized?.Invoke(this, e.Vector.X);
        SetColumn(grip, 1);
        Children.Add(button);
        Children.Add(grip);
    }

    public SortColumn Column { get; set; }

    public bool IsSortable { get; set; } = true;

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public event EventHandler<SortColumn>? SortRequested;

    public event EventHandler<double>? Resized;

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
