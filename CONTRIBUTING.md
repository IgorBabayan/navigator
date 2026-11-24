# Contributing to Navigator

Thanks for helping! Please read this page and `CLAUDE.md` (it doubles as the engineering handbook).

## Source originality (required)

Navigator looks and behaves like the Files app, but **must not contain its code**. Do not open, copy,
translate or adapt Files app source, XAML, styles or assets — learn behavior from the running app and its
public documentation only. By submitting a pull request you confirm that your contribution is your own
work or comes from a compatible source that you have listed in `THIRD-PARTY-NOTICES.md`.
See `docs/SOURCE_ORIGINALITY.md`.

## Workflow

1. Branch from `development`: `feature/<topic>`, `bugfix/<topic>`, or `hotfix/<topic>` (hotfixes may target `release`).
2. Keep pull requests focused; target `development`.
3. Before pushing:
   ```bash
   dotnet format Navigator.slnx --verify-no-changes
   dotnet build Navigator.slnx -c Release -p:ContinuousIntegrationBuild=true
   dotnet test tests/Navigator.UnitTests -c Release
   dotnet test tests/Navigator.IntegrationTests -c Release
   ```
4. CI does not run on `development`; run the commands above before every push. Update `docs/FEATURE_PARITY.md`, `docs/PROGRESS.md`, `docs/KNOWN_ISSUES.md` and `CHANGELOG.md`
   (under **Unreleased**) when behavior changes. Do not edit `README.md`.
5. Releases happen when `development` is merged into `release` (`docs/RELEASE.md`). Add `release:minor`
   or `release:major` to that PR when the series should change.

## Code guidelines

- Business logic in `Navigator.Core` (BCL only); OS calls only in `Navigator.Platform`; no Avalonia types
  outside `Navigator.App`.
- Async all the way, cancellation tokens on every long-running API, nothing heavy on the UI thread.
- Bounded collections and caches; dispose watchers, timers and subscriptions.
- File safety rules in `CLAUDE.md` are non-negotiable; every change to the operation engine needs an
  integration test.
- New user-facing strings go into **both** `en.json` and `uk.json`.
- Performance-sensitive changes need a measurement (probe or benchmark) in the PR description.
- New dependencies require a justification and an entry in `THIRD-PARTY-NOTICES.md`.

## Reporting bugs

Include OS and version, Navigator version (Settings → About), steps, expected vs actual behavior, and the
log file (Settings → Advanced → Open logs folder). Logs contain paths but never file contents.
