using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Navigator.Core.IO;

namespace Navigator.Updater;

/// <summary>
/// Installs a verified package. <see cref="PrepareAsync"/> does every check and all staging work while the
/// app keeps running (nothing in the working installation is touched); <see cref="StartAndExit"/> hands over to a
/// small script embedded in this assembly that waits for this process to exit, swaps the installation with a
/// rollback path, and starts the new version. No downloaded script is ever executed.
/// </summary>
public interface IUpdateInstaller
{
    InstallationKind Kind { get; }

    Task PrepareAsync(string package, CancellationToken cancellationToken);

    /// <summary>Starts the hand-over. The caller must shut the application down right after this returns.</summary>
    void StartAndExit();
}

public static class UpdateInstallerFactory
{
    public static IUpdateInstaller? Create(InstallationKind kind, string baseDirectory) => kind switch
    {
        InstallationKind.WindowsInstaller => new WindowsInstallerUpdate(),
        InstallationKind.WindowsPortable => new DirectorySwapUpdate(kind, baseDirectory.TrimEnd('\\', '/'), "Navigator.exe"),
        InstallationKind.LinuxPortable => new DirectorySwapUpdate(kind, baseDirectory.TrimEnd('/'), "Navigator"),
        InstallationKind.LinuxAppImage => new AppImageUpdate(),
        InstallationKind.MacAppBundle => new MacBundleUpdate(InstallationDetector.GetMacBundle(baseDirectory)!),
        _ => null,
    };

    /// <summary>Removes leftovers of a previous update. Called in the background once the new version is up.</summary>
    public static void CleanupAfterSuccessfulStart(InstallationKind kind, string baseDirectory)
    {
        var target = kind switch
        {
            InstallationKind.MacAppBundle => InstallationDetector.GetMacBundle(baseDirectory),
            InstallationKind.LinuxAppImage => Environment.GetEnvironmentVariable("APPIMAGE"),
            _ => baseDirectory.TrimEnd('\\', '/'),
        };

        if (string.IsNullOrEmpty(target))
            return;

        foreach (var leftover in new[] { target + ".old", target + ".update-staging", target + ".update" })
        {
            try
            {
                if (Directory.Exists(leftover))
                    Directory.Delete(leftover, recursive: true);
                else if (File.Exists(leftover))
                    File.Delete(leftover);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Retried on the next start.
            }
        }
    }
}

internal sealed class WindowsInstallerUpdate : IUpdateInstaller
{
    private string? _package;

    public InstallationKind Kind => InstallationKind.WindowsInstaller;

