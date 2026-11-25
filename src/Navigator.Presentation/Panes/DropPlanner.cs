using Navigator.Core.IO;
using Navigator.Core.Navigation;

namespace Navigator.Presentation.Panes;

/// <summary>What a drop of items on a folder does.</summary>
public enum DropAction
{
    None,
    Copy,
    Move,
}

/// <summary>
/// Decides what dropping items on a folder does. Pure and allocation-free, so drag targets can call it on every
/// drag-over event (the caller caches by destination and modifiers anyway).
/// </summary>
public static class DropPlanner
{
    /// <summary>
    /// <paramref name="sources"/> are the dragged paths, or null when they are not known before the drop (files dragged
    /// from another application on platforms where reading them mid-drag is slow): those are copied unless Shift is held.
    /// Known sources follow <see cref="PaneViewModel.ShouldMoveOnDrop"/>. A drop is refused when it would put a folder
    /// into itself, when it would leave the items where they already are (Ctrl still copies them as "- Copy"), and when
    /// server items would go anywhere (they leave a server only through Download); local items dropped on a server
    /// folder are uploaded.
    /// </summary>
    public static DropAction Plan(IReadOnlyList<string>? sources, string? destination, bool controlPressed, bool shiftPressed, IVolumeLocator volumes)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        if (string.IsNullOrEmpty(destination))
            return DropAction.None;

        var remoteDestination = RemoteUri.IsRemote(destination);
        if (sources is null)
            return !remoteDestination && shiftPressed && !controlPressed ? DropAction.Move : DropAction.Copy;
        if (sources.Count == 0)
            return DropAction.None;

        var allInDestination = true;
        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            if (RemoteUri.IsRemote(source))
                return DropAction.None;
            if (remoteDestination)
                continue;
            if (PathUtil.IsSameOrDescendant(source, destination))
                return DropAction.None;
            if (allInDestination && !PathUtil.AreEqual(Path.GetDirectoryName(PathUtil.TrimTrailingSeparator(source)), destination))
                allInDestination = false;
        }

        if (remoteDestination)
            return DropAction.Copy;
        if (allInDestination)
            return controlPressed ? DropAction.Copy : DropAction.None;
        return PaneViewModel.ShouldMoveOnDrop(sources, destination, controlPressed, shiftPressed, volumes) ? DropAction.Move : DropAction.Copy;
    }
}
