using System.Runtime.InteropServices;
using Pantallazo.Core;
using Pantallazo.Native;

namespace Pantallazo.Capture;

/// <summary>Rectángulo detectable al pasar el mouse en el selector de región.</summary>
public readonly record struct WindowRect(nint Handle, PixelRect Rect, bool IsWindow);

/// <summary>
/// Lista de ventanas y controles visibles en orden Z (la de arriba primero), con el mismo criterio que ShareX
/// (WindowsRectangleList): los controles hijos van antes que su ventana, el área cliente cuenta como rectángulo
/// aparte, y un control tapado por una ventana de más arriba se descarta. Se omiten las ventanas ocultas por DWM
/// (cloaked) y las de herramientas no activables (overlays). El primero que contiene el punto es el elegido.
/// </summary>
public static class WindowFinder
{
    private static readonly string[] IgnoredClasses = ["CEF-OSC-WIDGET"]; // overlay de NVIDIA

    public static IReadOnlyList<WindowRect> Snapshot(IReadOnlyCollection<nint> ignore, bool includeControls, TimeSpan timeout)
    {
        var found = new List<WindowRect>();
        var parents = new HashSet<nint>();
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        bool Check(nint handle, PixelRect? clip)
        {
            if (Environment.TickCount64 > deadline) return false;
            if (ignore.Contains(handle) || !NativeMethods.IsWindowVisible(handle)) return true;
            var isWindow = clip is null;
            PixelRect rect;
            if (isWindow)
            {
                if (IsCloaked(handle)) return true;
                var cls = NativeMethods.GetClassName(handle);
                if (cls is not null && IgnoredClasses.Contains(cls, StringComparer.OrdinalIgnoreCase)) return true;
                var ex = (long)NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE);
                if ((ex & NativeMethods.WS_EX_TOOLWINDOW) != 0 && (ex & NativeMethods.WS_EX_NOACTIVATE) != 0) return true;
                rect = WindowBounds(handle);
            }
            else
            {
                if (!NativeMethods.GetWindowRect(handle, out var r)) return true;
                rect = Screens.ToRect(r).Intersect(clip!.Value);
            }
            if (rect.IsEmpty) return true;

            if (includeControls && parents.Add(handle))
                NativeMethods.EnumChildWindows(handle, (h, _) => Check(h, rect), 0);

            if (isWindow && NativeMethods.GetClientRect(handle, out var cr))
            {
                var p = new NativeMethods.POINT();
                NativeMethods.ClientToScreen(handle, ref p);
                var client = new PixelRect(p.X, p.Y, cr.Width, cr.Height);
                if (!client.IsEmpty && client != rect) found.Add(new WindowRect(handle, client, false));
            }
            found.Add(new WindowRect(handle, rect, isWindow));
            return true;
        }

        try { NativeMethods.EnumWindows((h, _) => Check(h, null), 0); }
        catch { /* una ventana que se cierra a mitad de camino no debe romper el selector */ }

        var result = new List<WindowRect>(found.Count);
        foreach (var w in found)
        {
            if (!w.IsWindow && result.Any(r => Contains(r.Rect, w.Rect))) continue;
            result.Add(w);
        }
        return result;
    }

    public static WindowRect? Hit(IReadOnlyList<WindowRect> list, int x, int y)
    {
        foreach (var w in list)
            if (w.Rect.Contains(x, y)) return w;
        return null;
    }

    /// <summary>Marco visible (sin la sombra invisible de Windows 10/11).</summary>
    public static PixelRect WindowBounds(nint handle)
    {
        if (NativeMethods.DwmGetWindowAttribute(handle, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out NativeMethods.RECT r, Marshal.SizeOf<NativeMethods.RECT>()) == 0 && r.Width > 0)
            return Screens.ToRect(r);
        return NativeMethods.GetWindowRect(handle, out r) ? Screens.ToRect(r) : default;
    }

    private static bool IsCloaked(nint handle) =>
        NativeMethods.DwmGetWindowAttribute(handle, NativeMethods.DWMWA_CLOAKED, out int c, sizeof(int)) == 0 && c != 0;

    private static bool Contains(PixelRect outer, PixelRect inner) =>
        inner.X >= outer.X && inner.Y >= outer.Y && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;
}
