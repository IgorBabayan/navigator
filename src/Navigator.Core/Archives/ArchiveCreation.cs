using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using Navigator.Core.IO;
using Navigator.Core.Operations;

namespace Navigator.Core.Archives;

/// <summary>
/// "Create archive" for every format the Compress menu offers: ZIP and TAR(.GZ) with the base class library, 7z through
/// a 7-Zip or libarchive program already installed on the machine (<see cref="SevenZipTool"/>).
/// </summary>
public static class ArchiveCreation
{
    /// <summary>File suffix of an archive format, with its dot.</summary>
    public static string Suffix(ArchiveFormat format) => format switch
    {
        ArchiveFormat.Zip => ".zip",
        ArchiveFormat.Tar => ".tar",
        ArchiveFormat.TarGz => ".tar.gz",
        ArchiveFormat.SevenZip => ".7z",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Not a format Navigator can create."),
    };

    /// <summary>Formats that can be created on this machine, in menu order.</summary>
    public static IReadOnlyList<ArchiveFormat> Creatable()
        => SevenZipTool.IsAvailable
            ? [ArchiveFormat.Zip, ArchiveFormat.SevenZip, ArchiveFormat.Tar, ArchiveFormat.TarGz]
            : [ArchiveFormat.Zip, ArchiveFormat.Tar, ArchiveFormat.TarGz];

    public static Task CreateAsync(IReadOnlyList<string> sources, string archivePath, ArchiveFormat format, CompressionLevel level, OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(context);
        return format switch
        {
            ArchiveFormat.Zip => ArchiveService.CreateZipAsync(sources, archivePath, level, context),
            ArchiveFormat.Tar or ArchiveFormat.TarGz => CreateTarAsync(sources, archivePath, format == ArchiveFormat.TarGz ? level : null, context),
            ArchiveFormat.SevenZip => SevenZipTool.CreateAsync(sources, archivePath, level, context),
            _ => throw new OperationStepException(FileOperationErrorKind.NotSupported, $"Archives of type {format} cannot be created."),
        };
    }

