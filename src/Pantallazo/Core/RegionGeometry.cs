using System.Text.Json.Serialization;

namespace Pantallazo.Core;

/// <summary>Rectángulo entero en píxeles (físicos o de imagen).</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    [JsonIgnore] public int Right => X + Width;
    [JsonIgnore] public int Bottom => Y + Height;
    [JsonIgnore] public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    public static PixelRect FromPoints(int x1, int y1, int x2, int y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));

    public PixelRect Intersect(PixelRect other)
    {
        var x = Math.Max(X, other.X);
        var y = Math.Max(Y, other.Y);
        var r = Math.Min(Right, other.Right);
        var b = Math.Min(Bottom, other.Bottom);
        return r <= x || b <= y ? default : new PixelRect(x, y, r - x, b - y);
    }

    public PixelRect Union(PixelRect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        var x = Math.Min(X, other.X);
        var y = Math.Min(Y, other.Y);
        return new PixelRect(x, y, Math.Max(Right, other.Right) - x, Math.Max(Bottom, other.Bottom) - y);
    }

    public PixelRect Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };
}

/// <summary>
/// Cuentas del selector de región, copiadas del comportamiento de ShareX
/// (RegionSelectionOverlay / RegionCaptureWindow de ShareX).
/// Todo en píxeles de la imagen congelada (origen = esquina superior izquierda del escritorio virtual).
/// </summary>
public static class RegionGeometry
{
    /// <summary>Por debajo de este tamaño la selección no cuenta (ShareX MinimumSize = 5).</summary>
    public const int MinimumSize = 5;

    /// <summary>Arrastrar al menos esto convierte un clic sobre una ventana en una selección nueva.</summary>
    public const int DragThreshold = 4;

    public const int MagnifierOffset = 18;
    public const int InfoGapBelow = 8;
    public const int InfoGapAbove = 36;

    /// <summary>
    /// Rectángulo entre el punto de inicio y el actual, limitado a la imagen (ShareX: NormalizeAndClamp).
    /// Con <paramref name="square"/> (Shift) fuerza un cuadrado del lado mayor hacia donde va el mouse,
    /// recortado al espacio disponible (ShareX: CreateSelectionRectangle).
    /// </summary>
    public static PixelRect FromDrag(int startX, int startY, int x, int y, bool square, int imageWidth, int imageHeight)
    {
        if (square)
        {
            var dx = x - startX;
            var dy = y - startY;
            var side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            var availW = dx < 0 ? startX : imageWidth - startX;
            var availH = dy < 0 ? startY : imageHeight - startY;
            side = Math.Max(0, Math.Min(side, Math.Min(availW, availH)));
            x = startX + (dx < 0 ? -side : side);
            y = startY + (dy < 0 ? -side : side);
        }
        var left = Math.Clamp(Math.Min(startX, x), 0, imageWidth);
        var top = Math.Clamp(Math.Min(startY, y), 0, imageHeight);
        var right = Math.Clamp(Math.Max(startX, x), 0, imageWidth);
        var bottom = Math.Clamp(Math.Max(startY, y), 0, imageHeight);
        return new PixelRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    public static bool IsBigEnough(PixelRect r) => r.Width >= MinimumSize && r.Height >= MinimumSize;

    /// <summary>
    /// Posición del panel de la lupa: <see cref="MagnifierOffset"/> abajo a la derecha del puntero; si no entra
    /// se da vuelta y al final se limita a la superficie (ShareX: PositionPanelNearPointer, en DIP).
    /// </summary>
    public static (double X, double Y) PanelNearPointer(double px, double py, double width, double height,
        double surfaceWidth, double surfaceHeight)
    {
        var x = px + MagnifierOffset;
        var y = py + MagnifierOffset;
        if (x + width > surfaceWidth) x = px - width - MagnifierOffset;
        if (y + height > surfaceHeight) y = py - height - MagnifierOffset;
        return (Math.Clamp(x, 0, Math.Max(0, surfaceWidth - width)), Math.Clamp(y, 0, Math.Max(0, surfaceHeight - height)));
    }

    /// <summary>
    /// Posición del rótulo "ancho × alto" en DIP: abajo a la izquierda de la selección, 8 debajo;
    /// si no entra, 36 arriba (ShareX: UpdateSelectionInfo).
    /// </summary>
    public static (double X, double Y) SizeLabelPosition(double left, double top, double bottom, double surfaceWidth, double surfaceHeight)
    {
        var x = Math.Clamp(left, 0, Math.Max(0, surfaceWidth - 100));
        var y = bottom + InfoGapBelow;
        if (y + InfoGapAbove > surfaceHeight) y = Math.Max(0, top - InfoGapAbove);
        return (x, y);
    }

    /// <summary>Cantidad de píxeles de lado en la lupa: impar entre 3 y el máximo (ShareX: rueda = ±2).</summary>
    public static int ClampMagnifierPixels(int count, int maximum = 35)
    {
        maximum = Math.Clamp(maximum, 3, 35);
        if (maximum % 2 == 0) maximum--;
        count = Math.Clamp(count, 3, maximum);
        if (count % 2 == 0) count = count < maximum ? count + 1 : count - 1;
        return count;
    }
}
