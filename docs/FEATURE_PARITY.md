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
| Cloud drives (OneDrive, Google Drive, iCloud…) | ⬜ | ⬜ | | | | appear only as ordinary folders/volumes |
| WSL distributions | ⬜ | ⬜ | | ➖ | ➖ | |

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
| 7z / RAR / other archive formats | ⬜ | ⬜ | | | | |
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
| Shell context menu extensions — "Show more options" (Explorer) | 🟦 | 🟦 | 🟦 | ➖ | ➖ | `WindowsShellMenus`: classic IContextMenu on a per-menu STA thread, lazy sub-menus, entry bitmaps; duplicates of Navigator commands hidden. Windows 11 packaged-app commands (IExplorerCommand) are not hosted yet. Linux/macOS have no hostable shell menu |
| Set as default file manager | ⬜ | ⬜ | | | | Linux desktop file declares `inode/directory` |
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
| OS index search (Windows Search, Spotlight, Tracker) | ⬜ | ⬜ | | | | |
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

## Not planned for the first release

Git integration, FTP/SFTP/WebDAV, cloud provider APIs, Windows shell extensions, file system
metadata tags (xattr/NTFS streams), smart folders.
