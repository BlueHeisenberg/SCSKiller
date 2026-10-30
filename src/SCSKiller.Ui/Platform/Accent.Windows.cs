using Avalonia;
using Avalonia.Media;
using Microsoft.Win32;

namespace SCSKiller.Ui.Platform;

/// <summary>The system accent colour and its shades, as WinUI's resources take them. Avalonia derives the shades itself,
/// lighter and greyer than Windows' own.</summary>
static class Accent
{
    // AccentPalette's order: eight RGBA colours from lightest to darkest, the eighth unused
    static readonly string[] Keys =
        ["SystemAccentColorLight3", "SystemAccentColorLight2", "SystemAccentColorLight1", "SystemAccentColor",
         "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3"];

    /// <summary>Sets <paramref name="app"/>'s accent colours from the system's, now and whenever the user changes it.</summary>
    public static void Follow(Application app)
    {
        Apply(app);
        if (app.PlatformSettings is { } settings) settings.ColorValuesChanged += (_, _) => Apply(app);
    }

    static void Apply(Application app)
    {
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent", "AccentPalette", null)
            is not byte[] { Length: >= 28 } palette) return;   // none: Windows' default blue from App.axaml
        for (int i = 0; i < Keys.Length; i++) app.Resources[Keys[i]] = Color.FromRgb(palette[i * 4], palette[i * 4 + 1], palette[i * 4 + 2]);
    }
}
