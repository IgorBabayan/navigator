using System.Diagnostics;
using Navigator.Core.IO;
using Navigator.Core.Operations;

namespace Navigator.Core.Archives;

/// <summary>
/// 7z and RAR archives are opened through a program already on the machine, as the base class library reads neither:
/// 7-Zip (<c>7z</c>/<c>7zz</c> read both; <c>7za</c> reads 7z only), libarchive's <c>bsdtar</c> (macOS, most Linux
/// distributions, and Windows 11's own <c>tar.exe</c> from 23H2 on), <c>unrar</c>, or The Unarchiver's <c>unar</c>.
/// The search runs once (a few file probes) and is started early by <see cref="Warm"/>.
/// <para>
/// Extraction never lets the tool write into the destination directly: it unpacks into a hidden staging folder next
/// to the destination (same volume, so moving out is a rename), then every staged item is moved into place through
/// Navigator's own conflict handling, and links that point outside the destination are dropped.
/// </para>
/// </summary>
public static class ArchiveReaderTool
{
    internal enum ToolKind
    {
        SevenZip,
        SevenZipLite,
        Libarchive,
        Unrar,
        Unar,
    }

    internal sealed record Tool(string Program, ToolKind Kind)
    {
        public bool Reads(ArchiveFormat format) => format switch
        {
            ArchiveFormat.SevenZip => Kind is not ToolKind.Unrar,
            ArchiveFormat.Rar => Kind is not ToolKind.SevenZipLite,
            _ => false,
        };
    }

    private static readonly Lazy<IReadOnlyList<Tool>> Found = new(Search, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Starts the search in the background so menus never wait for it.</summary>
    public static void Warm() => _ = Task.Run(() => Found.Value);

    /// <summary>True when an installed program can open archives of this format.</summary>
    public static bool CanRead(ArchiveFormat format) => Pick(format) is not null;

    private static Tool? Pick(ArchiveFormat format)
    {
        foreach (var tool in Found.Value)
        {
            if (tool.Reads(format))
                return tool;
        }

        return null;
    }

    /// <summary>Entry names as the archive stores them ("/" separated). Throws <see cref="InvalidDataException"/> when the tool refuses the file.</summary>
    public static IReadOnlyList<string> List(string archivePath, ArchiveFormat format)
    {
        var tool = Pick(format) ?? throw new InvalidDataException($"No installed program can open '{Path.GetFileName(archivePath)}'.");
        string[] arguments = tool.Kind switch
        {
            ToolKind.SevenZip or ToolKind.SevenZipLite => ["l", "-slt", "--", archivePath],
            ToolKind.Libarchive => ["-t", "-f", archivePath],
            ToolKind.Unrar => ["lb", "-p-", "--", archivePath],
            _ => [archivePath], // lsar
        };

        var program = tool.Kind == ToolKind.Unar ? Sibling(tool.Program, "lsar") : tool.Program;
        var (exitCode, output, errors) = RunToEnd(program, arguments, workingDirectory: null, CancellationToken.None).GetAwaiter().GetResult();
        if (exitCode != 0)
            throw new InvalidDataException(errors.Length > 0 ? errors : $"'{Path.GetFileName(archivePath)}' could not be read.");
        return ParseListing(output, tool.Kind);
    }

    internal static IReadOnlyList<string> ParseListing(string output, ToolKind kind)
    {
        var names = new List<string>();
        var lines = output.Split('\n');
        switch (kind)
        {
            case ToolKind.SevenZip or ToolKind.SevenZipLite:
            {
                // "-slt": the archive's own block, a "----------" line, then one "Path = …" per entry.
                var entries = false;
                foreach (var raw in lines)
                {
                    var line = raw.TrimEnd('\r');
                    if (line.StartsWith("----------", StringComparison.Ordinal))
                        entries = true;
                    else if (entries && line.StartsWith("Path = ", StringComparison.Ordinal))
                        names.Add(line[7..].Replace('\\', '/'));
                }

                break;
            }

            case ToolKind.Unar:
            {
                // lsar: the first line names the archive and its format.
                for (var i = 1; i < lines.Length; i++)
                {
                    var line = lines[i].TrimEnd('\r');
                    if (line.Length > 0)
                        names.Add(line);
                }

                break;
            }

            default:
                foreach (var raw in lines)
                {
                    var line = raw.TrimEnd('\r');
                    if (line.Length > 0)
                        names.Add(line.Replace('\\', '/'));
                }

                break;
        }

        return names;
    }

    /// <summary>Extracts into <paramref name="root"/> (an existing, normalized folder) with conflict handling.</summary>
    internal static async Task ExtractAsync(string archivePath, ArchiveFormat format, string root, OperationContext context)
    {
        var tool = Pick(format) ?? throw new OperationStepException(FileOperationErrorKind.NotSupported,
            $"No installed program can open '{Path.GetFileName(archivePath)}' (install 7-Zip).");

        var staging = Path.Combine(root, $".navigator-extract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            context.SetCurrentItem(archivePath);
            string[] arguments = tool.Kind switch
            {
                ToolKind.SevenZip or ToolKind.SevenZipLite => ["x", "-y", "-bd", "-o" + staging, "--", archivePath],
                ToolKind.Libarchive => ["-x", "-f", archivePath, "-C", staging],
                ToolKind.Unrar => ["x", "-o+", "-y", "-p-", "--", archivePath, staging + Path.DirectorySeparatorChar],
                _ => ["-o", staging, "-f", "-D", archivePath],
            };

            var (exitCode, _, errors) = await RunToEnd(tool.Program, arguments, staging, context.CancellationToken).ConfigureAwait(false);
            context.CancellationToken.ThrowIfCancellationRequested();
            if (exitCode != 0)
            {
                throw new OperationStepException(FileOperationErrorKind.Unknown,
                    errors.Length > 0 ? errors : $"{Path.GetFileName(tool.Program)} exited with code {exitCode}.");
            }

            context.ReportBytes(new FileInfo(archivePath).Length);
            await MoveStagedAsync(staging, staging, root, context).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteTree(staging);
        }
    }

    private static async Task MoveStagedAsync(string stagingRoot, string folder, string root, OperationContext context)
    {
        foreach (var item in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }))
        {
            await context.CheckpointAsync().ConfigureAwait(false);
            var relative = Path.GetRelativePath(stagingRoot, item.FullName);
            var target = Path.Combine(root, relative);

            if (item.LinkTarget is { } link)
            {
                // Recreated only when the link stays inside the destination (no escape through extracted links).
                var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(target)!, link));
                if (PathUtil.IsSameOrDescendant(root, resolved) && !File.Exists(target) && !Directory.Exists(target))
                {
                    if (item is DirectoryInfo)
                        Directory.Move(item.FullName, target);
                    else
                        File.Move(item.FullName, target);
                }

