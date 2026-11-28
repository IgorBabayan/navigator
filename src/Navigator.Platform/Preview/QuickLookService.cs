using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Navigator.Core.Platform;

namespace Navigator.Platform.Preview;

/// <summary>
/// External "quick look" previewers.
/// <list type="bullet">
/// <item>QuickLook (QL-Win): its per-user named pipe, the same "Toggle"/"Switch" messages its Explorer plugin sends.</item>
/// <item>Seer: WM_COPYDATA to its hidden window.</item>
/// <item>PowerToys Peek: PowerToys.Peek.UI.exe with the file as argument (PowerToys 0.9x and later).</item>
/// <item>macOS: <c>qlmanage -p</c> (Quick Look); Linux: GNOME Sushi over D-Bus.</item>
/// </list>
/// Detection is cached for a few seconds: pressing the space bar repeatedly never scans the process list each time.
/// </summary>
public sealed partial class QuickLookService : IQuickLookService, IDisposable
{
    private static readonly TimeSpan DetectionLifetime = TimeSpan.FromSeconds(5);
    private const int SeerInvoke = 5000;

    private readonly Lock _gate = new();
    private IReadOnlyList<QuickLookProvider> _available = [];
    private QuickLookProvider? _automatic;
    private long _detectedAt = long.MinValue;
    private Process? _macPreview;

    public IReadOnlyList<QuickLookProvider> GetAvailableProviders()
    {
        Refresh();
        return _available;
    }

    public QuickLookProvider? ResolveAutomatic()
    {
        Refresh();
        return _automatic;
    }

    public Task<bool> ToggleAsync(QuickLookProvider provider, string path, CancellationToken cancellationToken = default)
        => provider switch
        {
            QuickLookProvider.QuickLook when OperatingSystem.IsWindows() => SendQuickLookAsync("QuickLook.App.PipeMessages.Toggle", path, cancellationToken),
            QuickLookProvider.Seer when OperatingSystem.IsWindows() => Task.FromResult(SendSeer(path)),
            QuickLookProvider.PowerToysPeek when OperatingSystem.IsWindows() => Task.FromResult(PeekExecutable() is { } peek && ProcessRunner.Start(peek, [path])),
            QuickLookProvider.System when OperatingSystem.IsMacOS() => Task.FromResult(ToggleMacQuickLook(path)),
            QuickLookProvider.System when OperatingSystem.IsLinux() => ShowSushiAsync(path, cancellationToken),
            _ => Task.FromResult(false),
        };

    public Task<bool> SwitchAsync(QuickLookProvider provider, string path, CancellationToken cancellationToken = default)
        => provider switch
        {
            QuickLookProvider.QuickLook when OperatingSystem.IsWindows() => SendQuickLookAsync("QuickLook.App.PipeMessages.Switch", path, cancellationToken),
            QuickLookProvider.System when OperatingSystem.IsMacOS() && IsMacPreviewOpen => Task.FromResult(ToggleMacQuickLook(path, restart: true)),
            _ => Task.FromResult(false),
        };

    private void Refresh()
    {
        var now = Environment.TickCount64;
        lock (_gate)
        {
            if (now - _detectedAt < DetectionLifetime.TotalMilliseconds)
                return;
            _detectedAt = now;
        }

        var available = new List<QuickLookProvider>();
        QuickLookProvider? automatic = null;
        if (OperatingSystem.IsWindows())
        {
            var quickLookRunning = IsProcessRunning("QuickLook");
            var seerRunning = FindSeerWindow() != 0;
            var peek = PeekExecutable();
            var peekRunning = peek is not null && IsProcessRunning("PowerToys.Peek.UI");
            if (quickLookRunning)
                available.Add(QuickLookProvider.QuickLook);
            if (seerRunning)
                available.Add(QuickLookProvider.Seer);
            if (peek is not null)
                available.Add(QuickLookProvider.PowerToysPeek);

            // A previewer the user keeps running is the one they want; a cold start of Peek would be slower than ours.
            automatic = quickLookRunning ? QuickLookProvider.QuickLook
                : seerRunning ? QuickLookProvider.Seer
                : peekRunning ? QuickLookProvider.PowerToysPeek
                : null;
        }
        else if (OperatingSystem.IsMacOS())
        {
            available.Add(QuickLookProvider.System);
            automatic = QuickLookProvider.System;
        }
        else if (OperatingSystem.IsLinux() && IsSushiInstalled())
        {
            available.Add(QuickLookProvider.System);
        }

        lock (_gate)
        {
            _available = available;
            _automatic = automatic;
        }
    }

