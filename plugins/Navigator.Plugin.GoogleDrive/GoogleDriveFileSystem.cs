using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Navigator.Core.Caching;
using Navigator.Core.Navigation;
using Navigator.Plugins;
using Navigator.Plugins.Cloud;

namespace Navigator.Plugin.GoogleDrive;

/// <summary>One Drive item as Navigator sees it.</summary>
/// <param name="Segment">The item's unique name within its folder listing (see <see cref="DriveNames"/>).</param>
internal sealed record DriveItem(string Id, string Name, string Segment, string MimeType, long Size, DateTime ModifiedUtc, string? TargetId = null, string? TargetMimeType = null)
{
    public static DriveItem Root { get; } = new("root", string.Empty, string.Empty, DriveNames.FolderMime, 0, default);

    public bool IsShortcut => MimeType == DriveNames.ShortcutMime;

    /// <summary>The type of what the item stands for (a shortcut's target).</summary>
    public string EffectiveMime => TargetMimeType ?? MimeType;

    public bool IsFolder => EffectiveMime == DriveNames.FolderMime;

    /// <summary>The id whose children or content are read (a shortcut's target).</summary>
    public string ContentId => TargetId ?? Id;

    public RemoteEntry ToEntry() => new(Segment, IsFolder, Size, ModifiedUtc, IsShortcut);
}

/// <summary>
/// gdrive://{account}/path: "My Drive" of a signed-in account (Drive API v3). Drive identifies items by id, not by path,
/// so paths are resolved segment by segment through short-lived caches (folder listings 30 s, resolved paths 5 min;
/// every change Navigator makes clears them). Deleting moves items to the Drive trash; uploads are resumable (8 MiB
/// chunks) and never replace an existing item; shortcuts are shown as links and deleting one removes only the shortcut.
/// </summary>
internal sealed class GoogleDriveFileSystem : IRemoteFileSystem, IRemoteLocationMapper, IDisposable
{
    public const string Scheme = "gdrive";
    private const string Api = "https://www.googleapis.com/drive/v3/";
    private const string UploadApi = "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&supportsAllDrives=true&fields=id";
    private const string ItemFields = "id,name,mimeType,size,modifiedTime,shortcutDetails(targetId,targetMimeType)";
    private const string AllDrives = "&supportsAllDrives=true&includeItemsFromAllDrives=true";
    private const int UploadChunkBytes = 32 * 256 * 1024; // 8 MiB, a multiple of 256 KiB as the API requires
    private static readonly long ListingLifetime = TimeSpan.FromSeconds(30).Ticks;
    private static readonly long PathLifetime = TimeSpan.FromMinutes(5).Ticks;

    private readonly CloudApi _api;
    private readonly CloudAccountStore _accounts;
    private readonly PluginStrings _strings;
    private readonly LruCache<string, Cached<IReadOnlyList<DriveItem>>> _listings = new(256);
    private readonly LruCache<string, Cached<DriveItem>> _paths = new(8192);

    public GoogleDriveFileSystem(CloudApi api, CloudAccountStore accounts, PluginStrings strings)
    {
        _api = api;
        _accounts = accounts;
        _strings = strings;
        _accounts.Changed += (_, _) => ClearCaches(null);
    }

    public IReadOnlyList<string> Schemes { get; } = [Scheme];