                continue;
            }

            if (item is DirectoryInfo directory)
            {
                if (!Directory.Exists(target) && !File.Exists(target))
                {
                    // Nothing to merge with: the whole folder moves in one rename.
                    Directory.Move(directory.FullName, target);
                    continue;
                }

                if (Directory.Exists(target))
                    await MoveStagedAsync(stagingRoot, directory.FullName, root, context).ConfigureAwait(false);
                continue;
            }

            var file = (FileInfo)item;
            context.SetCurrentItem(relative);
            var destination = await ArchiveService.ResolveExtractConflictAsync(target, file.Length, file.LastWriteTimeUtc, context).ConfigureAwait(false);
            if (destination is null)
                continue;
            await context.RunWithRecoveryAsync(destination, () =>
            {
                File.Move(file.FullName, destination, overwrite: true);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
        }
    }

    private static void TryDeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover hidden staging folder is harmless; it is not worth failing the operation for.
        }
    }

    private static async Task<(int ExitCode, string Output, string Errors)> RunToEnd(string program, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        if (workingDirectory is not null)
            start.WorkingDirectory = workingDirectory;
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new OperationStepException(FileOperationErrorKind.NotSupported, $"'{program}' could not be started.");
        process.StandardInput.Close(); // a password prompt reads end-of-input and fails instead of waiting forever
        using var stop = cancellationToken.Register(() =>
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
        var text = await output.ConfigureAwait(false);
        var message = (await errors.ConfigureAwait(false)).Trim();
        return (process.ExitCode, text, message.Length > 400 ? message[..400] : message);
    }

    private static string Sibling(string program, string name)
    {
        var candidate = Path.Combine(Path.GetDirectoryName(program)!, OperatingSystem.IsWindows() ? name + ".exe" : name);
        return File.Exists(candidate) ? candidate : program;
    }

    private static List<Tool> Search()
    {
        var tools = new List<Tool>(3);
        string[] full = OperatingSystem.IsWindows() ? ["7z.exe", "7zz.exe"] : ["7zz", "7z"];
        foreach (var name in full)
        {
            if (SevenZipTool.OnPath(name) is { } program)
            {
                tools.Add(new Tool(program, ToolKind.SevenZip));
                break;
            }
        }

        if (tools.Count == 0 && OperatingSystem.IsWindows())
        {
            foreach (var root in new[] { "ProgramW6432", "ProgramFiles", "ProgramFiles(x86)" })
            {
                if (Environment.GetEnvironmentVariable(root) is { Length: > 0 } programs && File.Exists(Path.Combine(programs, "7-Zip", "7z.exe")))
                {
                    tools.Add(new Tool(Path.Combine(programs, "7-Zip", "7z.exe"), ToolKind.SevenZip));
                    break;
                }
            }
        }

        if (tools.Count > 0)
            return tools; // full 7-Zip reads everything that matters here

        if (OperatingSystem.IsWindows())
        {
            // tar.exe is libarchive; it reads RAR and 7z from Windows 11 23H2 (build 22631) on.
            var tar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
            if (Environment.OSVersion.Version.Build >= 22631 && File.Exists(tar))
                tools.Add(new Tool(tar, ToolKind.Libarchive));
        }
        else if (SevenZipTool.OnPath("bsdtar") is { } bsdtar)
        {
            tools.Add(new Tool(bsdtar, ToolKind.Libarchive));
        }

        if (SevenZipTool.OnPath(OperatingSystem.IsWindows() ? "7za.exe" : "7za") is { } lite)
            tools.Add(new Tool(lite, ToolKind.SevenZipLite));
        if (SevenZipTool.OnPath(OperatingSystem.IsWindows() ? "unrar.exe" : "unrar") is { } unrar)
            tools.Add(new Tool(unrar, ToolKind.Unrar));
        if (SevenZipTool.OnPath(OperatingSystem.IsWindows() ? "unar.exe" : "unar") is { } unar)
            tools.Add(new Tool(unar, ToolKind.Unar));
        return tools;
    }
}
