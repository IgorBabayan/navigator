using System.Globalization;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using Microsoft.Win32;
using Navigator.Core.Platform;

namespace Navigator.Platform.Startup;

/// <summary>Writes a small text file only when its content changes (no needless disk writes at every start).</summary>
internal static class LoginItemFile
{
    public static bool Write(string path, string content)
    {
        try
        {
            if (File.Exists(path) && File.ReadAllText(path) == content)
                return true;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Navigator.Core.Settings.AtomicFile.WriteAllText(path, content, keepBackup: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing else to do; the agent exits on its own when it has nothing to hold.
        }
    }
}

/// <summary>Text formats used by the login items and desktop shortcuts (pure functions, testable on every OS).</summary>
internal static class ShortcutText
{
    private static readonly System.Buffers.SearchValues<char> Reserved = System.Buffers.SearchValues.Create(" \t\n\"'\\><~|&;$*?#()`");

    /// <summary>Desktop Entry quoting: reserved characters inside double quotes, "%" doubled.</summary>
    internal static string DesktopEntryQuote(string value)
    {
        var escaped = value.Replace("%", "%%", StringComparison.Ordinal);
        if (escaped.Length > 0 && escaped.AsSpan().IndexOfAny(Reserved) < 0)
            return escaped;
        var builder = new StringBuilder(escaped.Length + 8).Append('"');
        foreach (var c in escaped)
        {
            if (c is '"' or '`' or '$' or '\\')
                builder.Append('\\');
            builder.Append(c);
        }

        return builder.Append('"').ToString();
    }

    /// <summary>GTK accelerator text ("&lt;Control&gt;&lt;Alt&gt;e") for a gesture, or null when the key has no GNOME name.</summary>
    internal static string? Accelerator(HotkeyGesture gesture)
    {
        var key = gesture.Key switch
        {
            { Length: 1 } k when char.IsAsciiLetter(k[0]) => k.ToLowerInvariant(),
            { Length: 2 } k when k[0] == 'D' && char.IsAsciiDigit(k[1]) => k[1..],
            var k when k.StartsWith("NumPad", StringComparison.Ordinal) && k.Length == 7 && char.IsAsciiDigit(k[6]) => "KP_" + k[6..],
            var k when k.Length is 2 or 3 && k[0] == 'F' && int.TryParse(k.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var f) && f is >= 1 and <= 24 => k,
            "Space" => "space",
            "Enter" or "Return" => "Return",
            "Escape" => "Escape",
            "Tab" => "Tab",
            "Back" => "BackSpace",
            "Insert" => "Insert",
            "Delete" => "Delete",
            "Home" => "Home",
            "End" => "End",
            "PageUp" or "Prior" => "Page_Up",
            "PageDown" or "Next" => "Page_Down",
            "Left" or "Right" or "Up" or "Down" => gesture.Key,
            "OemComma" => "comma",
            "OemPeriod" => "period",
            "OemMinus" => "minus",
            "OemPlus" => "equal",
            "OemQuestion" => "slash",
            "OemSemicolon" => "semicolon",
            "OemQuotes" => "apostrophe",
            "OemOpenBrackets" => "bracketleft",
            "OemCloseBrackets" => "bracketright",
            "OemPipe" => "backslash",
            "OemTilde" => "grave",
            _ => null,
        };
        if (key is null)
            return null;
        var builder = new StringBuilder();
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Control))
            builder.Append("<Control>");
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Shift))
            builder.Append("<Shift>");
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Alt))
            builder.Append("<Alt>");
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Meta))
            builder.Append("<Super>");
        return builder.Append(key).ToString();
    }

    /// <summary>Parses gsettings' printed string array ("@as []", "['/a/', '/b/']").</summary>
    internal static List<string> ParseList(string text)
    {
        var result = new List<string>();
        var span = text.AsSpan();
        while (true)
        {
            var open = span.IndexOf('\'');
            if (open < 0)
                break;
            span = span[(open + 1)..];
            var close = span.IndexOf('\'');
            if (close < 0)
                break;
            result.Add(span[..close].ToString());
            span = span[(close + 1)..];
        }

        return result;
    }

    internal static string FormatList(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(Literal)) + "]";

    /// <summary>A GVariant string literal.</summary>
    internal static string Literal(string value) => "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";
}

