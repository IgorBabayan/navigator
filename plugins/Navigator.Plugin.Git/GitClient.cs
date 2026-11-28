using System.Collections.Concurrent;
using System.Globalization;
using Navigator.Plugins;

namespace Navigator.Plugin.Git;

/// <summary>The last commit that touched an item.</summary>
internal sealed record GitCommitInfo(string Hash, string Author, DateTimeOffset Date, string Subject);

/// <summary>What a remote operation does.</summary>
internal enum GitOperation
{
    Pull,
    Push,
    Fetch,
}

/// <summary>
/// Runs the git command line: locates the executable once, keeps the status of each repository for a few seconds (all
/// panes and subfolders of a repository share one <c>git status</c>), walks the history for "last commit" columns and
/// runs pull/push/fetch one at a time per repository. Never prompts on a terminal (<c>GIT_TERMINAL_PROMPT=0</c>);
/// credential helpers with their own window (Git Credential Manager) still work.
/// </summary>
internal sealed class GitClient
{
    /// <summary>A status this young is reused when a folder is opened (not when it changed or is refreshed).</summary>
    private static readonly TimeSpan StatusReuse = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HistoryBudget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RemoteTimeout = TimeSpan.FromMinutes(10);
    private const int HistoryCommitLimit = 2000;
    private const int MaxCachedHistories = 64;

