namespace Navigator.Platform.Icons;

/// <summary>
/// Icon lookup in a freedesktop.org icon theme (Icon Theme Specification): the theme, the themes it inherits from,
/// then "hicolor". Only the contexts a file manager needs are searched (mime types, places, devices, status), and only
/// unscaled directories: Navigator asks for pixel sizes and rasterizes itself. Results are file paths, best first.
/// Pure managed code, no I/O beyond the theme directories, so it is unit-testable with a fake theme on disk.
/// </summary>
internal sealed class FreedesktopIconTheme
{
    private static readonly string[] UsefulContexts = ["MimeTypes", "Places", "Devices", "Status"];
    private static readonly string[] Extensions = [".png", ".svg"];

    private readonly IReadOnlyList<string> _baseDirectories;
    private readonly List<ThemeData> _chain = [];

    private FreedesktopIconTheme(IReadOnlyList<string> baseDirectories) => _baseDirectories = baseDirectories;

    public string Name => _chain.Count > 0 ? _chain[0].Name : "hicolor";

    /// <summary>Loads <paramref name="themeName"/> with its inherited themes; "hicolor" always ends the chain.</summary>
    public static FreedesktopIconTheme Load(string themeName, IReadOnlyList<string> baseDirectories)
    {
        ArgumentNullException.ThrowIfNull(baseDirectories);
        var theme = new FreedesktopIconTheme(baseDirectories);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        theme.AddWithParents(string.IsNullOrWhiteSpace(themeName) ? "hicolor" : themeName.Trim(), seen, depth: 0);
        if (seen.Add("hicolor"))
            theme.AddTheme("hicolor");
        return theme;
    }

    /// <summary>Icon directories in lookup order: ~/.icons, $XDG_DATA_HOME/icons, each $XDG_DATA_DIRS/icons, pixmaps.</summary>
    public static IReadOnlyList<string> DefaultBaseDirectories(string home)
    {
        var bases = new List<string> { Path.Combine(home, ".icons") };
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        bases.Add(Path.Combine(string.IsNullOrEmpty(dataHome) ? Path.Combine(home, ".local", "share") : dataHome, "icons"));
        var dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        foreach (var directory in (string.IsNullOrEmpty(dataDirs) ? "/usr/local/share:/usr/share" : dataDirs).Split(':', StringSplitOptions.RemoveEmptyEntries))
            bases.Add(Path.Combine(directory, "icons"));
        bases.Add("/usr/share/pixmaps");
        return bases;
    }

    /// <summary>
    /// Image files for the first of <paramref name="names"/> the theme chain has, best size first. Several candidates
    /// are returned (up to <paramref name="limit"/>) so a file the image loader rejects (an SVG without the SVG loader)
    /// can fall back to the next. A name earlier in the list wins over a better-sized later name, per theme.
    /// </summary>
    public List<string> Find(IReadOnlyList<string> names, int pixelSize, int limit = 4)
    {
        ArgumentNullException.ThrowIfNull(names);
        var found = new List<string>(limit);
        foreach (var theme in _chain)
        {
            foreach (var name in names)
            {
                foreach (var directory in theme.Ranked(pixelSize))
                {
                    foreach (var extension in Extensions)
                    {
                        if (directory.IsScalable && extension != ".svg")
                            continue; // scalable directories hold vector images only
                        var file = Path.Combine(directory.FullPath, name + extension);
                        if (File.Exists(file))
                        {
                            found.Add(file);
                            if (found.Count >= limit)
                                return found;
                        }
                    }
                }

                if (found.Count > 0)
                    return found;
            }
        }

        // Last resort per the specification: unthemed images directly in the base directories (pixmaps).
        foreach (var name in names)
        {
            foreach (var root in _baseDirectories)
            {
                foreach (var extension in Extensions)
                {
                    var file = Path.Combine(root, name + extension);
                    if (File.Exists(file))
                        found.Add(file);
                }
            }

            if (found.Count > 0)
                break;
        }

        return found;
    }

    private void AddWithParents(string name, HashSet<string> seen, int depth)
    {
        if (depth > 8 || !seen.Add(name))
            return;
        var parents = AddTheme(name);
        foreach (var parent in parents)
            AddWithParents(parent, seen, depth + 1);
    }

    /// <summary>Reads the first index.theme found for the theme; returns its Inherits list.</summary>
    private List<string> AddTheme(string name)
    {
        foreach (var root in _baseDirectories)
        {
            var index = Path.Combine(root, name, "index.theme");
            if (!File.Exists(index))
                continue;
            IEnumerable<string> lines;
            try
            {
                lines = File.ReadAllLines(index);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var (directories, parents) = ParseIndex(lines);
            var located = new List<ThemeDirectory>();
            foreach (var directory in directories)
            {
                foreach (var themeRoot in _baseDirectories)
                {
                    var full = Path.Combine(themeRoot, name, directory.Name);
                    if (Directory.Exists(full))
                        located.Add(directory with { FullPath = full });
                }
            }

            _chain.Add(new ThemeData(name, located));
            return parents;
        }

        return [];
    }

    /// <summary>index.theme → the useful, unscaled directories and the inherited theme names.</summary>
    internal static (List<ThemeDirectory> Directories, List<string> Parents) ParseIndex(IEnumerable<string> lines)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        Dictionary<string, string>? current = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                current = [];
                sections[line[1..^1]] = current;
                continue;
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (current is not null && equals > 0)
                current[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }

        var parents = new List<string>();
        var directories = new List<ThemeDirectory>();
        if (!sections.TryGetValue("Icon Theme", out var header))
            return (directories, parents);
        if (header.TryGetValue("Inherits", out var inherits))
            parents.AddRange(inherits.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (!header.TryGetValue("Directories", out var listed))
            return (directories, parents);

        foreach (var name in listed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!sections.TryGetValue(name, out var keys) || !Number(keys, "Size", out var size))
                continue;
            if (Number(keys, "Scale", out var scale) && scale != 1)
                continue;
            if (keys.TryGetValue("Context", out var context) && Array.IndexOf(UsefulContexts, context) < 0)
                continue;

            var type = keys.GetValueOrDefault("Type", "Threshold");
            var threshold = Number(keys, "Threshold", out var t) ? t : 2;
            var min = Number(keys, "MinSize", out var lo) ? lo : size;
            var max = Number(keys, "MaxSize", out var hi) ? hi : size;
            var (low, high) = type switch
            {
                "Fixed" => (size, size),
                "Scalable" => (min, max),
                _ => (size - threshold, size + threshold),
            };
            directories.Add(new ThemeDirectory(name, string.Empty, size, low, high, type == "Scalable"));
        }

        return (directories, parents);
    }

    private static bool Number(Dictionary<string, string> keys, string key, out int value)
    {
        value = 0;
        return keys.TryGetValue(key, out var text) && int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    internal sealed record ThemeDirectory(string Name, string FullPath, int Size, int Low, int High, bool IsScalable)
    {
        /// <summary>0 = fits; then larger images (scaled down, sharp) before smaller ones (scaled up, blurry).</summary>
        public int Distance(int pixelSize)
        {
            if (pixelSize >= Low && pixelSize <= High)
                return 0;
            return Size >= pixelSize ? 1000 + (Size - pixelSize) : 100_000 + (pixelSize - Size);
        }
    }

    private sealed record ThemeData(string Name, List<ThemeDirectory> Directories)
    {
        public IEnumerable<ThemeDirectory> Ranked(int pixelSize) => Directories.OrderBy(d => d.Distance(pixelSize));
    }
}
