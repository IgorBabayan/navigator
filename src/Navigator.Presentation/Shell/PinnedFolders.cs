using Navigator.Core.IO;
using Navigator.Core.Platform;
using Navigator.Presentation.Settings;

namespace Navigator.Presentation.Shell;

/// <summary>Quick-access pins shared by the sidebar and the Home page. First run pins the platform's user folders.</summary>
public sealed class PinnedFolders
{
    private static readonly KnownFolder[] Defaults =
        [KnownFolder.Desktop, KnownFolder.Downloads, KnownFolder.Documents, KnownFolder.Pictures, KnownFolder.Music, KnownFolder.Videos];

    private readonly SettingsService _settings;
    private readonly IKnownFolderProvider _knownFolders;

    public PinnedFolders(SettingsService settings, IKnownFolderProvider knownFolders)
    {
        _settings = settings;
        _knownFolders = knownFolders;
        _settings.Changed += (_, section) =>
        {
            if (section is "*" or "sidebar")
                Changed?.Invoke(this, EventArgs.Empty);
        };
    }

    public event EventHandler? Changed;

    public IReadOnlyList<string> Paths
        => _settings.Current.Sidebar.PinnedFolders
           ?? [.. Defaults.Select(_knownFolders.GetPath).Where(p => p is not null).Select(p => p!)];

    public bool IsPinned(string path) => Paths.Any(p => PathUtil.AreEqual(p, path));

    public void Pin(string path)
    {
        if (IsPinned(path))
            return;
        var list = Paths.ToList();
        list.Add(PathUtil.TrimTrailingSeparator(path));
        Save(list);
    }

    /// <summary>
    /// Pins folders at <paramref name="index"/> (drag and drop onto the pinned group). Folders that are already pinned
    /// move to that position instead of being pinned twice. One settings write for the whole batch.
    /// </summary>
    public void PinAt(IReadOnlyList<string> paths, int index)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var incoming = new List<string>(paths.Count);
        foreach (var path in paths)
        {
            var trimmed = PathUtil.TrimTrailingSeparator(path);
            if (trimmed.Length > 0 && !incoming.Exists(p => PathUtil.AreEqual(p, trimmed)))
                incoming.Add(trimmed);
        }

        if (incoming.Count == 0)
            return;

        var list = Paths.ToList();
        var insertAt = Math.Clamp(index, 0, list.Count);
        for (var i = list.Count - 1; i >= 0; i--)
        {
            var existing = list[i];
            if (!incoming.Exists(p => PathUtil.AreEqual(p, existing)))
                continue;
            if (i < insertAt)
                insertAt--;
            list.RemoveAt(i);
        }

        list.InsertRange(insertAt, incoming);
        Save(list);
    }

    public void Unpin(string path) => Save([.. Paths.Where(p => !PathUtil.AreEqual(p, path))]);

    public void Move(string path, int newIndex)
    {
        var list = Paths.ToList();
        var index = list.FindIndex(p => PathUtil.AreEqual(p, path));
        if (index < 0)
            return;
        var item = list[index];
        list.RemoveAt(index);
        list.Insert(Math.Clamp(newIndex, 0, list.Count), item);
        Save(list);
    }

    public KnownFolder? KnownFolderFor(string path)
    {
        foreach (var folder in Enum.GetValues<KnownFolder>())
        {
            if (_knownFolders.GetPath(folder) is { } known && PathUtil.AreEqual(known, path))
                return folder;
        }

        return null;
    }

    /// <summary>
    /// OS icon for a pinned folder: user folders (Desktop, Downloads, Pictures…) get their own system icon; any other
    /// folder gets the plain stock folder icon, so a slow or disconnected location is never touched just to draw an icon.
    /// </summary>
    public static string SystemIconKeyFor(string path, KnownFolder? known)
        => known is not null ? SystemIconKey.ForPath(path) : SystemIconKey.Stock(SystemIconKind.Folder);

    private void Save(List<string> paths) => _settings.Update("sidebar", s => s.Sidebar.PinnedFolders = paths);
}
