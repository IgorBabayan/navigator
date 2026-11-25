using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Navigator.Plugins.Cloud;

/// <summary>
/// One HTTP client for every cloud plugin (the contract assembly is shared by all plugins): one connection pool, HTTP/2
/// where the service offers it, compressed responses, DNS changes picked up every few minutes. Created on first use, so
/// it costs nothing until a cloud account is opened.
/// </summary>
public static class CloudHttp
{
    private static readonly Lazy<HttpClient> Shared = new(() =>
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            ConnectTimeout = TimeSpan.FromSeconds(20),
            EnableMultipleHttp2Connections = true,
        };
        var client = new HttpClient(handler)
        {
            // Transfers can take long; each metadata call has its own timeout (CloudApi).
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        var version = typeof(CloudHttp).Assembly.GetName().Version;
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Navigator", version?.ToString(3) ?? "0.0.0"));
        return client;
    });

    public static HttpClient Client => Shared.Value;
}

/// <summary>
/// Calls a cloud REST API for one account: adds the bearer token, refreshes it once on 401, retries throttling and
/// transient server errors (429, 5xx, honoring Retry-After) with backoff, and turns failures into
/// <see cref="RemoteFileSystemException"/> so panes show the usual messages.
/// </summary>
public sealed class CloudApi(CloudAuthenticator authenticator)
{
    private const int MaxAttempts = 4;
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(60);

    public CloudAuthenticator Authenticator => authenticator;

    /// <summary>
    /// Sends a request built by <paramref name="create"/> (called again for every retry, so request bodies must be
    /// re-creatable). Returns the successful response (the caller disposes it); <paramref name="accept"/> marks other
    /// status codes as expected answers (e.g. 308 of a resumable upload). <paramref name="transfer"/> requests
    /// (downloads, uploads) have no overall timeout.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(string accountId, Func<HttpRequestMessage> create, CancellationToken cancellationToken, bool transfer = false, Func<HttpStatusCode, bool>? accept = null)
    {
        ArgumentNullException.ThrowIfNull(create);
        var refreshed = false;
        for (var attempt = 1; ; attempt++)
        {
            var token = await authenticator.GetAccessTokenAsync(accountId, forceRefresh: false, cancellationToken).ConfigureAwait(false);
            using var request = create();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var timeout = transfer ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout?.CancelAfter(MetadataTimeout);
            HttpResponseMessage response;
            try
            {
                response = await CloudHttp.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout?.Token ?? cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                if (attempt < MaxAttempts && ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded)
                {
                    await DelayAsync(attempt, null, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new RemoteFileSystemException(RemoteErrorKind.ConnectionFailed, ex.Message, ex);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new RemoteFileSystemException(RemoteErrorKind.ConnectionFailed, "The service did not answer in time.", ex);
            }

            if (response.IsSuccessStatusCode || accept?.Invoke(response.StatusCode) == true)
                return response;

            using (response)
            {
                var body = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Unauthorized && !refreshed)
                {
                    refreshed = true;
                    await authenticator.GetAccessTokenAsync(accountId, forceRefresh: true, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (attempt < MaxAttempts && IsTransient(response, body))
                {
                    await DelayAsync(attempt, response.Headers.RetryAfter, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw ToException(response.StatusCode, body);
            }
        }
    }

    /// <summary>Sends a request and parses the JSON answer.</summary>
    public async Task<JsonDocument> SendJsonAsync(string accountId, Func<HttpRequestMessage> create, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(accountId, create, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new RemoteFileSystemException(RemoteErrorKind.Other, "The service sent an unreadable answer.", ex);
        }
    }

    public static RemoteFileSystemException ToException(HttpStatusCode status, string body)
    {
        var message = ErrorMessage(body) ?? string.Create(CultureInfo.InvariantCulture, $"HTTP {(int)status}");
        var kind = status switch
        {
            HttpStatusCode.Unauthorized => RemoteErrorKind.AuthenticationFailed,
            HttpStatusCode.Forbidden => RemoteErrorKind.AccessDenied,
            HttpStatusCode.NotFound or HttpStatusCode.Gone => RemoteErrorKind.NotFound,
            HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed => RemoteErrorKind.AlreadyExists,
            _ => RemoteErrorKind.Other,
        };
        return new RemoteFileSystemException(kind, message);
    }

    private static bool IsTransient(HttpResponseMessage response, string body)
        => (int)response.StatusCode is 429 or 500 or 502 or 503 or 504
           // Google reports per-user throttling as 403 with a rate-limit reason.
           || (response.StatusCode == HttpStatusCode.Forbidden && body.Contains("RateLimitExceeded", StringComparison.OrdinalIgnoreCase));

    private static Task DelayAsync(int attempt, RetryConditionHeaderValue? retryAfter, CancellationToken cancellationToken)
    {
        var delay = retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null)
                    ?? TimeSpan.FromMilliseconds((1 << attempt) * 250 + Random.Shared.Next(250));
        return Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds, 100, 30_000)), cancellationToken);
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return text.Length > 8192 ? text[..8192] : text;
        }
        catch (HttpRequestException)
        {
            return string.Empty;
        }
    }

    /// <summary>The "error.message" of a Google or Microsoft Graph error body.</summary>
    private static string? ErrorMessage(string body)
    {
        if (body.Length == 0 || body[0] != '{')
            return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                    return message.GetString();
                if (error.ValueKind == JsonValueKind.String)
                    return error.GetString();
            }
        }
        catch (JsonException)
        {
            // Not JSON after all.
        }

        return null;
    }
}

/// <summary>The body of a response; disposing the stream disposes the response (and frees its connection).</summary>
public sealed class ResponseStream(HttpResponseMessage response, Stream inner) : Stream
{
    public static async Task<Stream> OpenAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        try
        {
            return new ResponseStream(response, await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => response.Content.Headers.ContentLength ?? throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => inner.Read(buffer);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            response.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// OAuth client registration of a cloud plugin: an environment variable (for development and self-built packages), or
/// the value the release build embedded as assembly metadata (repository variables, see docs/PLUGINS.md).
/// </summary>
public static class CloudClientConfiguration
{
    public static string? Read(System.Reflection.Assembly assembly, string metadataKey, string environmentVariable)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        if (Environment.GetEnvironmentVariable(environmentVariable) is { Length: > 0 } fromEnvironment)
            return fromEnvironment.Trim();
        foreach (var attribute in assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), inherit: false))
        {
            if (attribute is System.Reflection.AssemblyMetadataAttribute { Key: var key, Value: { Length: > 0 } value } && key == metadataKey)
                return value;
        }

        return null;
    }
}
