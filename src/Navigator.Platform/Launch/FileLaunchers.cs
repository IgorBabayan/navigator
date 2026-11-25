using System.Collections.Frozen;
using System.Runtime.Versioning;
using System.Text;
using Navigator.Core.Platform;

namespace Navigator.Platform.Launch;

[SupportedOSPlatform("windows")]
public sealed class WindowsFileLauncher : IFileLauncher
{
    public Task<bool> OpenAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult(ProcessRunner.Start(path, useShellExecute: true));

    // Enumerating handlers needs SHAssocEnumHandlers (COM); the shell's own "Open with" dialog is used instead.
    public Task<IReadOnlyList<AppHandler>> GetHandlersAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AppHandler>>([]);

    public Task<bool> OpenWithAsync(string path, AppHandler handler, CancellationToken cancellationToken = default)
        => Task.FromResult(ProcessRunner.Start(handler.Id, [path]));

    public Task<bool> ShowOpenWithDialogAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult(ProcessRunner.Start("rundll32.exe", ["shell32.dll,OpenAs_RunDLL", path]));

    public Task<bool> OpenTerminalAsync(string directory, CancellationToken cancellationToken = default)
    {
        if (ProcessRunner.FindOnPath("wt.exe") is { } terminal)
            return Task.FromResult(ProcessRunner.Start(terminal, ["-d", directory]));
        if (ProcessRunner.FindOnPath("pwsh.exe") is { } pwsh)
            return Task.FromResult(ProcessRunner.Start(pwsh, ["-NoExit"], directory, useShellExecute: true));
        return Task.FromResult(ProcessRunner.Start("cmd.exe", ["/K"], directory, useShellExecute: true));
    }

    public Task<bool> RevealInSystemFileManagerAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult(ProcessRunner.Start("explorer.exe", ["/select,", path]));

    // Types the shell itself offers "Run as administrator" / "Run as different user" for.
    private static readonly FrozenSet<string> ElevatableExtensions =
        new[] { ".exe", ".com", ".bat", ".cmd", ".msi" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public ElevationOptions GetElevationOptions(string path)
        => ElevatableExtensions.Contains(Path.GetExtension(path)) ? ElevationOptions.Administrator | ElevationOptions.OtherUser : ElevationOptions.None;

    public Task<bool> RunElevatedAsync(string path, ElevationOptions mode, CancellationToken cancellationToken = default)
    {
        var verb = mode switch
        {
            ElevationOptions.Administrator => "runas",
            ElevationOptions.OtherUser => "runasuser",
            _ => null,
        };

        // Dismissing the UAC / credential prompt surfaces as a failed start, which callers treat as "nothing happened".
        return Task.FromResult(verb is not null && ProcessRunner.StartWithVerb(path, verb, Path.GetDirectoryName(path)));
    }

    public string ShortcutExtension => ".lnk";

    /// <summary>
    /// Shell links are COM objects (IShellLinkW); the always-present Windows PowerShell writes them, so Navigator ships no
    /// COM interop of its own for a rarely used command. Both paths travel as Base64 literals inside an encoded script:
    /// no file name can ever be interpreted as script text.
    /// </summary>
    public async Task<bool> CreateShortcutAsync(string targetPath, string shortcutPath, CancellationToken cancellationToken = default)
    {
        if (File.Exists(shortcutPath) || Directory.Exists(shortcutPath))
            return false;

        static string Literal(string value) => Convert.ToBase64String(Encoding.Unicode.GetBytes(value));
        var script = new StringBuilder()
            .AppendLine("$ErrorActionPreference = 'Stop'")
            .AppendLine("function Read-Arg([string]$b) { [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($b)) }")
            .Append("$target = Read-Arg '").Append(Literal(targetPath)).AppendLine("'")
            .Append("$link = Read-Arg '").Append(Literal(shortcutPath)).AppendLine("'")
            .AppendLine("if (Test-Path -LiteralPath $link) { exit 3 }")
            .AppendLine("$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($link)")
            .AppendLine("$shortcut.TargetPath = $target")
            .AppendLine("$shortcut.WorkingDirectory = [IO.Path]::GetDirectoryName($target)")
            .AppendLine("$shortcut.Save()")
            .ToString();

        var result = await ProcessRunner.RunAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Literal(script)],
            input: null, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        return result is { ExitCode: 0 } && File.Exists(shortcutPath);
    }
}

[SupportedOSPlatform("linux")]
public sealed class LinuxFileLauncher : IFileLauncher
{
    private static readonly (string Executable, string[] Arguments)[] Terminals =
    [
        ("kgx", []), ("gnome-terminal", []), ("konsole", []), ("xfce4-terminal", []), ("kitty", []),
        ("alacritty", []), ("foot", []), ("wezterm", ["start", "--cwd", "{dir}"]), ("ghostty", []),
        ("tilix", []), ("x-terminal-emulator", []), ("xterm", []),
    ];

    private readonly LinuxDesktopEntries _entries = new();