    private static bool IsProcessRunning(string name)
    {
        var processes = Process.GetProcessesByName(name);
        foreach (var process in processes)
            process.Dispose();
        return processes.Length > 0;
    }

    // ── QuickLook (QL-Win) ──────────────────────────────────────

    [SupportedOSPlatform("windows")]
    private static async Task<bool> SendQuickLookAsync(string message, string path, CancellationToken cancellationToken)
    {
        var pipeName = "QuickLook.App.Pipe." + WindowsIdentity.GetCurrent().User?.Value;
        try
        {
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            await client.ConnectAsync(500, cancellationToken).ConfigureAwait(false);
            await using var writer = new StreamWriter(client, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await writer.WriteLineAsync($"{message}|{path}".AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false; // not running (any more)
        }
    }

    // ── Seer ────────────────────────────────────────────────────

    private static nint FindSeerWindow() => OperatingSystem.IsWindows() ? FindWindowW("SeerWindowClass", null) : 0;

    [SupportedOSPlatform("windows")]
    private static unsafe bool SendSeer(string path)
    {
        var window = FindSeerWindow();
        if (window == 0)
            return false;
        fixed (char* text = path)
        {
            var data = new CopyData { Data = SeerInvoke, Length = (path.Length + 1) * sizeof(char), Pointer = (nint)text };
            return SendMessageTimeoutW(window, 0x004A /* WM_COPYDATA */, 0, (nint)(&data), 0x0002 /* SMTO_ABORTIFHUNG */, 1000, out _) != 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyData
    {
        public nint Data;
        public int Length;
        public nint Pointer;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowW(string className, string? windowName);

    [LibraryImport("user32.dll")]
    private static partial nint SendMessageTimeoutW(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);

    // ── PowerToys Peek ──────────────────────────────────────────

    private static string? PeekExecutable()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        string[] roots =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerToys"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerToys"),
        ];
        foreach (var root in roots)
        {
            foreach (var candidate in new[] { Path.Combine(root, "WinUI3Apps", "PowerToys.Peek.UI.exe"), Path.Combine(root, "modules", "Peek", "PowerToys.Peek.UI.exe") })
            {
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    // ── macOS Quick Look ────────────────────────────────────────

    private bool IsMacPreviewOpen
    {
        get
        {
            lock (_gate)
                return _macPreview is { HasExited: false };
        }
    }

    /// <summary>qlmanage shows one preview window per process: a second press closes it, a selection change replaces it.</summary>
    private bool ToggleMacQuickLook(string path, bool restart = false)
    {
        lock (_gate)
        {
            if (_macPreview is { } running)
            {
                var wasOpen = !running.HasExited;
                Kill(running);
                _macPreview = null;
                if (wasOpen && !restart)
                    return true;
            }

            try
            {
                var info = new ProcessStartInfo("/usr/bin/qlmanage") { UseShellExecute = false, CreateNoWindow = true };
                info.ArgumentList.Add("-p");
                info.ArgumentList.Add(path);
                var process = Process.Start(info);
                if (process is null)
                    return false;
                _macPreview = process;
                return true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
            {
                return false;
            }
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }

        process.Dispose();
    }

    // ── GNOME Sushi ─────────────────────────────────────────────

    private static bool IsSushiInstalled()
    {
        if (ProcessRunner.FindOnPath("dbus-send") is null)
            return false;
        var dataDirectories = (Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } dirs ? dirs : "/usr/local/share:/usr/share").Split(':', StringSplitOptions.RemoveEmptyEntries);
        return dataDirectories.Any(d => File.Exists(Path.Combine(d, "dbus-1", "services", "org.gnome.NautilusPreviewer.service")));
    }

    private static async Task<bool> ShowSushiAsync(string path, CancellationToken cancellationToken)
    {
        if (ProcessRunner.FindOnPath("dbus-send") is not { } dbusSend)
            return false;
        var uri = new Uri(path).AbsoluteUri;
        var result = await ProcessRunner.RunCapturingAsync(dbusSend,
            ["--session", "--type=method_call", "--dest=org.gnome.NautilusPreviewer", "/org/gnome/NautilusPreviewer",
             "org.gnome.NautilusPreviewer.ShowFile", "string:" + uri, "int32:0", "boolean:true"],
            TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        return result is { ExitCode: 0 };
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_macPreview is { } process)
                Kill(process);
            _macPreview = null;
        }
    }
}
