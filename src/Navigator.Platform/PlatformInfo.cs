using System.Runtime.InteropServices;
using Navigator.Core.Platform;

namespace Navigator.Platform;

public sealed class PlatformInfo : IPlatformInfo
{
    public OperatingSystemKind OperatingSystem { get; } = AppDirectories.CurrentOs();

    public Architecture ProcessArchitecture { get; } = RuntimeInformation.ProcessArchitecture;

    public string RuntimeIdentifier => BuildRuntimeIdentifier(OperatingSystem, ProcessArchitecture);

    public string? DesktopEnvironment { get; } = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");

    public bool IsWaylandSession { get; } =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
        || string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase);

    public string HomeDirectory { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string BuildRuntimeIdentifier(OperatingSystemKind os, Architecture architecture)
    {
        var osPart = os switch
        {
            OperatingSystemKind.Windows => "win",
            OperatingSystemKind.MacOS => "osx",
            _ => "linux",
        };

        var archPart = architecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => "x64",
        };

        return $"{osPart}-{archPart}";
    }
}
