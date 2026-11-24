# Changelog

All notable changes are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and versions follow [Semantic Versioning](https://semver.org/). Release versions are assigned by the release
workflow (`docs/RELEASE.md`); entries are collected under **Unreleased** until then.

## [Unreleased]

### Added

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
- Packaging: Windows installer + portable ZIP, Linux AppImage + tar.gz, macOS DMG + ZIP.
- Documentation set and source-originality scanner.

### Changed

- Tabs all have the same width and shrink equally when the title bar fills up, instead of sizing to their titles.
- A tab in split view shows both pane titles separated by "|" (left first).
- The Recycle Bin / Trash moved into the sidebar's quick access (pinned) section.

### Fixed

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
