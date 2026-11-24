using System.Globalization;
using System.Text;

namespace Navigator.Platform.Launch;

/// <summary>A parsed .desktop application entry (freedesktop Desktop Entry Specification).</summary>
internal sealed record DesktopEntry(string Id, string Name, string Exec, string? Icon, bool Terminal, IReadOnlyList<string> MimeTypes, string FilePath);

/// <summary>
/// Application/MIME associations from the XDG data and config directories:
/// *.desktop files, mimeapps.list (defaults, added and removed associations) and the shared MIME glob database.
/// Built lazily on first use and cached; nothing is read at startup.
/// </summary>
internal sealed class LinuxDesktopEntries
{
    private readonly IReadOnlyList<string> _dataDirectories;
    private readonly IReadOnlyList<string> _configDirectories;
    private readonly Lazy<Index> _index;

    public LinuxDesktopEntries()
        : this(DataDirectories(), ConfigDirectories())
    {
    }

    internal LinuxDesktopEntries(IReadOnlyList<string> dataDirectories, IReadOnlyList<string> configDirectories)
    {
        _dataDirectories = dataDirectories;
        _configDirectories = configDirectories;
        _index = new Lazy<Index>(Build, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string GetMimeType(string path)
    {
        if (Directory.Exists(path))
            return "inode/directory";

        var name = Path.GetFileName(path).ToLowerInvariant();
        var globs = _index.Value.Globs;
        // Longest matching extension wins (".tar.gz" before ".gz").
        for (var dot = name.IndexOf('.', StringComparison.Ordinal); dot >= 0; dot = name.IndexOf('.', dot + 1))
        {
            if (globs.TryGetValue(name[dot..], out var mime))
                return mime;
        }

        return "application/octet-stream";
    }

    public IReadOnlyList<(DesktopEntry Entry, bool IsDefault)> GetHandlers(string path)
    {
        var index = _index.Value;
        var mime = GetMimeType(path);
        var result = new List<(DesktopEntry, bool)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        index.Defaults.TryGetValue(mime, out var defaults);
        foreach (var id in defaults ?? [])
        {
            if (index.Entries.TryGetValue(id, out var entry) && seen.Add(id))
                result.Add((entry, result.Count == 0));
        }

        index.Added.TryGetValue(mime, out var added);
        index.Removed.TryGetValue(mime, out var removed);
        var candidates = (added ?? []).Concat(index.Entries.Values.Where(e => e.MimeTypes.Contains(mime, StringComparer.OrdinalIgnoreCase)).Select(e => e.Id));
        foreach (var id in candidates)
        {
            if (removed?.Contains(id) == true || !index.Entries.TryGetValue(id, out var entry) || !seen.Add(id))
                continue;
            result.Add((entry, false));
        }

        return result;
    }

    public DesktopEntry? Find(string id) => _index.Value.Entries.GetValueOrDefault(id);

    /// <summary>
    /// Expands the Exec key for one file (field codes %f %F %u %U; deprecated/unused codes removed).
    /// Returns the program and its argument list; quoting follows the Desktop Entry Specification.
    /// </summary>
    internal static (string Program, List<string> Arguments)? ExpandExec(string exec, string filePath, string name)
    {
        var tokens = Tokenize(exec);
        if (tokens.Count == 0)
            return null;

        var arguments = new List<string>();
        var usedFile = false;
        foreach (var token in tokens.Skip(1))
        {
            switch (token)
            {
                case "%f" or "%F":
                    arguments.Add(filePath);
                    usedFile = true;
                    continue;
                case "%u" or "%U":
                    arguments.Add(new Uri(filePath).AbsoluteUri);
                    usedFile = true;
                    continue;
                case "%i" or "%k" or "%d" or "%D" or "%n" or "%N" or "%v" or "%m":
                    continue;
                case "%c":
                    arguments.Add(name);
                    continue;
            }

            arguments.Add(token.Replace("%%", "%", StringComparison.Ordinal));
        }

        if (!usedFile)
            arguments.Add(filePath);
        return (tokens[0], arguments);
    }

    internal static List<string> Tokenize(string exec)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        for (var i = 0; i < exec.Length; i++)
        {
            var c = exec[i];
            if (inQuotes)
            {
                if (c == '\\' && i + 1 < exec.Length && exec[i + 1] is '"' or '`' or '$' or '\\')
                    current.Append(exec[++i]);
                else if (c == '"')
                    inQuotes = false;
                else
                    current.Append(c);
            }
            else if (c == '"')
            {
                inQuotes = true;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (hasToken || current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (hasToken || current.Length > 0)
            tokens.Add(current.ToString());
        return tokens;
    }

    internal static DesktopEntry? Parse(string id, string filePath, IEnumerable<string> lines)
    {
        var inMain = false;
        string? name = null, localizedName = null, exec = null, icon = null, type = null;
        var terminal = false;
        var hidden = false;
        var mimeTypes = new List<string>();
        var language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            if (line[0] == '[')
            {
                inMain = line == "[Desktop Entry]";
                continue;
            }

            if (!inMain)
                continue;

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
                continue;
            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            switch (key)
            {
                case "Name": name = value; break;
                case "Exec": exec = value; break;
                case "Icon": icon = value; break;
                case "Type": type = value; break;
                case "Terminal": terminal = value == "true"; break;
                case "NoDisplay" or "Hidden": hidden |= value == "true"; break;
                case "MimeType": mimeTypes.AddRange(value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
                default:
                    if (key == $"Name[{language}]")
                        localizedName = value;
                    break;
            }
        }

        if (type != "Application" || exec is null || (name is null && localizedName is null))
            return null;

        // Hidden/NoDisplay entries are still valid handlers when explicitly listed as defaults; mark only fully hidden ones out.
        return hidden && mimeTypes.Count == 0 ? null : new DesktopEntry(id, localizedName ?? name!, exec, icon, terminal, mimeTypes, filePath);
    }

    private Index Build()
    {
        var entries = new Dictionary<string, DesktopEntry>(StringComparer.Ordinal);
        // Earlier directories take precedence ($XDG_DATA_HOME first).
        foreach (var dataDirectory in _dataDirectories)
        {
            var applications = Path.Combine(dataDirectory, "applications");
            if (!Directory.Exists(applications))
                continue;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(applications, "*.desktop", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                var id = Path.GetRelativePath(applications, file).Replace('/', '-');
                if (entries.ContainsKey(id))
                    continue;
                try
                {
                    if (Parse(id, file, File.ReadLines(file)) is { } entry)
                        entries[id] = entry;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        var defaults = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var added = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var removed = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var mimeAppsFiles = _configDirectories.Select(d => Path.Combine(d, "mimeapps.list"))
            .Concat(_dataDirectories.Select(d => Path.Combine(d, "applications", "mimeapps.list")));
        foreach (var file in mimeAppsFiles.Where(File.Exists))
            ReadMimeApps(file, defaults, added, removed);

        var globs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dataDirectory in _dataDirectories.Reverse())
        {
            var globsFile = Path.Combine(dataDirectory, "mime", "globs2");
            if (File.Exists(globsFile))
                ReadGlobs(globsFile, globs);
        }

        return new Index(entries, defaults, added, removed, globs);
    }

    internal static void ReadMimeApps(string file, Dictionary<string, List<string>> defaults, Dictionary<string, List<string>> added, Dictionary<string, HashSet<string>> removed)
    {
        string? section = null;
        foreach (var raw in File.ReadLines(file))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            if (line[0] == '[')
            {
                section = line;
                continue;
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
                continue;
            var mime = line[..equals].Trim();
            var ids = line[(equals + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            switch (section)
            {
                case "[Default Applications]":
                    // First file wins for defaults (user config overrides system).
                    if (!defaults.ContainsKey(mime))
                        defaults[mime] = [.. ids];
                    break;
                case "[Added Associations]":
                    if (!added.TryGetValue(mime, out var list))
                        added[mime] = list = [];
                    list.AddRange(ids.Where(id => !list.Contains(id)));
                    break;
                case "[Removed Associations]":
                    if (!removed.TryGetValue(mime, out var set))
                        removed[mime] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.UnionWith(ids);
                    break;
            }
        }
    }

    /// <summary>globs2 lines: "weight:mime/type:*.ext". Only simple "*.ext" globs are used.</summary>
    internal static void ReadGlobs(string file, Dictionary<string, string> globs)
    {
        foreach (var line in File.ReadLines(file))
        {
            if (line.Length == 0 || line[0] == '#')
                continue;
            var parts = line.Split(':');
            if (parts.Length < 3 || !parts[2].StartsWith("*.", StringComparison.Ordinal) || parts[2].AsSpan(1).IndexOfAny('*', '?', '[') >= 0)
                continue;
            globs.TryAdd(parts[2][1..].ToLowerInvariant(), parts[1]);
        }
    }

    private static IReadOnlyList<string> DataDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d && Path.IsPathRooted(d) ? d : Path.Combine(home, ".local", "share");
        var dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } dirs ? dirs : "/usr/local/share:/usr/share";
        return [dataHome, .. dataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries).Where(Path.IsPathRooted)];
    }

    private static IReadOnlyList<string> ConfigDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c && Path.IsPathRooted(c) ? c : Path.Combine(home, ".config");
        var configDirs = Environment.GetEnvironmentVariable("XDG_CONFIG_DIRS") is { Length: > 0 } dirs ? dirs : "/etc/xdg";
        return [configHome, .. configDirs.Split(':', StringSplitOptions.RemoveEmptyEntries).Where(Path.IsPathRooted)];
    }

    private sealed record Index(
        Dictionary<string, DesktopEntry> Entries,
        Dictionary<string, List<string>> Defaults,
        Dictionary<string, List<string>> Added,
        Dictionary<string, HashSet<string>> Removed,
        Dictionary<string, string> Globs);
}
