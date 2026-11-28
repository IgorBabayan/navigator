using System.Globalization;
using System.Runtime.CompilerServices;
using Navigator.Plugins;

namespace Navigator.Plugin.Git;

/// <summary>
/// Git inside repositories: the "Git status" column and badges from one shared <c>git status</c> of the repository,
/// then — only while one of them is shown — the "Last commit", "Author" and "Commit date" columns from one bounded walk
/// of the folder's history. The status bar shows the branch with ahead/behind counts and offers Pull, Push and Fetch.
/// </summary>
internal sealed class GitDecorator : IFolderDecorator, IDisposable
{
    public const string StatusColumn = "status";
    public const string CommitColumn = "commit";
    public const string AuthorColumn = "author";
    public const string DateColumn = "date";

    private readonly GitClient _client;
    private readonly GitRepositoryLocator _locator;
    private readonly IPluginContext _context;
    private readonly PluginPreferences _preferences;
    private readonly GitDirectoryWatcher _watcher;

    public GitDecorator(GitClient client, GitRepositoryLocator locator, IPluginContext context, PluginPreferences preferences)
    {
        _client = client;
        _locator = locator;
        _context = context;
        _preferences = preferences;
        _watcher = new GitDirectoryWatcher(repository =>
        {
            _client.Invalidate(repository);
            Changed?.Invoke(this, new FolderDecorationsChangedEventArgs(repository.Root));
        });
        // The badge switch applies to the folders shown right away.
        _preferences.Changed += (_, _) => Changed?.Invoke(this, FolderDecorationsChangedEventArgs.Everywhere);
    }

    public string Id => "git";

    public IReadOnlyList<PluginColumn> Columns =>
    [
        new(StatusColumn, GitPlugin.Text["Git.Column.Status"], 110),
        new(CommitColumn, GitPlugin.Text["Git.Column.Commit"], 260, IsVisibleByDefault: false),
        new(AuthorColumn, GitPlugin.Text["Git.Column.Author"], 140, IsVisibleByDefault: false),
        new(DateColumn, GitPlugin.Text["Git.Column.Date"], 150, IsVisibleByDefault: false),
    ];

    public event EventHandler<FolderDecorationsChangedEventArgs>? Changed;

    public bool AppliesTo(string folderPath) => _client.Executable is not null && _locator.Find(folderPath) is not null;

    public async IAsyncEnumerable<FolderDecoration> DecorateAsync(FolderDecorationRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_locator.Find(request.FolderPath) is not { } repository)
            yield break;
        _watcher.Track(repository);

        GitRepoStatus status;
        try
        {
            status = await _client.GetStatusAsync(repository, fresh: request.Reason != FolderDecorationReason.Opened, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            // Not a usable repository for git ("dubious ownership", corrupt index…): say so once, decorate nothing.
            _context.Log.Info($"git status failed in {repository.Root}: {ex.Message}");
            yield break;
        }

        var relative = RelativeFolder(repository.Root, request.FolderPath);
        var children = status.ChildrenOf(relative, out var wholeFolder);
        var badges = _preferences.GetBool(GitPlugin.BadgesPreference, true);
        var items = new Dictionary<string, ItemDecoration>(StringComparer.Ordinal);
        foreach (var entry in request.Entries)
        {
            var state = wholeFolder != GitFileState.None ? new GitChildState(wholeFolder, false)
                : children.TryGetValue(entry.Name, out var own) ? own
                : default;
            if (state.State == GitFileState.None)
                continue;
            items[entry.Name] = new ItemDecoration
            {
                Badge = badges ? BadgeOf(state.State) : null,
                Cells = new Dictionary<string, string>(1, StringComparer.Ordinal) { [StatusColumn] = TextOf(state, entry.IsDirectory) },
            };
        }

        yield return new FolderDecoration { Status = SegmentOf(status), Items = items };

        if (!request.VisibleColumns.Overlaps([CommitColumn, AuthorColumn, DateColumn]) || wholeFolder is GitFileState.Untracked or GitFileState.Ignored)
            yield break;

        // History: only names git may know (untracked ones never appear in it).
        var names = new List<string>(request.Entries.Count);
        foreach (var entry in request.Entries)
        {
            if (!children.TryGetValue(entry.Name, out var own) || own.State is not (GitFileState.Untracked or GitFileState.Ignored) || own.IsContained)
                names.Add(entry.Name);
        }

        var commits = await _client.GetLastCommitsAsync(repository, relative, status.Branch.Oid, names, cancellationToken).ConfigureAwait(false);
        if (commits.Count == 0)
            yield break;
        var history = new Dictionary<string, ItemDecoration>(commits.Count, StringComparer.Ordinal);
        foreach (var entry in request.Entries)
        {
            if (!commits.TryGetValue(entry.Name, out var commit))
                continue;
            history[entry.Name] = new ItemDecoration
            {
                Cells = new Dictionary<string, string>(3, StringComparer.Ordinal)
                {
                    [CommitColumn] = commit.Subject,
                    [AuthorColumn] = commit.Author,
                    [DateColumn] = commit.Date.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                },
            };
        }

        yield return new FolderDecoration { Items = history };
    }

