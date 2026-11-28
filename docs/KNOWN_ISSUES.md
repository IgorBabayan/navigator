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
| Clipboard | Windows: files copied or cut in Explorer and files copied inside a Remote Desktop session (streamed clipboard files) can be pasted into Navigator; streamed files cannot be pasted into a server (plugin) folder and show no progress bar (a toast reports start and end). Files copied in Navigator are offered to other applications as text only. Linux/macOS: pasting files copied in Finder/Nautilus is not implemented. |
| Drag and drop | Files dropped from other applications on Linux and macOS are copied by default (Shift moves): their paths are only read on drop, so the volume is not known while hovering. Server (plugin) items cannot be dropped anywhere (use Download); local items dropped on a server folder are uploaded. No spring-loaded folders (hovering a folder does not open it). |
| Rubber-band selection | Selects only realized (visible) rows; items scrolled out of view during the drag are not included. |
| Omnibar | No drag-and-drop onto breadcrumb segments; typed commands (e.g. `cmd`, `notepad`) and URLs are not launched; on Windows, mice whose driver sends only `WM_APPCOMMAND` (no XBUTTON or browser keys) do not navigate. |
| Columns | The name column cannot be moved (it carries the icon and the rename editor); headers are reordered by dragging or from their menu, not with the keyboard. |
| Views | Column (Miller) layout: the columns left of the current folder are read-only (no selection, context menu or drop target; click to navigate) and are not refreshed by file system changes until revisited. Compact overlay uses a fixed size (420×520) and keeps the window's position. Always on top relies on the window manager (some Wayland compositors ignore it). |
| Tabs | A window's only tab cannot be torn into a new window (it can be dropped on another window's tab strip). There is no drag preview while a tab is outside the strip; the tab is only dimmed. Tabs are moved between windows of the same Navigator process only. |
| Open with | Windows uses the system "Open with" dialog (no in-app handler list); macOS has no handler enumeration (uses `open -a`). |
| macOS trash | Restore works only for items trashed by Navigator (Finder's "put back" metadata is private). |
| Windows notifications | Finished-operation notifications are in-app toasts only. |
| Global shortcut when closed | Needs the shortcut agent (≈7 MB private memory, no CPU while idle, measured on Linux/X11) or, on GNOME/Wayland, a GNOME custom shortcut. Other Wayland desktops (KDE, Sway…) cannot get it automatically. The Linux autostart entry and macOS LaunchAgent stay behind if the app is deleted without turning the setting off (harmless: they point to a missing file); the Windows uninstaller removes its entry. |
| Cloud plugins | Need OAuth clients (`docs/PLUGINS.md`); not yet tested against the live services. Google Drive: "My Drive" only (no "Shared with me" or shared drives root), Google Docs show no size. Refresh tokens stay in the OS keychain when a cloud plugin is removed without signing out first. |
| Thumbnails | Video/PDF/document thumbnails appear in icon and tile layouts only. Linux needs `pdftoppm` (poppler-utils) for PDFs and `ffmpegthumbnailer` or `ffmpeg` for videos; other types (legacy Office, HEIF, RAW) only when the desktop already put a thumbnail in the shared cache. A file without a thumbnail is remembered until it changes or the disk cache is cleared (installing a codec later needs Settings → Performance → Clear). |
| Archives | ZIP create/extract, TAR and TAR.GZ extract only; no 7z, RAR, XZ, BZip2. |
| Preview | Videos show a frame and their details, not playback (Open plays them). Markdown: a CommonMark/GFM subset (no HTML, footnotes or remote images; links are not clickable). Legacy .doc/.xls/.ppt show only the platform's page thumbnail. PDF page counts are unknown for files whose page tree is in compressed object streams. Audio playback on Linux needs ffplay, mpv, gst-play-1.0 or paplay. Seer is reached by its window message (no selection following); PowerToys Peek is started per file. |
| Quick look | The space bar acts only while the file list has focus; a custom gesture can be set in Settings → Keyboard. GNOME Sushi is used only when chosen explicitly. |
| Owner/permissions | Owner via `stat(1)`. Windows ACLs are shown read-only (Security → Advanced… opens Explorer's editor, which opens on its first page on non-English Windows); Unix modes can be changed (no recursive apply, no chown). |
| Signatures | Linux cannot decide whether a Windows signature is trusted (shown as "not verified"); Windows checks run offline (no revocation check). |
| Folder icons | On Linux the icon is stored where the desktop's file manager looks (GIO metadata for GTK file managers, `.directory` on KDE); whether it is drawn depends on that file manager. Navigator's own lists show custom folder icons on Windows only. |
| Search | No OS index integration (Windows Search, Spotlight, Tracker); content search is a linear scan. |
| Tags | Stored in Navigator's data folder, not in file metadata; renaming files outside Navigator loses tags. |
| Network | Only mounted network volumes; no SMB/NFS browsing, FTP, SFTP, WebDAV or cloud APIs. |
| Settings | Settings are searched from the address bar (by name, description and page); there is no separate search box inside the settings window. |
| Performance indicator | Shows only what Avalonia's metrics report (all windows together); GPU time and per-window numbers are not available. Frames per second counts rendered frames, so it is near 0 while nothing changes on screen. |
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