    public Task<bool> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        if (ProcessRunner.FindOnPath("xdg-open") is { } xdgOpen)
            return Task.FromResult(ProcessRunner.Start(xdgOpen, [path]));
        return Task.FromResult(ProcessRunner.FindOnPath("gio") is { } gio && ProcessRunner.Start(gio, ["open", path]));
    }

    public Task<IReadOnlyList<AppHandler>> GetHandlersAsync(string path, CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<AppHandler>>(
            () => [.. _entries.GetHandlers(path).Select(h => new AppHandler(h.Entry.Id, h.Entry.Name, h.Entry.Icon, h.IsDefault))],
            cancellationToken);

    public Task<bool> OpenWithAsync(string path, AppHandler handler, CancellationToken cancellationToken = default)
    {
        var entry = _entries.Find(handler.Id);
        if (entry is null)
            return Task.FromResult(false);

        // gtk-launch respects DBusActivatable and Terminal=true entries; fall back to running Exec ourselves.
        if (ProcessRunner.FindOnPath("gtk-launch") is { } gtkLaunch && ProcessRunner.Start(gtkLaunch, [Path.GetFileNameWithoutExtension(entry.Id), path]))
            return Task.FromResult(true);

        var expanded = LinuxDesktopEntries.ExpandExec(entry.Exec, path, entry.Name);
        return Task.FromResult(expanded is { } command && ProcessRunner.Start(command.Program, command.Arguments, Path.GetDirectoryName(path)));
    }

    public Task<bool> ShowOpenWithDialogAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> OpenTerminalAsync(string directory, CancellationToken cancellationToken = default)
    {
        if (Environment.GetEnvironmentVariable("TERMINAL") is { Length: > 0 } preferred && ProcessRunner.Start(preferred, null, directory))
            return Task.FromResult(true);

        foreach (var (executable, arguments) in Terminals)
        {
            if (ProcessRunner.FindOnPath(executable) is not { } found)
                continue;
            var args = arguments.Select(a => a.Replace("{dir}", directory, StringComparison.Ordinal));
            if (ProcessRunner.Start(found, args, directory))
                return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public async Task<bool> RevealInSystemFileManagerAsync(string path, CancellationToken cancellationToken = default)
    {
        // org.freedesktop.FileManager1.ShowItems is implemented by Nautilus, Dolphin, Nemo, Thunar…
        if (ProcessRunner.FindOnPath("dbus-send") is { } dbusSend)
        {
            var output = await ProcessRunner.RunForOutputAsync(dbusSend,
                [
                    "--session", "--print-reply", "--dest=org.freedesktop.FileManager1", "--type=method_call",
                    "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems",
                    $"array:string:{new Uri(path).AbsoluteUri}", "string:",
                ],
                TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            if (output is not null)
                return true;
        }

        return await OpenAsync(Path.GetDirectoryName(path) ?? path, cancellationToken).ConfigureAwait(false);
    }

    // Desktop sessions have no portable "run this GUI program as root" verb (pkexec refuses most GUI apps): not offered.
    public ElevationOptions GetElevationOptions(string path) => ElevationOptions.None;

    public Task<bool> RunElevatedAsync(string path, ElevationOptions mode, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public string ShortcutExtension => string.Empty;

    public Task<bool> CreateShortcutAsync(string targetPath, string shortcutPath, CancellationToken cancellationToken = default)
        => UnixSymbolicLinks.CreateAsync(targetPath, shortcutPath, cancellationToken);
}

/// <summary>Shortcuts on Linux and macOS are symbolic links; symlink(2) itself refuses to replace an existing entry.</summary>
internal static class UnixSymbolicLinks
{
    public static Task<bool> CreateAsync(string targetPath, string linkPath, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            try
            {
                File.CreateSymbolicLink(linkPath, targetPath);
                return true;
            }
            catch (Exception refused) when (refused is UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }, cancellationToken);
}

[SupportedOSPlatform("macos")]
public sealed class MacFileLauncher : IFileLauncher
{
    public Task<bool> OpenAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult(ProcessRunner.Start("/usr/bin/open", [path]));

    // LaunchServices enumeration (LSCopyApplicationURLsForURL) is not wrapped yet: the UI offers "Choose application…".
    public Task<IReadOnlyList<AppHandler>> GetHandlersAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AppHandler>>([]);

    public Task<bool> OpenWithAsync(string path, AppHandler handler, CancellationToken cancellationToken = default)
        => Task.FromResult(ProcessRunner.Start("/usr/bin/open", ["-a", handler.Id, path]));

    public Task<bool> ShowOpenWithDialogAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> OpenTerminalAsync(string directory, CancellationToken cancellationToken = default)
        => Task.FromResult(ProcessRunner.Start("/usr/bin/open", ["-a", "Terminal", directory]));

    public Task<bool> RevealInSystemFileManagerAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult(ProcessRunner.Start("/usr/bin/open", ["-R", path]));

    // Finder has no per-file "run as administrator"; elevation is the program's own business (Authorization Services).
    public ElevationOptions GetElevationOptions(string path) => ElevationOptions.None;

    public Task<bool> RunElevatedAsync(string path, ElevationOptions mode, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public string ShortcutExtension => string.Empty;

    public Task<bool> CreateShortcutAsync(string targetPath, string shortcutPath, CancellationToken cancellationToken = default)
        => UnixSymbolicLinks.CreateAsync(targetPath, shortcutPath, cancellationToken);
}
