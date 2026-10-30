namespace SCSKiller.Ui.Models;

/// <summary>The seven accent shades WinUI's resources take, from the one accent colour a Linux desktop gives: the accent
/// itself, and the others at the lightness of Windows' default palette's shades, with the accent's hue. Black text on Light2
/// (the dark theme's accent fill) and white text on Dark1 (the light theme's) then read as on Windows' own blue.</summary>
public static class AccentPalette
{
    // OKLab lightness of Windows' default palette (#99EBFF #4CC2FF #0091F8, #0067C0 #003E92 #001A68) and each shade's
    // chroma over the accent's (#0078D4). The accent's own slot is the input as it is.
    static readonly (double L, double C)[] Shades = [(0.894, 0.50), (0.773, 0.81), (0.647, 1.10), (0, 0), (0.515, 0.96), (0.387, 0.90), (0.269, 0.83)];

    /// <summary>Light3, Light2, Light1, the accent, Dark1, Dark2, Dark3.</summary>
    public static (byte R, byte G, byte B)[] From(byte r, byte g, byte b)
    {
        var (l, a, bb) = ToOklab(r, g, b);
        double c = Math.Sqrt(a * a + bb * bb), h = Math.Atan2(bb, a);
        return Shades.Select(s => s.L == 0 ? (r, g, b) : InGamut(s.L, c * s.C, h)).ToArray();
    }

    /// <summary>The colour at lightness <paramref name="l"/> and hue <paramref name="h"/> with the most of chroma
    /// <paramref name="c"/> that sRGB can show.</summary>
    static (byte, byte, byte) InGamut(double l, double c, double h)
    {
        double lo = 0, hi = c;
        if (Fits(l, c, h)) lo = c;
        else
            for (int i = 0; i < 24; i++)
            {
                double mid = (lo + hi) / 2;
                if (Fits(l, mid, h)) lo = mid; else hi = mid;
            }
        var (r, g, b) = ToLinear(l, lo * Math.Cos(h), lo * Math.Sin(h));
        return (Encode(r), Encode(g), Encode(b));
    }

    static bool Fits(double l, double c, double h)
    {
        var (r, g, b) = ToLinear(l, c * Math.Cos(h), c * Math.Sin(h));
        const double e = 1e-6;
        return r is >= -e and <= 1 + e && g is >= -e and <= 1 + e && b is >= -e and <= 1 + e;
    }

    static (double L, double A, double B) ToOklab(byte r8, byte g8, byte b8)
    {
        double r = Decode(r8), g = Decode(g8), b = Decode(b8);
        double l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        double m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        double s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    static (double R, double G, double B) ToLinear(double l, double a, double b)
    {
        double l3 = Math.Pow(l + 0.3963377774 * a + 0.2158037573 * b, 3);
        double m3 = Math.Pow(l - 0.1055613458 * a - 0.0638541728 * b, 3);
        double s3 = Math.Pow(l - 0.0894841775 * a - 1.2914855480 * b, 3);
        return (4.0767416621 * l3 - 3.3077115913 * m3 + 0.2309699292 * s3,
                -1.2684380046 * l3 + 2.6097574011 * m3 - 0.3413193965 * s3,
                -0.0041960863 * l3 - 0.7034186147 * m3 + 1.7076147010 * s3);
    }

    static double Decode(byte v)
    {
        double c = v / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    static byte Encode(double v)
    {
        v = Math.Clamp(v, 0, 1);
        double c = v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
        return (byte)Math.Round(c * 255);
    }
}
