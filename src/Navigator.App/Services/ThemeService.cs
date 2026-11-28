using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Microsoft.Extensions.Logging;
using Navigator.App.Infrastructure;
using Navigator.Core.Settings;
using Navigator.Presentation.Settings;

namespace Navigator.App.Services;

/// <summary>
/// Applies appearance settings at runtime, without restarting: theme variant, accent color, density,
/// typography scale, motion level and the window backdrop. Everything is expressed as application resources
/// (design tokens) consumed through DynamicResource, so a change re-styles the whole UI in one pass.
/// High contrast is a resource dictionary merged over the tokens on top of the light or dark variant (chosen in
/// Settings, or followed from the OS when the theme is "Use system setting").
/// </summary>
internal sealed class ThemeService(SettingsService settings)
{
    /// <summary>Which high-contrast palette is on, if any.</summary>
    private enum Contrast
    {
        None,
        Dark,
        Light,
    }

    // Windows' high-contrast highlight colors ("Aquatic" / "Desert"): selection, focus and accent in those themes.
    private static readonly Color HighlightDark = Color.Parse("#1AEBFF");
    private static readonly Color HighlightLight = Color.Parse("#37006E");

    private Contrast _contrast;
    private IResourceProvider? _contrastResources;
    private IResourceProvider? _contrastDarkCache;
    private IResourceProvider? _contrastLightCache;

    /// <summary>A high-contrast palette is applied: windows stay opaque, without backdrop, image or custom background.</summary>
    public bool IsHighContrast => _contrast != Contrast.None;

    private static readonly Easing Standard = new SplineEasing(0.1, 0.9, 0.2, 1.0);
    private static readonly Easing Decelerate = new SplineEasing(0.0, 0.0, 0.0, 1.0);

    public event EventHandler? BackdropChanged;

    public AppearanceSettings Appearance => settings.Current.Appearance;

    public AnimationLevel Motion => settings.Current.Appearance.Animations;

    public void Initialize()
    {
        ApplyAll();
        // Fluent's own controls follow the OS accent by themselves; our Accent.* tokens are computed here, so
        // they must be recomputed when the user changes the Windows/macOS/Linux accent while the app is running.
        // Only the accent is reapplied (a few brushes), not the whole appearance.
        if (Application.Current?.PlatformSettings is { } platform)
        {
            // The same event reports the OS high-contrast switch, which "Use system setting" follows.
            platform.ColorValuesChanged += (_, values) => Avalonia.Threading.Dispatcher.UIThread.Post(() => OnPlatformColorsChanged(values));
        }

        settings.Changed += (_, section) =>
        {
            if (section is "appearance" or "*")
                Avalonia.Threading.Dispatcher.UIThread.Post(ApplyAll);
            if (section is "advanced" or "*")
                ApplyLogLevel();
        };
    }

    private void OnPlatformColorsChanged(PlatformColorValues values)
    {
        var app = Application.Current!;
        var appearance = settings.Current.Appearance;
        if (ResolveContrast(appearance.Theme, values) != _contrast)
        {
            ApplyAll();
            return;
        }

        if (_contrast == Contrast.None && appearance.AccentColor is null)
            ApplyAccent(app, null, Contrast.None);
    }

