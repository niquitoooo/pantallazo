using System.Runtime.InteropServices;
using Pantallazo.Recording.Native;
using static Pantallazo.Recording.Native.User32;

namespace Pantallazo.Recording;

/// <summary>Monitores conectados, en píxeles físicos del escritorio virtual (independiente del DPI del proceso).</summary>
public static class DisplayCatalog
{
    public static IReadOnlyList<DisplayInfo> GetDisplays()
    {
        var raw = new List<(nint Handle, string Device, bool Primary)>();
        MonitorEnumProc proc = (h, _, _, _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(h, ref mi))
                raw.Add((h, mi.szDevice, (mi.dwFlags & MONITORINFOF_PRIMARY) != 0));
            return true;
        };
        EnumDisplayMonitors(0, 0, proc, 0);
        GC.KeepAlive(proc);

        var names = QueryFriendlyNames();
        var list = new List<(nint Handle, string Device, bool Primary, int X, int Y, int W, int H, int Hz)>();
        foreach (var (handle, device, primary) in raw)
        {
            var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref dm)) continue;
            int hz = (int)dm.dmDisplayFrequency;
            if (names.TryGetValue(device, out var n) && n.Hz > 0) hz = n.Hz;
            list.Add((handle, device, primary, dm.dmPositionX, dm.dmPositionY, (int)dm.dmPelsWidth, (int)dm.dmPelsHeight, hz));
        }

        // Orden visual: de izquierda a derecha, de arriba a abajo.
        list.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        var result = new List<DisplayInfo>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            var d = list[i];
            string friendly = names.TryGetValue(d.Device, out var n) && !string.IsNullOrWhiteSpace(n.Name)
                ? n.Name
                : $"Pantalla {i + 1}";
            result.Add(new DisplayInfo(d.Device, friendly, d.X, d.Y, d.W, d.H, d.Hz, d.Primary, d.Handle));
        }
        return result;
    }

    /// <summary>Nombre amigable y frecuencia exacta por nombre GDI (\\.\DISPLAYn), vía QueryDisplayConfig.</summary>
    private static Dictionary<string, (string Name, int Hz)> QueryFriendlyNames()
    {
        var map = new Dictionary<string, (string, int)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0) return map;
            var paths = new DISPLAYCONFIG_PATH_INFO[np];
            var modes = new DISPLAYCONFIG_MODE_INFO[nm];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, 0) != 0) return map;
            for (int i = 0; i < np; i++)
            {
                var p = paths[i];
                var src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = p.sourceInfo.adapterId,
                        id = p.sourceInfo.id,
                    },
                };
                if (DisplayConfigGetDeviceInfo(ref src) != 0) continue;
                var tgt = new DISPLAYCONFIG_TARGET_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                        adapterId = p.targetInfo.adapterId,
                        id = p.targetInfo.id,
                    },
                };
                string name = "";
                if (DisplayConfigGetDeviceInfo(ref tgt) == 0)
                {
                    name = tgt.monitorFriendlyDeviceName ?? "";
                    if (string.IsNullOrWhiteSpace(name) && tgt.outputTechnology == DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL)
                        name = "Pantalla integrada";
                }
                var rr = p.targetInfo.refreshRate;
                int hz = rr.Denominator != 0 ? (int)Math.Round((double)rr.Numerator / rr.Denominator) : 0;
                if (!map.ContainsKey(src.viewGdiDeviceName)) map[src.viewGdiDeviceName] = (name, hz);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            // Sin DisplayConfig: quedan los nombres genéricos.
        }
        return map;
    }
}
