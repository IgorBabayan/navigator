using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Navigator.Core.IO;
using Navigator.Core.Navigation;
using Navigator.Presentation.Items;
using Navigator.Presentation.Panes;

namespace Navigator.App.Views;

/// <summary>Items being dragged by Navigator itself (any pane, any tab, any window of this process).</summary>
internal sealed class ItemDrag(string[] paths, string[] localFolders)
{
    public IReadOnlyList<string> Paths { get; } = paths;

    /// <summary>Local folders among <see cref="Paths"/>: what the pinned group accepts.</summary>
    public IReadOnlyList<string> LocalFolders { get; } = localFolders;
}

/// <summary>
/// Drag and drop of files and folders, shared by the panes, the tab strip and the sidebar.
/// <para>
/// Outgoing drags carry <see cref="DataFormat.File"/> items, so other applications (Explorer, Finder, file managers,
/// mail clients, editors…) receive real files. Each item is resolved lazily, only when a target asks for it, so starting
/// a drag of thousands of items costs nothing beyond one small object per item. Navigator's own targets never read
/// the transfer: they use <see cref="Active"/>, which also carries server paths that have no OS file representation.
/// </para>
/// <para>
/// Incoming drags from other applications are inspected by format only while hovering (reading the data mid-drag is a
/// synchronous round trip to the source on X11); the paths are read once, on drop. Windows is the exception: its
/// in-process OLE data object is cheap to read, so the paths are read once per drag to pick move or copy by volume.
/// </para>
/// </summary>
internal static class ItemDragDrop
{
    private static IDataTransfer? _readTransfer;
    private static IReadOnlyList<string>? _readPaths;

    /// <summary>Navigator's drag in progress, or null. Only one drag can run at a time on every platform.</summary>
    public static ItemDrag? Active { get; private set; }

    /// <summary>
    /// Starts dragging <paramref name="items"/> and returns when the drop is done (or cancelled). Server items can only
    /// be copied (uploads/downloads); local items can be copied, moved, or linked (pinned in the sidebar).
    /// </summary>
    public static async Task StartAsync(PointerPressedEventArgs trigger, Visual source, IReadOnlyList<FileItemViewModel> items)
    {
        if (Active is not null || items.Count == 0)
            return;

        var storage = TopLevel.GetTopLevel(source)?.StorageProvider;
        using var transfer = new DataTransfer();
        var paths = new string[items.Count];
        var folders = new List<string>();
        var anyRemote = false;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var path = item.FullPath;
            paths[i] = path;
            if (RemoteUri.IsRemote(path))
            {
                anyRemote = true;
                continue;
            }

            var isDirectory = item.IsDirectory;
            if (isDirectory)
                folders.Add(path);
            if (storage is null)
                continue;
            var entry = new DataTransferItem();
            entry.Set(DataFormat.File, () => Resolve(storage, path, isDirectory));
            transfer.Add(entry);
        }

