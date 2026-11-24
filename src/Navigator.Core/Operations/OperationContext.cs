namespace Navigator.Core.Operations;

/// <summary>
/// Passed to operation bodies. Wraps cancellation, pausing, progress and the user-decision callbacks,
/// remembering "apply to all" answers for the remainder of the operation.
/// </summary>
public sealed class OperationContext
{
    private readonly FileOperation _operation;
    private ConflictResolution? _conflictForFiles;
    private ConflictResolution? _conflictForFolders;
    private ErrorResolution? _errorForAll;

    internal OperationContext(FileOperation operation, FileOperationOptions options)
    {
        _operation = operation;
        Options = options;
    }

    public FileOperation Operation => _operation;

    public FileOperationOptions Options { get; }

    public CancellationToken CancellationToken => _operation.CancellationToken;

    public int SkippedCount { get; private set; }

    public ValueTask CheckpointAsync()
    {
        CancellationToken.ThrowIfCancellationRequested();
        return _operation.PauseGate.WaitIfPausedAsync(CancellationToken);
    }

    public void AddTotals(long bytes, int items) => _operation.AddTotals(bytes, items);

    public void ReportBytes(long bytes) => _operation.AddProcessedBytes(bytes);

    public void CompleteItem() => _operation.CompleteItem();

    public void SetCurrentItem(string? path) => _operation.SetCurrentItem(path);

    public void RecordTransferred(TransferredItem item) => _operation.AddTransferred(item);

    public void RecordTrashed(Platform.TrashItem item) => _operation.AddTrashed(item);

    public void MarkRunning() => _operation.SetState(FileOperationState.Running);

    public async ValueTask<ConflictResolution> ResolveConflictAsync(ConflictInfo conflict)
    {
        var remembered = conflict.IsFolderMerge ? _conflictForFolders : _conflictForFiles;
        if (remembered is { } r && (r != ConflictResolution.Replace || conflict.CanReplace))
            return r;

        var choice = await _operation.Interaction.ResolveConflictAsync(conflict, CancellationToken).ConfigureAwait(false);
        var resolution = choice.Resolution;
        if (resolution == ConflictResolution.Replace && !conflict.CanReplace)
            resolution = ConflictResolution.KeepBoth;

        if (choice.ApplyToAll)
        {
            if (conflict.IsFolderMerge)
                _conflictForFolders = resolution;
            else
                _conflictForFiles = resolution;
        }

        if (resolution == ConflictResolution.Cancel)
            throw new OperationCanceledException(CancellationToken);

        return resolution;
    }

    /// <summary>
    /// Runs <paramref name="step"/> and lets the user retry, skip or cancel on failure.
    /// Returns false when the step was skipped.
    /// </summary>
    public async ValueTask<bool> RunWithRecoveryAsync(string path, Func<ValueTask> step)
    {
        while (true)
        {
            CancellationToken.ThrowIfCancellationRequested();
            Exception failure;
            try
            {
                await step().ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (OperationStepException ex)
            {
                failure = ex;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                failure = ex;
            }

            var kind = failure is OperationStepException stepException ? stepException.Kind : IoErrorClassifier.Classify(failure);
            var canRetry = IoErrorClassifier.IsRetryable(kind);
            ErrorResolution resolution;
            if (_errorForAll is { } remembered && (remembered != ErrorResolution.Retry || canRetry))
            {
                resolution = remembered;
            }
            else
            {
                var choice = await _operation.Interaction.ResolveErrorAsync(path, kind, failure.Message, canRetry, CancellationToken).ConfigureAwait(false);
                resolution = choice.Resolution == ErrorResolution.Retry && !canRetry ? ErrorResolution.Skip : choice.Resolution;
                if (choice.ApplyToAll && resolution != ErrorResolution.Retry)
                    _errorForAll = resolution;
            }

            switch (resolution)
            {
                case ErrorResolution.Retry:
                    continue;
                case ErrorResolution.Skip:
                    SkippedCount++;
                    _operation.AddError(new FileOperationError(path, kind, failure.Message, Skipped: true));
                    return false;
                default:
                    _operation.AddError(new FileOperationError(path, kind, failure.Message, Skipped: false));
                    throw new OperationCanceledException(CancellationToken);
            }
        }
    }

    public void NoteSkipped() => SkippedCount++;
}

/// <summary>A failure detected by Navigator itself (not by the OS), e.g. moving a folder into itself.</summary>
public sealed class OperationStepException(FileOperationErrorKind kind, string message) : IOException(message)
{
    public FileOperationErrorKind Kind { get; } = kind;
}
