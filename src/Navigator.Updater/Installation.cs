using System.Reflection;
using Navigator.Core.Platform;
using Navigator.Core.Versioning;

namespace Navigator.Updater;

public enum InstallationKind
{
    /// <summary>Local/dev build or unknown layout: never self-updates.</summary>
    Development,
    WindowsInstaller,
    WindowsPortable,
    LinuxAppImage,
    LinuxPortable,

    /// <summary>Installed by apt/dnf/pacman: the package manager owns the files; we only notify.</summary>
    LinuxPackageManaged,
    MacAppBundle,

    /// <summary>A bundle in a location the user cannot write (e.g. /Applications without admin rights).</summary>
    MacAppBundleReadOnly,
}

/// <summary>Build-time configuration baked into release builds by CI (AssemblyMetadata attributes).</summary>
public sealed record UpdateConfiguration(string? Repository, string? PublicKey, SemVersion CurrentVersion)
{
    public const string RepositoryKey = "UpdateRepository";
    public const string PublicKeyKey = "UpdatePublicKey";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Repository) && CurrentVersion.Major + CurrentVersion.Minor + CurrentVersion.Patch > 0;

    public static UpdateConfiguration FromAssembly(Assembly? assembly)
    {
        var metadata = assembly?.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(a => a.Key, a => a.Value) ?? [];
        var informational = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = SemVersion.TryParse(informational, out var parsed) ? parsed.WithoutMetadata() : SemVersion.Create(0, 0, 0, "dev");
        return new UpdateConfiguration(
            metadata.GetValueOrDefault(RepositoryKey) is { Length: > 0 } repository ? repository : null,
            metadata.GetValueOrDefault(PublicKeyKey) is { Length: > 0 } key ? key : null,
            version);
    }
}

/// <summary>Determines how the running copy was installed, which decides whether and how it may replace itself.</summary>
public static class InstallationDetector
{
    /// <summary>Marker shipped inside portable archives (Windows ZIP, Linux tar.gz).</summary>
    public const string PortableMarker = ".navigator-portable";

    public static InstallationKind Detect(UpdateConfiguration configuration, OperatingSystemKind os, string baseDirectory, Func<string, string?> env)
    {
        if (!configuration.IsConfigured || configuration.CurrentVersion.IsPrerelease && configuration.CurrentVersion.Prerelease == "dev")
            return InstallationKind.Development;

        var directory = baseDirectory.TrimEnd('/', '\\');
        switch (os)
        {
            case OperatingSystemKind.Windows:
                if (File.Exists(Path.Combine(directory, "unins000.exe")))
                    return InstallationKind.WindowsInstaller;
                return File.Exists(Path.Combine(directory, PortableMarker)) ? InstallationKind.WindowsPortable : InstallationKind.Development;

            case OperatingSystemKind.Linux:
                if (env("APPIMAGE") is { Length: > 0 } appImage && File.Exists(appImage))
                    return InstallationKind.LinuxAppImage;
                if (File.Exists(Path.Combine(directory, PortableMarker)))
                    return InstallationKind.LinuxPortable;
                return directory.StartsWith("/usr/", StringComparison.Ordinal) || directory.StartsWith("/opt/", StringComparison.Ordinal)
                    ? InstallationKind.LinuxPackageManaged
                    : InstallationKind.Development;

            case OperatingSystemKind.MacOS:
            {
                var bundle = GetMacBundle(directory);
                if (bundle is null || bundle.StartsWith("/Volumes/", StringComparison.Ordinal))
                    return InstallationKind.Development; // running from the DMG or a publish folder
                return IsWritable(Path.GetDirectoryName(bundle)!) ? InstallationKind.MacAppBundle : InstallationKind.MacAppBundleReadOnly;
            }

            default:
                return InstallationKind.Development;
        }
    }

    /// <summary>".../Navigator.app/Contents/MacOS" → ".../Navigator.app".</summary>
    public static string? GetMacBundle(string baseDirectory)
    {
        var macOs = new DirectoryInfo(baseDirectory.TrimEnd('/'));
        var bundle = macOs.Parent?.Parent;
        return macOs.Name == "MacOS" && macOs.Parent?.Name == "Contents" && bundle?.Extension == ".app" ? bundle.FullName : null;
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".navigator-write-test-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>Picks the one asset this installation can update itself from.</summary>
public static class UpdateAssetSelector
{
    public static ManifestAsset? Select(UpdateManifest manifest, string runtimeIdentifier, InstallationKind kind)
    {
        var (assetKind, suffix) = kind switch
        {
            InstallationKind.WindowsInstaller => (AssetKinds.Installer, "-setup.exe"),
            InstallationKind.WindowsPortable => (AssetKinds.Portable, ".zip"),
            InstallationKind.LinuxAppImage => (AssetKinds.AppImage, ".AppImage"),
            InstallationKind.LinuxPortable => (AssetKinds.Portable, ".tar.gz"),
            InstallationKind.MacAppBundle => (AssetKinds.AppArchive, ".zip"),
            _ => (null, null),
        };

        if (assetKind is null)
            return null;

        // Both the declared kind/RID and the file extension must agree; anything unexpected is rejected.
        return manifest.Assets.SingleOrDefault(a =>
            string.Equals(a.Rid, runtimeIdentifier, StringComparison.Ordinal)
            && string.Equals(a.Kind, assetKind, StringComparison.Ordinal)
            && a.Name.EndsWith(suffix!, StringComparison.Ordinal));
    }

    public static bool SupportsSelfUpdate(InstallationKind kind)
        => kind is InstallationKind.WindowsInstaller or InstallationKind.WindowsPortable or InstallationKind.LinuxAppImage
            or InstallationKind.LinuxPortable or InstallationKind.MacAppBundle;
}