/// <summary>Windows: a value under HKCU\Software\Microsoft\Windows\CurrentVersion\Run (per user, no elevation).</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsLoginItem : ILoginItemService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Navigator shortcut";

    public bool IsSupported => true;

    public bool Register(string executable, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var command = string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote));
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key.GetValue(ValueName) as string != command)
                key.SetValue(ValueName, command, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return false;
        }
    }

    public void Unregister()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            // Left as it is; the agent exits at once when it has no shortcut to hold.
        }
    }

    private static string Quote(string value) => value.Length > 0 && value.AsSpan().IndexOfAny(" \t\"") < 0 ? value : "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}

/// <summary>Linux: an XDG autostart entry (~/.config/autostart), honored by GNOME, KDE, Xfce, Cinnamon, MATE…</summary>
[SupportedOSPlatform("linux")]
internal sealed class XdgLoginItem : ILoginItemService
{
    private readonly string _path = Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config && Path.IsPathRooted(config)
            ? config
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "autostart", "io.github.igorbabayan.Navigator.Shortcut.desktop");

    public bool IsSupported => true;

    public bool Register(string executable, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var exec = string.Join(' ', new[] { executable }.Concat(arguments).Select(ShortcutText.DesktopEntryQuote));
        var content = new StringBuilder()
            .Append("[Desktop Entry]\n")
            .Append("Type=Application\n")
            .Append("Name=Navigator global shortcut\n")
            .Append("Comment=Opens Navigator with its global keyboard shortcut\n")
            .Append("Exec=").Append(exec).Append('\n')
            .Append("Icon=io.github.igorbabayan.Navigator\n")
            .Append("Terminal=false\n")
            .Append("NoDisplay=true\n")
            .Append("X-GNOME-Autostart-enabled=true\n")
            .ToString();
        return LoginItemFile.Write(_path, content);
    }

    public void Unregister() => LoginItemFile.Delete(_path);
}

/// <summary>macOS: a per-user LaunchAgent that launchd starts at login (no Login Items permission prompt).</summary>
[SupportedOSPlatform("macos")]
internal sealed class MacLoginItem : ILoginItemService
{
    private const string Label = "io.github.igorbabayan.navigator.shortcut";
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", Label + ".plist");

    public bool IsSupported => true;

    public bool Register(string executable, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var builder = new StringBuilder()
            .Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n")
            .Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n")
            .Append("<plist version=\"1.0\">\n<dict>\n")
            .Append("  <key>Label</key><string>").Append(Label).Append("</string>\n")
            .Append("  <key>ProgramArguments</key>\n  <array>\n");
        foreach (var part in new[] { executable }.Concat(arguments))
            builder.Append("    <string>").Append(SecurityElement.Escape(part)).Append("</string>\n");
        builder.Append("  </array>\n")
            .Append("  <key>RunAtLoad</key><true/>\n")
            .Append("  <key>ProcessType</key><string>Interactive</string>\n")
            .Append("  <key>LimitLoadToSessionType</key><string>Aqua</string>\n")
            .Append("</dict>\n</plist>\n");
        return LoginItemFile.Write(_path, builder.ToString());
    }

    public void Unregister() => LoginItemFile.Delete(_path);
}

internal sealed class UnsupportedLoginItem : ILoginItemService
{
    public bool IsSupported => false;

    public bool Register(string executable, IReadOnlyList<string> arguments) => false;

    public void Unregister()
    {
    }
}

