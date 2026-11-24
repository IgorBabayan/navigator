# Platform support

| Platform | Architectures | Minimum version | Packages |
|---|---|---|---|
| Windows | x64, arm64 | Windows 10 1809 (build 17763) | Inno Setup installer (per-user by default, per-machine optional), portable ZIP |
| Linux | x64, arm64 | glibc 2.35 (Ubuntu 22.04 era) with X11 or XWayland | AppImage, portable tar.gz |
| macOS | arm64 (Apple Silicon), x64 (Intel) | macOS 12 Monterey | DMG, ZIP (used by the updater) |

All builds are self-contained (.NET runtime included) and ReadyToRun-compiled; no runtime installation is needed.

## Per-platform implementation

| Capability | Windows | Linux | macOS |
|---|---|---|---|
| Path rules | case-insensitive, Windows invalid characters/reserved names enforced | case-sensitive, only `/` and NUL invalid | case-insensitive by default (APFS), Unicode NFC-normalized comparison |
| Known folders | `SHGetKnownFolderPath` | `$XDG_CONFIG_HOME/user-dirs.dirs` (+ defaults) | `~/Desktop`, `~/Documents`, `~/Downloads`, … |
| Drives/volumes | `DriveInfo`, filtered | `/proc/mounts` via `DriveInfo`, virtual file systems hidden (proc, sysfs, tmpfs, snap squashfs, …) | `/` + `/Volumes/*` |
| Volume change detection | watcher on drive roots + fingerprint | watchers on `/media`, `/mnt`, `/run/media/$USER` + fingerprint | watcher on `/Volumes` + fingerprint |
| Trash | Recycle Bin (`SHFileOperationW`, `$I`/`$R` parsing for listing/restore) | freedesktop.org Trash 1.0 (home trash + `$topdir/.Trash-$uid`) | `NSFileManager trashItemAtURL` (listing `~/.Trash`; restore only for items trashed by Navigator) |
| Open / open with | `ShellExecute`; "Open with" system dialog | `xdg-open`; `.desktop` files + `mimeapps.list` + `globs2` | `open`, `open -a` |
| Terminal | Windows Terminal (`wt`) → PowerShell 7 (`pwsh`) → `cmd` | first found of `kgx`, `gnome-terminal`, `konsole`, `xfce4-terminal`, `kitty`, …, `x-terminal-emulator`, `xterm` | Terminal.app |
| Reveal in system manager | `explorer /select,` | `dbus-send` FileManager1 → `xdg-open` parent | `open -R` |
| Notifications | in-app toast | `notify-send` (libnotify) | `osascript display notification` |
| File owner | — | `stat -c %U` | `stat -f %Su` |
| Sidebar/Home icons | shell stock icons + known-folder icons (`SHGetStockIconInfo`, `SHGetFileInfoW`, `SHGetImageList`) on a background STA thread | Navigator icon set | Navigator icon set |
| Window chrome | extended client area, native caption buttons, Mica/Acrylic | extended client area where the WM supports it, native decorations otherwise | traffic lights inset into the title bar |
| Data directories | `%APPDATA%\Navigator` (settings), `%LOCALAPPDATA%\Navigator\Cache` and `\Logs` | `$XDG_CONFIG_HOME/navigator`, `$XDG_CACHE_HOME/navigator`, `$XDG_STATE_HOME/navigator/logs` | `~/Library/Application Support/Navigator`, `~/Library/Caches/Navigator`, `~/Library/Logs/Navigator` |

`NAVIGATOR_DATA_DIR` overrides all data locations (portable use, tests).

## Validation status

The development environment was Linux without a display and without NuGet access:

- Linux: all Core/Platform/Updater logic built and tested (124 automated tests passed); UI not run.
- Windows, macOS: platform code compiles (same assembly) but has **not been executed**. CI runs the full
  unit + integration suites on `windows-latest` and `macos-latest`.

See `docs/KNOWN_ISSUES.md` for platform-specific gaps.