    public async Task ExecuteAsync(string commandId, string folderPath, CancellationToken cancellationToken)
    {
        if (_locator.Find(folderPath) is not { } repository)
            return;
        var operation = commandId switch
        {
            "pull" => GitOperation.Pull,
            "push" => GitOperation.Push,
            "fetch" => GitOperation.Fetch,
            _ => (GitOperation?)null,
        };
        if (operation is null)
        {
            if (commandId == "refresh")
            {
                _client.Invalidate(repository);
                Changed?.Invoke(this, new FolderDecorationsChangedEventArgs(repository.Root));
            }

            return;
        }

        await RunAsync(repository, operation.Value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs a remote operation with notifications; shared by the status segment and the context menu.</summary>
    public async Task RunAsync(GitRepository repository, GitOperation operation, CancellationToken cancellationToken)
    {
        GitBranchInfo? branch = null;
        try
        {
            branch = (await _client.GetStatusAsync(repository, fresh: false, cancellationToken).ConfigureAwait(false)).Branch;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            // The operation reports the actual problem.
        }

        var name = Path.GetFileName(repository.Root);
        var title = GitPlugin.Text["Git.Operation." + operation];
        _context.Ui.Notify(title, GitPlugin.Text.Format("Git.Running." + operation, name, branch?.Label ?? string.Empty));
        try
        {
            var summary = await Task.Run(() => _client.RunOperationAsync(repository, operation, branch, cancellationToken), cancellationToken).ConfigureAwait(false);
            _context.Ui.Notify(title, summary.Length > 0 ? summary : GitPlugin.Text.Format("Git.Done." + operation, name), PluginNotificationKind.Success);
        }
        finally
        {
            Changed?.Invoke(this, new FolderDecorationsChangedEventArgs(repository.Root));
        }
    }

    internal static string RelativeFolder(string root, string folder)
    {
        var relative = Path.GetRelativePath(root, folder);
        return relative == "." ? string.Empty : relative.Replace('\\', '/');
    }

    private static PluginStatusSegment SegmentOf(GitRepoStatus status)
    {
        var branch = status.Branch;
        var text = branch.Label;
        if (branch.Ahead > 0)
            text += " ↑" + branch.Ahead.ToString(CultureInfo.CurrentCulture);
        if (branch.Behind > 0)
            text += " ↓" + branch.Behind.ToString(CultureInfo.CurrentCulture);

        var changed = status.ChangedCount;
        var tip = branch.IsDetached ? GitPlugin.Text.Format("Git.Tip.Detached", branch.Label)
            : branch.HasUpstream ? GitPlugin.Text.Format("Git.Tip.Tracking", branch.Label, branch.Upstream)
            : GitPlugin.Text.Format("Git.Tip.NoUpstream", branch.Label);
        if (changed > 0)
            tip += "\n" + GitPlugin.Text.Format(changed == 1 ? "Git.Tip.OneChange" : "Git.Tip.Changes", changed);

        var push = branch.HasUpstream || branch.IsDetached
            ? GitPlugin.Text["Git.Operation.Push"] + (branch.Ahead > 0 ? $" ({branch.Ahead.ToString(CultureInfo.CurrentCulture)})" : string.Empty)
            : GitPlugin.Text["Git.Operation.Publish"];
        var pull = GitPlugin.Text["Git.Operation.Pull"] + (branch.Behind > 0 ? $" ({branch.Behind.ToString(CultureInfo.CurrentCulture)})" : string.Empty);
        return new PluginStatusSegment(text, "SourceBranch", tip)
        {
            Commands =
            [
                new PluginCommand("pull", pull, "Download"),
                new PluginCommand("push", push, "Upload"),
                new PluginCommand("fetch", GitPlugin.Text["Git.Operation.Fetch"], "Refresh"),
            ],
        };
    }

    /// <summary>Letters like code editors show them: M(odified), A(dded), U(ntracked), D(eleted), R(enamed), C(onflict).</summary>
    internal static PluginBadge? BadgeOf(GitFileState state) => state switch
    {
        GitFileState.Modified => new PluginBadge("AlphaMBox", "#D29922"),
        GitFileState.Staged => new PluginBadge("AlphaMBox", "#2EA043"),
        GitFileState.Added => new PluginBadge("AlphaABox", "#2EA043"),
        GitFileState.Untracked => new PluginBadge("AlphaUBox", "#2EA043"),
        GitFileState.Renamed => new PluginBadge("AlphaRBox", "#2EA043"),
        GitFileState.Deleted => new PluginBadge("AlphaDBox", "#DA3633"),
        GitFileState.Conflicted => new PluginBadge("AlphaCBox", "#DA3633"),
        _ => null,
    };

    internal static string TextOf(GitChildState state, bool isDirectory)
        => GitPlugin.Text[(isDirectory && state.IsContained ? "Git.Status.Contains." : "Git.Status.") + state.State];

    public void Dispose() => _watcher.Dispose();
}
