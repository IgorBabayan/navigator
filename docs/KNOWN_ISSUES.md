# Known issues and limitations

Honest list of what is missing, partial or unverified. Keep it current.

## Verification gaps (highest priority)

1. **The Avalonia app layer (`src/Navigator.App`) has never been compiled.** The development environment
   had no access to nuget.org, so Avalonia packages could not be restored. The code was written against the
   Avalonia 12 documentation; expect a round of compile fixes on first build. Everything below the UI
   (Core, Platform, Updater, Presentation, ReleaseTool) compiles with analyzers and is tested.
2. Tests ran against an MSTest-compatible shim, not real MSTest (`docs/TESTING.md`).
3. Windows and macOS code paths have not been executed.
4. Release and CI workflows have not run on GitHub yet; packaging scripts have not been executed
   (shell scripts pass `shellcheck`; YAML parses).
5. No UI performance numbers (startup, memory, frame rate) — `docs/PERFORMANCE.md`.

## Functional gaps

| Area | Issue |
|---|---|
| Clipboard | Copy/cut/paste of files works only inside Navigator. Pasting files copied in Explorer/Finder/Nautilus (and vice versa) is not implemented. |
| Drag and drop | Only within Navigator; no drag to/from other applications. |
| Rubber-band selection | Selects only realized (visible) rows; items scrolled out of view during the drag are not included. |
| Omnibar | No drag-and-drop onto breadcrumb segments; typed commands (e.g. `cmd`, `notepad`) and URLs are not launched; on Windows, mice whose driver sends only `WM_APPCOMMAND` (no XBUTTON or browser keys) do not navigate. |
| Columns | Columns can be resized and hidden, not reordered. |
| Views | No column (Miller) view; no compact overlay. |
| Tabs | Cannot drag a tab out to a new window. |
| Open with | Windows uses the system "Open with" dialog (no in-app handler list); macOS has no handler enumeration (uses `open -a`). |
| macOS trash | Restore works only for items trashed by Navigator (Finder's "put back" metadata is private). |
| Windows notifications | Finished-operation notifications are in-app toasts only. |
| Thumbnails | Images only, memory cache only (no disk cache); no video/PDF/OS shell thumbnails. |
| Archives | ZIP create/extract, TAR and TAR.GZ extract only; no 7z, RAR, XZ, BZip2. |
| Preview | Markdown is shown as source; no PDF, audio/video, Office previews. |
| Owner/permissions | Owner via `stat(1)`; permissions/ACLs are read-only. |
| Search | No OS index integration (Windows Search, Spotlight, Tracker); content search is a linear scan. |
| Tags | Stored in Navigator's data folder, not in file metadata; renaming files outside Navigator loses tags. |
| Network | Only mounted network volumes; no SMB/NFS browsing, FTP, SFTP, WebDAV or cloud APIs. |
| Settings | No search box over settings. |
| Accessibility | No explicit `AutomationProperties` names; high-contrast themes not implemented; OS "reduce motion" is not followed automatically. |
| Git integration | Not implemented. |
| Linux packages | No DEB/RPM/Flatpak; AppImage + tar.gz only. |
| Windows packages | No MSIX/winget manifest. |
| macOS | Apple Silicon and Intel builds are separate (no universal binary). |

## Release pipeline notes

- appimagetool is only published under the moving `continuous` tag; set the
  `APPIMAGETOOL_SHA256_*` repository variables to pin it, otherwise the workflow warns and proceeds.
- Without signing secrets, Windows builds are unsigned (SmartScreen warnings) and macOS builds are ad-hoc
  signed (Gatekeeper requires right-click → Open).
- `README.md` is intentionally unchanged (project rule); user-facing documentation lives in `docs/`.
