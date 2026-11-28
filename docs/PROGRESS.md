# Progress

_Last updated: 2026-11-25 (branch `feature/foundation`, based on `development`)._

## Summary

| Layer | State | Verified how |
|---|---|---|
| Navigator.Core | Complete for v1 scope | builds with analyzers (latest-recommended); unit + integration tests pass |
| Navigator.Platform | Complete for v1 scope | builds; Linux paths tested; Windows/macOS code not executed |
| Navigator.Updater | Complete | builds; unit tests pass; no real update performed |
| Navigator.Presentation | Complete for v1 scope | builds against a CommunityToolkit.Mvvm shim; tests pass |
| Navigator.App (Avalonia) | Written | **not compiled** (no NuGet access in the development environment) |
| Plugins (host, SSH, FTP) | Complete for v1 scope | host + plugins build with warnings as errors; unit tests pass; end-to-end against OpenSSH, AsyncSSH, vsftpd, pyftpdlib (see `docs/PLUGINS.md`) |
| ReleaseTool, PerfProbe | Complete | builds; release logic unit-tested; probe run |
| CI / release workflows | Written | YAML parsed; not run on GitHub |
| Packaging scripts | Written | `shellcheck` clean (Linux/macOS); not executed |
| Docs | Complete set | — (`README.md` intentionally untouched per project rule) |

Test status: 256 unit tests passed on 2026-11-25 (shimmed MSTest, Linux; integration tests not re-run) — see `docs/TESTING.md`.
Originality scan: 0 matching code windows against Files app sources — see `docs/SOURCE_ORIGINALITY.md`.

## Done

- Solution structure, central package management, analyzers, style rules, version management.
- File operation engine with all safety rules, conflict handling, pause/resume/cancel, retry/skip, undo/redo.
- Enumeration pipeline with batched first paint; natural sort; grouping; filtering; live updates.
- Settings with source-generated JSON, atomic writes, backups, corruption recovery, migrations, import/export/reset.
- Session restore (windows with bounds, tabs with location, split-pane location and pin state).
- Platform services for Windows, Linux, macOS.
- Updater with signed manifests and per-installation-kind installers with rollback.
- All view models incl. 12 settings pages, command registry with customizable shortcuts, en/uk localization.
- Explorer-style omnibar: sub-folder chevrons, root-locations/overflow button, autocompletion, typed-address
  history, shell names; mouse side buttons and browser keys for back/forward.
- Avalonia UI: design tokens, styles, title bar tabs, omnibar, command bar, sidebar, all layouts,
  preview pane, operations, dialogs, properties and settings windows, motion system, original icons/logo.
- CI and release workflows; Windows/Linux/macOS packaging scripts.
- Documentation set and `CLAUDE.md`.
- Plugin system (`docs/PLUGINS.md`): contract + host with isolated loading, plugin shop in Settings → Plugins
  (signed catalog, install/update/remove, install from file), built-in permanent Network plugin, SSH (SFTP) and FTP
  (FTP/FTPS) plugins as separate packages, server folders in panes (browse, open, download, upload, new folder,
  rename, delete), release pipeline for plugin packages and catalog.
