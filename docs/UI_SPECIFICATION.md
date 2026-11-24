# UI specification

Navigator's window follows the familiar Files-app layout. Everything is styled through tokens in
`src/Navigator.App/Theme/Tokens.axaml` and the shared styles in `Theme/Controls.axaml`.

## Window layout

```
┌───────────────────────────────────────────────────────────────────────────┐
│ [logo] [Tab 1][Tab 2][+]                           drag area   [–][□][×]  │ title bar 44 px
├───────────────────────────────────────────────────────────────────────────┤
│ [←][→][↑][⟳]  [ Omnibar: breadcrumb ▸ path ▸ … | search ]  [⋯]           │ navigation bar
│ [New ▾][Cut][Copy][Paste][Rename][Share][Delete] │ [Sort][Group][Layout][Preview] │ command bar
├────────────┬──────────────────────────────────────────────┬───────────────┤
│ Sidebar    │ Pane (single or split)                       │ Preview /     │
│ Home       │ ┌ column headers (details layout) ─────────┐ │ details pane  │
│ Pinned     │ │ rows / tiles (virtualized)               │ │               │
│ Library    │ │                                          │ │               │
│ Drives     │ │                                          │ │               │
│ Network    │ └──────────────────────────────────────────┘ │               │
│ Tags       │ status bar: N items · M selected · size      │               │
│ Recycle    │                                              │               │
├────────────┴──────────────────────────────────────────────┴───────────────┤
│ Operations flyout (on demand) · toasts (bottom-right)                       │
└───────────────────────────────────────────────────────────────────────────┘
```

- **Title bar**: client area extended into the title bar (`ExtendClientAreaToDecorationsHint`); tabs sit in
  the title bar and empty space drags the window. Caption buttons are the platform's own (right side on
  Windows/Linux, traffic lights on the left on macOS); `ConfigureCaptionInsets` reserves room for them.
- **Tabs**: icon + title + close button, middle-click closes, drag to reorder, context menu (new, duplicate,
  pin, close, close others, close to the right, reopen closed); Ctrl+Shift+T reopens closed tabs
  (25 remembered); new windows via the `window.new` command.
- **Omnibar**: shows clickable breadcrumb segments; clicking the empty area or Ctrl+L / Alt+D switches to an
  editable path box (accepts `~`, environment variables, relative paths); Ctrl+F / F3 searches the current
  folder recursively; Ctrl+Shift+F filters the current listing; Ctrl+Shift+P opens the command palette.
  (Breadcrumb chevron flyouts and path autocompletion are not implemented yet — `docs/KNOWN_ISSUES.md`.)
- **Sidebar**: collapsible sections, resizable with a splitter (clamped to 160–480 px, persisted), toggled
  with the sidebar command; pinned folders are reorderable by drag; drives show a usage bar; context menus
  per item.
- **Pane**: layouts **Details, List, Tiles, Small/Medium/Large/Extra-large icons**; group headers; column
  headers with sort indicator, resizable columns (48–1200 px) and a column visibility menu; rubber-band
  selection; keyboard type-to-select; empty-folder and error states with retry. Column reordering is not
  implemented.
- **Split view**: two panes side by side (Ctrl+Shift+S / toolbar), focused pane highlighted; drag between panes.
- **Preview pane**: image, text and Markdown source (first 64 KB), folder summary, multi-selection summary,
  metadata for other files; **details** with
  properties, permissions, tags and hashes on demand.
- **Status bar**: item count, selection count and total selected size; loading indicator.
- **Home page**: quick access (pinned), drives with capacity, recent files, tags.
- **Settings window**: page list on the left — General, Appearance, Files & folders, Sidebar, Operations,
  Search, Tags, Keyboard, Performance, Updates, Advanced, About; import/export/reset. A search box over
  settings is not implemented.

## Tokens

| Group | Tokens (default = "Normal" density) |
|---|---|
| Typography | `Font.Caption` 12, `Font.Body` 14, `Font.Subtitle` 16, `Font.Title` 20, `Font.Display` 28; family: Segoe UI Variable / SF Pro / Inter / Cantarell / Noto Sans / Ubuntu fallback chain; scaled by the font-size setting |
| Sizes | `Size.Row` 32, `Size.Control` 32, `Size.Tab` 36, `Size.TitleBar` 44, `Size.Icon` 16, `Size.RowIcon` 18, `Size.SidebarItem` 36 |
| Density | Compact: row 26 / control 28 / tab 32 · Normal: 32 / 32 / 36 · Comfortable: 40 / 36 / 40 |
| Spacing | `Space.Card` 16, `Space.Page` 32,24,32,32, `Space.Toolbar` 8,4,8,4, `Space.Row` 12,0,12,0 |
| Radius | `Radius.Control` 4, `Radius.Card` 8, `Radius.Overlay` 8, `Radius.Tab` 8,8,0,0, `Radius.Layer` 8,0,0,0, `Radius.Pill` 2 |
| Elevation | `Shadow.Card`, `Shadow.Flyout`, `Shadow.Dialog` |
| Color | Light/Dark theme dictionaries: `Background.*`, `Layer.*`, `Text.*`, `Stroke.*`, `Control.*` brushes; accent (`Accent.Brush`, `Accent.SubtleBrush` 16 %, `Accent.SelectionBrush` 24 %, `Accent.ForegroundBrush` contrast-picked) from system accent or the custom accent setting |
| Backdrop | None / Mica / Acrylic setting with tint opacity; mapped to Avalonia `TransparencyLevelHint` (Mica on Windows 11, acrylic/blur where the platform supports it, solid fallback) |
| Motion | see `docs/ANIMATION_SPECIFICATION.md` |

## States

Every interactive element defines: rest, pointer-over, pressed, selected, disabled, focus-visible
(Fluent theme focus adorner), drop-target (rows and sidebar items while dragging).

## Accessibility

- Full keyboard navigation (Tab order: navigation bar → command bar → sidebar → pane → preview);
  all commands have shortcuts and are reachable through the command palette.
- Icon-only buttons carry localized tooltips. Explicit `AutomationProperties.Name` values are not set yet (`docs/KNOWN_ISSUES.md`).
- Body text colors were chosen for ≥ 4.5:1 contrast in both themes (not yet audited with a tool); accent foreground is picked by luminance.
- Animations can be reduced or turned off.
- High-contrast themes: not implemented (see `docs/KNOWN_ISSUES.md`).

## Icons

Original 24×24 path icons (`Controls/IconCatalog.cs`), rendered by `IconView` with theme brushes; file type
icons are category-based (folder, document, image, video, audio, archive, code, executable, …).

## Localization

English (`en`) and Ukrainian (`uk`), selectable in Settings → General (follows the OS language by default).
Layouts avoid fixed widths for text so longer Ukrainian strings fit.
