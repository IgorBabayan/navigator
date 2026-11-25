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

    /// <summary>
    /// Runs a short command, writes <paramref name="input"/> (if any) to its standard input and closes it, and returns
    /// the exit code and standard output. Secrets are passed this way, never as arguments (which other users can see).
    /// Returns null when the command could not be started or timed out.
    /// </summary>
    public static async Task<(int ExitCode, string Output)?> RunAsync(string fileName, IEnumerable<string> arguments, string? input, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            var info = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
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
            try
            {
                if (input is not null)
                    await process.StandardInput.WriteAsync(input.AsMemory(), timeoutSource.Token).ConfigureAwait(false);
                process.StandardInput.Close();
                var output = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
                var errors = process.StandardError.ReadToEndAsync(timeoutSource.Token);
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
                await errors.ConfigureAwait(false);
                return (process.ExitCode, await output.ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException or IOException)
        {
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone.
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
