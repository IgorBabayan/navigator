using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Navigator.Core.Platform;

namespace Navigator.Platform.Preview;

public sealed class UnsupportedAudioPlayer : IAudioPreviewPlayer
{
    public bool IsSupported => false;

    public bool IsPlaying => false;

    public bool Play(string path) => false;

    public void Stop()
    {
    }
}

/// <summary>
/// Windows: the Media Control Interface (winmm), which plays MP3, WAV, WMA, M4A/AAC and FLAC through the installed
/// DirectShow/Media Foundation decoders without any window or extra dependency.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsMciAudioPlayer : IAudioPreviewPlayer, IDisposable
{
    private const string Alias = "navigator_preview";
    private readonly Lock _gate = new();
    private bool _open;

    public bool IsSupported => true;

    public bool IsPlaying
    {
        get
        {
            lock (_gate)
            {
                if (!_open)
                    return false;
                Span<char> mode = stackalloc char[32];
                return Send($"status {Alias} mode", mode) && mode[..Math.Max(0, mode.IndexOf('\0'))].SequenceEqual("playing");
            }
        }
    }

    public bool Play(string path)
    {
        lock (_gate)
        {
            CloseLocked();
            if (path.Contains('"', StringComparison.Ordinal))
                return false;
            if (!Send($"open \"{path}\" type mpegvideo alias {Alias}", []))
                return false;
            _open = true;
            if (Send($"play {Alias}", []))
                return true;
            CloseLocked();
            return false;
        }
    }

    public void Stop()
    {
        lock (_gate)
            CloseLocked();
    }

    private void CloseLocked()
    {
        if (!_open)
            return;
        Send($"close {Alias}", []);
        _open = false;
    }

    private static unsafe bool Send(string command, Span<char> result)
    {
        fixed (char* buffer = result)
            return mciSendStringW(command, buffer, (uint)result.Length, 0) == 0;
    }

    [LibraryImport("winmm.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint mciSendStringW(string command, char* returnString, uint returnLength, nint callback);

    public void Dispose() => Stop();
}

/// <summary>
/// macOS and Linux: a command-line player (afplay; ffplay, mpv, gst-play-1.0 or paplay), started without a window and
/// killed on stop. Only one plays at a time.
/// </summary>
internal sealed class ProcessAudioPlayer : IAudioPreviewPlayer, IDisposable
{
    private readonly Lock _gate = new();
    private readonly (string Executable, Func<string, string[]> Arguments, Func<string, bool> Accepts)? _player;
    private Process? _process;

    public ProcessAudioPlayer()
    {
        if (OperatingSystem.IsMacOS() && File.Exists("/usr/bin/afplay"))
        {
            _player = ("/usr/bin/afplay", static path => [path], static _ => true);
            return;
        }

        if (ProcessRunner.FindOnPath("ffplay") is { } ffplay)
            _player = (ffplay, static path => ["-nodisp", "-autoexit", "-loglevel", "quiet", path], static _ => true);
        else if (ProcessRunner.FindOnPath("mpv") is { } mpv)
            _player = (mpv, static path => ["--no-video", "--really-quiet", "--", path], static _ => true);
        else if (ProcessRunner.FindOnPath("gst-play-1.0") is { } gst)
            _player = (gst, static path => ["--no-interactive", "-q", path], static _ => true);
        else if (ProcessRunner.FindOnPath("paplay") is { } paplay)
            _player = (paplay, static path => [path], static path => Path.GetExtension(path).ToLowerInvariant() is ".wav" or ".flac" or ".ogg" or ".oga" or ".aiff");
    }

    public bool IsSupported => _player is not null;

    public bool IsPlaying
    {
        get
        {
            lock (_gate)
                return _process is { HasExited: false };
        }
    }

    public bool Play(string path)
    {
        lock (_gate)
        {
            StopLocked();
            if (_player is not { } player || !player.Accepts(path))
                return false;
            try
            {
                var info = new ProcessStartInfo(player.Executable)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true, // players that read keys from stdin must not take the terminal's
                };
                foreach (var argument in player.Arguments(path))
                    info.ArgumentList.Add(argument);
                _process = Process.Start(info);
                return _process is not null;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
            {
                return false;
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
            StopLocked();
    }

    private void StopLocked()
    {
        if (_process is not { } process)
            return;
        _process = null;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone.
        }

        process.Dispose();
    }

    public void Dispose() => Stop();
}
