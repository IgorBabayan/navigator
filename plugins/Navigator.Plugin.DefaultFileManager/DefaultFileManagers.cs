using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Navigator.Plugins;

namespace Navigator.Plugin.DefaultFileManager;

/// <summary>Switches the system's default file manager between Navigator and the system's own.</summary>
internal interface IDefaultFileManager
{
    /// <summary>Navigator is the default now (read from the system, not from a remembered flag).</summary>
    bool IsNavigatorDefault();

    /// <summary>Makes Navigator the default; remembers what it replaces.</summary>
    Task EnableAsync(string executable, CancellationToken cancellationToken);

    /// <summary>Gives the role back to what was the default before (File Explorer on Windows).</summary>
    Task DisableAsync(CancellationToken cancellationToken);

    /// <summary>Points the registration at <paramref name="executable"/> when Navigator moved (portable copies, updates).</summary>
    Task RepairAsync(string executable, CancellationToken cancellationToken);
}

/// <summary>
/// Windows, per user (HKCU\Software\Classes, no administrator rights):
/// <list type="bullet">
/// <item><c>Directory\shell</c> and <c>Drive\shell</c> get a "navigator" verb that is made their default verb, so
/// double-clicking a folder on the desktop, ShellExecute from other apps and "Open" in dialogs open Navigator. Explorer's
/// own verbs stay untouched (Navigator still calls Explorer for "Reveal" and the classic menu).</item>
/// <item>This PC's "opennewwindow" (Win+E) runs Navigator.</item>
/// </list>
/// The previous default verbs are kept in the plugin's preferences and restored on <see cref="DisableAsync"/>; keys the
/// plugin created are deleted, keys that existed are left as they were.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsDefaultFileManager(PluginPreferences preferences) : IDefaultFileManager
{
    internal const string Verb = "navigator";
    private const string Classes = @"Software\Classes";
    private const string ThisPcOpenWindow = @"CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\shell\opennewwindow";
    private const string NoValue = "\u0000none";
    private static readonly string[] FolderClasses = ["Directory", "Drive"];

    public bool IsNavigatorDefault()
    {
        using var shell = Registry.CurrentUser.OpenSubKey($@"{Classes}\Directory\shell");
        return string.Equals(shell?.GetValue(null) as string, Verb, StringComparison.OrdinalIgnoreCase);
    }

    public Task EnableAsync(string executable, CancellationToken cancellationToken)
    {
        foreach (var folderClass in FolderClasses)
        {
            using var shell = Registry.CurrentUser.CreateSubKey($@"{Classes}\{folderClass}\shell");
            var previous = shell.GetValue(null) as string;
            if (!string.Equals(previous, Verb, StringComparison.OrdinalIgnoreCase))
                preferences.SetString("previous." + folderClass, previous ?? NoValue);
            WriteVerb(shell, executable);
            shell.SetValue(null, Verb);
        }

        bool thisPcExisted;
        using (var existing = Registry.CurrentUser.OpenSubKey($@"{Classes}\{ThisPcOpenWindow}"))
            thisPcExisted = existing is not null;
        if (!thisPcExisted)
            preferences.SetBool("createdThisPc", true);
        using (var open = Registry.CurrentUser.CreateSubKey($@"{Classes}\{ThisPcOpenWindow}\command"))
        {
            open.SetValue(null, Quote(executable));
            open.SetValue("DelegateExecute", string.Empty); // overrides Explorer's handler from HKLM
        }

        NotifyShell();
        return Task.CompletedTask;
    }

    public Task DisableAsync(CancellationToken cancellationToken)
    {
        foreach (var folderClass in FolderClasses)
        {
            using (var shell = Registry.CurrentUser.OpenSubKey($@"{Classes}\{folderClass}\shell", writable: true))
            {
                if (shell is not null)
                {
                    shell.DeleteSubKeyTree(Verb, throwOnMissingSubKey: false);
                    var previous = preferences.GetString("previous." + folderClass);
                    if (string.Equals(shell.GetValue(null) as string, Verb, StringComparison.OrdinalIgnoreCase))
                    {
                        if (previous is null or NoValue)
                            shell.DeleteValue(string.Empty, throwOnMissingValue: false);
                        else
                            shell.SetValue(null, previous);
                    }
                }
            }

            DeleteIfEmpty($@"{Classes}\{folderClass}\shell");
            preferences.SetString("previous." + folderClass, null);
        }

        if (preferences.GetBool("createdThisPc", false))
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\{ThisPcOpenWindow}", throwOnMissingSubKey: false);
            DeleteIfEmpty($@"{Classes}\CLSID\{{52205fd8-5dfb-447d-801a-d0b52f2e83e1}}\shell");
            DeleteIfEmpty($@"{Classes}\CLSID\{{52205fd8-5dfb-447d-801a-d0b52f2e83e1}}");
            preferences.SetString("createdThisPc", null);
        }
        else
        {
            using var open = Registry.CurrentUser.OpenSubKey($@"{Classes}\{ThisPcOpenWindow}\command", writable: true);
            if (open?.GetValue(null) is string command && command.Contains("Navigator", StringComparison.OrdinalIgnoreCase))
            {
                open.DeleteValue(string.Empty, throwOnMissingValue: false);
                open.DeleteValue("DelegateExecute", throwOnMissingValue: false);
            }
        }

        NotifyShell();
        return Task.CompletedTask;
    }

    public Task RepairAsync(string executable, CancellationToken cancellationToken)
    {
        if (!IsNavigatorDefault())
            return Task.CompletedTask;
        using var command = Registry.CurrentUser.OpenSubKey($@"{Classes}\Directory\shell\{Verb}\command");
        if (command?.GetValue(null) is string current && current.Contains(Quote(executable), StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;
        return EnableAsync(executable, cancellationToken);
    }

    private static void WriteVerb(RegistryKey shell, string executable)
    {
        using var verb = shell.CreateSubKey(Verb);
        verb.SetValue(null, "Open in Navigator");
        verb.SetValue("Icon", Quote(executable) + ",0");
        using var command = verb.CreateSubKey("command");
        command.SetValue(null, Quote(executable) + " \"%1\"");
    }

    private static void DeleteIfEmpty(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        if (key is null || key.SubKeyCount > 0 || key.ValueCount > 0)
            return;
        key.Dispose();
        Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false);
    }

    private static string Quote(string path) => "\"" + path + "\"";

    private const int ShcneAssocChanged = 0x08000000;

    /// <summary>Tells Explorer that file associations changed, so open windows and the desktop pick it up at once.</summary>
    private static void NotifyShell() => SHChangeNotify(ShcneAssocChanged, 0, 0, 0);

    [LibraryImport("shell32")]
    private static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}

