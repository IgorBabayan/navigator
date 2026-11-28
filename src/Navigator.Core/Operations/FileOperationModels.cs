namespace Navigator.Core.Operations;

public enum FileOperationKind
{
    Copy,
    Move,
    Delete,
    Recycle,
    Restore,
    CreateArchive,
    ExtractArchive,
}

public enum FileOperationState
{
    Queued,
    Preparing,
    Running,
    Paused,
    Completed,
    CompletedWithErrors,
    Cancelled,
    Failed,
}

public enum ConflictResolution
{
    /// <summary>Overwrite the destination file. For folder-onto-folder conflicts this means "merge".</summary>
    Replace,
    KeepBoth,
    Skip,
    Cancel,
}

public enum ErrorResolution
{
    Retry,
    Skip,
    Cancel,
}

public enum FileOperationErrorKind
{
    Unknown,
    NotFound,
    AccessDenied,
    FileInUse,
    DiskFull,
    PathTooLong,
    InvalidName,
    DestinationInsideSource,
    AlreadyExists,
    DeviceNotReady,
    NetworkError,
    NotSupported,
    VerificationFailed,
}

/// <summary>One unit of a copy/move: <see cref="Source"/> goes into <see cref="DestinationDirectory"/>, optionally renamed.</summary>
public sealed record TransferPair(string Source, string DestinationDirectory, string? TargetName = null);

/// <summary>Describes a name collision presented to the user.</summary>
public sealed record ConflictInfo(
    string SourcePath,
    string DestinationPath,
    bool SourceIsDirectory,
    bool DestinationIsDirectory,
    long SourceLength,
    long DestinationLength,
    DateTime SourceLastWriteUtc,
    DateTime DestinationLastWriteUtc)
{
    /// <summary>A file cannot replace a folder (or vice versa); only keep-both / skip are offered.</summary>
    public bool CanReplace => SourceIsDirectory == DestinationIsDirectory;

    public bool IsFolderMerge => SourceIsDirectory && DestinationIsDirectory;
}

public readonly record struct ConflictChoice(ConflictResolution Resolution, bool ApplyToAll = false);

public readonly record struct ErrorChoice(ErrorResolution Resolution, bool ApplyToAll = false);

public sealed record FileOperationError(string Path, FileOperationErrorKind Kind, string Message, bool Skipped);

/// <summary>Point-in-time progress. Computed on demand so the UI can poll at its own frame rate.</summary>
public readonly record struct ProgressSnapshot(
    long TotalBytes,
    long ProcessedBytes,
    int TotalItems,
    int ProcessedItems,
    string? CurrentItem,
    double BytesPerSecond,
    TimeSpan? Remaining)
{
    public double Fraction => TotalBytes > 0
        ? Math.Clamp((double)ProcessedBytes / TotalBytes, 0, 1)
        : TotalItems > 0 ? Math.Clamp((double)ProcessedItems / TotalItems, 0, 1) : 0;
}

/// <summary>
/// User decisions during an operation. Implemented by the UI (dialogs) and by policies in tests.
/// Calls may arrive on any thread; implementations marshal to the UI thread themselves.
/// </summary>
public interface IFileOperationInteraction
{
    ValueTask<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict, CancellationToken cancellationToken);

    ValueTask<ErrorChoice> ResolveErrorAsync(string path, FileOperationErrorKind kind, string message, bool canRetry, CancellationToken cancellationToken);
}

/// <summary>Non-interactive policy (tests, scripted operations, undo/redo).</summary>
public sealed class PolicyInteraction(ConflictResolution conflict, ErrorResolution error) : IFileOperationInteraction
{
    private readonly ConflictResolution _conflict = conflict;
    private readonly ErrorResolution _error = error;

    public static PolicyInteraction SkipConflictsAndErrors { get; } = new(ConflictResolution.Skip, ErrorResolution.Skip);

    public ValueTask<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict, CancellationToken cancellationToken)
        => ValueTask.FromResult(new ConflictChoice(
            _conflict == ConflictResolution.Replace && !conflict.CanReplace ? ConflictResolution.KeepBoth : _conflict, ApplyToAll: true));

    public ValueTask<ErrorChoice> ResolveErrorAsync(string path, FileOperationErrorKind kind, string message, bool canRetry, CancellationToken cancellationToken)
        => ValueTask.FromResult(new ErrorChoice(_error == ErrorResolution.Retry && !canRetry ? ErrorResolution.Skip : _error, ApplyToAll: true));
}

/// <summary>Result details used by the undo system and by tag/path bookkeeping after an operation.</summary>
public sealed record TransferredItem(string SourcePath, string DestinationPath, bool IsDirectory, bool WasMoved);

public sealed class FileOperationOptions
{
    /// <summary>Flush copied data to stable storage before deleting the source of a cross-volume move.</summary>
    public bool FlushBeforeDeletingSource { get; init; } = true;

    /// <summary>Compare SHA-256 of source and destination before deleting the source of a cross-volume move.</summary>
    public bool VerifyMovedFileContents { get; init; }

    public string CopyNameSuffix { get; init; } = "Copy";

    public int CopyBufferSize { get; init; } = 1024 * 1024;
}

/// <summary>Result of <see cref="FileOperationService.RenameMany"/>: what was renamed, and the failure that stopped it (if any).</summary>
public sealed record BatchRenameOutcome(IReadOnlyList<RenameRecord> Done, Exception? Failure);
