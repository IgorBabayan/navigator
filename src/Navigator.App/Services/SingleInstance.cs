using System.Diagnostics;
using Navigator.Core.Platform;

namespace Navigator.App.Services;

/// <summary>
/// One Navigator per user: the first process holds an exclusive lock on <c>instance.lock</c> in the cache folder for
/// its whole life. A later launch (shortcut, executable, "Open with", <c>--activate</c>) hands its folder arguments to
/// the running instance over the <see cref="ActivationChannel"/>, which brings its window to the front, and exits
/// before Avalonia is touched.
/// <para>
/// Cost for the first instance is one file open; the OS drops the lock when the process ends, even after a crash, so
/// there is nothing stale to clean up. A launch that finds the lock taken but nobody answering yet (the running
/// instance is still starting, or is just exiting after an update) retries briefly and, should nobody ever answer,
/// starts anyway rather than leave the user with nothing.
/// </para>
/// </summary>
internal static class SingleInstance
{
    private const string LockFileName = "instance.lock";
    private static readonly TimeSpan WaitForRunningInstance = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(2);
    private static FileStream? _lock;

    /// <summary>True when this process should start the app; false when a running instance was shown instead.</summary>
    public static bool Claim(string[] args)
    {
        AppDirectories directories;
        try
        {
            directories = AppDirectories.Resolve();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true;
        }

        var lockPath = Path.Combine(directories.CacheDirectory, LockFileName);
        if (TryLock(lockPath) is not false)
            return true; // ours, or no lock possible at all (unwritable cache folder): start as before

        // Launched by the user, this process may take the foreground right now: let the running instance use that.
        if (OperatingSystem.IsWindows())
            Navigator.Platform.Hotkeys.WindowsForeground.AllowForAnyProcess();

        var socket = ActivationChannel.SocketPath(directories);
        var folders = FolderArguments(args);
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            if (ActivationChannel.TrySignal(socket, SignalTimeout, folders))
                return false;
            if (TryLock(lockPath) is not false)
                return true; // the other instance has just exited
            if (Stopwatch.GetElapsedTime(started) > WaitForRunningInstance)
                return true;
            Thread.Sleep(100);
        }
    }

    /// <summary>Lets the next launch become the instance (called when the app has shut down).</summary>
    public static void Release()
    {
        _lock?.Dispose();
        _lock = null;
    }

    /// <summary>True: this process holds the lock. False: another process does. Null: locking is impossible here.</summary>
    private static bool? TryLock(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            // FileShare.None is an exclusive share mode on Windows and an exclusive flock() on Linux and macOS.
            _lock = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            return false; // sharing violation (Windows) or lock held (Unix)
        }
    }

    /// <summary>Folders on the command line, resolved here: the running instance has a different working directory.</summary>
    private static List<string> FolderArguments(string[] args)
    {
        var folders = new List<string>();
        foreach (var argument in args)
        {
            if (argument.Length == 0 || argument[0] == '-')
                continue;
            try
            {
                var full = Path.GetFullPath(argument);
                if (Directory.Exists(full))
                    folders.Add(full);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Not a usable path.
            }
        }

        return folders;
    }
}
