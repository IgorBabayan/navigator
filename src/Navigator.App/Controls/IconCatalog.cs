using System.Collections.Frozen;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Navigator.App.Controls;

/// <summary>One drawable icon: an optional stroked outline (toolbar glyphs) and filled layers (file-type icons).</summary>
internal sealed record IconDefinition(Geometry? Stroke, IReadOnlyList<(Geometry Geometry, IBrush? Brush)> Fills);

/// <summary>
/// Navigator's own icon set, drawn on a 24×24 grid. Designed for this project (no third-party or reference-app assets);
/// geometry is parsed once and shared by every icon instance, so thousands of rows cost no extra allocations.
/// </summary>
internal static class IconCatalog
{
    private static readonly FrozenDictionary<string, IconDefinition> Icons = Build();

    public static IconDefinition? Get(string? key)
        => key is not null && Icons.TryGetValue(key, out var icon) ? icon : null;

    private static FrozenDictionary<string, IconDefinition> Build()
    {
        var map = new Dictionary<string, IconDefinition>(StringComparer.Ordinal);

        void Line(string key, string data) => map[key] = new IconDefinition(Geometry.Parse(data), []);

        // ── Stroked UI glyphs ──
        Line("Back", "M19,12 H5 M11,6 L5,12 L11,18");
        Line("Forward", "M5,12 H19 M13,6 L19,12 L13,18");
        Line("Up", "M12,19 V5 M6,11 L12,5 L18,11");
        Line("Refresh", "M19,12 A7,7 0 1 1 16.9,7 M19,4 V8 H15");
        Line("Add", "M12,5 V19 M5,12 H19");
        Line("Close", "M6,6 L18,18 M18,6 L6,18");
        Line("ChevronRight", "M9,6 L15,12 L9,18");
        Line("ChevronDown", "M6,9 L12,15 L18,9");
        Line("Search", "M10.5,4 A6.5,6.5 0 1 1 10.49,4 Z M15.2,15.2 L20,20");
        Line("Cut", "M7,4 L16.5,15.5 M17,4 L7.5,15.5 M7,15 A2.5,2.5 0 1 1 6.99,15 Z M17,15 A2.5,2.5 0 1 1 16.99,15 Z");
        Line("Copy", "M8,8 H18 V20 H8 Z M5,16 V4 H15");
        Line("Paste", "M9,4 H15 V7 H9 Z M7,5.5 H5.5 V20 H18.5 V5.5 H17 M9,12 H15 M9,16 H13");
        Line("Rename", "M4,17 V20 H7 L18,9 L15,6 Z M13,8 L16,11 M14,20 H20");
        Line("Delete", "M5,7 H19 M10,4 H14 M7,7 L8,20 H16 L17,7 M10.5,11 V16 M13.5,11 V16");
        Line("Share", "M12,4 V15 M8,8 L12,4 L16,8 M6,12 V20 H18 V12");
        Line("Sort", "M7,5 V19 M4,16 L7,19 L10,16 M17,19 V5 M14,8 L17,5 L20,8");
        Line("Group", "M4,5 H20 M7,9 H20 M7,13 H20 M4,17 H20 M7,21 H20");
        Line("LayoutDetails", "M4,6 H20 M4,10 H20 M4,14 H20 M4,18 H20");
        Line("LayoutList", "M4,6 H6 M9,6 H20 M4,12 H6 M9,12 H20 M4,18 H6 M9,18 H20");
        Line("LayoutTiles", "M4,4 H10 V10 H4 Z M14,4 H20 V10 H14 Z M4,14 H10 V20 H4 Z M14,14 H20 V20 H14 Z");
        Line("LayoutGrid", "M4,4 H8 V8 H4 Z M10,4 H14 V8 H10 Z M16,4 H20 V8 H16 Z M4,10 H8 V14 H4 Z M10,10 H14 V14 H10 Z M16,10 H20 V14 H16 Z M4,16 H8 V20 H4 Z M10,16 H14 V20 H10 Z M16,16 H20 V20 H16 Z");
        Line("Settings", "M12,9 A3,3 0 1 1 11.99,9 Z M12,3 V5.5 M12,18.5 V21 M3,12 H5.5 M18.5,12 H21 M5.6,5.6 L7.4,7.4 M16.6,16.6 L18.4,18.4 M5.6,18.4 L7.4,16.6 M16.6,7.4 L18.4,5.6");
        Line("PreviewPane", "M4,5 H20 V19 H4 Z M14,5 V19");
        Line("SplitView", "M4,5 H20 V19 H4 Z M12,5 V19");
        Line("Sidebar", "M4,5 H20 V19 H4 Z M9,5 V19");
        Line("More", "M6,12 H6.01 M12,12 H12.01 M18,12 H18.01");
        Line("Pin", "M9,4 H15 L14,10 L17,13 H7 L10,10 Z M12,13 V20");
        Line("Terminal", "M4,5 H20 V19 H4 Z M7,9 L10,12 L7,15 M12,15 H16");
        Line("Properties", "M12,4 A8,8 0 1 1 11.99,4 Z M12,11 V16 M12,8 V8.01");
        Line("Info", "M12,4 A8,8 0 1 1 11.99,4 Z M12,11 V16 M12,8 V8.01");
        Line("OpenWith", "M14,4 H20 V10 M20,4 L11,13 M18,14 V20 H4 V6 H10");
        Line("Undo", "M9,7 L4,12 L9,17 M4,12 H15 A5,5 0 0 1 15,22");
        Line("Redo", "M15,7 L20,12 L15,17 M20,12 H9 A5,5 0 0 0 9,22");
        Line("Restore", "M5,12 A7,7 0 1 0 7,7 M5,4 V8 H9");
        Line("Archive", "M4,5 H20 V9 H4 Z M5,9 V19 H19 V9 M10,13 H14");
        Line("Extract", "M4,5 H20 V9 H4 Z M5,9 V19 H19 V9 M12,11 V17 M9,14 L12,17 L15,14");
        Line("Keyboard", "M3,6 H21 V18 H3 Z M7,10 H7.01 M11,10 H11.01 M15,10 H15.01 M7,14 H17");
        Line("Speed", "M4,16 A8,8 0 1 1 20,16 M12,16 L16,9");
        Line("Update", "M12,4 V15 M8,11 L12,15 L16,11 M5,20 H19");
        Line("Advanced", "M4,7 H14 M18,7 H20 M16,5 V9 M4,17 H6 M10,17 H20 M8,15 V19");
        Line("Appearance", "M12,4 A8,8 0 1 1 11.99,4 Z M12,4 V20 M12,4 A8,8 0 0 0 12,20");
        Line("Tag", "M4,4 H12 L20,12 L12,20 L4,12 Z M8,8 H8.01");
        Line("Check", "M5,12 L10,17 L19,7");
        Line("Pause", "M8,5 V19 M16,5 V19");
        Line("Play", "M7,5 L19,12 L7,19 Z");
        Line("Window", "M4,5 H20 V19 H4 Z M4,9 H20");
        Line("Hidden", "M3,12 C6,6 18,6 21,12 C18,18 6,18 3,12 Z M12,10 A2,2 0 1 1 11.99,10 Z M4,4 L20,20");
        Line("Filter", "M4,5 H20 L14,12 V19 L10,17 V12 Z");
        Line("Multiple", "M7,7 H19 V19 H7 Z M4,16 V4 H16");
        Line("Warning", "M12,4 L21,20 H3 Z M12,10 V14 M12,17 V17.01");
        Line("Error", "M12,4 A8,8 0 1 1 11.99,4 Z M9,9 L15,15 M15,9 L9,15");
        Line("Success", "M12,4 A8,8 0 1 1 11.99,4 Z M8,12 L11,15 L16,9");
        Line("Home", "M4,11 L12,4 L20,11 M6,9.5 V20 H10 V14 H14 V20 H18 V9.5");
        Line("Trash", "M5,7 H19 M10,4 H14 M7,7 L8,20 H16 L17,7");
        Line("Speed", "M4,16 A8,8 0 1 1 20,16 M12,16 L16,9");

        // ── Filled file-type icons (soft, flat, two-tone) ──
        var folderBack = Geometry.Parse("M2,6.5 C2,5.1 3.1,4 4.5,4 H9.2 L11.4,6 H19.5 C20.9,6 22,7.1 22,8.5 V18 C22,19.4 20.9,20.5 19.5,20.5 H4.5 C3.1,20.5 2,19.4 2,18 Z");
        var folderFront = Geometry.Parse("M2,9.5 C2,8.7 2.7,8 3.5,8 H20.5 C21.3,8 22,8.7 22,9.5 V18 C22,19.4 20.9,20.5 19.5,20.5 H4.5 C3.1,20.5 2,19.4 2,18 Z");
        var page = Geometry.Parse("M6,2 H14 L20,8 V20 C20,21.1 19.1,22 18,22 H6 C4.9,22 4,21.1 4,20 V4 C4,2.9 4.9,2 6,2 Z");
        var fold = Geometry.Parse("M14,2 L20,8 H15.5 C14.7,8 14,7.3 14,6.5 Z");

        IBrush Brush(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));

