using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Navigator.Core.Platform;

namespace Navigator.Platform;

/// <summary>Well-known user folders: Windows Known Folders, XDG user directories, macOS home layout.</summary>
public sealed partial class KnownFolderProvider : IKnownFolderProvider
{
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");
    private readonly Dictionary<KnownFolder, string?> _cache = [];
    private readonly Lock _gate = new();
    private readonly string _home;
    private readonly OperatingSystemKind _os;
    private readonly Func<string, string?> _env;

    public KnownFolderProvider()
        : this(AppDirectories.CurrentOs(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable)
    {
    }

    internal KnownFolderProvider(OperatingSystemKind os, string home, Func<string, string?> env)
    {
        _os = os;
        _home = home;
        _env = env;
    }

    public string? GetPath(KnownFolder folder)
    {
        lock (_gate)
        {
            if (!_cache.TryGetValue(folder, out var path))
            {
                path = Resolve(folder);
                if (path is not null && !Directory.Exists(path))
                    path = null;
                _cache[folder] = path;
            }

            return path;
        }
    }

    private string? Resolve(KnownFolder folder)
    {
        if (folder == KnownFolder.Home)
            return _home;

        return _os switch
        {
            OperatingSystemKind.Windows => ResolveWindows(folder),
            OperatingSystemKind.MacOS => folder switch
            {
                KnownFolder.Desktop => Path.Combine(_home, "Desktop"),
                KnownFolder.Documents => Path.Combine(_home, "Documents"),
                KnownFolder.Downloads => Path.Combine(_home, "Downloads"),
                KnownFolder.Pictures => Path.Combine(_home, "Pictures"),
                KnownFolder.Music => Path.Combine(_home, "Music"),
                KnownFolder.Videos => Path.Combine(_home, "Movies"),
                _ => null,
            },
            _ => ResolveXdg(folder),
        };
    }

    private string? ResolveWindows(KnownFolder folder)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        return folder switch
        {
            KnownFolder.Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            KnownFolder.Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            KnownFolder.Pictures => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            KnownFolder.Music => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            KnownFolder.Videos => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            KnownFolder.Downloads => GetWindowsDownloads() ?? Path.Combine(_home, "Downloads"),
            _ => null,
        };
    }

    [SupportedOSPlatform("windows")]
    private static string? GetWindowsDownloads()
    {
        var id = DownloadsFolderId;
        if (SHGetKnownFolderPath(in id, 0, IntPtr.Zero, out var pointer) != 0)
            return null;
        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid folderId, uint flags, IntPtr token, out IntPtr path);

    private string? ResolveXdg(KnownFolder folder)
    {
        var key = folder switch
        {
            KnownFolder.Desktop => "XDG_DESKTOP_DIR",
            KnownFolder.Documents => "XDG_DOCUMENTS_DIR",
            KnownFolder.Downloads => "XDG_DOWNLOAD_DIR",
            KnownFolder.Pictures => "XDG_PICTURES_DIR",
            KnownFolder.Music => "XDG_MUSIC_DIR",
            KnownFolder.Videos => "XDG_VIDEOS_DIR",
            _ => null,
        };

        if (key is null)
            return null;

        var configHome = _env("XDG_CONFIG_HOME") is { Length: > 0 } c && Path.IsPathRooted(c) ? c : Path.Combine(_home, ".config");
        var file = Path.Combine(configHome, "user-dirs.dirs");
        var fromFile = File.Exists(file) ? ParseUserDirs(File.ReadLines(file), key, _home) : null;
        if (fromFile is not null)
            return fromFile;

        var fallback = folder switch
        {
            KnownFolder.Desktop => "Desktop",
            KnownFolder.Documents => "Documents",
            KnownFolder.Downloads => "Downloads",
            KnownFolder.Pictures => "Pictures",
            KnownFolder.Music => "Music",
            KnownFolder.Videos => "Videos",
            _ => null,
        };
        return fallback is null ? null : Path.Combine(_home, fallback);
    }

    /// <summary>Parses user-dirs.dirs lines of the form XDG_DOWNLOAD_DIR="$HOME/Downloads".</summary>
    internal static string? ParseUserDirs(IEnumerable<string> lines, string key, string home)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || !line.StartsWith(key + "=", StringComparison.Ordinal))
                continue;

            var value = line[(key.Length + 1)..].Trim().Trim('"');
            if (value.StartsWith("$HOME", StringComparison.Ordinal))
                value = home + value[5..];
            else if (!value.StartsWith('/'))
                continue;

            value = value.TrimEnd('/');
            // XDG uses $HOME/ (i.e. the home folder itself) to say "this directory is disabled".
            return string.Equals(value, home.TrimEnd('/'), StringComparison.Ordinal) ? null : value;
        }

        return null;
    }
}
