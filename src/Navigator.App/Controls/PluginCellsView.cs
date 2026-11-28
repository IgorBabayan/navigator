using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Navigator.Presentation.Items;
using Navigator.Presentation.Plugins;

namespace Navigator.App.Controls;

/// <summary>
/// The plugin columns of one details row ("Git status", "Status"…), drawn as text directly in Render: no child controls
/// and no bindings per cell, so a row costs the same whatever the number of plugin columns. Widths come from the pane's
/// <see cref="PluginColumnStrip"/>; <see cref="Revision"/> changes when one is resized. Formatted texts are kept until
/// their text or width changes, so scrolling re-measures nothing.
/// </summary>
public sealed class PluginCellsView : Control
{
    public static readonly StyledProperty<IReadOnlyList<PluginColumnViewModel>?> ColumnsProperty =
        AvaloniaProperty.Register<PluginCellsView, IReadOnlyList<PluginColumnViewModel>?>(nameof(Columns));

    public static readonly StyledProperty<int> RevisionProperty = AvaloniaProperty.Register<PluginCellsView, int>(nameof(Revision));

    public static readonly StyledProperty<PluginItemDecoration?> DecorationProperty =
        AvaloniaProperty.Register<PluginCellsView, PluginItemDecoration?>(nameof(Decoration));

    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<PluginCellsView>();

    public static readonly StyledProperty<double> FontSizeProperty = TextElement.FontSizeProperty.AddOwner<PluginCellsView>();

    public static readonly StyledProperty<FontFamily> FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner<PluginCellsView>();

    private const double CellPadding = 8;
    private FormattedText?[] _texts = [];
    private string?[] _textSources = [];
    private double[] _textWidths = [];

    static PluginCellsView()
    {
        AffectsMeasure<PluginCellsView>(ColumnsProperty, RevisionProperty);
        AffectsRender<PluginCellsView>(DecorationProperty, ForegroundProperty, FontSizeProperty, FontFamilyProperty);
    }

    public IReadOnlyList<PluginColumnViewModel>? Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public int Revision
    {
        get => GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    public PluginItemDecoration? Decoration
    {
        get => GetValue(DecorationProperty);
        set => SetValue(DecorationProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Another font or brush: every cached text is rebuilt on the next render.
        if (change.Property == ForegroundProperty || change.Property == FontSizeProperty || change.Property == FontFamilyProperty || change.Property == ColumnsProperty)
            Array.Clear(_textSources);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = 0.0;
        if (Columns is { } columns)
        {
            foreach (var column in columns)
                width += column.Width;
        }

        return new Size(width, double.IsFinite(availableSize.Height) ? availableSize.Height : FontSize * 1.5);
    }

    public override void Render(DrawingContext context)
    {
        if (Columns is not { Count: > 0 } columns || Decoration is not { } decoration)
            return;
        if (_texts.Length != columns.Count)
        {
            _texts = new FormattedText?[columns.Count];
            _textSources = new string?[columns.Count];
            _textWidths = new double[columns.Count];
        }

        var x = 0.0;
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            var text = decoration.CellText(column.Key);
            var room = column.Width - (2 * CellPadding);
            if (text.Length > 0 && room > 4)
            {
                var formatted = TextFor(i, text, room);
                var left = column.AlignRight ? x + column.Width - CellPadding - formatted.Width : x + CellPadding;
                context.DrawText(formatted, new Point(left, Math.Round((Bounds.Height - formatted.Height) / 2)));
            }

            x += column.Width;
        }
    }

    private FormattedText TextFor(int index, string text, double room)
    {
        if (_texts[index] is { } cached && ReferenceEquals(_textSources[index], text) && Math.Abs(_textWidths[index] - room) < 0.5)
            return cached;
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(FontFamily), FontSize, Foreground ?? Brushes.Gray)
        {
            MaxTextWidth = room,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        _texts[index] = formatted;
        _textSources[index] = text;
        _textWidths[index] = room;
        return formatted;
    }
}
