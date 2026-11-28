# Plugins

Navigator can be extended with plugins: sidebar sections and file systems for addresses such as `sftp://`
and `ftp://`. Plugins are installed, updated and removed in **Settings → Plugins** (the plugin shop, also
reachable from the plug button next to Settings in the title bar).

| Plugin | Id | Project | Ships | Removable |
|---|---|---|---|---|
| Network (computers, shares, network drives) | `network` | `plugins/Navigator.Plugin.Network` | inside the app (built-in) | no (permanent) |
| SSH (SFTP) | `ssh` | `plugins/Navigator.Plugin.Ssh` | `navigator-plugin-ssh-{version}.navplugin` | yes |
| FTP (FTP, FTPS) | `ftp` | `plugins/Navigator.Plugin.Ftp` | `navigator-plugin-ftp-{version}.navplugin` | yes |
| Google Drive (Cloud group) | `gdrive` | `plugins/Navigator.Plugin.GoogleDrive` | `navigator-plugin-gdrive-{version}.navplugin` | yes |
| OneDrive (Cloud group) | `onedrive` | `plugins/Navigator.Plugin.OneDrive` | `navigator-plugin-onedrive-{version}.navplugin` | yes |
| Git | `git` | `plugins/Navigator.Plugin.Git` | `navigator-plugin-git-{version}.navplugin` | yes |
| Cloud sync folders (Cloud group) | `cloudsync` | `plugins/Navigator.Plugin.CloudSync` | `navigator-plugin-cloudsync-{version}.navplugin` | yes |
| WSL distributions (Windows) | `wsl` | `plugins/Navigator.Plugin.Wsl` | `navigator-plugin-wsl-{version}.navplugin` | yes |
| Windows Search index (Windows) | `windows-search` | `plugins/Navigator.Plugin.WindowsSearch` | `navigator-plugin-windows-search-{version}.navplugin` | yes |
| Windows 11 app commands (Windows) | `win11-menu` | `plugins/Navigator.Plugin.ModernMenu` | `navigator-plugin-win11-menu-{version}.navplugin` | yes |
| Default file manager (Windows, Linux) | `default-file-manager` | `plugins/Navigator.Plugin.DefaultFileManager` | `navigator-plugin-default-file-manager-{version}.navplugin` | yes |

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
  `IRemoteFileSystem`, `IFolderDecorator`, `IItemMenuProvider`, `ISearchProvider`, `IPluginSettingsPage` (API 1.3, see
  below), `IPluginUi` (dialogs and notifications in Navigator's style), `PluginStrings` (a plugin's own localization) and
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

`"platforms": ["windows", "linux", "macos"]` (API 1.3, optional) limits the operating systems a plugin runs on. It comes
from `NavigatorPluginPlatforms` in the project ("windows", "windows;linux"), is copied into the catalog, hides the plugin
from the shop elsewhere, and makes the host refuse to install it (and mark an installed copy *Incompatible*) on other
systems. No field means every system.

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

## Plugin API 1.3: decorations, menus, search, settings

All of these are called off the UI thread unless stated, must honor cancellation, and are isolated: an exception from a
plugin is logged and that plugin's contribution is skipped, nothing else.

- **Folder decorators** (`IFolderDecorator`, `IPluginContext.AddFolderDecorator`): columns of the details view
  (`PluginColumn`, keyed `{decorator}.{column}`, widths and visibility in `FilesAndFolders.PluginColumns`), a badge on
  item icons (`PluginBadge`, drawn by the icon control itself: no extra visuals per row) and a status bar segment with
  commands (`PluginStatusSegment`, e.g. the Git branch with Pull/Push). A pane asks the applicable decorators
  (`AppliesTo`, must be cheap) once a local folder's listing is complete, again 400 ms after a burst of file changes or a
  decorator's `Changed` notice, and cancels the run on navigation. `DecorateAsync` streams steps (cheap data first,
  costly columns later) that are merged off the UI thread; the UI thread only assigns results, and unchanged results raise
  no property changes. `FolderDecorationRequest.VisibleColumns` tells a decorator which of its columns are shown, so
  hidden columns cost nothing. Plugin columns follow the built-in ones and are drawn as text by one control per row
  (`PluginCellsView`); the header context menus show and hide them.
- **Menu providers** (`IItemMenuProvider`): entries for the pane menus of local items and folder backgrounds. The menu
  opens at once with Navigator's own entries; plugin entries are inserted above "Show more options" when they arrive
  (left out after 3 s). The menu's lifetime token ends when it closes; entries stay invokable for a few seconds after.
- **Search providers** (`ISearchProvider`): the first provider whose `CanSearchAsync` accepts a pane search (waited for
  at most 1 s) serves it; otherwise, or when the provider fails before its first result, Navigator's own engine runs.
