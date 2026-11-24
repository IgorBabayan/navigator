# Progress

_Last updated: 2026-11-24 (branch `feature/foundation`, based on `development`)._

## Summary

| Layer | State | Verified how |
|---|---|---|
| Navigator.Core | Complete for v1 scope | builds with analyzers (latest-recommended); unit + integration tests pass |
| Navigator.Platform | Complete for v1 scope | builds; Linux paths tested; Windows/macOS code not executed |
| Navigator.Updater | Complete | builds; unit tests pass; no real update performed |
| Navigator.Presentation | Complete for v1 scope | builds against a CommunityToolkit.Mvvm shim; tests pass |
| Navigator.App (Avalonia) | Written | **not compiled** (no NuGet access in the development environment) |
| ReleaseTool, PerfProbe | Complete | builds; release logic unit-tested; probe run |
| CI / release workflows | Written | YAML parsed; not run on GitHub |
| Packaging scripts | Written | `shellcheck` clean (Linux/macOS); not executed |
| Docs | Complete set | — (`README.md` intentionally untouched per project rule) |

Test status: 124 automated tests passed (shimmed MSTest, Linux) — see `docs/TESTING.md`.
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
- Avalonia UI: design tokens, styles, title bar tabs, omnibar, command bar, sidebar, all layouts,
  preview pane, operations, dialogs, properties and settings windows, motion system, original icons/logo.
- CI and release workflows; Windows/Linux/macOS packaging scripts.
- Documentation set and `CLAUDE.md`.

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
| 2026-11-24 | View models use explicit properties, no MVVM source generators | predictable build, readable code, no generator dependency |
| 2026-11-24 | Selection lives in `ItemSelection` (view model), not in the list control | container recycling, large-folder performance, testability |
| 2026-11-24 | One virtualized `ItemsControl` over row models for all layouts | single virtualization path; grouping and tiles without nested panels |
| 2026-11-24 | Platform implementations in one assembly, selected at runtime | one build per RID, no conditional compilation in shared code |
| 2026-11-24 | Version reservation by atomic tag push | race-free unique versions without a workflow-level concurrency group |
| 2026-11-24 | Signed update manifest (ECDSA P-256) | asset hashes alone do not protect against a compromised release upload |
| 2026-11-24 | Tags stored in app data | portable across file systems; no xattr/ADS permission issues |
