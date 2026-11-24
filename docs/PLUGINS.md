# Plugins

Navigator can be extended with plugins: sidebar sections and file systems for addresses such as `sftp://`
and `ftp://`. Plugins are installed, updated and removed in **Settings → Plugins** (the plugin shop, also
reachable from the plug button next to Settings in the title bar).

| Plugin | Id | Project | Ships | Removable |
|---|---|---|---|---|
| Network | `network` | `plugins/Navigator.Plugin.Network` | inside the app (built-in) | no (permanent) |
| SSH (SFTP) | `ssh` | `plugins/Navigator.Plugin.Ssh` | `navigator-plugin-ssh-{version}.navplugin` | yes |
| FTP (FTP, FTPS) | `ftp` | `plugins/Navigator.Plugin.Ftp` | `navigator-plugin-ftp-{version}.navplugin` | yes |

Only the Network plugin is part of the app build: `Navigator.App` references `Navigator.Plugin.Network` and no
other plugin project. SSH and FTP are built and released as separate packages and installed from the shop.

## Architecture

```
Navigator.App ──► Navigator.Plugins (host: discovery, loading, install/update/remove, shop client)
      │                 │
      │                 └──► Navigator.Plugins.Abstractions (the contract; BCL + Navigator.Core)
      │                                ▲
      └──► Navigator.Plugin.Network ───┘        Navigator.Plugin.Ssh / .Ftp ──► Abstractions (Private=false)
```

- **Contract** (`src/Navigator.Plugins.Abstractions`): `INavigatorPlugin`, `IPluginContext`, `IPluginSidebarSection`,
  `IRemoteFileSystem`, `IPluginUi` (dialogs in Navigator's style), `PluginStrings` (a plugin's own localization) and
  ready-made helpers for server plugins in `Navigator.Plugins.Remote` (saved connections, the "Add connection"
  form, in-memory credential cache, connection pool). Its version is `PluginApi` (major.minor). A plugin runs when the
  major version matches and its minor version is not newer than the host's; breaking changes bump the major.
- **Host** (`src/Navigator.Plugins`): `PluginHost` owns all plugins and publishes their contributions as immutable
  snapshots (the sidebar and panes read them without locks). `PluginStoreClient` reads the shop catalog.
- **Presentation**: `SidebarViewModel` shows plugin sections between Drives and Tags (each section loads on a worker
  thread and refreshes on its own); panes browse plugin addresses (`LocationKind.Remote`) through
  `RemoteFileService`; `PluginsViewModel` is the shop page.

### Loading and isolation

- Built-in plugins are constructed directly from a factory (`BuiltInPlugin`): no assembly scanning, no reflection,
  nothing extra before the first frame.
- Installed plugins are loaded by `PluginHost.StartAsync` on a worker thread **after the first frame**. Each gets its
  own `AssemblyLoadContext`, so private dependencies (SSH.NET, BouncyCastle) never clash with the app or other
  plugins. `Navigator.*` assemblies and anything the app has already loaded (the BCL, Microsoft.Extensions) always
  come from the app, so contract types are identical on both sides.
- A plugin that throws while loading is marked **Failed** and shown with its error; other plugins are unaffected.
- `Initialize` must only register contributions. Network access happens when the user opens a server.

### Files on disk

Plugins live in machine-local app data (never a roaming profile):

| OS | Folder |
|---|---|
| Windows | `%LOCALAPPDATA%\Navigator\Plugins` |
| Linux | `$XDG_DATA_HOME/navigator/plugins` (default `~/.local/share/navigator/plugins`) |
| macOS | `~/Library/Application Support/Navigator/Plugins` |
| `NAVIGATOR_DATA_DIR` set | `$NAVIGATOR_DATA_DIR/plugins` |

```
plugins/
  installed.json          active version of each installed plugin (written atomically)
  ssh/1.0.0/              extracted package
  ssh/data/               the plugin's own settings (connections.json, known_hosts.json)
  .staging/               extraction in progress
```