- **Settings pages** (`IPluginSettingsPage`, one per plugin): switches, buttons and notes shown under the plugin in
  Settings → Plugins. A failing switch shows the plugin's message and turns back.
- `IPluginUi.Notify` (corner notifications), `IPluginUninstallHandler` (undo system changes before removal; not called on
  exit), `PluginProcess` (helper programs: argument lists, both streams read concurrently, timeouts and cancellation kill
  the process tree, record streaming that stops the program when the reader stops), `PluginPreferences` (small
  per-plugin settings in `preferences.json`, written atomically).

## Git plugin

- Uses the installed `git` (PATH; Git for Windows' default folders; on macOS Homebrew or the developer tools, never the
  stub that asks to install them). Nothing runs until a folder inside a repository is shown. Repositories are found by
  looking for `.git` (folder, or a `gitdir:` file for worktrees and submodules) in the folder and its parents, cached;
  UNC paths and `.git` folders themselves are skipped.
- **Status**: one `git --no-optional-locks status --porcelain=v2 --branch -z` per repository, shared by every pane and
  subfolder and reused for 3 s when a folder is merely opened (a change or a refresh asks again). `--no-optional-locks`
  never writes the index, so Navigator's reads cannot trigger its own watchers. A folder's state is the strongest state
  inside it ("Contains changes", "Contains untracked files", "Contains conflicts"); untracked and ignored folders count
  as a whole.
