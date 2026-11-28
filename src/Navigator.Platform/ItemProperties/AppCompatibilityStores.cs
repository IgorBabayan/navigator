using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;
using Navigator.Core.Customization;
using Navigator.Core.Platform;

namespace Navigator.Platform.ItemProperties;

public sealed class UnsupportedAppCompatibilityStore : IAppCompatibilityStore
{
    public bool IsSupported(string path) => false;

    public CompatibilitySettings Get(string path) => CompatibilitySettings.Default;

    public bool Set(string path, CompatibilitySettings settings) => false;
}

/// <summary>
/// Windows: the same per-user AppCompat layers Explorer's Compatibility tab writes
/// (HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers, value name = full program path).
/// Layers Navigator does not show are preserved.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsAppCompatibilityStore : IAppCompatibilityStore
{
    private const string LayersKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";

    public bool IsSupported(string path) => Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase);

    public CompatibilitySettings Get(string path)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LayersKey);
            return CompatibilityLayers.Parse(key?.GetValue(path) as string).Settings;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return CompatibilitySettings.Default;
        }
    }

    public bool Set(string path, CompatibilitySettings settings)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(LayersKey, writable: true);
            var (_, other) = CompatibilityLayers.Parse(key.GetValue(path) as string);
            var value = CompatibilityLayers.Format(settings, other);
            if (value is null)
                key.DeleteValue(path, throwOnMissingValue: false);
            else
                key.SetValue(path, value, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
