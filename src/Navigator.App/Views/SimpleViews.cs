using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Navigator.Core.IO;
using Navigator.Core.Navigation;
using Navigator.Presentation.Panes;
using Navigator.Presentation.Shell;

namespace Navigator.App.Views;

public sealed partial class SidebarView : UserControl
{
    /// <summary>Top/bottom share of a pinned entry that inserts before/after it; the middle drops into the folder.</summary>
    private const double PinEdge = 0.25;

    private SidebarItemViewModel? _pressed;
    private readonly IVolumeLocator _volumes;
    private readonly DropEvaluator _drop;
    private Border? _dropHighlight;
    private string? _dropClass;

    public SidebarView()
    {
        InitializeComponent();
        _volumes = App.Services.GetRequiredService<IVolumeLocator>();
        _drop = new DropEvaluator(_volumes);
        // Drop target: pin folders to the pinned group (header, or between pinned entries), or copy/move/upload items
        // into a pinned folder, a drive or a server entry.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    public SidebarViewModel? Model => DataContext as SidebarViewModel;

    /// <summary>Menu items a plugin added to the previous menu; replaced each time a menu opens.</summary>
    private static readonly object PluginItemTag = new();

    /// <summary>Re-checks the trash for its entry, then appends the entry's plugin actions (e.g. "Edit…", "Remove" for a saved server) to its context menu.</summary>
    private void OnItemMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu { DataContext: SidebarItemViewModel item } menu)
            return;
        if (item.IsTrash)
            Model?.RefreshTrashState();
        for (var i = menu.Items.Count - 1; i >= 0; i--)
        {
            if (menu.Items[i] is Control { Tag: var tag } && ReferenceEquals(tag, PluginItemTag))
                menu.Items.RemoveAt(i);
        }

