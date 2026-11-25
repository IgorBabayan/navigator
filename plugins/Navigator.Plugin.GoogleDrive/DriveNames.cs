namespace Navigator.Plugin.GoogleDrive;

/// <summary>
/// Maps Drive's names to path segments and back. Drive allows what a path cannot represent, so:
/// <list type="bullet">
/// <item>"/" and "\" in a name are shown as "∕" (U+2215) and "⧵" (U+29F5) and mapped back when a name is sent;</item>
/// <item>Google Docs, Sheets, Slides and Drawings get the extension of the format they download as ("Budget.xlsx");</item>
/// <item>items with the same name in one folder get " (2)", " (3)"… in creation order, so every item has its own path.</item>
/// </list>
/// </summary>
internal static class DriveNames
{
    public const string FolderMime = "application/vnd.google-apps.folder";
    public const string ShortcutMime = "application/vnd.google-apps.shortcut";
    private const string GoogleAppsPrefix = "application/vnd.google-apps.";
    private const char SlashStandIn = '∕';
    private const char BackslashStandIn = '⧵';

    private static readonly Dictionary<string, (string Extension, string Mime)> Exports = new(StringComparer.Ordinal)
    {
        ["application/vnd.google-apps.document"] = (".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
        ["application/vnd.google-apps.spreadsheet"] = (".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
        ["application/vnd.google-apps.presentation"] = (".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation"),
        ["application/vnd.google-apps.drawing"] = (".pdf", "application/pdf"),
        ["application/vnd.google-apps.script"] = (".json", "application/vnd.google-apps.script+json"),
    };

    /// <summary>The download format of a Google Docs editor file, or null (regular files, folders).</summary>
    public static (string Extension, string Mime)? ExportFormat(string mimeType)
        => Exports.TryGetValue(mimeType, out var format) ? format : null;

    /// <summary>True for Drive-only items without file content or export (Forms, Sites, My Maps…).</summary>
    public static bool IsContentless(string mimeType)
        => mimeType.StartsWith(GoogleAppsPrefix, StringComparison.Ordinal) && mimeType != FolderMime && mimeType != ShortcutMime && !Exports.ContainsKey(mimeType);

    /// <summary>The path segment for one item (before duplicates are numbered).</summary>
    public static string ToSegment(string name, string effectiveMime)
    {
        var segment = name.Replace('/', SlashStandIn).Replace('\\', BackslashStandIn);
        if (segment.Length == 0 || segment is "." or "..")
            segment = "_" + segment;
        if (ExportFormat(effectiveMime) is { } format && !segment.EndsWith(format.Extension, StringComparison.OrdinalIgnoreCase))
            segment += format.Extension;
        return segment;
    }

    /// <summary>
    /// The Drive name for a path segment the user typed (new folder, upload, rename). For a Google Docs editor file the
    /// export extension is dropped again, so renaming "Budget.xlsx" to "Plan.xlsx" names the sheet "Plan".
    /// </summary>
    public static string ToDriveName(string segment, string? effectiveMime = null)
    {
        var name = segment.Replace(SlashStandIn, '/').Replace(BackslashStandIn, '\\');
        if (effectiveMime is not null && ExportFormat(effectiveMime) is { } format && name.Length > format.Extension.Length
            && name.EndsWith(format.Extension, StringComparison.OrdinalIgnoreCase))
            name = name[..^format.Extension.Length];
        return name;
    }

    /// <summary>
    /// Unique segments for the items of one folder, in the order given (Drive's creation order): the first item keeps its
    /// name, later ones with the same name get " (2)", " (3)"… before the extension.
    /// </summary>
    public static string[] AssignSegments(IReadOnlyList<(string Name, string EffectiveMime)> items)
    {
        var result = new string[items.Count];
        var taken = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var segment = ToSegment(items[i].Name, items[i].EffectiveMime);
            if (!taken.Add(segment))
            {
                var dot = segment.LastIndexOf('.');
                var (stem, extension) = dot > 0 ? (segment[..dot], segment[dot..]) : (segment, string.Empty);
                for (var n = 2; ; n++)
                {
                    var candidate = string.Concat(stem, " (", n.ToString(System.Globalization.CultureInfo.InvariantCulture), ")", extension);
                    if (taken.Add(candidate))
                    {
                        segment = candidate;
                        break;
                    }
                }
            }

            result[i] = segment;
        }

        return result;
    }

    /// <summary>A string literal for a Drive search query ('…' with \ and ' escaped).</summary>
    public static string QueryLiteral(string value) => "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";
}
