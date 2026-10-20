using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RecHarness;

/// <summary>
/// Ventanita que cambia en cada repintado (contador + rectángulo que se mueve), para que WGC entregue cuadros
/// (sin cambios en pantalla no llegan cuadros). Corre en su propio hilo STA.
/// </summary>
internal sealed class AnimationWindow : Form
{
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _paints;
    private volatile bool _running = true;
    private readonly Font _font = new("Consolas", 28, FontStyle.Bold);

    public long Paints => Interlocked.Read(ref _paints);

    /// <summary>Mientras suena el tono (Stopwatch.GetTimestamp de inicio y fin), la ventana se pinta toda blanca.</summary>
    public static long FlashFrom, FlashUntil;

    private AnimationWindow(Rectangle bounds)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        BackColor = Color.Black;
        Text = "RecHarness";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Interlocked.Increment(ref _paints);
        var g = e.Graphics;
        long now = Stopwatch.GetTimestamp();
        if (Interlocked.Read(ref FlashFrom) != 0 && now >= Interlocked.Read(ref FlashFrom) && now < Interlocked.Read(ref FlashUntil))
        {
            g.Clear(Color.White);
            return;
        }
        double t = _clock.Elapsed.TotalSeconds;
        int w = ClientSize.Width, h = ClientSize.Height;
        int hue = (int)(t * 120) % 360;
        g.Clear(FromHue(hue));
        int box = Math.Min(w, h) / 4;
        int x = (int)((Math.Sin(t * 3) * 0.5 + 0.5) * (w - box));
        int y = (int)((Math.Cos(t * 2.3) * 0.5 + 0.5) * (h - box));
        g.FillRectangle(Brushes.White, x, y, box, box);
        g.DrawString($"{Paints}  {t:0.000}s", _font, Brushes.Black, 10, 10);
    }

    private static Color FromHue(int hue)
    {
        double hh = hue / 60.0;
        int i = (int)hh;
        double f = hh - i;
        int q = (int)(255 * (1 - f)), tt = (int)(255 * f);
        return i switch
        {
            0 => Color.FromArgb(255, tt, 60),
            1 => Color.FromArgb(q, 255, 60),
            2 => Color.FromArgb(60, 255, tt),
            3 => Color.FromArgb(60, q, 255),
            4 => Color.FromArgb(tt, 60, 255),
            _ => Color.FromArgb(255, 60, q),
        };
    }

    /// <summary>Abre la ventana en <paramref name="bounds"/> (píxeles físicos) y la anima hasta Close().</summary>
    public static AnimationWindow Show(Rectangle bounds)
    {
        AnimationWindow? win = null;
        var ready = new ManualResetEventSlim();
        var ui = new Thread(() =>
        {
            win = new AnimationWindow(bounds);
            win.Shown += (_, _) => ready.Set();
            Application.Run(win);
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.IsBackground = true;
        ui.Start();
        ready.Wait();
        var w = win!;
        var pump = new Thread(() =>
        {
            timeBeginPeriod(1);
            try
            {
                while (w._running)
                {
                    try { w.BeginInvoke(() => { w.Invalidate(); }); } catch { break; }
                    Thread.Sleep(2);
                }
            }
            finally { timeEndPeriod(1); }
        }) { IsBackground = true };
        pump.Start();
        return w;
    }

    public void CloseFromAnyThread()
    {
        _running = false;
        try { BeginInvoke(Close); } catch { }
    }
}
