using Navigator.Core.Platform;

namespace Navigator.Platform.Trash;

/// <summary>Used where no trash exists. The UI then offers only permanent deletion, with an explicit warning.</summary>
public sealed class UnsupportedTrash : ITrashService
{
    public bool IsSupported => false;

    public bool CanBrowse => false;

    public Task<TrashItem?> MoveToTrashAsync(string path, CancellationToken cancellationToken = default)
        => throw new PlatformNotSupportedException("This system has no trash.");

    public Task<IReadOnlyList<TrashItem>> ListAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<TrashItem>>([]);

    public Task RestoreAsync(TrashItem item, CancellationToken cancellationToken = default) => throw new PlatformNotSupportedException();

    public Task DeletePermanentlyAsync(TrashItem item, CancellationToken cancellationToken = default) => throw new PlatformNotSupportedException();

    public Task EmptyAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
