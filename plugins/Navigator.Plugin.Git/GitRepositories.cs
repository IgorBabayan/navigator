using System.Collections.Concurrent;

namespace Navigator.Plugin.Git;

/// <summary>A work tree: its root folder and its git directory (".git", or the folder a ".git" file points to).</summary>
internal sealed record GitRepository(string Root, string GitDirectory);

/// <summary>
/// Finds the repository a folder belongs to by looking for ".git" in it and its parents — a few file-system checks,
/// answered from a cache for every later folder of the same tree. Network paths (\\server\share, mounted remote
/// volumes are not detected) are skipped: git over SMB is slow and usually refused for "dubious ownership" anyway.
/// </summary>
internal sealed class GitRepositoryLocator
{
    private const int MaxCached = 4096;
    private static readonly TimeSpan FoundLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MissingLifetime = TimeSpan.FromSeconds(15);
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly ConcurrentDictionary<string, (GitRepository? Repository, long At)> _cache = new(PathComparer);

    /// <summary>The repository containing <paramref name="folder"/>, or null (also inside a ".git" folder itself).</summary>
    public GitRepository? Find(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        if (IsSkipped(folder))
            return null;
        var now = Environment.TickCount64;
        if (_cache.TryGetValue(folder, out var cached) && now - cached.At < (cached.Repository is null ? MissingLifetime : FoundLifetime).TotalMilliseconds)
            return cached.Repository;

        var found = Walk(folder);
        if (_cache.Count >= MaxCached)
            _cache.Clear();
        _cache[folder] = (found, now);
        return found;
    }

    /// <summary>Forgets cached answers (a repository was created or removed).</summary>
    public void Clear() => _cache.Clear();

    private static bool IsSkipped(string folder)
    {
        if (folder.StartsWith(@"\\", StringComparison.Ordinal) || folder.StartsWith("//", StringComparison.Ordinal))
            return true;
        foreach (var segment in folder.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private GitRepository? Walk(string folder)
    {
        string? current;
        try
        {
            current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        while (current is not null)
        {
            // A parent answered earlier answers for its children too.
            if (!ReferenceEquals(current, folder) && _cache.TryGetValue(current, out var known) && known.Repository is not null
                && Environment.TickCount64 - known.At < FoundLifetime.TotalMilliseconds)
                return known.Repository;

            var marker = Path.Combine(current, ".git");
            try
            {
                if (Directory.Exists(marker))
                    return new GitRepository(current, marker);
                if (File.Exists(marker) && ReadGitFile(marker, current) is { } linked)
                    return new GitRepository(current, linked);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    /// <summary>A ".git" file (worktrees, submodules) holds "gitdir: &lt;path&gt;", absolute or relative to the work tree.</summary>
    internal static string? ReadGitFile(string file, string root)
    {
        try
        {
            using var reader = new StreamReader(file);
            var line = reader.ReadLine();
            if (line is null || !line.StartsWith("gitdir:", StringComparison.Ordinal))
                return null;
            var target = line["gitdir:".Length..].Trim();
            if (target.Length == 0)
                return null;
            var full = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(root, target));
            return Directory.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>
/// Watches the git directories of recently shown repositories (HEAD, index, FETCH_HEAD: commits, checkouts, staging,
/// fetches) and reports a repository as changed, debounced. Bounded: the least recently used watcher is dropped.
/// Navigator itself runs <c>git status --no-optional-locks</c>, which never writes the index, so its own reads
/// cannot trigger it.
/// </summary>
internal sealed class GitDirectoryWatcher : IDisposable
{
    private const int MaxWatched = 12;
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);

    private readonly Action<GitRepository> _changed;
    private readonly Lock _gate = new();
    private readonly LinkedList<Watch> _watches = [];
    private bool _disposed;

    public GitDirectoryWatcher(Action<GitRepository> changed) => _changed = changed;

    /// <summary>Starts watching <paramref name="repository"/> (or marks it recently used).</summary>
    public void Track(GitRepository repository)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            for (var node = _watches.First; node is not null; node = node.Next)
            {
                if (node.Value.Repository == repository)
                {
                    _watches.Remove(node);
                    _watches.AddFirst(node);
                    return;
                }
            }

            Watch watch;
            try
            {
                watch = new Watch(repository, this);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
            {
                return; // not watchable (e.g. too many inotify watches): status is still refreshed by the panes
            }

            _watches.AddFirst(watch);
            while (_watches.Count > MaxWatched)
            {
                _watches.Last!.Value.Dispose();
                _watches.RemoveLast();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var watch in _watches)
                watch.Dispose();
            _watches.Clear();
        }
    }

    private sealed class Watch : IDisposable
    {
        private readonly FileSystemWatcher _watcher;
        private readonly GitDirectoryWatcher _owner;
        private Timer? _timer;

        public Watch(GitRepository repository, GitDirectoryWatcher owner)
        {
            Repository = repository;
            _owner = owner;
            _watcher = new FileSystemWatcher(repository.GitDirectory)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            };
            _watcher.Changed += OnEvent;
            _watcher.Created += OnEvent;
            _watcher.Renamed += OnEvent;
            _watcher.Deleted += OnEvent;
            _watcher.EnableRaisingEvents = true;
        }

        public GitRepository Repository { get; }

        private void OnEvent(object sender, FileSystemEventArgs e)
        {
            // index.lock, HEAD.lock… come and go during every git command; the final rename onto the real name counts.
            var name = e.Name ?? string.Empty;
            if (name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
                return;
            if (name is not ("HEAD" or "index" or "FETCH_HEAD" or "ORIG_HEAD" or "MERGE_HEAD" or "packed-refs"))
                return;
            var timer = Volatile.Read(ref _timer);
            if (timer is null)
            {
                var created = new Timer(_ => _owner._changed(Repository));
                timer = Interlocked.CompareExchange(ref _timer, created, null) ?? created;
                if (!ReferenceEquals(timer, created))
                    created.Dispose();
            }

            timer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }

        public void Dispose()
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _timer?.Dispose();
        }
    }
}
