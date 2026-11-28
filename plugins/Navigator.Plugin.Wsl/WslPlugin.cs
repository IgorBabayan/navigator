using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Navigator.Core.Navigation;
using Navigator.Plugins;

namespace Navigator.Plugin.Wsl;

/// <summary>
/// Windows Subsystem for Linux: every installed distribution in the sidebar's "Linux" section. Initialization only
/// registers the section; the distribution list is read from the registry when the sidebar asks for it.
/// </summary>
public sealed class WslPlugin : INavigatorPlugin, IDisposable
{
    private WslSection? _section;

    public void Initialize(IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!OperatingSystem.IsWindows())
            return;
        _section = new WslSection(context, new PluginStrings(typeof(WslPlugin).Assembly, () => context.Language));
        context.AddSidebarSection(_section);
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
            _section?.Dispose();
    }
}

/// <summary>One registered distribution.</summary>
/// <param name="Name">"Ubuntu-24.04"; also the folder name under \\wsl.localhost.</param>
/// <param name="Version">1 or 2 (WSL 1 runs without a virtual machine).</param>
internal sealed record WslDistribution(string Name, int Version, bool IsDefault, string? Flavor)
{
    /// <summary>
    /// The distribution's root as Windows shares it: \\wsl.localhost\{name}\ on Windows 11 (and recent Windows 10 with the
    /// Store version of WSL), \\wsl$\{name}\ before.
    /// </summary>
    public string RootPath(int windowsBuild) => (windowsBuild >= 22000 ? @"\\wsl.localhost\" : @"\\wsl$\") + Name + @"\";

    /// <summary>A Material Design Icons name for the distribution's logo, or the penguin.</summary>
    public string IconKey
    {
        get
        {
            var name = (Flavor ?? string.Empty) + " " + Name;
            if (name.Contains("ubuntu", StringComparison.OrdinalIgnoreCase))
                return "Ubuntu";
            if (name.Contains("debian", StringComparison.OrdinalIgnoreCase) || name.Contains("kali", StringComparison.OrdinalIgnoreCase))
                return "Debian";
            if (name.Contains("fedora", StringComparison.OrdinalIgnoreCase))
                return "Fedora";
            if (name.Contains("arch", StringComparison.OrdinalIgnoreCase))
                return "Arch";
            return "Linux";
        }
    }
}

/// <summary>Reads HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss, where WSL registers each distribution of the user.</summary>
internal static class WslRegistry
{
    private const string LxssKey = @"Software\Microsoft\Windows\CurrentVersion\Lxss";
    private const int InstalledState = 1;

    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<WslDistribution> Read()
    {
        try
        {
            using var lxss = Registry.CurrentUser.OpenSubKey(LxssKey);
            if (lxss is null)
                return [];
            var defaultId = lxss.GetValue("DefaultDistribution") as string;
            var found = new List<WslDistribution>();
            foreach (var id in lxss.GetSubKeyNames())
            {
                using var key = lxss.OpenSubKey(id);
                if (key?.GetValue("DistributionName") is not string { Length: > 0 } name)
                    continue;
                if (key.GetValue("State") is int state && state != InstalledState)
                    continue; // being installed or uninstalled
                found.Add(new WslDistribution(
                    name,
                    key.GetValue("Version") is int version ? version : 2,
                    string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase),
                    key.GetValue("Flavor") as string));
            }

            return Sort(found);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    /// <summary>The default distribution first, then by name.</summary>
    internal static List<WslDistribution> Sort(List<WslDistribution> distributions)
        => [.. distributions.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)];
}

/// <summary>The sidebar's "Linux" section, below Network: one entry per distribution, with "Open terminal" and "Shut down".</summary>
[SupportedOSPlatform("windows")]
internal sealed class WslSection : IPluginSidebarSection, IDisposable
{
    private const string TerminalCommand = "terminal";
    private const string ShutDownCommand = "shutdown";

    private readonly IPluginContext _context;
    private readonly PluginStrings _text;
    private readonly int _build = Environment.OSVersion.Version.Build;

    public WslSection(IPluginContext context, PluginStrings text)
    {
        _context = context;
        _text = text;
        // Installing or removing a distribution changes no drive, but mount changes are a cheap moment to look again.
        _context.Volumes.VolumesChanged += OnVolumesChanged;
    }

    public string Id => "wsl";

    public string Title => _text["Wsl.Section"];

    public string IconKey => "Linux";

    public string? SystemIconKey => null;

    public int Order => 120;

    public bool ShowWhenEmpty => false;

    public IReadOnlyList<PluginCommand> Commands => [];

    public event EventHandler? Changed;

    public ValueTask<IReadOnlyList<PluginSidebarEntry>> GetEntriesAsync(CancellationToken cancellationToken)
    {
        var entries = new List<PluginSidebarEntry>();
        foreach (var distribution in WslRegistry.Read())
        {
            var detail = distribution.IsDefault
                ? _text.Format("Wsl.DetailDefault", distribution.Version)
                : _text.Format("Wsl.Detail", distribution.Version);
            entries.Add(new PluginSidebarEntry(distribution.Name, distribution.Name, distribution.IconKey, Location.Folder(distribution.RootPath(_build)), detail)
            {
                Commands =
                [
                    new PluginCommand(TerminalCommand, _text["Wsl.OpenTerminal"], "Console"),
                    new PluginCommand(ShutDownCommand, _text["Wsl.ShutDown"], "Power"),
                ],
            });
        }

        return ValueTask.FromResult<IReadOnlyList<PluginSidebarEntry>>(entries);
    }

    public async Task ExecuteAsync(string commandId, PluginSidebarEntry? entry, CancellationToken cancellationToken)
    {
        if (entry is null)
            return;
        var wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
        switch (commandId)
        {
            case TerminalCommand:
                try
                {
                    // A console program started from a window gets its own console (Windows Terminal when it is the default).
                    using var terminal = Process.Start(new ProcessStartInfo(wsl) { ArgumentList = { "-d", entry.Id, "--cd", "~" }, UseShellExecute = false });
                }
                catch (Win32Exception ex)
                {
                    await _context.Ui.ShowMessageAsync(_text["Wsl.OpenTerminal"], ex.Message).ConfigureAwait(true);
                }

                break;
            case ShutDownCommand:
                var result = await PluginProcess.RunAsync(new PluginProcessRequest(wsl, ["--terminate", entry.Id])
                {
                    Timeout = TimeSpan.FromSeconds(30),
                    // wsl.exe writes UTF-16 to a pipe.
                    OutputEncoding = System.Text.Encoding.Unicode,
                }, cancellationToken).ConfigureAwait(true);
                if (result.Succeeded)
                    _context.Ui.Notify(_text["Wsl.ShutDown"], _text.Format("Wsl.ShutDownDone", entry.Name), PluginNotificationKind.Success);
                else
                    await _context.Ui.ShowMessageAsync(_text["Wsl.ShutDown"], (result.Error.Length > 0 ? result.Error : result.Output).Trim()).ConfigureAwait(true);
                break;
        }
    }

    private void OnVolumesChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose() => _context.Volumes.VolumesChanged -= OnVolumesChanged;
}
