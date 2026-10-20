using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Pantallazo.Logging;

namespace Pantallazo.Services;

/// <summary>
/// Ícono de la bandeja con Shell_NotifyIcon. Como ShareX: clic = seleccionar región (o detener si está grabando),
/// doble clic = abrir la ventana, clic derecho = menú.
/// </summary>
public sealed partial class TrayIcon : IDisposable
{
    private const int WM_APP_TRAY = 0x8000 + 1;
    private const int WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205;
    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const int NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_SHOWTIP = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public nint hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATA data);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegisterWindowMessage(string name);

    [LibraryImport("user32.dll")]
    private static partial uint GetDoubleClickTime();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint h);

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int fIcon, xHotspot, yHotspot;
        public nint hbmMask, hbmColor;
    }

    [DllImport("user32.dll")]
    private static extern nint CreateIconIndirect(ref ICONINFO info);

    [DllImport("gdi32.dll")]
    private static extern nint CreateBitmap(int w, int h, uint planes, uint bpp, byte[]? bits);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint h);

    private readonly HwndSource _host;
    private readonly int _taskbarCreated;
    private readonly DispatcherTimer _clickTimer;
    private readonly Func<ContextMenu> _menuFactory;
    private nint _iconNormal, _iconRecording;
    private bool _recording;
    private string _tip = "Pantallazo";
    private bool _added;

    public event EventHandler? Clicked;
    public event EventHandler? DoubleClicked;

    public TrayIcon(Func<ContextMenu> menuFactory)
    {
        _menuFactory = menuFactory;
        _host = new HwndSource(new HwndSourceParameters("Pantallazo - bandeja") { WindowStyle = 0, Width = 0, Height = 0 });
        _host.AddHook(WndProc);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _clickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime()) };
        _clickTimer.Tick += (_, _) => { _clickTimer.Stop(); Clicked?.Invoke(this, EventArgs.Empty); };
        BuildIcons();
        Add();
    }

    public void SetRecording(bool recording, string tip)
    {
        _recording = recording;
        _tip = tip;
        Update(NIM_MODIFY);
    }

    public void SetTip(string tip)
    {
        _tip = tip;
        Update(NIM_MODIFY);
    }

    private void Add()
    {
        _added = Update(NIM_ADD);
        if (!_added) Log.Warn("No se pudo agregar el ícono de la bandeja.");
    }

    private bool Update(int msg)
    {
        var d = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _host.Handle,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP,
            uCallbackMessage = WM_APP_TRAY,
            hIcon = _recording ? _iconRecording : _iconNormal,
            szTip = _tip.Length > 120 ? _tip[..120] : _tip,
            szInfo = "",
            szInfoTitle = "",
        };
        return Shell_NotifyIcon(msg, ref d);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == _taskbarCreated)
        {
            Add(); // el Explorador se reinició
            return 0;
        }
        if (msg != WM_APP_TRAY) return 0;
        switch ((int)lParam & 0xFFFF)
        {
            case WM_LBUTTONUP:
                _clickTimer.Stop();
                _clickTimer.Start();
                break;
            case WM_LBUTTONDBLCLK:
                _clickTimer.Stop();
                DoubleClicked?.Invoke(this, EventArgs.Empty);
                break;
            case WM_RBUTTONUP:
                ShowMenu();
                break;
        }
        handled = true;
        return 0;
    }

    private Window? _menuHost;

    private void ShowMenu()
    {
        // Un menú de WPF necesita una ventana activa para cerrarse al hacer clic afuera.
        _menuHost ??= new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Width = 1,
            Height = 1,
            Left = -100,
            Top = -100,
            Topmost = true,
        };
        _menuHost.Show();
        _menuHost.Activate();
        var menu = _menuFactory();
        menu.Placement = PlacementMode.MousePoint;
        menu.Closed += (_, _) => _menuHost?.Hide();
        menu.IsOpen = true;
    }

    private void BuildIcons()
    {
        var size = Math.Max(16, Native.NativeMethods.GetSystemMetrics(49) is var s and > 0 ? s : 16); // SM_CXSMICON
        _iconNormal = RenderIcon(size, recording: false);
        _iconRecording = RenderIcon(size, recording: true);
    }

    /// <summary>Dibuja el ícono de la app (<see cref="AppIconArt"/>, en rojo si graba) y lo convierte en HICON.</summary>
    private static nint RenderIcon(int size, bool recording)
    {
        var rtb = AppIconArt.Render(size, recording);
        var pixels = new byte[size * size * 4];
        rtb.CopyPixels(pixels, size * 4, 0);
        // Pbgra32 → BGRA sin premultiplicar (lo que espera un ícono de 32 bits).
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var a = pixels[i + 3];
            if (a is 0 or 255) continue;
            pixels[i] = (byte)Math.Min(255, pixels[i] * 255 / a);
            pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / a);
            pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / a);
        }
        var color = CreateBitmap(size, size, 1, 32, pixels);
        var mask = CreateBitmap(size, size, 1, 1, new byte[((size + 15) / 16) * 2 * size]);
        var info = new ICONINFO { fIcon = 1, hbmColor = color, hbmMask = mask };
        var icon = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);
        return icon;
    }

    public void Dispose()
    {
        if (_added) Update(NIM_DELETE);
        _menuHost?.Close();
        _host.Dispose();
        if (_iconNormal != 0) DestroyIcon(_iconNormal);
        if (_iconRecording != 0) DestroyIcon(_iconRecording);
    }
}
