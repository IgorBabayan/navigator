#!/usr/bin/env python3
"""Regenerates src/Navigator.App/Controls/IconCatalog.cs from Google's Material Symbols (Apache-2.0).

Usage:
  npm pack @material-symbols/svg-400 && mkdir -p /tmp/ms && tar -xzf material-symbols-svg-400-*.tgz -C /tmp/ms
  python3 tools/icons/make_material_icons.py /tmp/ms/package/rounded

Every path is rewritten from the 960-unit Material grid (viewBox "0 -960 960 960") onto Navigator's 24x24 grid as
absolute M/L/C/Q/A/Z commands only, so Avalonia's path parser never meets shorthand forms (S, T, H, V, relatives).
Paths use the nonzero fill rule ("F1"), as the SVG sources do. Optional badges (known folders) are scaled into the
glyph they decorate. The output is deterministic: running the script twice gives the same file.
"""
import os
import re
import sys

UNIT = 24 / 960

# UI glyphs: drawn with the control's Foreground. key -> Material Symbols name (outlined weight 400, rounded style)
GLYPHS = {
    "Add": "add", "Advanced": "tune", "Appearance": "palette", "Compress": "folder_zip", "Extract": "unarchive",
    "Back": "arrow_back", "Forward": "arrow_forward", "Up": "arrow_upward", "Check": "check",
    "ChevronDown": "keyboard_arrow_down", "ChevronRight": "chevron_right", "Close": "close", "CodeEditor": "code",
    "Copy": "content_copy", "Cut": "content_cut", "Delete": "delete", "Download": "download", "Upload": "upload",
    "Error": "error", "Filter": "filter_alt", "FolderAdd": "create_new_folder", "Group": "view_agenda",
    "Hidden": "visibility_off", "History": "history", "Home": "home", "Info": "info", "Keyboard": "keyboard",
    "LayoutDetails": "table_rows", "LayoutList": "list", "LayoutTiles": "view_module", "LayoutGrid": "apps",
    "More": "more_horiz", "Multiple": "file_copy", "OpenWith": "open_in_new", "Paste": "content_paste",
    "Pause": "pause", "Play": "play_arrow", "Pin": "keep", "Plugin": "extension", "PreviewPane": "right_panel_open",
    "Sidebar": "left_panel_open", "SplitView": "vertical_split", "Redo": "redo", "Undo": "undo", "Refresh": "refresh",
    "Rename": "drive_file_rename", "Restore": "restore_from_trash", "Search": "search", "SendTo": "send",
    "Settings": "settings", "Share": "share", "Shield": "admin_panel_settings", "Shortcut": "arrow_outward",
    "SignOut": "logout", "Sort": "sort", "Speed": "speed", "Success": "check_circle", "Tag": "sell",
    "Terminal": "terminal", "Trash": "delete", "Update": "system_update_alt", "UserSwitch": "switch_account",
    "Warning": "warning", "Window": "select_window", "Properties": "info", "Layers": "stacks",
}

# Colored glyphs: file types, drives, servers, clouds. key -> (Material name, preferring its filled form, color)
COLORED = {
    "File": ("draft", "#9CA3AF"), "Text": ("description", "#6B7280"), "Code": ("code_blocks", "#7C3AED"),
    "Document": ("article", "#2563EB"), "Spreadsheet": ("table_chart", "#16A34A"),
    "Presentation": ("slideshow", "#EA580C"), "Pdf": ("picture_as_pdf", "#DC2626"), "Image": ("image", "#0EA5E9"),
    "Video": ("video_file", "#DB2777"), "Audio": ("audio_file", "#9333EA"), "Archive": ("folder_zip", "#CA8A04"),
    "Executable": ("deployed_code", "#475569"), "Font": ("font_download", "#0F766E"),
    "DiskImage": ("album", "#64748B"), "Link": ("link", "#0891B2"),
    "Drive": ("hard_drive", "#6B7280"), "DriveRemovable": ("usb", "#3B82F6"), "DriveNetwork": ("lan", "#10B981"),
    "DriveOptical": ("album", "#64748B"),
    "Server": ("dns", "#64748B"), "ServerSsh": ("dns", "#6366F1"), "ServerFtp": ("dns", "#F59E0B"),
    "Cloud": ("cloud", "#38BDF8"), "CloudGoogleDrive": ("cloud", "#22C55E"), "CloudOneDrive": ("cloud", "#3B82F6"),
}

FOLDER_COLOR = "#F2B035"
BADGE_COLOR = "#FFF7E0"
FOLDERS = {"Desktop": "desktop_windows", "Documents": "description", "Downloads": "download",
           "Pictures": "image", "Music": "music_note", "Videos": "movie"}