        if (!item.HasCommands)
            return;
        menu.Items.Add(new Separator { Tag = PluginItemTag });
        foreach (var command in item.Commands)
        {
            var menuItem = new MenuItem
            {
                Header = command.Title,
                Command = command.Command,
                Tag = PluginItemTag,
                Icon = new Controls.IconView { Kind = command.IconKey, Width = 16, Height = 16 },
            };
            if (command.IsDestructive)
                menuItem.Classes.Add("destructive");
            menu.Items.Add(menuItem);
        }
    }

    private void OnItemPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: SidebarItemViewModel item })
            _pressed = item;
    }

    /// <summary>Left click navigates, middle click opens in a new tab; dropping a pinned item on another reorders.</summary>
    private void OnItemReleased(object? sender, PointerReleasedEventArgs e)
    {
        var pressed = _pressed;
        _pressed = null;
        if (pressed is null || Model is not { } model)
            return;

        var target = (this.InputHitTest(e.GetPosition(this)) as Control)?.DataContext as SidebarItemViewModel;
        if (target is not null && target != pressed && pressed.Path is not null && target.Path is not null)
        {
            var section = model.Sections.FirstOrDefault(s => s.IsReorderable && s.Items.Contains(target) && s.Items.Contains(pressed));
            if (section is not null)
            {
                model.MovePinned(pressed, section.Items.IndexOf(target));
                return;
            }
        }

        if (target != pressed)
            return;
        if (e.InitialPressMouseButton == MouseButton.Middle)
            model.OpenInNewTabCommand.Execute(pressed);
        else if (e.InitialPressMouseButton == MouseButton.Left)
            model.NavigateCommand.Execute(pressed);
    }

    // ── Drop target ────────────────────────────────────────────

    private SidebarDropTarget DropTargetAt(DragEventArgs e)
    {
        if (Model is not { } model)
            return default;

        for (var visual = e.Source as Visual; visual is not null && !ReferenceEquals(visual, this); visual = visual.GetVisualParent())
        {
            if (visual is Border { DataContext: SidebarItemViewModel item } surface && surface.Classes.Contains("hoverable"))
                return EntryTarget(model, item, surface, e.GetPosition(surface).Y);

            // The pinned group's header pins at the end (also while the group is collapsed or empty).
            if (visual is Button { DataContext: SidebarSectionViewModel { IsReorderable: true } } && model.CanPin)
                return new SidebarDropTarget(SidebarDropKind.Pin, null, null, model.PinnedCount, null);
        }

        return default;
    }

    private static SidebarDropTarget EntryTarget(SidebarViewModel model, SidebarItemViewModel item, Border surface, double y)
    {
        var height = surface.Bounds.Height;
        if (model.CanPin && item.IsTrash && model.Sections.Any(s => s.IsReorderable && s.Items.Contains(item)))
            return new SidebarDropTarget(SidebarDropKind.Pin, item, surface, model.PinnedCount, "dropbefore");
        if (model.CanPin && item.IsPinned && height > 0)
        {
            if (y < height * PinEdge)
                return new SidebarDropTarget(SidebarDropKind.Pin, item, surface, model.PinIndexOf(item), "dropbefore");
            if (y > height * (1 - PinEdge))
                return new SidebarDropTarget(SidebarDropKind.Pin, item, surface, model.PinIndexOf(item) + 1, "dropafter");
        }

        return item.Location.Kind is LocationKind.Folder or LocationKind.Remote && item.Path is not null
            ? new SidebarDropTarget(SidebarDropKind.Into, item, surface, 0, "droptarget")
            : default;
    }

    /// <summary>Pinning changes no file: "link" where the source offers it (the OS shows a shortcut arrow), else "copy".</summary>
    private static DragDropEffects PinEffect(DragEventArgs e)
    {
        if (ItemDragDrop.Active is { } own ? own.LocalFolders.Count == 0 : !e.DataTransfer.Contains(DataFormat.File))
            return DragDropEffects.None;
        if (e.DragEffects.HasFlag(DragDropEffects.Link))
            return DragDropEffects.Link;
        return e.DragEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    /// <summary>The pane that performs sidebar drops (its notifications and conflict dialogs belong to this window).</summary>
    private PaneViewModel? ActivePane => (TopLevel.GetTopLevel(this) as MainWindow)?.Shell.ActivePane;

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var target = DropTargetAt(e);
        e.DragEffects = target.Kind switch
        {
            SidebarDropKind.Pin => PinEffect(e),
            SidebarDropKind.Into when ActivePane is not null => _drop.Evaluate(e, target.Item!.Path),
            _ => DragDropEffects.None,
        };
        SetDropHighlight(e.DragEffects == DragDropEffects.None ? null : target.Surface, target.Highlight);
        e.Handled = true;
    }

    /// <summary>Raised for every child crossed (Enter follows at once): only the highlight is cleared.</summary>
    private void OnDragLeave(object? sender, DragEventArgs e) => SetDropHighlight(null, null);

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var target = DropTargetAt(e);
        SetDropHighlight(null, null);
        _drop.Reset();
        e.Handled = true;
        switch (target.Kind)
        {
            case SidebarDropKind.Pin when Model is { } model:
                var effect = PinEffect(e);
                List<string> folders = effect == DragDropEffects.None ? [] : ItemDragDrop.FoldersOnDrop(e).Where(p => !RemoteUri.IsRemote(p)).ToList();
                ItemDragDrop.ForgetRead();
                if (folders.Count == 0)
                {
                    e.DragEffects = DragDropEffects.None;
                    return;
                }

                model.PinFolders(folders, target.PinIndex);
                e.DragEffects = effect;
                return;
            case SidebarDropKind.Into:
                e.DragEffects = ItemDragDrop.Drop(e, target.Item!.Path, ActivePane, _volumes);
                return;
            default:
                e.DragEffects = DragDropEffects.None;
                return;
        }
    }

    /// <summary>One highlighted entry at a time; only a style class changes (insertion line or folder outline).</summary>
    private void SetDropHighlight(Border? surface, string? cssClass)
    {
        if (ReferenceEquals(surface, _dropHighlight) && string.Equals(cssClass, _dropClass, StringComparison.Ordinal))
            return;
        if (_dropHighlight is not null && _dropClass is not null)
            _dropHighlight.Classes.Remove(_dropClass);
        _dropHighlight = surface;
        _dropClass = surface is null ? null : cssClass;
        if (surface is not null && cssClass is not null)
            surface.Classes.Add(cssClass);
    }
}

/// <summary>What dropping on the sidebar does at the pointer.</summary>
internal enum SidebarDropKind
{
    None,

    /// <summary>Copy/move/upload into the entry's folder.</summary>
    Into,

    /// <summary>Pin the dragged folders at <see cref="SidebarDropTarget.PinIndex"/>.</summary>
    Pin,
}

internal readonly record struct SidebarDropTarget(SidebarDropKind Kind, SidebarItemViewModel? Item, Border? Surface, int PinIndex, string? Highlight);

/// <summary>Section expanded → chevron down, collapsed → chevron right.</summary>
public sealed class ExpandGlyphConverter : IValueConverter
{
    public static ExpandGlyphConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? "ChevronDown" : "ChevronRight";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

public sealed partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();
}

public sealed partial class PreviewPaneView : UserControl
{
    public PreviewPaneView() => InitializeComponent();
}

public sealed partial class OperationsView : UserControl
{
    public OperationsView() => InitializeComponent();
}
