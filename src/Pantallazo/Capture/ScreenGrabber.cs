using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pantallazo.Core;
using Pantallazo.Native;

namespace Pantallazo.Capture;

/// <summary>Puntero guardado: copia de su ícono y esquina superior izquierda donde se dibuja (píxeles físicos).</summary>
public sealed class CursorSnapshot(nint icon, int x, int y) : IDisposable
{
    public nint Icon { get; private set; } = icon;
    public int X { get; } = x;
    public int Y { get; } = y;

    public void Dispose()
    {
        if (Icon != 0) NativeMethods.DestroyIcon(Icon);
        Icon = 0;
    }
}

/// <summary>Imagen BGRA de 32 bits, con su ubicación en el escritorio virtual.</summary>
public sealed class CapturedImage(PixelRect bounds, byte[] pixels)
{
    public PixelRect Bounds { get; } = bounds;
    public int Width => Bounds.Width;
    public int Height => Bounds.Height;
    public byte[] Pixels { get; } = pixels;
    public int Stride => Width * 4;

    public BitmapSource ToBitmapSource()
    {
        var bmp = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, Pixels, Stride);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Recorte en coordenadas del escritorio virtual (se limita a la imagen).</summary>
    public CapturedImage Crop(PixelRect desktopRect)
    {
        var r = desktopRect.Intersect(Bounds);
        if (r.IsEmpty) throw new ArgumentException("La región está fuera de la pantalla.");
        var dst = new byte[r.Width * r.Height * 4];
        var srcX = r.X - Bounds.X;
        var srcY = r.Y - Bounds.Y;
        for (var row = 0; row < r.Height; row++)
            Buffer.BlockCopy(Pixels, (srcY + row) * Stride + srcX * 4, dst, row * r.Width * 4, r.Width * 4);
        return new CapturedImage(r, dst);
    }

    /// <summary>Color del píxel (x, y) en coordenadas de la imagen.</summary>
    public Color PixelAt(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return Colors.Black;
        var i = y * Stride + x * 4;
        return Color.FromRgb(Pixels[i + 2], Pixels[i + 1], Pixels[i]);
    }
}

/// <summary>
/// Captura con GDI BitBlt(SRCCOPY | CAPTUREBLT) desde el DC del escritorio, igual que ShareX:
/// funciona con varios monitores y DPI distintos porque el proceso es PerMonitorV2 y todo está en píxeles físicos.
/// </summary>
public static class ScreenGrabber
{
    public static CapturedImage CaptureVirtualScreen(bool includeCursor) =>
        Capture(Screens.VirtualScreen(), includeCursor);

