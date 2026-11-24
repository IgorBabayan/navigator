using System.Runtime.Versioning;
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
}