- Pane context menus restructured after the Files app (owner's request): one `MenuEntry` model for item,
  background, trash and server menus; entries built from commands (shown when runnable), filtered per location,
  single-item and Shift rules; icon strip; "Show more options" hosts Explorer's classic menu on Windows; Open with /
  Send to from the shell on Windows, from Navigator elsewhere; new entries: open in other pane, open file location,
  extract files… / here / here (smart) / to folder, unpin, edit tags; visibility settings per entry. Composer and
  shell bridge unit-tested; Avalonia rendering and Windows interop written, not compiled/run here.
- Icons: Material Design Icons through the `Material.Icons.Avalonia` package (replaces the generated Material Symbols
  set); files and folders show the OS icon for their type (Windows shell, Linux icon theme via GIO + gdk-pixbuf, macOS
  NSWorkspace), with a setting to turn it off; icons on every menu item (attached `c:MenuIcon.Kind`), tag colors in
  the Edit tags menu.
- Edit tags → "Clear tag(s)": disabled without tags, one entry for a single tag, a sub-menu per tag otherwise.
- Navigation and layout (2026-11-28): column (Miller) layout, tab tear-off into a new window / onto another window,
  compact overlay and always on top, details column reordering, toolbar customization, settings search from the
  address bar. Path logic (`ColumnTrail`, `ColumnOrder`), toolbar arrangement and settings search are unit-tested
  (tests written; not run in this environment, which had no .NET SDK); the Avalonia parts are written, not compiled.

- Preview and properties (2026-11-28): quick look with the space bar (built-in window; QuickLook, Seer, PowerToys
  Peek, macOS Quick Look, GNOME Sushi), rendered Markdown, PDF/media/Office previews with details, audio playback,
  thumbnails of videos/PDFs/documents/album art with disk caching, Properties tabs Security, Digital signatures,
  Compatibility and Customize (folder icon). Readers (`MediaInfoReader`, `PdfInfoReader`, `OfficeDocumentReader`,
  `AuthenticodeReader`, `MarkdownParser`, `CompatibilityLayers`, `IniText`) are unit-tested (21 tests, run against
  the MSTest shim) and were exercised on real ffmpeg/python-docx/reportlab output and Microsoft-signed DLLs; Core,
  Platform and Presentation build with warnings as errors; the Avalonia views are written, not compiled.

## In progress / next (in order)

1. Build `Navigator.App` with NuGet access and fix compile errors.
2. Run on Windows, Linux, macOS; fix runtime issues; update `FEATURE_PARITY.md` statuses from 🟦 to ✅.
3. Run CI on GitHub; fix platform test failures.
4. Add Avalonia.Headless UI tests and Shell/Pane view model tests.
5. Measure UI performance; run BenchmarkDotNet; record in `PERFORMANCE.md`.
6. Configure release secrets/variables (`docs/RELEASE.md`) and do a first release; test updating.
7. Work through Phase 6 of `docs/IMPLEMENTATION_PLAN.md`.

## Decisions log

| Date | Decision | Reason |
|---|---|---|
| 2026-11-28 | Column layout keeps the current folder as the pane's own list and draws ancestors as read-only columns | every command, the rename editor, drag and drop and virtualization keep working unchanged; navigation stays the single source of truth (history, breadcrumb) |
| 2026-11-28 | Details cells are arranged by `DetailsCellsPanel` from a shared column order | reordering moves existing cells; no per-order templates and no row rebuild |
| 2026-11-24 | View models use explicit properties, no MVVM source generators | predictable build, readable code, no generator dependency |
| 2026-11-24 | Selection lives in `ItemSelection` (view model), not in the list control | container recycling, large-folder performance, testability |
| 2026-11-24 | One virtualized `ItemsControl` over row models for all layouts | single virtualization path; grouping and tiles without nested panels |
| 2026-11-24 | Platform implementations in one assembly, selected at runtime | one build per RID, no conditional compilation in shared code |
| 2026-11-24 | Version reservation by atomic tag push | race-free unique versions without a workflow-level concurrency group |
| 2026-11-24 | Signed update manifest (ECDSA P-256) | asset hashes alone do not protect against a compromised release upload |
| 2026-11-24 | Tags stored in app data | portable across file systems; no xattr/ADS permission issues |
| 2026-11-24 | OS icons through `ISystemIconProvider` (Windows shell image lists, flat API + vtable call, no built-in COM interop) | native look for user folders/drives while staying trimming-safe; other platforms fall back to the own icon set |
| 2026-11-24 | Translucent brushes carry alpha in the color; rows recycle without transitions (`RowSurface`) | `BrushTransition` interpolates color and opacity separately (hover flash); recycled rows faded between items while scrolling |
| 2026-11-24 | Status center summary values are cached per tick; cards raise only changed properties | bindings never enumerate the operation list; idle cards cost nothing |
| 2026-11-24 | Global "open Navigator" shortcut is a registry command flagged `IsGlobal`, registered with the OS by `IGlobalHotkeyService` (Windows RegisterHotKey on its own message thread, macOS Carbon `RegisterEventHotKey`, Linux XCB grab on its own connection; Wayland unsupported) | reuses the shortcut editor and conflict check; no polling, no Accessibility permission on macOS; XCB reports a taken key as a checked error instead of Avalonia's process-wide Xlib error handler |
| 2026-11-24 | Background mode is opt-in (`KeepRunningInBackground`) and uses Avalonia's `TrayIcon` | a global shortcut can only open a closed app if the process stays alive; nothing is created when off |
| 2026-11-24 | Plugins: Network built in (factory, no scanning); others as `.navplugin` packages in their own `AssemblyLoadContext`, loaded after the first frame | zero startup cost for the built-in; plugin dependencies (SSH.NET) never reach the app |
| 2026-11-24 | Plugin updates/removals finish on the next start (non-collectible load contexts) | loaded assemblies cannot be deleted on Windows; collectible contexts are slower and unloading is fragile |
| 2026-11-24 | Plugin shop catalog = release asset signed with the update key | one unauthenticated request, no API rate limit, same trust chain as updates |
| 2026-11-25 | Context menus follow the Files app's structure: declarative `MenuEntry` list + `PaneMenuComposer.Refine` (location, single-item, Shift rules) rendered by `PaneMenuView`; supersedes `ItemMenuPlanner` | owner's request; one model for every pane menu, testable without UI |
| 2026-11-25 | Explorer's classic menu hosted through vtable calls on a dedicated STA thread **per menu**; queried at right-click, consumed by "Show more options" / "Open with" / "Send to" (4 s patience) | shell extensions can hang; one stuck extension never blocks the next menu or the UI thread |
| 2026-11-25 | Icons from `Material.Icons.Avalonia` (owner's request); `IconView` keeps drawing them itself from `MaterialIconDataProvider` data (one parsed geometry per icon, no control per row) | virtualized lists stay as cheap as before; any Material icon name is usable as an icon key |
| 2026-11-25 | File rows use the OS icon per file *type* (`type:.ext` key, one bitmap per type), per-item keys only for types that carry their own icon (programs, shortcuts, `.app`) | a folder of 10,000 PDFs costs one icon; programs still look right |
| 2026-11-25 | Linux OS icons via GIO (shared-mime-info) + own icon-theme lookup + gdk-pixbuf decoding (P/Invoke, no new package) | the desktop's real theme incl. SVG themes; absent libraries → Navigator's icons |
| 2026-11-24 | FTP implemented on the BCL; SSH uses SSH.NET (MIT) inside the plugin package only | FTP is small enough to own; SSH key exchange/ciphers (curve25519, ed25519, chacha20) are not in the BCL |
