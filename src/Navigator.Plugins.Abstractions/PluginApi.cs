namespace Navigator.Plugins;

/// <summary>
/// Version of the plugin contract in this assembly. A plugin declares the version it was built against
/// (plugin.json "apiVersion"); the host loads it when the major versions are equal and the plugin's minor
/// version is not newer than the host's.
/// </summary>
public static class PluginApi
{
    public const int Major = 1;

    /// <summary>1.1: <see cref="IRemoteLocationMapper"/>.</summary>
    public const int Minor = 1;

    public static string Version => $"{Major}.{Minor}";

    /// <summary>True when a plugin built against <paramref name="apiVersion"/> ("1.0") can run on this host.</summary>
    public static bool IsCompatible(string? apiVersion)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return false;
        var parts = apiVersion.Split('.');
        return parts.Length is 1 or 2
               && int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var major)
               && major == Major
               && (parts.Length == 1 || (int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var minor) && minor <= Minor));
    }
}

/// <summary>
/// Entry point of a plugin. The host creates exactly one instance (public parameterless constructor) and calls
/// <see cref="Initialize"/> once, off the UI thread. Implement <see cref="IDisposable"/> to release connections
/// when the plugin is removed or the app exits.
/// </summary>
public interface INavigatorPlugin
{
    /// <summary>
    /// Registers the plugin's contributions with <paramref name="context"/>. Must be quick: no network or disk scans
    /// here (they belong in the contribution methods, which run on demand).
    /// </summary>
    void Initialize(IPluginContext context);
}
