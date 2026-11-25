using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Navigator.Core.Platform;

namespace Navigator.Platform.Icons;

/// <summary>
/// Linux desktop icons from the user's icon theme (GNOME, KDE, Xfce…): the icon a file manager shows for a file type,
/// for folders, user folders, drives and the trash.
/// <para>
/// File type → icon names comes from shared-mime-info through GIO (the same database every desktop uses); names → files
/// from <see cref="FreedesktopIconTheme"/>; files → pixels through gdk-pixbuf, which also renders SVG themes when the
/// SVG loader is installed (it is on every mainstream desktop). All three libraries are part of any GTK or Qt desktop
/// install; when one is missing the provider reports itself unsupported and Navigator keeps its own icons.
/// </para>
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed partial class FreedesktopIcons : ISystemIconProvider, IDisposable
{
    private const string Glib = "libglib-2.0.so.0";
    private const string GObject = "libgobject-2.0.so.0";
    private const string Gio = "libgio-2.0.so.0";
    private const string Pixbuf = "libgdk_pixbuf-2.0.so.0";

    private readonly IKnownFolderProvider? _knownFolders;
    private readonly Lazy<Task<FreedesktopIconTheme>> _theme;
    private readonly ConcurrentDictionary<string, string[]> _namesByType = new(StringComparer.Ordinal);

    // Decoding is CPU work; two at a time keeps a large folder from flooding the thread pool.
    private readonly SemaphoreSlim _decoders = new(2, 2);

    public FreedesktopIcons(IPlatformInfo platform, IKnownFolderProvider? knownFolders)
    {
        ArgumentNullException.ThrowIfNull(platform);
        _knownFolders = knownFolders;
        IsSupported = Libraries.Available;
        _theme = new Lazy<Task<FreedesktopIconTheme>>(() => Task.Run(async () =>
        {
            var name = await IconThemeName.DetectAsync(platform).ConfigureAwait(false);
            return FreedesktopIconTheme.Load(name, FreedesktopIconTheme.DefaultBaseDirectories(platform.HomeDirectory));
        }));
    }

    public bool IsSupported { get; }

    public void Dispose() => _decoders.Dispose();

    public async Task<SystemIconImage?> GetIconAsync(string key, int pixelSize, CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
            return null;
        if (SystemIconKey.TryParseThemed(key, out var themed) && Path.IsPathRooted(themed))
            return await DecodeFileAsync(themed, pixelSize, cancellationToken).ConfigureAwait(false);
        var names = NamesFor(key);
        if (names.Length == 0)
            return null;

        var theme = await _theme.Value.ConfigureAwait(false);
        await _decoders.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                foreach (var file in theme.Find(names, pixelSize))
                {
                    // "-symbolic" icons are single-color glyphs meant to be painted in the text color.
                    if (Decode(file, pixelSize) is { } image)
                        return Path.GetFileNameWithoutExtension(file).EndsWith("-symbolic", StringComparison.Ordinal) ? image with { IsTemplate = true } : image;
                }

                return null;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _decoders.Release();
        }
    }

    private async Task<SystemIconImage?> DecodeFileAsync(string file, int pixelSize, CancellationToken cancellationToken)
    {
        await _decoders.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => File.Exists(file) ? Decode(file, pixelSize) : null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _decoders.Release();
        }
    }

    /// <summary>Theme icon names for a key, most specific first.</summary>
    private string[] NamesFor(string key)
    {
        if (SystemIconKey.TryParseAction(key, out var command))
            return ActionNames(command);
        if (SystemIconKey.TryParseThemed(key, out var themed))
            return [themed];
        if (SystemIconKey.TryParseFileType(key, out var extension))
            return _namesByType.GetOrAdd(extension, NamesForType);
        if (!SystemIconKey.TryParse(key, out var kind, out var path))
            return [];
        if (path is null)
            return StockNames(kind);
        if (KnownFolderNames(path) is { } known)
            return known;
        return Directory.Exists(path) ? ["folder"] : _namesByType.GetOrAdd(Path.GetExtension(path).ToLowerInvariant(), NamesForType);
    }

    /// <summary>freedesktop action icon names for toolbar commands; the recolorable symbolic variant first.</summary>
    private static string[] ActionNames(string command) => command switch
    {
        "New" => ["list-add-symbolic", "document-new-symbolic", "list-add", "document-new"],
        "Cut" => ["edit-cut-symbolic", "edit-cut"],
        "Copy" => ["edit-copy-symbolic", "edit-copy"],
        "Paste" => ["edit-paste-symbolic", "edit-paste"],
        "Rename" => ["edit-rename-symbolic", "document-edit-symbolic", "edit-rename", "document-edit"],
        "Delete" => ["edit-delete-symbolic", "user-trash-symbolic", "edit-delete"],
        "Share" => ["emblem-shared-symbolic", "document-send-symbolic", "emblem-shared"],
        "Sort" => ["view-sort-ascending-symbolic", "view-sort-symbolic", "view-sort-ascending"],
        "Group" => ["view-group-symbolic", "view-list-tree-symbolic", "view-group"],
        "Layout" => ["view-list-details-symbolic", "view-list-symbolic", "view-list-details"],
        "More" => ["view-more-symbolic", "open-menu-symbolic", "view-more-horizontal-symbolic", "application-menu"],
        "Properties" => ["document-properties-symbolic", "document-properties"],
        "Download" => ["folder-download-symbolic", "download-symbolic"],
        "Upload" => ["document-send-symbolic", "upload-symbolic"],
        _ => [],
    };

    private static string[] StockNames(SystemIconKind kind) => kind switch
    {
        SystemIconKind.Folder => ["folder"],
        SystemIconKind.Computer => ["computer"],
        SystemIconKind.Network => ["network-workgroup", "network-server"],
        SystemIconKind.DriveFixed or SystemIconKind.DriveRam => ["drive-harddisk"],
        SystemIconKind.DriveRemovable => ["drive-removable-media", "media-removable", "drive-harddisk"],
        SystemIconKind.DriveNetwork => ["folder-remote", "network-server", "drive-harddisk"],
        SystemIconKind.DriveOptical => ["drive-optical", "media-optical"],
        SystemIconKind.RecycleBin => ["user-trash"],
        SystemIconKind.RecycleBinFull => ["user-trash-full", "user-trash"],
        _ => [],
    };

    private string[]? KnownFolderNames(string path)
    {
        if (_knownFolders is null)
            return null;
        foreach (var (folder, names) in KnownFolderIcons)
        {
            if (_knownFolders.GetPath(folder) is { } known && string.Equals(Path.TrimEndingDirectorySeparator(known), Path.TrimEndingDirectorySeparator(path), StringComparison.Ordinal))
                return names;
        }

        return null;
    }

    private static readonly (KnownFolder Folder, string[] Names)[] KnownFolderIcons =
    [
        (KnownFolder.Home, ["user-home", "folder"]),
        (KnownFolder.Desktop, ["user-desktop", "folder"]),
        (KnownFolder.Documents, ["folder-documents", "folder"]),
        (KnownFolder.Downloads, ["folder-download", "folder"]),
        (KnownFolder.Pictures, ["folder-pictures", "folder"]),
        (KnownFolder.Music, ["folder-music", "folder"]),
        (KnownFolder.Videos, ["folder-videos", "folder"]),
    ];

    /// <summary>shared-mime-info's guess from the name alone ("x.pdf" → application/pdf → application-pdf, …).</summary>
    private static unsafe string[] NamesForType(string extension)
    {
        var names = new List<string>(4);
        var probe = Encoding.UTF8.GetBytes("file" + extension + "\0");
        nint contentType;
        fixed (byte* name = probe)
        {
            int uncertain;
            contentType = g_content_type_guess(name, null, 0, &uncertain);
        }

        if (contentType == 0)
            return ["text-x-generic"];
        try
        {
            var icon = g_content_type_get_icon(contentType);
            if (icon != 0)
            {
                try
                {
                    if (g_type_check_instance_is_a(icon, g_themed_icon_get_type()) != 0)
                    {
                        for (var entry = g_themed_icon_get_names(icon); entry is not null && *entry != 0; entry++)
                        {
                            if (Marshal.PtrToStringUTF8(*entry) is { Length: > 0 } iconName)
                                names.Add(iconName);
                        }
                    }
                }
                finally
                {
                    g_object_unref(icon);
                }
            }

            var generic = g_content_type_get_generic_icon_name(contentType);
            if (generic != 0)
            {
                if (Marshal.PtrToStringUTF8(generic) is { Length: > 0 } genericName && !names.Contains(genericName))
                    names.Add(genericName);
                g_free(generic);
            }
        }
        finally
        {
            g_free(contentType);
        }

        if (!names.Contains("text-x-generic"))
            names.Add("text-x-generic"); // every theme has it; better than no icon
        return [.. names];
    }

    /// <summary>gdk-pixbuf decodes PNG and (with its SVG loader) SVG at the requested size → BGRA, straight alpha.</summary>
    private static unsafe SystemIconImage? Decode(string file, int pixelSize)
    {
        var path = Encoding.UTF8.GetBytes(file + "\0");
        nint failure = 0;
        nint image;
        fixed (byte* name = path)
            image = gdk_pixbuf_new_from_file_at_size(name, pixelSize, pixelSize, &failure);
        if (failure != 0)
            g_error_free(failure);
        if (image == 0)
            return null;

        try
        {
            int width = gdk_pixbuf_get_width(image), height = gdk_pixbuf_get_height(image);
            int stride = gdk_pixbuf_get_rowstride(image), channels = gdk_pixbuf_get_n_channels(image);
            if (width <= 0 || height <= 0 || width > 1024 || height > 1024 || gdk_pixbuf_get_bits_per_sample(image) != 8 || channels is not (3 or 4))
                return null;

            var source = gdk_pixbuf_read_pixels(image);
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                var row = source + ((long)y * stride);
                for (var x = 0; x < width; x++)
                {
                    var from = row + (x * channels);
                    var to = ((y * width) + x) * 4;
                    pixels[to] = from[2];     // B
                    pixels[to + 1] = from[1]; // G
                    pixels[to + 2] = from[0]; // R
                    pixels[to + 3] = channels == 4 ? from[3] : (byte)255;
                }
            }

            return new SystemIconImage(width, height, pixels);
        }
        finally
        {
            g_object_unref(image);
        }
    }

    private static class Libraries
    {
        public static readonly bool Available = Probe();

        private static bool Probe()
        {
            foreach (var library in new[] { Glib, GObject, Gio, Pixbuf })
            {
                if (!NativeLibrary.TryLoad(library, out var handle))
                    return false;
                NativeLibrary.Free(handle);
            }

            return true;
        }
    }

    [LibraryImport(Glib)]
    private static partial void g_free(nint memory);

    [LibraryImport(Glib)]
    private static partial void g_error_free(nint error);

    [LibraryImport(GObject)]
    private static partial void g_object_unref(nint instance);

    [LibraryImport(GObject)]
    private static partial int g_type_check_instance_is_a(nint instance, nuint type);

    [LibraryImport(Gio)]
    private static unsafe partial nint g_content_type_guess(byte* fileName, byte* data, nuint dataSize, int* resultUncertain);

    [LibraryImport(Gio)]
    private static partial nint g_content_type_get_icon(nint contentType);

    [LibraryImport(Gio)]
    private static partial nint g_content_type_get_generic_icon_name(nint contentType);

    [LibraryImport(Gio)]
    private static partial nuint g_themed_icon_get_type();

    [LibraryImport(Gio)]
    private static unsafe partial nint* g_themed_icon_get_names(nint icon);

    [LibraryImport(Pixbuf)]
    private static unsafe partial nint gdk_pixbuf_new_from_file_at_size(byte* fileName, int width, int height, nint* error);

    [LibraryImport(Pixbuf)]
    private static partial int gdk_pixbuf_get_width(nint pixbuf);

    [LibraryImport(Pixbuf)]
    private static partial int gdk_pixbuf_get_height(nint pixbuf);

    [LibraryImport(Pixbuf)]
    private static partial int gdk_pixbuf_get_rowstride(nint pixbuf);

    [LibraryImport(Pixbuf)]
    private static partial int gdk_pixbuf_get_n_channels(nint pixbuf);

    [LibraryImport(Pixbuf)]
    private static partial int gdk_pixbuf_get_bits_per_sample(nint pixbuf);

    [LibraryImport(Pixbuf)]
    private static unsafe partial byte* gdk_pixbuf_read_pixels(nint pixbuf);
}

