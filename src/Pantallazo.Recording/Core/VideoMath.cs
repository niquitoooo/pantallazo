namespace Pantallazo.Recording;

/// <summary>Cálculos puros de video: tamaño de salida, escalado, bitrate y fps efectivos.</summary>
internal static class VideoMath
{
    // Tope práctico de los encoders H.264 por hardware (nivel 5.1/5.2 ≈ 4096×2304).
    public const int HardwareMaxWidth = 4096;
    public const int HardwareMaxHeight = 2304;
    // Modo seguro del encoder por software (más arriba, algunos encoders fallan con E_OUTOFMEMORY).
    public const int SoftwareMaxWidth = 1920;
    public const int SoftwareMaxHeight = 1080;
    public const int SoftwareMaxFps = 60;

    public const int MinBitrate = 4_000_000;
    public const int MaxBitrate = 60_000_000;

    /// <summary>Redondea hacia abajo a par (H.264 4:2:0 necesita dimensiones pares), mínimo 2.</summary>
    public static (int Width, int Height) EvenFloor(int width, int height)
        => (Math.Max(2, width & ~1), Math.Max(2, height & ~1));

    /// <summary>
    /// Tamaño que recibe el encoder: el de entrada si entra en el máximo, si no, escalado proporcional
    /// (dimensiones pares). Se respeta ancho, alto y área máximos.
    /// </summary>
    public static (int Width, int Height) FitOutput(int width, int height, int maxWidth, int maxHeight)
    {
        (width, height) = EvenFloor(width, height);
        double scale = 1.0;
        if (width > maxWidth) scale = Math.Min(scale, (double)maxWidth / width);
        if (height > maxHeight) scale = Math.Min(scale, (double)maxHeight / height);
        double area = (double)width * height;
        double maxArea = (double)maxWidth * maxHeight;
        if (area > maxArea) scale = Math.Min(scale, Math.Sqrt(maxArea / area));
        if (scale >= 1.0) return (width, height);
        int w = (int)Math.Floor(width * scale);
        int h = (int)Math.Floor(height * scale);
        return EvenFloor(Math.Min(w, maxWidth), Math.Min(h, maxHeight));
    }

    /// <summary>
    /// Tamaño de salida: el del área, achicado si el encoder no lo acepta y, si se pidió, hasta
    /// <paramref name="maxHeight"/> de alto (proporcional; nunca se agranda).
    /// </summary>
    public static (int Width, int Height) OutputSize(int width, int height, bool software, int? maxHeight = null)
    {
        var (w, h) = software
            ? FitOutput(width, height, SoftwareMaxWidth, SoftwareMaxHeight)
            : FitOutput(width, height, HardwareMaxWidth, HardwareMaxHeight);
        if (maxHeight is int mh && mh > 0 && h > mh) (w, h) = FitOutput(w, h, w, mh);
        return (w, h);
    }

    /// <summary>Tamaño que eligió el usuario, sin límites del encoder: el área (pares) o achicada al alto pedido.</summary>
    public static (int Width, int Height) WantedSize(int width, int height, int? maxHeight)
    {
        var (w, h) = EvenFloor(width, height);
        return maxHeight is int mh && mh > 0 && h > mh ? FitOutput(w, h, w, mh) : (w, h);
    }

    public static double QualityFactor(VideoQuality quality) => quality switch
    {
        VideoQuality.Low => 0.06,
        VideoQuality.High => 0.16,
        _ => 0.10,
    };

    /// <summary>Bitrate medio objetivo en bits/s: ancho×alto×fps×factor, acotado a 4–60 Mbps.</summary>
    public static int Bitrate(int width, int height, int fps, VideoQuality quality)
    {
        double bits = (double)width * height * fps * QualityFactor(quality);
        return (int)Math.Clamp(bits, MinBitrate, MaxBitrate);
    }

    public static int EffectiveFps(int requested, bool software)
    {
        int fps = Math.Clamp(requested <= 0 ? 30 : requested, 1, 240);
        return software ? Math.Min(fps, SoftwareMaxFps) : fps;
    }
}