TOKEN = re.compile(r"[MmLlHhVvCcSsQqTtAaZz]|-?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")
ARGS = {"M": 2, "L": 2, "H": 1, "V": 1, "C": 6, "S": 4, "Q": 4, "T": 2, "A": 7, "Z": 0}


def read_path(folder, name, prefer_fill):
    for candidate in ([name + "-fill", name] if prefer_fill else [name]):
        file = os.path.join(folder, candidate + ".svg")
        if os.path.exists(file):
            data = re.findall(r'\sd="([^"]+)"', open(file, encoding="utf-8").read())
            if not data:
                raise SystemExit(f"no path in {file}")
            return " ".join(data)
    raise SystemExit(f"Material symbol not found: {name}")


def absolute(path):
    """Parses SVG path data into absolute segments: ("M",x,y) ("L",x,y) ("C",...) ("Q",...) ("A",...) ("Z",)."""
    tokens = TOKEN.findall(path)
    segments, i = [], 0
    cx = cy = sx = sy = 0.0
    last_ctrl = None
    cmd = None
    while i < len(tokens):
        if tokens[i].isalpha():
            cmd = tokens[i]
            i += 1
            if cmd in "Zz":
                segments.append(("Z",))
                cx, cy = sx, sy
                last_ctrl = None
                continue
        elif cmd is None:
            raise SystemExit("path data starts with a number")
        upper, rel = cmd.upper(), cmd.islower()
        count = ARGS[upper]
        if upper == "A":
            # rx ry rotation large sweep x y; flags may be glued to the next number
            values = []
            while len(values) < 7:
                token = tokens[i]
                if len(values) in (3, 4) and len(token) > 1 and token[0] in "01" and not token.startswith(("0.", "1.")):
                    values.append(float(token[0]))
                    tokens[i] = token[1:]
                    continue
                values.append(float(token))
                i += 1
            n = values
        else:
            n = [float(t) for t in tokens[i:i + count]]
            i += count
        ox, oy = (cx, cy) if rel else (0.0, 0.0)
        if upper == "M":
            cx, cy = n[0] + ox, n[1] + oy
            sx, sy = cx, cy
            segments.append(("M", cx, cy))
            cmd = "l" if rel else "L"  # implicit lineto after moveto
            last_ctrl = None
        elif upper == "L":
            cx, cy = n[0] + ox, n[1] + oy
            segments.append(("L", cx, cy))
            last_ctrl = None
        elif upper == "H":
            cx = n[0] + (cx if rel else 0.0)
            segments.append(("L", cx, cy))
            last_ctrl = None
        elif upper == "V":
            cy = n[0] + (cy if rel else 0.0)
            segments.append(("L", cx, cy))
            last_ctrl = None
        elif upper == "C":
            x1, y1, x2, y2, x, y = n[0] + ox, n[1] + oy, n[2] + ox, n[3] + oy, n[4] + ox, n[5] + oy
            segments.append(("C", x1, y1, x2, y2, x, y))
            cx, cy, last_ctrl = x, y, ("C", x2, y2)
        elif upper == "S":
            if last_ctrl and last_ctrl[0] == "C":
                x1, y1 = 2 * cx - last_ctrl[1], 2 * cy - last_ctrl[2]
            else:
                x1, y1 = cx, cy
            x2, y2, x, y = n[0] + ox, n[1] + oy, n[2] + ox, n[3] + oy
            segments.append(("C", x1, y1, x2, y2, x, y))
            cx, cy, last_ctrl = x, y, ("C", x2, y2)
        elif upper == "Q":
            x1, y1, x, y = n[0] + ox, n[1] + oy, n[2] + ox, n[3] + oy
            segments.append(("Q", x1, y1, x, y))
            cx, cy, last_ctrl = x, y, ("Q", x1, y1)
        elif upper == "T":
            if last_ctrl and last_ctrl[0] == "Q":
                x1, y1 = 2 * cx - last_ctrl[1], 2 * cy - last_ctrl[2]
            else:
                x1, y1 = cx, cy
            x, y = n[0] + ox, n[1] + oy
            segments.append(("Q", x1, y1, x, y))
            cx, cy, last_ctrl = x, y, ("Q", x1, y1)
        elif upper == "A":
            x, y = n[5] + ox, n[6] + oy
            segments.append(("A", n[0], n[1], n[2], int(n[3]), int(n[4]), x, y))
            cx, cy, last_ctrl = x, y, None
    return segments


