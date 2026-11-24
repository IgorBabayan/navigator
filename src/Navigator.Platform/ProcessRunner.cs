using System.ComponentModel;
using System.Diagnostics;

namespace Navigator.Platform;

/// <summary>Starts helper processes without a shell (arguments are passed as a list: no injection through file names).</summary>
internal static class ProcessRunner
{
    public static bool Start(string fileName, IEnumerable<string>? arguments = null, string? workingDirectory = null, bool useShellExecute = false)
    {
        try
        {
            var info = new ProcessStartInfo(fileName)
            {
                UseShellExecute = useShellExecute,
                CreateNoWindow = !useShellExecute,
            };

            if (workingDirectory is not null)
                info.WorkingDirectory = workingDirectory;
            if (arguments is not null)
            {
                foreach (var argument in arguments)
                    info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info);
            return process is not null || useShellExecute;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Runs a short command and returns its standard output (null on failure or timeout).</summary>
    public static async Task<string?> RunForOutputAsync(string fileName, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            var info = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
                info.ArgumentList.Add(argument);

            using var process = Process.Start(info);
            if (process is null)
                return null;

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            var output = await process.StandardOutput.ReadToEndAsync(timeoutSource.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? output : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Finds an executable on PATH.</summary>
    public static string? FindOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate))
                return candidate;
            if (OperatingSystem.IsWindows() && File.Exists(candidate + ".exe"))
                return candidate + ".exe";
        }

        return null;
    }
}
