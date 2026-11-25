using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Navigator.Core.Navigation;
using Navigator.Core.Settings;

namespace Navigator.Plugins.Cloud;

/// <summary>
/// A signed-in cloud account. <see cref="Id"/> is a short random token used as the "host" of the account's addresses
/// (gdrive://3f9a1c0b7e/Documents), so addresses never contain an e-mail address. No tokens are part of it.
/// </summary>
public sealed class CloudAccount
{
    public string Id { get; set; } = string.Empty;

    /// <summary>The provider's stable user id (signing in again with the same user updates this account).</summary>
    public string Subject { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    [JsonIgnore]
    public string Name => Email.Length > 0 ? Email : DisplayName.Length > 0 ? DisplayName : Id;

    internal static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(5));
}

/// <summary>Who signed in, as reported by the service after sign-in.</summary>
public sealed record CloudIdentity(string Subject, string Email, string DisplayName);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(List<CloudAccount>))]
internal sealed partial class CloudJsonContext : JsonSerializerContext;

/// <summary>Signed-in accounts of one plugin (accounts.json in the plugin's data folder). Reads once, writes atomically, thread-safe.</summary>
public sealed class CloudAccountStore(string dataDirectory)
{
    private readonly string _path = Path.Combine(dataDirectory, "accounts.json");
    private readonly Lock _gate = new();
    private List<CloudAccount>? _accounts;

    public event EventHandler? Changed;

    public IReadOnlyList<CloudAccount> GetAll()
    {
        lock (_gate)
            return [.. Load()];
    }