Loaded assemblies cannot be deleted while the app runs (Windows), so **updates and removals finish on the next
start**: an update of a running plugin is shown as "becomes active after a restart"; a removed plugin disappears from
the sidebar at once and its files are deleted at the next start. Anything not listed in `installed.json` is cleaned
up at start.

## Packages and the shop

A package (`.navplugin`) is a ZIP with `plugin.json` and the plugin's assemblies at its root:

```json
{
  "id": "ssh", "name": "SSH", "description": "…", "author": "Navigator",
  "version": "1.0.0", "apiVersion": "1.0",
  "assembly": "Navigator.Plugin.Ssh.dll", "entryType": "Navigator.Plugin.Ssh.SshPlugin",
  "iconKey": "ServerSsh", "homepage": "https://github.com/IgorBabayan/navigator"
}
```

Extraction is defensive: no absolute paths, `..`, drive letters or links; bounded entry count and size; every entry
is checked against its declared length while copying; files are created new, never overwritten.

**Catalog.** Every release publishes `plugin-catalog.json` (id, version, contract version, package name, size,
SHA-256) and `plugin-catalog.json.sig` next to the packages. Release builds read
`https://github.com/{repository}/releases/latest/download/plugin-catalog.json`: one request, no GitHub API rate
limit, and only when the shop page is opened (never at startup). When the build has the update public key, the
catalog must carry a valid ECDSA signature (same key as the update manifest); packages are verified by size and
SHA-256 before installation. Development builds have no shop; `NAVIGATOR_PLUGIN_CATALOG` points the shop at another
catalog (a local file path or an HTTPS URL) for testing.

**Install from file.** A `.navplugin` can also be installed from disk. It has no catalog signature, so the user is
warned and must confirm. A package can never replace a built-in plugin.

## Building plugins

Plugin projects live in `plugins/`. `plugins/Directory.Build.props` and `.targets` turn a project into a packaged
plugin when it sets `NavigatorPluginId`:

```xml
<PropertyGroup>
  <NavigatorPluginId>ssh</NavigatorPluginId>
  <NavigatorPluginVersion>1.0.0</NavigatorPluginVersion>   <!-- the plugin's own version -->
  <NavigatorPluginName>SSH</NavigatorPluginName>
  <NavigatorPluginDescription>…</NavigatorPluginDescription>
  <NavigatorPluginAuthor>Navigator</NavigatorPluginAuthor>
  <NavigatorPluginEntryType>Navigator.Plugin.Ssh.SshPlugin</NavigatorPluginEntryType>
  <NavigatorPluginIconKey>ServerSsh</NavigatorPluginIconKey>
</PropertyGroup>
<ItemGroup>
  <ProjectReference Include="..\..\src\Navigator.Plugins.Abstractions\Navigator.Plugins.Abstractions.csproj" Private="false" />
</ItemGroup>
```

- Every build writes `plugin.json` next to the assembly (single source of truth for the version).
- `dotnet publish -c Release plugins/Navigator.Plugin.Ssh` writes `artifacts/plugins/navigator-plugin-ssh-1.0.0.navplugin`
  (host-provided `Navigator.*` assemblies are left out).
- Bump `NavigatorPluginVersion` to ship an update; the shop offers it when it is newer than the installed version.
- `NavigatorPluginApiVersion` (in `plugins/Directory.Build.props`) must equal `PluginApi` — a unit test checks it.
- Localized strings: embed `Strings/{language}.json` (`LogicalName="Strings.%(Filename)%(Extension)"`) and read them
  with `PluginStrings`; missing keys fall back to English. Shared texts of server plugins come from the host
  (`Plugin.Remote.*` keys) through `IPluginContext.GetHostString`.

The release workflow publishes every packaged plugin, generates the catalog with
`Navigator.ReleaseTool plugin-catalog`, signs it with `sign` and uploads all of it with the release (`docs/RELEASE.md`).

## Writing a file-system plugin

Implement `IRemoteFileSystem` for one or more schemes. Rules the host relies on:

