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

    /// <summary>A <see cref="Navigator.Core.Platform.SystemIconKey"/>; null (the default) costs nothing.</summary>
    public static readonly StyledProperty<string?> SystemIconProperty = AvaloniaProperty.Register<IconView, string?>(nameof(SystemIcon));

    static IconView()
    {
        AffectsRender<IconView>(KindProperty, ForegroundProperty, SystemIconProperty);
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

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
            return;

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
            context.DrawImage(bitmap, rect);
        }

        return true;
    }
}
