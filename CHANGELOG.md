# Changelog

All notable changes are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and versions follow [Semantic Versioning](https://semver.org/). Release versions are assigned by the release
workflow (`docs/RELEASE.md`); entries are collected under **Unreleased** until then.

## [Unreleased]

### Added

- Plugin system with a plugin shop (Settings → Plugins, or the plug button in the title bar): install, update and
  remove plugins from a signed catalog published with each release, or install a `.navplugin` file after a warning.
  Plugins load after the first frame, each isolated from the app and from other plugins. See `docs/PLUGINS.md`.
- Network plugin: the sidebar's Network section, built into the app and permanent.
- SSH plugin (separate package): browse SFTP servers in panes; saved connections in the sidebar; password and private
  key sign-in; server keys trusted on first use; open, download, upload, new folder, rename, delete.
- FTP plugin (separate package): FTP and FTPS (explicit TLS) servers, anonymous sign-in, self-signed certificates
  trusted per server after a warning; same file actions as SSH.
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
