# Feature parity with the Files app

Status legend

| Status | Meaning |
|---|---|
| ✅ Tested | Implemented; the logic is covered by automated tests that were run (`docs/TESTING.md`). |
| 🟦 Written | Implemented, but not yet validated: UI code (Avalonia app layer) or platform code that could not be built/run in the development environment. Must be verified on each OS before it counts as done. |
| 🟨 Partial | Implemented with documented gaps (`docs/KNOWN_ISSUES.md`). |
| ⬜ Planned | Not implemented. |
| ➖ N/A | Not applicable on that platform. |

Platform columns: **W** Windows · **L** Linux · **M** macOS. "Logic" is the status of the UI-independent
engine/view-model code; "UI" is the status of the Avalonia surface.

## Shell and navigation

| Feature | Logic | UI | W | L | M | Notes |
|---|---|---|---|---|---|---|
| Tabs (new, close, duplicate, pin, reorder, close others/right) | 🟦 | 🟦 | ✓ | ✓ | ✓ | `ShellViewModel` (no dedicated tests yet) |
| Reopen closed tabs (25) | 🟦 | 🟦 | ✓ | ✓ | ✓ | |
| Multiple windows | 🟦 | 🟦 | ✓ | ✓ | ✓ | `WindowService` |
| Tab/window session restore | ✅ | 🟦 | ✓ | ✓ | ✓ | `SessionState` persistence + corruption tests; restore flow in `WindowService` unverified |
| Drag a tab out into a new window | 🟦 | 🟦 | ✓ | ✓ | ✓ | `ShellViewModel.DetachTab`/`AdoptTab`, `WindowService.MoveTabToWindow`; dropped on another window's strip it joins that window; tab menu "Move to new window"; the tab keeps its panes and history |
| Back / forward / up / refresh, history (128) | ✅ | 🟦 | ✓ | ✓ | ✓ | `NavigationHistory` |
| Mouse back/forward side buttons, browser keys | ➖ | 🟦 | ✓ | ✓ | ✓ | XButton1/XButton2 act on release on the pane under the pointer; BrowserBack/BrowserForward keys |
| Breadcrumb bar | ✅ | 🟦 | ✓ | ✓ | ✓ | |
| Breadcrumb chevron flyouts (sub-folders, root locations, overflow «) | ✅ | 🟦 | ✓ | ✓ | ✓ | `AddressCompletion.FindFolders`, `PaneViewModel.GetBreadcrumbChildrenAsync` / `GetRootLocationsAsync` |
| Editable address bar (`~`, env vars, relative, shell names) | ✅ | 🟦 | ✓ | ✓ | ✓ | `PathUtil.ExpandUserInput`, `AddressAliases` |
| Address autocompletion (Up/Down, Tab) | ✅ | 🟦 | ✓ | ✓ | ✓ | `AddressCompletion` |
| Typed-address history drop-down (25) | ✅ | 🟦 | ✓ | ✓ | ✓ | `GeneralSettings.AddressHistory`, F4 / Alt+Down / arrow |
| Command palette | 🟦 | 🟦 | ✓ | ✓ | ✓ | Ctrl+Shift+P |
| Home page (quick access, drives, recent files, tags) | 🟦 | 🟦 | ✓ | ✓ | ✓ | |
| Split view (dual pane) | 🟦 | 🟦 | ✓ | ✓ | ✓ | |
| Column view (Miller columns) | 🟦 | 🟦 | ✓ | ✓ | ✓ | `PaneViewModel.Columns`, `ColumnTrail` (tested), `MillerColumnViewModel`; Layout → Columns, Ctrl+Shift+6; current folder is the full pane list, ancestors are read-only columns |
| Compact overlay / always on top | 🟦 | 🟦 | ✓ | ✓ | 🟦 | toolbar toggle and More menu; `Window.Topmost`; compact overlay shrinks the window, hides the sidebar and restores everything when left |
| Full screen | 🟦 | 🟦 | ✓ | ✓ | ✓ | |