/// <summary>
/// GNOME (and Ubuntu, Budgie, Pantheon…) custom keyboard shortcut through gsettings: the desktop runs
/// <c>Navigator --activate</c>, so the shortcut works under Wayland and while Navigator is not running, with no process
/// of Navigator's own waiting for it.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class GnomeDesktopShortcut : IDesktopShortcutService
{
    private const string MediaKeys = "org.gnome.settings-daemon.plugins.media-keys";
    private const string BindingPath = "/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/navigator-activate/";
    private const string BindingSchema = MediaKeys + ".custom-keybinding:" + BindingPath;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly string? _gsettings = ProcessRunner.FindOnPath("gsettings");

    public bool IsSupported => _gsettings is not null
                               && (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty)
                               .Split(':').Any(d => d.Equals("GNOME", StringComparison.OrdinalIgnoreCase) || d.Equals("Unity", StringComparison.OrdinalIgnoreCase)
                                                    || d.Equals("Budgie", StringComparison.OrdinalIgnoreCase) || d.Equals("Pantheon", StringComparison.OrdinalIgnoreCase));

    public async Task<HotkeyRegistrationResult> RegisterAsync(HotkeyGesture gesture, string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (_gsettings is not { } tool)
            return HotkeyRegistrationResult.Unsupported;
        if (ShortcutText.Accelerator(gesture) is not { } binding)
            return HotkeyRegistrationResult.InvalidGesture;

        var list = await GetListAsync(tool, cancellationToken).ConfigureAwait(false);
        if (list is null)
            return HotkeyRegistrationResult.Unsupported;
        if (!list.Contains(BindingPath))
        {
            list.Add(BindingPath);
            if (!await SetAsync(tool, MediaKeys, "custom-keybindings", ShortcutText.FormatList(list), cancellationToken).ConfigureAwait(false))
                return HotkeyRegistrationResult.Unsupported;
        }

        var command = string.Join(' ', new[] { executable }.Concat(arguments).Select(ShortcutText.DesktopEntryQuote).Select(p => p.Replace("%%", "%", StringComparison.Ordinal)));
        var ok = await SetAsync(tool, BindingSchema, "name", ShortcutText.Literal("Navigator"), cancellationToken).ConfigureAwait(false)
                 && await SetAsync(tool, BindingSchema, "command", ShortcutText.Literal(command), cancellationToken).ConfigureAwait(false)
                 && await SetAsync(tool, BindingSchema, "binding", ShortcutText.Literal(binding), cancellationToken).ConfigureAwait(false);
        return ok ? HotkeyRegistrationResult.Registered : HotkeyRegistrationResult.Unsupported;
    }

    public async Task UnregisterAsync(CancellationToken cancellationToken = default)
    {
        if (_gsettings is not { } tool || await GetListAsync(tool, cancellationToken).ConfigureAwait(false) is not { } list || !list.Remove(BindingPath))
            return;
        await SetAsync(tool, MediaKeys, "custom-keybindings", ShortcutText.FormatList(list), cancellationToken).ConfigureAwait(false);
        foreach (var key in new[] { "name", "command", "binding" })
            await ProcessRunner.RunAsync(tool, ["reset", BindingSchema, key], null, Timeout, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<List<string>?> GetListAsync(string tool, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(tool, ["get", MediaKeys, "custom-keybindings"], null, Timeout, cancellationToken).ConfigureAwait(false);
        return result is { ExitCode: 0 } found ? ShortcutText.ParseList(found.Output) : null;
    }

    private static async Task<bool> SetAsync(string tool, string schema, string key, string value, CancellationToken cancellationToken)
        => await ProcessRunner.RunAsync(tool, ["set", schema, key, value], null, Timeout, cancellationToken).ConfigureAwait(false) is { ExitCode: 0 };
}

internal sealed class UnsupportedDesktopShortcut : IDesktopShortcutService
{
    public bool IsSupported => false;

    public Task<HotkeyRegistrationResult> RegisterAsync(HotkeyGesture gesture, string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        => Task.FromResult(HotkeyRegistrationResult.Unsupported);

    public Task UnregisterAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