    public CloudAccount? Find(string id)
    {
        lock (_gate)
            return Load().FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public CloudAccount? FindBySubject(string subject)
    {
        lock (_gate)
            return Load().FirstOrDefault(a => a.Subject == subject);
    }

    public void AddOrUpdate(CloudAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        lock (_gate)
        {
            var list = Load();
            var index = list.FindIndex(a => a.Id == account.Id);
            if (index >= 0)
                list[index] = account;
            else
                list.Add(account);
            Save(list);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            var list = Load();
            if (list.RemoveAll(a => a.Id == id) == 0)
                return;
            Save(list);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private List<CloudAccount> Load()
    {
        if (_accounts is not null)
            return _accounts;
        try
        {
            _accounts = File.Exists(_path) ? JsonSerializer.Deserialize(File.ReadAllBytes(_path), CloudJsonContext.Default.ListCloudAccount) ?? [] : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _accounts = [];
        }

        _accounts.RemoveAll(a => a.Id.Length == 0 || a.Subject.Length == 0);
        return _accounts;
    }

    private void Save(List<CloudAccount> list)
    {
        _accounts = list;
        AtomicFile.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(list, CloudJsonContext.Default.ListCloudAccount));
    }
}

/// <summary>
/// Sign-in and access tokens for the accounts of one cloud plugin. Access tokens live in memory and are refreshed shortly
/// before they expire (one refresh per account at a time, however many panes ask); refresh tokens are kept by the OS
/// credential store (<see cref="IPluginContext.Secrets"/>). When a refresh token stops working (password changed,
/// access revoked), the user is asked to sign in again in the browser.
/// </summary>
public sealed class CloudAuthenticator : IDisposable
{
    private const string RefreshPrefix = "refresh-";

    private readonly IPluginContext _context;
    private readonly CloudAccountStore _store;
    private readonly Func<OAuthProvider?> _provider;
    private readonly Func<string, CancellationToken, Task<CloudIdentity>> _identify;
    private readonly ConcurrentDictionary<string, OAuthTokens> _tokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    /// <param name="serviceName">"Google Drive", "OneDrive".</param>
    /// <param name="provider">The OAuth registration, or null when this build has none (see <paramref name="configurationHint"/>).</param>
    /// <param name="identify">Asks the service who the access token belongs to.</param>
    /// <param name="configurationHint">How to provide a client id (shown when <paramref name="provider"/> returns null).</param>
    public CloudAuthenticator(IPluginContext context, CloudAccountStore store, string serviceName, Func<OAuthProvider?> provider,
        Func<string, CancellationToken, Task<CloudIdentity>> identify, string configurationHint)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        _context = context;
        _store = store;
        _provider = provider;
        _identify = identify;
        ServiceName = serviceName;
        ConfigurationHint = configurationHint;
    }

    public string ServiceName { get; }

    public string ConfigurationHint { get; }

    public CloudAccountStore Accounts => _store;

    /// <summary>A valid access token for the account (refreshed or re-authorized as needed).</summary>
    public async Task<string> GetAccessTokenAsync(string accountId, bool forceRefresh, CancellationToken cancellationToken)
    {
        if (!forceRefresh && _tokens.TryGetValue(accountId, out var cached) && !cached.IsExpiring)
            return cached.AccessToken;

        var account = _store.Find(accountId)
                      ?? throw new RemoteFileSystemException(RemoteErrorKind.NotFound, Text("Plugin.Cloud.UnknownAccount"));
        var gate = _locks.GetOrAdd(account.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_tokens.TryGetValue(account.Id, out cached) && !cached.IsExpiring && (!forceRefresh || cached.ExpiresAt - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(55)))
                return cached.AccessToken; // another caller refreshed while this one waited

            var provider = _provider() ?? throw NotConfigured();
            var refreshToken = await _context.Secrets.GetAsync(RefreshPrefix + account.Id, cancellationToken).ConfigureAwait(false);
            if (refreshToken is not null)
            {
                try
                {
                    var tokens = await OAuthClient.RefreshAsync(provider, refreshToken, cancellationToken).ConfigureAwait(false);
                    await RememberAsync(account, tokens, refreshToken, cancellationToken).ConfigureAwait(false);
                    return tokens.AccessToken;
                }
                catch (OAuthException ex) when (ex.IsInvalidGrant)
                {
                    _context.Log.Info($"{ServiceName}: the stored sign-in of an account is no longer valid; asking to sign in again");
                }
                catch (OAuthException ex)
                {
                    throw new RemoteFileSystemException(RemoteErrorKind.AuthenticationFailed, ex.Message, ex);
                }
                catch (HttpRequestException ex)
                {
                    throw new RemoteFileSystemException(RemoteErrorKind.ConnectionFailed, ex.Message, ex);
                }
            }

            return await SignInAgainAsync(account, provider, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Signs a new account in through the browser. Returns null when the user cancelled; failures are shown to the user.</summary>
    public async Task<CloudAccount?> AddAccountAsync(CancellationToken cancellationToken)
    {
        if (_provider() is not { } provider)
        {
            await _context.Ui.ShowMessageAsync(Format("Plugin.Cloud.SignInTitle", ServiceName), Format("Plugin.Cloud.NotConfigured", ServiceName, ConfigurationHint)).ConfigureAwait(false);
            return null;
        }

        if (!await _context.Ui.ConfirmAsync(Format("Plugin.Cloud.SignInTitle", ServiceName), Format("Plugin.Cloud.SignInMessage", ServiceName), Text("Plugin.Cloud.OpenBrowser")).ConfigureAwait(false))
            return null;

        try
        {
            var tokens = await OAuthClient.SignInAsync(_context, provider, null, Pages(), cancellationToken).ConfigureAwait(false);
            var identity = await _identify(tokens.AccessToken, cancellationToken).ConfigureAwait(false);
            var account = _store.FindBySubject(identity.Subject) ?? new CloudAccount { Id = CloudAccount.NewId(), Subject = identity.Subject };
            account.Email = identity.Email;
            account.DisplayName = identity.DisplayName;
            var persisted = await RememberAsync(account, tokens, null, cancellationToken).ConfigureAwait(false);
            _store.AddOrUpdate(account);
            if (!persisted)
                await _context.Ui.ShowMessageAsync(Format("Plugin.Cloud.SignInTitle", ServiceName), Text("Plugin.Cloud.SessionOnly")).ConfigureAwait(false);
            return account;
        }
        catch (OAuthException ex) when (ex.IsAccessDenied)
        {
            return null;
        }
        catch (Exception ex) when (ex is OAuthException or TimeoutException or HttpRequestException or RemoteFileSystemException or JsonException)
        {
            _context.Log.Warning($"{ServiceName}: sign-in failed", ex);
            var message = ex switch
            {
                TimeoutException => Text("Plugin.Cloud.TimedOut"),
                OAuthException { Error: "browser" } => Text("Plugin.Cloud.BrowserFailed"),
                _ => ex.Message,
            };
            await _context.Ui.ShowMessageAsync(Text("Plugin.Cloud.SignInFailed"), message).ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>Forgets an account and its stored sign-in. Files in the cloud are not touched.</summary>
    public async Task SignOutAsync(string accountId, CancellationToken cancellationToken)
    {
        _tokens.TryRemove(accountId, out _);
        _store.Remove(accountId);
        await _context.Secrets.RemoveAsync(RefreshPrefix + accountId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> SignInAgainAsync(CloudAccount account, OAuthProvider provider, CancellationToken cancellationToken)
    {
        if (!await _context.Ui.ConfirmAsync(Format("Plugin.Cloud.SignInTitle", ServiceName), Format("Plugin.Cloud.SignInAgainMessage", account.Name), Text("Plugin.Cloud.OpenBrowser")).ConfigureAwait(false))
            throw new RemoteFileSystemException(RemoteErrorKind.Cancelled, "Sign-in was cancelled.");

        OAuthTokens tokens;
        CloudIdentity identity;
        try
        {
            tokens = await OAuthClient.SignInAsync(_context, provider, account.Email, Pages(), cancellationToken).ConfigureAwait(false);
            identity = await _identify(tokens.AccessToken, cancellationToken).ConfigureAwait(false);
        }
        catch (OAuthException ex) when (ex.IsAccessDenied)
        {
            throw new RemoteFileSystemException(RemoteErrorKind.Cancelled, "Sign-in was cancelled.", ex);
        }
        catch (TimeoutException ex)
        {
            throw new RemoteFileSystemException(RemoteErrorKind.Other, Text("Plugin.Cloud.TimedOut"), ex);
        }
        catch (OAuthException ex) when (ex.Error == "browser")
        {
            throw new RemoteFileSystemException(RemoteErrorKind.Other, Text("Plugin.Cloud.BrowserFailed"), ex);
        }
        catch (Exception ex) when (ex is OAuthException or HttpRequestException or JsonException)
        {
            throw new RemoteFileSystemException(RemoteErrorKind.AuthenticationFailed, ex.Message, ex);
        }

        if (identity.Subject != account.Subject)
        {
            await _context.Ui.ShowMessageAsync(Text("Plugin.Cloud.SignInFailed"), Format("Plugin.Cloud.WrongAccount", identity.Email, account.Name)).ConfigureAwait(false);
            throw new RemoteFileSystemException(RemoteErrorKind.Cancelled, "Signed in with another account.");
        }

        await RememberAsync(account, tokens, null, cancellationToken).ConfigureAwait(false);
        return tokens.AccessToken;
    }

    /// <summary>Caches the access token; stores the refresh token when it is new. Returns whether the sign-in survives a restart.</summary>
    private async Task<bool> RememberAsync(CloudAccount account, OAuthTokens tokens, string? storedRefreshToken, CancellationToken cancellationToken)
    {
        _tokens[account.Id] = tokens;
        if (tokens.RefreshToken is null)
            return false;
        if (string.Equals(tokens.RefreshToken, storedRefreshToken, StringComparison.Ordinal))
            return true;
        return await _context.Secrets.SetAsync(RefreshPrefix + account.Id, tokens.RefreshToken, CancellationToken.None).ConfigureAwait(false);
    }

    private RemoteFileSystemException NotConfigured()
        => new(RemoteErrorKind.Other, Format("Plugin.Cloud.NotConfigured", ServiceName, ConfigurationHint));

    private OAuthPages Pages() => new(Text("Plugin.Cloud.SignedInPage"), Text("Plugin.Cloud.SignInDeniedPage"));

    private string Text(string key) => _context.GetHostString(key);

    private string Format(string key, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, Text(key), arguments);

    public void Dispose()
    {
        _tokens.Clear();
        foreach (var gate in _locks.Values)
            gate.Dispose();
        _locks.Clear();
    }
}

/// <summary>
/// The sidebar entries of one cloud service: its signed-in accounts, shown in the shared "Cloud" group (below Network).
/// Offers "Add account" (sign in through the browser) and, per account, "Sign out".
/// </summary>
public sealed class CloudAccountsSection : IPluginSidebarSection, IPluginSidebarGroupMember
{
    public const string AddAccountCommand = "add-account";
    private const string SignOutCommand = "sign-out";

    private readonly IPluginContext _context;
    private readonly CloudAuthenticator _authenticator;
    private readonly string _scheme;

    public CloudAccountsSection(IPluginContext context, CloudAuthenticator authenticator, string id, string scheme, string iconKey, int order)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authenticator);
        _context = context;
        _authenticator = authenticator;
        _scheme = scheme;
        Id = id;
        IconKey = iconKey;
        Order = order;
        authenticator.Accounts.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public string Id { get; }

    public string GroupId => PluginSidebarGroups.Cloud;

    public string Title => _authenticator.ServiceName;

    public string IconKey { get; }

    public string? SystemIconKey => null;

    public int Order { get; }

    public bool ShowWhenEmpty => true;

    public IReadOnlyList<PluginCommand> Commands
        => [new(AddAccountCommand, string.Format(CultureInfo.CurrentCulture, _context.GetHostString("Plugin.Cloud.AddServiceAccount"), _authenticator.ServiceName), "Add")];

    public event EventHandler? Changed;

    /// <summary>The address an account's entry opens: its root folder.</summary>
    public static string RootAddress(string scheme, string accountId) => scheme + "://" + accountId + "/";

    public ValueTask<IReadOnlyList<PluginSidebarEntry>> GetEntriesAsync(CancellationToken cancellationToken)
    {
        PluginCommand[] commands = [new(SignOutCommand, _context.GetHostString("Plugin.Cloud.SignOut"), "SignOut", IsDestructive: true)];
        IReadOnlyList<PluginSidebarEntry> entries = [.. _authenticator.Accounts.GetAll()
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => new PluginSidebarEntry(Id + ":" + a.Id, a.Name, IconKey, Location.Remote(RootAddress(_scheme, a.Id)), _authenticator.ServiceName) { Commands = commands })];
        return ValueTask.FromResult(entries);
    }

    public async Task ExecuteAsync(string commandId, PluginSidebarEntry? entry, CancellationToken cancellationToken)
    {
        switch (commandId)
        {
            case AddAccountCommand:
                await _authenticator.AddAccountAsync(cancellationToken).ConfigureAwait(false);
                break;
            case SignOutCommand when entry is not null && RemoteUri.TryParse(entry.Location.Path, out var address):
                var message = string.Format(CultureInfo.CurrentCulture, _context.GetHostString("Plugin.Cloud.SignOutConfirm"), entry.Name);
                if (await _context.Ui.ConfirmAsync(_context.GetHostString("Plugin.Cloud.SignOut"), message, _context.GetHostString("Plugin.Cloud.SignOut"), isDestructive: true).ConfigureAwait(false))
                    await _authenticator.SignOutAsync(address.Host, cancellationToken).ConfigureAwait(false);
                break;
        }
    }
}
