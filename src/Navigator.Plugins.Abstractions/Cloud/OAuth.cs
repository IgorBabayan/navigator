using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Navigator.Plugins.Cloud;

/// <summary>An OAuth 2.0 authorization server and Navigator's registration with it (a public "desktop" client).</summary>
/// <param name="RedirectHost">"127.0.0.1" or "localhost": what the provider accepts for loopback redirects.</param>
public sealed record OAuthProvider(Uri AuthorizeEndpoint, Uri TokenEndpoint, string ClientId, string? ClientSecret, IReadOnlyList<string> Scopes, string RedirectHost = "127.0.0.1")
{
    /// <summary>Additional query parameters for the authorization request (e.g. Google's access_type=offline).</summary>
    public IReadOnlyDictionary<string, string> AuthorizeParameters { get; init; } = new Dictionary<string, string>();
}

/// <summary>Tokens of one account. <see cref="RefreshToken"/> is null when the server did not issue a new one.</summary>
public sealed record OAuthTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt)
{
    /// <summary>True when the access token expires within a minute (refresh before use).</summary>
    public bool IsExpiring => DateTimeOffset.UtcNow >= ExpiresAt - TimeSpan.FromMinutes(1);
}

/// <summary>The authorization server refused a request. <see cref="IsInvalidGrant"/>: the refresh token is no longer valid.</summary>
public sealed class OAuthException : Exception
{
    public OAuthException()
    {
    }

    public OAuthException(string message)
        : base(message)
    {
    }

    public OAuthException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public OAuthException(string error, string message)
        : base(message)
    {
        Error = error;
    }

    public string? Error { get; }

    public bool IsInvalidGrant => Error == "invalid_grant";

    /// <summary>The user declined in the browser (or closed the sign-in).</summary>
    public bool IsAccessDenied => Error == "access_denied";
}

/// <summary>Texts the loopback page shows in the browser after sign-in (already localized).</summary>
public sealed record OAuthPages(string Success, string Failure);