    private void ApplyAll()
    {
        var app = Application.Current!;
        var appearance = settings.Current.Appearance;
        var contrast = ResolveContrast(appearance.Theme, app.PlatformSettings?.GetColorValues());

        app.RequestedThemeVariant = contrast switch
        {
            Contrast.Dark => ThemeVariant.Dark,
            Contrast.Light => ThemeVariant.Light,
            _ => appearance.Theme switch
            {
                ThemeMode.Light => ThemeVariant.Light,
                ThemeMode.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            },
        };

        ApplyContrast(app, contrast);
        ApplyAccent(app, appearance.AccentColor, contrast);
        ApplyDensity(app, appearance.Density, appearance.FontScale);
        ApplyFontFamily(app, appearance.FontFamily);
        ApplyMotion(app, appearance.Animations);
        ApplyLogLevel();

        // High contrast keeps the window opaque: a custom tint or see-through backdrop would lower the contrast.
        app.Resources["Window.CustomBackground"] = !IsHighContrast && appearance.BackgroundColor is { } background && Color.TryParse(background, out var color)
            ? new SolidColorBrush(color)
            : null;
        app.Resources["Window.TintOpacity"] = IsHighContrast ? 1.0 : 1.0 - (appearance.Transparency * 0.6);
        BackdropChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyLogLevel()
        => LogLevelSwitch.Minimum = settings.Current.Advanced.LogLevel switch
        {
            LogVerbosity.Error => LogLevel.Error,
            LogVerbosity.Warning => LogLevel.Warning,
            LogVerbosity.Debug => LogLevel.Debug,
            _ => LogLevel.Information,
        };

    private static Contrast ResolveContrast(ThemeMode theme, PlatformColorValues? system) => theme switch
    {
        ThemeMode.HighContrastDark => Contrast.Dark,
        ThemeMode.HighContrastLight => Contrast.Light,
        ThemeMode.System when system is { ContrastPreference: ColorContrastPreference.High } =>
            system.ThemeVariant == PlatformThemeVariant.Dark ? Contrast.Dark : Contrast.Light,
        _ => Contrast.None,
    };

    /// <summary>
    /// Swaps the high-contrast dictionary in or out of the application resources. Each palette is loaded once, on first
    /// use, and kept; an unchanged setting touches nothing, so other appearance changes never re-resolve every brush.
    /// </summary>
    private void ApplyContrast(Application app, Contrast contrast)
    {
        if (contrast == _contrast)
            return;

        var merged = app.Resources.MergedDictionaries;
        if (_contrastResources is not null)
            merged.Remove(_contrastResources);
        _contrastResources = contrast switch
        {
            Contrast.Dark => _contrastDarkCache ??= LoadPalette("HighContrastDark"),
            Contrast.Light => _contrastLightCache ??= LoadPalette("HighContrastLight"),
            _ => null,
        };
        if (_contrastResources is not null)
            merged.Add(_contrastResources); // last merged dictionary wins over Tokens.axaml
        _contrast = contrast;
    }

    private static IResourceProvider LoadPalette(string name)
        => (IResourceProvider)AvaloniaXamlLoader.Load(new Uri($"avares://Navigator/Theme/{name}.axaml"));

    private static (Color Color, Contrast Contrast)? _appliedAccent;

    private static void ApplyAccent(Application app, string? accent, Contrast contrast)
    {
        string[] keys = ["SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3", "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3"];
        Color color;
        if (contrast != Contrast.None)
        {
            // High contrast ignores the chosen and the OS accent: its highlight color is part of the palette.
            color = contrast == Contrast.Dark ? HighlightDark : HighlightLight;
            SetAccentShades(app, color);
        }
        else if (accent is null || !Color.TryParse(accent, out color))
        {
            // Follow the operating system accent (Fluent reads it from the platform).
            var system = app.PlatformSettings?.GetColorValues().AccentColor1;
            foreach (var key in keys)
                app.Resources.Remove(key);
            color = system ?? Color.Parse("#0078D4");
        }
        else
        {
            SetAccentShades(app, color);
        }

        // Settings changes and OS color events (which also fire on light/dark switches) often leave the accent
        // as it was: skip replacing the brushes then, so no DynamicResource consumer is re-resolved for nothing.
        if (_appliedAccent == (color, contrast))
            return;
        _appliedAccent = (color, contrast);

        // High contrast: stronger fills and an opaque selection outline; the alphas keep row text at 7:1 or more.
        var highContrast = contrast != Contrast.None;

        var darkText = Luminance(color) > 0.55;
        var foreground = new SolidColorBrush(darkText ? Colors.Black : Colors.White);
        app.Resources["Accent.Brush"] = new SolidColorBrush(color);

        // Filled accent buttons: hover and pressed shift the fill away from the label color (darker under white text,
        // lighter under black text), so the label never loses contrast as Fluent's lighter accent shades made it do.
        app.Resources["Accent.HoverBrush"] = new SolidColorBrush(darkText ? Mix(color, Colors.White, 0.2) : Mix(color, Colors.Black, 0.12));
        app.Resources["Accent.PressedBrush"] = new SolidColorBrush(darkText ? Mix(color, Colors.White, 0.35) : Mix(color, Colors.Black, 0.22));
        // Alpha goes into the color, not into Opacity: BrushTransition interpolates both independently, which makes
        // a fade from a transparent brush overshoot (flash) when the target carries its translucency in Opacity.
        app.Resources["Accent.SubtleBrush"] = new SolidColorBrush(WithAlpha(color, highContrast ? 0.24 : 0.16));
        app.Resources["Accent.SelectionBrush"] = new SolidColorBrush(WithAlpha(color, highContrast ? 0.32 : 0.24));
        app.Resources["Accent.SelectionHoverBrush"] = new SolidColorBrush(WithAlpha(color, highContrast ? 0.40 : 0.30));
        app.Resources["Accent.SelectionBorderBrush"] = new SolidColorBrush(highContrast ? color : WithAlpha(color, 0.72));
        app.Resources["Accent.ForegroundBrush"] = foreground;

        // Text selection (every TextBox: omnibar, search, rename, settings) and focused text-box borders use the
        // same accent as the rest of the UI. The selected text gets a contrasting color, so a light accent such
        // as yellow stays readable.
        app.Resources["TextControlSelectionHighlightColor"] = new SolidColorBrush(color);
        app.Resources["TextControlSelectionForegroundBrush"] = foreground;
        app.Resources["TextControlBorderBrushFocused"] = new SolidColorBrush(color);
    }

    private static void SetAccentShades(Application app, Color color)
    {
        app.Resources["SystemAccentColor"] = color;
        app.Resources["SystemAccentColorLight1"] = Mix(color, Colors.White, 0.2);
        app.Resources["SystemAccentColorLight2"] = Mix(color, Colors.White, 0.4);
        app.Resources["SystemAccentColorLight3"] = Mix(color, Colors.White, 0.6);
        app.Resources["SystemAccentColorDark1"] = Mix(color, Colors.Black, 0.2);
        app.Resources["SystemAccentColorDark2"] = Mix(color, Colors.Black, 0.4);
        app.Resources["SystemAccentColorDark3"] = Mix(color, Colors.Black, 0.6);
    }

    /// <summary>A custom family overrides the token; removing the override restores the cross-platform fallback list.</summary>
    private static void ApplyFontFamily(Application app, string? family)
    {
        if (string.IsNullOrWhiteSpace(family))
            app.Resources.Remove("Font.Family");
        else
            app.Resources["Font.Family"] = new FontFamily(family);
    }

    private static void ApplyDensity(Application app, InterfaceDensity density, double fontScale)
    {
        var (row, control, tab) = density switch
        {
            InterfaceDensity.Compact => (26.0, 28.0, 32.0),
            InterfaceDensity.Comfortable => (40.0, 36.0, 40.0),
            _ => (32.0, 32.0, 36.0),
        };

        app.Resources["Size.Row"] = row;
        app.Resources["Size.Control"] = control;
        app.Resources["Size.Tab"] = tab;
        app.Resources["Font.Body"] = Math.Round(14 * fontScale, 1);
        app.Resources["Font.Caption"] = Math.Round(12 * fontScale, 1);
        app.Resources["Font.Subtitle"] = Math.Round(16 * fontScale, 1);
        app.Resources["Font.Title"] = Math.Round(20 * fontScale, 1);
        app.Resources["Font.Display"] = Math.Round(28 * fontScale, 1);
    }

    /// <summary>
    /// Transitions are shared resources: Off swaps in empty collections, Reduced keeps only short opacity fades
    /// (no movement) — the accessibility "reduce motion" contract. Only compositor-friendly properties
    /// (opacity, transforms, brushes) are ever animated, never layout sizes.
    /// </summary>
    private static void ApplyMotion(Application app, AnimationLevel level)
    {
        var fast = TimeSpan.FromMilliseconds(level == AnimationLevel.Reduced ? 80 : 120);
        var normal = TimeSpan.FromMilliseconds(level == AnimationLevel.Reduced ? 100 : 187);
        var slow = TimeSpan.FromMilliseconds(level == AnimationLevel.Reduced ? 120 : 250);
        var enabled = level != AnimationLevel.Off;
        var movement = level == AnimationLevel.Full;

        app.Resources["Motion.Enabled"] = enabled;
        app.Resources["Motion.Movement"] = movement;
        app.Resources["Motion.Duration.Fast"] = enabled ? fast : TimeSpan.Zero;
        app.Resources["Motion.Duration.Normal"] = enabled ? normal : TimeSpan.Zero;
        app.Resources["Motion.Duration.Slow"] = enabled ? slow : TimeSpan.Zero;

        // Hover/selection fades: background and border recolor together (Border.BackgroundProperty is shared with
        // ContentPresenter, so the same collection also serves button templates). Selection borders follow the fill.
        app.Resources["Motion.Hover"] = enabled
            ? new Transitions
            {
                new BrushTransition { Property = Avalonia.Controls.Border.BackgroundProperty, Duration = fast, Easing = Standard },
                new BrushTransition { Property = Avalonia.Controls.Border.BorderBrushProperty, Duration = fast, Easing = Standard },
            }
            : new Transitions();
        // Progress values arrive at 10 Hz; easing between them turns the steps into continuous motion.
        app.Resources["Motion.Progress"] = enabled
            ? new Transitions
            {
                new DoubleTransition { Property = Avalonia.Controls.Primitives.RangeBase.ValueProperty, Duration = slow, Easing = Decelerate },
                new DoubleTransition { Property = Navigator.App.Controls.ProgressRing.ValueProperty, Duration = slow, Easing = Decelerate },
            }
            : new Transitions();
        app.Resources["Motion.Fade"] = enabled
            ? new Transitions { new DoubleTransition { Property = Visual.OpacityProperty, Duration = normal, Easing = Decelerate } }
            : new Transitions();
        app.Resources["Motion.FadeAndMove"] = movement
            ? new Transitions
            {
                new DoubleTransition { Property = Visual.OpacityProperty, Duration = normal, Easing = Decelerate },
                new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = slow, Easing = Decelerate },
            }
            : (Transitions)app.Resources["Motion.Fade"]!;
        app.Resources["Motion.Indicator"] = movement
            ? new Transitions { new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = normal, Easing = Standard } }
            : new Transitions();
    }

    private static Color WithAlpha(Color color, double alpha)
        => Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), color.R, color.G, color.B);

    private static Color Mix(Color a, Color b, double amount)
        => Color.FromArgb(a.A, (byte)(a.R + ((b.R - a.R) * amount)), (byte)(a.G + ((b.G - a.G) * amount)), (byte)(a.B + ((b.B - a.B) * amount)));

    private static double Luminance(Color c) => ((0.299 * c.R) + (0.587 * c.G) + (0.114 * c.B)) / 255.0;
}
