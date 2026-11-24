# Animation specification

Motion in Navigator is short, purposeful and never blocks input. Values were chosen to match the feel of
Fluent-style apps; they are Navigator's own tokens (see `docs/SOURCE_ORIGINALITY.md`).

## Principles

1. Animate only compositor-friendly properties: **opacity, render transforms, brushes**. Never animate
   width/height/margins (that forces layout every frame).
2. Never delay data. Items appear the moment they are loaded; entrance motion is cosmetic and starts from
   the final layout position.
3. No motion on hot paths: list rows, scrolling, selection changes in large lists and progress updates are
   not animated beyond a brush transition on hover.
4. Every animation obeys **Settings → Appearance → Animations** (`AnimationLevel`).

## Tokens

Defined at runtime by `ThemeService.ApplyMotion` (placeholders in `Theme/Tokens.axaml`).

| Token | Full | Reduced | Off |
|---|---|---|---|
| `Motion.Duration.Fast` | 120 ms | 80 ms | 0 |
| `Motion.Duration.Normal` | 187 ms | 100 ms | 0 |
| `Motion.Duration.Slow` | 250 ms | 120 ms | 0 |
| `Motion.Enabled` | true | true | false |
| `Motion.Movement` (translation allowed) | true | false | false |

Easing curves:

| Name | Cubic Bézier | Used for |
|---|---|---|
| Standard | (0.1, 0.9, 0.2, 1.0) | hover brushes, indicator movement |
| Decelerate | (0.0, 0.0, 0.0, 1.0) | entrances (fade, slide-in) |

## Transition sets

| Resource | Full | Reduced | Off | Applied to |
|---|---|---|---|---|
| `Motion.Hover` | Background + border brush, Fast, Standard | same | none | `Border.hoverable`, `Border.item`, `Border.tab`, the content presenter of `Button.subtle` / `ToggleButton.subtle` (sidebar items, rows, tabs, command buttons) |
| `Motion.Progress` | Progress value, Slow, Decelerate | same | none | `ProgressBar`, `c:ProgressRing` |
| `Motion.Fade` | Opacity, Normal, Decelerate | same | none | flyout/overlay content |
| `Motion.FadeAndMove` | Opacity (Normal) + translate (Slow), Decelerate | falls back to `Motion.Fade` | none | entrances via `Motion.Enter` |
| `Motion.Indicator` | Translate, Normal, Standard | none | none | selection pill of sidebar items |

## Entrance behavior (`c:Motion.Enter="True"`)

When the element is attached to the visual tree: opacity 0 → 1; with movement allowed it also slides
from `translateY(6px)` to `0`. Used on:

| Element | Effect |
|---|---|
| New tab header (`MainWindow`) | fade + 6 px rise |
| Toast / in-app notification (`MainWindow`) | fade + 6 px rise |
| Home page cards (quick access, drives) | fade + 6 px rise |
| Preview/details pane | fade + 6 px rise |

## Interaction catalogue

| Interaction | Motion |
|---|---|
| Hover over row, tab, sidebar item, button | background brush transition (Fast) |
| Selection of sidebar item | accent pill appears; pill moves with `Motion.Indicator` |
| Open tab | header entrance |
| Close tab | removed immediately (no exit animation — keeps keyboard close-spam responsive) |
| Open preview pane | entrance |
| Toasts | entrance; auto-dismiss after the configured delay |
| Folder navigation | no page transition: content is replaced as soon as the first batch arrives (perceived speed beats motion) |
| Drag over a folder | `droptarget` style (brush transition) |
| Operations progress | progress bars and the toolbar ring ease between updates (`Motion.Progress`); status center cards enter with `Motion.Enter`; the details chevron rotates with `Motion.Indicator` |
| Scrolling (row recycling) | no motion: `RowSurface` disables transitions while a recycled row receives its new item |

Brush rule: translucent brushes keep their alpha in the color (`#AARRGGBB`), never in `Opacity`, and hoverable
surfaces rest on `Brush.Rest` (the hover color at zero alpha). `BrushTransition` interpolates color and opacity
separately, so a transparent → `(color, Opacity)` fade overshoots mid-way and reads as a blink.

## Reduced motion

- `Reduced`: only fades remain; nothing translates; durations shortened.
- `Off`: transitions are empty collections; entrance handler returns immediately.
- The default is `Full`. Following the OS "reduce motion" preference automatically is listed in
  `docs/KNOWN_ISSUES.md` (not implemented — Avalonia exposes no cross-platform API for it).

## Verification status

Durations and easing values are set in code and covered by review only. Frame timing has **not** been
measured (the Avalonia app could not be built in the development sandbox) — see `docs/PERFORMANCE.md`.
