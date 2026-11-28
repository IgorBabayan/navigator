namespace Navigator.Plugin.Git;

/// <summary>State of one path in <c>git status</c>, in display priority order (higher wins when a folder aggregates).</summary>
internal enum GitFileState
{
    None = 0,
    Ignored,
    Untracked,
    Staged,
    Added,
    Renamed,
    Modified,
    Deleted,
    Conflicted,
}

/// <summary>A child's state; <see cref="IsContained"/> when it comes from something inside a folder.</summary>
internal readonly record struct GitChildState(GitFileState State, bool IsContained);

/// <summary>The branch line of <c>git status --branch</c>.</summary>
internal sealed record GitBranchInfo(string? Branch, string? Oid, string? Upstream, int Ahead, int Behind)
{
    public bool IsDetached => Branch is null;

    public bool HasUpstream => Upstream is not null;

    /// <summary>"main", or the abbreviated commit when HEAD is detached, or "(no commits)" for a fresh repository.</summary>
    public string Label => Branch ?? (Oid is { Length: >= 7 } oid ? oid[..7] : "HEAD");
}

/// <summary>
/// Parsed <c>git status --porcelain=v2 --branch -z</c> of a whole repository: the branch and every path that is not
/// clean, relative to the work tree root with '/' separators.
/// </summary>
internal sealed class GitRepoStatus
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public GitRepoStatus(GitBranchInfo branch, Dictionary<string, GitFileState> paths)
    {
        Branch = branch;
        Paths = paths;
    }

    public GitBranchInfo Branch { get; }

    /// <summary>Changed paths; untracked folders end with '/'.</summary>
    public IReadOnlyDictionary<string, GitFileState> Paths { get; }

    public int ChangedCount => Paths.Count(p => p.Value is not (GitFileState.Ignored or GitFileState.None));

    /// <summary>Parses the NUL-separated porcelain v2 output.</summary>
    public static GitRepoStatus Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        string? branch = null, oid = null, upstream = null;
        int ahead = 0, behind = 0;
        var paths = new Dictionary<string, GitFileState>(PathComparer);
        var records = output.Split('\0');
        for (var i = 0; i < records.Length; i++)
        {
            var record = records[i];
            if (record.Length < 2)
                continue;
            switch (record[0])
            {
                case '#':
                    ParseHeader(record, ref branch, ref oid, ref upstream, ref ahead, ref behind);
                    break;
                case '1':
                    // 1 XY sub mH mI mW hH hI path
                    if (Field(record, 8) is { } changed)
                        Set(paths, changed, StateOf(record[2], record[3]));
                    break;
                case '2':
                    // 2 XY sub mH mI mW hH hI Xscore path, then the original path as the next record
                    if (Field(record, 9) is { } renamed)
                        Set(paths, renamed, GitFileState.Renamed);
                    i++; // skip the original path
                    break;
                case 'u':
                    // u XY sub m1 m2 m3 mW h1 h2 h3 path
                    if (Field(record, 10) is { } conflicted)
                        Set(paths, conflicted, GitFileState.Conflicted);
                    break;
                case '?':
                    Set(paths, record[2..], GitFileState.Untracked);
                    break;
                case '!':
                    Set(paths, record[2..], GitFileState.Ignored);
                    break;
            }
        }

        return new GitRepoStatus(new GitBranchInfo(branch, oid, upstream, ahead, behind), paths);
    }

    private static void ParseHeader(string record, ref string? branch, ref string? oid, ref string? upstream, ref int ahead, ref int behind)
    {
        if (record.StartsWith("# branch.head ", StringComparison.Ordinal))
        {
            var name = record["# branch.head ".Length..];
            branch = name == "(detached)" ? null : name;
        }
        else if (record.StartsWith("# branch.oid ", StringComparison.Ordinal))
        {
            var value = record["# branch.oid ".Length..];
            oid = value == "(initial)" ? null : value;
        }
        else if (record.StartsWith("# branch.upstream ", StringComparison.Ordinal))
        {
            upstream = record["# branch.upstream ".Length..];
        }
        else if (record.StartsWith("# branch.ab ", StringComparison.Ordinal))
        {
            // "# branch.ab +1 -2"
            var parts = record["# branch.ab ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                _ = int.TryParse(parts[0].TrimStart('+'), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ahead);
                _ = int.TryParse(parts[1].TrimStart('-'), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out behind);
            }
        }
    }

    /// <summary>The path: everything after the <paramref name="spaces"/>-th space (paths may contain spaces).</summary>
    private static string? Field(string record, int spaces)
    {
        var at = 0;
        for (var seen = 0; seen < spaces; seen++)
        {
            at = record.IndexOf(' ', at);
            if (at < 0)
                return null;
            at++;
        }

        return at < record.Length ? record[at..] : null;
    }

    private static GitFileState StateOf(char index, char worktree)
    {
        if (index == 'A')
            return GitFileState.Added;
        if (index == 'D' || worktree == 'D')
            return GitFileState.Deleted;
        if (index is 'R' or 'C' || worktree is 'R' or 'C')
            return GitFileState.Renamed;
        if (worktree is 'M' or 'T')
            return GitFileState.Modified;
        if (index is 'M' or 'T')
            return GitFileState.Staged;
        return GitFileState.None;
    }

    private static void Set(Dictionary<string, GitFileState> paths, string path, GitFileState state)
    {
        if (state == GitFileState.None)
            return;
        if (!paths.TryGetValue(path, out var known) || known < state)
            paths[path] = state;
    }

    /// <summary>
    /// The state of each child of <paramref name="relativeFolder"/> ("" for the root, "src/app" otherwise): a file's own
    /// state; for a folder the strongest state of anything inside it (<see cref="GitChildState.IsContained"/>), or its
    /// own state when it is untracked or ignored as a whole. One pass over the changed paths, whatever the folder's size.
    /// When the folder shown lies inside an untracked or ignored folder, <paramref name="wholeFolder"/> is that state.
    /// </summary>
    public Dictionary<string, GitChildState> ChildrenOf(string relativeFolder, out GitFileState wholeFolder)
    {
        ArgumentNullException.ThrowIfNull(relativeFolder);
        wholeFolder = GitFileState.None;
        var prefix = relativeFolder.Length == 0 ? string.Empty : relativeFolder.TrimEnd('/') + "/";
        var comparison = PathComparer == StringComparer.Ordinal ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var children = new Dictionary<string, GitChildState>(PathComparer);
        foreach (var (path, state) in Paths)
        {
            if (prefix.Length > 0 && !path.StartsWith(prefix, comparison))
            {
                // An untracked or ignored folder that contains the folder shown: everything in it shares its state.
                if (path.EndsWith('/') && prefix.StartsWith(path, comparison))
                {
                    wholeFolder = state;
                    return new Dictionary<string, GitChildState>(PathComparer);
                }

                continue;
            }

            var rest = path.AsSpan(prefix.Length);
            var slash = rest.IndexOf('/');
            var child = slash < 0 ? rest.ToString() : rest[..slash].ToString();
            if (child.Length == 0)
                continue;
            var contained = slash >= 0 && slash < rest.Length - 1;
            var aggregated = contained ? Inherited(state) : state;
            if (aggregated == GitFileState.None)
                continue;
            if (!children.TryGetValue(child, out var known) || known.State < aggregated)
                children[child] = new GitChildState(aggregated, contained);
        }

        return children;
    }

    private static GitFileState Inherited(GitFileState inner) => inner switch
    {
        GitFileState.Ignored => GitFileState.None,
        GitFileState.Deleted or GitFileState.Renamed or GitFileState.Added or GitFileState.Staged => GitFileState.Modified,
        _ => inner,
    };
}
