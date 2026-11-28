using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Navigator.App.Controls;

/// <summary>
/// Draws a catalog icon directly in Render: no child visuals, no template, no resource lookups per instance —
/// the cheapest possible icon for virtualized lists. Material glyphs: colored layers keep their color, uncolored
/// layers take Foreground.
/// When <see cref="SystemIcon"/> is set and the OS can draw it (Windows shell icons), that bitmap is drawn instead;
/// the catalog icon (<see cref="Kind"/>) shows while it loads and wherever the OS has no such icon.
/// </summary>
public sealed class IconView : Control
{
    public static readonly StyledProperty<string?> KindProperty = AvaloniaProperty.Register<IconView, string?>(nameof(Kind));

    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<IconView>();

    /// <summary>
    /// A toolbar command ("Cut", "Copy", "Paste", "Rename", "Delete"…) drawn the way the OS draws it: Windows' icon font
    /// (the glyphs File Explorer's command bar uses), the icon theme's action icon on Linux, an SF Symbol on macOS.
    /// <see cref="Kind"/> is drawn where the OS has none.
    /// </summary>
    public static readonly StyledProperty<string?> OsGlyphProperty = AvaloniaProperty.Register<IconView, string?>(nameof(OsGlyph));

    /// <summary>A <see cref="Navigator.Core.Platform.SystemIconKey"/>; null (the default) costs nothing.</summary>
    public static readonly StyledProperty<string?> SystemIconProperty = AvaloniaProperty.Register<IconView, string?>(nameof(SystemIcon));

    /// <summary>
    /// A small catalog icon drawn over the bottom-left corner (plugin badges: sync state, Git state); null (the default)
    /// costs nothing. Drawn in this control's own Render, so badges add no visuals to the rows.
    /// </summary>
    public static readonly StyledProperty<string?> BadgeProperty = AvaloniaProperty.Register<IconView, string?>(nameof(Badge));

    /// <summary>Color of <see cref="Badge"/> as "#RRGGBB"; null draws it in <see cref="Foreground"/>.</summary>
    public static readonly StyledProperty<string?> BadgeTintProperty = AvaloniaProperty.Register<IconView, string?>(nameof(BadgeTint));

    /// <summary>The disc behind a badge, so it stays legible on any icon.</summary>
    private static readonly IBrush BadgeBackdrop = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IBrush> BadgeBrushes = new(StringComparer.OrdinalIgnoreCase);

    static IconView()
    {
        AffectsRender<IconView>(KindProperty, ForegroundProperty, SystemIconProperty, OsGlyphProperty, BadgeProperty, BadgeTintProperty);
        WidthProperty.OverrideDefaultValue<IconView>(16);
        HeightProperty.OverrideDefaultValue<IconView>(16);
    }

    public string? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public string? SystemIcon
    {
        get => GetValue(SystemIconProperty);
        set => SetValue(SystemIconProperty, value);
    }

    public string? OsGlyph
    {
        get => GetValue(OsGlyphProperty);
        set => SetValue(OsGlyphProperty, value);
    }

    public string? Badge
    {
        get => GetValue(BadgeProperty);
        set => SetValue(BadgeProperty, value);
    }

    public string? BadgeTint
    {
        get => GetValue(BadgeTintProperty);
        set => SetValue(BadgeTintProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
            return;
        RenderIcon(context, size);
        if (Badge is { } badge)
            RenderBadge(context, badge, size);
    }

    /// <summary>
    /// The badge, on a light disc in the bottom-left corner: a little over half of a row icon (legible at 16–20 px), a
    /// third of a tile icon (at most 32 px).
    /// </summary>
    private void RenderBadge(DrawingContext context, string key, double size)
    {
        if (IconCatalog.Get(key) is not { } icon)
            return;
        var badge = size <= 24 ? Math.Max(8, Math.Round(size * 0.56)) : Math.Round(Math.Min(size * 0.35, 32));
        var left = (Bounds.Width - size) / 2;
        var top = ((Bounds.Height - size) / 2) + size - badge;
        var center = new Point(left + (badge / 2), top + (badge / 2));
        context.DrawEllipse(BadgeBackdrop, null, center, (badge / 2) + 0.5, (badge / 2) + 0.5);

        var tint = BadgeTint is { Length: > 0 } hex
            ? BadgeBrushes.GetOrAdd(hex, static text => Color.TryParse(text, out var color) ? new Avalonia.Media.Immutable.ImmutableSolidColorBrush(color) : Brushes.Gray)
            : Foreground ?? Brushes.Gray;
        var scale = badge / 24.0;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(left, top)))
        {
            foreach (var (geometry, layerTint) in icon.Layers)
                context.DrawGeometry(layerTint ?? tint, null, geometry);
        }
    }

    private void RenderIcon(DrawingContext context, double size)
    {
        if (OsGlyph is { } command)
        {
            if (OsGlyphs.Get(command) is { } shape)
            {
                RenderFontGlyph(context, shape, size);
                return;
            }

            if (SystemIcons.IsSupported && TryRenderSystemIcon(context, OsGlyphs.ActionKey(command), size))
                return;
        }

        if (SystemIcon is { } systemIcon && TryRenderSystemIcon(context, systemIcon, size))
            return;

        var icon = IconCatalog.Get(Kind);
        if (icon is null)
            return;

        var scale = size / 24.0;
        var offset = new Vector((Bounds.Width - size) / 2, (Bounds.Height - size) / 2);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset)))
        {
            var foreground = Foreground ?? Brushes.Gray;
            foreach (var (geometry, tint) in icon.Layers)
                context.DrawGeometry(tint ?? foreground, null, geometry);
        }
    }

    private bool TryRenderSystemIcon(DrawingContext context, string key, double size)
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        if (!SystemIcons.TryGet(key, (int)Math.Ceiling(size * scaling), this, out var bitmap) || bitmap is null)
            return false;

        var rect = new Rect((Bounds.Width - size) / 2, (Bounds.Height - size) / 2, size, size);
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality }))
        {
            if (SystemIcons.MaskOf(bitmap) is { } mask)
            {
                // A template icon takes the text color, like the Material glyphs around it.
                using (context.PushOpacityMask(mask, rect))
                    context.DrawRectangle(Foreground ?? Brushes.Gray, null, rect);
            }
            else
            {
                context.DrawImage(bitmap, rect);
            }
        }

        return true;
    }

    /// <summary>A glyph of the OS icon font, at the font size equal to the icon size, centred.</summary>
    private void RenderFontGlyph(DrawingContext context, Geometry shape, double size)
    {
        var scale = size / OsGlyphs.EmSize;
        var bounds = shape.Bounds;
        var offset = new Vector(
            (Bounds.Width / 2) - ((bounds.X + (bounds.Width / 2)) * scale),
            (Bounds.Height / 2) - ((bounds.Y + (bounds.Height / 2)) * scale));
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset)))
            context.DrawGeometry(Foreground ?? Brushes.Gray, null, shape);
    }
}
