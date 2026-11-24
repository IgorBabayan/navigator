namespace Navigator.Core.IO;

/// <summary>
/// Decides whether two paths live on the same volume, which determines whether a move can be an
/// atomic rename or must be a verified copy followed by deleting the source.
/// </summary>
public interface IVolumeLocator
{
    /// <summary>Mount point / drive root that contains <paramref name="path"/>.</summary>
    string GetVolumeRoot(string path);

    bool AreOnSameVolume(string first, string second);
}

/// <summary>
/// Longest-prefix match of the path against the mounted volume roots reported by <see cref="DriveInfo"/>
/// (drive letters on Windows, mount table on Linux/macOS). The mount list is cached briefly because
/// moves of many items query it repeatedly.
/// </summary>
public sealed class VolumeLocator : IVolumeLocator
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(5);
    private readonly Func<IReadOnlyList<string>> _rootsProvider;
    private readonly Lock _gate = new();
    private string[] _roots = [];
    private long _loadedAt = long.MinValue;

    public VolumeLocator()
        : this(ReadRoots)
    {
    }

    internal VolumeLocator(Func<IReadOnlyList<string>> rootsProvider) => _rootsProvider = rootsProvider;

    public string GetVolumeRoot(string path)
    {
        var normalized = PathUtil.TrimTrailingSeparator(path);
        string? best = null;
        foreach (var root in GetRoots())
        {
            if (PathUtil.IsSameOrDescendant(root, normalized) && (best is null || root.Length > best.Length))
                best = root;
        }

        return best ?? Path.GetPathRoot(normalized) ?? normalized;
    }

    public bool AreOnSameVolume(string first, string second)
        => string.Equals(GetVolumeRoot(first), GetVolumeRoot(second), PathUtil.Comparison);

    private string[] GetRoots()
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (now - _loadedAt > CacheLifetime.TotalMilliseconds)
            {
                _roots = [.. _rootsProvider().Select(PathUtil.TrimTrailingSeparator)];
                _loadedAt = now;
            }

            return _roots;
        }
    }

    private static IReadOnlyList<string> ReadRoots()
    {
        try
        {
            return [.. DriveInfo.GetDrives().Select(d => d.RootDirectory.FullName)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
