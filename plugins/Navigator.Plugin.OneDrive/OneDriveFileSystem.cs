using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Navigator.Core.Caching;
using Navigator.Core.Navigation;
using Navigator.Plugins;
using Navigator.Plugins.Cloud;

namespace Navigator.Plugin.OneDrive;

/// <summary>
/// How Microsoft Graph addresses one item: by path in the user's own drive ("me/drive/root:/Documents/a.txt"), or by
/// id in another drive for folders shared with the user ("drives/{drive}/items/{id}"), and by path below such an item.
/// </summary>
internal readonly record struct GraphRef(string Value)
{
    public static GraphRef Root { get; } = new("me/drive/root");

    private bool UsesPath => Value.Contains(":/", StringComparison.Ordinal);

    /// <summary>The child named <paramref name="name"/> (path addressing relative to this item).</summary>
    public GraphRef Child(string name) => new(Value + (UsesPath ? "/" : ":/") + Uri.EscapeDataString(name));

    /// <summary>A sub-resource ("children", "content", "createUploadSession").</summary>
    public string Sub(string resource) => Value + (UsesPath ? ":/" : "/") + resource;

    public static GraphRef Remote(string driveId, string itemId) => new($"drives/{Uri.EscapeDataString(driveId)}/items/{Uri.EscapeDataString(itemId)}");
}

/// <summary>One OneDrive item. <see cref="Ref"/> addresses its content (a shared folder's target for remote items).</summary>
internal sealed record DriveEntry(string Name, bool IsFolder, long Size, DateTime ModifiedUtc, bool IsRemote, GraphRef Ref, GraphRef Self)
{
    public RemoteEntry ToEntry() => new(Name, IsFolder, Size, ModifiedUtc, IsRemote);
}

/// <summary>
/// onedrive://{account}/path: the OneDrive of a signed-in Microsoft account through Microsoft Graph. OneDrive is path
/// addressable, so most operations are one request; folders that others shared and the user added to "My files" are
/// followed into their own drive. Listings are cached for 30 s (cleared by every change Navigator makes). Uploads never
/// replace an existing file (conflictBehavior=fail, checked by the server when the upload completes); files above
/// 4 MiB use a resumable upload session in 10 MiB chunks. Deleting moves items to the OneDrive recycle bin.
/// </summary>
internal sealed class OneDriveFileSystem : IRemoteFileSystem, IRemoteLocationMapper, IDisposable
{
    public const string Scheme = "onedrive";
    private const string Graph = "https://graph.microsoft.com/v1.0/";
    private const string Select = "$select=id,name,size,lastModifiedDateTime,folder,file,package,remoteItem";
    private const int SimpleUploadLimit = 4 * 1024 * 1024;
    private const int ChunkBytes = 32 * 320 * 1024; // 10 MiB, a multiple of 320 KiB as the API requires
    private const int MaxChunkAttempts = 4;
    private static readonly long ListingLifetime = TimeSpan.FromSeconds(30).Ticks;

    private readonly CloudApi _api;
    private readonly CloudAccountStore _accounts;
    private readonly PluginStrings _strings;
    private readonly LruCache<string, (long Stored, IReadOnlyList<DriveEntry> Items)> _listings = new(256);
    private readonly LruCache<string, DriveEntry> _paths = new(8192);

    public OneDriveFileSystem(CloudApi api, CloudAccountStore accounts, PluginStrings strings)
    {
        _api = api;
        _accounts = accounts;
        _strings = strings;
        _accounts.Changed += (_, _) =>
        {
            _listings.Clear();
            _paths.Clear();
        };
    }

    public IReadOnlyList<string> Schemes { get; } = [Scheme];

    // ── IRemoteFileSystem ─────────────────────────────────────

    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(RemoteAddress folder, CancellationToken cancellationToken)
    {
        var item = await ResolveAsync(folder, cancellationToken).ConfigureAwait(false);
        if (!item.IsFolder)
            throw new RemoteFileSystemException(RemoteErrorKind.NotFound, _strings["OneDrive.NotAFolder"]);
        var children = await ListChildrenAsync(folder, item, cancellationToken).ConfigureAwait(false);
        var entries = new RemoteEntry[children.Count];
        for (var i = 0; i < entries.Length; i++)
            entries[i] = children[i].ToEntry();
        return entries;
    }

    public async Task<RemoteEntry?> GetEntryAsync(RemoteAddress address, CancellationToken cancellationToken)
    {
        try
        {
            return (await ResolveAsync(address, cancellationToken).ConfigureAwait(false)).ToEntry();
        }
        catch (RemoteFileSystemException ex) when (ex.Kind == RemoteErrorKind.NotFound)
        {
            return null;
        }
    }