    private static readonly IReadOnlyDictionary<string, string?> Environment = new Dictionary<string, string?>
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_OPTIONAL_LOCKS"] = "0",
        ["GIT_MERGE_AUTOEDIT"] = "no",
        ["GIT_EDITOR"] = "true", // git runs it through its own shell on every OS
        ["GIT_PAGER"] = "cat",
    };

    private readonly Lazy<string?> _executable;
    private readonly ConcurrentDictionary<string, StatusEntry> _statuses = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, GitCommitInfo>> _histories = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _operations = new(StringComparer.Ordinal);

    public GitClient(Func<string?>? locate = null) => _executable = new Lazy<string?>(locate ?? LocateGit);

    /// <summary>Full path of git, or null when it is not installed.</summary>
    public string? Executable => _executable.Value;

    /// <summary>The repository's status, shared and reused for <see cref="StatusReuse"/> unless <paramref name="fresh"/>.</summary>
    public Task<GitRepoStatus> GetStatusAsync(GitRepository repository, bool fresh, CancellationToken cancellationToken)
    {
        var now = System.Environment.TickCount64;
        if (!fresh && _statuses.TryGetValue(repository.Root, out var known) && now - known.Started < StatusReuse.TotalMilliseconds && !known.Task.IsFaulted && !known.Task.IsCanceled)
            return known.Task.WaitAsync(cancellationToken);

        // The status itself is not tied to one pane: a pane navigating away does not cancel what another pane waits for.
        var entry = new StatusEntry(ReadStatusAsync(repository), now);
        _statuses[repository.Root] = entry;
        return entry.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Drops cached results of a repository (after a commit, checkout, pull…).</summary>
    public void Invalidate(GitRepository repository)
    {
        _statuses.TryRemove(repository.Root, out _);
        foreach (var key in _histories.Keys)
        {
            if (key.StartsWith(repository.Root + "\n", StringComparison.Ordinal))
                _histories.TryRemove(key, out _);
        }
    }

    private async Task<GitRepoStatus> ReadStatusAsync(GitRepository repository)
    {
        var result = await RunAsync(repository, ["--no-optional-locks", "status", "--porcelain=v2", "--branch", "-z", "--untracked-files=normal", "--ignore-submodules=dirty"],
            StatusTimeout, CancellationToken.None).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new PluginProcessException(FirstLine(result.Error), result.ExitCode, result.Error);
        return GitRepoStatus.Parse(result.Output);
    }

    /// <summary>
    /// The last commit touching each of <paramref name="names"/> in <paramref name="relativeFolder"/>: one walk of
    /// <c>git log</c> limited to the folder, stopped as soon as every name was found (or after a bounded number of commits
    /// and seconds, for names git never tracked). Cached per folder and HEAD commit.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, GitCommitInfo>> GetLastCommitsAsync(GitRepository repository, string relativeFolder, string? head, IReadOnlyCollection<string> names, CancellationToken cancellationToken)
    {
        var key = repository.Root + "\n" + relativeFolder + "\n" + head;
        if (head is not null && _histories.TryGetValue(key, out var cached))
            return cached;

        var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var wanted = new HashSet<string>(names, comparer);
        var found = new Dictionary<string, GitCommitInfo>(comparer);
        if (head is null || wanted.Count == 0)
            return found;

        var prefix = relativeFolder.Length == 0 ? string.Empty : relativeFolder.TrimEnd('/') + "/";
        var request = Request(repository, ["-c", "core.quotePath=false", "log", "--no-renames", "--name-only", $"-n{HistoryCommitLimit}",
            "--format=%x1e%H%x1f%an%x1f%at%x1f%s", "--", relativeFolder.Length == 0 ? "." : relativeFolder], HistoryBudget);
        GitCommitInfo? commit = null;
        try
        {
            await foreach (var line in PluginProcess.ReadRecordsAsync(request, '\n', cancellationToken).ConfigureAwait(false))
            {
                if (line.Length == 0)
                    continue;
                if (line[0] == '\x1e')
                {
                    commit = ParseCommit(line);
                    continue;
                }

                if (commit is null || !line.StartsWith(prefix, comparer == StringComparer.Ordinal ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                    continue;
                var rest = line.AsSpan(prefix.Length);
                var slash = rest.IndexOf('/');
                var child = (slash < 0 ? rest : rest[..slash]).ToString();
                if (wanted.Remove(child))
                {
                    found[child] = commit;
                    if (wanted.Count == 0)
                        break; // stops (and ends) git: the rest of the history is not needed
                }
            }
        }
        catch (TimeoutException)
        {
            // A long history: what was found within the budget is shown, and not cached as complete.
            return found;
        }
        catch (PluginProcessException)
        {
            // An empty repository or an unreadable history: no commit columns.
        }

        if (_histories.Count >= MaxCachedHistories)
            _histories.Clear();
        _histories[key] = found;
        return found;
    }

    internal static GitCommitInfo? ParseCommit(string line)
    {
        var parts = line[1..].Split('\x1f');
        if (parts.Length < 4 || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            return null;
        return new GitCommitInfo(parts[0], parts[1], DateTimeOffset.FromUnixTimeSeconds(seconds), string.Join('\x1f', parts[3..]).Trim());
    }

    /// <summary>
    /// Pull, push (publishing a branch without upstream to the first remote) or fetch. One remote operation per repository
    /// at a time; returns git's summary line. Throws <see cref="InvalidOperationException"/> with git's message on failure.
    /// </summary>
    public async Task<string> RunOperationAsync(GitRepository repository, GitOperation operation, GitBranchInfo? branch, CancellationToken cancellationToken)
    {
        var gate = _operations.GetOrAdd(repository.Root, static _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException(GitPlugin.Text["Git.Busy"]);
        try
        {
            IReadOnlyList<string> arguments = operation switch
            {
                GitOperation.Pull => ["pull"],
                GitOperation.Fetch => ["fetch", "--prune"],
                _ when branch is { HasUpstream: false, IsDetached: false } => ["push", "--set-upstream", await DefaultRemoteAsync(repository, cancellationToken).ConfigureAwait(false), "HEAD"],
                _ => ["push"],
            };
            var result = await RunAsync(repository, arguments, RemoteTimeout, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
                throw new InvalidOperationException(Summary(result.Error.Length > 0 ? result.Error : result.Output, 1200));
            return FirstLine(result.Output.Length > 0 ? result.Output : result.Error);
        }
        finally
        {
            Invalidate(repository);
            gate.Release();
        }
    }

    private async Task<string> DefaultRemoteAsync(GitRepository repository, CancellationToken cancellationToken)
    {
        var result = await RunAsync(repository, ["remote"], TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        var remotes = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (remotes.Length == 0)
            throw new InvalidOperationException(GitPlugin.Text["Git.NoRemote"]);
        return remotes.Contains("origin", StringComparer.Ordinal) ? "origin" : remotes[0];
    }

    /// <summary>"git version 2.47.1", or null when git is missing or broken.</summary>
    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken)
    {
        if (Executable is not { } git)
            return null;
        try
        {
            var result = await PluginProcess.RunAsync(new PluginProcessRequest(git, ["--version"]) { Timeout = TimeSpan.FromSeconds(10), Environment = Environment }, cancellationToken).ConfigureAwait(false);
            return result.Succeeded ? result.Output.Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            return null;
        }
    }

    private Task<PluginProcessResult> RunAsync(GitRepository repository, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        => PluginProcess.RunAsync(Request(repository, arguments, timeout), cancellationToken);

    private PluginProcessRequest Request(GitRepository repository, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var git = Executable ?? throw new PluginProcessException(GitPlugin.Text["Git.NotInstalled"]);
        return new PluginProcessRequest(git, ["-C", repository.Root, .. arguments])
        {
            WorkingDirectory = repository.Root,
            Environment = Environment,
            Timeout = timeout,
        };
    }

    private static string FirstLine(string text)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            return line;
        return string.Empty;
    }

    private static string Summary(string text, int limit)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= limit ? trimmed : trimmed[..limit] + "…";
    }

    /// <summary>git on PATH; on Windows also Git for Windows' default folder; on macOS only a real git (not the Xcode stub).</summary>
    private static string? LocateGit()
    {
        if (OperatingSystem.IsMacOS())
        {
            foreach (var candidate in new[] { "/opt/homebrew/bin/git", "/usr/local/bin/git" })
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            // /usr/bin/git is a stub that asks to install the developer tools when they are missing.
            return File.Exists("/usr/bin/git") && (Directory.Exists("/Library/Developer/CommandLineTools/usr/bin") || Directory.Exists("/Applications/Xcode.app"))
                ? "/usr/bin/git"
                : null;
        }

        if (PluginProcess.FindExecutable("git") is { } onPath)
            return onPath;
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[] { System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles), System.Environment.GetEnvironmentVariable("LOCALAPPDATA") is { } local ? Path.Combine(local, "Programs") : null })
            {
                if (root is null)
                    continue;
                var candidate = Path.Combine(root, "Git", "cmd", "git.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private sealed record StatusEntry(Task<GitRepoStatus> Task, long Started);
}
