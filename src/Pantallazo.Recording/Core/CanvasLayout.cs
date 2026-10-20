namespace Pantallazo.Recording;

/// <summary>Rectángulo entero en píxeles físicos del escritorio virtual.</summary>
internal readonly record struct RectI(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public RectI Intersect(RectI other)
    {
        int x1 = Math.Max(X, other.X), y1 = Math.Max(Y, other.Y);
        int x2 = Math.Min(Right, other.Right), y2 = Math.Min(Bottom, other.Bottom);
        return x2 > x1 && y2 > y1 ? new RectI(x1, y1, x2 - x1, y2 - y1) : default;
    }

    public static RectI Union(IEnumerable<RectI> rects)
    {
        bool any = false;
        int x1 = 0, y1 = 0, x2 = 0, y2 = 0;
        foreach (var r in rects)
        {
            if (r.IsEmpty) continue;
            if (!any) { x1 = r.X; y1 = r.Y; x2 = r.Right; y2 = r.Bottom; any = true; continue; }
            x1 = Math.Min(x1, r.X); y1 = Math.Min(y1, r.Y);
            x2 = Math.Max(x2, r.Right); y2 = Math.Max(y2, r.Bottom);
        }
        return any ? new RectI(x1, y1, x2 - x1, y2 - y1) : default;
    }

    public long Area => (long)Width * Height;
}

/// <summary>Qué parte de un monitor se copia y a qué posición del lienzo.</summary>
/// <param name="Source">Recorte en coordenadas locales del monitor (0,0 = su esquina).</param>
/// <param name="DestX">Destino dentro del lienzo.</param>
internal sealed record MonitorPlacement(DisplayInfo Display, RectI Source, int DestX, int DestY);

internal static class CanvasLayout
{
    public static RectI BoundsOf(DisplayInfo d) => new(d.X, d.Y, d.Width, d.Height);

    /// <summary>
    /// Rectángulo que se graba (en el escritorio virtual), con ancho y alto pares (se recorta 1 px si hace falta).
    /// Lanza <see cref="ArgumentException"/> con mensaje en castellano si el destino no es válido.
    /// </summary>
    public static RectI ResolveCanvas(CaptureTargetKind kind, IReadOnlyList<DisplayInfo> displays,
        string? displayId, (int X, int Y, int Width, int Height)? region)
    {
        if (displays.Count == 0) throw new ArgumentException("No se encontró ningún monitor.");
        RectI canvas;
        switch (kind)
        {
            case CaptureTargetKind.Display:
                var d = displays.FirstOrDefault(x => string.Equals(x.Id, displayId, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException("El monitor elegido ya no está conectado.");
                canvas = BoundsOf(d);
                break;
            case CaptureTargetKind.Region:
                if (region is not { } r || r.Width < 2 || r.Height < 2)
                    throw new ArgumentException("La región elegida es demasiado chica.");
                var virt = RectI.Union(displays.Select(BoundsOf));
                canvas = new RectI(r.X, r.Y, r.Width, r.Height).Intersect(virt);
                if (canvas.Width < 2 || canvas.Height < 2)
                    throw new ArgumentException("La región elegida queda fuera de los monitores.");
                break;
            default:
                canvas = RectI.Union(displays.Select(BoundsOf));
                break;
        }
        return canvas with { Width = canvas.Width & ~1, Height = canvas.Height & ~1 };
    }

    /// <summary>Monitores que tocan el lienzo, con su recorte y destino. Ordenados por área copiada (mayor primero).</summary>
    public static IReadOnlyList<MonitorPlacement> Place(RectI canvas, IEnumerable<DisplayInfo> displays)
    {
        var list = new List<MonitorPlacement>();
        foreach (var d in displays)
        {
            var b = BoundsOf(d);
            var inter = b.Intersect(canvas);
            if (inter.IsEmpty) continue;
            list.Add(new MonitorPlacement(d,
                new RectI(inter.X - b.X, inter.Y - b.Y, inter.Width, inter.Height),
                inter.X - canvas.X, inter.Y - canvas.Y));
        }
        return list.OrderByDescending(p => p.Source.Area).ToList();
    }
}
