using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using SCSKiller.Ui.Models;

namespace SCSKiller.Ui.Platform;

/// <summary>The desktop's accent colour (the portal's org.freedesktop.appearance accent-color, which Avalonia reads and
/// follows) and the shades <see cref="AccentPalette"/> makes of it. Without one, Windows' default blue from App.axaml.</summary>
static class Accent
{
    static readonly string[] Keys =
        ["SystemAccentColorLight3", "SystemAccentColorLight2", "SystemAccentColorLight1", "SystemAccentColor",
         "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3"];

    /// <summary>Avalonia's accent when the portal has none (or no portal runs).</summary>
    static readonly Color None = Color.FromRgb(0x00, 0x78, 0xD7);

    /// <summary>Sets <paramref name="app"/>'s accent colours from the desktop's, now and whenever the user changes it.</summary>
    public static void Follow(Application app)
    {
        if (app.PlatformSettings is not { } settings) return;
        var defaults = Keys.Select(k => app.Resources[k]).ToArray();
        Apply(app, settings.GetColorValues(), defaults);
        settings.ColorValuesChanged += (_, values) => Dispatcher.UIThread.Post(() => Apply(app, values, defaults));
    }

    static void Apply(Application app, PlatformColorValues values, object?[] defaults)
    {
        var accent = values.AccentColor1;
        if (accent.A == 0 || (accent.R, accent.G, accent.B) == (None.R, None.G, None.B))
        {
            for (int i = 0; i < Keys.Length; i++) app.Resources[Keys[i]] = defaults[i];
            return;
        }
        var shades = AccentPalette.From(accent.R, accent.G, accent.B);
        for (int i = 0; i < Keys.Length; i++) app.Resources[Keys[i]] = Color.FromRgb(shades[i].R, shades[i].G, shades[i].B);
    }
}