    // ── IRemoteFileSystem ─────────────────────────────────────

    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(RemoteAddress folder, CancellationToken cancellationToken)
    {
        var item = await ResolveAsync(folder, cancellationToken).ConfigureAwait(false);
        if (!item.IsFolder)
            throw new RemoteFileSystemException(RemoteErrorKind.NotFound, _strings["GoogleDrive.NotAFolder"]);
        var children = await ListChildrenAsync(folder.Host, item.ContentId, cancellationToken).ConfigureAwait(false);
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
            throw new RemoteFileSystemException(RemoteErrorKind.Other, _strings["GoogleDrive.NotAFolder"]);
        if (DriveNames.IsContentless(item.EffectiveMime))
            throw new RemoteFileSystemException(RemoteErrorKind.Other, _strings["GoogleDrive.NotDownloadable"]);

        var url = DriveNames.ExportFormat(item.EffectiveMime) is { } export
            ? $"{Api}files/{Uri.EscapeDataString(item.ContentId)}/export?mimeType={Uri.EscapeDataString(export.Mime)}"
            : $"{Api}files/{Uri.EscapeDataString(item.ContentId)}?alt=media&supportsAllDrives=true";
        var response = await _api.SendAsync(file.Host, () => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken, transfer: true).ConfigureAwait(false);
        return await ResponseStream.OpenAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task UploadAsync(Stream source, RemoteAddress target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var (parent, name) = await PrepareNewItemAsync(target, cancellationToken).ConfigureAwait(false);

        // 1. Open a resumable session. Nothing appears in Drive until the last chunk arrives, so a cancelled upload
        //    leaves no partial file behind.
        var metadata = Json(writer =>
        {
            writer.WriteString("name", DriveNames.ToDriveName(name));
            writer.WriteStartArray("parents");
            writer.WriteStringValue(parent.ContentId);
            writer.WriteEndArray();
        });
        Uri session;
        using (var start = await _api.SendAsync(target.Host, () =>
               {
                   var request = new HttpRequestMessage(HttpMethod.Post, UploadApi) { Content = JsonContent(metadata) };
                   request.Headers.TryAddWithoutValidation("X-Upload-Content-Type", "application/octet-stream");
                   return request;
               }, cancellationToken).ConfigureAwait(false))
        {
            session = start.Headers.Location ?? throw new RemoteFileSystemException(RemoteErrorKind.Other, "Google Drive did not open an upload session.");
        }

        // 2. Send the content in chunks; the total size is announced with the last one (streams need not be seekable).
        var buffer = ArrayPool<byte>.Shared.Rent(UploadChunkBytes);
        try
        {
            long offset = 0;
            while (true)
            {
                var count = await ReadFullAsync(source, buffer, UploadChunkBytes, cancellationToken).ConfigureAwait(false);
                var last = count < UploadChunkBytes;
                var from = offset;
                var range = count == 0
                    ? new ContentRangeHeaderValue(offset)
                    : last ? new ContentRangeHeaderValue(from, from + count - 1, from + count) : new ContentRangeHeaderValue(from, from + count - 1);
                using var response = await _api.SendAsync(target.Host, () =>
                {
                    var content = new ByteArrayContent(buffer, 0, count);
                    content.Headers.ContentRange = range;
                    return new HttpRequestMessage(HttpMethod.Put, session) { Content = content };
                }, cancellationToken, transfer: true, accept: static status => (int)status == 308).ConfigureAwait(false);
                offset += count;
                if (last)
                    break;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            Invalidate(target.Host, parent.ContentId);
        }
    }

    public async Task CreateFolderAsync(RemoteAddress folder, CancellationToken cancellationToken)
    {
        var (parent, name) = await PrepareNewItemAsync(folder, cancellationToken).ConfigureAwait(false);
        var body = Json(writer =>
        {
            writer.WriteString("name", DriveNames.ToDriveName(name));
            writer.WriteString("mimeType", DriveNames.FolderMime);
            writer.WriteStartArray("parents");
            writer.WriteStringValue(parent.ContentId);
            writer.WriteEndArray();
        });
        using var response = await _api.SendAsync(folder.Host, () => new HttpRequestMessage(HttpMethod.Post, Api + "files?supportsAllDrives=true&fields=id") { Content = JsonContent(body) },
            cancellationToken).ConfigureAwait(false);
        Invalidate(folder.Host, parent.ContentId);
    }

    /// <summary>Moves the item to the Drive trash (recoverable there for 30 days). A shortcut is trashed itself, never its target.</summary>
    public async Task DeleteAsync(RemoteAddress address, bool isDirectory, CancellationToken cancellationToken)
    {
        if (address.IsRoot)
            throw new RemoteFileSystemException(RemoteErrorKind.AccessDenied, _strings["GoogleDrive.Root"]);
        var item = await ResolveAsync(address, cancellationToken).ConfigureAwait(false);
        var body = Json(writer => writer.WriteBoolean("trashed", true));
        using var response = await _api.SendAsync(address.Host, () => new HttpRequestMessage(HttpMethod.Patch, $"{Api}files/{Uri.EscapeDataString(item.Id)}?supportsAllDrives=true&fields=id")
        {
            Content = JsonContent(body),
        }, cancellationToken).ConfigureAwait(false);
        ClearCaches(address.Host);
    }

    public async Task RenameAsync(RemoteAddress address, string newName, CancellationToken cancellationToken)
    {
        if (address.IsRoot)
            throw new RemoteFileSystemException(RemoteErrorKind.AccessDenied, _strings["GoogleDrive.Root"]);
        var item = await ResolveAsync(address, cancellationToken).ConfigureAwait(false);
        var parent = await ResolveAsync(Parent(address), cancellationToken).ConfigureAwait(false);
        if (await FindChildAsync(address.Host, parent.ContentId, newName, fresh: true, cancellationToken).ConfigureAwait(false) is { } existing && existing.Id != item.Id)
            throw new RemoteFileSystemException(RemoteErrorKind.AlreadyExists, newName);

        var body = Json(writer => writer.WriteString("name", DriveNames.ToDriveName(newName, item.EffectiveMime)));
        using var response = await _api.SendAsync(address.Host, () => new HttpRequestMessage(HttpMethod.Patch, $"{Api}files/{Uri.EscapeDataString(item.Id)}?supportsAllDrives=true&fields=id")
        {
            Content = JsonContent(body),
        }, cancellationToken).ConfigureAwait(false);
        ClearCaches(address.Host);
    }

    // ── IRemoteLocationMapper (pure lookups, no I/O) ─────────────

    public string? GetLocalPath(RemoteAddress address) => null;

    public string? GetParentAddress(string localPath) => null;

    /// <summary>The account's e-mail address for its root ("My Drive"), so tabs and breadcrumbs show whose drive it is.</summary>
    public string? GetDisplayName(RemoteAddress address)
        => address.IsRoot && _accounts.Find(address.Host) is { } account ? account.Name : null;

    // ── Path resolution ───────────────────────────────────────

    private async Task<DriveItem> ResolveAsync(RemoteAddress address, CancellationToken cancellationToken)
    {
        if (address.IsRoot)
            return DriveItem.Root;
        var key = address.Host + address.Path;
        if (_paths.TryGet(key, out var cached) && cached.IsFresh(PathLifetime))
            return cached.Value;

        var parent = await ResolveAsync(Parent(address), cancellationToken).ConfigureAwait(false);
        if (!parent.IsFolder)
            throw new RemoteFileSystemException(RemoteErrorKind.NotFound, address.Path);
        var name = address.Path[(address.Path.LastIndexOf('/') + 1)..];
        var item = await FindChildAsync(address.Host, parent.ContentId, name, fresh: false, cancellationToken).ConfigureAwait(false)
                   ?? throw new RemoteFileSystemException(RemoteErrorKind.NotFound, address.Path);
        _paths.Set(key, new Cached<DriveItem>(item));
        return item;
    }

    /// <summary>
    /// The child whose segment is <paramref name="segment"/>. Uses a cached listing when there is one; otherwise asks
    /// Drive for the items with that name (one small query instead of listing a large folder) and falls back to the full
    /// listing only for numbered duplicates.
    /// </summary>
    private async Task<DriveItem?> FindChildAsync(string account, string folderId, string segment, bool fresh, CancellationToken cancellationToken)
    {
        if (!fresh && _listings.TryGet(account + "/" + folderId, out var listing) && listing.IsFresh(ListingLifetime))
            return listing.Value.FirstOrDefault(i => i.Segment == segment);

        var name = DriveNames.ToDriveName(segment);
        var condition = $"name = {DriveNames.QueryLiteral(name)}";
        var dot = name.LastIndexOf('.');
        if (dot > 0)
            condition = $"({condition} or name = {DriveNames.QueryLiteral(name[..dot])})"; // a Google Doc shown as "Name.docx"
        var candidates = await QueryAsync(account, $"{DriveNames.QueryLiteral(folderId)} in parents and trashed = false and {condition}", cancellationToken).ConfigureAwait(false);
        if (candidates.FirstOrDefault(i => i.Segment == segment) is { } match)
            return match;

        var all = await ListChildrenAsync(account, folderId, cancellationToken, fresh: true).ConfigureAwait(false);
        return all.FirstOrDefault(i => i.Segment == segment);
    }

    private async Task<IReadOnlyList<DriveItem>> ListChildrenAsync(string account, string folderId, CancellationToken cancellationToken, bool fresh = false)
    {
        var key = account + "/" + folderId;
        if (!fresh && _listings.TryGet(key, out var cached) && cached.IsFresh(ListingLifetime))
            return cached.Value;
        var items = await QueryAsync(account, $"{DriveNames.QueryLiteral(folderId)} in parents and trashed = false", cancellationToken).ConfigureAwait(false);
        _listings.Set(key, new Cached<IReadOnlyList<DriveItem>>(items));
        return items;
    }

    /// <summary>Runs a files.list query (all pages, creation order) and assigns unique segments.</summary>
    private async Task<IReadOnlyList<DriveItem>> QueryAsync(string account, string query, CancellationToken cancellationToken)
    {
        var raw = new List<DriveItem>();
        string? pageToken = null;
        do
        {
            var url = $"{Api}files?q={Uri.EscapeDataString(query)}&fields={Uri.EscapeDataString("nextPageToken,files(" + ItemFields + ")")}&pageSize=1000&orderBy=createdTime{AllDrives}"
                      + (pageToken is null ? string.Empty : "&pageToken=" + Uri.EscapeDataString(pageToken));
            using var document = await _api.SendJsonAsync(account, () => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.TryGetProperty("files", out var files))
            {
                foreach (var file in files.EnumerateArray())
                    raw.Add(Parse(file));
            }

            pageToken = root.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
        }
        while (pageToken is not null);

        var segments = DriveNames.AssignSegments([.. raw.Select(i => (i.Name, i.EffectiveMime))]);
        var result = new DriveItem[raw.Count];
        for (var i = 0; i < result.Length; i++)
            result[i] = raw[i] with { Segment = segments[i] };
        return result;
    }

    internal static DriveItem Parse(JsonElement file)
    {
        var id = file.GetProperty("id").GetString()!;
        var name = file.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
        var mime = file.TryGetProperty("mimeType", out var m) ? m.GetString() ?? string.Empty : string.Empty;
        var size = file.TryGetProperty("size", out var s) && long.TryParse(s.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) ? bytes : 0;
        var modified = file.TryGetProperty("modifiedTime", out var t) && DateTime.TryParse(t.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time)
            ? time
            : default;
        string? targetId = null, targetMime = null;
        if (mime == DriveNames.ShortcutMime && file.TryGetProperty("shortcutDetails", out var shortcut))
        {
            targetId = shortcut.TryGetProperty("targetId", out var ti) ? ti.GetString() : null;
            targetMime = shortcut.TryGetProperty("targetMimeType", out var tm) ? tm.GetString() : null;
        }

        return new DriveItem(id, name, name, mime, size, modified, targetId, targetMime);
    }

    /// <summary>The parent folder of a new item, after checking that its name is free (Drive itself would allow duplicates).</summary>
    private async Task<(DriveItem Parent, string Name)> PrepareNewItemAsync(RemoteAddress target, CancellationToken cancellationToken)
    {
        if (target.IsRoot)
            throw new RemoteFileSystemException(RemoteErrorKind.AlreadyExists, "/");
        var parent = await ResolveAsync(Parent(target), cancellationToken).ConfigureAwait(false);
        if (!parent.IsFolder)
            throw new RemoteFileSystemException(RemoteErrorKind.NotFound, _strings["GoogleDrive.NotAFolder"]);
        var name = target.Path[(target.Path.LastIndexOf('/') + 1)..];
        if (await FindChildAsync(target.Host, parent.ContentId, name, fresh: true, cancellationToken).ConfigureAwait(false) is not null)
            throw new RemoteFileSystemException(RemoteErrorKind.AlreadyExists, name);
        return (parent, name);
    }

    private static RemoteAddress Parent(RemoteAddress address)
    {
        var slash = address.Path.LastIndexOf('/');
        return address.WithPath(slash <= 0 ? "/" : address.Path[..slash]);
    }

    private void Invalidate(string account, string folderId)
    {
        _listings.Remove(account + "/" + folderId);
        _paths.RemoveWhere(key => key.StartsWith(account + "/", StringComparison.Ordinal));
    }

    private void ClearCaches(string? account)
    {
        if (account is null)
        {
            _listings.Clear();
            _paths.Clear();
            return;
        }

        _listings.RemoveWhere(key => key.StartsWith(account + "/", StringComparison.Ordinal));
        _paths.RemoveWhere(key => key.StartsWith(account + "/", StringComparison.Ordinal));
    }

    private static async Task<int> ReadFullAsync(Stream source, byte[] buffer, int count, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < count)
        {
            var read = await source.ReadAsync(buffer.AsMemory(total, count - total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
        }

        return total;
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

    /// <summary>A cached value and when it was stored.</summary>
    private sealed class Cached<T>(T value)
    {
        private readonly long _stored = DateTime.UtcNow.Ticks;

        public T Value { get; } = value;

        public bool IsFresh(long lifetimeTicks) => DateTime.UtcNow.Ticks - _stored < lifetimeTicks;
    }
}