- Every method is called off the UI thread, may run concurrently and must honor cancellation.
- Throw `RemoteFileSystemException` with a `RemoteErrorKind` (`NotFound`, `AccessDenied`, `AuthenticationFailed`,
  `ConnectionFailed`, `HostKeyRejected`, `AlreadyExists`, `Cancelled`); the pane shows a localized message.
- `UploadAsync`, `CreateFolderAsync` and `RenameAsync` must **never overwrite**: fail with `AlreadyExists`.
  Write uploads to a temporary name and rename them into place.
- `DeleteAsync` of a symbolic link removes the link, never its target, and a recursive delete never follows links.
- Never write passwords to disk or logs. `CredentialCache` keeps them in memory for the session.

On the host side, downloads are written to `.{guid}.navigator-partial` and moved into place without overwriting
(" (2)" names), and local files dropped on a server folder are uploaded. The local file engine never receives a
plugin address.

## SSH plugin

- SFTP over [SSH.NET](https://github.com/sshnet/SSH.NET) 2026.0.1 (MIT; uses BouncyCastle.Cryptography, MIT), private to
  the plugin's package.
- Sign-in: password (asked once per session, kept in memory) and private key files (OpenSSH, PuTTY, PKCS#8; encrypted
  keys ask for the passphrase). Keyboard-interactive servers are answered with the password.
- Server keys: trust on first use (`known_hosts.json` in the plugin's data folder, SHA-256 fingerprints). A changed
  key is reported with a warning and connects only if the user replaces it.
- One SSH session per server is shared by all panes and transfers and closed after 5 idle minutes; a session the
  server dropped is replaced transparently once.
- SSH.NET resolves every path with SFTP `REALPATH`, which follows links. Delete and rename therefore act on the item
  as listed in its folder, so they affect the link itself (verified against OpenSSH 9.6).

## FTP plugin

- Own implementation on the .NET base class library (no third-party code): FTP and FTPS with explicit TLS
  (`AUTH TLS`, `PBSZ 0`, `PROT P` — data connections are encrypted as well).
- Passive mode only (`EPSV`, falling back to `PASV`). The data connection always goes to the control connection's
  address; the address in a `PASV` reply is ignored, which also prevents "FTP bounce" redirection.
- Listings: `MLSD` when the server supports it, otherwise `LIST` (Unix `ls -l` and Windows/IIS formats). Deletes use
  `LIST`, which marks links reliably (some servers report a link to a folder as a folder in `MLSD`), and stop at any
  line they cannot parse.
- Uploads go to a temporary name and are renamed after checking the target name is free (`RNTO` replaces existing
  files on most servers). TLS data connections are closed with `close_notify` and a half-close, so servers never see
  a truncated upload.
- TLS session reuse (vsftpd `require_ssl_reuse`): .NET resumes sessions only for host names, so a server addressed by
  IP uses a reserved `ip-….navigator.invalid` TLS name and its certificate is matched against the real IP. Servers
  that refuse resumed TLS 1.3 sessions on data connections (`522`) are switched to TLS 1.2 automatically.
- Certificates the system does not trust (self-signed) can be trusted per server after a warning
  (`trusted_certificates.json`, SHA-256). Anonymous sign-in is supported.
- Up to 3 connections per server (FTP allows one transfer per connection), reused for 2 idle minutes.

## Verification

- Unit tests (`PluginTests`, `FtpProtocolTests`): contract versions, manifest and package validation (path escapes,
  links), catalog signing and tampering, release catalog generation, host behavior (permanent built-ins, failing
  plugins, invalid packages), remote addresses, connection pool, FTP reply/listing parsing.
- End-to-end (manual harness, not in CI): both packages installed through `PluginHost` from a signed local catalog and
  exercised against OpenSSH 9.6 (`internal-sftp`), AsyncSSH (chrooted SFTP), vsftpd 3.0.5 (FTP and FTPS with
  `require_ssl_reuse`) and pyftpdlib (FTP and FTPS with a self-signed certificate): listing, links, 3 MB
  upload/download byte-for-byte, no-overwrite on upload and rename, recursive delete, link delete, missing folders,
  wrong password, anonymous login, 16 parallel listings, update (pending until restart, then cleaned up) and removal.
