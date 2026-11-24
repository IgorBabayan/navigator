namespace Navigator.Core.Platform;

/// <summary>
/// Platform-appropriate locations for Navigator's own data.
/// <list type="bullet">
/// <item>Windows: %APPDATA%\Navigator (settings), %LOCALAPPDATA%\Navigator\Cache and \Logs</item>
/// <item>Linux: $XDG_CONFIG_HOME/navigator, $XDG_CACHE_HOME/navigator, $XDG_STATE_HOME/navigator/logs</item>
/// <item>macOS: ~/Library/Application Support/Navigator, ~/Library/Caches/Navigator, ~/Library/Logs/Navigator</item>
/// </list>
/// Setting NAVIGATOR_DATA_DIR redirects everything below one folder (portable mode, tests).
/// </summary>
public sealed record AppDirectories(string ConfigDirectory, string CacheDirectory, string LogDirectory)
{
    public const string OverrideVariable = "NAVIGATOR_DATA_DIR";

    public string SettingsFile => Path.Combine(ConfigDirectory, "settings.json");

    public string SessionFile => Path.Combine(ConfigDirectory, "session.json");

    public string TagsFile => Path.Combine(ConfigDirectory, "tags.json");

    public string ThumbnailCacheDirectory => Path.Combine(CacheDirectory, "thumbnails");

    public string UpdateDirectory => Path.Combine(CacheDirectory, "updates");

    public static AppDirectories Resolve() => Resolve(Environment.GetEnvironmentVariable, CurrentOs(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static AppDirectories Resolve(Func<string, string?> env, OperatingSystemKind os, string home)
    {
        if (env(OverrideVariable) is { Length: > 0 } root)
            return new AppDirectories(Path.Combine(root, "config"), Path.Combine(root, "cache"), Path.Combine(root, "logs"));

        switch (os)
        {
            case OperatingSystemKind.Windows:
            {
                var roaming = env("APPDATA") ?? Path.Combine(home, "AppData", "Roaming");
                var local = env("LOCALAPPDATA") ?? Path.Combine(home, "AppData", "Local");
                return new AppDirectories(
                    Path.Combine(roaming, "Navigator"),
                    Path.Combine(local, "Navigator", "Cache"),
                    Path.Combine(local, "Navigator", "Logs"));
            }

            case OperatingSystemKind.MacOS:
            {
                var library = Path.Combine(home, "Library");
                return new AppDirectories(
                    Path.Combine(library, "Application Support", "Navigator"),
                    Path.Combine(library, "Caches", "Navigator"),
                    Path.Combine(library, "Logs", "Navigator"));
            }

            default:
            {
                var config = Absolute(env("XDG_CONFIG_HOME")) ?? Path.Combine(home, ".config");
                var cache = Absolute(env("XDG_CACHE_HOME")) ?? Path.Combine(home, ".cache");
                var state = Absolute(env("XDG_STATE_HOME")) ?? Path.Combine(home, ".local", "state");
                return new AppDirectories(
                    Path.Combine(config, "navigator"),
                    Path.Combine(cache, "navigator"),
                    Path.Combine(state, "navigator", "logs"));
            }
        }
    }

    // The XDG spec says relative paths in these variables are invalid and must be ignored.
    private static string? Absolute(string? value) => !string.IsNullOrEmpty(value) && Path.IsPathRooted(value) ? value : null;

    public static OperatingSystemKind CurrentOs()
        => OperatingSystem.IsWindows() ? OperatingSystemKind.Windows
            : OperatingSystem.IsMacOS() ? OperatingSystemKind.MacOS
            : OperatingSystem.IsLinux() ? OperatingSystemKind.Linux
            : OperatingSystemKind.Other;

    public void EnsureCreated()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
