# Testing

## Suites

| Project | Kind | Content |
|---|---|---|
| `tests/Navigator.UnitTests` | MSTest, fast, no real disk beyond temp folders | paths and name rules, natural sort, grouping, filters, history, LRU cache, semver, byte formatting, settings store (round trip, corruption, migrations, sanitize, import), session store, selection model (click/Ctrl/Shift/keyboard/rubber band/type-to-select), localization parity, command registry (names, shortcut conflicts), bulk rename planning, previews, permission text, platform parsers (XDG user dirs, `.trashinfo`, Windows `$I` files, `.desktop` files, RIDs, volume filtering), updater (manifest validation, signatures, asset selection, release selection, downloader hash/resume, installation detection, safe extraction), release tool (versioning, labels, asset classification, manifest) |
| `tests/Navigator.IntegrationTests` | MSTest against real temp directories | copy/move/merge/conflicts, cross-volume move verify-then-delete (simulated second volume), cancellation leaves no partial files, pause/resume, permanent delete never follows links, symlink recreation, recycle + undo, rename/case-only rename, create never truncates, queue concurrency, failure isolation, enumeration batches/filters/errors/cancellation, folder statistics, directory monitor coalescing, search (names, content across buffer boundaries, limits, cancellation), archives (round trip, traversal blocked, smart destination), hash vectors, atomic file writes, freedesktop trash list/restore/delete |
| `tests/Navigator.Benchmarks` | BenchmarkDotNet | enumeration, sort, search at 10k/100k entries; comparer; cache |
| `tests/Shared` | helpers | `TempDirectory`, `FakeVolumeLocator`, `FolderTrash`, `FakeHttpHandler` |

Run:

```bash
dotnet test tests/Navigator.UnitTests -c Release
dotnet test tests/Navigator.IntegrationTests -c Release
dotnet test Navigator.slnx -c Release --filter "FullyQualifiedName~Settings"   # subset
```

CI (`ci.yml`, triggered by pull requests into `release` or manually) runs both suites on ubuntu-22.04, windows-latest and macos-latest, uploads TRX results, and
publishes all six RIDs as a smoke test. `release.yml` repeats the tests before building packages.

## Results so far (honest record)

| Date | Environment | What ran | Result |
|---|---|---|---|
| 2026-11-24 | Ubuntu 24.04 container, .NET 10.0.15, **no nuget.org access** | All unit + integration tests compiled against a minimal MSTest-compatible shim (attributes + `Assert` subset) and the presentation layer against a CommunityToolkit.Mvvm shim; run three times | **124 passed, 0 failed** (each run) |
| 2026-11-25 | Ubuntu 24.04 container, .NET 10.0.115, **no nuget.org access** | New `ItemMenuPlannerTests` run against the same kind of shims (full suite not re-run in this session) | **11 passed, 0 failed** |
| 2026-11-25 (later) | same | Whole `Navigator.UnitTests` suite (incl. new `PaneMenuTests`, `ExtractDestinationTests`; `ItemMenuPlannerTests` removed) against MSTest / CommunityToolkit.Mvvm / Logging shims | **245 passed, 0 failed** |
| 2026-11-25 (icons, tags) | same | Whole `Navigator.UnitTests` suite incl. new `SystemIconTests` and Clear-tag tests; Linux OS-icon provider also run for real against Adwaita/Humanity themes (GIO, gdk-pixbuf, librsvg loader) | **256 passed, 0 failed** |
| 2026-11-28 (preview, properties) | Ubuntu 24.04 container, .NET SDK 10.0.404, **no nuget.org access** | Whole `Navigator.UnitTests` suite incl. new `PreviewAndPropertiesTests` (media, document, PDF, Authenticode, compatibility, INI, Markdown) against MSTest / CommunityToolkit.Mvvm shims; readers also run on real files (ffmpeg-encoded MP3/FLAC/Ogg/Opus/WAV/M4A/MP4/MOV/MKV/WebM/AVI with album art, python-docx/openpyxl/python-pptx/odfpy/reportlab documents, Microsoft-signed DLLs) and the Linux ffmpeg thumbnail path | **328 passed, 1 failed** — the failure is `PluginApi_MatchesTheVersionPackagedPluginsAreBuiltAgainst`, which looks for the repository root above its output folder (the shim build lives outside the repository) |

Caveats:

- The shim is not MSTest. CI is the first run with the real framework; differences (e.g. assertion
  messages, test discovery) may surface there.
- Windows and macOS test runs have not happened yet.
- The Avalonia app (`src/Navigator.App`) could not be compiled; no UI test was run.

## Not yet covered

| Area | Plan |
|---|---|
| UI (views, bindings, keyboard routing, drag and drop) | `Avalonia.Headless` + MSTest project `tests/Navigator.UiTests` (package already pinned in `Directory.Packages.props`) |
| `ShellViewModel` / `PaneViewModel` flows (tabs, navigation, load pipeline) | unit tests with `ImmediateDispatcher` and a temp folder |
| TAR archives, Windows Recycle Bin, macOS trash | platform-conditional integration tests (run in CI matrix) |
| Updater swap scripts end-to-end | CI job that installs release N and updates to a locally built N+1 |
| Stress | 1M-entry folder, 10k-file copy with cancellation at random points, watcher storms |

## Rules

- Never mark a feature as done in `docs/FEATURE_PARITY.md` without a passing test or a documented manual check.
- Never record benchmark or test results that were not actually produced.
- Tests must clean up their temp folders (`TempDirectory` does) and must not touch the user's real
  settings — use `NAVIGATOR_DATA_DIR` or the store constructors that take a directory.
