using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace Navigator.Plugin.CloudSync;

internal enum CloudProvider
{
    Other,
    OneDrive,
    Dropbox,
    ICloud,
    GoogleDrive,
    Box,
    Nextcloud,
}

/// <summary>A folder a sync client keeps in step with the cloud.</summary>
internal sealed record SyncRoot(string Path, CloudProvider Provider, string DisplayName);

/// <summary>Where the sync clients are looked for; replaceable in tests.</summary>
internal sealed record SyncRootSources(string Home, string? AppData, string? LocalAppData, Func<string, string?> Environment)
{
    public static SyncRootSources Current => new(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
        NullIfEmpty(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData)),
        NullIfEmpty(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData)),
        System.Environment.GetEnvironmentVariable);

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}

/// <summary>
/// Finds the sync folders of the usual clients:
/// <list type="bullet">
/// <item>Windows: every Cloud Files sync root registered for the user (OneDrive, Dropbox, iCloud, Google Drive, Box,
/// Nextcloud… — SyncRootManager in the registry), plus OneDrive's environment variables, Dropbox's info.json and
/// iCloud Drive's classic folder.</item>
/// <item>macOS: File Provider folders in ~/Library/CloudStorage, iCloud Drive, Dropbox.</item>
/// <item>Linux: Dropbox, the OneDrive client (abraunegg), Nextcloud.</item>
/// </list>
/// The answer is cached for half a minute; folders that do not exist are left out.
/// </summary>
internal sealed class SyncRootLocator(SyncRootSources? sources = null)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);
    internal static readonly StringComparer PathComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly Lock _gate = new();
    private IReadOnlyList<SyncRoot> _roots = [];
    private long _readAt = long.MinValue;

    public IReadOnlyList<SyncRoot> GetRoots()
    {
        lock (_gate)
        {
            var now = System.Environment.TickCount64;
            if (_readAt != long.MinValue && now - _readAt < Lifetime.TotalMilliseconds)
                return _roots;
            _roots = Discover(sources ?? SyncRootSources.Current);
            _readAt = now;
            return _roots;
        }
    }

    /// <summary>The sync folder <paramref name="path"/> lies in (the deepest one), or null.</summary>
    public SyncRoot? FindRoot(string path)
    {
        var comparison = PathComparer == StringComparer.Ordinal ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        SyncRoot? best = null;
        foreach (var root in GetRoots())
        {
            var prefix = System.IO.Path.TrimEndingDirectorySeparator(root.Path);
            if (path.StartsWith(prefix, comparison) && (path.Length == prefix.Length || path[prefix.Length] is '/' or '\\')
                && (best is null || prefix.Length > best.Path.Length))
                best = root;
        }

        return best;
    }

    public void Invalidate()
    {
        lock (_gate)
            _readAt = long.MinValue;
    }

    internal static IReadOnlyList<SyncRoot> Discover(SyncRootSources sources)
    {
        var found = new List<SyncRoot>();
        void Add(string? path, CloudProvider provider, string name)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            string full;
            try
            {
                full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
                if (!Directory.Exists(full))
                    return;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
            {
                return;
            }

            if (!found.Exists(r => PathComparer.Equals(r.Path, full)))
                found.Add(new SyncRoot(full, provider, name));
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                foreach (var (path, provider, name) in WindowsSyncRoots())
                    Add(path, provider, name);
                Add(sources.Environment("OneDriveConsumer"), CloudProvider.OneDrive, "OneDrive - Personal");
                Add(sources.Environment("OneDriveCommercial"), CloudProvider.OneDrive, "OneDrive");
                Add(sources.Environment("OneDrive"), CloudProvider.OneDrive, "OneDrive");
                Add(System.IO.Path.Combine(sources.Home, "iCloudDrive"), CloudProvider.ICloud, "iCloud Drive");
                foreach (var folder in new[] { sources.LocalAppData, sources.AppData })
                {
                    if (folder is not null)
                        AddDropbox(System.IO.Path.Combine(folder, "Dropbox", "info.json"), Add);
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                var storage = System.IO.Path.Combine(sources.Home, "Library", "CloudStorage");
                if (Directory.Exists(storage))
                {
                    foreach (var folder in Directory.EnumerateDirectories(storage).Order(StringComparer.Ordinal))
                    {
                        var (provider, name) = DescribeCloudStorageFolder(System.IO.Path.GetFileName(folder));
                        Add(folder, provider, name);
                    }
                }

                Add(System.IO.Path.Combine(sources.Home, "Library", "Mobile Documents", "com~apple~CloudDocs"), CloudProvider.ICloud, "iCloud Drive");
                AddDropbox(System.IO.Path.Combine(sources.Home, ".dropbox", "info.json"), Add);
                AddNextcloud(System.IO.Path.Combine(sources.Home, "Library", "Preferences", "Nextcloud", "nextcloud.cfg"), Add);
            }
            else
            {
                AddDropbox(System.IO.Path.Combine(sources.Home, ".dropbox", "info.json"), Add);
                var config = sources.Environment("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : System.IO.Path.Combine(sources.Home, ".config");
                AddLinuxOneDrive(System.IO.Path.Combine(config, "onedrive", "config"), sources.Home, Add);
                AddNextcloud(System.IO.Path.Combine(config, "Nextcloud", "nextcloud.cfg"), Add);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // What was found before the failure is still useful.
        }

        return [.. found.OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>"OneDrive-Personal", "OneDrive-Contoso", "Dropbox", "GoogleDrive-me@example.com", "Box-Box"…</summary>
    internal static (CloudProvider Provider, string Name) DescribeCloudStorageFolder(string folder)
    {
        var dash = folder.IndexOf('-', StringComparison.Ordinal);
        var vendor = dash < 0 ? folder : folder[..dash];
        var account = dash < 0 ? null : folder[(dash + 1)..];
        var provider = ProviderOf(vendor);
        var label = provider switch
        {
            CloudProvider.OneDrive => "OneDrive",
            CloudProvider.Dropbox => "Dropbox",
            CloudProvider.GoogleDrive => "Google Drive",
            CloudProvider.Box => "Box",
            CloudProvider.ICloud => "iCloud Drive",
            CloudProvider.Nextcloud => "Nextcloud",
            _ => vendor,
        };
        return (provider, account is { Length: > 0 } && !string.Equals(account, vendor, StringComparison.OrdinalIgnoreCase) ? $"{label} - {account}" : label);
    }

    internal static CloudProvider ProviderOf(string vendor)
    {
        if (vendor.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase))
            return CloudProvider.OneDrive;
        if (vendor.StartsWith("Dropbox", StringComparison.OrdinalIgnoreCase))
            return CloudProvider.Dropbox;
        if (vendor.StartsWith("iCloud", StringComparison.OrdinalIgnoreCase))
            return CloudProvider.ICloud;
        if (vendor.StartsWith("GoogleDrive", StringComparison.OrdinalIgnoreCase) || vendor.StartsWith("DriveFS", StringComparison.OrdinalIgnoreCase))
            return CloudProvider.GoogleDrive;
        if (vendor.StartsWith("Box", StringComparison.OrdinalIgnoreCase))
            return CloudProvider.Box;
        if (vendor.StartsWith("Nextcloud", StringComparison.OrdinalIgnoreCase))
            return CloudProvider.Nextcloud;
        return CloudProvider.Other;
    }

    /// <summary>Dropbox's info.json: <c>{"personal": {"path": …}, "business": {"path": …}}</c>.</summary>
    internal static void AddDropbox(string infoFile, Action<string?, CloudProvider, string> add)
    {
        if (!File.Exists(infoFile))
            return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(infoFile));
            foreach (var account in document.RootElement.EnumerateObject())
            {
                if (account.Value.ValueKind == JsonValueKind.Object && account.Value.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String)
                    add(path.GetString(), CloudProvider.Dropbox, account.Name == "business" ? "Dropbox (Business)" : "Dropbox");
            }
        }
        catch (JsonException)
        {
            // Unreadable: Dropbox rewrites it on its next start.
        }
    }

    /// <summary>The OneDrive client for Linux: <c>sync_dir = "~/OneDrive"</c> in its config (default ~/OneDrive).</summary>
    internal static void AddLinuxOneDrive(string configFile, string home, Action<string?, CloudProvider, string> add)
    {
        if (!File.Exists(configFile))
            return;
        var folder = "~/OneDrive";
        foreach (var raw in File.ReadLines(configFile))
        {
            var line = raw.Trim();
            if (!line.StartsWith("sync_dir", StringComparison.Ordinal) || line.IndexOf('=', StringComparison.Ordinal) is not (> 0 and var equals))
                continue;
            folder = line[(equals + 1)..].Trim().Trim('"');
        }

        add(folder.StartsWith('~') ? home + folder[1..] : folder, CloudProvider.OneDrive, "OneDrive");
    }

    /// <summary>Nextcloud's nextcloud.cfg lists every synced folder as <c>0\Folders\1\localPath=/home/me/Nextcloud/</c>.</summary>
    internal static void AddNextcloud(string configFile, Action<string?, CloudProvider, string> add)
    {
        if (!File.Exists(configFile))
            return;
        foreach (var line in File.ReadLines(configFile))
        {
            var at = line.IndexOf("\\localPath=", StringComparison.Ordinal);
            if (at > 0)
                add(line[(at + "\\localPath=".Length)..].Trim(), CloudProvider.Nextcloud, "Nextcloud");
        }
    }

    /// <summary>
    /// Cloud Files sync roots of the current user: HKLM\…\Explorer\SyncRootManager\{Provider}!{SID}!{Account}, with the
    /// folder in its UserSyncRoots value named after the user's SID.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static IEnumerable<(string Path, CloudProvider Provider, string Name)> WindowsSyncRoots()
    {
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        using var manager = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager");
        if (manager is null || sid is null)
            yield break;
        foreach (var name in manager.GetSubKeyNames())
        {
            using var root = manager.OpenSubKey(name);
            using var users = root?.OpenSubKey("UserSyncRoots");
            if (root is null || users?.GetValue(sid) is not string path)
                continue;
            var parts = name.Split('!');
            var provider = ProviderOf(parts[0]);
            var display = root.GetValue("DisplayNameResource") as string;
            yield return (path, provider, Native.ResolveDisplayName(display) ?? FallbackName(provider, parts));
        }
    }

    private static string FallbackName(CloudProvider provider, string[] keyParts)
    {
        var account = keyParts.Length >= 3 ? keyParts[2] : null;
        return provider switch
        {
            CloudProvider.OneDrive => account is "Personal" ? "OneDrive - Personal" : "OneDrive",
            CloudProvider.ICloud => "iCloud Drive",
            CloudProvider.GoogleDrive => "Google Drive",
            _ => keyParts[0],
        };
    }
}
