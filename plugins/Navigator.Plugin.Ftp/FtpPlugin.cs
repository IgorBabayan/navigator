using Navigator.Plugins;
using Navigator.Plugins.Remote;

namespace Navigator.Plugin.Ftp;

/// <summary>
/// FTP plugin: an "FTP" sidebar section with saved servers and the ftp:// and ftps:// file systems.
/// Initialization only registers contributions; nothing connects until a server is opened.
/// </summary>
public sealed class FtpPlugin : INavigatorPlugin, IDisposable
{
    private FtpFileSystem? _fileSystem;

    public void Initialize(IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var strings = new PluginStrings(typeof(FtpPlugin).Assembly, () => context.Language);
        var store = new RemoteConnectionStore(context.DataDirectory);
        _fileSystem = new FtpFileSystem(context, store, strings);
        context.AddFileSystem(_fileSystem);
        context.AddSidebarSection(new RemoteConnectionsSection(context, store, "ftp", () => strings["Ftp.Title"], "ServerFtp", order: 300,
            [
                new RemoteProtocol(FtpFileSystem.SecureScheme, strings["Ftp.ProtocolSecure"], FtpFileSystem.DefaultPort),
                new RemoteProtocol(FtpFileSystem.PlainScheme, strings["Ftp.ProtocolPlain"], FtpFileSystem.DefaultPort),
            ],
            supportsKeyFiles: false, supportsAnonymous: true));
    }

    public void Dispose() => _fileSystem?.Dispose();
}
