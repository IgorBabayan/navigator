using System.Collections.Frozen;

namespace Navigator.Presentation.Items;

/// <summary>Visual category of an item. Drives the icon and the generic "type" text.</summary>
public enum ItemCategory
{
    Folder,
    File,
    Image,
    Video,
    Audio,
    Archive,
    Document,
    Spreadsheet,
    Presentation,
    Pdf,
    Text,
    Code,
    Executable,
    Font,
    DiskImage,
    Link,
}

public static class ItemCategories
{
    private static readonly FrozenDictionary<string, ItemCategory> ByExtension = Build();

    public static ItemCategory FromExtension(string extension)
        => ByExtension.TryGetValue(extension, out var category) ? category : ItemCategory.File;

    private static FrozenDictionary<string, ItemCategory> Build()
    {
        var map = new Dictionary<string, ItemCategory>(StringComparer.OrdinalIgnoreCase);
        void Add(ItemCategory category, params string[] extensions)
        {
            foreach (var extension in extensions)
                map[extension] = category;
        }

        Add(ItemCategory.Image, ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff", ".svg", ".heic", ".heif", ".avif", ".raw", ".cr2", ".nef", ".psd");
        Add(ItemCategory.Video, ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".m4v", ".mpg", ".mpeg", ".3gp");
        Add(ItemCategory.Audio, ".mp3", ".wav", ".flac", ".aac", ".ogg", ".opus", ".m4a", ".wma", ".aiff", ".mid", ".midi");
        Add(ItemCategory.Archive, ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".cab", ".lz", ".lzma");
        Add(ItemCategory.Document, ".doc", ".docx", ".odt", ".rtf", ".pages");
        Add(ItemCategory.Spreadsheet, ".xls", ".xlsx", ".ods", ".csv", ".numbers");
        Add(ItemCategory.Presentation, ".ppt", ".pptx", ".odp", ".key");
        Add(ItemCategory.Pdf, ".pdf");
        Add(ItemCategory.Text, ".txt", ".md", ".markdown", ".log", ".ini", ".cfg", ".conf", ".rst", ".nfo");
        Add(ItemCategory.Code, ".cs", ".csproj", ".sln", ".slnx", ".axaml", ".xaml", ".xml", ".json", ".yml", ".yaml", ".toml", ".js", ".ts", ".tsx", ".jsx",
            ".py", ".rb", ".go", ".rs", ".java", ".kt", ".c", ".h", ".cpp", ".hpp", ".swift", ".sh", ".ps1", ".bat", ".cmd", ".html", ".htm", ".css", ".scss",
            ".sql", ".lua", ".php", ".fs", ".vb", ".dart", ".zig", ".nix");
        Add(ItemCategory.Executable, ".exe", ".msi", ".appimage", ".deb", ".rpm", ".apk", ".app", ".com", ".dll", ".so", ".dylib");
        Add(ItemCategory.Font, ".ttf", ".otf", ".woff", ".woff2");
        Add(ItemCategory.DiskImage, ".iso", ".img", ".dmg", ".vhd", ".vhdx", ".qcow2");
        Add(ItemCategory.Link, ".lnk", ".url", ".desktop", ".webloc");
        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsPreviewableText(string extension)
        => FromExtension(extension) is ItemCategory.Text or ItemCategory.Code || extension is ".csv" or ".gitignore" or ".editorconfig";

    public static bool IsPreviewableImage(string extension)
        => extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".ico";
}