    public async Task<Stream> OpenReadAsync(RemoteAddress file, CancellationToken cancellationToken)
    {
        var item = await ResolveAsync(file, cancellationToken).ConfigureAwait(false);
        if (item.IsFolder)
            throw new RemoteFileSystemException(RemoteErrorKind.Other, _strings["OneDrive.NotAFolder"]);
        // Graph answers with a redirect to a pre-authorized download URL; HttpClient follows it without the bearer token.
        var response = await _api.SendAsync(file.Host, () => new HttpRequestMessage(HttpMethod.Get, Graph + item.Ref.Sub("content")), cancellationToken, transfer: true).ConfigureAwait(false);
        return await ResponseStream.OpenAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task UploadAsync(Stream source, RemoteAddress target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var (parent, name) = await ParentOfNewItemAsync(target, cancellationToken).ConfigureAwait(false);
        var child = parent.Ref.Child(name);
        try
        {
            // Small files: one request. Larger (or unknown-length) sources go through an upload session.
            var head = ArrayPool<byte>.Shared.Rent(SimpleUploadLimit + 1);
            try
            {
                var count = await ReadFullAsync(source, head, SimpleUploadLimit + 1, cancellationToken).ConfigureAwait(false);
                if (count <= SimpleUploadLimit)
                {
                    using var response = await _api.SendAsync(target.Host,
                        () => new HttpRequestMessage(HttpMethod.Put, Graph + child.Sub("content") + "?@microsoft.graph.conflictBehavior=fail") { Content = Octets(head, 0, count) },
                        cancellationToken, transfer: true).ConfigureAwait(false);
                    return;
                }

                await UploadWithSessionAsync(source, head, count, target.Host, child, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(head);
            }
        }
        finally
        {
            Invalidate(target);
        }
    }

    public async Task CreateFolderAsync(RemoteAddress folder, CancellationToken cancellationToken)
    {
        var (parent, name) = await ParentOfNewItemAsync(folder, cancellationToken).ConfigureAwait(false);
        var body = Json(writer =>
        {
            writer.WriteString("name", name);
            writer.WriteStartObject("folder");
            writer.WriteEndObject();
            writer.WriteString("@microsoft.graph.conflictBehavior", "fail");
        });
        using var response = await _api.SendAsync(folder.Host, () => new HttpRequestMessage(HttpMethod.Post, Graph + parent.Ref.Sub("children")) { Content = JsonContent(body) },
            cancellationToken).ConfigureAwait(false);
        Invalidate(folder);
    }

    /// <summary>Moves the item to the OneDrive recycle bin. A shared folder added to "My files" is removed from there only.</summary>
    public async Task DeleteAsync(RemoteAddress address, bool isDirectory, CancellationToken cancellationToken)
    {
        if (address.IsRoot)
            throw new RemoteFileSystemException(RemoteErrorKind.AccessDenied, _strings["OneDrive.Root"]);
        var item = await ResolveAsync(address, cancellationToken).ConfigureAwait(false);
        using var response = await _api.SendAsync(address.Host, () => new HttpRequestMessage(HttpMethod.Delete, Graph + item.Self.Value), cancellationToken).ConfigureAwait(false);
        Invalidate(address);
    }

    /// <summary>Renames in place; the server refuses (409) when the name is taken, which is reported as "already exists".</summary>
    public async Task RenameAsync(RemoteAddress address, string newName, CancellationToken cancellationToken)
    {
        if (address.IsRoot)
            throw new RemoteFileSystemException(RemoteErrorKind.AccessDenied, _strings["OneDrive.Root"]);
        var item = await ResolveAsync(address, cancellationToken).ConfigureAwait(false);
        var body = Json(writer => writer.WriteString("name", newName));
        using var response = await _api.SendAsync(address.Host, () => new HttpRequestMessage(HttpMethod.Patch, Graph + item.Self.Value + "?" + Select) { Content = JsonContent(body) },
            cancellationToken).ConfigureAwait(false);
        Invalidate(address);
    }

    // ── IRemoteLocationMapper (pure lookups, no I/O) ─────────────

    public string? GetLocalPath(RemoteAddress address) => null;

    public string? GetParentAddress(string localPath) => null;

    public string? GetDisplayName(RemoteAddress address)
        => address.IsRoot && _accounts.Find(address.Host) is { } account ? account.Name : null;

    // ── Resolution ────────────────────────────────────────────

    private async Task<DriveEntry> ResolveAsync(RemoteAddress address, CancellationToken cancellationToken)
    {
        if (address.IsRoot)
            return new DriveEntry(string.Empty, true, 0, default, false, GraphRef.Root, GraphRef.Root);
        var key = address.Host + address.Path;
        if (_paths.TryGet(key, out var cached))
            return cached;

        // Fast path: one request by full path. It fails only below a shared folder (another drive): then walk.
        var direct = GraphRef.Root;
        foreach (var segment in address.Path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            direct = direct.Child(segment);
        DriveEntry? item = null;
        try
        {
            item = await GetItemAsync(address.Host, direct, cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteFileSystemException ex) when (ex.Kind == RemoteErrorKind.NotFound)
        {
            var parentAddress = Parent(address);
            if (!parentAddress.IsRoot)
            {
                var parent = await ResolveAsync(parentAddress, cancellationToken).ConfigureAwait(false);
                // Below a shared folder the path continues in the other drive.
                if (parent.IsFolder && parent.Ref.Value.StartsWith("drives/", StringComparison.Ordinal))
                    item = await GetItemAsync(address.Host, parent.Ref.Child(address.Path[(address.Path.LastIndexOf('/') + 1)..]), cancellationToken).ConfigureAwait(false);
            }

            if (item is null)
                throw;
        }

        _paths.Set(key, item);
        return item;
    }

    private async Task<DriveEntry> GetItemAsync(string account, GraphRef reference, CancellationToken cancellationToken)
    {
        using var document = await _api.SendJsonAsync(account, () => new HttpRequestMessage(HttpMethod.Get, Graph + reference.Value + "?" + Select), cancellationToken).ConfigureAwait(false);
        return Parse(document.RootElement, reference);
    }

    private async Task<IReadOnlyList<DriveEntry>> ListChildrenAsync(RemoteAddress folder, DriveEntry item, CancellationToken cancellationToken)
    {
        var key = folder.Host + folder.Path;
        if (_listings.TryGet(key, out var cached) && DateTime.UtcNow.Ticks - cached.Stored < ListingLifetime)
            return cached.Items;

        var result = new List<DriveEntry>();
        string? url = Graph + item.Ref.Sub("children") + "?" + Select;
        while (url is not null)
        {
            var page = url;
            using var document = await _api.SendJsonAsync(folder.Host, () => new HttpRequestMessage(HttpMethod.Get, page), cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.TryGetProperty("value", out var values))
            {
                foreach (var value in values.EnumerateArray())
                {
                    var name = value.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                    result.Add(Parse(value, item.Ref.Child(name)));
                }
            }

            url = root.TryGetProperty("@odata.nextLink", out var next) && next.GetString() is { } link && link.StartsWith(Graph, StringComparison.Ordinal) ? link : null;
        }

        _listings.Set(key, (DateTime.UtcNow.Ticks, result));
        var prefix = folder.IsRoot ? folder.Host : folder.Host + folder.Path;
        foreach (var child in result)
            _paths.Set(prefix + "/" + child.Name, child); // opening a listed folder needs no extra request
        return result;
    }

    internal static DriveEntry Parse(JsonElement value, GraphRef self)
    {
        var name = value.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
        var size = value.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
        var modified = value.TryGetProperty("lastModifiedDateTime", out var t) && t.TryGetDateTime(out var time) ? time.ToUniversalTime() : default;
        if (value.TryGetProperty("remoteItem", out var remote) && remote.ValueKind == JsonValueKind.Object
            && remote.TryGetProperty("id", out var remoteId) && remote.TryGetProperty("parentReference", out var parentReference)
            && parentReference.TryGetProperty("driveId", out var driveId))
        {
            var isFolder = remote.TryGetProperty("folder", out _);
            if (remote.TryGetProperty("size", out var rs) && rs.TryGetInt64(out var remoteBytes))
                size = remoteBytes;
            return new DriveEntry(name, isFolder, size, modified, IsRemote: true, GraphRef.Remote(driveId.GetString()!, remoteId.GetString()!), self);
        }

        // OneNote notebooks (package) are folders in OneDrive's own UI as well.
        var folder = value.TryGetProperty("folder", out _) || value.TryGetProperty("package", out _);
        return new DriveEntry(name, folder, size, modified, IsRemote: false, self, self);
    }

    private async Task<(DriveEntry Parent, string Name)> ParentOfNewItemAsync(RemoteAddress target, CancellationToken cancellationToken)
    {
        if (target.IsRoot)
            throw new RemoteFileSystemException(RemoteErrorKind.AlreadyExists, "/");
        var parent = await ResolveAsync(Parent(target), cancellationToken).ConfigureAwait(false);
        if (!parent.IsFolder)
            throw new RemoteFileSystemException(RemoteErrorKind.NotFound, _strings["OneDrive.NotAFolder"]);
        return (parent, target.Path[(target.Path.LastIndexOf('/') + 1)..]);
    }

    // ── Upload sessions ───────────────────────────────────────

    /// <summary>
    /// Resumable upload: the session is created with conflictBehavior=fail; chunk requests go to the pre-authorized
    /// upload URL without the bearer token (as Graph requires) and are retried on transient failures. A failed or
    /// cancelled upload deletes its session, so no partial file appears.
    /// </summary>
    private async Task UploadWithSessionAsync(Stream source, byte[] head, int headCount, string account, GraphRef child, CancellationToken cancellationToken)
    {
        // Every chunk must state the total size: an unseekable source is spooled to a temporary file first.
        string? spool = null;
        Stream input = source;
        long total;
        if (source.CanSeek)
        {
            total = source.Length - source.Position + headCount;
        }
        else
        {
            spool = Path.Combine(Path.GetTempPath(), "navigator-upload-" + Guid.NewGuid().ToString("N"));
            var file = new FileStream(spool, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            await source.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            total = file.Length + headCount;
            file.Position = 0;
            input = file;
        }

        var body = Json(writer =>
        {
            writer.WriteStartObject("item");
            writer.WriteString("@microsoft.graph.conflictBehavior", "fail");
            writer.WriteEndObject();
        });
        Uri uploadUrl;
        using (var document = await _api.SendJsonAsync(account, () => new HttpRequestMessage(HttpMethod.Post, Graph + child.Sub("createUploadSession")) { Content = JsonContent(body) },
                   cancellationToken).ConfigureAwait(false))
        {
            uploadUrl = new Uri(document.RootElement.GetProperty("uploadUrl").GetString()!);
        }

        var buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        var completed = false;
        try
        {
            long offset = 0;
            var pending = head.AsMemory(0, headCount);
            while (offset < total)
            {
                // Fill one chunk: first from what was already read, then from the source.
                var count = Math.Min(pending.Length, ChunkBytes);
                pending[..count].CopyTo(buffer);
                pending = pending[count..];
                if (count < ChunkBytes)
                    count += await ReadFullAsync(input, buffer.AsMemory(count), ChunkBytes - count, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                    throw new RemoteFileSystemException(RemoteErrorKind.Other, _strings["OneDrive.UploadFailed"]);
                await PutChunkAsync(uploadUrl, buffer, count, offset, total, cancellationToken).ConfigureAwait(false);
                offset += count;
            }

            completed = true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (spool is not null)
                await input.DisposeAsync().ConfigureAwait(false);
            if (!completed)
                await CancelSessionAsync(uploadUrl).ConfigureAwait(false);
        }
    }

    private static async Task PutChunkAsync(Uri uploadUrl, byte[] buffer, int count, long offset, long total, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = Octets(buffer, 0, count) };
            request.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + count - 1, total);
            HttpResponseMessage response;
            try
            {
                response = await CloudHttp.Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < MaxChunkAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException ex)
            {
                throw new RemoteFileSystemException(RemoteErrorKind.ConnectionFailed, ex.Message, ex);
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                    return; // 202 for intermediate chunks, 200/201 when the file is complete
                var status = (int)response.StatusCode;
                if (attempt < MaxChunkAttempts && status is 429 or 500 or 502 or 503 or 504)
                {
                    await Task.Delay(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(attempt * 2), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw CloudApi.ToException(response.StatusCode, text);
            }
        }
    }

    private static async Task CancelSessionAsync(Uri uploadUrl)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await CloudHttp.Client.DeleteAsync(uploadUrl, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // The server drops unfinished sessions on its own after a while.
        }
    }

    // ── Helpers ───────────────────────────────────────────────

    private static RemoteAddress Parent(RemoteAddress address)
    {
        var slash = address.Path.LastIndexOf('/');
        return address.WithPath(slash <= 0 ? "/" : address.Path[..slash]);
    }

    private void Invalidate(RemoteAddress changed)
    {
        var account = changed.Host;
        _listings.RemoveWhere(key => key.StartsWith(account + "/", StringComparison.Ordinal));
        _paths.RemoveWhere(key => key.StartsWith(account + "/", StringComparison.Ordinal));
    }

    private static async Task<int> ReadFullAsync(Stream source, Memory<byte> buffer, int count, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < count)
        {
            var read = await source.ReadAsync(buffer.Slice(total, count - total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
        }

        return total;
    }

    private static ByteArrayContent Octets(byte[] buffer, int offset, int count)
    {
        var content = new ByteArrayContent(buffer, offset, count);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }

    private static byte[] Json(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static ByteArrayContent JsonContent(byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    public void Dispose()
    {
        _listings.Dispose();
        _paths.Dispose();
    }
}
