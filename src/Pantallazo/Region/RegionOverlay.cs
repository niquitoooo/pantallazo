using System.Windows;
using System.Windows.Media;
using Pantallazo.Core;

namespace Pantallazo.Region;

/// <summary>
/// Capa de la selección en píxeles de imagen (ShareX: RegionSelectionOverlay): oscurece afuera, marca el
/// rectángulo activo con una línea del color de acento y encima una discontinua negra 5/5, y la cruz central.
/// </summary>
internal sealed class RegionOverlay : FrameworkElement
{
    private static readonly Pen AntPen = CreateAntPen();
    private Brush _dim = Frozen(new SolidColorBrush(Color.FromArgb(51, 0, 0, 0)));
    private Brush _accent = Brushes.DodgerBlue;
    private Pen _accentPen = new(Brushes.DodgerBlue, 1);

    public PixelRect Selection { get; private set; }
    public PixelRect Hover { get; private set; }
    public bool ShowCenterCrosshair { get; set; } = true;
    public bool ShowCursorCrosshair { get; set; }
    public Point CursorPoint { get; private set; }

    public PixelRect Active => Selection.IsEmpty ? Hover : Selection;

    public RegionOverlay()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    public void SetAccent(Color c)
    {
        _accent = Frozen(new SolidColorBrush(c));
        _accentPen = new Pen(_accent, 1);
        _accentPen.Freeze();
        InvalidateVisual();
    }

    public void SetDimAlpha(byte alpha)
    {
        _dim = Frozen(new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0)));
        InvalidateVisual();
    }

    public void SetSelection(PixelRect r)
    {
        if (r == Selection) return;
        Selection = r;
        InvalidateVisual();
    }

    public void SetHover(PixelRect r)
    {
        if (r == Hover) return;
        Hover = r;
        InvalidateVisual();
    }

    public void SetCursor(Point p)
    {
        if (p == CursorPoint) return;
        CursorPoint = p;
        if (ShowCursorCrosshair) InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var surface = new Rect(0, 0, ActualWidth, ActualHeight);
        var a = Active;
        var active = a.IsEmpty ? Rect.Empty : Rect.Intersect(new Rect(a.X, a.Y, a.Width, a.Height), surface);

        if (active.IsEmpty || active.Width <= 0 || active.Height <= 0)
        {
            dc.DrawRectangle(_dim, null, surface);
        }
        else
        {
            Fill(dc, new Rect(0, 0, surface.Width, active.Top));
            Fill(dc, new Rect(0, active.Top, active.Left, active.Height));
            Fill(dc, new Rect(active.Right, active.Top, surface.Width - active.Right, active.Height));
            Fill(dc, new Rect(0, active.Bottom, surface.Width, surface.Height - active.Bottom));

            var ant = new Rect(active.X + 0.5, active.Y + 0.5, Math.Max(0, active.Width - 1), Math.Max(0, active.Height - 1));
            dc.DrawRectangle(null, _accentPen, ant);
            dc.DrawLine(AntPen, ant.TopLeft, ant.TopRight);
            dc.DrawLine(AntPen, ant.TopLeft, ant.BottomLeft);
            dc.DrawLine(AntPen, ant.TopRight, ant.BottomRight);
            dc.DrawLine(AntPen, ant.BottomLeft, ant.BottomRight);

            if (ShowCenterCrosshair && !Selection.IsEmpty)
            {
                var cx = (int)Math.Floor(Selection.X + Selection.Width / 2.0);
                var cy = (int)Math.Floor(Selection.Y + Selection.Height / 2.0);
                Cross(dc, Brushes.Black, cx - 1, cy - 1);
                Cross(dc, _accent, cx, cy);
            }
        }

        if (ShowCursorCrosshair) DrawCursorCrosshair(dc, surface);
    }

    private void DrawCursorCrosshair(DrawingContext dc, Rect surface)
    {
        const double gap = 5;
        var x = Math.Clamp(Math.Floor(CursorPoint.X), surface.Left, Math.Max(surface.Left, surface.Right - 1)) + 0.5;
        var y = Math.Clamp(Math.Floor(CursorPoint.Y), surface.Top, Math.Max(surface.Top, surface.Bottom - 1)) + 0.5;
        AntLine(dc, new Point(surface.Left, y), new Point(x - gap, y));
        AntLine(dc, new Point(x + gap, y), new Point(surface.Right, y));
        AntLine(dc, new Point(x, surface.Top), new Point(x, y - gap));
        AntLine(dc, new Point(x, y + gap), new Point(x, surface.Bottom));
    }

    private void AntLine(DrawingContext dc, Point a, Point b)
    {
        if (b.X <= a.X && b.Y <= a.Y) return;
        dc.DrawLine(_accentPen, a, b);
        dc.DrawLine(AntPen, a, b);
    }

    private static void Cross(DrawingContext dc, Brush brush, int cx, int cy)
    {
        const int radius = 10, diameter = radius * 2 + 1;
        dc.DrawRectangle(brush, null, new Rect(cx - radius, cy, diameter, 1));
        dc.DrawRectangle(brush, null, new Rect(cx, cy - radius, 1, diameter));
    }

    private void Fill(DrawingContext dc, Rect r)
    {
        if (r.Width > 0 && r.Height > 0) dc.DrawRectangle(_dim, null, r);
    }

    private static Pen CreateAntPen()
    {
        var p = new Pen(new SolidColorBrush(Color.FromArgb(230, 0, 0, 0)), 1)
        {
            DashStyle = new DashStyle([5, 5], 0),
            DashCap = PenLineCap.Flat,
        };
        p.Freeze();
        return p;
    }

    private static Brush Frozen(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }
}