- **Columns**: "Git status" (shown by default), "Last commit", "Commit author", "Commit date" (hidden by default; switch
  them on from a column header's context menu). The commit columns come from one `git log --name-only` walk restricted
  to the folder, stopped as soon as every item has its commit (at most 2,000 commits / 5 s), cached per folder and HEAD.
- **Badges**: M (modified, amber), M (staged, green), A, U, R (green), D, C (red); switchable in Settings → Plugins → Git.
- **Status bar**: branch (or the detached commit) with ↑ahead ↓behind; tooltip with upstream and the number of changes;
  Pull, Push (Publish branch with `--set-upstream` to `origin` or the only remote when there is no upstream) and Fetch.
  The background menu of a repository folder has the same commands under "Git". One remote operation per repository at a
  time; `GIT_TERMINAL_PROMPT=0` (credential helpers with their own window, such as Git Credential Manager, still work),
  no editor for merge commits. Results and errors are shown as notifications / messages with git's own text.
- The git directories of the 12 most recently shown repositories are watched (HEAD, index, FETCH_HEAD, ORIG_HEAD,
  MERGE_HEAD, packed-refs; debounced), so commits, checkouts and fetches made elsewhere update the panes.

## Cloud sync folders plugin

- Sync folders: Windows — every Cloud Files sync root of the user (`HKLM\…\Explorer\SyncRootManager\{provider}!{SID}!{account}\UserSyncRoots`,
  display names resolved with `SHLoadIndirectString`), OneDrive's environment variables, Dropbox's `info.json`, the
  classic iCloud Drive folder; macOS — `~/Library/CloudStorage/*`, iCloud Drive, Dropbox; Linux — Dropbox, the OneDrive
  client (abraunegg, `sync_dir`), Nextcloud (`nextcloud.cfg`). Cached for 30 s; looked up again when volumes change or
  from the plugin's settings.
- Sidebar: the sync folders join the **Cloud** group (before cloud accounts), with the shell's icon for the folder on
  Windows (each client's own logo).
- Sync state (badge and "Status" column): Windows from the placeholder attributes that come with every listing
  (`RECALL_ON_DATA_ACCESS`/`RECALL_ON_OPEN`/`OFFLINE` → available when online, `PINNED` → always available, pinned but
  not local → downloading, otherwise available on this device): no I/O at all. macOS: one `lstat` per item for
  `SF_DATALESS`. Linux clients keep full copies: available on this device. "Sync pending" needs per-file Cloud Files
  calls and is not shown.

## WSL plugin (Windows)

- The sidebar's **Linux** section (below Network) lists the distributions registered under
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss` (installed ones, the default first) — read from the registry, so
  listing never starts a distribution. Entries open `\\wsl.localhost\{name}\` (`\\wsl$\{name}\` before Windows 11) as an
  ordinary folder, so every file action works there; Windows starts the distribution on first access.
- Entry menu: **Open terminal** (`wsl.exe -d {name} --cd ~`) and **Shut down** (`wsl.exe --terminate {name}`).

## Windows Search index plugin (Windows)

- A pane search in a local folder that has items in the Windows Search index (one-row probe, remembered for 2 minutes)
  is answered by the index: `SELECT TOP n … FROM SystemIndex WHERE SCOPE|DIRECTORY='file:…/'` with `System.FileName
  LIKE` (a plain text matches anywhere in the name; `*` and `?` make it a whole-name pattern) or `CONTAINS(*, '"word*"')`
  for content searches. Hidden and system items follow the pane's setting.
- Runs through OLE DB (`IDataInitialize` → `Search.CollatorDSO` → `ICommandText` → `IRowset` with one fixed-size row
  accessor) on a dedicated MTA thread per query, called through vtables; results stream in batches with back-pressure.
  64-bit Navigator only. Not indexed, service stopped or any failure before the first result → Navigator's own search.
- Settings: switch it off; open Indexing Options.

## Windows 11 app commands plugin (Windows 11)

- Packaged apps register their File Explorer commands as `windows.fileExplorerContextMenus` extensions (item types `*`,
  `.ext`, `Directory`, `Directory\Background`, `Drive`). The plugin reads them from the manifests of the user's packages
  (AppModel repository in the registry; manifests are read again only when the package list changes) and, for a
  right-click, creates the matching IExplorerCommand objects out of process (`CLSCTX_LOCAL_SERVER`), asks for state,
  title, icon and sub-commands with the selection as an IShellItemArray, and invokes the chosen one (with
  `CoAllowSetForegroundWindow`, so the app comes to the front). All COM work happens on one MTA worker thread; objects
  are released 5 s after the menu closed. Windows Terminal's "Open in Terminal" is left out (Navigator has its own).
- Windows 10's classic menu already contains these commands, so the plugin does nothing there.

## Default file manager plugin (Windows, Linux)

- Switched in Settings → Plugins (off until the user turns it on); removing the plugin switches it off first.
- Windows (per user, no administrator rights): a `navigator` verb becomes the default verb of `Directory` and `Drive`
  (`"Navigator.exe" "%1"`), and This PC's `opennewwindow` (Win+E) runs Navigator. Previous default verbs are remembered
  and restored; keys the plugin created are removed. Explorer is notified (`SHChangeNotify`). After every start the
  registration is pointed at the running executable if Navigator moved.
- Linux: `xdg-mime default io.github.igorbabayan.Navigator.desktop inode/directory` (a hidden desktop entry is written to
  `~/.local/share/applications` when none is installed); the previous default is restored, or Navigator's line removed
  from `mimeapps.list`.
- macOS does not let another app replace Finder; the plugin is not offered there.

## Network plugin

- Built into the app and permanent. The sidebar's Network section lists mounted network shares and drives on every OS.
- **Windows:** the section always starts with a **Network** entry, like File Explorer. `network://computers/` lists the
  computers Network Discovery found; `network://computers/SERVER` lists that computer's shared folders (administrative
  shares such as `C$` are hidden; a computer that was not discovered can be typed, e.g. `network://computers/192.168.1.20`).
  A share opens as the ordinary folder `\\SERVER\share`, so all file actions work there; Up from a share root returns
  to the computer's list. When a computer asks for credentials, Navigator asks for a user name and password and signs in
  to the computer (`IPC$`); Windows keeps that session. Uses `WNetEnumResource` and `NetShareEnum`; calls run off the
  UI thread and are abandoned when the user navigates away.
- The mapping between plugin addresses and local folders uses `IRemoteLocationMapper` (plugin API 1.1).

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

## Cloud group (plugin API 1.2)

Sidebar sections that implement `IPluginSidebarGroupMember` with `PluginSidebarGroups.Cloud` are merged by the host
into one section, **Cloud**, placed right below Network (order 150; SSH 200, FTP 300). Each member keeps its own
entries and commands; an "add-account" command offered by several members is shown once and asks which service to
use. The group listens to its members only while the sidebar listens to it. Settings → Sidebar has a "Cloud" switch.

Shared helpers for cloud plugins live in `Navigator.Plugins.Cloud` (contract assembly, BCL only):

- `OAuthClient`: OAuth 2.0 for native apps (RFC 8252) — authorization code + PKCE (S256) in the system browser,
  one-shot loopback redirect on a random port (127.0.0.1, plus ::1 for providers that require `localhost`), state
  check, 5-minute timeout. No embedded browser.
- `CloudAuthenticator`: access tokens in memory, refreshed a minute before expiry (one refresh per account at a time);
  refresh tokens in `IPluginContext.Secrets`; an expired/revoked sign-in asks the user to sign in again and checks it
  is the same account.
- `CloudAccountStore` (`accounts.json` in the plugin folder: id, provider user id, e-mail, name — never tokens),
  `CloudAccountsSection` (accounts, "Add account", "Sign out"), `CloudApi` (bearer token, one refresh on 401, retries
  for 429/5xx honoring `Retry-After`, errors mapped to `RemoteErrorKind`), one shared `HttpClient` (HTTP/2,
  compression, created on first use).
- `IPluginContext.Secrets`: Windows DPAPI (per-user encrypted files in `%LOCALAPPDATA%\Navigator\Secrets`), macOS
  login Keychain (`security`, secret via stdin), Linux Secret Service (`secret-tool`, secret via stdin). Without a store
  the secret lives in memory for the session and the user is told so. `IPluginContext.OpenInBrowser` opens https only.

Addresses are `gdrive://{account}/path` and `onedrive://{account}/path`, where `{account}` is a random 10-hex id (no
e-mail addresses in paths); the root shows the account's e-mail. Nothing touches the network until an account is added
or opened.

### OAuth clients

Release builds embed the clients from repository **variables** (they are public desktop clients):
`NAVIGATOR_GOOGLE_CLIENT_ID`, `NAVIGATOR_GOOGLE_CLIENT_SECRET` (Google treats a desktop client secret as public) and
`NAVIGATOR_ONEDRIVE_CLIENT_ID`. The same environment variables override them at run time (development, self-built
packages). Without a client the plugin explains this when "Add account" is used.

- Google: Google Cloud project with the Drive API enabled, OAuth client type "Desktop app", scope
  `https://www.googleapis.com/auth/drive` (a restricted scope: public distribution needs Google's verification).
- Microsoft: Entra app registration, "Mobile and desktop applications" platform with redirect URI `http://localhost`,
  account types "any organization and personal accounts", delegated `Files.ReadWrite.All`, `User.Read`,
  `offline_access`; public client flows enabled (no secret).

## Google Drive plugin

- Drive API v3, "My Drive" of each account. Drive identifies items by id, so paths are resolved segment by segment
  through caches (folder listings 30 s, resolved paths 5 min; every change Navigator makes clears them); a name lookup
  is one small query, not a listing of a large folder.
- Names a path cannot hold: `/` and `\` are shown as `∕` / `⧵`; Google Docs, Sheets, Slides and Drawings get the
  extension they download as (`.docx`, `.xlsx`, `.pptx`, `.pdf`) and are exported on download; items with the same
  name in one folder get ` (2)`, ` (3)`… in creation order. Forms, Sites and My Maps cannot be downloaded.
- Shortcuts are links: opening follows the target, deleting removes only the shortcut.
- Uploads are resumable (8 MiB chunks, nothing appears until complete) and refuse an existing name; rename refuses
  an existing name; delete moves to the Drive trash.

## OneDrive plugin

- Microsoft Graph, personal and work/school accounts. Path addressing (one request per operation); folders shared with
  the user and added to "My files" are followed into their drive; listed children are cached so opening them costs no
  request.
- Uploads: up to 4 MiB in one request, larger files through an upload session (10 MiB chunks, chunk requests without
  the bearer token as Graph requires, session deleted on failure/cancel). `conflictBehavior=fail` everywhere: never
  overwrites. Delete moves to the OneDrive recycle bin.

## Verification

- Unit tests (`IntegrationPluginTests`, plugin API 1.3): host publishing of decorators, menu and search providers and
  settings pages; `platforms` in manifests and the catalog; change notices; preferences; `PluginProcess` (quoting-free
  arguments, both streams, record streaming that stops the program, timeouts); plugin column strip (visibility, widths,
  persistence) and decoration equality; Git porcelain v2 parsing, folder aggregation, worktree `.git` files and an
  end-to-end run against a real repository (status, branch segment, badges, last commits); Cloud Files attribute
  mapping, CloudStorage naming and Linux sync client discovery; WSL paths and icons; Windows Search SQL; Windows 11
  manifest parsing and item-type matching; the Linux desktop entry. Not exercised: Windows registry, COM (OLE DB,
  IExplorerCommand) and WSL calls (no Windows in the development environment).
- Unit tests (`CloudTests`): Cloud group merging/order/command routing/subscriptions, scoped secrets with memory
  fallback, https-only browser, token parsing, the full PKCE loopback sign-in against a local token server, denied
  consent, account store, error mapping, Drive name mapping and item parsing, Graph references and item parsing.
  Not yet exercised against the live Google and Microsoft services (needs registered OAuth clients).
- Unit tests (`PluginTests`, `FtpProtocolTests`): contract versions, manifest and package validation (path escapes,
  links), catalog signing and tampering, release catalog generation, host behavior (permanent built-ins, failing
  plugins, invalid packages), remote addresses, connection pool, FTP reply/listing parsing.
- End-to-end (manual harness, not in CI): both packages installed through `PluginHost` from a signed local catalog and
  exercised against OpenSSH 9.6 (`internal-sftp`), AsyncSSH (chrooted SFTP), vsftpd 3.0.5 (FTP and FTPS with
  `require_ssl_reuse`) and pyftpdlib (FTP and FTPS with a self-signed certificate): listing, links, 3 MB
  upload/download byte-for-byte, no-overwrite on upload and rename, recursive delete, link delete, missing folders,
  wrong password, anonymous login, 16 parallel listings, update (pending until restart, then cleaned up) and removal.