    /// <summary>A POSIX (pax) tar, gzip-compressed when <paramref name="gzipLevel"/> is set. Written to a temporary file first.</summary>
    private static async Task CreateTarAsync(IReadOnlyList<string> sources, string archivePath, CompressionLevel? gzipLevel, OperationContext context)
    {
        var items = new List<(string FullPath, string EntryName, bool IsDirectory, long Length)>();
        foreach (var source in sources)
        {
            var trimmed = PathUtil.TrimTrailingSeparator(source);
            ArchiveService.Collect(trimmed, Path.GetDirectoryName(trimmed)!, items);
        }

        context.AddTotals(items.Sum(i => Math.Max(0, i.Length)), items.Count);
        context.MarkRunning();

        var partial = archivePath + ".navigator-partial";
        var finished = false;
        try
        {
            var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            Stream sink = gzipLevel is { } level ? new GZipStream(file, level, leaveOpen: false) : file;
            await using (var writer = new TarWriter(sink, TarEntryFormat.Pax, leaveOpen: false))
            {
                foreach (var (fullPath, entryName, isDirectory, length) in items)
                {
                    await context.CheckpointAsync().ConfigureAwait(false);
                    context.SetCurrentItem(fullPath);
                    var name = isDirectory ? entryName + "/" : entryName;
                    await context.RunWithRecoveryAsync(fullPath,
                        async () => await writer.WriteEntryAsync(fullPath, name, context.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
                    context.ReportBytes(Math.Max(0, length));
                    context.CompleteItem();
                }
            }

            File.Move(partial, archivePath, overwrite: false);
            finished = true;
            context.RecordTransferred(new TransferredItem(sources[0], archivePath, IsDirectory: false, WasMoved: false));
        }
        finally
        {
            if (!finished && File.Exists(partial))
                File.Delete(partial);
        }
    }
}

/// <summary>
/// 7z archives are written by a program the user already has: 7-Zip's command line (<c>7z</c>, <c>7zz</c>, <c>7za</c>),
/// or libarchive's <c>bsdtar</c> (macOS, most Linux distributions, and Windows 11's own <c>tar.exe</c>, which creates 7z
/// since 24H2). The search runs once, off the UI thread when <see cref="Warm"/> is called at startup.
/// </summary>
public static class SevenZipTool
{
    private static readonly Lazy<(string Program, bool IsLibarchive)?> Found = new(Search, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>True when a program that can write 7z was found. The first read searches (a few file probes).</summary>
    public static bool IsAvailable => Found.Value is not null;

    /// <summary>Starts the search in the background so menus never wait for it.</summary>
    public static void Warm() => _ = Task.Run(() => Found.Value);

    internal static async Task CreateAsync(IReadOnlyList<string> sources, string archivePath, CompressionLevel level, OperationContext context)
    {
        if (Found.Value is not { } tool)
            throw new OperationStepException(FileOperationErrorKind.NotSupported, "No program that writes 7z archives was found (install 7-Zip).");

        var trimmed = sources.Select(PathUtil.TrimTrailingSeparator).ToList();
        var folder = Path.GetDirectoryName(trimmed[0])!;
        var oneFolder = trimmed.TrueForAll(s => PathUtil.AreEqual(Path.GetDirectoryName(s), folder));

        long bytes = 0;
        var listed = new List<(string FullPath, string EntryName, bool IsDirectory, long Length)>();
        foreach (var source in trimmed)
            ArchiveService.Collect(source, Path.GetDirectoryName(source)!, listed);
        foreach (var item in listed)
            bytes += Math.Max(0, item.Length);
        context.AddTotals(bytes, 1);
        context.MarkRunning();
        context.SetCurrentItem(trimmed[0]);

        // The suffix stays ".7z": libarchive picks the format from it, 7-Zip from -t7z.
        var partial = Path.Combine(Path.GetDirectoryName(archivePath)!, $".{Path.GetFileNameWithoutExtension(archivePath)}.{Environment.ProcessId}.partial.7z");
        var start = new ProcessStartInfo(tool.Program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = folder,
        };

        // Relative member names keep paths out of the archive; a leading "-" never reads as a switch.
        IEnumerable<string> Members() => trimmed.Select(s => oneFolder ? Path.GetFileName(s) : s).Select(n => n.StartsWith('-') ? "." + Path.DirectorySeparatorChar + n : n);

        if (tool.IsLibarchive)
        {
            foreach (var argument in new[] { "-c", "-f", partial, "--format", "7zip", "--options", $"7zip:compression-level={LibarchiveLevel(level)}" })
                start.ArgumentList.Add(argument);
        }
        else
        {
            foreach (var argument in new[] { "a", "-t7z", $"-mx={SevenZipLevel(level)}", "-y", "-bd", "--", partial })
                start.ArgumentList.Add(argument);
        }

        foreach (var member in Members())
            start.ArgumentList.Add(member);

        var finished = false;
        try
        {
            using var process = Process.Start(start) ?? throw new OperationStepException(FileOperationErrorKind.NotSupported, $"'{tool.Program}' could not be started.");
            using var stop = context.CancellationToken.Register(() =>
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already exited.
                }
            });

            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await output.ConfigureAwait(false);
            var message = (await errors.ConfigureAwait(false)).Trim();
            context.CancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || !File.Exists(partial))
                throw new OperationStepException(FileOperationErrorKind.Unknown, message.Length > 0 ? message : $"{Path.GetFileName(tool.Program)} exited with code {process.ExitCode}.");

            File.Move(partial, archivePath, overwrite: false);
            finished = true;
            context.ReportBytes(bytes);
            context.CompleteItem();
            context.RecordTransferred(new TransferredItem(sources[0], archivePath, IsDirectory: false, WasMoved: false));
        }
        finally
        {
            if (!finished && File.Exists(partial))
                File.Delete(partial);
        }
    }

    private static int SevenZipLevel(CompressionLevel level) => level switch
    {
        CompressionLevel.NoCompression => 0,
        CompressionLevel.Fastest => 1,
        CompressionLevel.SmallestSize => 9,
        _ => 5,
    };

    private static int LibarchiveLevel(CompressionLevel level) => level switch
    {
        CompressionLevel.NoCompression => 0,
        CompressionLevel.Fastest => 1,
        CompressionLevel.SmallestSize => 9,
        _ => 6,
    };

    private static (string, bool)? Search()
    {
        string[] sevenZipNames = OperatingSystem.IsWindows() ? ["7z.exe", "7za.exe", "7zz.exe"] : ["7zz", "7z", "7za"];
        foreach (var name in sevenZipNames)
        {
            if (OnPath(name) is { } program)
                return (program, false);
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[] { "ProgramW6432", "ProgramFiles", "ProgramFiles(x86)" })
            {
                if (Environment.GetEnvironmentVariable(root) is { Length: > 0 } programs && File.Exists(Path.Combine(programs, "7-Zip", "7z.exe")))
                    return (Path.Combine(programs, "7-Zip", "7z.exe"), false);
            }

            // tar.exe is libarchive; it writes 7z from Windows 11 24H2 (build 26100) on, the release whose Explorer creates 7z itself.
            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var tar = Path.Combine(system, "tar.exe");
            return Environment.OSVersion.Version.Build >= 26100 && File.Exists(tar) ? (tar, true) : null;
        }

        return OnPath("bsdtar") is { } bsdtar ? (bsdtar, true) : null;
    }

    internal static string? OnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is skipped.
            }
        }

        return null;
    }
}