/// <summary>
/// Linux: the default handler of <c>inode/directory</c> (what the desktop, browsers' "Show in folder" and
/// <c>xdg-open</c> use), set with <c>xdg-mime</c>. Navigator's desktop entry is used when it is installed (AppImage
/// integration, packages); otherwise one is written to ~/.local/share/applications and removed again later.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxDefaultFileManager(PluginPreferences preferences, string home, Func<string, string?> environment) : IDefaultFileManager
{
    internal const string DesktopId = "io.github.igorbabayan.Navigator.desktop";
    private const string MimeType = "inode/directory";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public bool IsNavigatorDefault() => string.Equals(Query(), DesktopId, StringComparison.Ordinal);

    public async Task EnableAsync(string executable, CancellationToken cancellationToken)
    {
        var xdgMime = PluginProcess.FindExecutable("xdg-mime") ?? throw new InvalidOperationException(DefaultFileManagerPlugin.Text["Default.NoXdgMime"]);
        var previous = Query();
        if (!string.Equals(previous, DesktopId, StringComparison.Ordinal))
            preferences.SetString("previous.directory", previous ?? string.Empty);
        if (FindDesktopEntry() is null)
        {
            var file = Path.Combine(DataHome, "applications", DesktopId);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, LinuxDesktopEntry.For(executable), cancellationToken).ConfigureAwait(false);
            preferences.SetBool("createdDesktopEntry", true);
        }

        await RunAsync(xdgMime, ["default", DesktopId, MimeType], cancellationToken).ConfigureAwait(false);
    }

    public async Task DisableAsync(CancellationToken cancellationToken)
    {
        var previous = preferences.GetString("previous.directory");
        if (IsNavigatorDefault())
        {
            if (previous is { Length: > 0 } && PluginProcess.FindExecutable("xdg-mime") is { } xdgMime)
                await RunAsync(xdgMime, ["default", previous, MimeType], cancellationToken).ConfigureAwait(false);
            else
                RemoveFromMimeApps();
        }

        if (preferences.GetBool("createdDesktopEntry", false))
        {
            var file = Path.Combine(DataHome, "applications", DesktopId);
            if (File.Exists(file))
                File.Delete(file);
            preferences.SetString("createdDesktopEntry", null);
        }

        preferences.SetString("previous.directory", null);
    }

    public async Task RepairAsync(string executable, CancellationToken cancellationToken)
    {
        if (!preferences.GetBool("createdDesktopEntry", false) || !IsNavigatorDefault())
            return;
        var file = Path.Combine(DataHome, "applications", DesktopId);
        var wanted = LinuxDesktopEntry.For(executable);
        if (!File.Exists(file) || !string.Equals(await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false), wanted, StringComparison.Ordinal))
            await File.WriteAllTextAsync(file, wanted, cancellationToken).ConfigureAwait(false);
    }

    private string DataHome => environment("XDG_DATA_HOME") is { Length: > 0 } data ? data : Path.Combine(home, ".local", "share");

    private string ConfigHome => environment("XDG_CONFIG_HOME") is { Length: > 0 } config ? config : Path.Combine(home, ".config");

    private string? FindDesktopEntry()
    {
        var folders = new List<string> { DataHome, Path.Combine(DataHome, "flatpak", "exports", "share") };
        folders.AddRange((environment("XDG_DATA_DIRS") is { Length: > 0 } dirs ? dirs : "/usr/local/share:/usr/share").Split(':', StringSplitOptions.RemoveEmptyEntries));
        foreach (var folder in folders)
        {
            var candidate = Path.Combine(folder, "applications", DesktopId);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }


    /// <summary>The desktop entry currently registered for folders, or null.</summary>
    private static string? Query()
    {
        if (PluginProcess.FindExecutable("xdg-mime") is not { } xdgMime)
            return null;
        try
        {
            var result = PluginProcess.RunAsync(new PluginProcessRequest(xdgMime, ["query", "default", MimeType]) { Timeout = Timeout }, CancellationToken.None).GetAwaiter().GetResult();
            var id = result.Output.Trim();
            return result.Succeeded && id.Length > 0 ? id : null;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            return null;
        }
    }

    private static async Task RunAsync(string xdgMime, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await PluginProcess.RunAsync(new PluginProcessRequest(xdgMime, arguments) { Timeout = Timeout }, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new InvalidOperationException((result.Error.Length > 0 ? result.Error : result.Output).Trim());
    }

    /// <summary>Without a previous default: removes Navigator's line from mimeapps.list, so the desktop's own choice applies.</summary>
    private void RemoveFromMimeApps()
    {
        var file = Path.Combine(ConfigHome, "mimeapps.list");
        if (!File.Exists(file))
            return;
        var lines = File.ReadAllLines(file);
        var kept = lines.Where(line => !(line.StartsWith(MimeType + "=", StringComparison.Ordinal) && line.Contains(DesktopId, StringComparison.Ordinal))).ToArray();
        if (kept.Length != lines.Length)
            File.WriteAllLines(file, kept);
    }
}

/// <summary>The desktop entry Navigator writes when none is installed (hidden from menus: it only serves folders).</summary>
internal static class LinuxDesktopEntry
{
    public static string For(string executable) => string.Join('\n',
        "[Desktop Entry]",
        "Type=Application",
        "Name=Navigator",
        "GenericName=File Manager",
        "Comment=Fast, modern file manager",
        $"Exec=\"{executable.Replace("\"", "\\\"", StringComparison.Ordinal)}\" %U",
        "Icon=io.github.igorbabayan.Navigator",
        "Terminal=false",
        "Categories=System;FileTools;FileManager;Utility;",
        "MimeType=inode/directory;",
        "StartupWMClass=navigator",
        "NoDisplay=true",
        string.Empty);
}