    public static CapturedImage Capture(PixelRect bounds, bool includeCursor)
    {
        if (bounds.IsEmpty) throw new InvalidOperationException("No se encontró ninguna pantalla para capturar.");

        var desktop = NativeMethods.GetDesktopWindow();
        var srcDc = NativeMethods.GetWindowDC(desktop);
        if (srcDc == 0) throw new InvalidOperationException("Windows no dio acceso a la pantalla (GetWindowDC).");
        var memDc = NativeMethods.CreateCompatibleDC(srcDc);
        nint dib = 0, old = 0;
        try
        {
            var header = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = bounds.Width,
                biHeight = -bounds.Height, // de arriba hacia abajo
                biPlanes = 1,
                biBitCount = 32,
            };
            dib = NativeMethods.CreateDIBSection(memDc, ref header, 0, out var bits, 0, 0);
            if (dib == 0 || bits == 0)
                throw new InvalidOperationException($"No hay memoria para una captura de {bounds.Width}×{bounds.Height}.");
            old = NativeMethods.SelectObject(memDc, dib);

            if (!NativeMethods.BitBlt(memDc, 0, 0, bounds.Width, bounds.Height, srcDc, bounds.X, bounds.Y,
                    NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT))
                throw new InvalidOperationException("Windows no permitió copiar la pantalla (BitBlt).");

            if (includeCursor) DrawCursor(memDc, bounds);

            var size = bounds.Width * bounds.Height * 4;
            var pixels = new byte[size];
            Marshal.Copy(bits, pixels, 0, size);
            for (var i = 3; i < size; i += 4) pixels[i] = 255; // GDI deja el alfa en 0
            return new CapturedImage(bounds, pixels);
        }
        finally
        {
            if (old != 0) NativeMethods.SelectObject(memDc, old);
            if (dib != 0) NativeMethods.DeleteObject(dib);
            NativeMethods.DeleteDC(memDc);
            NativeMethods.ReleaseDC(desktop, srcDc);
        }
    }

    /// <summary>
    /// Guarda el puntero tal como está ahora (copia del ícono, punto activo y posición en pantalla), para dibujarlo
    /// después sobre una imagen congelada. null si el puntero está oculto. Hay que llamar a Dispose.
    /// </summary>
    public static CursorSnapshot? TakeCursor()
    {
        var ci = new NativeMethods.CURSORINFO { cbSize = Marshal.SizeOf<NativeMethods.CURSORINFO>() };
        if (!NativeMethods.GetCursorInfo(ref ci) || (ci.flags & NativeMethods.CURSOR_SHOWING) == 0 || ci.hCursor == 0) return null;
        if (!NativeMethods.GetIconInfo(ci.hCursor, out var ii)) return null;
        if (ii.hbmMask != 0) NativeMethods.DeleteObject(ii.hbmMask);
        if (ii.hbmColor != 0) NativeMethods.DeleteObject(ii.hbmColor);
        var copy = NativeMethods.CopyIcon(ci.hCursor);
        return copy == 0 ? null : new CursorSnapshot(copy, ci.ptScreenPos.X - ii.xHotspot, ci.ptScreenPos.Y - ii.yHotspot);
    }

    /// <summary>Devuelve la imagen con el puntero dibujado en su lugar (si cae afuera de la imagen, queda igual).</summary>
    public static CapturedImage WithCursor(CapturedImage image, CursorSnapshot cursor)
    {
        var b = image.Bounds;
        var screenDc = NativeMethods.GetWindowDC(0);
        var memDc = NativeMethods.CreateCompatibleDC(screenDc);
        nint dib = 0, old = 0;
        try
        {
            var header = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = b.Width,
                biHeight = -b.Height,
                biPlanes = 1,
                biBitCount = 32,
            };
            dib = NativeMethods.CreateDIBSection(memDc, ref header, 0, out var bits, 0, 0);
            if (dib == 0 || bits == 0) return image;
            old = NativeMethods.SelectObject(memDc, dib);
            Marshal.Copy(image.Pixels, 0, bits, image.Pixels.Length);
            NativeMethods.DrawIconEx(memDc, cursor.X - b.X, cursor.Y - b.Y, cursor.Icon, 0, 0, 0, 0, NativeMethods.DI_NORMAL);
            var pixels = new byte[image.Pixels.Length];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            return new CapturedImage(b, pixels);
        }
        finally
        {
            if (old != 0) NativeMethods.SelectObject(memDc, old);
            if (dib != 0) NativeMethods.DeleteObject(dib);
            NativeMethods.DeleteDC(memDc);
            NativeMethods.ReleaseDC(0, screenDc);
        }
    }

    private static void DrawCursor(nint dc, PixelRect bounds)
    {
        var ci = new NativeMethods.CURSORINFO { cbSize = Marshal.SizeOf<NativeMethods.CURSORINFO>() };
        if (!NativeMethods.GetCursorInfo(ref ci) || (ci.flags & NativeMethods.CURSOR_SHOWING) == 0 || ci.hCursor == 0) return;
        if (!NativeMethods.GetIconInfo(ci.hCursor, out var ii)) return;
        try
        {
            var x = ci.ptScreenPos.X - ii.xHotspot - bounds.X;
            var y = ci.ptScreenPos.Y - ii.yHotspot - bounds.Y;
            NativeMethods.DrawIconEx(dc, x, y, ci.hCursor, 0, 0, 0, 0, NativeMethods.DI_NORMAL);
        }
        finally
        {
            if (ii.hbmMask != 0) NativeMethods.DeleteObject(ii.hbmMask);
            if (ii.hbmColor != 0) NativeMethods.DeleteObject(ii.hbmColor);
        }
    }
}
