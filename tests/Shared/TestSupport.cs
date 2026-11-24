using System.Net;
using Navigator.Core.IO;
using Navigator.Core.Platform;

namespace Navigator.Tests;

/// <summary>A unique folder under the system temp directory, deleted on dispose. Tests never touch user files.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "navigator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string CreateFile(string relative, string content = "data", DateTime? modifiedUtc = null)
    {
        var full = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        if (modifiedUtc is { } time)
            File.SetLastWriteTimeUtc(full, time);
        return full;
    }

    public string CreateDirectory(string relative)
    {
        var full = Combine(relative);
        Directory.CreateDirectory(full);
        return full;
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Forces the cross-volume code path (copy + verify + delete) or the same-volume path (rename).</summary>
public sealed class FakeVolumeLocator(bool sameVolume) : IVolumeLocator
{
    public string GetVolumeRoot(string path) => sameVolume ? "/" : path;

    public bool AreOnSameVolume(string first, string second) => sameVolume;
}

/// <summary>In-memory trash: moves items into a folder so undo/restore can be tested anywhere.</summary>
public sealed class FolderTrash(string folder) : ITrashService
{
    private readonly List<TrashItem> _items = [];

    public bool IsSupported => true;

    public bool CanBrowse => true;

    public Task<TrashItem?> MoveToTrashAsync(string path, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(folder);
        var storage = Path.Combine(folder, Guid.NewGuid().ToString("N"));
        var isDirectory = Directory.Exists(path);
        if (isDirectory)
            Directory.Move(path, storage);
        else
            File.Move(path, storage);
        var item = new TrashItem(storage, Path.GetFileName(path), path, DateTimeOffset.Now, 0, isDirectory, storage);
        _items.Add(item);
        return Task.FromResult<TrashItem?>(item);
    }

    public Task<IReadOnlyList<TrashItem>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TrashItem>>([.. _items]);

    public Task RestoreAsync(TrashItem item, CancellationToken cancellationToken = default)
    {
        if (File.Exists(item.OriginalPath) || Directory.Exists(item.OriginalPath))
            throw new IOException("exists");
        if (item.IsDirectory)
            Directory.Move(item.StoragePath, item.OriginalPath);
        else
            File.Move(item.StoragePath, item.OriginalPath);
        _items.Remove(item);
        return Task.CompletedTask;
    }

    public Task DeletePermanentlyAsync(TrashItem item, CancellationToken cancellationToken = default)
    {
        if (item.IsDirectory)
            Directory.Delete(item.StoragePath, true);
        else
            File.Delete(item.StoragePath);
        _items.Remove(item);
        return Task.CompletedTask;
    }

    public Task EmptyAsync(CancellationToken cancellationToken = default)
    {
        foreach (var item in _items.ToList())
            DeletePermanentlyAsync(item, cancellationToken);
        return Task.CompletedTask;
    }
}

/// <summary>Serves canned HTTP responses by URL (no network access in tests).</summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public List<string> Requests { get; } = [];

    public void Add(string url, byte[] body, HttpStatusCode status = HttpStatusCode.OK)
        => _routes[url] = _ => new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };

    public void Add(string url, Func<HttpRequestMessage, HttpResponseMessage> responder) => _routes[url] = responder;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        Requests.Add(url);
        var response = _routes.TryGetValue(url, out var responder) ? responder(request) : new HttpResponseMessage(HttpStatusCode.NotFound);
        response.RequestMessage = request;
        return Task.FromResult(response);
    }
}
