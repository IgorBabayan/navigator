using System.Net.Http.Headers;
using System.Text.Json;
using Navigator.Plugins;
using Navigator.Plugins.Cloud;

namespace Navigator.Plugin.OneDrive;

/// <summary>
/// OneDrive in the sidebar's Cloud group: signed-in Microsoft accounts and the onedrive:// file system.
/// Initialization only registers contributions; nothing touches the network until an account is added or opened.
/// </summary>
public sealed class OneDrivePlugin : INavigatorPlugin, IDisposable
{
    public const string ServiceName = "OneDrive";
    private const string ConfigurationHint = "NAVIGATOR_ONEDRIVE_CLIENT_ID";

    private static readonly Lazy<OAuthProvider?> Provider = new(CreateProvider);

    private CloudAuthenticator? _authenticator;
    private OneDriveFileSystem? _fileSystem;

    public void Initialize(IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var strings = new PluginStrings(typeof(OneDrivePlugin).Assembly, () => context.Language);
        var accounts = new CloudAccountStore(context.DataDirectory);
        _authenticator = new CloudAuthenticator(context, accounts, ServiceName, () => Provider.Value, IdentifyAsync, ConfigurationHint);
        _fileSystem = new OneDriveFileSystem(new CloudApi(_authenticator), accounts, strings);
        context.AddFileSystem(_fileSystem);
        context.AddSidebarSection(new CloudAccountsSection(context, _authenticator, "onedrive", OneDriveFileSystem.Scheme, "CloudOneDrive", order: 20));
    }

    public void Dispose()
    {
        _fileSystem?.Dispose();
        _authenticator?.Dispose();
    }

    private static OAuthProvider? CreateProvider()
    {
        var clientId = CloudClientConfiguration.Read(typeof(OneDrivePlugin).Assembly, "OAuthClientId", "NAVIGATOR_ONEDRIVE_CLIENT_ID");
        if (clientId is null)
            return null;
        return new OAuthProvider(
            new Uri("https://login.microsoftonline.com/common/oauth2/v2.0/authorize"),
            new Uri("https://login.microsoftonline.com/common/oauth2/v2.0/token"),
            clientId,
            ClientSecret: null, // public client: PKCE only
            ["Files.ReadWrite.All", "User.Read", "offline_access"],
            RedirectHost: "localhost")
        {
            AuthorizeParameters = new Dictionary<string, string> { ["prompt"] = "select_account" },
        };
    }

    private static async Task<CloudIdentity> IdentifyAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me?$select=id,displayName,mail,userPrincipalName");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await CloudHttp.Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw CloudApi.ToException(response.StatusCode, body);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        string Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;
        var email = Text("mail");
        return new CloudIdentity(Text("id") is { Length: > 0 } id ? id : throw new JsonException("No user id."), email.Length > 0 ? email : Text("userPrincipalName"), Text("displayName"));
    }
}
