namespace Pantallazo.Core;

/// <summary>
/// Esquema de color Material 3 (variante "tonal spot", la de Material You) generado desde un color semilla.
/// Los nombres de los roles son los de la especificación M3; los tonos, los de la tabla oficial de roles.
/// </summary>
public static class MaterialScheme
{
    public const string DefaultSeed = "#4F6CB8";

    /// <summary>Por debajo de este croma (LCh) la semilla se considera gris.</summary>
    public const double GrayChroma = 8;

    private sealed record Palette(double Hue, double Chroma)
    {
        public Rgb Tone(double t) => ColorMath.FromLch(t, Chroma, Hue);
    }

    public static IReadOnlyDictionary<string, Rgb> Create(Rgb seed, bool dark)
    {
        var (_, c, h) = ColorMath.ToLch(seed);
        // Semilla casi gris: su matiz es ruido de redondeo (un gris salía verde), así que todo el esquema queda en
        // grises, como la variante "monocromo" de M3. Solo el error conserva su rojo.
        var gray = c < GrayChroma;
        var p = new Palette(h, gray ? 0 : Math.Max(c, 44));
        var s = new Palette(h, gray ? 0 : 16);
        var t = new Palette((h + 60) % 360, gray ? 0 : 28);
        var n = new Palette(h, gray ? 0 : 5);
        var nv = new Palette(h, gray ? 0 : 9);
        var e = new Palette(30, 80);

        var d = new Dictionary<string, Rgb>();
        void Set(string name, Palette pal, double light, double darkTone) => d[name] = pal.Tone(dark ? darkTone : light);

        Set("Primary", p, 40, 80);
        Set("OnPrimary", p, 100, 20);
        Set("PrimaryContainer", p, 90, 30);
        Set("OnPrimaryContainer", p, 10, 90);
        Set("InversePrimary", p, 80, 40);
        Set("Secondary", s, 40, 80);
        Set("OnSecondary", s, 100, 20);
        Set("SecondaryContainer", s, 90, 30);
        Set("OnSecondaryContainer", s, 10, 90);
        Set("Tertiary", t, 40, 80);
        Set("OnTertiary", t, 100, 20);
        Set("TertiaryContainer", t, 90, 30);
        Set("OnTertiaryContainer", t, 10, 90);
        Set("Error", e, 40, 80);
        Set("OnError", e, 100, 20);
        Set("ErrorContainer", e, 90, 30);
        Set("OnErrorContainer", e, 10, 90);
        Set("Surface", n, 98, 6);
        Set("SurfaceDim", n, 87, 6);
        Set("SurfaceBright", n, 98, 24);
        Set("SurfaceContainerLowest", n, 100, 4);
        Set("SurfaceContainerLow", n, 96, 10);
        Set("SurfaceContainer", n, 94, 12);
        Set("SurfaceContainerHigh", n, 92, 17);
        Set("SurfaceContainerHighest", n, 90, 22);
        Set("OnSurface", n, 10, 90);
        Set("OnSurfaceVariant", nv, 30, 80);
        Set("Outline", nv, 50, 60);
        Set("OutlineVariant", nv, 80, 30);
        Set("InverseSurface", n, 20, 90);
        Set("InverseOnSurface", n, 95, 20);
        d["Scrim"] = new Rgb(0, 0, 0);
        d["Shadow"] = new Rgb(0, 0, 0);
        return d;
    }
}