    public async Task PrepareAsync(string package, CancellationToken cancellationToken)
    {
        await PackageChecks.RequireMagicAsync(package, "MZ"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        _package = package;
    }

    public void StartAndExit()
    {
        // Same Inno Setup AppId => in-place upgrade; /CLOSEAPPLICATIONS waits for us; the silent [Run] entry relaunches.
        var info = new ProcessStartInfo(_package ?? throw new InvalidOperationException("Prepare first."))
        {
            UseShellExecute = true, // lets Windows show UAC for per-machine installs
        };
        foreach (var argument in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/NOCANCEL" })
            info.ArgumentList.Add(argument);
        Process.Start(info)?.Dispose();
    }
}

/// <summary>Portable ZIP (Windows) and tar.gz (Linux): extract next to the app, swap folders after exit, roll back on failure.</summary>
internal sealed class DirectorySwapUpdate(InstallationKind kind, string appDirectory, string executableName) : IUpdateInstaller
{
    private const string WindowsScript = """
        param([int]$ProcessId, [string]$App, [string]$Staging, [string]$Exe)
        $ErrorActionPreference = 'Stop'
        try { Wait-Process -Id $ProcessId -Timeout 120 -ErrorAction SilentlyContinue } catch { }
        $old = "$App.old"
        if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Recurse -Force }
        $moved = $false
        try {
            Rename-Item -LiteralPath $App -NewName (Split-Path $old -Leaf)
            $moved = $true
            Rename-Item -LiteralPath $Staging -NewName (Split-Path $App -Leaf)
        } catch {
            if ($moved -and -not (Test-Path -LiteralPath $App)) { Rename-Item -LiteralPath $old -NewName (Split-Path $App -Leaf) }
        }
        Start-Process -FilePath (Join-Path $App $Exe)
        """;

    private const string UnixScript = """
        trap '' HUP
        PID="$1"; APP="$2"; STAGING="$3"; EXE="$4"
        while kill -0 "$PID" 2>/dev/null; do sleep 0.3; done
        rm -rf "$APP.old"
        if mv "$APP" "$APP.old"; then
            if ! mv "$STAGING" "$APP"; then mv "$APP.old" "$APP"; fi
        fi
        exec "$APP/$EXE"
        """;

    private string Staging => appDirectory + ".update-staging";

    public InstallationKind Kind => kind;

    public async Task PrepareAsync(string package, CancellationToken cancellationToken)
    {
        PackageChecks.RequireWritable(Path.GetDirectoryName(appDirectory)!);
        if (Directory.Exists(Staging))
            Directory.Delete(Staging, recursive: true);
        Directory.CreateDirectory(Staging);

        if (kind == InstallationKind.WindowsPortable)
            await ExtractZipAsync(package, Staging, cancellationToken).ConfigureAwait(false);
        else
            await ExtractTarGzAsync(package, Staging, cancellationToken).ConfigureAwait(false);

        // Archives may contain a single top-level folder; flatten it so the staging folder looks like the app folder.
        var entries = Directory.GetFileSystemEntries(Staging);
        if (entries.Length == 1 && Directory.Exists(entries[0]) && !File.Exists(Path.Combine(Staging, executableName)))
        {
            var inner = entries[0];
            foreach (var entry in Directory.GetFileSystemEntries(inner))
                Directory.Move(entry, Path.Combine(Staging, Path.GetFileName(entry)));
            Directory.Delete(inner);
        }

        var executable = Path.Combine(Staging, executableName);
        if (!File.Exists(executable))
            throw new InvalidDataException("The update package does not contain the application executable.");

        await File.WriteAllTextAsync(Path.Combine(Staging, InstallationDetector.PortableMarker), string.Empty, cancellationToken).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }

    public void StartAndExit()
    {
        if (OperatingSystem.IsWindows())
            UpdateScript.RunPowerShellAfterExit(WindowsScript, appDirectory, Staging, executableName);
        else
            UpdateScript.RunShellAfterExit(UnixScript, appDirectory, Staging, executableName);
    }

    private static async Task ExtractZipAsync(string package, string destination, CancellationToken cancellationToken)
    {
        await using var zip = await ZipFile.OpenReadAsync(package, cancellationToken).ConfigureAwait(false);
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = SafeTarget(destination, entry.FullName);
            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await entry.ExtractToFileAsync(target, overwrite: false, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ExtractTarGzAsync(string package, string destination, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(package);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await using var reader = new TarReader(gzip);
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } entry)
        {
            var target = SafeTarget(destination, entry.Name);
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(target);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await entry.ExtractToFileAsync(target, overwrite: false, cancellationToken).ConfigureAwait(false);
                    break;
                case TarEntryType.SymbolicLink:
                    var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(target)!, entry.LinkName));
                    if (!PathUtil.IsSameOrDescendant(destination, resolved))
                        throw new InvalidDataException($"Update package link '{entry.Name}' points outside the package.");
                    File.CreateSymbolicLink(target, entry.LinkName);
                    break;
                default:
                    throw new InvalidDataException($"Unexpected entry type {entry.EntryType} in the update package.");
            }
        }
    }

    internal static string SafeTarget(string root, string entryName)
    {
        var relative = entryName.Replace('\\', '/').TrimStart('/');
        if (relative.StartsWith("./", StringComparison.Ordinal))
            relative = relative[2..];
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (relative.Length == 0 || Path.IsPathRooted(relative) || !PathUtil.IsSameOrDescendant(root, full))
            throw new InvalidDataException($"Update package entry '{entryName}' is not a safe path.");
        return full;
    }
}

/// <summary>AppImage: stage next to the current image, then swap with two atomic renames (old kept for rollback).</summary>
internal sealed class AppImageUpdate : IUpdateInstaller
{
    private const string RelaunchScript = """
        trap '' HUP
        while kill -0 "$1" 2>/dev/null; do sleep 0.3; done
        exec "$2"
        """;

    private string? _target;

    public InstallationKind Kind => InstallationKind.LinuxAppImage;

