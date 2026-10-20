using Pantallazo.Core;
using Pantallazo.Native;

namespace Pantallazo.Capture;

public sealed record ScreenInfo(string DeviceName, PixelRect Bounds, PixelRect WorkArea, bool IsPrimary);

/// <summary>Monitores en píxeles físicos (la app es PerMonitorV2, así que Windows no virtualiza las coordenadas).</summary>
public static class Screens
{
    public static IReadOnlyList<ScreenInfo> All()
    {
        var list = new List<ScreenInfo>();
        NativeMethods.EnumDisplayMonitors(0, 0, (nint h, nint _, ref NativeMethods.RECT _, nint _) =>
        {
            var info = new NativeMethods.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
            if (NativeMethods.GetMonitorInfo(h, ref info))
            {
                list.Add(new ScreenInfo(info.szDevice,
                    ToRect(info.rcMonitor), ToRect(info.rcWork), (info.dwFlags & 1) != 0));
            }
            return true;
        }, 0);
        return list;
    }

    /// <summary>Unión de todos los monitores (escritorio virtual).</summary>
    public static PixelRect VirtualScreen()
    {
        var r = new PixelRect(
            NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
            NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
            NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
            NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));
        if (!r.IsEmpty) return r;
        return All().Aggregate(default(PixelRect), (acc, s) => acc.Union(s.Bounds));
    }

    public static ScreenInfo? FromPoint(int x, int y, IReadOnlyList<ScreenInfo>? screens = null) =>
        (screens ?? All()).FirstOrDefault(s => s.Bounds.Contains(x, y)) ?? (screens ?? All()).FirstOrDefault(s => s.IsPrimary);

    internal static PixelRect ToRect(NativeMethods.RECT r) => new(r.Left, r.Top, r.Width, r.Height);
}
