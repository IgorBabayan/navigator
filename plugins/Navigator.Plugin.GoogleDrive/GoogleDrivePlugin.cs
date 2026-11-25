using System.Net.Http.Headers;
using System.Text.Json;
using Navigator.Plugins;
using Navigator.Plugins.Cloud;

namespace Navigator.Plugin.GoogleDrive;

/// <summary>
/// Google Drive in the sidebar's Cloud group: signed-in accounts and the gdrive:// file system.
/// Initialization only registers contributions; nothing touches the network until an account is added or opened.
/// </summary>
public sealed class GoogleDrivePlugin : INavigatorPlugin, IDisposable
{
    public const string ServiceName = "Google Drive";
    private const string ConfigurationHint = "NAVIGATOR_GOOGLE_CLIENT_ID (and NAVIGATOR_GOOGLE_CLIENT_SECRET)";

    private static readonly Lazy<OAuthProvider?> Provider = new(CreateProvider);

    private CloudAuthenticator? _authenticator;
    private GoogleDriveFileSystem? _fileSystem;

    public void Initialize(IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var strings = new PluginStrings(typeof(GoogleDrivePlugin).Assembly, () => context.Language);
        var accounts = new CloudAccountStore(context.DataDirectory);
        _authenticator = new CloudAuthenticator(context, accounts, ServiceName, () => Provider.Value, IdentifyAsync, ConfigurationHint);
        _fileSystem = new GoogleDriveFileSystem(new CloudApi(_authenticator), accounts, strings);
        context.AddFileSystem(_fileSystem);
        context.AddSidebarSection(new CloudAccountsSection(context, _authenticator, "gdrive", GoogleDriveFileSystem.Scheme, "CloudGoogleDrive", order: 10));
    }

    public void Dispose()
    {
        _fileSystem?.Dispose();
        _authenticator?.Dispose();
    }

    private static OAuthProvider? CreateProvider()
    {
        var assembly = typeof(GoogleDrivePlugin).Assembly;
        var clientId = CloudClientConfiguration.Read(assembly, "OAuthClientId", "NAVIGATOR_GOOGLE_CLIENT_ID");
        if (clientId is null)
            return null;
        return new OAuthProvider(
            new Uri("https://accounts.google.com/o/oauth2/v2/auth"),
            new Uri("https://oauth2.googleapis.com/token"),
            clientId,
            CloudClientConfiguration.Read(assembly, "OAuthClientSecret", "NAVIGATOR_GOOGLE_CLIENT_SECRET"),
            ["https://www.googleapis.com/auth/drive"])
        {
            // A refresh token is issued only with offline access; "consent" makes Google issue one on every sign-in.
            AuthorizeParameters = new Dictionary<string, string> { ["access_type"] = "offline", ["prompt"] = "consent" },
        };
    }

    /// <summary>The signed-in user: Drive's permission id is stable for the account.</summary>
    private static async Task<CloudIdentity> IdentifyAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/about?fields=user(permissionId,emailAddress,displayName)");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await CloudHttp.Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw CloudApi.ToException(response.StatusCode, body);
        using var document = JsonDocument.Parse(body);
        var user = document.RootElement.GetProperty("user");
        return new CloudIdentity(
            user.GetProperty("permissionId").GetString() ?? throw new JsonException("No permissionId."),
            user.TryGetProperty("emailAddress", out var email) ? email.GetString() ?? string.Empty : string.Empty,
            user.TryGetProperty("displayName", out var name) ? name.GetString() ?? string.Empty : string.Empty);
    }
}