## Sidebar

| Feature | Logic | UI | W | L | M | Notes |
|---|---|---|---|---|---|---|
| Home, pinned folders (pin/unpin/reorder) | 🟦 | 🟦 | ✓ | ✓ | ✓ | `PinnedFolders` |
| Default pins = known folders | 🟨 | 🟦 | 🟦 | ✅ | 🟦 | XDG `user-dirs.dirs` parsing tested; Windows shell API and macOS paths untested |
| Drives with capacity bar, live mount/unmount | ✅ | 🟦 | 🟦 | ✅ | 🟦 | Linux filtering tested; change detection via mount-root watchers + 3 s fingerprint while visible |
| Network locations | 🟨 | 🟦 | 🟦 | 🟦 | 🟦 | built-in Network plugin; Windows: browse discovered computers and their SMB shares (untested on Windows); Linux/macOS: mounted network volumes only |
| SSH / SFTP servers (plugin) | ✅ | 🟦 | ✓ | ✓ | ✓ | SSH plugin; saved connections, password/key sign-in, host key TOFU; tested against OpenSSH and AsyncSSH |
| FTP / FTPS servers (plugin) | ✅ | 🟦 | ✓ | ✓ | ✓ | FTP plugin; explicit TLS, anonymous; tested against vsftpd and pyftpdlib |
| Plugin shop (install/update/remove) | ✅ | 🟦 | ✓ | ✓ | ✓ | Settings → Plugins; signed catalog; built-in Network cannot be removed |
| Tags section | 🟦 | 🟦 | ✓ | ✓ | ✓ | |
| Recycle Bin / Trash entry | ✅ | 🟦 | ✓ | ✓ | ✓ | |
| Collapsible sections, resizable width | 🟦 | 🟦 | ✓ | ✓ | ✓ | |
| Cloud sync folders (OneDrive, Dropbox, iCloud Drive, Google Drive, Box, Nextcloud) with sync-status icons (plugin) | ✅ | 🟦 | 🟦 | ✅ | 🟦 | Cloud sync folders plugin: sync roots in the sidebar's Cloud group; badge + "Status" column (available when online / on this device / always available / downloading). Windows: Cloud Files sync roots (SyncRootManager) and placeholder attributes from the listing; macOS: ~/Library/CloudStorage, iCloud Drive, `SF_DATALESS`; Linux: Dropbox, OneDrive client, Nextcloud. Linux discovery and decorations tested; Windows and macOS untested |
| WSL distributions (plugin) | ✅ | 🟦 | 🟦 | ➖ | ➖ | WSL plugin: "Linux" sidebar section from HKCU…\Lxss (no wsl.exe call), `\\wsl.localhost\{name}\` (`\\wsl$` before Windows 11), Open terminal / Shut down. Paths, icons and order tested; registry and wsl.exe untested |

## Browsing and views

| Feature | Logic | UI | W | L | M | Notes |
|---|---|---|---|---|---|---|
| Details layout with resizable columns, column visibility | 🟦 | 🟦 | ✓ | ✓ | ✓ | |
| Column reordering | ✅ | 🟦 | ✓ | ✓ | ✓ | drag a header sideways or use its menu (move left/right, reset); `ColumnOrder` (tested), `DetailsCellsPanel`; name column stays first; order stored in `FilesAndFolders.Columns` |
| List, Tiles, Small/Medium/Large/Extra-large icon layouts | 🟦 | 🟦 | ✓ | ✓ | ✓ | row-chunked virtualization |
| Sort (name/date modified/created/type/size), natural order, folders first | ✅ | 🟦 | ✓ | ✓ | ✓ | `NaturalStringComparer` |
| Group by (name, type, date, size …) | ✅ | 🟦 | ✓ | ✓ | ✓ | `EntryGrouper` |
| Per-folder view preferences (bounded 500) | 🟦 | 🟦 | ✓ | ✓ | ✓ | |
| Hidden / protected system items, file extensions toggles | ✅ | 🟦 | ✓ | ✓ | ✓ | |
| Incremental live updates (file system watcher) | ✅ | 🟦 | ✓ | ✓ | ✓ | `DirectoryMonitor` + binary-search apply |
| OS icons for file types and folders (setting) | ✅ | 🟦 | 🟦 | ✅ | 🟦 | Windows shell image lists by type (programs/shortcuts per file); Linux icon theme (GIO + gdk-pixbuf, verified on Adwaita/Humanity); macOS NSWorkspace (not run yet) |
| Thumbnails (images) | 🟦 | 🟦 | ✓ | ✓ | ✓ | images decoded by Avalonia at display size; memory LRU + disk cache (images > 256 KB) |
| Thumbnails of videos, PDFs, documents, album art + disk cache | ✅ | 🟦 | 🟦 | ✅ | 🟦 | icon/tile layouts only (≥ 64 px); embedded art (`MediaInfoReader`, `OfficeDocumentReader`, tested) → Windows `IShellItemImageFactory` / macOS `qlmanage -t` / Linux freedesktop cache + `pdftoppm` + `ffmpegthumbnailer`/`ffmpeg` (Linux ffmpeg path run here); negative results cached |
| Folder sizes (opt-in) | ✅ | 🟦 | ✓ | ✓ | ✓ | low-priority background |
| Filter current folder | ✅ | 🟦 | ✓ | ✓ | ✓ | `NameFilter` |
| Selection: click, Ctrl, Shift ranges, select all/invert/clear | ✅ | 🟦 | ✓ | ✓ | ✓ | `ItemSelection` |
| Rubber-band selection | 🟨 | 🟦 | ✓ | ✓ | ✓ | selects realized (visible) rows only |
| Type-to-select | ✅ | 🟦 | ✓ | ✓ | ✓ | |
| Checkbox selection mode | ⬜ | ⬜ | | | | |

## File operations

| Feature | Logic | UI | W | L | M | Notes |
|---|---|---|---|---|---|---|
| Copy / cut / paste (in-app clipboard) | 🟦 | 🟦 | ✓ | ✓ | ✓ | |
| Paste files from/to other apps (OS file clipboard) | ⬜ | ⬜ | | | | text clipboard only |
| Drag and drop inside Navigator (panes, tabs, sidebar) | ✅ | 🟦 | ✓ | ✓ | ✓ | `DropPlanner` (same volume = move), `ItemDragDrop`; hover a tab to switch to it, drop on a tab, pin by dropping on the pinned group |
| Drag and drop to/from other apps | 🟦 | 🟦 | ✓ | ✓ | ✓ | Avalonia `DataFormat.File`; files from other apps are copied unless Shift is held (Windows: same volume = move) |
| Queue, concurrency limit, pause/resume, cancel | ✅ | 🟦 | ✓ | ✓ | ✓ | `FileOperationQueue`, `PauseGate` |
| Progress, speed, ETA, status center (toolbar ring, cards, speed graph, dismiss/clear/cancel all) | 🟦 | 🟦 | ✓ | ✓ | ✓ | `OperationsViewModel`, `OperationsView`, `ProgressRing`, `SpeedGraph` |
| Conflict resolution (replace/skip/keep both/merge, apply to all) | ✅ | 🟦 | ✓ | ✓ | ✓ | never silent overwrite |
| Atomic copy via temp file, metadata preserved | ✅ | ➖ | ✓ | ✓ | ✓ | `SafeFileCopier` |
| Cross-volume move with verify-before-delete | ✅ | ➖ | ✓ | ✓ | ✓ | `TransferExecutor` |
| Symlinks recreated, never followed | ✅ | ➖ | 🟦 | ✅ | 🟦 | Windows symlink creation needs privilege/dev mode |
| Retry / skip / cancel on errors | ✅ | 🟦 | ✓ | ✓ | ✓ | `IoErrorClassifier` |
| Rename (incl. case-only), duplicate, new folder/file | ✅ | 🟦 | ✓ | ✓ | ✓ | rename happens in place in the list (`PaneViewModel.Rename`, one overlay editor per pane) |
| Multi-rename, Total Commander style (masks, ranges, counter, dates, case, search/replace incl. regex, live preview, one undo step) | ✅ | 🟦 | ✓ | ✓ | ✓ | `MultiRenameTemplate`, `MultiRenameViewModel`, `BulkRenamePlanner.Order`, `FileOperationService.RenameMany`; Ctrl+R |
| Undo / redo (rename, move, copy, create, recycle) | ✅ | 🟦 | ✓ | ✓ | ✓ | `UndoManager` |
| Delete to Recycle Bin / Trash | ✅ | 🟦 | 🟦 | ✅ | 🟦 | Linux freedesktop trash tested; Windows `SHFileOperationW`; macOS `NSFileManager` |
| Permanent delete (with confirmation) | ✅ | 🟦 | ✓ | ✓ | ✓ | |
| Trash browsing, restore, empty | 🟨 | 🟦 | 🟦 | ✅ | 🟨 | macOS restore only for items Navigator trashed |
| Archives: create ZIP; extract ZIP/TAR/TAR.GZ (smart destination, zip-slip safe) | 🟨 | 🟦 | ✓ | ✓ | ✓ | ZIP round trip and traversal tested; TAR paths untested; context menu: extract files… (pick folder), here (smart), here, to own folder; compress together or each |
| Open (extract) 7z and RAR archives | ✅ | 🟦 | 🟦 | ✅ | 🟦 | `ArchiveReaderTool` through an installed program: 7-Zip (`7z`/`7zz`; `7za` reads 7z only), libarchive `bsdtar` (Windows 11 23H2+ `tar.exe`), `unrar`, `unar`. Unpacked into a hidden staging folder, then moved in with Navigator's conflict handling; links leaving the destination are dropped. Same Extract menu as ZIP; offered only when a reader is installed. 7z creation stays external too (`SevenZipTool`). Tested with 7-Zip and bsdtar on Linux |
| Other archive formats (XZ, BZip2, CAB…) | ⬜ | ⬜ | | | | |
| Hashes (MD5, SHA-1, SHA-256/384/512, compare) | ✅ | 🟦 | ✓ | ✓ | ✓ | `FileHasher` |

## Opening and integration

| Feature | Logic | UI | W | L | M | Notes |
|---|---|---|---|---|---|---|
| Open with default app | 🟦 | 🟦 | 🟦 | 🟦 | 🟦 | `ShellExecute` / `xdg-open` / `open` |
| Open with… (handler list) | 🟨 | 🟦 | 🟦 | ✅ | 🟨 | Linux parses `.desktop`/`mimeapps.list` (tested); Windows lists recommended handlers (`SHAssocEnumHandlers`, launched via `IAssocHandler::Invoke`) before the shell's store/chooser entries; macOS uses `open -a` without enumeration |
| Open in terminal | 🟦 | 🟦 | 🟦 | 🟦 | 🟦 | platform default terminal |
| Open in code editor | 🟦 | 🟦 | 🟦 | 🟦 | 🟦 | configurable command |
| Reveal in system file manager | 🟦 | 🟦 | 🟦 | 🟦 | 🟦 | |
| Open in new tab / new window | 🟦 | 🟦 | ✓ | ✓ | ✓ | |
| Pane context menus as in Files (icon strip; entries by item type and location; Shift moves rare entries into the main list; background, trash and server variants) | ✅ | 🟦 | 🟦 | 🟦 | 🟦 | `PaneMenuComposer` + `MenuEntry`/`CommandEntry` (tested) + `PaneMenuView`; costly sub-menus fill on first open |
| Open in other pane; open file location (search, tags); edit tags; per-entry visibility settings | 🟦 | 🟦 | ✓ | ✓ | ✓ | Settings → General → Context menu |
| Run as administrator / as another user | 🟦 | 🟦 | 🟦 | ➖ | ➖ | Windows shell verbs `runas` / `runasuser` for .exe/.com/.bat/.cmd/.msi; not offered elsewhere |
| Create shortcut; create folder with selection; send to | 🟦 | 🟦 | 🟦 | 🟦 | 🟦 | Windows `.lnk` via Windows PowerShell + WScript.Shell; Linux/macOS symbolic link; never overwrites. Send to: the shell's list on Windows, known folders + removable drives elsewhere |
| Shell context menu extensions — "Show more options" (Explorer) | 🟦 | 🟦 | 🟦 | ➖ | ➖ | `WindowsShellMenus`: classic IContextMenu on a per-menu STA thread, lazy sub-menus, entry bitmaps; duplicates of Navigator commands hidden. Linux/macOS have no hostable shell menu |
| Windows 11 app commands in the context menu (plugin) | 🟨 | 🟦 | 🟦 | ➖ | ➖ | Windows 11 app commands plugin: `windows.fileExplorerContextMenus` verbs from the installed packages' manifests (rescanned when packages change), hosted out of process through IExplorerCommand on an MTA worker; added above "Show more options" when ready. Manifest parsing and item-type matching tested; COM hosting untested |
| Set as default file manager (plugin) | 🟨 | 🟦 | 🟦 | 🟦 | ➖ | Default file manager plugin, Settings → Plugins: Windows per-user `Directory`/`Drive` default verb + This PC (Win+E), restored when switched off or removed; Linux `xdg-mime default … inode/directory`. Desktop entry tested; registry and xdg-mime untested. macOS cannot replace Finder |
| Rotate pictures left / right | ✅ | 🟦 | ✓ | ✓ | ✓ | `ImageRotation`: JPEG losslessly through the EXIF orientation (two bytes patched in place; EXIF added when missing; XMP kept in step), PNG pixel-exact re-encode (`PngRotation`, every color type and bit depth, interlaced input). Navigator's thumbnails and previews honor EXIF orientation |
| Set as desktop / lock screen background | 🟦 | 🟦 | 🟦 | 🟦 | 🟦 | Windows `SystemParametersInfo` / WinRT `LockScreen` (via Windows PowerShell); Linux GNOME, Cinnamon, MATE, KDE Plasma, Xfce, LXQt/LXDE (lock screen: GNOME, KDE); macOS `NSWorkspace` (no lock screen API) |
| Install fonts (per user) | ✅ | 🟦 | 🟦 | 🟦 | 🟦 | `FontFileInfo` reads names (tested); Windows: user font folder + HKCU registration + `AddFontResource`; Linux `~/.local/share/fonts` + `fc-cache`; macOS `~/Library/Fonts`. Never replaces an installed file |
| Mount ISO / VHD / DMG | 🟦 | 🟦 | 🟦 | 🟦 | 🟦 | Windows `Mount-DiskImage` (VHD/VHDX behind UAC); Linux `udisksctl loop-setup` + `mount` (ISO/IMG); macOS `hdiutil attach`. Opens the new drive |
| Eject drives and mounted images | 🟦 | 🟦 | 🟦 | 🟦 | 🟦 | Sidebar and Home drive menus. Windows: shell "Eject" verb, `Dismount-DiskImage` for VHDs mounted from Navigator; Linux udisks2 unmount + power-off / loop-delete (or `gio mount -e`); macOS `diskutil eject` |
| Map / disconnect network drives | 🟦 | 🟦 | 🟦 | 🟦 | 🟦 | `DriveActions`: Windows "Map Network Drive" dialog (`WNetConnectionDialog`) and `WNetCancelConnection2`; Linux/macOS "Connect to server" (smb://, afp://, nfs://, sftp://…) through GVfs `gio mount` (credentials asked by Navigator, passed on standard input) or AppleScript `mount volume`; disconnect via `gio mount -u`/`umount`, `diskutil unmount` |
| Share to other apps | 🟦 | 🟦 | 🟦 | 🟦 | 🟦 | Windows: the shell's share sheet (Explorer's `Windows.ModernShare` verb from the item's shell menu); macOS: NSSharingService targets (AirDrop, Mail, Messages…); Linux: mail composer via `xdg-email` |
| Native notifications for finished operations | 🟨 | 🟦 | 🟨 | 🟦 | 🟦 | `notify-send`, `osascript`; Windows in-app toast only |

## Details, preview, properties

| Feature | Logic | UI | W | L | M | Notes |
|---|---|---|---|---|---|---|
| Preview pane: image, text, folder summary, multi-select summary | ✅ | 🟦 | ✓ | ✓ | ✓ | |
| Quick look with the space bar (built-in window; QuickLook, Seer, PowerToys Peek; macOS Quick Look; GNOME Sushi) | 🟦 | 🟦 | 🟦 | 🟦 | 🟦 | `ShellViewModel.ToggleQuickLookAsync`, `QuickLookService`, `QuickLookWindow`; only while the file list has focus |
| Rendered Markdown, PDF, media (audio/video), Office/OpenDocument previews | ✅ | 🟦 | ✓ | ✓ | ✓ | `MarkdownParser`, `PdfInfoReader`, `MediaInfoReader`, `OfficeDocumentReader` (tested on synthetic and real encoder output); audio playback via MCI / afplay / ffplay·mpv·gst-play·paplay; no in-app video playback (a frame + details, Open plays it) |
| Details: size, dates, attributes/permissions, owner | 🟨 | 🟦 | 🟦 | 🟦 | 🟦 | owner via `stat(1)` on Unix |
| Properties window (general, hashes, tags) | 🟦 | 🟦 | ✓ | ✓ | ✓ | |
| Properties → Security (owner, ACL entries; Unix mode editing; extended ACLs) | 🟦 | 🟦 | 🟦 | 🟦 | 🟦 | Windows ACLs read-only here, "Advanced…" opens Explorer's Security page; Unix chmod; getfacl / `ls -le` |
| Properties → Digital signatures | ✅ | 🟦 | 🟦 | 🟨 | 🟦 | `AuthenticodeReader` (tested, also on real Microsoft-signed files); WinVerifyTrust + catalogs (Windows), codesign/pkgutil (macOS); Linux decodes only (status "not verified") |
| Properties → Compatibility (Windows programs) | ✅ | 🟦 | 🟦 | ➖ | ➖ | `CompatibilityLayers` (tested), HKCU AppCompatFlags\Layers |
| Properties → Customize: folder icon | ✅ | 🟦 | 🟦 | 🟦 | 🟦 | desktop.ini (`IniText`, tested) / GIO metadata · KDE .directory / NSWorkspace |

## Search and tags

| Feature | Logic | UI | W | L | M | Notes |
|---|---|---|---|---|---|---|
| Recursive search (wildcards, cancellable, streaming results) | ✅ | 🟦 | ✓ | ✓ | ✓ | `FileSearchEngine` |
| Search inside file contents (opt-in setting) | ✅ | 🟦 | ✓ | ✓ | ✓ | streaming matcher, tested across buffer boundaries |
| OS index search: Windows Search (plugin) | 🟨 | 🟦 | 🟦 | ➖ | ➖ | Windows Search index plugin: indexed folders are searched through OLE DB (`Search.CollatorDSO`, vtable calls on an MTA worker); names (`LIKE`, wildcards) and contents (`CONTAINS`); not indexed or failing → Navigator's own search. SQL generation tested; the provider calls are untested (no Windows in the development environment). Spotlight and Tracker: ⬜ |
| Tags (create, colors, assign, browse by tag) | 🟦 | 🟦 | ✓ | ✓ | ✓ | stored in app data (`TagStore`), not in file system metadata |
| Clear tag(s) from the selection (Edit tags menu) | ✅ | 🟦 | ✓ | ✓ | ✓ | `TagStore.RemoveTag`, `PaneMenuComposer` (tested) |

## Settings, appearance, localization

| Feature | Logic | UI | W | L | M | Notes |
|---|---|---|---|---|---|---|
| All settings pages (12) | 🟦 | 🟦 | ✓ | ✓ | ✓ | |
| Import / export / reset; corrupt-file recovery; migrations | ✅ | 🟦 | ✓ | ✓ | ✓ | `SettingsStore` |
| Light/dark/system theme, accent color, density, font scale | 🟦 | 🟦 | ✓ | ✓ | ✓ | `ThemeService` |
| Mica / Acrylic backdrop | 🟦 | 🟦 | 🟦 | 🟨 | 🟨 | effect depends on platform/compositor |
| Animations full / reduced / off | 🟦 | 🟦 | ✓ | ✓ | ✓ | |
| Customizable keyboard shortcuts | ✅ | 🟦 | ✓ | ✓ | ✓ | `CommandRegistry` |
| Toolbar customization (show, hide, reorder buttons) | ✅ | 🟦 | ✓ | ✓ | ✓ | Settings → Appearance → Toolbar, or right-click the toolbar; `ToolbarLayout` (tested) |
| Search settings from the address bar | ✅ | 🟦 | ✓ | ✓ | ✓ | `SettingsSearch` (tested); setting rows in the omnibar drop-down open the settings window on the page and outline the setting; Enter on a non-path word opens the best match |
| Global shortcut to open Navigator; background mode with tray icon | 🟦 | 🟦 | 🟦 | 🟨 | 🟦 | `GlobalHotkeyController`; X11, and GNOME on Wayland (desktop shortcut); other Wayland desktops: bind `Navigator --activate` |
| Global shortcut works while Navigator is not running | 🟦 | 🟦 | 🟦 | 🟨 | 🟦 | `ShortcutAgentHost` started at sign-in; agent verified end-to-end on X11 (Xvfb + xdotool) |
| English + Ukrainian | ✅ | 🟦 | ✓ | ✓ | ✓ | key parity test |
| High-contrast themes | ⬜ | ⬜ | | | | |

## Updates and distribution

| Feature | Logic | UI | W | L | M | Notes |
|---|---|---|---|---|---|---|
| Update check (GitHub Releases), signed manifest, SHA-256 | ✅ | 🟦 | ✓ | ✓ | ✓ | `Navigator.Updater` |
| Install: Inno Setup / portable swap / AppImage / .app bundle with rollback | 🟨 | 🟦 | 🟦 | 🟦 | 🟦 | script generation tested; end-to-end swap not run |
| Never restart during file operations | 🟦 | 🟦 | ✓ | ✓ | ✓ | |
| Release workflow (version reservation, draft → verify → publish) | 🟦 | ➖ | | | | validated with PyYAML only; first real run pending |
| Packages: setup.exe + portable zip; AppImage + tar.gz; DMG + zip | 🟦 | ➖ | 🟦 | 🟦 | 🟦 | scripts not executed yet |
| DEB / RPM / Flatpak / MSIX / winget / Homebrew | ⬜ | ➖ | | | | |

## Git (plugin)

| Feature | Logic | UI | W | L | M | Notes |
|---|---|---|---|---|---|---|
| Branch in the status bar (ahead/behind), Pull / Push / Publish branch / Fetch | ✅ | 🟦 | 🟦 | ✅ | 🟦 | Git plugin through the installed `git`; status segment menu and a "Git" sub-menu in the folder's background menu; one remote operation per repository at a time; credential helpers' own windows work, terminal prompts never block |
| Git columns: status, last commit, author, commit date | ✅ | 🟦 | 🟦 | ✅ | 🟦 | one `git status --porcelain=v2` per repository shared by all panes and subfolders; commit columns (hidden by default) from one bounded `git log` walk per folder, stopped when every item is found; tested against a real repository |
| Git status badges on icons (M, A, U, D, R, C) | ✅ | 🟦 | 🟦 | ✅ | 🟦 | switchable in Settings → Plugins → Git |

## Not planned for the first release

WebDAV, file system metadata tags (xattr/NTFS streams), smart folders.
