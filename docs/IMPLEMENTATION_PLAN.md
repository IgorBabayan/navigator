# Implementation plan

Phases are ordered by the project priorities (originality → performance → safety → parity → platforms).
Status is tracked in `docs/PROGRESS.md`; feature-level status in `docs/FEATURE_PARITY.md`.

## Phase 0 — Foundation ✅ (code written; UI unverified)

Solution layout, central package management, analyzers, `.editorconfig`, CI and release workflows,
version management, packaging scripts, documentation set.

## Phase 1 — Core engines ✅

Paths, enumeration pipeline, sorting/grouping, navigation history, caching, scheduler, settings with
migrations and corruption recovery, file operation engine with safety rules, undo/redo, search, directory
monitoring, archives, hashing, tags.

## Phase 2 — Platform layer ✅ (Linux tested, Windows/macOS unexecuted)

Known folders, volumes + change detection, trash on three platforms, launchers, notifications, ownership.

## Phase 3 — Presentation ✅

Shell/tabs/windows, panes with virtualized row model, selection, sidebar, home, preview/details,
operations, command registry and palette, settings pages, localization (en/uk), updater view model.

## Phase 4 — Avalonia UI 🟦 (written, not compiled)

Design tokens, control styles, title bar with tabs, omnibar, command bar, sidebar, pane views for all
layouts, preview pane, operations flyout, toasts, dialogs, properties and settings windows, theme/backdrop/
density/motion service, original icon set.

**Next step:** restore packages, fix compile errors, run on all three OSes, then mark items in
`FEATURE_PARITY.md` as validated.

## Phase 5 — Verification and hardening ⏭ next

1. Build the full solution with NuGet access; fix Avalonia compile/runtime issues.
2. Run CI on GitHub (real MSTest, Windows/macOS).
3. Add `tests/Navigator.UiTests` (Avalonia.Headless): window creation, navigation, keyboard shortcuts,
   selection, rename flow, drag between panes, settings round trip.
4. Add view model tests for `ShellViewModel`/`PaneViewModel`.
5. Measure startup, memory and scrolling (`docs/PERFORMANCE.md`), run BenchmarkDotNet suite.
6. First release from `release` branch with update signing key configured; test update N → N+1 on every
   installation kind.

## Phase 6 — Parity gaps (by user impact)

1. OS file clipboard + drag and drop with other apps (Avalonia `DataTransfer` file formats).
2. Breadcrumb flyouts and address autocompletion.
3. Rubber band over virtualized items (geometry-based selection on the row model).
4. Thumbnail disk cache, video/PDF thumbnails via OS APIs (Windows shell, freedesktop thumbnail spec, QuickLook).
5. Column reordering, tab tear-off, column view.
6. Windows in-app "Open with" list (registry `OpenWithProgids`), macOS handler enumeration (Launch Services).
7. Accessibility pass (automation names, high contrast, OS reduce-motion).
8. Additional archive formats, rendered Markdown/PDF preview.
9. Git status column, network browsing, cloud drive detection.

## Phase 7 — Distribution extras

DEB/RPM/Flatpak, MSIX/winget, Homebrew cask, universal macOS binary.
