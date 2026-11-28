using System.Collections.Concurrent;
using System.Collections.Frozen;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Material.Icons;

namespace Navigator.App.Controls;

/// <summary>One drawable icon: filled layers on a 24x24 grid; a layer without a brush takes the control's Foreground.</summary>
internal sealed record IconDefinition(IReadOnlyList<(Geometry Geometry, IBrush? Brush)> Layers);

/// <summary>
/// Navigator's icons, taken from the Material Design Icons set of the Material.Icons package
/// (<see cref="MaterialIconDataProvider"/>). Navigator's own names (used by view models: "Folder", "Pdf", "Cut"…) map
/// to an icon and, for file types, drives and clouds, a color; any other <see cref="MaterialIconKind"/> name works
/// as a key too ("TagRemove"). Each geometry is parsed the first time it is drawn and then shared by every instance, so
/// thousands of rows cost one parse per icon. Icons the operating system draws are not here (<see cref="SystemIcons"/>).
/// </summary>
internal static class IconCatalog
{
    private static readonly FrozenDictionary<string, (MaterialIconKind Kind, string? Color)> Names = BuildNames();
    private static readonly ConcurrentDictionary<string, IconDefinition?> Parsed = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, IBrush> Tints = new(StringComparer.Ordinal);

    public static IconDefinition? Get(string? key)
        => string.IsNullOrEmpty(key) ? null : Parsed.GetOrAdd(key, Create);

    private static IconDefinition? Create(string key)
    {
        MaterialIconKind kind;
        string? color = null;
        if (Names.TryGetValue(key, out var named))
            (kind, color) = named;
        else if (!Enum.TryParse(key, ignoreCase: false, out kind) || !Enum.IsDefined(kind))
            return null;

        var geometry = Geometry.Parse(MaterialIconDataProvider.GetData(kind));
        var brush = color is null ? null : Tints.GetOrAdd(color, static hex => new ImmutableSolidColorBrush(Color.Parse(hex)));
        return new IconDefinition([(geometry, brush)]);
    }

