using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Navigator.App.Infrastructure;

/// <summary>
/// Minimal file logger: one writer thread, bounded queue (logging never blocks the UI; excess entries are dropped),
/// daily files, at most <see cref="MaxFiles"/> files of <see cref="MaxFileBytes"/> each.
/// Messages are never enriched with file contents; callers avoid logging personal paths except in errors.
/// </summary>
internal sealed class RotatingFileLoggerProvider : ILoggerProvider
{
    public const int MaxFiles = 7;
    public const long MaxFileBytes = 5 * 1024 * 1024;

    private readonly string _directory;
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly Task _writer;

    public RotatingFileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        _writer = Task.Run(WriteLoopAsync);
    }

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));

    private void Enqueue(string line) => _queue.Writer.TryWrite(line);

    private async Task WriteLoopAsync()
    {
        StreamWriter? writer = null;
        var currentDay = DateOnly.MinValue;
        try
        {
            await foreach (var line in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                if (writer is null || today != currentDay || writer.BaseStream.Length > MaxFileBytes)
                {
                    if (writer is not null)
                        await writer.DisposeAsync().ConfigureAwait(false);
                    writer = Open(today);
                    currentDay = today;
                    Prune();
                }

                await writer.WriteLineAsync(line).ConfigureAwait(false);
                if (_queue.Reader.Count == 0)
                    await writer.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // Logging must never take the application down.
        }
        finally
        {
            if (writer is not null)
                await writer.DisposeAsync().ConfigureAwait(false);
        }
    }

    private StreamWriter Open(DateOnly day)
    {
        var baseName = $"navigator-{day:yyyyMMdd}";
        var path = Path.Combine(_directory, baseName + ".log");
        for (var i = 1; File.Exists(path) && new FileInfo(path).Length > MaxFileBytes; i++)
            path = Path.Combine(_directory, $"{baseName}-{i}.log");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        return new StreamWriter(stream, new UTF8Encoding(false));
    }

    private void Prune()
    {
        try
        {
            foreach (var old in new DirectoryInfo(_directory).GetFiles("navigator-*.log").OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxFiles))
                old.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _writer.Wait(TimeSpan.FromSeconds(2));
    }

    private sealed class FileLogger(string category, RotatingFileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= LogLevelSwitch.Minimum;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var builder = new StringBuilder(160);
            builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                .Append(" [").Append(logLevel.ToString()[..4].ToUpperInvariant()).Append("] ")
                .Append(category).Append(": ").Append(formatter(state, exception));
            if (exception is not null)
                builder.AppendLine().Append(exception);
            provider.Enqueue(builder.ToString());
        }
    }
}

/// <summary>Runtime log level (Settings → Advanced → Log level) without rebuilding the logging pipeline.</summary>
internal static class LogLevelSwitch
{
    public static LogLevel Minimum { get; set; } = LogLevel.Information;
}