/// <summary>
/// OAuth 2.0 for native apps (RFC 8252): authorization code with PKCE (S256) in the system browser and a one-shot
/// loopback redirect on a random port. No embedded browser, no client secret needed where the provider allows it, and
/// the listener exists only for the few minutes of a sign-in. Refreshes tokens with the refresh_token grant.
/// </summary>
public static class OAuthClient
{
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);
    private const int MaxRequestBytes = 16 * 1024;

    /// <summary>
    /// Opens the provider's sign-in page in the browser and waits for the redirect. Throws <see cref="OAuthException"/>
    /// when the server reports an error, <see cref="TimeoutException"/> after 5 minutes without an answer.
    /// </summary>
    public static async Task<OAuthTokens> SignInAsync(IPluginContext context, OAuthProvider provider, string? loginHint, OAuthPages pages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(pages);

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        using var listeners = LoopbackListeners.Start(provider.RedirectHost);
        var redirect = string.Create(CultureInfo.InvariantCulture, $"http://{provider.RedirectHost}:{listeners.Port}");

        var query = new List<KeyValuePair<string, string>>
        {
            new("response_type", "code"),
            new("client_id", provider.ClientId),
            new("redirect_uri", redirect),
            new("scope", string.Join(' ', provider.Scopes)),
            new("state", state),
            new("code_challenge", challenge),
            new("code_challenge_method", "S256"),
        };
        if (!string.IsNullOrEmpty(loginHint))
            query.Add(new("login_hint", loginHint));
        query.AddRange(provider.AuthorizeParameters);
        var url = new Uri(provider.AuthorizeEndpoint + "?" + FormEncode(query));
        if (!context.OpenInBrowser(url))
            throw new OAuthException("browser", "The web browser could not be opened.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SignInTimeout);
        IReadOnlyDictionary<string, string> answer;
        try
        {
            answer = await listeners.WaitForRedirectAsync(state, pages, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Sign-in timed out.");
        }

        if (answer.TryGetValue("error", out var error))
            throw new OAuthException(error, answer.GetValueOrDefault("error_description") ?? error);
        if (!answer.TryGetValue("code", out var code) || code.Length == 0)
            throw new OAuthException("invalid_response", "The sign-in answer carried no authorization code.");

        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"),
            new("code", code),
            new("redirect_uri", redirect),
            new("client_id", provider.ClientId),
            new("code_verifier", verifier),
        };
        if (!string.IsNullOrEmpty(provider.ClientSecret))
            form.Add(new("client_secret", provider.ClientSecret));
        return await RequestTokensAsync(provider, form, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A new access token for <paramref name="refreshToken"/>. The result keeps the old refresh token unless the server rotated it.</summary>
    public static Task<OAuthTokens> RefreshAsync(OAuthProvider provider, string refreshToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "refresh_token"),
            new("refresh_token", refreshToken),
            new("client_id", provider.ClientId),
        };
        if (!string.IsNullOrEmpty(provider.ClientSecret))
            form.Add(new("client_secret", provider.ClientSecret));
        return RequestTokensAsync(provider, form, refreshToken, cancellationToken);
    }

    private static async Task<OAuthTokens> RequestTokensAsync(OAuthProvider provider, List<KeyValuePair<string, string>> form, string? previousRefreshToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, provider.TokenEndpoint)
        {
            Content = new StringContent(FormEncode(form), Encoding.ASCII, "application/x-www-form-urlencoded"),
        };
        using var response = await CloudHttp.Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return ParseTokens(body, (int)response.StatusCode, previousRefreshToken);
    }

    internal static OAuthTokens ParseTokens(byte[] body, int status, string? previousRefreshToken)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new OAuthException($"The sign-in server answered with HTTP {status} and no valid JSON.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new OAuthException("invalid_response", "Unexpected token response.");
            if (root.TryGetProperty("error", out var error))
            {
                var code = error.ValueKind == JsonValueKind.String ? error.GetString()! : "error";
                var description = root.TryGetProperty("error_description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()! : code;
                throw new OAuthException(code, description);
            }

            if (status is < 200 or > 299 || !root.TryGetProperty("access_token", out var access) || access.ValueKind != JsonValueKind.String)
                throw new OAuthException("invalid_response", $"The sign-in server answered with HTTP {status}.");

            var seconds = root.TryGetProperty("expires_in", out var expires) ? expires.ValueKind switch
            {
                JsonValueKind.Number when expires.TryGetInt32(out var n) => n,
                JsonValueKind.String when int.TryParse(expires.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) => n,
                _ => 3600,
            } : 3600;
            var refresh = root.TryGetProperty("refresh_token", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : previousRefreshToken;
            return new OAuthTokens(access.GetString()!, refresh, DateTimeOffset.UtcNow.AddSeconds(Math.Max(seconds, 60)));
        }
    }

    internal static string FormEncode(IEnumerable<KeyValuePair<string, string>> values)
        => string.Join('&', values.Select(v => Uri.EscapeDataString(v.Key) + "=" + Uri.EscapeDataString(v.Value)));

    internal static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var key = Uri.UnescapeDataString((equals < 0 ? pair : pair[..equals]).Replace('+', ' '));
            var value = equals < 0 ? string.Empty : Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
            result.TryAdd(key, value);
        }

        return result;
    }

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// The redirect target: a minimal HTTP/1.1 responder on the loopback interface (IPv4, plus IPv6 for "localhost",
    /// which some browsers resolve to ::1). Requests without the expected state (favicon, prefetch) get a 404.
    /// </summary>
    private sealed class LoopbackListeners : IDisposable
    {
        private readonly List<TcpListener> _listeners = [];

        private LoopbackListeners()
        {
        }

        public int Port { get; private set; }

        public static LoopbackListeners Start(string host)
        {
            var result = new LoopbackListeners();
            var v4 = new TcpListener(IPAddress.Loopback, 0);
            v4.Start(4);
            result._listeners.Add(v4);
            result.Port = ((IPEndPoint)v4.LocalEndpoint).Port;
            if (host == "localhost" && Socket.OSSupportsIPv6)
            {
                try
                {
                    var v6 = new TcpListener(IPAddress.IPv6Loopback, result.Port);
                    v6.Start(4);
                    result._listeners.Add(v6);
                }
                catch (SocketException)
                {
                    // Port taken on ::1 or IPv6 disabled: IPv4 still answers.
                }
            }

            return result;
        }

        public async Task<IReadOnlyDictionary<string, string>> WaitForRedirectAsync(string state, OAuthPages pages, CancellationToken cancellationToken)
        {
            var accepts = _listeners.ToDictionary(l => l.AcceptTcpClientAsync(cancellationToken).AsTask(), l => l);
            while (true)
            {
                var done = await Task.WhenAny(accepts.Keys).ConfigureAwait(false);
                var listener = accepts[done];
                accepts.Remove(done);
                using var client = await done.ConfigureAwait(false);
                var answer = await HandleAsync(client, state, pages, cancellationToken).ConfigureAwait(false);
                if (answer is not null)
                    return answer;
                accepts[listener.AcceptTcpClientAsync(cancellationToken).AsTask()] = listener;
            }
        }

        private static async Task<IReadOnlyDictionary<string, string>?> HandleAsync(TcpClient client, string state, OAuthPages pages, CancellationToken cancellationToken)
        {
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var stream = client.GetStream();
            string? target;
            try
            {
                target = await ReadRequestTargetAsync(stream, requestTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            var query = target is null ? null : ParseQuery(target.Contains('?', StringComparison.Ordinal) ? target[(target.IndexOf('?', StringComparison.Ordinal) + 1)..] : string.Empty);
            if (query is null || !query.TryGetValue("state", out var received) || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(received), Encoding.ASCII.GetBytes(state)))
            {
                await WriteAsync(stream, "404 Not Found", string.Empty, cancellationToken).ConfigureAwait(false);
                return null;
            }

            var ok = !query.ContainsKey("error");
            var html = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Navigator</title></head>"
                       + "<body style=\"font-family:system-ui,sans-serif;margin:48px;color:#1f2937\"><p>"
                       + WebUtility.HtmlEncode(ok ? pages.Success : pages.Failure) + "</p></body></html>";
            await WriteAsync(stream, "200 OK", html, cancellationToken).ConfigureAwait(false);
            return query;
        }

        /// <summary>Reads the request head (bounded) and returns the request target of a GET, or null.</summary>
        private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(MaxRequestBytes);
            try
            {
                var length = 0;
                while (length < MaxRequestBytes)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(length, MaxRequestBytes - length), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    length += read;
                    if (buffer.AsSpan(0, length).IndexOf("\r\n"u8) >= 0)
                        break;
                }

                var line = Encoding.ASCII.GetString(buffer, 0, Math.Max(0, buffer.AsSpan(0, length).IndexOf("\r\n"u8)));
                var parts = line.Split(' ');
                return parts.Length == 3 && parts[0] == "GET" ? parts[1] : null;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static async Task WriteAsync(NetworkStream stream, string status, string html, CancellationToken cancellationToken)
        {
            var body = Encoding.UTF8.GetBytes(html);
            var head = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
                $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n"));
            try
            {
                await stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The browser went away; the answer has been read already.
            }
        }

        public void Dispose()
        {
            foreach (var listener in _listeners)
                listener.Dispose();
        }
    }
}
