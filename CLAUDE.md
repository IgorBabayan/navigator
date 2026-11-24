# CLAUDE.md — instructions for future development sessions

Read this file and `docs/PROGRESS.md` before changing anything. Continue from the documented progress;
do not redesign the architecture without recording the decision in `docs/ARCHITECTURE.md`.

## Project

**Navigator** is an independently engineered, high-performance, cross-platform (Windows, Linux, macOS)
file manager whose look, feel, animations and feature set are modelled on the Files app
(https://files.community). Priorities, in order: original code, performance, file safety, visual and
functional parity, cross-platform support, maintainability, low resource use, polish.

## Source originality — mandatory

- Files app is a **visual/behavioral reference only**. Never open, copy, translate, paraphrase or adapt
  its source code, XAML, styles, assets, architecture or project layout. Use its running app, docs and
  screenshots to learn *what* it does; design *how* independently.
- Renaming or reformatting copied code does not make it original.
- ED Assistant (https://github.com/IgorBabayan/ed-assistant, same author) **may** be reused/adapted
  (updater, workflows, installers, utilities). Record every reuse in `docs/SOURCE_ORIGINALITY.md`.
- Before releases run `tools/originality/similarity_scan.py` (see `docs/SOURCE_ORIGINALITY.md`).
- Icons and the logo are drawn for this project (`src/Navigator.App/Controls/IconCatalog.cs`,
  `tools/branding/make_logo.py`). Do not import third-party icon sets without a license review.

## Repository rules (from the project owner)

- Work from the `development` branch. Feature branches: `feature/*`, fixes: `bugfix/*`, urgent: `hotfix/*`.
- Deliver code changes as a single unified git patch when asked.
- **Do not modify `README.md`.**
- Performance is the top technical priority: measure, don't assume.

## Architecture (details: docs/ARCHITECTURE.md)

```
src/Navigator.Core          BCL only. Domain + engines: paths, enumeration, sorting, navigation,
                            file operation engine (safety rules), search, monitoring, caching,
                            settings (+migrations), archives, hashing, tags, semver, platform contracts.
src/Navigator.Platform      BCL only. Windows/Linux/macOS implementations (trash, volumes, known folders,
                            launching, notifications, ownership). Selected at runtime (PlatformServices).
src/Navigator.Updater       BCL only. GitHub Releases updater: manifest, signature, download, installers.
src/Navigator.Presentation  View models (CommunityToolkit.Mvvm, no Avalonia). Unit-testable.
src/Navigator.App           Avalonia 12 app: composition root, design system, views, UI services.
tools/Navigator.ReleaseTool Version reservation, checksums, update manifest, signing (used by CI).
tests/*                     MSTest unit + integration tests, BenchmarkDotNet benchmarks.
installer/*                 Inno Setup, AppImage/tar.gz, macOS .app/DMG/ZIP packaging scripts.
```

Rules: business logic never references Avalonia; shared code never calls OS APIs directly (use the
`Navigator.Core.Platform` interfaces); view models use explicit properties (no reflection-heavy patterns);
everything long-running is async, cancellable and off the UI thread; collections are bounded.

## Technology

C# / .NET 10, Avalonia 12 (compiled bindings), CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection
and Logging, System.Text.Json source generation, MSTest, BenchmarkDotNet, GitHub Actions.
Package versions live in `Directory.Packages.props`. New dependencies need a written justification.

## Conventions

- File-scoped namespaces, `_camelCase` private fields, nullable enabled, analyzers at `latest-recommended`;
  CI treats warnings as errors.
- User-facing text goes through `Localizer` (`src/Navigator.Presentation/Localization/Strings/*.json`).
  Every key must exist in every language (a unit test enforces it). In XAML use `{c:Tr Key}`.
- Styling only through tokens in `src/Navigator.App/Theme/Tokens.axaml`; motion through the
  `Motion.*` resources managed by `ThemeService` (respects reduced/off motion).
- No `.Result`/`.Wait()`, no empty catches, no fire-and-forget without error handling.
- File safety rules (never break them): no silent overwrite; copy through a temp file + atomic rename;
  cross-volume moves delete the source only after verification; links are never followed; a folder is
  removed after a move only when every child moved.

## Commands

```bash
dotnet restore Navigator.slnx
dotnet build Navigator.slnx -c Release
dotnet test tests/Navigator.UnitTests -c Release
dotnet test tests/Navigator.IntegrationTests -c Release
dotnet run --project src/Navigator.App
dotnet run -c Release --project tests/Navigator.Benchmarks -- --filter '*'
dotnet format Navigator.slnx --verify-no-changes
```

## Releases and updates

- Every PR merged into `release` publishes a GitHub Release (`.github/workflows/release.yml`):
  exact merge commit, version reserved by atomic tag push, draft → verify → publish, Latest reconciled.
- Version series in `version.json`; labels `release:minor` / `release:major` bump; default is patch.
- The updater reads `update-manifest.json` (+ `.sig` when `NAVIGATOR_UPDATE_PUBLIC_KEY` is configured),
  verifies SHA-256, never downgrades, never restarts during file operations. See `docs/UPDATER.md`.
- Never push, merge, change repository settings or publish real releases without explicit authorization.

## Documentation map

`docs/PROGRESS.md` (status, next steps) · `docs/FEATURE_PARITY.md` · `docs/ARCHITECTURE.md` ·
`docs/UI_SPECIFICATION.md` · `docs/ANIMATION_SPECIFICATION.md` · `docs/SOURCE_ORIGINALITY.md` ·
`docs/PERFORMANCE.md` · `docs/PLATFORM_SUPPORT.md` · `docs/BUILD.md` · `docs/RELEASE.md` ·
`docs/UPDATER.md` · `docs/TESTING.md` · `docs/IMPLEMENTATION_PLAN.md` · `docs/KNOWN_ISSUES.md`.

Keep `docs/PROGRESS.md` and `docs/FEATURE_PARITY.md` in sync with reality. Never mark a feature done
until it is implemented **and** validated; never fabricate benchmark or test results.