    private static FrozenDictionary<string, (MaterialIconKind Kind, string? Color)> BuildNames()
    {
        var map = new Dictionary<string, (MaterialIconKind, string?)>(StringComparer.Ordinal);
        void Glyph(string key, MaterialIconKind kind) => map[key] = (kind, null);
        void Tinted(string key, MaterialIconKind kind, string color) => map[key] = (kind, color);

        // ── Commands and UI (drawn in the text color) ──
        Glyph("Add", MaterialIconKind.Plus);
        Glyph("Advanced", MaterialIconKind.Tune);
        Glyph("Appearance", MaterialIconKind.Palette);
        Glyph("Back", MaterialIconKind.ArrowLeft);
        Glyph("Forward", MaterialIconKind.ArrowRight);
        Glyph("Up", MaterialIconKind.ArrowUp);
        Glyph("Refresh", MaterialIconKind.Refresh);
        Glyph("History", MaterialIconKind.History);
        Glyph("Home", MaterialIconKind.Home);
        Glyph("Check", MaterialIconKind.Check);
        Glyph("ChevronDown", MaterialIconKind.ChevronDown);
        Glyph("ChevronRight", MaterialIconKind.ChevronRight);
        Glyph("Close", MaterialIconKind.Close);
        Glyph("Search", MaterialIconKind.Magnify);
        Glyph("Filter", MaterialIconKind.Filter);
        Glyph("Cut", MaterialIconKind.ContentCut);
        Glyph("Copy", MaterialIconKind.ContentCopy);
        Glyph("Paste", MaterialIconKind.ContentPaste);
        Glyph("Multiple", MaterialIconKind.ContentDuplicate);
        Glyph("Rename", MaterialIconKind.RenameBox);
        Glyph("Delete", MaterialIconKind.DeleteOutline);
        Glyph("Trash", MaterialIconKind.DeleteOutline);
        Glyph("Restore", MaterialIconKind.DeleteRestore);
        Glyph("Properties", MaterialIconKind.Wrench);
        Glyph("Info", MaterialIconKind.InformationOutline);
        Glyph("Share", MaterialIconKind.Share);
        Glyph("OpenWith", MaterialIconKind.OpenInNew);
        Glyph("Window", MaterialIconKind.DockWindow);
        Glyph("SplitView", MaterialIconKind.ViewSplitVertical);
        Glyph("Sidebar", MaterialIconKind.DockLeft);
        Glyph("PreviewPane", MaterialIconKind.DockRight);
        Glyph("Pin", MaterialIconKind.Pin);
        Glyph("Unpin", MaterialIconKind.PinOff);
        Glyph("Tag", MaterialIconKind.TagOutline);
        Glyph("TagRemove", MaterialIconKind.TagRemoveOutline);
        Glyph("Terminal", MaterialIconKind.Console);
        Glyph("CodeEditor", MaterialIconKind.CodeTags);
        Glyph("Compress", MaterialIconKind.ArchiveArrowDown);
        Glyph("Extract", MaterialIconKind.ArchiveArrowUp);
        Glyph("FolderAdd", MaterialIconKind.FolderPlusOutline);
        Glyph("Shortcut", MaterialIconKind.ArrowTopRight);
        Glyph("SendTo", MaterialIconKind.Send);
        Glyph("Shield", MaterialIconKind.ShieldAccount);
        Glyph("UserSwitch", MaterialIconKind.AccountSwitch);
        Glyph("Download", MaterialIconKind.Download);
        Glyph("Upload", MaterialIconKind.Upload);
        Glyph("More", MaterialIconKind.DotsHorizontal);
        Glyph("Sort", MaterialIconKind.Sort);
        Glyph("Group", MaterialIconKind.ViewAgenda);
        Glyph("LayoutDetails", MaterialIconKind.TableLarge);
        Glyph("LayoutList", MaterialIconKind.ViewList);
        Glyph("LayoutTiles", MaterialIconKind.ViewModule);
        Glyph("LayoutGrid", MaterialIconKind.ViewGrid);
        Glyph("Hidden", MaterialIconKind.EyeOff);
        Glyph("Undo", MaterialIconKind.Undo);
        Glyph("Redo", MaterialIconKind.Redo);
        Glyph("Settings", MaterialIconKind.Cog);
        Glyph("Keyboard", MaterialIconKind.Keyboard);
        // Map network drive / Connect to server: the counterpart of LanDisconnect, and a clean outline at 12 px (the
        // filled folder-with-network glyph blurred into a blob at the sidebar header's size).
        Glyph("MapNetworkDrive", MaterialIconKind.LanConnect);
        Glyph("Speed", MaterialIconKind.Speedometer);
        Glyph("Update", MaterialIconKind.Update);
        Glyph("Plugin", MaterialIconKind.Puzzle);
        Glyph("SignOut", MaterialIconKind.Logout);
        Glyph("Pause", MaterialIconKind.Pause);
        Glyph("Play", MaterialIconKind.Play);
        Glyph("Warning", MaterialIconKind.Alert);
        Glyph("Error", MaterialIconKind.AlertCircle);
        Glyph("Success", MaterialIconKind.CheckCircle);
        Glyph("Layers", MaterialIconKind.Layers);

        // ── File types (colored; rows show the OS icon instead where the platform provides one) ──
        Tinted("Folder", MaterialIconKind.Folder, "#F2B035");
        Tinted("Desktop", MaterialIconKind.Monitor, "#F2B035");
        Tinted("Documents", MaterialIconKind.FolderText, "#F2B035");
        Tinted("Downloads", MaterialIconKind.FolderDownload, "#F2B035");
        Tinted("Pictures", MaterialIconKind.FolderImage, "#F2B035");
        Tinted("Music", MaterialIconKind.FolderMusic, "#F2B035");
        Tinted("Videos", MaterialIconKind.FolderPlay, "#F2B035");
        Tinted("File", MaterialIconKind.File, "#9CA3AF");
        Tinted("Text", MaterialIconKind.FileDocument, "#6B7280");
        Tinted("Code", MaterialIconKind.FileCode, "#7C3AED");
        Tinted("Document", MaterialIconKind.FileWord, "#2563EB");
        Tinted("Spreadsheet", MaterialIconKind.FileExcel, "#16A34A");
        Tinted("Presentation", MaterialIconKind.FilePowerpoint, "#EA580C");
        Tinted("Pdf", MaterialIconKind.FilePdfBox, "#DC2626");
        Tinted("Image", MaterialIconKind.FileImage, "#0EA5E9");
        Tinted("Video", MaterialIconKind.FileVideo, "#DB2777");
        Tinted("Audio", MaterialIconKind.FileMusic, "#9333EA");
        Tinted("Archive", MaterialIconKind.ZipBox, "#CA8A04");
        Tinted("Executable", MaterialIconKind.Application, "#475569");
        Tinted("Font", MaterialIconKind.FormatFont, "#0F766E");
        Tinted("DiskImage", MaterialIconKind.Disc, "#64748B");
        Tinted("Link", MaterialIconKind.LinkVariant, "#0891B2");

        // ── Drives, servers, clouds (one shape per kind, colored; no service logos) ──
        Tinted("Drive", MaterialIconKind.Harddisk, "#6B7280");
        Tinted("DriveRemovable", MaterialIconKind.Usb, "#3B82F6");
        Tinted("DriveNetwork", MaterialIconKind.ServerNetwork, "#10B981");
        Tinted("DriveOptical", MaterialIconKind.Disc, "#64748B");
        Tinted("Server", MaterialIconKind.Server, "#64748B");
        Tinted("ServerSsh", MaterialIconKind.Server, "#6366F1");
        Tinted("ServerFtp", MaterialIconKind.Server, "#F59E0B");
        Tinted("Cloud", MaterialIconKind.Cloud, "#38BDF8");
        Tinted("CloudGoogleDrive", MaterialIconKind.Cloud, "#22C55E");
        Tinted("CloudOneDrive", MaterialIconKind.Cloud, "#3B82F6");

        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
