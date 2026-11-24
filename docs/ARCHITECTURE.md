# Architecture

Navigator is a layered .NET 10 application. Everything below the UI layer is plain .NET (BCL only, plus
CommunityToolkit.Mvvm and logging abstractions in Presentation), so the engines and view models are
testable without a display and the UI framework is replaceable.

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ Navigator.App (Avalonia 12)                                                  │
│  CompositionRoot · Views/AXAML · design tokens · ThemeService · UI services  │
└───────────────▲──────────────────────────────────────────────────────────────┘
                │ binds to (compiled bindings)
┌───────────────┴──────────────────────────────────────────────────────────────┐
│ Navigator.Presentation (view models, no Avalonia)                            │
│  Shell · Tabs · Panes · Items/rows · Sidebar · Details · Operations ·        │
│  Settings pages · Commands · Localization · Updates                          │
└───────▲───────────────────────▲───────────────────────────▲──────────────────┘
        │                       │                           │
┌───────┴────────┐   ┌──────────┴─────────┐   ┌─────────────┴──────────────────┐
│ Navigator.Core │◄──┤ Navigator.Platform │   │ Navigator.Updater              │
│ domain/engines │   │ Win/Linux/macOS    │   │ GitHub Releases, manifest,     │
│ + contracts    │   │ implementations    │   │ signature, download, install   │
└────────────────┘   └────────────────────┘   └────────────────────────────────┘
```

## Projects

| Project | Depends on | Responsibility |
|---|---|---|
| `Navigator.Core` | BCL | Paths, enumeration, sorting/grouping, navigation history, file operation engine, undo/redo, search, directory monitoring, LRU cache, background scheduler, settings + migrations + atomic persistence, archives, hashing, tags, semantic versions, platform contracts (`IKnownFolderProvider`, `IVolumeProvider`, `ITrashService`, `IFileLauncher`, …). |
| `Navigator.Platform` | Core | Per-OS implementations compiled into one assembly and selected at runtime (`PlatformServices`). P/Invoke is isolated in `Native/` and the per-OS classes. |
| `Navigator.Updater` | Core | Release discovery, manifest parsing/validation, ECDSA signature verification, resumable verified download, per-installation-kind installers with rollback. |
| `Navigator.Presentation` | Core, Platform contracts, Updater, CommunityToolkit.Mvvm, M.E.Logging.Abstractions | All view models and UI-independent services. Owns selection, row models and the localization catalog. |
| `Navigator.Plugins.Abstractions` | Core | The plugin contract (`INavigatorPlugin`, `IPluginContext`, sidebar sections, `IRemoteFileSystem`, plugin dialogs, `PluginApi` version) and shared helpers for server plugins (saved connections, credential cache, connection pool). |
| `Navigator.Plugins` | Core, Updater, Plugins.Abstractions | Plugin host: built-in plugins, isolated loading of installed packages (one `AssemblyLoadContext` each), install/update/remove, plugin shop client (signed catalog). |
| `Navigator.App` | everything above + Avalonia + `plugins/Navigator.Plugin.Network` | Window chrome, views, design system, UI service implementations (dispatcher, dialogs, clipboard, thumbnails, windows, theme). |
| `plugins/Navigator.Plugin.*` | Plugins.Abstractions | Network (built into the app, permanent), SSH and FTP (separate `.navplugin` packages, installed from the shop). See `docs/PLUGINS.md`. |
| `tools/Navigator.ReleaseTool` | Core, Updater, Plugins | Version computation, asset classification, checksums, manifest and plugin catalog generation, signing; used by `release.yml`. |

## Key design decisions

1. **Explicit MVVM properties, no source generators.** View models use `ObservableObject.SetProperty`
   explicitly. This keeps build-time cost and trimming behavior predictable and makes the code easy to
   read without generator output.
2. **Selection lives in the view model** (`ItemSelection`), not in the list control. This lets the
   virtualized views recycle containers freely, makes range/toggle/invert selection O(1)/O(n) without
   touching UI objects, and makes all selection logic unit-testable.
3. **Rows model.** Every layout (details, list, tiles, grid) is rendered by one virtualized
   `ItemsControl` + `VirtualizingStackPanel` over a flat list of row view models
   (`FileItemViewModel`, `GroupHeaderViewModel`, `TileRowViewModel`). Tile/grid layouts chunk items
   into rows sized from the viewport width. Grouping is a header row, so it costs nothing extra to virtualize.
4. **Load pipeline.** Directory listing runs on a background thread (`DirectoryEnumerator.EnumerateBatchesAsync`).
   The first batch (96 entries) is published immediately so the view is never blank; later batches are
   accumulated (2048 entries or 60 ms) and a sorted snapshot is published off the UI thread at most every
   250 ms. Navigating away cancels the load.
5. **Incremental watcher updates.** `DirectoryMonitor` coalesces `FileSystemWatcher` events (debounce,
   rename pairing, overflow → full refresh). `PaneViewModel.ApplyChanges` inserts/removes with a binary
   search on the current sort (`EntrySorter.FindInsertIndex`) and falls back to a rebuild only when grouping
   or bulk changes make that cheaper.
6. **Lazy item work.** Display strings (size, date, type) are computed on first access; thumbnails are
   requested only for realized rows and cancelled when rows are recycled; folder sizes are opt-in and run on
   the background scheduler at low priority.
7. **File operation engine** (`Navigator.Core.Operations`): a queue with bounded concurrency, per-operation
   pause gate and cancellation, progress sampled by polling (no per-byte events), conflict policies with
   "apply to all", retry/skip/cancel recovery, undo/redo records. Safety rules are enforced in
   `SafeFileCopier`/`TransferExecutor`:
   - writes go to `.{name}.{id}.navigator-partial` and are atomically renamed into place;
   - an existing destination is never replaced without an explicit conflict decision;
   - same-volume moves are renames; cross-volume moves copy → verify (size, optional hash) → delete source;
   - symbolic links are recreated, never followed; a source folder is removed only when all children moved;
   - read-only/system/permission failures surface as classified errors (`IoErrorClassifier`).
8. **Settings.** `AppSettings` (schema version 1) is serialized with `System.Text.Json` source generation.
   `AtomicFile` writes through a temp file and keeps a `.bak`. Unreadable files are preserved as
   `*.corrupt-<timestamp>` and defaults are used. Migrations implement `ISettingsMigration` and run in order.
   Import validates and sanitizes before applying.
9. **Platform abstraction.** Shared code talks to interfaces in `Navigator.Core.Platform`. Implementations:
   Windows (Recycle Bin via `SHFileOperationW`, `$I` parsing for restore; `SHGetKnownFolderPath`),
   Linux (freedesktop.org Trash spec 1.0 incl. per-volume `.Trash-$uid`, XDG user dirs, `.desktop`
   handlers + `mimeapps.list`), macOS (`NSFileManager trashItemAtURL` via `objc_msgSend`, `open`).
10. **Updater.** See `docs/UPDATER.md`. The updater refuses to restart while operations run
    (`TryRestartToUpdate(canRestart)`), and each installation kind has its own swap strategy with rollback.
11. **Plugins.** See `docs/PLUGINS.md`. Sidebar sections and file systems for plugin addresses (`sftp://`, `ftp://`)
    come from plugins. The only built-in plugin (Network) is constructed directly, so plugins add nothing before the
    first frame; installed packages load afterwards on a worker thread, each in its own `AssemblyLoadContext`.
    Contributions are published as immutable snapshots, so readers never lock. Plugin addresses are a separate
    `LocationKind.Remote`; the local file engine never receives one, which keeps its safety rules intact.
    Third-party libraries a plugin needs (SSH.NET for SSH) ship only inside that plugin's package, never in the app.

## Threading model

- UI thread: view model property changes visible to bindings, collection mutations of bound collections.
- Background: enumeration, sorting of snapshots, search, hashing, thumbnails decode, folder sizes,
  file operations, update download. `IUiDispatcher` posts results back; `BackgroundWorkScheduler`
  provides priorities (Visible/Normal/Background) with bounded parallelism so thumbnails never starve navigation.
- All background work takes a `CancellationToken`; navigation, tab close and app shutdown cancel it.

## Dependency injection

`Navigator.App/Infrastructure/CompositionRoot.cs` registers singletons for engines and services and
transient factories for per-tab/per-pane view models. No service locator is used inside view models;
`App.Services` is only used by the Avalonia entry points.

## Logging

`Microsoft.Extensions.Logging` with a rotating file provider (`RotatingFileLoggerProvider`, daily files, at most 7 × 5 MB)
in the app data `logs` folder. Level is switchable at runtime from Settings → Advanced. Paths are logged,
file contents never are.