        map["Folder"] = new IconDefinition(null, [(folderBack, Brush("#E8A93A")), (folderFront, Brush("#F7C55B"))]);
        foreach (var known in new[] { "Desktop", "Documents", "Downloads", "Pictures", "Music", "Videos" })
        {
            var badge = known switch
            {
                "Downloads" => "M12,10.5 V16.5 M9.5,14 L12,16.5 L14.5,14",
                "Pictures" => "M8,17 L11,13.5 L13,15.5 L14.5,14 L16.5,17 Z",
                "Music" => "M11,16.5 A1.5,1.5 0 1 1 10.99,16.5 Z M12.5,16.5 V11 L15.5,10.5 V12",
                "Videos" => "M10,11 L15,13.75 L10,16.5 Z",
                "Documents" => "M9,11.5 H15 M9,14 H15 M9,16.5 H13",
                _ => "M8.5,11 H15.5 V15.5 H8.5 Z M12,15.5 V17 M10,17 H14",
            };
            map[known] = new IconDefinition(Geometry.Parse(badge), [(folderBack, Brush("#E8A93A")), (folderFront, Brush("#F7C55B"))]);
        }

        void FileIcon(string key, string color, string? glyph = null)
            => map[key] = new IconDefinition(glyph is null ? null : Geometry.Parse(glyph), [(page, Brush("#F3F4F6")), (fold, Brush("#D1D5DB")), (Geometry.Parse("M4,15 H20 V20 C20,21.1 19.1,22 18,22 H6 C4.9,22 4,21.1 4,20 Z"), Brush(color))]);

