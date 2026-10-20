using System.Globalization;

namespace Pantallazo.Core;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// Paletas tonales al estilo Material 3. M3 usa HCT (matiz y croma de CAM16, tono = L* de CIELAB);
/// acá se aproxima con LCh de CIELAB: el tono es exactamente L*, que es lo que fija el contraste,
/// y el matiz/croma de Lab se parecen lo suficiente a los de CAM16 para armar un esquema coherente.
/// </summary>
public static class ColorMath
{
    public static bool TryParseHex(string? text, out Rgb rgb)
    {
        rgb = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().TrimStart('#');
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        rgb = new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public static (double L, double C, double H) ToLch(Rgb c)
    {
        var (l, a, b) = ToLab(c);
        var h = Math.Atan2(b, a) * 180 / Math.PI;
        if (h < 0) h += 360;
        return (l, Math.Sqrt(a * a + b * b), h);
    }

    /// <summary>Color con ese tono (L*), matiz y el mayor croma ≤ <paramref name="chroma"/> que entre en sRGB.</summary>
    public static Rgb FromLch(double tone, double chroma, double hue)
    {
        tone = Math.Clamp(tone, 0, 100);
        if (tone <= 0) return new Rgb(0, 0, 0);
        if (tone >= 100) return new Rgb(255, 255, 255);
        double lo = 0, hi = chroma;
        if (InGamut(tone, hi, hue)) return ToRgb(tone, hi, hue);
        for (var i = 0; i < 24; i++)
        {
            var mid = (lo + hi) / 2;
            if (InGamut(tone, mid, hue)) lo = mid; else hi = mid;
        }
        return ToRgb(tone, lo, hue);
    }

    /// <summary>Relación de contraste WCAG entre dos colores (1..21).</summary>
    public static double Contrast(Rgb a, Rgb b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double RelativeLuminance(Rgb c) =>
        0.2126 * Linear(c.R / 255.0) + 0.7152 * Linear(c.G / 255.0) + 0.0722 * Linear(c.B / 255.0);

    private static bool InGamut(double l, double c, double h)
    {
        var (r, g, b) = LchToLinear(l, c, h);
        const double e = 1e-4;
        return r is >= -e and <= 1 + e && g is >= -e and <= 1 + e && b is >= -e and <= 1 + e;
    }

    private static Rgb ToRgb(double l, double c, double h)
    {
        var (r, g, b) = LchToLinear(l, c, h);
        return new Rgb(ToByte(r), ToByte(g), ToByte(b));
    }

    private static byte ToByte(double linear) =>
        (byte)Math.Round(Math.Clamp(Gamma(Math.Clamp(linear, 0, 1)), 0, 1) * 255);

    // D65
    private const double Xn = 0.95047, Yn = 1.0, Zn = 1.08883;

    private static (double L, double A, double B) ToLab(Rgb c)
    {
        double r = Linear(c.R / 255.0), g = Linear(c.G / 255.0), b = Linear(c.B / 255.0);
        var x = (0.4124564 * r + 0.3575761 * g + 0.1804375 * b) / Xn;
        var y = (0.2126729 * r + 0.7151522 * g + 0.0721750 * b) / Yn;
        var z = (0.0193339 * r + 0.1191920 * g + 0.9503041 * b) / Zn;
        double fx = F(x), fy = F(y), fz = F(z);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    private static (double R, double G, double B) LchToLinear(double l, double c, double h)
    {
        var hr = h * Math.PI / 180;
        double a = c * Math.Cos(hr), bb = c * Math.Sin(hr);
        var fy = (l + 16) / 116;
        var fx = fy + a / 500;
        var fz = fy - bb / 200;
        double x = FInv(fx) * Xn, y = FInv(fy) * Yn, z = FInv(fz) * Zn;
        return (
            3.2404542 * x - 1.5371385 * y - 0.4985314 * z,
            -0.9692660 * x + 1.8760108 * y + 0.0415560 * z,
            0.0556434 * x - 0.2040259 * y + 1.0572252 * z);
    }

    private static double F(double t) => t > 216.0 / 24389 ? Math.Cbrt(t) : (24389.0 / 27 * t + 16) / 116;
    private static double FInv(double t) => t * t * t > 216.0 / 24389 ? t * t * t : (116 * t - 16) / (24389.0 / 27);
    private static double Linear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    private static double Gamma(double c) => c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
}
