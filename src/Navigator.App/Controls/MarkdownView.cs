using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Navigator.Presentation.Details;

namespace Navigator.App.Controls;

/// <summary>
/// Renders parsed Markdown (<see cref="MarkdownParser"/>) as native text blocks: headings, paragraphs with bold,
/// italic, strikethrough, code and link runs, code blocks, quotes, lists, rules and tables. Text stays selectable.
/// The tree is built once per document (the preview text is capped at 64 KB), not per layout pass; nothing is
/// fetched or executed (links are only underlined, images show their alt text).
/// </summary>
public sealed class MarkdownView : StackPanel
{
    public static readonly StyledProperty<IReadOnlyList<MarkdownBlock>?> BlocksProperty =
        AvaloniaProperty.Register<MarkdownView, IReadOnlyList<MarkdownBlock>?>(nameof(Blocks));

    private static readonly FontFamily Monospace = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");
    private static readonly IBrush CodeBackground = new SolidColorBrush(Color.FromArgb(0x24, 0x80, 0x80, 0x80));

    public MarkdownView() => Spacing = 8;

    private double BaseFontSize => GetValue(TextElement.FontSizeProperty);

    public IReadOnlyList<MarkdownBlock>? Blocks
    {
        get => GetValue(BlocksProperty);
        set => SetValue(BlocksProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BlocksProperty)
            Rebuild();
    }

    private void Rebuild()
    {
        Children.Clear();
        if (Blocks is not { } blocks)
            return;
        foreach (var block in blocks)
            Children.Add(Create(block));
    }

    private Control Create(MarkdownBlock block)
    {
        switch (block.Kind)
        {
            case MarkdownBlockKind.Heading:
                var heading = Text(block.Spans);
                heading.FontWeight = FontWeight.SemiBold;
                heading.FontSize = BaseFontSize * block.Level switch { 1 => 1.6, 2 => 1.35, 3 => 1.15, _ => 1.0 };
                heading.Margin = new Thickness(0, block.Level <= 2 ? 8 : 4, 0, 0);
                return heading;
            case MarkdownBlockKind.Code:
                return new Border
                {
                    Classes = { "md-code" },
                    Child = new SelectableTextBlock { Text = block.Code, FontFamily = Monospace, FontSize = BaseFontSize * 0.9, TextWrapping = TextWrapping.Wrap },
                };
            case MarkdownBlockKind.Quote:
                return new Border { Classes = { "md-quote" }, Margin = new Thickness(block.Level * 12, 0, 0, 0), Child = Text(block.Spans) };
            case MarkdownBlockKind.ListItem:
                var item = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(4 + (block.Level * 16), 0, 0, 0) };
                var marker = new TextBlock { Text = block.Marker, Margin = new Thickness(0, 0, 8, 0), MinWidth = 12 };
                var content = Text(block.Spans);
                Grid.SetColumn(content, 1);
                item.Children.Add(marker);
                item.Children.Add(content);
                return item;
            case MarkdownBlockKind.Rule:
                return new Border { Classes = { "md-rule" } };
            case MarkdownBlockKind.Table:
                return Table(block);
            default:
                return Text(block.Spans);
        }
    }

    private static ScrollViewer Table(MarkdownBlock block)
    {
        var rows = block.Rows ?? [];
        var columns = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        var grid = new Grid();
        for (var c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < rows[r].Count; c++)
            {
                var cell = Text(rows[r][c]);
                cell.Margin = new Thickness(0, 2, 16, 2);
                if (r == 0)
                    cell.FontWeight = FontWeight.SemiBold;
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }

        // Wide tables scroll sideways instead of squeezing the preview.
        return new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = grid };
    }

    private static SelectableTextBlock Text(IReadOnlyList<MarkdownSpan> spans)
    {
        var block = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Stretch };
        var inlines = new InlineCollection();
        foreach (var span in spans)
        {
            var run = new Run(span.Text);
            if ((span.Style & MarkdownStyle.Bold) != 0)
                run.FontWeight = FontWeight.SemiBold;
            if ((span.Style & MarkdownStyle.Italic) != 0)
                run.FontStyle = FontStyle.Italic;
            if ((span.Style & MarkdownStyle.Code) != 0)
            {
                run.FontFamily = Monospace;
                run.Background = CodeBackground;
            }

            if ((span.Style & MarkdownStyle.Strikethrough) != 0)
                run.TextDecorations = TextDecorations.Strikethrough;
            else if ((span.Style & MarkdownStyle.Link) != 0)
                run.TextDecorations = TextDecorations.Underline;
            inlines.Add(run);
        }

        block.Inlines = inlines;
        return block;
    }
}
