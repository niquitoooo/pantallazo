using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pantallazo.Services;

/// <summary>
/// El ícono de la app (cámara de fotos clásica blanca sobre un cuadrado redondeado). Un solo dibujo para el .ico
/// (<c>Pantallazo.exe --make-icon</c>), la ventana y la bandeja; grabando, la bandeja lo muestra en rojo.
/// </summary>
public static class AppIconArt
{
    public const string MakeIconArg = "--make-icon";
    private static readonly int[] IcoSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    private static readonly Color Blue = Color.FromRgb(0x4F, 0x6C, 0xB8);
    private static readonly Color BlueLens = Color.FromRgb(0xDC, 0xE1, 0xFF);
    private static readonly Color Red = Color.FromRgb(0xBA, 0x1A, 0x1A);
    private static readonly Color RedLens = Color.FromRgb(0xFF, 0xDA, 0xD6);
    private static readonly Color Flash = Color.FromRgb(0xFF, 0xB4, 0xAB);

    /// <summary>Dibuja el ícono en un cuadrado de <paramref name="size"/> (el diseño está hecho sobre 64).</summary>
    public static void Draw(DrawingContext dc, double size, bool recording = false)
    {
        var bg = Brush(recording ? Red : Blue);
        var white = Brushes.White;
        dc.PushTransform(new ScaleTransform(size / 64, size / 64));
        dc.DrawRoundedRectangle(bg, null, new Rect(0, 0, 64, 64), 16, 16);
        // Joroba del visor y cuerpo
        var hump = new StreamGeometry();
        using (var g = hump.Open())
        {
            g.BeginFigure(new Point(23, 19.5), true, true);
            g.PolyLineTo([new Point(26, 14), new Point(38, 14), new Point(41, 19.5)], true, true);
        }
        hump.Freeze();
        dc.DrawGeometry(white, null, hump);
        dc.DrawRoundedRectangle(white, null, new Rect(9, 19, 46, 31), 8, 8);
        // Lente, brillo y flash
        dc.DrawEllipse(bg, null, new Point(32, 34.5), 11, 11);
        dc.DrawEllipse(Brush(recording ? RedLens : BlueLens), null, new Point(32, 34.5), 6.5, 6.5);
        dc.DrawEllipse(white, null, new Point(29.5, 32), 2, 2);
        dc.DrawEllipse(Brush(recording ? Red : Flash), null, new Point(47, 25.5), 2.3, 2.3);
        dc.Pop();
    }

    public static BitmapSource Render(int size, bool recording = false)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) Draw(dc, size, recording);
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>Escribe un .ico con todos los tamaños que usa Windows, cada uno en PNG (como el que se reemplazó).</summary>
    public static void WriteIco(string path)
    {
        var images = IcoSizes.Select(s =>
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(Render(s)));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return (Size: s, Png: ms.ToArray());
        }).ToList();

        using var w = new BinaryWriter(File.Create(path));
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)images.Count);
        var offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((ushort)1); w.Write((ushort)32);
            w.Write(png.Length); w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) w.Write(png);
    }

    private static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
