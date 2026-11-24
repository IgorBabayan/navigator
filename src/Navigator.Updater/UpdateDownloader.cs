using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Navigator.Core.Hashing;

namespace Navigator.Updater;

public readonly record struct DownloadProgress(long Received, long Total, double BytesPerSecond)
{
    public double Fraction => Total > 0 ? Math.Clamp((double)Received / Total, 0, 1) : 0;
}

/// <summary>
/// Downloads one release asset over HTTPS with resume (HTTP Range), bounded retries and integrity checks:
/// the size must match the manifest and the SHA-256 must match byte for byte, otherwise the file is deleted.
/// </summary>
public sealed class UpdateDownloader(HttpClient http)
{
    public const int MaxAttempts = 4;

    public async Task<string> DownloadAsync(Uri url, ManifestAsset asset, string directory, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (url.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Updates are only downloaded over HTTPS.");

        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, asset.Name);
        var partial = target + ".part";

        if (File.Exists(target) && await MatchesAsync(target, asset, cancellationToken).ConfigureAwait(false))
            return target; // already downloaded and verified earlier

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadOnceAsync(url, asset, partial, progress, cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (Exception ex) when (attempt < MaxAttempts && ex is HttpRequestException or IOException && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
            }
        }

        if (!await MatchesAsync(partial, asset, cancellationToken).ConfigureAwait(false))
        {
            File.Delete(partial);
            throw new InvalidDataException($"{asset.Name} failed integrity verification (SHA-256 mismatch). It was deleted and will not be installed.");
        }

        File.Move(partial, target, overwrite: true);
        return target;
    }

    private async Task DownloadOnceAsync(Uri url, ManifestAsset asset, string partial, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (existing >= asset.Size)
        {
            File.Delete(partial);
            existing = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
            request.Headers.Range = new RangeHeaderValue(existing, null);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("The download was redirected to a non-HTTPS location.");

        var resumed = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!resumed)
            existing = 0;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(partial, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 0, FileOptions.Asynchronous);
        var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            var received = existing;
            var started = Environment.TickCount64;
            var startedBytes = existing;
            var lastReport = 0L;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                received += read;
                if (received > asset.Size)
                    throw new InvalidDataException($"{asset.Name} is larger than the manifest says.");
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

                var now = Environment.TickCount64;
                if (progress is not null && now - lastReport >= 100)
                {
                    var seconds = Math.Max(0.001, (now - started) / 1000.0);
                    progress.Report(new DownloadProgress(received, asset.Size, (received - startedBytes) / seconds));
                    lastReport = now;
                }
            }

            progress?.Report(new DownloadProgress(received, asset.Size, 0));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static async Task<bool> MatchesAsync(string path, ManifestAsset asset, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != asset.Size)
            return false;
        try
        {
            var actual = await FileHasher.ComputeAsync(path, HashKind.Sha256, cancellationToken: cancellationToken).ConfigureAwait(false);
            return FileHasher.DigestsEqual(actual, asset.Sha256);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
