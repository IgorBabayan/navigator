using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using Navigator.Core.Platform;

namespace Navigator.Platform;

/// <summary>
/// Shared caching for the "New" menu's document types: one scan at a time, off the caller's thread, reused for a while
/// (types are installed rarely; the first scan is started at startup so the menu never waits).
/// </summary>
public abstract class CachedNewItemTemplates : INewItemTemplates
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly Lock _gate = new();
    private Task<IReadOnlyList<NewItemTemplate>>? _scan;
    private long _scannedAt;

    public Task<IReadOnlyList<NewItemTemplate>> GetAsync(CancellationToken cancellationToken = default)
    {
        Task<IReadOnlyList<NewItemTemplate>> scan;
        lock (_gate)
        {
            var stale = _scan is null || (_scan.IsCompleted && Environment.TickCount64 - _scannedAt > Lifetime.TotalMilliseconds);
            if (stale)
            {
                _scannedAt = Environment.TickCount64;
                _scan = Task.Run(SafeScan, CancellationToken.None);
            }

            scan = _scan!;
        }

        return scan.WaitAsync(cancellationToken);
    }

    private IReadOnlyList<NewItemTemplate> SafeScan()
    {
        try
        {
            var found = Scan();
            found.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.DisplayName, b.DisplayName));
            return found;
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    protected abstract List<NewItemTemplate> Scan();
}

/// <summary>
/// Windows: the types registered for Explorer's New menu. A class key (".ext") or its ProgID key carries a "ShellNew"
/// key whose values say how to make the file: NullFile (empty), Data (these bytes) or FileName (copy of a template).
/// Command-only registrations (they run a program) and types Navigator lists itself are left out.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsNewItemTemplates : CachedNewItemTemplates
{
    private const int FriendlyDocumentName = 3; // ASSOCSTR_FRIENDLYDOCNAME

    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase) { ".lnk", ".url", ".library-ms", ".bfc" };

    protected override List<NewItemTemplate> Scan()
    {
        var result = new List<NewItemTemplate>();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in Registry.ClassesRoot.GetSubKeyNames())
        {
            if (extension.Length < 2 || extension[0] != '.' || Excluded.Contains(extension))
                continue;

            using var classKey = Registry.ClassesRoot.OpenSubKey(extension);
            if (classKey is null)
                continue;
            var progId = classKey.GetValue(null) as string;
            using var shellNew = classKey.OpenSubKey("ShellNew")
                ?? (string.IsNullOrEmpty(progId) ? null : classKey.OpenSubKey(progId + "\\ShellNew"));
            if (shellNew is null || Describe(extension, progId, shellNew) is not { } template || !known.Add(extension))
                continue;
            result.Add(template);
        }

        return result;
    }

    private static NewItemTemplate? Describe(string extension, string? progId, RegistryKey shellNew)
    {
        NewItemContent content;
        byte[]? data = null;
        string? file = null;
        if (shellNew.GetValue("FileName") is string templateName && templateName.Length > 0)
        {
            file = LocateTemplate(templateName);
            content = file is null ? NewItemContent.Empty : NewItemContent.CopyOfFile;
        }
        else if (shellNew.GetValue("Data") is { } raw)
        {
            data = raw switch
            {
                byte[] bytes => bytes,
                string text => Encoding.UTF8.GetBytes(text),
                _ => null,
            };
            content = NewItemContent.Bytes;
        }
        else if (Array.IndexOf(shellNew.GetValueNames(), "NullFile") >= 0)
        {
            content = NewItemContent.Empty;
        }
        else
        {
            return null; // "Command" registrations start a program instead of creating a file
        }

        return new NewItemTemplate(FriendlyName(extension, progId), extension, content, data, file);
    }

    /// <summary>"FileName" is absolute or relative to one of the folders Explorer looks in.</summary>
    private static string? LocateTemplate(string name)
    {
        var expanded = Environment.ExpandEnvironmentVariables(name);
        if (Path.IsPathRooted(expanded))
            return File.Exists(expanded) ? expanded : null;

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string[] folders =
        [
            Path.Combine(windows, "ShellNew"),
            Environment.GetFolderPath(Environment.SpecialFolder.Templates),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonTemplates),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Office", "root", "VFS", "Windows", "ShellNew"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Office", "root", "VFS", "Windows", "ShellNew"),
        ];
        foreach (var folder in folders)
        {
            if (folder.Length == 0)
                continue;
            var candidate = Path.Combine(folder, expanded);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>The type name Explorer shows ("Microsoft Word Document"), in the user's language.</summary>
    private static unsafe string FriendlyName(string extension, string? progId)
    {
        const int capacity = 260;
        var buffer = stackalloc char[capacity];
        var length = (uint)capacity;
        if (AssocQueryStringW(0, FriendlyDocumentName, extension, null, buffer, &length) >= 0 && length > 1)
            return new string(buffer, 0, (int)length - 1);
        if (!string.IsNullOrEmpty(progId) && Registry.ClassesRoot.OpenSubKey(progId) is { } progKey)
        {
            using (progKey)
            {
                if (progKey.GetValue(null) is string { Length: > 0 } described)
                    return described;
            }
        }

        return extension[1..].ToUpperInvariant() + " File";
    }

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int AssocQueryStringW(uint flags, int kind, string association, string? extra, char* output, uint* length);
}

/// <summary>
/// Linux and macOS: every file in the user's Templates folder (XDG_TEMPLATES_DIR, as GNOME Files and Dolphin use it;
/// ~/Templates when the user-dirs file does not name one) becomes a "New" entry that copies it.
/// </summary>
public sealed class TemplateFolderNewItems(string homeDirectory) : CachedNewItemTemplates
{
    private const int Limit = 64;

    protected override List<NewItemTemplate> Scan()
    {
        var result = new List<NewItemTemplate>();
        var folder = TemplatesFolder();
        if (folder is null || !Directory.Exists(folder) || PathsEqual(folder, homeDirectory))
            return result;

        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        foreach (var file in Directory.EnumerateFiles(folder, "*", options))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith('.'))
                continue;
            result.Add(new NewItemTemplate(Path.GetFileNameWithoutExtension(name), Path.GetExtension(name), NewItemContent.CopyOfFile, TemplatePath: file));
            if (result.Count == Limit)
                break;
        }

        return result;
    }

    private string? TemplatesFolder()
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } custom ? custom : Path.Combine(homeDirectory, ".config");
        var userDirs = Path.Combine(config, "user-dirs.dirs");
        if (File.Exists(userDirs))
        {
            foreach (var line in File.ReadLines(userDirs))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("XDG_TEMPLATES_DIR=", StringComparison.Ordinal))
                    continue;
                var value = trimmed["XDG_TEMPLATES_DIR=".Length..].Trim('"');
                return value.StartsWith("$HOME", StringComparison.Ordinal) ? homeDirectory + value["$HOME".Length..] : value;
            }
        }

        return Path.Combine(homeDirectory, "Templates");
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.Ordinal);
}