        FileIcon("File", "#9CA3AF");
        FileIcon("Text", "#6B7280", "M7.5,9 H12 M7.5,11.5 H16.5");
        FileIcon("Code", "#7C3AED", "M9.5,8.5 L7,11 L9.5,13.5 M14.5,8.5 L17,11 L14.5,13.5");
        FileIcon("Document", "#2563EB", "M7.5,9 H16.5 M7.5,11.5 H16.5");
        FileIcon("Spreadsheet", "#16A34A", "M7.5,8.5 H16.5 V13 H7.5 Z M12,8.5 V13");
        FileIcon("Presentation", "#EA580C", "M7.5,8.5 H16.5 V13 H7.5 Z");
        FileIcon("Pdf", "#DC2626", "M8,12.5 C10,7 12,7 16,12.5");
        FileIcon("Image", "#0EA5E9", "M7,13 L10,9.5 L12,11.5 L13.5,10 L17,13 Z");
        FileIcon("Video", "#DB2777", "M10,8 L15,10.75 L10,13.5 Z");
        FileIcon("Audio", "#9333EA", "M10,12.5 A1.5,1.5 0 1 1 9.99,12.5 Z M11.5,12.5 V7.5 L15,7 V8.5");
        FileIcon("Archive", "#CA8A04", "M11,3 V5 M13,5 V7 M11,7 V9 M13,9 V11 M11,11 H13 V13 H11 Z");
        FileIcon("Executable", "#475569", "M8,8 H16 V13 H8 Z M8,10 H16");
        FileIcon("Font", "#0F766E", "M9,13 L12,6.5 L15,13 M10.2,10.6 H13.8");
        FileIcon("DiskImage", "#64748B", "M12,7 A3.5,3.5 0 1 1 11.99,7 Z M12,10 H12.01");
        FileIcon("Link", "#0891B2", "M10,12 H14 M9,9.5 H8 A2.5,2.5 0 0 0 8,14.5 H9 M15,9.5 H16 A2.5,2.5 0 0 1 16,14.5 H15");

        var drive = Geometry.Parse("M3,13 L5.5,6 C5.8,5.4 6.4,5 7.1,5 H16.9 C17.6,5 18.2,5.4 18.5,6 L21,13 V17 C21,18.1 20.1,19 19,19 H5 C3.9,19 3,18.1 3,17 Z");
        var driveFace = Geometry.Parse("M3,13 H21 V17 C21,18.1 20.1,19 19,19 H5 C3.9,19 3,18.1 3,17 Z");
        map["Drive"] = new IconDefinition(Geometry.Parse("M16,16 H18"), [(drive, Brush("#9CA3AF")), (driveFace, Brush("#6B7280"))]);
        map["DriveRemovable"] = new IconDefinition(Geometry.Parse("M16,16 H18"), [(drive, Brush("#60A5FA")), (driveFace, Brush("#3B82F6"))]);
        map["DriveNetwork"] = new IconDefinition(Geometry.Parse("M12,19 V22 M7,22 H17"), [(drive, Brush("#34D399")), (driveFace, Brush("#10B981"))]);
        map["DriveOptical"] = new IconDefinition(null, [(Geometry.Parse("M12,3 A9,9 0 1 1 11.99,3 Z"), Brush("#CBD5E1")), (Geometry.Parse("M12,10 A2,2 0 1 1 11.99,10 Z"), Brush("#64748B"))]);

        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
