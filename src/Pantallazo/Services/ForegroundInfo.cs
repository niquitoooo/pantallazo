using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Pantallazo.Services;

/// <summary>
/// Qué programa y qué ventana estaban al frente, para las etiquetas {app} y {ventana} de los nombres de archivo.
/// Se saltean las ventanas de Pantallazo (por ejemplo al grabar desde su propia ventana) y el escritorio da
/// "Escritorio".
/// </summary>
public static class ForegroundInfo
{
    public sealed record Info(string App, string Window);

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern long GetWindowLongPtr(nint hwnd, int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attr, out int value, int size);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(nint process, int flags, StringBuilder name, ref int size);

    private static readonly uint OwnPid = (uint)Environment.ProcessId;

    public static Info Get()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            // Si al frente está Pantallazo (o nada útil), la siguiente ventana visible en el orden de Windows.
            for (var i = 0; hwnd != 0 && i < 200 && !IsCandidate(hwnd); i++) hwnd = GetWindow(hwnd, 2 /* GW_HWNDNEXT */);
            if (hwnd == 0) return new Info("Escritorio", "");
            var cls = ClassOf(hwnd);
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return new Info("Escritorio", "");
            var title = TitleOf(hwnd);
            GetWindowThreadProcessId(hwnd, out var pid);
            var app = AppName(pid);
            // Las apps de la Tienda viven dentro de ApplicationFrameHost: el nombre útil es el título.
            if (app is null || app.Equals("Application Frame Host", StringComparison.OrdinalIgnoreCase) ||
                app.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
                app = string.IsNullOrWhiteSpace(title) ? "App" : title;
            return new Info(app, title);
        }
        catch
        {
            return new Info("App", "");
        }
    }

    private static bool IsCandidate(nint hwnd)
    {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == OwnPid) return false;
        const long WS_EX_TOOLWINDOW = 0x80;
        if ((GetWindowLongPtr(hwnd, -20) & WS_EX_TOOLWINDOW) != 0) return false;
        if (DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out var cloaked, 4) == 0 && cloaked != 0) return false;
        var cls = ClassOf(hwnd);
        return cls is "Progman" or "WorkerW" || TitleOf(hwnd).Length > 0;
    }

    private static string ClassOf(nint hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string TitleOf(nint hwnd)
    {
        var sb = new StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString().Trim();
    }

    /// <summary>Descripción del ejecutable ("Google Chrome") o, si no tiene, su nombre ("chrome").</summary>
    private static string? AppName(uint pid)
    {
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            if (!QueryFullProcessImageName(h, 0, sb, ref size)) return null;
            var path = sb.ToString();
            string? description = null;
            try { description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim(); } catch { }
            return string.IsNullOrWhiteSpace(description) ? Path.GetFileNameWithoutExtension(path) : description;
        }
        finally { CloseHandle(h); }
    }
}
