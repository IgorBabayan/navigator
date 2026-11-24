using Navigator.Plugins;
using Navigator.Plugins.Remote;

namespace Navigator.Plugin.Ssh;

/// <summary>
/// SSH plugin: an "SSH" sidebar section with saved servers and the sftp:// file system.
/// Initialization only registers contributions; nothing connects until a server is opened.
/// </summary>
public sealed class SshPlugin : INavigatorPlugin, IDisposable
{
    private SftpFileSystem? _fileSystem;

    public void Initialize(IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var strings = new PluginStrings(typeof(SshPlugin).Assembly, () => context.Language);
        var store = new RemoteConnectionStore(context.DataDirectory);
        _fileSystem = new SftpFileSystem(context, store, strings);
        context.AddFileSystem(_fileSystem);
        context.AddSidebarSection(new RemoteConnectionsSection(context, store, "ssh", () => strings["Ssh.Title"], "ServerSsh", order: 200,
            [new RemoteProtocol(SftpFileSystem.Scheme, "SFTP", SftpFileSystem.DefaultPort)], supportsKeyFiles: true, supportsAnonymous: false));
    }

    public void Dispose() => _fileSystem?.Dispose();
}
