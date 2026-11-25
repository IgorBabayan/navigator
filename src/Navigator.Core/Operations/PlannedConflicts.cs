using Navigator.Core.IO;

namespace Navigator.Core.Operations;

/// <summary>What the user chose for one item in the "Conflicting file name" dialog.</summary>
public enum NameConflictAction
{
    GenerateNewName,
    ReplaceExisting,
    Skip,
}

/// <summary>A top-level item whose name is already taken in the destination folder.</summary>
/// <param name="SourcePath">The incoming item.</param>
/// <param name="DestinationPath">The existing item it would land on.</param>
public sealed record NameConflict(string SourcePath, string DestinationPath, bool SourceIsDirectory, bool DestinationIsDirectory)
{
    /// <summary>A file cannot replace a folder or the other way round; only a new name or skipping is offered then.</summary>
    public bool CanReplace => SourceIsDirectory == DestinationIsDirectory;
}

/// <summary>
/// Copy/move planning done before an operation is queued: finds the incoming items whose names are taken in the
/// destination and turns the user's answers into the transfer list. Items already in the destination folder are left
/// to the engine (copying gives them a "- Copy" name, moving them is a no-op), so that flow does not change.
/// </summary>
public static class NameConflictPlanner
{
    /// <summary>Probes the destination for each source (one stat per item; call off the UI thread).</summary>
    public static IReadOnlyList<NameConflict> Find(IReadOnlyList<string> sources, string destinationDirectory)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var found = new List<NameConflict>();
        foreach (var raw in sources)
        {
            var source = PathUtil.TrimTrailingSeparator(raw);
            if (PathUtil.AreEqual(Path.GetDirectoryName(source), destinationDirectory))
                continue;

            var target = Path.Combine(destinationDirectory, Path.GetFileName(source));
            var existing = ItemInfo.Probe(target);
            if (!existing.Exists)
                continue;
            var incoming = ItemInfo.Probe(source);
            if (incoming.Exists)
                found.Add(new NameConflict(source, target, incoming.IsDirectory, existing.IsDirectory));
        }

        return found;
    }

    /// <summary>
    /// Transfer pairs for the answers: skipped items are dropped, "generate new name" items get a free name (unique
    /// among each other too), replaced items keep theirs. <paramref name="replaced"/> receives the replaced sources.
    /// </summary>
    public static List<TransferPair> Apply(
        IReadOnlyList<string> sources,
        string destinationDirectory,
        IReadOnlyDictionary<string, NameConflictAction> answers,
        ISet<string> replaced)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(answers);
        ArgumentNullException.ThrowIfNull(replaced);

        var taken = new HashSet<string>(PathUtil.Comparer);
        var pairs = new List<TransferPair>(sources.Count);
        foreach (var raw in sources)
        {
            var source = PathUtil.TrimTrailingSeparator(raw);
            if (!answers.TryGetValue(source, out var action))
            {
                pairs.Add(new TransferPair(source, destinationDirectory));
                continue;
            }

            switch (action)
            {
                case NameConflictAction.Skip:
                    break;
                case NameConflictAction.ReplaceExisting:
                    replaced.Add(source);
                    pairs.Add(new TransferPair(source, destinationDirectory));
                    break;
                default:
                    var isDirectory = Directory.Exists(source);
                    var name = PathUtil.GetUniqueName(destinationDirectory, Path.GetFileName(source), isDirectory,
                        candidate => taken.Contains(candidate) || ItemInfo.AnyExists(candidate));
                    taken.Add(Path.Combine(destinationDirectory, name));
                    pairs.Add(new TransferPair(source, destinationDirectory, name));
                    break;
            }
        }

        return pairs;
    }
}

/// <summary>
/// Answers the engine's conflict questions from the plan: inside an item the user chose to replace, existing files are
/// replaced and folders merged without asking again. Anything else (a name taken after the plan was made) goes to
/// <paramref name="fallback"/>, the usual one-conflict-at-a-time dialog.
/// </summary>
public sealed class PlannedConflictInteraction(IReadOnlyCollection<string> replacedSources, IFileOperationInteraction fallback) : IFileOperationInteraction
{
    private readonly IReadOnlyCollection<string> _replaced = replacedSources;
    private readonly IFileOperationInteraction _fallback = fallback;

    public ValueTask<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        foreach (var root in _replaced)
        {
            if (PathUtil.IsSameOrDescendant(root, conflict.SourcePath))
                return ValueTask.FromResult(new ConflictChoice(conflict.CanReplace ? ConflictResolution.Replace : ConflictResolution.KeepBoth));
        }

        return _fallback.ResolveConflictAsync(conflict, cancellationToken);
    }

    public ValueTask<ErrorChoice> ResolveErrorAsync(string path, FileOperationErrorKind kind, string message, bool canRetry, CancellationToken cancellationToken)
        => _fallback.ResolveErrorAsync(path, kind, message, canRetry, cancellationToken);
}
