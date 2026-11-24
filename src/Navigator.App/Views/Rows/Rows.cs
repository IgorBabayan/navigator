using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Navigator.Presentation.Items;
using Navigator.Presentation.Services;

namespace Navigator.App.Views.Rows;

/// <summary>Starts thumbnail loading when a row becomes visible and cancels it when the row is recycled.</summary>
internal static class RowThumbnails
{
    private static IThumbnailProvider? _provider;

    private static IThumbnailProvider Provider => _provider ??= App.Services.GetRequiredService<IThumbnailProvider>();

    public static void Request(Visual row, FileItemViewModel? item)
    {
        if (item is null || item.HasThumbnail)
            return;
        var size = row.FindAncestorOfType<PaneView>()?.Pane?.ThumbnailPixelSize ?? 48;
        item.RequestThumbnail(Provider, size);
    }
}

public sealed partial class DetailsRow : UserControl
{
    private FileItemViewModel? _item;

    public DetailsRow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            _item?.CancelThumbnail();
            _item = DataContext as FileItemViewModel;
            if (this.IsAttachedToVisualTree())
                RowThumbnails.Request(this, _item);
        };
        AttachedToVisualTree += (_, _) => RowThumbnails.Request(this, _item);
        DetachedFromVisualTree += (_, _) => _item?.CancelThumbnail();
    }
}

public sealed partial class ListRow : UserControl
{
    private FileItemViewModel? _item;

    public ListRow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            _item?.CancelThumbnail();
            _item = DataContext as FileItemViewModel;
            if (this.IsAttachedToVisualTree())
                RowThumbnails.Request(this, _item);
        };
        AttachedToVisualTree += (_, _) => RowThumbnails.Request(this, _item);
        DetachedFromVisualTree += (_, _) => _item?.CancelThumbnail();
    }
}

public sealed partial class TileRow : UserControl
{
    public TileRow() => InitializeComponent();

    private void OnTileAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control { DataContext: FileItemViewModel item } control)
            RowThumbnails.Request(control, item);
    }

    private void OnTileDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control { DataContext: FileItemViewModel item })
            item.CancelThumbnail();
    }
}

public sealed partial class GroupHeaderRow : UserControl
{
    public GroupHeaderRow() => InitializeComponent();
}

/// <summary>
/// Picks the row visual for each entry of PaneViewModel.Rows according to the pane's current layout.
/// Rows are rebuilt (one Reset) whenever the layout changes, so containers never mix templates.
/// </summary>
internal sealed class RowTemplates(PaneView owner) : IDataTemplate
{
    public bool Match(object? data) => data is FileItemViewModel or TileRowViewModel or GroupHeaderViewModel;

    public Control? Build(object? param) => param switch
    {
        TileRowViewModel => new TileRow(),
        GroupHeaderViewModel => new GroupHeaderRow(),
        FileItemViewModel when owner.Pane?.IsListLayout == true => new ListRow(),
        FileItemViewModel => new DetailsRow(),
        _ => null,
    };
}