/// <summary>
/// Grilla de la lupa (ShareX: MagnifierPixelGrid): líneas negras alfa 75 entre píxeles, fila y columna central
/// en el color de acento con opacidad 75/255, y el píxel central con contorno negro y otro blanco adentro.
/// Se dibuja en píxeles físicos para que las líneas queden nítidas a cualquier escala.
/// </summary>
internal sealed class MagnifierGrid : FrameworkElement
{
    private static readonly Brush GridBrush = Freeze(new SolidColorBrush(Color.FromArgb(75, 0, 0, 0)));
    private Brush _accent = Brushes.DodgerBlue;

    public int PixelCount { get; set; } = 15;
    public double Scale { get; set; } = 1;

    public MagnifierGrid()
    {
        IsHitTestVisible = false;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    public void SetAccent(Color c) => _accent = Freeze(new SolidColorBrush(c));

    protected override void OnRender(DrawingContext dc)
    {
        var count = Math.Max(1, PixelCount);
        var s = Scale > 0 ? Scale : 1;
        var w = ActualWidth * s;
        var h = ActualHeight * s;
        int Snap(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);
        var right = Snap(w);
        var bottom = Snap(h);
        var c = count / 2;
        var cl = Snap(c * w / count);
        var ct = Snap(c * h / count);
        var cr = Snap((c + 1) * w / count);
        var cb = Snap((c + 1) * h / count);

        dc.PushOpacity(75 / 255.0);
        Px(dc, _accent, 0, ct, cl, cb - 1, s);
        Px(dc, _accent, cr, ct, right, cb - 1, s);
        Px(dc, _accent, cl, 0, cr - 1, ct, s);
        Px(dc, _accent, cl, cb, cr - 1, bottom, s);
        dc.Pop();

        for (var i = 1; i < count; i++)
        {
            var x = Snap(i * w / count);
            var y = Snap(i * h / count);
            Px(dc, GridBrush, x - 1, 0, x, bottom, s);
            Px(dc, GridBrush, 0, y - 1, right, y, s);
        }

        cl--;
        ct--;
        Outline(dc, Brushes.Black, cl, ct, cr, cb, s);
        Outline(dc, Brushes.White, cl + 1, ct + 1, cr - 1, cb - 1, s);
    }

    private static void Outline(DrawingContext dc, Brush b, int l, int t, int r, int bt, double s)
    {
        if (r <= l || bt <= t) return;
        Px(dc, b, l, t, r, t + 1, s);
        Px(dc, b, l, bt - 1, r, bt, s);
        Px(dc, b, l, t + 1, l + 1, bt - 1, s);
        Px(dc, b, r - 1, t + 1, r, bt - 1, s);
    }

    private static void Px(DrawingContext dc, Brush b, int l, int t, int r, int bt, double s)
    {
        if (r <= l || bt <= t) return;
        dc.DrawRectangle(b, null, new Rect(l / s, t / s, (r - l) / s, (bt - t) / s));
    }

    private static Brush Freeze(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }
}
