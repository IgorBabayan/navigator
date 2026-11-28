using System.Runtime.Versioning;
using System.Xml;
using Microsoft.Win32;

namespace Navigator.Plugin.ModernMenu;

/// <summary>
/// One command a packaged app registered for File Explorer's context menu: an IExplorerCommand class for an item type
/// ("*" every file, ".txt", "Directory", "Directory\Background").
/// </summary>
internal sealed record PackagedVerb(string Package, string ItemType, string VerbId, Guid Clsid);

/// <summary>
/// Finds the <c>windows.fileExplorerContextMenus</c> extensions of the packages installed for the user. The package list
/// comes from the AppModel repository in the registry; manifests are read only when that list changed since the last
/// scan, and only manifests that mention the extension are parsed.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class PackagedVerbCatalog
{
    private const string RepositoryKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    /// <summary>Classes Navigator covers itself: "Open in Terminal" of Windows Terminal and Windows Terminal Preview (Navigator has "Open in terminal").</summary>
    private static readonly HashSet<Guid> Duplicates = [new("9F156763-7844-4DC4-B2B1-901F640F5155"), new("02DB545A-3E20-46DE-83A5-1329B1E88B6B")];

    private readonly Lock _gate = new();
    private IReadOnlyList<PackagedVerb> _verbs = [];
    private string? _fingerprint;

    /// <summary>Every registered verb (rescanned when packages were installed, updated or removed).</summary>
    public IReadOnlyList<PackagedVerb> GetVerbs()
    {
        lock (_gate)
        {
            var packages = ReadPackages();
            var fingerprint = string.Join('|', packages.Select(p => p.FullName));
            if (fingerprint == _fingerprint)
                return _verbs;

            var verbs = new List<PackagedVerb>();
            foreach (var (fullName, root) in packages)
            {
                var manifest = Path.Combine(root, "AppxManifest.xml");
                try
                {
                    if (!File.Exists(manifest))
                        continue;
                    var text = File.ReadAllText(manifest);
                    if (!text.Contains(PackagedVerbManifest.ExtensionCategory, StringComparison.OrdinalIgnoreCase))
                        continue;
                    foreach (var verb in PackagedVerbManifest.Parse(fullName, text))
                    {
                        if (!Duplicates.Contains(verb.Clsid))
                            verbs.Add(verb);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
                {
                    // An unreadable manifest only loses that package's commands.
                }
            }

            _verbs = verbs;
            _fingerprint = fingerprint;
            return verbs;
        }
    }

    private static List<(string FullName, string Root)> ReadPackages()
    {
        var packages = new List<(string, string)>();
        try
        {
            using var repository = Registry.CurrentUser.OpenSubKey(RepositoryKey);
            if (repository is null)
                return packages;
            foreach (var name in repository.GetSubKeyNames())
            {
                using var package = repository.OpenSubKey(name);
                if (package?.GetValue("PackageRootFolder") is string { Length: > 0 } root)
                    packages.Add((name, root));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // No repository access: no packaged commands.
        }

        packages.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return packages;
    }
}

/// <summary>Reading verbs from a manifest and matching them to a selection: plain string work, testable anywhere.</summary>
internal static class PackagedVerbManifest
{
    public const string ExtensionCategory = "windows.fileExplorerContextMenus";

    /// <summary>
    /// The verbs in one AppxManifest.xml: every <c>Verb</c> (Id, Clsid) inside an <c>ItemType</c> (Type) inside an
    /// extension of category <c>windows.fileExplorerContextMenus</c>, whatever the desktop4/desktop5/desktop10 prefix.
    /// </summary>
    public static List<PackagedVerb> Parse(string package, string manifest)
    {
        var verbs = new List<PackagedVerb>();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreWhitespace = true };
        using var reader = XmlReader.Create(new StringReader(manifest), settings);
        var extensionDepth = -1;
        string? itemType = null;
        var itemTypeDepth = -1;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.Depth == itemTypeDepth)
                {
                    itemType = null;
                    itemTypeDepth = -1;
                }

                if (reader.Depth == extensionDepth)
                    extensionDepth = -1;
                continue;
            }

            if (reader.NodeType != XmlNodeType.Element)
                continue;
            switch (reader.LocalName)
            {
                case "Extension" when string.Equals(reader.GetAttribute("Category"), ExtensionCategory, StringComparison.OrdinalIgnoreCase):
                    if (!reader.IsEmptyElement)
                        extensionDepth = reader.Depth;
                    break;
                case "ItemType" when extensionDepth >= 0:
                    itemType = reader.GetAttribute("Type");
                    itemTypeDepth = reader.IsEmptyElement ? -1 : reader.Depth;
                    break;
                case "Verb" when extensionDepth >= 0 && itemType is not null:
                    if (reader.GetAttribute("Id") is { Length: > 0 } id && Guid.TryParse(reader.GetAttribute("Clsid"), out var clsid))
                        verbs.Add(new PackagedVerb(package, itemType, id, clsid));
                    break;
            }
        }

        return verbs;
    }

    /// <summary>
    /// The verbs that apply to every one of <paramref name="targets"/> (or to the folder background), each class once.
    /// Files match "*" and their extension; folders and drives match "Directory" (and "Folder", "Drive").
    /// </summary>
    public static List<PackagedVerb> Matching(IReadOnlyList<PackagedVerb> verbs, IReadOnlyList<(string Path, bool IsDirectory)> targets, bool background)
    {
        var matching = new List<PackagedVerb>();
        if (targets.Count == 0)
            return matching;
        var seen = new HashSet<Guid>();
        foreach (var verb in verbs)
        {
            if (seen.Contains(verb.Clsid))
                continue;
            var applies = true;
            foreach (var (path, isDirectory) in targets)
            {
                if (!AppliesTo(verb.ItemType, path, isDirectory, background))
                {
                    applies = false;
                    break;
                }
            }

            if (applies && seen.Add(verb.Clsid))
                matching.Add(verb);
        }

        return matching;
    }

    private static bool AppliesTo(string itemType, string path, bool isDirectory, bool background)
    {
        if (background)
            return string.Equals(itemType, @"Directory\Background", StringComparison.OrdinalIgnoreCase);
        if (isDirectory)
        {
            return itemType.Equals("Directory", StringComparison.OrdinalIgnoreCase) || itemType.Equals("Folder", StringComparison.OrdinalIgnoreCase)
                   || (itemType.Equals("Drive", StringComparison.OrdinalIgnoreCase) && Path.GetPathRoot(path) is { } root && string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase));
        }

        if (itemType == "*")
            return true;
        return itemType.StartsWith('.') && string.Equals(itemType, Path.GetExtension(path), StringComparison.OrdinalIgnoreCase);
    }
}