/// <summary>Which icon theme the desktop uses: KDE settings, then GTK settings files, then GNOME's gsettings, then Adwaita.</summary>
internal static class IconThemeName
{
    public static async Task<string> DetectAsync(IPlatformInfo platform)
    {
        var home = platform.HomeDirectory;
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } custom ? custom : Path.Combine(home, ".config");
        var desktop = platform.DesktopEnvironment ?? string.Empty;

        if (desktop.Contains("KDE", StringComparison.OrdinalIgnoreCase))
            return ReadKey(Path.Combine(configHome, "kdeglobals"), "Icons", "Theme") ?? "breeze";

        foreach (var gtk in new[] { "gtk-4.0", "gtk-3.0" })
        {
            if (ReadKey(Path.Combine(configHome, gtk, "settings.ini"), "Settings", "gtk-icon-theme-name") is { } fromGtk)
                return fromGtk;
        }

        if (ProcessRunner.FindOnPath("gsettings") is { } gsettings)
        {
            var output = await ProcessRunner.RunForOutputAsync(gsettings, ["get", "org.gnome.desktop.interface", "icon-theme"],
                TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
            var value = output?.Trim().Trim('\'', '"');
            if (!string.IsNullOrEmpty(value))
                return value;
        }

        return "Adwaita";
    }

    /// <summary>Value of <paramref name="key"/> in <paramref name="section"/> of an ini-style file, or null.</summary>
    internal static string? ReadKey(string file, string section, string key)
    {
        if (!File.Exists(file))
            return null;
        string[] lines;
        try
        {
            lines = File.ReadAllLines(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var inSection = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inSection = string.Equals(line, "[" + section + "]", StringComparison.Ordinal);
                continue;
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (inSection && equals > 0 && string.Equals(line[..equals].Trim(), key, StringComparison.Ordinal))
            {
                var value = line[(equals + 1)..].Trim();
                return value.Length > 0 ? value : null;
            }
        }

        return null;
    }
}