        Active = new ItemDrag(paths, [.. folders]);
        var allowed = anyRemote ? DragDropEffects.Copy : DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link;
        try
        {
            await DragDrop.DoDragDropAsync(trigger, transfer, allowed).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // The source left the visual tree before the platform drag started (e.g. its tab was closed): nothing to drag.
        }
        finally
        {
            Active = null;
            ForgetRead();
        }
    }

    /// <summary>Paths known while hovering: Navigator's own drag, Windows file drags; null when only the drop reveals them.</summary>
    public static IReadOnlyList<string>? PathsWhileHovering(DragEventArgs e)
    {
        if (Active is { } active)
            return active.Paths;
        return OperatingSystem.IsWindows() ? ReadPaths(e) : null;
    }

    /// <summary>True when the drag carries something Navigator can drop: its own items or files from another app.</summary>
    public static bool CarriesFiles(DragEventArgs e) => Active is not null || e.DataTransfer.Contains(DataFormat.File);

    /// <summary>All dragged paths, read on drop (synchronously: the platform releases the data when the event returns).</summary>
    public static IReadOnlyList<string> PathsOnDrop(DragEventArgs e) => Active?.Paths ?? ReadPaths(e) ?? [];

    /// <summary>Local folders among the dragged items, for pinning (empty for files and server items).</summary>
    public static IReadOnlyList<string> FoldersOnDrop(DragEventArgs e)
    {
        if (Active is { } active)
            return active.LocalFolders;
        var paths = ReadPaths(e);
        if (paths is null)
            return [];
        var folders = new List<string>(paths.Count);
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
                folders.Add(path);
        }

        return folders;
    }

    /// <summary>
    /// Maps the planned action onto what the source allows. The move cursor needs the source's consent; when only a
    /// copy is allowed the drop still works as a copy.
    /// </summary>
    public static DragDropEffects ToEffect(DropAction action, DragDropEffects allowed) => action switch
    {
        DropAction.Move when allowed.HasFlag(DragDropEffects.Move) => DragDropEffects.Move,
        DropAction.Move or DropAction.Copy when allowed.HasFlag(DragDropEffects.Copy) => DragDropEffects.Copy,
        _ => DragDropEffects.None,
    };

    /// <summary>
    /// The effect reported back to the drag source after a drop that Navigator performs itself. Another application
    /// is always told "copy": on "move" some sources delete their originals while Navigator's queued move is still
    /// running, which would lose data.
    /// </summary>
    public static DragDropEffects ReportedEffect(DragDropEffects effect) => Active is null && effect == DragDropEffects.Move ? DragDropEffects.Copy : effect;

    /// <summary>
    /// Runs a drop on <paramref name="destination"/>: plans it from the same information the hover used (so the result
    /// matches the cursor), re-validates it against the real paths, then queues the copy/move/upload. Returns the effect.
    /// </summary>
    public static DragDropEffects Drop(DragEventArgs e, string? destination, PaneViewModel? executor, IVolumeLocator volumes)
    {
        if (executor is null || string.IsNullOrEmpty(destination))
            return DragDropEffects.None;
        var control = e.KeyModifiers.HasFlag(PrimaryModifier);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var action = DropPlanner.Plan(PathsWhileHovering(e), destination, control, shift, volumes);
        var effect = ToEffect(action, e.DragEffects);
        if (effect == DragDropEffects.None)
            return DragDropEffects.None;

        var sources = PathsOnDrop(e);
        var move = effect == DragDropEffects.Move;
        // Files from another app are checked again now that every path is known: a folder dropped into itself, or
        // files dropped on the folder they are already in, are refused. (Navigator's own paths were known all along.)
        if (sources.Count == 0 || (Active is null && DropPlanner.Plan(sources, destination, control, shift, volumes) == DropAction.None))
        {
            ForgetRead();
            return DragDropEffects.None;
        }

        ForgetRead();

        _ = executor.TransferIntoAsync(sources, destination, move);
        return ReportedEffect(effect);
    }

    public static KeyModifiers PrimaryModifier => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    private static IStorageItem? Resolve(IStorageProvider storage, string path, bool isDirectory)
    {
        // Every desktop storage provider answers synchronously; a pending task is never waited on (it would block the UI).
        if (isDirectory)
        {
            var folder = storage.TryGetFolderFromPathAsync(path);
            return folder.IsCompletedSuccessfully ? folder.Result : null;
        }

        var file = storage.TryGetFileFromPathAsync(path);
        return file.IsCompletedSuccessfully ? file.Result : null;
    }

    /// <summary>Local paths of the dragged files, read once per transfer and cached for the rest of the drag.</summary>
    private static IReadOnlyList<string>? ReadPaths(DragEventArgs e)
    {
        var transfer = e.DataTransfer;
        if (ReferenceEquals(transfer, _readTransfer))
            return _readPaths;

        IReadOnlyList<string>? paths = null;
        if (transfer.Contains(DataFormat.File) && transfer.TryGetFiles() is { Length: > 0 } files)
        {
            var list = new List<string>(files.Length);
            foreach (var file in files)
            {
                if (file.TryGetLocalPath() is { Length: > 0 } local)
                    list.Add(local);
            }

            paths = list;
        }

        _readTransfer = transfer;
        _readPaths = paths;
        return paths;
    }

    /// <summary>Drops the cached paths (end of a drag), so a finished transfer is not kept alive.</summary>
    public static void ForgetRead()
    {
        _readTransfer = null;
        _readPaths = null;
    }
}

/// <summary>
/// Per-target cache of the hover decision: drag-over fires on every mouse move (and periodically on Windows), but the
/// answer only changes with the destination, the modifier keys, or the dragged set.
/// </summary>
internal sealed class DropEvaluator(IVolumeLocator volumes)
{
    private object? _source;
    private string? _destination;
    private KeyModifiers _modifiers;
    private DropAction _action;

    public DragDropEffects Evaluate(DragEventArgs e, string? destination)
    {
        if (string.IsNullOrEmpty(destination) || !ItemDragDrop.CarriesFiles(e))
            return DragDropEffects.None;

        var paths = ItemDragDrop.PathsWhileHovering(e);
        var source = (object?)paths ?? e.DataTransfer;
        var modifiers = e.KeyModifiers & (ItemDragDrop.PrimaryModifier | KeyModifiers.Shift);
        if (!ReferenceEquals(source, _source) || modifiers != _modifiers || !string.Equals(destination, _destination, StringComparison.Ordinal))
        {
            _source = source;
            _destination = destination;
            _modifiers = modifiers;
            _action = DropPlanner.Plan(paths, destination, modifiers.HasFlag(ItemDragDrop.PrimaryModifier), modifiers.HasFlag(KeyModifiers.Shift), volumes);
        }

        return ItemDragDrop.ToEffect(_action, e.DragEffects);
    }

    /// <summary>Releases the references to the last drag (called when it ends).</summary>
    public void Reset()
    {
        _source = null;
        _destination = null;
    }
}
