namespace Navigator.Core.Customization;

/// <summary>"Run this program in compatibility mode for" (the Windows version the program is told it runs on).</summary>
public enum CompatibilityMode
{
    None,
    Windows95,
    Windows98,
    WindowsXpSp2,
    WindowsXpSp3,
    WindowsVista,
    WindowsVistaSp1,
    WindowsVistaSp2,
    Windows7,
    Windows8,
}

public enum ReducedColorMode
{
    None,
    Colors8Bit,
    Colors16Bit,
}

/// <summary>"Override high DPI scaling behavior. Scaling performed by:".</summary>
public enum DpiScalingOverride
{
    None,
    Application,
    System,
    SystemEnhanced,
}

/// <summary>The options of Explorer's Compatibility tab that are stored as AppCompat layers.</summary>
public sealed record CompatibilitySettings(
    CompatibilityMode Mode = CompatibilityMode.None,
    ReducedColorMode ReducedColor = ReducedColorMode.None,
    bool Resolution640x480 = false,
    bool DisableFullscreenOptimizations = false,
    bool RunAsAdministrator = false,
    DpiScalingOverride DpiOverride = DpiScalingOverride.None)
{
    public static CompatibilitySettings Default { get; } = new();

    public bool IsDefault => this == Default;
}

/// <summary>
/// Parses and writes the value Windows keeps per program under
/// HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers ("~ WIN7RTM RUNASADMIN HIGHDPIAWARE").
/// Layers Navigator does not show (set by other tools or the Program Compatibility Assistant) are kept as they are.
/// </summary>
public static class CompatibilityLayers
{
    private static readonly (CompatibilityMode Mode, string Token)[] Modes =
    [
        (CompatibilityMode.Windows95, "WIN95"),
        (CompatibilityMode.Windows98, "WIN98"),
        (CompatibilityMode.WindowsXpSp2, "WINXPSP2"),
        (CompatibilityMode.WindowsXpSp3, "WINXPSP3"),
        (CompatibilityMode.WindowsVista, "VISTARTM"),
        (CompatibilityMode.WindowsVistaSp1, "VISTASP1"),
        (CompatibilityMode.WindowsVistaSp2, "VISTASP2"),
        (CompatibilityMode.Windows7, "WIN7RTM"),
        (CompatibilityMode.Windows8, "WIN8RTM"),
    ];

    /// <summary>The settings in <paramref name="value"/> and the layers that are not part of them.</summary>
    public static (CompatibilitySettings Settings, IReadOnlyList<string> Other) Parse(string? value)
    {
        var settings = CompatibilitySettings.Default;
        var other = new List<string>();
        if (string.IsNullOrWhiteSpace(value))
            return (settings, other);

        var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var gdiScaling = tokens.Contains("GDIDPISCALING", StringComparer.OrdinalIgnoreCase);
        foreach (var raw in tokens)
        {
            var token = raw.ToUpperInvariant();
            if (token is "~" or "$")
                continue; // markers ("not from the compatibility assistant"), rewritten on save
            var mode = Array.FindIndex(Modes, m => m.Token == token);
            if (mode >= 0)
            {
                settings = settings with { Mode = Modes[mode].Mode };
                continue;
            }

            settings = token switch
            {
                "256COLOR" => settings with { ReducedColor = ReducedColorMode.Colors8Bit },
                "16BITCOLOR" => settings with { ReducedColor = ReducedColorMode.Colors16Bit },
                "640X480" => settings with { Resolution640x480 = true },
                "DISABLEDXMAXIMIZEDWINDOWEDMODE" => settings with { DisableFullscreenOptimizations = true },
                "RUNASADMIN" => settings with { RunAsAdministrator = true },
                "HIGHDPIAWARE" => settings with { DpiOverride = DpiScalingOverride.Application },
                "DPIUNAWARE" => settings with { DpiOverride = gdiScaling ? DpiScalingOverride.SystemEnhanced : DpiScalingOverride.System },
                "GDIDPISCALING" => settings,
                _ => Keep(settings, other, raw),
            };
        }

        return (settings, other);
    }

    /// <summary>The registry value for the settings plus the kept layers; null when nothing is left (delete the value).</summary>
    public static string? Format(CompatibilitySettings settings, IReadOnlyList<string> other)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(other);
        var tokens = new List<string>();
        if (settings.Mode != CompatibilityMode.None)
            tokens.Add(Modes.First(m => m.Mode == settings.Mode).Token);
        if (settings.ReducedColor == ReducedColorMode.Colors8Bit)
            tokens.Add("256COLOR");
        else if (settings.ReducedColor == ReducedColorMode.Colors16Bit)
            tokens.Add("16BITCOLOR");
        if (settings.Resolution640x480)
            tokens.Add("640X480");
        if (settings.DisableFullscreenOptimizations)
            tokens.Add("DISABLEDXMAXIMIZEDWINDOWEDMODE");
        if (settings.RunAsAdministrator)
            tokens.Add("RUNASADMIN");
        switch (settings.DpiOverride)
        {
            case DpiScalingOverride.Application:
                tokens.Add("HIGHDPIAWARE");
                break;
            case DpiScalingOverride.System:
                tokens.Add("DPIUNAWARE");
                break;
            case DpiScalingOverride.SystemEnhanced:
                tokens.Add("GDIDPISCALING");
                tokens.Add("DPIUNAWARE");
                break;
        }

        tokens.AddRange(other);
        return tokens.Count == 0 ? null : "~ " + string.Join(' ', tokens);
    }

    private static CompatibilitySettings Keep(CompatibilitySettings settings, List<string> other, string token)
    {
        other.Add(token);
        return settings;
    }
}
