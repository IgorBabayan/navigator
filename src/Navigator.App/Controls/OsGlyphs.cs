using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Navigator.Core.Platform;

namespace Navigator.App.Controls;

/// <summary>
/// Toolbar command icons from the operating system. On Windows they are glyphs of the system icon font — Segoe Fluent
/// Icons on Windows 11, Segoe MDL2 Assets on Windows 10 — the font File Explorer's command bar draws with; each glyph is
/// turned into a geometry once and shared, so drawing one costs the same as a Material icon. Linux and macOS pictures
/// come through <see cref="SystemIcons"/> with an "action:" key (theme action icons, SF Symbols).
/// </summary>
internal static class OsGlyphs
{
    /// <summary>Font size the geometries are built at; <see cref="IconView"/> scales them to its own size.</summary>
    public const double EmSize = 24;

    private static readonly Lazy<Typeface?> IconFont = new(FindIconFont);
    private static readonly Dictionary<string, Geometry?> Shapes = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> ActionKeys = new(StringComparer.Ordinal);

    /// <summary>The Windows font glyph for a command, or null (other systems, no icon font, unknown command). UI thread.</summary>
    public static Geometry? Get(string command)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        if (Shapes.TryGetValue(command, out var known))
            return known;

        Geometry? shape = null;
        if (CodePoint(command) is { } code && IconFont.Value is { } font)
        {
            try
            {
                var text = new FormattedText(code.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, font, EmSize, Brushes.Black);
                shape = text.BuildGeometry(new Point(0, 0));
            }
            catch (Exception unusable) when (unusable is InvalidOperationException or ArgumentException or NotSupportedException)
            {
                shape = null; // the font could not shape the glyph: the Material icon is drawn instead
            }
        }

        Shapes[command] = shape;
        return shape;
    }

    /// <summary>The <see cref="SystemIconKey.Action"/> key of a command, allocated once.</summary>
    public static string ActionKey(string command)
    {
        if (!ActionKeys.TryGetValue(command, out var key))
            ActionKeys[command] = key = SystemIconKey.Action(command);
        return key;
    }

    /// <summary>Code points shared by Segoe Fluent Icons and Segoe MDL2 Assets.</summary>
    private static char? CodePoint(string command) => command switch
    {
        "New" => '\uECC8',        // AddTo: the circled plus of Explorer's "New"
        "Cut" => '\uE8C6',
        "Copy" => '\uE8C8',
        "Paste" => '\uE77F',
        "Rename" => '\uE8AC',
        "Share" => '\uE72D',
        "Delete" => '\uE74D',
        "Sort" => '\uE8CB',
        "Group" => '\uF168',
        "Layout" => '\uE8FD',     // BulletedList: Explorer's "View"
        "More" => '\uE712',
        "Properties" => '\uE946', // Info
        "Download" => '\uE896',
        "Upload" => '\uE898',
        _ => null,
    };

    private static Typeface? FindIconFont()
    {
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in FontManager.Current.SystemFonts)
            installed.Add(family.Name);
        foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
        {
            if (installed.Contains(name))
                return new Typeface(new FontFamily(name));
        }

        return null;
    }
}