    public async Task PrepareAsync(string package, CancellationToken cancellationToken)
    {
        await PackageChecks.RequireMagicAsync(package, [0x7F, (byte)'E', (byte)'L', (byte)'F'], cancellationToken).ConfigureAwait(false);
        var target = Environment.GetEnvironmentVariable("APPIMAGE") ?? throw new InvalidOperationException("Not running from an AppImage.");
        PackageChecks.RequireWritable(Path.GetDirectoryName(target)!);

        // Same folder => same file system => the final renames are atomic.
        var staged = target + ".update";
        File.Copy(package, staged, overwrite: true);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(staged, File.GetUnixFileMode(target) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        _target = target;
    }

    public void StartAndExit()
    {
        var target = _target ?? throw new InvalidOperationException("Prepare first.");
        var staged = target + ".update";
        var old = target + ".old";

        // Safe while running: the mounted image keeps its inode until this process exits.
        File.Move(target, old, overwrite: true);
        try
        {
            File.Move(staged, target);
        }
        catch
        {
            File.Move(old, target);
            throw;
        }

        UpdateScript.RunShellAfterExit(RelaunchScript, target);
    }
}

/// <summary>macOS: extract the .app with ditto (keeps symlinks, permissions, signatures), check the signature, swap after exit.</summary>
internal sealed class MacBundleUpdate(string bundle) : IUpdateInstaller
{
    private const string SwapScript = """
        trap '' HUP
        PID="$1"; APP="$2"; STAGED="$3"
        while kill -0 "$PID" 2>/dev/null; do sleep 0.3; done
        rm -rf "$APP.old"
        if mv "$APP" "$APP.old"; then
            if ! mv "$STAGED" "$APP"; then rm -rf "$APP"; mv "$APP.old" "$APP"; fi
        fi
        open "$APP"
        """;

    private string? _staged;

    public InstallationKind Kind => InstallationKind.MacAppBundle;

    public async Task PrepareAsync(string package, CancellationToken cancellationToken)
    {
        await PackageChecks.RequireMagicAsync(package, "PK"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        var stagingRoot = bundle + ".update-staging";
        if (Directory.Exists(stagingRoot))
            Directory.Delete(stagingRoot, recursive: true);
        Directory.CreateDirectory(stagingRoot);

        if (!await RunAsync("/usr/bin/ditto", ["-x", "-k", package, stagingRoot], cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("The update archive could not be extracted.");

        var staged = Directory.GetDirectories(stagingRoot, "*.app").SingleOrDefault()
                     ?? throw new InvalidDataException("The update archive does not contain an application bundle.");

        // A Developer ID signed installation only accepts an update that passes strict signature verification.
        if (await RunAsync("/usr/bin/codesign", ["--verify", "--strict", bundle], cancellationToken).ConfigureAwait(false)
            && !await RunAsync("/usr/bin/codesign", ["--verify", "--deep", "--strict", staged], cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("The update's code signature is not valid. The update was rejected.");

        _staged = staged;
    }

    public void StartAndExit()
        => UpdateScript.RunShellAfterExit(SwapScript, bundle, _staged ?? throw new InvalidOperationException("Prepare first."));

    private static async Task<bool> RunAsync(string program, string[] arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info);
        if (process is null)
            return false;
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode == 0;
    }
}

internal static class PackageChecks
{
    public static async Task RequireMagicAsync(string path, byte[] magic, CancellationToken cancellationToken)
    {
        var header = new byte[magic.Length];
        await using var stream = File.OpenRead(path);
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan().SequenceEqual(magic))
            throw new InvalidDataException("The downloaded package has an unexpected format and was rejected.");
    }

    public static void RequireWritable(string directory)
    {
        var probe = Path.Combine(directory, $".navigator-update-probe-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException($"Navigator cannot update itself in '{directory}'. Download the new version from the Releases page.", ex);
        }
    }
}

internal static class UpdateScript
{
    public static void RunShellAfterExit(string script, params string[] arguments)
    {
        var info = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(script);
        info.ArgumentList.Add("navigator-updater"); // $0
        info.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        Process.Start(info)?.Dispose();
    }

    public static void RunPowerShellAfterExit(string script, params string[] arguments)
    {
        var file = Path.Combine(Path.GetTempPath(), $"navigator-update-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(file, script, Encoding.UTF8);
        var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", file,
                     "-ProcessId", Environment.ProcessId.ToString(CultureInfo.InvariantCulture), "-App", arguments[0], "-Staging", arguments[1], "-Exe", arguments[2] })
            info.ArgumentList.Add(argument);
        Process.Start(info)?.Dispose();
    }
}