def emit(segments, scale, dx, dy):
    def num(v):
        text = f"{v:.3f}".rstrip("0").rstrip(".")
        return "0" if text in ("-0", "") else text

    def pt(x, y):
        return f"{num(x * scale + dx)},{num((y + 960) * scale + dy)}"

    parts = []
    for seg in segments:
        kind = seg[0]
        if kind == "Z":
            parts.append("Z")
        elif kind in "ML":
            parts.append(f"{kind}{pt(seg[1], seg[2])}")
        elif kind == "C":
            parts.append(f"C{pt(seg[1], seg[2])} {pt(seg[3], seg[4])} {pt(seg[5], seg[6])}")
        elif kind == "Q":
            parts.append(f"Q{pt(seg[1], seg[2])} {pt(seg[3], seg[4])}")
        elif kind == "A":
            parts.append(f"A{num(seg[1] * scale)},{num(seg[2] * scale)} {num(seg[3])} {seg[4]} {seg[5]} {pt(seg[6], seg[7])}")
    return "F1 " + " ".join(parts)


def main():
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    folder = sys.argv[1]
    lines = []
    clashes = GLYPHS.keys() & COLORED.keys()
    if clashes:
        raise SystemExit(f"keys used for both a UI glyph and a colored icon: {sorted(clashes)}")
    for key in sorted(GLYPHS):
        data = emit(absolute(read_path(folder, GLYPHS[key], prefer_fill=False)), UNIT, 0, 0)
        lines.append(f'        Glyph("{key}", "{data}");')
    lines.append("")
    for key in sorted(COLORED):
        name, color = COLORED[key]
        data = emit(absolute(read_path(folder, name, prefer_fill=True)), UNIT, 0, 0)
        lines.append(f'        Tinted("{key}", "{color}", "{data}");')
    lines.append("")
    folder_data = emit(absolute(read_path(folder, "folder", prefer_fill=True)), UNIT, 0, 0)
    lines.append(f'        var folder = "{folder_data}";')
    lines.append(f'        Tinted("Folder", "{FOLDER_COLOR}", folder);')
    # Badge: the symbol at 45 % size, centered in the folder body (x 12, y 14 on the 24 grid).
    badge_scale = UNIT * 0.45
    for key in sorted(FOLDERS):
        dx = 12 - 12 * 0.45
        dy = 14.2 - 12 * 0.45
        badge = emit(absolute(read_path(folder, FOLDERS[key], prefer_fill=True)), badge_scale, dx, dy)
        lines.append(f'        Badged("{key}", folder, "{FOLDER_COLOR}", "{badge}", "{BADGE_COLOR}");')

    body = "\n".join(lines)
    output = TEMPLATE.replace("$BODY$", body)
    target = os.path.join(os.path.dirname(__file__), "..", "..", "src", "Navigator.App", "Controls", "IconCatalog.cs")
    with open(os.path.normpath(target), "w", encoding="utf-8", newline="\n") as handle:
        handle.write(output)
    print(f"{len(GLYPHS) + len(COLORED) + len(FOLDERS) + 1} icons written")


TEMPLATE = """// <auto-generated>
// Generated by tools/icons/make_material_icons.py from Material Symbols (Rounded, weight 400) by Google, Apache-2.0
// (see THIRD-PARTY-NOTICES.md). Edit the script, not this file.
// </auto-generated>
using System.Collections.Frozen;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Navigator.App.Controls;

/// <summary>One drawable icon: filled layers on a 24x24 grid; a layer without a brush takes the control's Foreground.</summary>
internal sealed record IconDefinition(IReadOnlyList<(Geometry Geometry, IBrush? Brush)> Layers);

/// <summary>
/// Navigator's icon set: Material Symbols glyphs, rescaled to a 24x24 grid. UI glyphs follow the text color; file types,
/// drives, servers and clouds carry their own color. Geometry is parsed once and shared by every icon instance, so
/// thousands of rows cost no extra allocations. Icons the operating system draws (Windows shell icons) are not here.
/// </summary>
internal static class IconCatalog
{
    private static readonly FrozenDictionary<string, IconDefinition> Icons = Build();

    public static IconDefinition? Get(string? key)
        => key is not null && Icons.TryGetValue(key, out var icon) ? icon : null;

    private static FrozenDictionary<string, IconDefinition> Build()
    {
        var map = new Dictionary<string, IconDefinition>(StringComparer.Ordinal);
        var brushes = new Dictionary<string, IBrush>(StringComparer.Ordinal);

        IBrush Paint(string hex)
        {
            if (!brushes.TryGetValue(hex, out var brush))
                brushes[hex] = brush = new ImmutableSolidColorBrush(Color.Parse(hex));
            return brush;
        }

        void Glyph(string key, string data) => map[key] = new IconDefinition([(Geometry.Parse(data), null)]);

        void Tinted(string key, string color, string data) => map[key] = new IconDefinition([(Geometry.Parse(data), Paint(color))]);

        void Badged(string key, string shape, string shapeColor, string badge, string badgeColor)
            => map[key] = new IconDefinition([(Geometry.Parse(shape), Paint(shapeColor)), (Geometry.Parse(badge), Paint(badgeColor))]);

$BODY$

        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
"""

if __name__ == "__main__":
    main()
