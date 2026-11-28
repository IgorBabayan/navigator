using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

namespace Navigator.Plugins;

/// <summary>A command line run by <see cref="PluginProcess"/>. Arguments are passed as a list: no shell, no quoting issues.</summary>
public sealed record PluginProcessRequest(string FileName, IReadOnlyList<string> Arguments)
{
    public string? WorkingDirectory { get; init; }

    /// <summary>Variables added to (or, with a null value, removed from) the inherited environment.</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    /// <summary>The process is killed (with its children) after this long and <see cref="TimeoutException"/> is thrown.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Output beyond this many characters (per stream) is dropped, so a runaway command cannot exhaust memory.</summary>
    public int MaxOutputChars { get; init; } = 8 * 1024 * 1024;

    /// <summary>Output is decoded as UTF-8 unless set.</summary>
    public Encoding? OutputEncoding { get; init; }
}

/// <summary>Exit code and captured output of a finished command.</summary>
public sealed record PluginProcessResult(int ExitCode, string Output, string Error)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>The command could not be started or reported a failure.</summary>
public sealed class PluginProcessException : IOException
{
    public PluginProcessException()
    {
    }

    public PluginProcessException(string message)
        : base(message)
    {
    }

    public PluginProcessException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PluginProcessException(string message, int exitCode, string error)
        : base(message)
    {
        ExitCode = exitCode;
        Error = error;
    }

    public int ExitCode { get; } = -1;

    /// <summary>What the command wrote to its error stream.</summary>
    public string Error { get; } = string.Empty;
}

/// <summary>
/// Runs helper programs for plugins (git, wsl, xdg-mime…) without a console window, reading both output streams
/// concurrently (a full pipe can never stall the child), with a timeout and cancellation that kill the whole process
/// tree. Every method may be called from any thread.
/// </summary>
public static class PluginProcess
{
    /// <summary>Runs the command to completion and returns its exit code and output.</summary>
    public static async Task<PluginProcessResult> RunAsync(PluginProcessRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var process = Start(request, redirectInput: false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);
        var output = ReadAllAsync(process.StandardOutput, request.MaxOutputChars);
        var error = ReadAllAsync(process.StandardError, request.MaxOutputChars);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return new PluginProcessResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(request.FileName)} did not finish within {request.Timeout.TotalSeconds:0} s.");
        }
    }

    /// <summary>
    /// Runs the command and streams its output as records ended by <paramref name="separator"/> (e.g. '\0' for
    /// <c>git … -z</c>, '\n' for lines). Stopping the enumeration early kills the process, so a caller can read just
    /// what it needs from a long listing. Throws <see cref="PluginProcessException"/> after the last record when the
    /// command failed.
    /// </summary>
    public static async IAsyncEnumerable<string> ReadRecordsAsync(PluginProcessRequest request, char separator, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var process = Start(request, redirectInput: false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);
        var error = ReadAllAsync(process.StandardError, 64 * 1024);
        var records = Channel.CreateBounded<string>(new BoundedChannelOptions(256) { SingleReader = true, SingleWriter = true });
        var pump = PumpRecordsAsync(process.StandardOutput, separator, request.MaxOutputChars, records.Writer, timeout.Token);
        var finished = false;
        try
        {
            while (true)
            {
                bool more;
                try
                {
                    more = await records.Reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException($"{Path.GetFileName(request.FileName)} did not finish within {request.Timeout.TotalSeconds:0} s.");
                }

                if (!more)
                    break;
                while (records.Reader.TryRead(out var record))
                    yield return record;
            }

            await pump.ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            finished = true;
            if (process.ExitCode != 0)
            {
                var message = (await error.ConfigureAwait(false)).Trim();
                throw new PluginProcessException(message.Length > 0 ? message : $"{Path.GetFileName(request.FileName)} exited with code {process.ExitCode}.", process.ExitCode, message);
            }
        }
        finally
        {
            if (!finished)
                Kill(process);
        }
    }

    /// <summary>
    /// Full path of a program found on PATH (with the PATHEXT extensions on Windows), or null. Absolute paths are
    /// returned when the file exists.
    /// </summary>
    public static string? FindExecutable(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (Path.IsPathFullyQualified(name))
            return File.Exists(name) ? name : null;

        var extensions = OperatingSystem.IsWindows() && Path.GetExtension(name).Length == 0
            ? (System.Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];
        foreach (var folder in (System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string directory;
            try
            {
                directory = folder.Trim().Trim('"');
                if (directory.Length == 0 || !Path.IsPathFullyQualified(directory))
                    continue; // relative PATH entries would resolve against Navigator's working folder
            }
            catch (ArgumentException)
            {
                continue;
            }

            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, name + extension);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static Process Start(PluginProcessRequest request, bool redirectInput)
    {
        var encoding = request.OutputEncoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var info = new ProcessStartInfo(request.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectInput,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding,
        };
        foreach (var argument in request.Arguments)
            info.ArgumentList.Add(argument);
        if (request.WorkingDirectory is { } directory)
            info.WorkingDirectory = directory;
        if (request.Environment is { } environment)
        {
            foreach (var (key, value) in environment)
            {
                if (value is null)
                    info.Environment.Remove(key);
                else
                    info.Environment[key] = value;
            }
        }

        try
        {
            return Process.Start(info) ?? throw new PluginProcessException($"{request.FileName} could not be started.");
        }
        catch (Win32Exception ex)
        {
            throw new PluginProcessException($"{request.FileName} could not be started: {ex.Message}", ex);
        }
    }

    private static async Task<string> ReadAllAsync(StreamReader reader, int limit)
    {
        var text = new StringBuilder();
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            var room = limit - text.Length;
            if (room > 0)
                text.Append(buffer, 0, Math.Min(read, room));
        }

        return text.ToString();
    }

    private static async Task PumpRecordsAsync(StreamReader reader, char separator, int limit, ChannelWriter<string> writer, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            var record = new StringBuilder();
            var buffer = new char[16384];
            var total = 0;
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > limit)
                    throw new IOException("The command produced more output than allowed.");
                var start = 0;
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != separator)
                        continue;
                    record.Append(buffer, start, i - start);
                    await writer.WriteAsync(record.ToString(), cancellationToken).ConfigureAwait(false);
                    record.Clear();
                    start = i + 1;
                }

                record.Append(buffer, start, read - start);
            }

            if (record.Length > 0)
                await writer.WriteAsync(record.ToString(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or ChannelClosedException)
        {
            failure = ex is OperationCanceledException ? null : ex;
        }
        finally
        {
            writer.TryComplete(failure);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }
}
