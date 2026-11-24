using System.IO.Compression;

namespace Navigator.Plugins;

/// <summary>
/// A plugin package (.navplugin): a ZIP archive with plugin.json and the plugin's assemblies at its root.
/// Extraction is defensive: no absolute paths, no "..", no links, no alternate data streams, bounded entry count
/// and size, and every file is created new (never overwritten).
/// </summary>
public static class PluginPackage
{
    public const int MaxEntries = 2000;
    public const long MaxTotalBytes = 256L * 1024 * 1024;
    private const int UnixFileTypeMask = 0xF000 << 16;
    private const int UnixSymbolicLink = 0xA000 << 16;

    /// <summary>Reads and validates plugin.json without extracting anything.</summary>
    public static PluginManifest ReadManifest(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        return ReadManifest(archive);
    }

    private static PluginManifest ReadManifest(ZipArchive archive)
    {
        var entry = archive.GetEntry(PluginManifest.FileName)
                    ?? throw new InvalidDataException("The package has no plugin.json at its root.");
        if (entry.Length > PluginManifest.MaxBytes)
            throw new InvalidDataException("plugin.json is unexpectedly large.");
        using var stream = entry.Open();
        using var buffer = new MemoryStream((int)entry.Length);
        stream.CopyTo(buffer);
        var manifest = PluginManifest.Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        if (archive.GetEntry(manifest.Assembly) is null)
            throw new InvalidDataException($"The package does not contain {manifest.Assembly}.");
        return manifest;
    }

    /// <summary>Extracts the package into the empty or missing <paramref name="destination"/> and returns its manifest.</summary>
    public static PluginManifest Extract(string packagePath, string destination, CancellationToken cancellationToken = default)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var manifest = ReadManifest(archive);
        if (archive.Entries.Count > MaxEntries)
            throw new InvalidDataException("The package contains too many files.");

        var root = Path.GetFullPath(destination);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);

        long total = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            if (name.Length == 0 || name.StartsWith('/') || name.Contains(':', StringComparison.Ordinal)
                || name.Split('/').Any(segment => segment is ".." or "."))
                throw new InvalidDataException($"The package contains an unsafe path '{entry.FullName}'.");
            if ((entry.ExternalAttributes & UnixFileTypeMask) == UnixSymbolicLink)
                throw new InvalidDataException($"The package contains a link '{entry.FullName}'.");

            var target = Path.GetFullPath(Path.Combine(root, name));
            if (!target.StartsWith(rootWithSeparator, PathComparison))
                throw new InvalidDataException($"The package contains an unsafe path '{entry.FullName}'.");

            if (name.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            total += entry.Length;
            if (entry.Length < 0 || total > MaxTotalBytes)
                throw new InvalidDataException("The package is too large.");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920);
            CopyBounded(source, output, entry.Length);
        }

        return manifest;
    }

    // The declared length is checked while copying: a crafted entry cannot inflate beyond what it announced.
    private static void CopyBounded(Stream source, Stream destination, long declared)
    {
        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            written += read;
            if (written > declared)
                throw new InvalidDataException("A package entry is larger than declared.");
            destination.Write(buffer, 0, read);
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
