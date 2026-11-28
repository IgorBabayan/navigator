# Changelog

All notable changes are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and versions follow [Semantic Versioning](https://semver.org/). Release versions are assigned by the release
workflow (`docs/RELEASE.md`); entries are collected under **Unreleased** until then.

## [Unreleased]

### Added

- Quick look with the space bar (Settings → Files and folders → Quick look). With "Automatic" a running external
  previewer gets the selected file — QuickLook (QL-Win, through its pipe, following the selection), Seer or PowerToys
  Peek on Windows, Quick Look (`qlmanage`) on macOS — and Navigator's own Quick Look window is used otherwise. Each can
  also be chosen explicitly (GNOME Sushi on Linux). The built-in window shows the same preview as the pane, larger:
  arrow keys move through the folder, Enter opens the file, Space or Escape closes it. The space bar keeps its usual
  meaning on buttons, check boxes, in text boxes and while a type-to-select prefix is being typed.
- Richer previews (preview pane and Quick Look): rendered Markdown (headings, emphasis, code, quotes, lists, tables);
  PDFs (first page from the platform, plus version, pages, title, author, producer); videos (a frame plus duration,
  resolution, codecs, bit rate) and audio (album art, tags, duration, sample rate, channels, with Play/Stop) for MP3,
  FLAC, WAV, Ogg/Opus, MP4/M4A/MOV, Matroska/WebM and AVI; Office Open XML and OpenDocument files (the saved page
  thumbnail or the platform's, plus title, author, pages/words/slides/sheets and the text of the first pages or
  cells). Media, PDF and document details are read from headers only (a few small reads, no extra dependency).
- Thumbnails of videos, PDFs, documents and album art in icon and tile layouts (setting, on by default): embedded
  pictures first, then the Windows shell's thumbnail handlers, macOS Quick Look, or on Linux the freedesktop.org shared
  thumbnail cache, `pdftoppm` and `ffmpegthumbnailer`/`ffmpeg`. Every result, including "none", is kept in the
  thumbnail disk cache; details and list rows keep the type icon so scrolling long lists starts no extra work.
- Properties window tabs, each loaded only when opened: **Security** (owner, group, Windows access rules with a link
  to the system editor; Unix permissions editable as check boxes, extended ACL entries via `getfacl` / `ls -le`),
  **Digital signatures** (WinVerifyTrust offline on Windows including catalog-signed system files, codesign/pkgutil
  on macOS, Authenticode decoding elsewhere: signer, issuer, signing time, digest, certificate path), **Compatibility**
  for Windows programs (compatibility mode, reduced color, 640 × 480, fullscreen optimizations, run as administrator,
  high-DPI override — the same per-user AppCompat layers Explorer writes, other layers kept) and **Customize** for
  folders (change or restore the folder icon: desktop.ini on Windows, GIO metadata or KDE .directory on Linux,
  NSWorkspace on macOS). On Windows, folders with a custom icon now show it in Navigator's lists too.

- Column (Miller) layout (Layout → Columns, Ctrl+Shift+6): the current folder is the rightmost column with the full
  list (selection, menus, rename, drag and drop), and each folder on the way to it gets a column on its left with the
  open entry highlighted. Clicking a folder (or Right) opens it as the next column, Left goes back one column, a
  click in an earlier column continues from there. Going deeper reuses the listing just left; other columns come from
  the directory cache or one background listing, so stepping through columns does not re-read folders.
- Tabs can be dragged out of the tab strip: released away from it, the tab opens in a new window at the pointer;
  released over another window's tab strip, it joins that window there. The tab keeps its panes, history and
  selection. The tab menu has "Move to new window" for the same.
- Compact overlay: a small window that stays above other applications (toolbar button, More menu, command
  palette). The sidebar is hidden while it is on; leaving it restores the window's size, position and state. "Always on
  top" is available on its own as well.
- Details view columns can be reordered by dragging a header sideways, or with the header menu (move left / right,
  reset order). The order is saved; rows only re-arrange their cells, nothing is rebuilt.
- Toolbar customization (Settings → Appearance → Toolbar, or right-click the toolbar): show, hide and reorder the
  command bar buttons and separators and the view buttons; reset to the default arrangement. Download/Upload on
  server folders, the operations ring and the update badge keep appearing on their own.
- Settings can be searched from the address bar: typed words that are not a path list matching settings ("hidden",
  "accent", "toolbar"); choosing one opens the settings window on its page and outlines the setting. Enter on a
  word that is not a place opens the best matching setting instead of reporting a missing path.

- In-place rename: F2 (or Rename in the menu / toolbar) edits the item's name right in the list, in every layout,
  instead of opening a dialog. The stem is selected; Enter or clicking elsewhere commits, Escape cancels, Tab /
  Shift+Tab commit and move on to the next / previous item. An unusable name turns the box red and Enter shows why.
  With extensions hidden, only the shown name is edited and the extension is kept. One editor serves the whole pane
  (rows stay plain text), so the list costs nothing extra to scroll.
- Multi-rename (Ctrl+R, or Rename with several items selected), modelled on Total Commander's Multi-Rename Tool: name
  and extension masks with placeholders ([N] name, [E] extension, character ranges such as [N2-5], [N3,4], [N-3-],
  [C] counter with start/step/digits or inline [C10+5:3], [P]/[G] parent folders, [Y][M][D][h][m][s] date and time
  modified, [U]/[L]/[F]/[n] case from that point), search and replace (plain with several "a|b" pairs, or regular
  expressions with $1 references), case conversion, and a live, virtualized preview that flags invalid names,
  clashes inside the batch and names already taken. The batch is ordered so chains (a→b, b→c) work, renamed on a
  worker thread, and undone or redone as a single step. The last settings are remembered for the session.
- Windows "Open with": the sub-menu lists the applications Windows recommends for the type (default first, with
  their icons), followed by the shell's own "Search the Microsoft Store" / "Choose another app" entries. Applications
  are launched through their own association handler, so packaged (Store) apps work too.

- Windows: paste files copied in other applications. Files copied or cut in Explorer are pasted through the usual
  copy/move queue (a cut empties the clipboard afterwards, like Explorer). Files copied inside a Remote Desktop
  session (and other clipboard-only files such as zip contents or mail attachments) are streamed straight into the
  folder in 1 MiB chunks, with no temporary copy; taken names get a " (2)" suffix and a failed or cancelled item
  leaves no partial file. The context menu's Paste state is probed without fetching clipboard data.
- Drag and drop everywhere: between the panes of a tab, between tabs (rest on a tab to switch to it, or drop on the
  tab to put the items in its active folder), onto the sidebar (drop folders on the pinned group's header or between
  pinned entries to pin them; drop items on a pinned folder, drive or server entry to copy/move/upload them), out to
  other applications (real files) and in from other applications. Folders under the pointer are outlined, the list
  scrolls near its edges, and a folder can never be dropped into itself. Files coming from another application are
  always reported back to it as copied, so the source never deletes originals that Navigator is still moving.
- Drop overlay: while files are dragged over a pane, a dashed, tinted frame and a card ("Move to “Documents”",
  "Copy to…", "Upload to…", with the Ctrl/Shift hint) show where the items will land. Tabs and the pinned group's
  header light up with the accent color when they accept the drop.
- Recycle Bin / Trash in the sidebar: its context menu starts with "Empty trash" (with confirmation), followed by a
  separator and the other actions. The item is disabled while the trash is empty; the state is re-checked off the UI
  thread when the menu opens. Linux and macOS now answer "is the trash empty?" cheaply too, so the full-bin icon
  works there as well.
- Plugin system with a plugin shop (Settings → Plugins, or the plug button in the title bar): install, update and
  remove plugins from a signed catalog published with each release, or install a `.navplugin` file after a warning.
  Plugins load after the first frame, each isolated from the app and from other plugins. See `docs/PLUGINS.md`.
- Network plugin: the sidebar's Network section, built into the app and permanent. On Windows it has a "Network" entry
  that browses computers on the local network and their shared folders (sign-in when a computer asks for it).
- SSH plugin (separate package): browse SFTP servers in panes; saved connections in the sidebar; password and private
  key sign-in; server keys trusted on first use; open, download, upload, new folder, rename, delete.
- FTP plugin (separate package): FTP and FTPS (explicit TLS) servers, anonymous sign-in, self-signed certificates
  trusted per server after a warning; same file actions as SSH.
- Cloud group in the sidebar, right below Network, with two new plugins (separate packages): Google Drive and
  OneDrive. Sign in through the browser (OAuth with PKCE), several accounts per service; browse, download (Google Docs
  as Office files), upload, new folder, rename, delete to the service's trash. Sign-ins are kept in the OS keychain.
- The global shortcut also opens Navigator when it is not running (Settings → General → "Works when Navigator isn't
  running", on by default): a tiny agent started at sign-in holds the key; on GNOME/Wayland a desktop shortcut runs
  `Navigator --activate`. A second launch with `--activate` brings the running Navigator to the front.
- Plugin API 1.2: sidebar groups, plugin secrets, open in browser, cloud helpers.
- Releases publish `navigator-plugin-*.navplugin` packages and `plugin-catalog.json` (+ `.sig`).
- Solution foundation: .NET 10, Avalonia 12, central package management, analyzers, CI and release workflows.
- Core engines: batched directory enumeration, natural sorting, grouping, filtering, navigation history,
  LRU cache, prioritized background scheduler, directory monitoring with coalescing, recursive name and
  content search, ZIP/TAR archives, hashing, tags.
- Safe file operation engine: queue with concurrency limit, pause/resume/cancel, conflict resolution,
  retry/skip, atomic temp-file copies, verified cross-volume moves, link-safe deletes, undo/redo.
- Settings with source-generated JSON, atomic writes, backups, corruption recovery, migrations,
  import/export/reset; session restore.
- Platform services for Windows, Linux and macOS (known folders, volumes, trash, launchers, notifications).
- Automatic updater using GitHub Releases with signed manifests, SHA-256 verification and rollback.
- User interface: tabs in the title bar, omnibar, command bar, sidebar, home page, details/list/tiles/icon
  layouts, split view, preview pane, operations flyout, properties and settings windows, command palette,
  light/dark themes, accent colors, density, Mica/Acrylic, motion levels, original icon set and logo.
- English and Ukrainian localization.
- Settings → Keyboard shortcuts: search by command name, category or shortcut; record a shortcut by pressing it;
  per-command reset (shows the default) and "Reset all shortcuts"; conflicts are flagged on the affected rows;
  invalid input is rejected with a message instead of being saved.
- Settings now expose: font family, startup locations (for "Open specific locations"), sidebar visibility,
  clearing address history / remembered folder views / the skipped update version.
- "Show performance diagnostics" shows a memory bar in the status bar in the style of the JetBrains IDE memory
  indicator: managed heap in use of committed heap ("123 of 456M"); click it to run a garbage collection, hover it
  for frame rate, compositor/layout/input timings, visual count, working set and GC counts. It reads Avalonia's own metrics and measures nothing while it is off.
- "Show labels on toolbar buttons" works: off makes New/Sort/Group/Layout icon-only (with tooltips).
- "Show check boxes for selecting items": a check box in front of each item (Details, List, tiles) toggles it in
  the selection; Shift+click on it adds a range.
- Thumbnail disk cache (Settings → Performance): thumbnails of images over 256 KB are kept as small PNGs, bounded
  by the configured size (oldest removed first), with a "Clear" button.
- Recently visited folders kept in memory (Settings → Performance): revisiting a folder shows its items at once
  and refreshes them in the background; unchanged items keep their rows, thumbnails and selection.
- Packaging: Windows installer + portable ZIP, Linux AppImage + tar.gz, macOS DMG + ZIP.
- Global shortcut "Open Navigator from any app" (default Ctrl+Shift+Alt+E, Cmd+Shift+Option+E on macOS): brings
  Navigator to the front from any application, or opens a window. Change it in Settings → Keyboard shortcuts;
  Settings → General shows whether it is active and why not (taken by another app, Wayland session).
  Windows: RegisterHotKey; macOS: Carbon hot keys (no Accessibility permission); Linux X11: XCB key grab.
- "Keep running in the background" (Settings → General, off by default): after the last window closes Navigator
  stays in the notification area (Open / Exit) so the global shortcut opens it instantly; the managed heap is
  compacted once when it goes idle.
- Documentation set and source-originality scanner.

- Status center for file operations, opened from a toolbar button that appears while operations exist: a progress
  ring shows overall progress (a badge shows the outcome once everything finished); the flyout lists every operation
  with its kind, route ("From A to B"), state and percentage, a tone-colored progress bar, pause/resume, cancel,
  per-card dismiss, "Cancel all" and "Clear finished", and expandable details with a live throughput graph, speed,
  time left, item count and the current item. Replaces the status bar "File operations" button.

### Changed

- Ctrl+R now opens multi-rename; Refresh stays on F5 (both can be reassigned in Settings → Keyboard shortcuts).
- Tabs all have the same width and shrink equally when the title bar fills up, instead of sizing to their titles.
- A tab in split view shows both pane titles separated by "|" (left first).
- The Recycle Bin / Trash moved into the sidebar's quick access (pinned) section.
- Selection follows the accent color everywhere: selected rows get an accent outline, the selected sidebar entry an
  accent tint, and text selection / focused text boxes use the accent with a contrasting selected-text color.
  With "Use system accent", changing the OS accent while Navigator runs updates the UI immediately.

- Hover and selection fades are smooth: translucent brushes carry their alpha in the color (a fade from transparent
  used to overshoot to ~6× the target darkness mid-way and read as a blink), command buttons fade instead of snapping,
  hovering a selected row deepens its fill instead of dimming the whole row, and the tab close button fades in.
- Progress bars and the progress ring ease between the 10 Hz progress updates instead of stepping.

### Removed

- The "Enable experimental features" setting (it controlled nothing); the stored value is dropped on next save.

### Fixed

- The global "open Navigator" shortcut now brings Navigator in front of other apps on Windows (it was blocked by
  the foreground lock). Its description says when it can also open Navigator (background mode).
- The global shortcut now also gets past the foreground lock when the app in front refused the first attempt (a no-op
  input claims the right to switch, with a task-switcher fallback). Settings → Keyboard shortcuts shows on the
  "Open Navigator from any app" row whether the shortcut is active, taken by another app, or why it cannot work.
- Rows and tiles no longer blink while scrolling: a recycled container switched its hover/selection state with a fade
  from the previous item's state. `RowSurface` turns transitions off while virtualization assigns a new item.
- Clearing the search box while search results are shown returns to the searched folder (a Back step when it
  came from there); Back/Forward into results shows their query in the search box again.
- The performance overlay (Avalonia's renderer debug overlay) covered the title bar and search box and forced
  continuous redraws; it is replaced by the status bar indicator.
- Shortcuts with digit or punctuation keys were not reliably recognized: a bare `1` or `` ` `` is not an Avalonia
  key name (`Ctrl+Shift+1…5`, ``Ctrl+Shift+` ``). Gestures are now normalized to key names (`D1`, `OemTilde`, …)
  and shown in friendly form (`1`, `` ` ``, `,`).
- Settings: after "Reset all settings" or "Import", tag and shortcut rows kept editing the replaced settings object.
- Title bar: the default "Window" caption text no longer appears above the tabs; the window is titled "Navigator".
- Title bar: the drawn decorations' opaque background no longer shows through the tabs, so the selected tab has one
  color; the navigation toolbar is now full-width and flush with the tab strip.
- Shutdown: `ObjectDisposedException` from `PreviewViewModel.Dispose`. Window shells were resolved as tracked
  transients from the root container, so they were disposed on window close and again at shutdown (and every closed
  window's shell stayed alive until exit). Shells are now created untracked, and `Dispose` is idempotent.
- Shutdown: `InvalidOperationException` ("type only implements IAsyncDisposable"). The service container is now
  disposed asynchronously with a bounded wait, so `FileOperationQueue` and `BackgroundWorkScheduler` shut down cleanly.

- Split view: after a session restore neither pane showed the active-pane accent; the indicator is now style-based, so
  it resolves even when the pane is created before it joins the window.
- Omnibar: on Home (and Trash/tag views) clicking the address bar did nothing, and clicking the "Home" crumb navigated
  to an empty path. Every location can now be replaced by typing a path; the Home crumb opens the address box.
- Drives without a label are named like File Explorer: "Local Disk (C:)", "Removable Disk (E:)", …
- Sidebar section headers (Pinned, Drives, Tags): expander glyph and text are vertically centered; same for Home tag chips.

### Changed

- Sidebar section headers have icons; on Windows the sidebar and Home use the shell's own icons for user folders
  (Desktop, Downloads, …), drives and "This PC" (rendered once off the UI thread and cached). Other platforms keep
  Navigator's icon set.
- Sidebar drives no longer show a usage bar (free space is in the tooltip; Home still shows it).

### Known limitations

See `docs/KNOWN_ISSUES.md`. The UI layer has not been compiled or run yet.
