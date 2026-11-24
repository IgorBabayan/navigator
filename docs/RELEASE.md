# Release process

Releases are produced only by `.github/workflows/release.yml`. Nobody builds release packages by hand.

## Branches

| Branch | Purpose |
|---|---|
| `development` | Integration branch; all feature/bugfix branches merge here. No workflow runs on pushes or PRs here (build locally; `ci.yml` can be started manually). |
| `release` | Pull requests into `release` run CI (`ci.yml`) as the pre-release gate; every merged pull request produces one GitHub Release. |
| `feature/*`, `bugfix/*`, `hotfix/*` | Work branches. Hotfixes may target `release` directly and are merged back into `development`. |

## Triggers

- `pull_request` **closed** into `release` with `merged == true` → release from the PR's exact
  `merge_commit_sha`. Closed-without-merge PRs do nothing.
- `workflow_dispatch` (input `bump`: auto/patch/minor/major) → allowed only when dispatched **on the
  `release` branch**; releases the branch head (e.g. after fixing a secret). Dispatch from other branches
  fails in the `prepare` job.

## Version numbers

- `version.json` holds the series: `{"major": 1, "minor": 0}`. Releases are `MAJOR.MINOR.PATCH` tags `vX.Y.Z`.
- The patch number is computed: highest existing `vMAJOR.MINOR.*` tag + 1 (or `.0` for a new series).
- PR label `release:minor` or `release:major` bumps the series for that release (and the next release
  continues from the highest tag, so versions never go backwards: `ReleaseLogic.ComputeNextVersion`).
  Remember to update `version.json` in `development` afterwards to keep the series explicit.
- **Uniqueness without races**: `prepare` computes a candidate and pushes the tag pointing at the release
  commit. Tag creation is atomic on GitHub; if another run took that version, the push fails, tags are
  re-fetched and the next number is tried (up to 10 attempts). If the commit already has a `v*` tag from an
  earlier attempt, that version is reused, so a retried run never burns a new number.

## Pipeline

```
prepare ─► test (ubuntu, windows, macos) ─► build (6 RIDs, parallel) ─► publish
```

1. **prepare** — validates the trigger, resolves the exact commit, checks it is reachable from `release`,
   reserves the version (tag push), outputs `version`/`tag`/`commit`.
2. **test** — restore, build with warnings as errors, unit + integration tests on all three OSes.
3. **build** — `dotnet publish` self-contained per RID with `AppVersion`, `UpdateRepository` and
   `UpdatePublicKey`; then packaging:

   | RID | Runner | Outputs |
   |---|---|---|
   | win-x64, win-arm64 | windows-latest | `Navigator-{v}-{rid}-setup.exe` (Inno Setup), `Navigator-{v}-{rid}-portable.zip` |
   | linux-x64 | ubuntu-22.04 | `Navigator-{v}-linux-x64.AppImage`, `Navigator-{v}-linux-x64.tar.gz` |
   | linux-arm64 | ubuntu-22.04-arm | `Navigator-{v}-linux-arm64.AppImage`, `Navigator-{v}-linux-arm64.tar.gz` |
   | osx-arm64, osx-x64 | macos-latest | `Navigator-{v}-{rid}.dmg`, `Navigator-{v}-{rid}.zip` (updater bundle) |

   ubuntu-22.04 is used so the AppImage runs on older glibc distributions.
4. **publish** — downloads all artifacts, generates `update-manifest.json` (+ `.sig` when the signing key is
   configured) and `SHA256SUMS.txt`, verifies every file against the manifest, replaces an incomplete
   draft left by a failed run (an already *published* release is verified, never overwritten), creates a
   **draft** release for the tag, uploads assets, re-reads the uploaded asset list and verifies
   names and sizes, publishes the draft with `--latest=false`, then marks as **Latest** the highest stable
   version among all releases (so an older hotfix published late never steals "Latest").

No workflow-level concurrency group is used — it would cancel or discard queued releases. `prepare` is
serialized per commit and `publish` per tag, with `cancel-in-progress: false`.

### Plugin packages

The publish job also builds every packaged plugin (`plugins/*` projects with `NavigatorPluginId`; the built-in Network
plugin ships inside the app), copies the `navigator-plugin-{id}-{version}.navplugin` files into the release, writes
`plugin-catalog.json` with `Navigator.ReleaseTool plugin-catalog` and signs it with the update key
(`plugin-catalog.json.sig`). When the public key is configured, an unsigned catalog fails the release. The app's plugin
shop reads the catalog of the latest release. See `docs/PLUGINS.md`.

## Required configuration (repository settings)

| Name | Kind | Required | Purpose |
|---|---|---|---|
| `NAVIGATOR_UPDATE_SIGNING_KEY` | secret | recommended | ECDSA P-256 private key (PKCS#8, base64) that signs `update-manifest.json` |
| `NAVIGATOR_UPDATE_PUBLIC_KEY` | variable | with the key | matching public key (SPKI base64), compiled into the app; when set, the updater **requires** a valid signature |
| `WINDOWS_CERTIFICATE_PFX_BASE64`, `WINDOWS_CERTIFICATE_PASSWORD` | secrets | optional | Authenticode signing of `Navigator.exe` and the installer (`installer/windows/sign.ps1`) |
| `MACOS_CERTIFICATE_P12_BASE64`, `MACOS_CERTIFICATE_PASSWORD`, `MACOS_SIGNING_IDENTITY` | secrets | optional | Developer ID signing with hardened runtime; without them builds are ad-hoc signed |
| `APPLE_ID`, `APPLE_TEAM_ID`, `APPLE_APP_SPECIFIC_PASSWORD` | secrets | optional | notarization + stapling (needs Developer ID) |
| `APPIMAGETOOL_SHA256_X86_64`, `APPIMAGETOOL_SHA256_AARCH64` | variables | recommended | pins the appimagetool binary (upstream only publishes a moving `continuous` tag) |

Create the update key pair once:

```bash
dotnet run --project tools/Navigator.ReleaseTool -- keygen
# prints the private key (store as secret) and the public key (store as variable)
```

Recommended branch protection for `release`: require PRs, require the `CI` checks,
disallow force pushes and deletion; restrict who can push tags matching `v*` to the workflow (GitHub
rulesets) so versions cannot be reserved by hand.

## Changing a release

Releases are immutable once published. To fix a broken release, merge a fix into `release` — it gets the
next patch version. Deleting a published release does not free its version (the tag remains).

## Validation status

The workflows were validated structurally (YAML parsing, script linting with shellcheck) and the release
tool logic is unit-tested. **No real release has been run yet**; the first run on GitHub is the
end-to-end validation. See `docs/KNOWN_ISSUES.md`.
